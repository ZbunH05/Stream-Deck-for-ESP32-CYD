// CYD Deck đóng vai app Stream Deck cho plugin của Elgato (giao thức WebSocket JSON của Stream Deck SDK): bo CYD hiện ra
// với plugin như một Stream Deck Neo (4×2 phím 72 px). Hỗ trợ plugin .exe (C++, C#), Node.js và HTML/JS.
//
//   plugin → host: registerPlugin, setSettings, getSettings, setGlobalSettings, getGlobalSettings, setTitle, setImage,
//                  setState, showOk, showAlert, openUrl, logMessage, sendToPropertyInspector
//   host → plugin: deviceDidConnect, willAppear, willDisappear, keyDown, keyUp, didReceiveSettings,
//                  didReceiveGlobalSettings, propertyInspectorDidAppear/Disappear, sendToPlugin, titleParametersDidChange
//   Property Inspector (trang thuộc tính của hành động) chạy trong khung bên phải của trang cài đặt, cùng nguồn
//   http://127.0.0.1:47831; plugin HTML chạy trong một Edge ẩn (headless) nạp trang web\plugin-host.html.
//   Không hỗ trợ: núm xoay và màn cảm ứng của Stream Deck + (setFeedback...), switchToProfile, multi-action.
//
// Chỉ chạy plugin đang được dùng (có phím trong bộ phím, hay Property Inspector đang mở): đỡ tốn tài nguyên.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Web.Script.Serialization;

namespace CydAgent {

sealed class ElgatoHost : IDeckPlugins {
  public const string DeviceId = "CYDDECK00000000000000000000000001";
  const string WebOrigin = "http://127.0.0.1:47831";

  sealed class Runtime {
    public PluginInfo Info;
    public string Token;          // -pluginUUID truyền cho plugin, plugin đăng ký lại bằng nó
    public Process Proc;
    public WsConnection Conn;
    public List<string> Pending = new List<string>();  // tin nhắn gửi trước khi plugin đăng ký xong

    public DateTime StartedAt;
    public bool Stopping;
    public bool Loaded;  // plugin HTML: đã gửi loadPlugin cho Edge ẩn đang chạy
  }

  sealed class Inspector {
    public string Context, Plugin;
  }

  // Phím chỉ có trong trang cài đặt (chưa lưu) mà Property Inspector đang mở
  sealed class Draft {
    public string Context, ActionUuid, Settings;
    public int Slot;
  }

  readonly DeckSync deck;
  readonly WsServer server;
  readonly object gate = new object();
  readonly JavaScriptSerializer json = DeckConfig.Json();
  Dictionary<string, PluginInfo> plugins = new Dictionary<string, PluginInfo>();
  readonly Dictionary<string, Runtime> runtimes = new Dictionary<string, Runtime>();
  readonly Dictionary<string, WsConnection> inspectors = new Dictionary<string, WsConnection>();
  readonly Dictionary<string, Draft> drafts = new Dictionary<string, Draft>();
  readonly Dictionary<string, string> settingsOverride = new Dictionary<string, string>();  // context → settings mới nhất
  readonly Dictionary<string, string> titles = new Dictionary<string, string>();            // context → setTitle
  readonly Dictionary<string, KeyRef> shown = new Dictionary<string, KeyRef>();             // context đã willAppear → phím lúc đó
  readonly string hostToken = Guid.NewGuid().ToString("N");
  WsConnection host;
  Process edge;
  int renderId;
  readonly ConcurrentDictionary<int, string> rendered = new ConcurrentDictionary<int, string>();
  readonly ManualResetEvent renderSignal = new ManualResetEvent(false);
  public Func<string> HostPageUrl;  // DeckWeb gắn: địa chỉ web\plugin-host.html (có mã)

  static string GlobalSettingsPath { get { return Path.Combine(DeckConfig.Dir, "plugin-settings.json"); } }
  Dictionary<string, string> globalSettings = new Dictionary<string, string>();

  public ElgatoHost(DeckSync deck) {
    this.deck = deck;
    server = new WsServer(origin => origin.Length == 0 || origin == WebOrigin || origin == "http://localhost:47831");
    server.OnMessage = OnMessage;
    server.OnClose = OnClose;
    try {
      if (File.Exists(GlobalSettingsPath)) globalSettings = json.Deserialize<Dictionary<string, string>>(File.ReadAllText(GlobalSettingsPath, Encoding.UTF8));
    } catch (Exception) {
    }
    Reload();
    DeckConfig.PluginArt = StateArt;
    // bộ phím dựng lúc khởi động chưa có ảnh của plugin: dựng lại
    if (deck.Layout.Config.Pages.Any(p => p.Keys.Any(k => k.Kind == "plugin"))) {
      ThreadPool.QueueUserWorkItem(_ => deck.Apply(deck.Layout.Config));
    } else {
      Ensure();
    }
  }

  public int Port { get { return server.Port; } }
  public string Token { get { return hostToken; } }

  public void Reload() {
    var list = ElgatoPlugins.List();
    lock (gate) plugins = list.ToDictionary(p => p.Uuid, p => p, StringComparer.OrdinalIgnoreCase);
  }

  public List<PluginInfo> Plugins { get { lock (gate) return plugins.Values.OrderBy(p => p.Name).ToList(); } }

