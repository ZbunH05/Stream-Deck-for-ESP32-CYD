using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace CydAgent {

sealed class DeckWeb {
  public const int PORT = 47831;
  readonly DeckSync deck;
  readonly Link link;
  readonly ElgatoHost plugins;
  readonly Func<string, string> pick;
  readonly Action languageChanged;
  readonly string token;
  readonly string webDir;
  public string AdminMessage = "";

  public DeckWeb(DeckSync deck, Link link, ElgatoHost plugins, Func<string, string> pick, Action languageChanged) {
    this.deck = deck;
    this.link = link;
    this.plugins = plugins;
    this.pick = pick;
    this.languageChanged = languageChanged;
    var bytes = new byte[16];
    using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
    token = BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
    string baseDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
    webDir = Path.Combine(Path.GetDirectoryName(baseDir) ?? baseDir, "web");
    plugins.HostPageUrl = () => "http://127.0.0.1:" + PORT + "/plugin-host.html?v=1";
    new Thread(Run) { IsBackground = true, Name = "deck-web" }.Start();
  }

  public string Url { get { return "http://127.0.0.1:" + PORT + "/?t=" + token; } }

  public void Open() {
    string x86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
    string edge = Path.Combine(x86, @"Microsoft\Edge\Application\msedge.exe");
    try {
      if (File.Exists(edge)) {
        Process.Start(edge, "--app=\"" + Url + "\" --window-size=1280,820");
      } else {
        Process.Start(new ProcessStartInfo(Url) { UseShellExecute = true });
      }
    } catch (Exception ex) {
      Log.Agent("Không mở được trang cài đặt: " + ex.Message);
    }
  }

  void Run() {
    TcpListener listener;
    try {
      listener = new TcpListener(IPAddress.Loopback, PORT);
      listener.Start();
    } catch (Exception ex) {
      Log.Agent("Không mở được cổng trang cài đặt " + PORT + ": " + ex.Message);
      return;
    }
    while (true) {
      try {
        TcpClient client = listener.AcceptTcpClient();
        ThreadPool.QueueUserWorkItem(_ => Serve(client));
      } catch (Exception) {
      }
    }
  }

  static readonly Dictionary<string, string> Types = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase) {
    { ".html", "text/html; charset=utf-8" }, { ".htm", "text/html; charset=utf-8" }, { ".js", "text/javascript; charset=utf-8" },
    { ".mjs", "text/javascript; charset=utf-8" }, { ".css", "text/css; charset=utf-8" }, { ".json", "application/json; charset=utf-8" },
    { ".png", "image/png" }, { ".jpg", "image/jpeg" }, { ".jpeg", "image/jpeg" }, { ".gif", "image/gif" }, { ".svg", "image/svg+xml" },
    { ".ico", "image/x-icon" }, { ".webp", "image/webp" }, { ".woff", "font/woff" }, { ".woff2", "font/woff2" }, { ".ttf", "font/ttf" },
    { ".otf", "font/otf" }, { ".map", "application/json" }, { ".txt", "text/plain; charset=utf-8" }, { ".wav", "audio/wav" },
    { ".mp3", "audio/mpeg" },
  };

  void Serve(TcpClient client) {
    using (client) {
      try {
        client.ReceiveTimeout = 15000;
        var stream = client.GetStream();
        string head = ReadHead(stream);
        if (head == null) return;
        var lines = head.Split(new[] { "\r\n" }, StringSplitOptions.None);
        var first = lines[0].Split(' ');
        if (first.Length < 2) return;
        string method = first[0], target = first[1];
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in lines.Skip(1)) {
          int colon = line.IndexOf(':');
          if (colon > 0) headers[line.Substring(0, colon).Trim()] = line.Substring(colon + 1).Trim();
        }
        string host;
        headers.TryGetValue("Host", out host);
        if (host != "127.0.0.1:" + PORT && host != "localhost:" + PORT) {
          Respond(stream, 403, "text/plain", Encoding.UTF8.GetBytes("Host khong hop le"));
          return;
        }
        int length = 0;
        string lengthText;
        if (headers.TryGetValue("Content-Length", out lengthText)) int.TryParse(lengthText, out length);
        if (length > 32 << 20) return;
        byte[] body = ReadBody(stream, length);
        string path = Uri.UnescapeDataString(target.Split('?')[0]);

        if (method == "GET" && (path == "/" || path == "/index.html" || path == "/plugin-host.html")) {
          string file = path == "/plugin-host.html" ? "plugin-host.html" : "index.html";
          Respond(stream, 200, "text/html; charset=utf-8", File.ReadAllBytes(Path.Combine(webDir, file)));
          return;
        }
        if (method == "GET" && path.StartsWith("/plugins/")) {
          ServePluginFile(stream, path.Substring("/plugins/".Length));
          return;
        }
        if (!path.StartsWith("/api/")) {
          Respond(stream, 404, "text/plain", Encoding.UTF8.GetBytes("Khong co"));
          return;
        }
        string given;
        headers.TryGetValue("X-Deck-Token", out given);
        if (given != token) {
          Respond(stream, 403, "text/plain", Encoding.UTF8.GetBytes("Sai ma"));
          return;
        }
        object result = Api(method, path, Encoding.UTF8.GetString(body));
        Respond(stream, 200, "application/json; charset=utf-8", Encoding.UTF8.GetBytes(DeckConfig.Json().Serialize(result)));
      } catch (Exception ex) {
        Log.Agent("Trang cài đặt lỗi: " + ex.Message);
        try {
          Respond(client.GetStream(), 500, "application/json; charset=utf-8",
                  Encoding.UTF8.GetBytes(DeckConfig.Json().Serialize(new { ok = false, message = ex.Message })));
        } catch (Exception) {
        }
      }
    }
  }

  void ServePluginFile(NetworkStream stream, string rest) {
    int slash = rest.IndexOf('/');
    var plugin = plugins.PluginByUuid(slash > 0 ? rest.Substring(0, slash) : rest);
    string relative = slash > 0 ? rest.Substring(slash + 1) : "";
    string file = null;
    if (plugin != null) {
      if (relative.StartsWith("@icon")) {
        string actionUuid = relative.Length > 6 ? relative.Substring(6) : "";
        var action = plugin.Actions.FirstOrDefault(a => a.Uuid == actionUuid);
        file = ElgatoPlugins.ImageFile(plugin, action != null ? action.Icon : plugin.Icon) ??
               ElgatoPlugins.ImageFile(plugin, plugin.Icon);
      } else {
        file = ElgatoPlugins.SafePath(plugin, relative);
      }
    }
    if (file == null || !File.Exists(file)) {
      Respond(stream, 404, "text/plain", Encoding.UTF8.GetBytes("Khong co"));
      return;
    }
    string type;
    if (!Types.TryGetValue(Path.GetExtension(file), out type)) type = "application/octet-stream";
    Respond(stream, 200, type, File.ReadAllBytes(file));
  }

  static string ReadHead(NetworkStream stream) {
    var bytes = new List<byte>();
    int b;
    while ((b = stream.ReadByte()) >= 0) {
      bytes.Add((byte)b);
      int n = bytes.Count;
      if (n >= 4 && bytes[n - 4] == 13 && bytes[n - 3] == 10 && bytes[n - 2] == 13 && bytes[n - 1] == 10) {
        return Encoding.UTF8.GetString(bytes.ToArray(), 0, n - 4);
      }
      if (n > 65536) return null;
    }
    return null;
  }

  static byte[] ReadBody(NetworkStream stream, int length) {
    var body = new byte[length];
    int read = 0;
    while (read < length) {
      int n = stream.Read(body, read, length - read);
      if (n <= 0) break;
      read += n;
    }
    return body;
  }

  static void Respond(NetworkStream stream, int code, string type, byte[] body) {
    string status = code == 200 ? "OK" : code == 403 ? "Forbidden" : code == 404 ? "Not Found" : "Error";
    string head = "HTTP/1.1 " + code + " " + status + "\r\nContent-Type: " + type + "\r\nContent-Length: " + body.Length +
                  "\r\nCache-Control: no-store\r\nX-Content-Type-Options: nosniff\r\nConnection: close\r\n\r\n";
    byte[] h = Encoding.ASCII.GetBytes(head);
    stream.Write(h, 0, h.Length);
    stream.Write(body, 0, body.Length);
  }

  sealed class RenderRequest {
    public DeckIcon Icon { get; set; }
    public bool Title { get; set; }
    public bool Folder { get; set; }
  }

  sealed class UploadRequest {
    public string Name { get; set; }
    public string Data { get; set; }
  }

  sealed class TestRequest {
    public DeckKeyConfig Key { get; set; }
    public bool Hold { get; set; }
  }

  sealed class PickRequest {
    public string Kind { get; set; }
  }

  sealed class AutostartRequest {
    public bool Enabled { get; set; }
  }

  sealed class LangRequest {
    public string Lang { get; set; }
  }

  sealed class ObsRequest {
    public string Host { get; set; }
    public int Port { get; set; }
    public string Password { get; set; }
    public bool Enabled { get; set; }
  }

  sealed class PluginRequest {
    public string Uuid { get; set; }
    public bool Enabled { get; set; }
  }

  sealed class InspectorRequest {
    public string Context { get; set; }
    public string ActionUuid { get; set; }
    public int Slot { get; set; }
    public string Settings { get; set; }
  }

  sealed class PluginArtRequest {
    public DeckKeyConfig Key { get; set; }
    public int State { get; set; }
  }

  static string Png(byte[] px) {
    if (px == null) return "";
    using (var bmp = KeyArt.FromRgb565Swapped(px))
    using (var ms = new MemoryStream()) {
      bmp.Save(ms, ImageFormat.Png);
      return "data:image/png;base64," + Convert.ToBase64String(ms.ToArray());
    }
  }

  object Status() {
    var m = deck.LastMedia;
    var obs = deck.Obs;
    return new {
      connected = deck.Connected, port = link.PortName, status = deck.Status, linkStatus = link.Status, lastPress = deck.LastPress,
      admin = AdminMessage, lang = I18n.Lang, autostart = Autostart.Enabled,
      media = m.Has ? new { title = m.Title, artist = m.Artist, app = m.App, playing = m.Playing, permille = m.Permille } : null,
      obs = new {
        host = obs.Settings.Host, port = obs.Settings.Port, enabled = obs.Settings.Enabled,
        hasPassword = !string.IsNullOrEmpty(obs.Settings.Password), connected = obs.Connected, wanted = obs.Wanted,
        status = obs.Status, scenes = obs.Scenes, inputs = obs.Inputs,
      },
    };
  }

  object PluginList() {
    return plugins.Plugins.Select(p => new {
      uuid = p.Uuid, name = p.Name, author = p.Author, version = p.Version, category = p.Category, runtime = p.Runtime,
      enabled = p.Enabled, error = p.Error,
      actions = p.Actions.Where(a => a.Keypad).Select(a => new {
        uuid = a.Uuid, name = a.Name, tooltip = a.Tooltip, inspector = !string.IsNullOrEmpty(a.PropertyInspector),
        states = a.States.Count,
      }).ToList(),
    }).ToList();
  }

  object Api(string method, string path, string body) {
    var json = DeckConfig.Json();
    switch (path) {
      case "/api/state":
        return new { config = deck.Layout.Config, glyphs = Glyphs.Available(), plugins = PluginList(), status = Status() };
      case "/api/status":
        return Status();
      case "/api/obs/wake":
        deck.Obs.WakeFor(60);
        return Status();
      case "/api/config": {
        var config = DeckConfig.Normalize(json.Deserialize<DeckConfigData>(body));
        foreach (var k in config.Pages.SelectMany(p => p.Keys).Where(k => k.Kind == "plugin")) {
          k.Settings = plugins.LatestSettings(k.Context, k.Settings);
        }
        DeckConfig.Save(config);
        var admin = config.Pages.SelectMany(p => p.Keys)
                        .SelectMany(k => new[] { k.Kind == "action" || k.Kind == "toggle" ? k.Action : null, k.Hold })
                        .Where(a => a != null && a.Type == "run" && a.Admin && !string.IsNullOrWhiteSpace(a.Path));
        AdminMessage = AdminTasks.Sync(admin);
        deck.Apply(config);
        plugins.ClearDrafts();
        languageChanged();
        return new { ok = true, message = I18n.T("web.saved") + (AdminMessage.Length > 0 ? ". " + AdminMessage : ""), config };
      }
      case "/api/autostart": {
        var r = json.Deserialize<AutostartRequest>(body);
        Autostart.Set(r.Enabled);
        return new { ok = true, message = I18n.T(Autostart.Enabled ? "web.autostart_on" : "web.autostart_off"), status = Status() };
      }
      case "/api/lang": {
        var r = json.Deserialize<LangRequest>(body);
        var config = deck.Layout.Config;
        config.Lang = r.Lang == "vi" ? "vi" : "en";
        I18n.Lang = config.Lang;
        DeckConfig.Save(config);
        deck.Apply(config);
        languageChanged();
        return new { ok = true, lang = config.Lang };
      }
      case "/api/render": {
        var r = json.Deserialize<RenderRequest>(body);
        return new { png = Png(DeckConfig.Render(r.Icon, r.Title, r.Folder)) };
      }
      case "/api/plugin-art": {
        var r = json.Deserialize<PluginArtRequest>(body);
        if (r.Key == null || DeckConfig.PluginArt == null) return new { png = "" };
        return new { png = Png(DeckConfig.PluginArt(r.Key, r.State, !string.IsNullOrEmpty(r.Key.Title))) };
      }
      case "/api/upload": {
        var r = json.Deserialize<UploadRequest>(body);
        byte[] data = Convert.FromBase64String(r.Data ?? "");
        using (var stream = new MemoryStream(data))
        using (var image = Image.FromStream(stream)) {
          if (image.Width < 8 || image.Height < 8) throw new InvalidOperationException(I18n.T("web.image_small"));
        }
        Directory.CreateDirectory(DeckConfig.ImagesDir);
        string ext = Path.GetExtension(r.Name ?? "").ToLowerInvariant();
        if (!new[] { ".png", ".jpg", ".jpeg", ".gif", ".bmp", ".ico" }.Contains(ext)) ext = ".png";
        string file = Crc32.Update(0, data).ToString("x8") + ext;
        File.WriteAllBytes(Path.Combine(DeckConfig.ImagesDir, file), data);
        return new { ok = true, file };
      }
      case "/api/pick": {
        var r = json.Deserialize<PickRequest>(body);
        return new { path = pick(r.Kind ?? "any") };
      }
      case "/api/test": {
        var r = json.Deserialize<TestRequest>(body);
        var key = r.Key;
        if (key == null) throw new InvalidOperationException(I18n.T("web.no_key"));
        DeckAction action = r.Hold ? key.Hold : key.Action;
        if (action != null && action.Type == "obs") deck.Obs.Run(action);
        else DeckActions.Run(action);
        return new { ok = true, message = I18n.T("web.ran") };
      }
      case "/api/open-folder":
        Directory.CreateDirectory(DeckConfig.Dir);
        Process.Start("explorer.exe", "\"" + DeckConfig.Dir + "\"");
        return new { ok = true };

      case "/api/obs": {
        var r = json.Deserialize<ObsRequest>(body);
        deck.Obs.WakeFor(120);
        deck.Obs.Configure(r.Host, r.Port, string.IsNullOrEmpty(r.Password) ? null : r.Password, r.Enabled);
        return new { ok = true, message = I18n.T("web.obs_saved"), status = Status() };
      }
      case "/api/obs/local": {
        string obsHost = deck.Obs.Settings.Host ?? "127.0.0.1";
        if (obsHost != "127.0.0.1" && obsHost != "localhost" && obsHost != "::1") throw new InvalidOperationException(I18n.T("web.obs_not_local"));
        var c = DeckObs.LocalObsConfig();
        if (c == null) throw new InvalidOperationException(I18n.T("obs.no_config"));
        object v;
        bool enabled = c.TryGetValue("server_enabled", out v) && v is bool && (bool)v;
        int port = c.TryGetValue("server_port", out v) && v != null ? Convert.ToInt32(v) : 4455;
        bool auth = c.TryGetValue("auth_required", out v) && v is bool && (bool)v;
        string password = auth && c.TryGetValue("server_password", out v) && v != null ? v.ToString() : "";
        deck.Obs.Configure(obsHost, port, password, true);
        return new { ok = true, serverEnabled = enabled, message = enabled ? I18n.T("web.obs_local") : I18n.T("web.obs_local_off"), status = Status() };
      }

      case "/api/plugins":
        return PluginList();
      case "/api/plugins/install": {
        string file = pick("plugin");
        if (file == null) return new { ok = false, message = "" };
        string uuid = ElgatoPlugins.Install(file);
        plugins.Restart(uuid);
        var p = plugins.PluginByUuid(uuid);
        return new { ok = true, message = string.Format(I18n.T("plugin.installed"), p != null ? p.Name : uuid), uuid, plugins = PluginList() };
      }
      case "/api/plugins/enable": {
        var r = json.Deserialize<PluginRequest>(body);
        var p = plugins.PluginByUuid(r.Uuid);
        if (p == null) throw new InvalidOperationException(I18n.T("plugin.none"));
        ElgatoPlugins.SetEnabled(p.Uuid, r.Enabled);
        plugins.Restart(p.Uuid);
        return new { ok = true, message = string.Format(I18n.T(r.Enabled ? "plugin.enabled" : "plugin.disabled"), p.Name), plugins = PluginList() };
      }
      case "/api/plugins/restart": {
        var r = json.Deserialize<PluginRequest>(body);
        var p = plugins.PluginByUuid(r.Uuid);
        if (p == null) throw new InvalidOperationException(I18n.T("plugin.none"));
        p.Error = null;
        plugins.Restart(p.Uuid);
        return new { ok = true, message = string.Format(I18n.T("plugin.restarted"), p.Name), plugins = PluginList() };
      }
      case "/api/plugins/remove": {
        var r = json.Deserialize<PluginRequest>(body);
        var p = plugins.PluginByUuid(r.Uuid);
        if (p == null) throw new InvalidOperationException(I18n.T("plugin.none"));
        ElgatoPlugins.SetEnabled(p.Uuid, false);
        plugins.Restart(p.Uuid);
        Thread.Sleep(300);
        ElgatoPlugins.Uninstall(p);
        plugins.Reload();
        return new { ok = true, message = string.Format(I18n.T("plugin.removed"), p.Name), plugins = PluginList() };
      }
      case "/api/plugins/log": {
        var r = json.Deserialize<PluginRequest>(body);
        string file = ElgatoHost.LogPath(r.Uuid ?? "");
        string text = "";
        if (File.Exists(file)) {
          using (var fs = new FileStream(file, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
          using (var reader = new StreamReader(fs, Encoding.UTF8)) text = reader.ReadToEnd();
          var lines = text.Split('\n');
          if (lines.Length > 200) text = string.Join("\n", lines.Skip(lines.Length - 200));
        }
        return new { text };
      }
      case "/api/plugins/inspector": {
        var r = json.Deserialize<InspectorRequest>(body);
        if (string.IsNullOrEmpty(r.Context)) throw new InvalidOperationException(I18n.T("web.no_key"));
        plugins.OpenDraft(r.Context, r.ActionUuid, r.Slot, string.IsNullOrEmpty(r.Settings) ? "{}" : r.Settings);
        var info = plugins.InspectorInfo(r.Context, r.ActionUuid, r.Slot);
        return new { ok = info != null, inspector = info };
      }
      case "/api/plugins/settings": {
        var r = json.Deserialize<InspectorRequest>(body);
        return new { settings = plugins.LatestSettings(r.Context ?? "", r.Settings) };
      }
    }
    throw new InvalidOperationException("Không có " + path);
  }
}

static class Glyphs {
  static readonly string[,] All = {
    {"E756", "dòng lệnh terminal powershell cmd"}, {"E943", "mã code lập trình programming"}, {"E774", "web trình duyệt internet browser globe"},
    {"E8B7", "thư mục folder"}, {"E8A5", "tài liệu file document"}, {"E715", "thư email mail"}, {"E787", "lịch calendar"},
    {"E722", "máy ảnh camera"}, {"E714", "video phim movie"}, {"E720", "mic micro microphone"}, {"EC54", "tắt mic mute microphone"},
    {"E767", "loa âm lượng volume speaker"}, {"E74F", "tắt tiếng mute"}, {"E993", "nhỏ lại âm lượng volume down"}, {"E995", "to lên âm lượng volume up"},
    {"E768", "phát play"}, {"E769", "tạm dừng pause"}, {"E893", "bài sau next"}, {"E892", "bài trước previous"},
    {"E71A", "dừng stop"}, {"EC4F", "nhạc nốt nhạc music"}, {"E7F6", "tai nghe headphones"}, {"E7F5", "loa ngoài speakers"},
    {"E72E", "khoá lock"}, {"E7E8", "nguồn tắt máy power shutdown"}, {"E713", "cài đặt settings gear"}, {"E721", "tìm kiếm search"},
    {"E80F", "nhà home"}, {"E8C8", "sao chép copy"}, {"E77F", "dán paste"}, {"E8C6", "cắt cut"},
    {"E7A7", "hoàn tác undo"}, {"E7A6", "làm lại redo"}, {"E74E", "lưu save"}, {"E749", "in print"},
    {"E70F", "sửa edit bút pen"}, {"E74D", "xoá delete thùng rác trash"}, {"E710", "thêm add cộng plus"}, {"E72C", "làm mới refresh"},
    {"E7A8", "cắt màn chụp crop screenshot"}, {"E7F4", "màn hình monitor display"}, {"E93E", "phát trực tiếp live stream"},
    {"E7C8", "ghi hình record"}, {"E8BD", "trò chuyện chat message"}, {"E716", "người people"}, {"E7FC", "trò chơi game"},
    {"E706", "độ sáng brightness"}, {"E701", "wifi mạng network"}, {"E702", "bluetooth"}, {"E709", "máy bay airplane"},
    {"E8EF", "máy tính calculator"}, {"E917", "đồng hồ clock time"}, {"E896", "tải xuống download"},
    {"E898", "tải lên upload"}, {"E71B", "liên kết link"}, {"E718", "ghim pin"}, {"E734", "ngôi sao star favorite"},
    {"EB51", "tim heart"}, {"E7C1", "cờ flag"}, {"EA18", "khiên bảo mật shield security"}, {"EBE8", "lỗi bug"},
    {"E765", "bàn phím keyboard"}, {"E962", "chuột mouse"}, {"E717", "điện thoại phone"}, {"E753", "đám mây cloud"},
    {"E7BF", "giỏ hàng shopping cart"}, {"E804", "xe ô tô car"}, {"E707", "bản đồ map"}, {"EB9F", "ảnh photo picture"}, {"E8D2", "chữ font text"},
    {"E76E", "mặt cười emoji smile"}, {"E9D9", "chẩn đoán task manager diagnostic"}, {"E895", "đồng bộ sync"}, {"E7EF", "khởi động lại restart"},
    {"E708", "chế độ tối moon dark"}, {"E8AF", "điều khiển remote"}, {"E8F4", "danh sách list"}, {"E8FD", "gạch đầu dòng bullets"},
  };

  static List<object> cache;

  public static List<object> Available() {
    if (cache != null) return cache;
    var list = new List<object>();
    for (int i = 0; i < All.GetLength(0); i++) {
      bool ok;
      try {
        ok = KeyArt.HasGlyph("Segoe Fluent Icons", (char)Convert.ToInt32(All[i, 0], 16));
      } catch (Exception) {
        ok = false;
      }
      if (ok) list.Add(new { code = All[i, 0], name = All[i, 1] });
    }
    cache = list;
    return list;
  }
}

}