  PluginInfo PluginOfAction(string actionUuid, out PluginAction action) {
    action = null;
    lock (gate) {
      foreach (var p in plugins.Values) {
        var a = p.Actions.FirstOrDefault(x => string.Equals(x.Uuid, actionUuid, StringComparison.OrdinalIgnoreCase));
        if (a != null) {
          action = a;
          return p;
        }
      }
    }
    return null;
  }

  // ---------- phím của bộ phím ↔ context ----------

  sealed class KeyRef {
    public int BoardPage, Slot;
    public DeckKeyConfig Key;
  }

  KeyRef Find(string context) {
    var layout = deck.Layout;
    for (int b = 0; b < layout.ConfigOf.Length; b++) {
      for (int s = 0; s < DeckConfig.KEYS; s++) {
        var k = layout.KeyAt(b, s);
        if (k != null && k.Kind == "plugin" && k.Context == context) return new KeyRef { BoardPage = b, Slot = s, Key = k };
      }
    }
    return null;
  }

  IEnumerable<KeyRef> KeysOn(int boardPage) {
    var layout = deck.Layout;
    if (boardPage < 0 || boardPage >= layout.ConfigOf.Length) yield break;
    for (int s = 0; s < DeckConfig.KEYS; s++) {
      var k = layout.KeyAt(boardPage, s);
      if (k != null && k.Kind == "plugin") yield return new KeyRef { BoardPage = boardPage, Slot = s, Key = k };
    }
  }

  string SettingsOf(string context, string fallback) {
    lock (gate) {
      string s;
      if (settingsOverride.TryGetValue(context, out s)) return s;
    }
    return string.IsNullOrEmpty(fallback) ? "{}" : fallback;
  }

  // Cài đặt mới nhất của phím plugin (trang cài đặt dùng khi lưu, để không đè cài đặt plugin vừa đặt)
  public string LatestSettings(string context, string current) { return SettingsOf(context, current); }

  // ---------- chạy plugin ----------

  // Chạy những plugin đang được dùng, tắt những plugin không còn dùng
  void Ensure() {
    var needed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    foreach (var page in deck.Layout.Config.Pages) {
      foreach (var k in page.Keys) {
        if (k.Kind != "plugin") continue;
        PluginAction a;
        var p = PluginOfAction(k.ActionUuid, out a);
        if (p != null) needed.Add(p.Uuid);
      }
    }
    lock (gate) {
      foreach (var d in drafts.Values) {
        PluginAction a;
        var p = PluginOfAction(d.ActionUuid, out a);
        if (p != null) needed.Add(p.Uuid);
      }
    }
    List<Runtime> stop;
    List<PluginInfo> start;
    lock (gate) {
      stop = runtimes.Values.Where(r => !needed.Contains(r.Info.Uuid) || !r.Info.Enabled).ToList();
      start = needed.Where(u => plugins.ContainsKey(u) && !runtimes.ContainsKey(u)).Select(u => plugins[u])
                    .Where(p => p.Enabled && p.Error == null).ToList();
    }
    foreach (var r in stop) Stop(r);
    foreach (var p in start) Start(p);
  }

  string InfoJson(PluginInfo p) {
    var info = new Dictionary<string, object> {
      { "application", new Dictionary<string, object> {
          { "font", "Segoe UI" }, { "language", I18n.Lang }, { "platform", "windows" },
          { "platformVersion", Environment.OSVersion.Version.ToString() }, { "version", "6.9.1.21000" } } },
      { "plugin", new Dictionary<string, object> { { "uuid", p.Uuid }, { "version", p.Version } } },
      { "devicePixelRatio", 1 },
      { "colors", new Dictionary<string, object> {
          { "buttonPressedBackgroundColor", "#303030FF" }, { "buttonPressedBorderColor", "#646464FF" },
          { "buttonPressedTextColor", "#969696FF" }, { "disabledColor", "#F7821B59" }, { "highlightColor", "#F7821BFF" },
          { "mouseDownColor", "#CF6304FF" } } },
      { "devices", new object[] { new Dictionary<string, object> {
          { "id", DeviceId }, { "name", "CYD Deck" }, { "type", 9 },
          { "size", new Dictionary<string, object> { { "columns", 4 }, { "rows", 2 } } } } } },
    };
    return json.Serialize(info);
  }

  void Start(PluginInfo p) {
    var r = new Runtime { Info = p, Token = Guid.NewGuid().ToString("N").ToUpperInvariant(), StartedAt = DateTime.UtcNow };
    lock (gate) runtimes[p.Uuid] = r;
    try {
      if (p.Runtime == "html") {
        EnsureHost();  // Edge vừa mở thì Register (registerHost) đã nạp plugin này rồi
        lock (gate) {
          if (r.Loaded) return;
          r.Loaded = true;
        }
        SendHost(new Dictionary<string, object> {
          { "event", "loadPlugin" }, { "uuid", p.Uuid }, { "token", r.Token }, { "port", Port }, { "info", InfoJson(p) },
          { "url", "/plugins/" + Uri.EscapeDataString(p.Uuid) + "/" + p.CodePath.Replace('\\', '/') },
        });
        PluginLog(p, "chạy trong Edge ẩn");
        return;
      }
      string code = ElgatoPlugins.SafePath(p, p.CodePath);
      if (code == null || !File.Exists(code)) throw new FileNotFoundException(p.CodePath);
      string args = "-port " + Port + " -pluginUUID " + r.Token + " -registerEvent registerPlugin -info " + ElgatoPlugins.Quote(InfoJson(p));
      ProcessStartInfo psi;
      if (p.Runtime == "node") {
        string node = ElgatoPlugins.NodePath();
        if (node == null) throw new InvalidOperationException(I18n.T("plugin.no_node"));
        psi = new ProcessStartInfo(node, ElgatoPlugins.Quote(code) + " " + args);
      } else {
        psi = new ProcessStartInfo(code, args);
      }
      psi.WorkingDirectory = Path.GetDirectoryName(code);
      psi.UseShellExecute = false;
      psi.CreateNoWindow = true;
      psi.RedirectStandardOutput = true;
      psi.RedirectStandardError = true;
      var proc = new Process { StartInfo = psi, EnableRaisingEvents = true };
      proc.OutputDataReceived += (s, e) => { if (e.Data != null) PluginLog(p, e.Data); };
      proc.ErrorDataReceived += (s, e) => { if (e.Data != null) PluginLog(p, "! " + e.Data); };
      proc.Exited += (s, e) => Exited(r);
      proc.Start();
      Job.Add(proc);
      proc.BeginOutputReadLine();
      proc.BeginErrorReadLine();
      r.Proc = proc;
      PluginLog(p, "chạy " + p.Runtime + " " + Path.GetFileName(code) + " (pid " + proc.Id + ")");
    } catch (Exception ex) {
      p.Error = ex.Message;
      PluginLog(p, "không chạy được: " + ex.Message);
      lock (gate) runtimes.Remove(p.Uuid);
    }
  }

  void Exited(Runtime r) {
    if (r.Stopping) return;
    PluginLog(r.Info, "tiến trình đã thoát");
    lock (gate) {
      if (runtimes.ContainsKey(r.Info.Uuid) && runtimes[r.Info.Uuid] == r) runtimes.Remove(r.Info.Uuid);
      foreach (var c in shown.Where(kv => PluginOfActionUuid(kv.Value.Key.ActionUuid) == r.Info.Uuid).Select(kv => kv.Key).ToList()) {
        shown.Remove(c);
      }
    }
    // chết liên tục (5 lần trong 10 phút): thôi, báo lỗi trong trang cài đặt, xem log
    List<DateTime> times;
    int count;
    lock (gate) {
      if (!crashes.TryGetValue(r.Info.Uuid, out times)) crashes[r.Info.Uuid] = times = new List<DateTime>();
      times.RemoveAll(t => (DateTime.UtcNow - t).TotalMinutes > 10);
      times.Add(DateTime.UtcNow);
      count = times.Count;
    }
    if (count >= 5) {
      r.Info.Error = I18n.T("plugin.crashed");
      PluginLog(r.Info, "chết 5 lần trong 10 phút, không chạy lại nữa");
      return;
    }
    Thread.Sleep(2000);
    Ensure();
  }

  readonly Dictionary<string, List<DateTime>> crashes = new Dictionary<string, List<DateTime>>();

  string PluginOfActionUuid(string actionUuid) {
    PluginAction a;
    var p = PluginOfAction(actionUuid, out a);
    return p == null ? null : p.Uuid;
  }

  void Stop(Runtime r) {
    r.Stopping = true;
    lock (gate) runtimes.Remove(r.Info.Uuid);
    try {
      if (r.Conn != null) r.Conn.Close();
      if (r.Proc != null && !r.Proc.HasExited) r.Proc.Kill();
    } catch (Exception) {
    }
    if (r.Info.Runtime == "html") SendHost(new Dictionary<string, object> { { "event", "unloadPlugin" }, { "uuid", r.Info.Uuid } });
    PluginLog(r.Info, "đã tắt");
  }

  public void Shutdown() {
    List<Runtime> all;
    lock (gate) all = runtimes.Values.ToList();
    foreach (var r in all) Stop(r);
    try { if (edge != null && !edge.HasExited) edge.Kill(); } catch (Exception) { }
  }

  // ---------- Edge ẩn: plugin HTML và vẽ SVG ----------

  void EnsureHost() {
    lock (gate) {
      if (edge != null && !edge.HasExited) return;
      string exe = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), @"Microsoft\Edge\Application\msedge.exe");
      if (!File.Exists(exe) || HostPageUrl == null) return;
      string profile = Path.Combine(Path.GetTempPath(), "CydDeck-edge-host");
      string url = HostPageUrl() + "&port=" + Port + "&host=" + hostToken;
      var psi = new ProcessStartInfo(exe, "--headless=new --disable-gpu --no-first-run --no-default-browser-check " +
                                         "--disable-extensions --disable-background-timer-throttling --disable-renderer-backgrounding " +
                                         "--disable-backgrounding-occluded-windows --user-data-dir=" + ElgatoPlugins.Quote(profile) + " " +
                                         ElgatoPlugins.Quote(url)) {
        UseShellExecute = false, CreateNoWindow = true,
      };
      edge = Process.Start(psi);
      Job.Add(edge);
      Log.Agent("Mở Edge ẩn cho plugin HTML / ảnh SVG (pid " + edge.Id + ")");
    }
    var until = DateTime.UtcNow.AddSeconds(10);
    while (host == null && DateTime.UtcNow < until) Thread.Sleep(100);
  }

  void SendHost(Dictionary<string, object> message) {
    var h = host;
    if (h != null) h.Send(json.Serialize(message));
  }

  // SVG (data URL) → PNG qua canvas của Edge ẩn
  Bitmap RenderSvg(string dataUrl) {
    EnsureHost();
    if (host == null) return null;
    int id = Interlocked.Increment(ref renderId);
    SendHost(new Dictionary<string, object> { { "event", "render" }, { "id", id }, { "svg", dataUrl } });
    var until = DateTime.UtcNow.AddSeconds(3);
    string png;
    while (!rendered.TryRemove(id, out png)) {
      if (DateTime.UtcNow > until) return null;
      renderSignal.WaitOne(50);
    }
    return string.IsNullOrEmpty(png) ? null : DecodeDataUrl(png);
  }

  // ---------- ảnh ----------

  static Bitmap DecodeDataUrl(string url) {
    int comma = url.IndexOf(',');
    if (comma < 0) return null;
    string meta = url.Substring(0, comma);
    string data = url.Substring(comma + 1);
    byte[] bytes = meta.Contains(";base64") ? Convert.FromBase64String(data) : Encoding.UTF8.GetBytes(Uri.UnescapeDataString(data));
    using (var ms = new MemoryStream(bytes))
    using (var image = Image.FromStream(ms)) return new Bitmap(image);
  }

  // data:image/svg+xml;charset=utf8,<svg …> (chưa mã hoá, có thể chứa # hay <) → dạng base64 an toàn cho thẻ <img>
  static string SvgBase64(string dataUrl) {
    int comma = dataUrl.IndexOf(',');
    if (comma < 0 || dataUrl.Substring(0, comma).Contains(";base64")) return dataUrl;
    string text = dataUrl.Substring(comma + 1);
    try { text = Uri.UnescapeDataString(text); } catch (Exception) { }
    return "data:image/svg+xml;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(text));
  }

  // Ảnh plugin gửi (data URL PNG/JPEG/SVG, SVG trần, hay đường dẫn file trong thư mục plugin) → Bitmap
  Bitmap LoadImage(PluginInfo p, string image) {
    if (string.IsNullOrEmpty(image)) return null;
    try {
      if (image.StartsWith("data:image/svg", StringComparison.OrdinalIgnoreCase)) return RenderSvg(SvgBase64(image));
      if (image.StartsWith("data:", StringComparison.OrdinalIgnoreCase)) return DecodeDataUrl(image);
      if (image.TrimStart().StartsWith("<svg", StringComparison.OrdinalIgnoreCase)) {
        return RenderSvg("data:image/svg+xml;base64," + Convert.ToBase64String(Encoding.UTF8.GetBytes(image)));
      }
      string file = ElgatoPlugins.ImageFile(p, image);
      if (file == null) return null;
      if (file.EndsWith(".svg", StringComparison.OrdinalIgnoreCase)) {
        return RenderSvg("data:image/svg+xml;base64," + Convert.ToBase64String(File.ReadAllBytes(file)));
      }
      using (var ms = new MemoryStream(File.ReadAllBytes(file)))
      using (var picture = Image.FromStream(ms)) return new Bitmap(picture);
    } catch (Exception ex) {
      PluginLog(p, "ảnh lỗi: " + ex.Message);
      return null;
    }
  }

  static byte[] Compose(Bitmap picture, bool strip) {
    Graphics g;
    using (var bmp = KeyArt.Begin(Color.Black, out g)) {
      using (g) {
        if (picture != null) KeyArt.Cover(g, picture);
        if (strip) KeyArt.TitleStrip(g);
      }
      return KeyArt.ToRgb565Swapped(bmp);
    }
  }

  // Ảnh trạng thái từ manifest cho bộ phím (DeckConfig.PluginArt)
  byte[] StateArt(DeckKeyConfig key, int state, bool withTitle) {
    PluginAction action;
    var p = PluginOfAction(key.ActionUuid, out action);
    if (p == null || action == null || state >= action.States.Count) return null;
    var st = action.States[state];
    using (var picture = LoadImage(p, st.Image)) {
      if (picture == null && state > 0) return null;
      bool strip = st.ShowTitle && (withTitle || !string.IsNullOrEmpty(st.Title));
      return Compose(picture, strip);
    }
  }

  // ---------- sự kiện từ bo (DeckSync) ----------

  Dictionary<string, object> Coordinates(int slot) {
    return new Dictionary<string, object> { { "column", slot % 4 }, { "row", slot / 4 } };
  }

  Dictionary<string, object> KeyPayload(string context, DeckKeyConfig key, int slot) {
    return new Dictionary<string, object> {
      { "settings", ParseObject(SettingsOf(context, key.Settings)) }, { "coordinates", Coordinates(slot) },
      { "state", key.State }, { "isInMultiAction", false }, { "controller", "Keypad" },
    };
  }

  object ParseObject(string text) {
    try {
      return json.DeserializeObject(string.IsNullOrEmpty(text) ? "{}" : text) ?? new Dictionary<string, object>();
    } catch (Exception) {
      return new Dictionary<string, object>();
    }
  }

  Runtime RuntimeOf(string actionUuid) {
    PluginAction a;
    var p = PluginOfAction(actionUuid, out a);
    if (p == null) return null;
    lock (gate) {
      Runtime r;
      return runtimes.TryGetValue(p.Uuid, out r) ? r : null;
    }
  }

  void SendPlugin(Runtime r, Dictionary<string, object> message) {
    if (r == null) return;
    string text = json.Serialize(message);
    lock (gate) {
      if (r.Conn == null) {
        if (r.Pending.Count < 200) r.Pending.Add(text);
        return;
      }
    }
    r.Conn.Send(text);
  }

  void SendKeyEvent(string evt, KeyRef k) {
    if (evt == "keyDown" || evt == "keyUp") Count(PluginOfActionUuid(k.Key.ActionUuid), evt + " (bo)");
    SendPlugin(RuntimeOf(k.Key.ActionUuid), new Dictionary<string, object> {
      { "action", k.Key.ActionUuid }, { "event", evt }, { "context", k.Key.Context }, { "device", DeviceId },
      { "payload", KeyPayload(k.Key.Context, k.Key, k.Slot) },
    });
  }

  public void KeyDown(int boardPage, int slot) {
    var k = KeysOn(boardPage).FirstOrDefault(x => x.Slot == slot);
    if (k != null) SendKeyEvent("keyDown", k);
  }

  public void KeyUp(int boardPage, int slot) {
    var k = KeysOn(boardPage).FirstOrDefault(x => x.Slot == slot);
    if (k != null) SendKeyEvent("keyUp", k);
  }

  public void PageShown(int boardPage, int previous) {
    foreach (var k in KeysOn(previous).ToList()) Disappear(k);
    foreach (var k in KeysOn(boardPage).ToList()) Appear(k);
  }

  void Appear(KeyRef k) {
    lock (gate) {
      if (shown.ContainsKey(k.Key.Context)) return;
      shown[k.Key.Context] = k;
    }
    SendKeyEvent("willAppear", k);
    SendPlugin(RuntimeOf(k.Key.ActionUuid), new Dictionary<string, object> {
      { "action", k.Key.ActionUuid }, { "event", "titleParametersDidChange" }, { "context", k.Key.Context }, { "device", DeviceId },
      { "payload", new Dictionary<string, object> {
          { "coordinates", Coordinates(k.Slot) }, { "settings", ParseObject(SettingsOf(k.Key.Context, k.Key.Settings)) },
          { "state", k.Key.State }, { "title", k.Key.Title ?? "" },
          { "titleParameters", new Dictionary<string, object> {
              { "fontFamily", "" }, { "fontSize", 12 }, { "fontStyle", "" }, { "fontUnderline", false }, { "showTitle", true },
              { "titleAlignment", "bottom" }, { "titleColor", "#ffffff" } } } } },
    });
  }

  void Disappear(KeyRef k) {
    KeyRef old;
    lock (gate) {
      if (!shown.TryGetValue(k.Key.Context, out old)) return;
      shown.Remove(k.Key.Context);
    }
    SendKeyEvent("willDisappear", old);
    FlushActivity(PluginOfActionUuid(old.Key.ActionUuid));
  }

  // Plugin đã làm gì trong lúc phím của nó hiện trên bo: một dòng trong log của plugin khi phím rời màn hình
  // (xem bằng nút Log trong mục Plugin; giúp biết plugin có gửi nhãn, ảnh hay không)
  readonly Dictionary<string, Dictionary<string, int>> activity = new Dictionary<string, Dictionary<string, int>>();

  void Count(string uuid, string what) {
    if (uuid == null) return;
    lock (gate) {
      Dictionary<string, int> c;
      if (!activity.TryGetValue(uuid, out c)) activity[uuid] = c = new Dictionary<string, int>();
      int n;
      c.TryGetValue(what, out n);
      c[what] = n + 1;
    }
  }

  void FlushActivity(string uuid) {
    if (uuid == null) return;
    Dictionary<string, int> c;
    lock (gate) {
      if (!activity.TryGetValue(uuid, out c)) return;
      activity.Remove(uuid);
    }
    var p = PluginByUuid(uuid);
    if (p != null && c.Count > 0) PluginLog(p, "lúc phím hiện trên bo: " + string.Join(", ", c.Select(kv => kv.Key + " ×" + kv.Value)));
  }

  // Bộ phím mới: như Elgato khi đổi hồ sơ, mọi phím đang hiện nhận willDisappear rồi phím của trang đang hiện nhận
  // willAppear (bo vừa xoá nhãn và ảnh plugin, plugin vẽ lại trong willAppear)
  public void LayoutChanged(DeckLayout layout) {
    List<KeyRef> old;
    lock (gate) {
      titles.Clear();
      old = shown.Values.ToList();
      shown.Clear();
    }
    foreach (var k in old) SendKeyEvent("willDisappear", k);
    Ensure();
    int page = deck.VisiblePage;
    if (page >= 0) foreach (var k in KeysOn(page).ToList()) Appear(k);
  }

  // ---------- tin nhắn từ plugin và Property Inspector ----------

  static string S(Dictionary<string, object> d, string key) {
    object v;
    return d != null && d.TryGetValue(key, out v) && v != null ? v.ToString() : "";
  }

  void OnMessage(WsConnection conn, string text) {
    Dictionary<string, object> m;
    try {
      m = json.Deserialize<Dictionary<string, object>>(text);
    } catch (Exception) {
      return;
    }
    string evt = S(m, "event");
    if (conn.Tag == null) {
      Register(conn, evt, m);
      return;
    }
    if (conn.Tag is string && (string)conn.Tag == "host") {
      if (evt == "rendered") {
        rendered[Convert.ToInt32(m["id"])] = S(m, "png");
        renderSignal.Set();
        renderSignal.Reset();
      } else if (evt == "hostError") {
        Log.Agent("Plugin HTML " + S(m, "uuid") + ": " + S(m, "message"));
      }
      return;
    }
    var runtime = conn.Tag as Runtime;
    var inspector = conn.Tag as Inspector;
    PluginInfo plugin = runtime != null ? runtime.Info : null;
    if (inspector != null) lock (gate) plugins.TryGetValue(inspector.Plugin, out plugin);
    if (plugin == null) return;
    if (runtime != null) Count(plugin.Uuid, evt);
    string context = S(m, "context");
    object payloadObj;
    m.TryGetValue("payload", out payloadObj);
    var payload = payloadObj as Dictionary<string, object>;

    switch (evt) {
      case "setSettings":
        SaveSettings(context, json.Serialize(payloadObj ?? new Dictionary<string, object>()));
        if (runtime != null) {
          ToInspector(context, "didReceiveSettings", plugin, payloadObj);
        } else {
          var k = Find(context);
          var r = RuntimeOf(k != null ? k.Key.ActionUuid : DraftAction(context));
          SendPlugin(r, new Dictionary<string, object> {
            { "action", k != null ? k.Key.ActionUuid : DraftAction(context) }, { "event", "didReceiveSettings" }, { "context", context },
            { "device", DeviceId }, { "payload", new Dictionary<string, object> {
                { "settings", payloadObj ?? new Dictionary<string, object>() }, { "coordinates", Coordinates(k != null ? k.Slot : DraftSlot(context)) },
                { "isInMultiAction", false } } },
          });
        }
        break;
      case "getSettings": {
        var k = Find(context);
        var reply = new Dictionary<string, object> {
          { "action", k != null ? k.Key.ActionUuid : DraftAction(context) }, { "event", "didReceiveSettings" }, { "context", context },
          { "device", DeviceId }, { "payload", new Dictionary<string, object> {
              { "settings", ParseObject(SettingsOf(context, k != null ? k.Key.Settings : DraftSettings(context))) },
              { "coordinates", Coordinates(k != null ? k.Slot : DraftSlot(context)) }, { "isInMultiAction", false } } },
        };
        conn.Send(json.Serialize(reply));
        break;
      }
      case "setGlobalSettings":
        lock (gate) {
          globalSettings[plugin.Uuid] = json.Serialize(payloadObj ?? new Dictionary<string, object>());
          File.WriteAllText(GlobalSettingsPath, json.Serialize(globalSettings), new UTF8Encoding(false));
        }
        // báo bên kia (plugin ↔ các Property Inspector của nó)
        var global = new Dictionary<string, object> { { "event", "didReceiveGlobalSettings" }, { "payload", new Dictionary<string, object> { { "settings", payloadObj } } } };
        if (runtime != null) {
          List<WsConnection> pis;
          lock (gate) pis = inspectors.Values.Where(c => c.Tag is Inspector && ((Inspector)c.Tag).Plugin == plugin.Uuid).ToList();
          foreach (var pi in pis) pi.Send(json.Serialize(global));
        } else {
          Runtime r;
          lock (gate) runtimes.TryGetValue(plugin.Uuid, out r);
          SendPlugin(r, global);
        }
        break;
      case "getGlobalSettings": {
        string stored;
        lock (gate) globalSettings.TryGetValue(plugin.Uuid, out stored);
        conn.Send(json.Serialize(new Dictionary<string, object> {
          { "event", "didReceiveGlobalSettings" }, { "payload", new Dictionary<string, object> { { "settings", ParseObject(stored) } } } }));
        break;
      }
      case "sendToPlugin": {
        var k = Find(context);
        Runtime r;
        lock (gate) runtimes.TryGetValue(plugin.Uuid, out r);
        SendPlugin(r, new Dictionary<string, object> {
          { "action", S(m, "action").Length > 0 ? S(m, "action") : (k != null ? k.Key.ActionUuid : DraftAction(context)) },
          { "event", "sendToPlugin" }, { "context", context }, { "payload", payloadObj ?? new Dictionary<string, object>() } });
        break;
      }
      case "sendToPropertyInspector":
        ToInspector(context, "sendToPropertyInspector", plugin, payloadObj, S(m, "action"));
        break;
      case "setTitle": {
        var k = Find(context);
        if (k == null) break;
        string title = payload != null && payload.ContainsKey("title") && payload["title"] != null ? payload["title"].ToString() : "";
        title = title.Replace("\r", "").Replace("\n", " ").Trim();
        lock (gate) titles[context] = title;
        deck.SetKeyTitle(k.BoardPage, k.Slot, title.Length > 0 ? title : null);
        break;
      }
      case "setImage": {
        var k = Find(context);
        if (k == null) break;
        string image = payload != null ? S(payload, "image") : "";
        if (image.Length == 0) {
          deck.SetKeyImage(k.BoardPage, k.Slot, null);  // về ảnh trạng thái trong manifest
          break;
        }
        ThreadPool.QueueUserWorkItem(_ => {
          using (var picture = LoadImage(plugin, image)) {
            if (picture == null) return;
            string t;
            lock (gate) titles.TryGetValue(context, out t);
            bool strip = !string.IsNullOrEmpty(t) || !string.IsNullOrEmpty(k.Key.Title);
            deck.SetKeyImage(k.BoardPage, k.Slot, Compose(picture, strip));
          }
        });
        break;
      }
      case "setState": {
        var k = Find(context);
        if (k == null || payload == null || !payload.ContainsKey("state")) break;
        int state = Convert.ToInt32(payload["state"]);
        k.Key.State = state;
        deck.SetKeyState(k.BoardPage, k.Slot, state == 1);
        break;
      }
      case "showOk":
      case "showAlert": {
        var k = Find(context);
        if (k != null) deck.Feedback(k.BoardPage, k.Slot, evt == "showOk");
        break;
      }
      case "openUrl": {
        string url = payload != null ? S(payload, "url") : "";
        if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase)) {
          try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch (Exception) { }
        }
        break;
      }
      case "logMessage":
        PluginLog(plugin, payload != null ? S(payload, "message") : "");
        break;
      // switchToProfile, setFeedback, setFeedbackLayout, setTriggerDescription: Stream Deck +, bỏ qua
    }
  }

  void Register(WsConnection conn, string evt, Dictionary<string, object> m) {
    string uuid = S(m, "uuid");
    if (evt == "registerHost" && uuid == hostToken) {
      conn.Tag = "host";
      host = conn;
      List<Runtime> html;
      lock (gate) {
        html = runtimes.Values.Where(r => r.Info.Runtime == "html" && r.Conn == null).ToList();
        foreach (var r in html) r.Loaded = true;
      }
      foreach (var r in html) {  // Edge ẩn vừa mở (lại): nạp các plugin HTML đang cần
        SendHost(new Dictionary<string, object> {
          { "event", "loadPlugin" }, { "uuid", r.Info.Uuid }, { "token", r.Token }, { "port", Port }, { "info", InfoJson(r.Info) },
          { "url", "/plugins/" + Uri.EscapeDataString(r.Info.Uuid) + "/" + r.Info.CodePath.Replace('\\', '/') } });
      }
      return;
    }
    if (evt == "registerPlugin") {
      Runtime runtime;
      lock (gate) runtime = runtimes.Values.FirstOrDefault(r => r.Token == uuid);
      if (runtime == null) {
        conn.Close();
        return;
      }
      conn.Tag = runtime;
      List<string> pending;
      lock (gate) {
        runtime.Conn = conn;
        pending = runtime.Pending.ToList();
        runtime.Pending.Clear();
      }
      PluginLog(runtime.Info, "đã đăng ký");
      conn.Send(json.Serialize(new Dictionary<string, object> {
        { "event", "deviceDidConnect" }, { "device", DeviceId },
        { "deviceInfo", new Dictionary<string, object> { { "name", "CYD Deck" }, { "type", 9 },
                                                         { "size", new Dictionary<string, object> { { "columns", 4 }, { "rows", 2 } } } } } }));
      foreach (var text in pending) conn.Send(text);
      // phím của plugin trên trang đang hiện
      int page = deck.VisiblePage;
      foreach (var k in KeysOn(page).Where(k => PluginOfActionUuid(k.Key.ActionUuid) == runtime.Info.Uuid).ToList()) {
        lock (gate) shown.Remove(k.Key.Context);
        Appear(k);
      }
      return;
    }
    if (evt == "registerPropertyInspector") {
      var k = Find(uuid);
      string action = k != null ? k.Key.ActionUuid : DraftAction(uuid);
      string plugin = PluginOfActionUuid(action);
      if (plugin == null) {
        conn.Close();
        return;
      }
      conn.Tag = new Inspector { Context = uuid, Plugin = plugin };
      lock (gate) inspectors[uuid] = conn;
      Runtime r;
      lock (gate) runtimes.TryGetValue(plugin, out r);
      if (k == null) {  // phím chưa lưu: báo plugin như phím vừa hiện để nó dựng sẵn
        SendPlugin(r, new Dictionary<string, object> {
          { "action", action }, { "event", "willAppear" }, { "context", uuid }, { "device", DeviceId },
          { "payload", new Dictionary<string, object> {
              { "settings", ParseObject(SettingsOf(uuid, DraftSettings(uuid))) }, { "coordinates", Coordinates(DraftSlot(uuid)) },
              { "state", 0 }, { "isInMultiAction", false }, { "controller", "Keypad" } } } });
      }
      SendPlugin(r, new Dictionary<string, object> {
        { "action", action }, { "event", "propertyInspectorDidAppear" }, { "context", uuid }, { "device", DeviceId } });
      return;
    }
    conn.Close();
  }

  void OnClose(WsConnection conn) {
    var runtime = conn.Tag as Runtime;
    if (runtime != null) {
      lock (gate) if (runtime.Conn == conn) runtime.Conn = null;
      return;
    }
    var inspector = conn.Tag as Inspector;
    if (inspector != null) {
      lock (gate) {
        WsConnection current;
        if (inspectors.TryGetValue(inspector.Context, out current) && current == conn) inspectors.Remove(inspector.Context);
      }
      var k = Find(inspector.Context);
      string action = k != null ? k.Key.ActionUuid : DraftAction(inspector.Context);
      Runtime r;
      lock (gate) runtimes.TryGetValue(inspector.Plugin, out r);
      SendPlugin(r, new Dictionary<string, object> {
        { "action", action }, { "event", "propertyInspectorDidDisappear" }, { "context", inspector.Context }, { "device", DeviceId } });
      return;
    }
    if (conn.Tag is string && (string)conn.Tag == "host" && host == conn) host = null;
  }

  void ToInspector(string context, string evt, PluginInfo plugin, object payload, string action = null) {
    WsConnection pi;
    lock (gate) inspectors.TryGetValue(context, out pi);
    if (pi == null) return;
    var k = Find(context);
    var message = new Dictionary<string, object> {
      { "action", action ?? (k != null ? k.Key.ActionUuid : DraftAction(context)) }, { "event", evt }, { "context", context },
      { "device", DeviceId },
    };
    if (evt == "didReceiveSettings") {
      message["payload"] = new Dictionary<string, object> {
        { "settings", payload ?? new Dictionary<string, object>() }, { "coordinates", Coordinates(k != null ? k.Slot : DraftSlot(context)) } };
    } else {
      message["payload"] = payload ?? new Dictionary<string, object>();
    }
    pi.Send(json.Serialize(message));
  }

  // Cài đặt của phím: nhớ ngay (trang cài đặt gộp khi lưu), và ghi vào deck.json nếu phím đã có trong bộ phím
  void SaveSettings(string context, string settings) {
    lock (gate) settingsOverride[context] = settings;
    var k = Find(context);
    if (k == null) return;
    k.Key.Settings = settings;
    try {
      DeckConfig.Save(deck.Layout.Config);
    } catch (Exception ex) {
      Log.Agent("Không lưu được cài đặt plugin: " + ex.Message);
    }
  }

  // ---------- Property Inspector của phím chưa lưu ----------

  public void OpenDraft(string context, string actionUuid, int slot, string settings) {
    lock (gate) drafts[context] = new Draft { Context = context, ActionUuid = actionUuid, Slot = slot, Settings = settings };
    Ensure();
  }

  string DraftAction(string context) {
    lock (gate) {
      Draft d;
      return drafts.TryGetValue(context, out d) ? d.ActionUuid : "";
    }
  }

  int DraftSlot(string context) {
    lock (gate) {
      Draft d;
      return drafts.TryGetValue(context, out d) ? d.Slot : 0;
    }
  }

  string DraftSettings(string context) {
    lock (gate) {
      Draft d;
      return drafts.TryGetValue(context, out d) ? d.Settings : "{}";
    }
  }

  public Dictionary<string, object> InspectorInfo(string context, string actionUuid, int slot) {
    PluginAction action;
    var p = PluginOfAction(actionUuid, out action);
    if (p == null || action == null || string.IsNullOrEmpty(action.PropertyInspector)) return null;
    var actionInfo = new Dictionary<string, object> {
      { "action", actionUuid }, { "context", context }, { "device", DeviceId },
      { "payload", new Dictionary<string, object> {
          { "settings", ParseObject(SettingsOf(context, DraftSettings(context))) }, { "coordinates", Coordinates(slot) } } },
    };
    return new Dictionary<string, object> {
      { "url", "/plugins/" + Uri.EscapeDataString(p.Uuid) + "/" + action.PropertyInspector.Replace('\\', '/') },
      { "port", Port }, { "info", InfoJson(p) }, { "actionInfo", json.Serialize(actionInfo) },
    };
  }

  // Đã lưu bộ phím: phím nháp nào đã thành phím thật thì Find thấy; bỏ nháp để không giữ plugin chạy thừa
  public void ClearDrafts() {
    List<Draft> gone;
    lock (gate) {
      gone = drafts.Values.ToList();
      drafts.Clear();
    }
    foreach (var d in gone) {
      if (Find(d.Context) != null) continue;
      SendPlugin(RuntimeOf(d.ActionUuid), new Dictionary<string, object> {
        { "action", d.ActionUuid }, { "event", "willDisappear" }, { "context", d.Context }, { "device", DeviceId },
        { "payload", new Dictionary<string, object> {
            { "settings", ParseObject(SettingsOf(d.Context, d.Settings)) }, { "coordinates", Coordinates(d.Slot) },
            { "state", 0 }, { "isInMultiAction", false }, { "controller", "Keypad" } } } });
    }
    Ensure();
  }

  public PluginInfo PluginByUuid(string uuid) {
    lock (gate) {
      PluginInfo p;
      return plugins.TryGetValue(uuid ?? "", out p) ? p : null;
    }
  }

  public void Restart(string uuid) {
    Runtime r;
    lock (gate) runtimes.TryGetValue(uuid, out r);
    if (r != null) Stop(r);
    lock (gate) crashes.Remove(uuid);
    Reload();
    Ensure();
  }

  // ---------- log từng plugin: logs\plugin-<uuid>.log ----------

  static readonly object logGate = new object();

  public static string LogPath(string uuid) { return Path.Combine(Log.Dir, "plugin-" + uuid + ".log"); }

  static void PluginLog(PluginInfo p, string line) {
    lock (logGate) {
      try {
        string path = LogPath(p.Uuid);
        var fi = new FileInfo(path);
        if (fi.Exists && fi.Length > (512 << 10)) File.Delete(path);
        File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss ") + line + "\r\n", Encoding.UTF8);
      } catch (Exception) {
      }
    }
  }
}

}  // namespace CydAgent
