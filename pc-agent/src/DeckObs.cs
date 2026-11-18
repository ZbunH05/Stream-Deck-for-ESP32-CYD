// CYD Deck: điều khiển OBS Studio qua obs-websocket 5 (có sẵn trong OBS 28 trở lên; bật ở Tools → WebSocket Server
// Settings). Client viết tay trên ClientWebSocket của .NET 4.5: Hello → Identify (xác thực SHA-256) → Identified, rồi
// yêu cầu (op 6) và sự kiện (op 5). Tự nối lại khi OBS mở/tắt.
//
// Chỉ nối khi bộ phím có phím OBS hay trang có thanh OBS (Needed). Mật khẩu lưu mã hoá DPAPI ở %APPDATA%\CydDeck\obs.json;
// có nút lấy thẳng từ cấu hình obs-websocket của OBS trên cùng máy.
using System;
using System.Collections;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace CydAgent {

public sealed class ObsSettings {
  public string Host { get; set; }
  public int Port { get; set; }
  public bool Enabled { get; set; }
  public string Password { get; set; }  // mã hoá DPAPI (CurrentUser), base64
}

sealed class DeckObs {
  static string FilePath { get { return Path.Combine(DeckConfig.Dir, "obs.json"); } }

  ObsSettings settings;
  string password = "";
  readonly object gate = new object();
  ClientWebSocket ws;
  int nextId;
  readonly ConcurrentDictionary<string, ManualResetEvent> waiting = new ConcurrentDictionary<string, ManualResetEvent>();
  readonly ConcurrentDictionary<string, Dictionary<string, object>> answers = new ConcurrentDictionary<string, Dictionary<string, object>>();
  public Func<bool> Needed = () => false;  // DeckSync: bộ phím có dùng OBS không

  // trạng thái OBS
  volatile bool connected;
  string scene = "", recTime = "", liveTime = "";
  bool rec, recPaused, live, vcam;
  long dropped;
  double cpu;
  List<string> scenes = new List<string>();
  List<string> inputs = new List<string>();
  readonly Dictionary<string, bool> muted = new Dictionary<string, bool>();
  readonly Dictionary<string, double> volume = new Dictionary<string, double>();  // 0..1 (inputVolumeMul)
  public volatile string Status = "";

  public DeckObs() {
    Load();
    new Thread(Run) { IsBackground = true, Name = "obs" }.Start();
  }

  public bool Connected { get { return connected; } }
  // Trang cài đặt đang mở mục OBS hay đang sửa phím OBS: nối OBS dù bộ phím chưa có phím OBS (để đọc danh sách cảnh)
  long wantedUntil;

  public void WakeFor(int seconds) { Interlocked.Exchange(ref wantedUntil, DateTime.UtcNow.AddSeconds(seconds).Ticks); }

  public bool Wanted {
    get { return settings.Enabled && (Needed() || DateTime.UtcNow.Ticks < Interlocked.Read(ref wantedUntil)); }
  }
  public ObsSettings Settings { get { return settings; } }

  // ---------- cài đặt ----------

  void Load() {
    settings = null;
    try {
      if (File.Exists(FilePath)) settings = DeckConfig.Json().Deserialize<ObsSettings>(File.ReadAllText(FilePath, Encoding.UTF8));
    } catch (Exception) {
    }
    if (settings == null) settings = new ObsSettings { Host = "127.0.0.1", Port = 4455, Enabled = true };
    password = Unprotect(settings.Password);
  }

  public void Configure(string host, int port, string newPassword, bool enabled) {
    settings.Host = string.IsNullOrWhiteSpace(host) ? "127.0.0.1" : host.Trim();
    settings.Port = port > 0 ? port : 4455;
    settings.Enabled = enabled;
    if (newPassword != null) {
      password = newPassword;
      settings.Password = Protect(newPassword);
    }
    Directory.CreateDirectory(DeckConfig.Dir);
    File.WriteAllText(FilePath, DeckConfig.Json().Serialize(settings), new UTF8Encoding(false));
    Disconnect();  // nối lại với cài đặt mới
  }

  static string Protect(string text) {
    if (string.IsNullOrEmpty(text)) return "";
    return Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(text), Encoding.ASCII.GetBytes("CydDeck.obs"),
                                                        DataProtectionScope.CurrentUser));
  }

  static string Unprotect(string stored) {
    try {
      if (string.IsNullOrEmpty(stored)) return "";
      return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(stored),
                                                             Encoding.ASCII.GetBytes("CydDeck.obs"), DataProtectionScope.CurrentUser));
    } catch (Exception) {
      return "";
    }
  }

  // Cổng và mật khẩu từ cấu hình obs-websocket của OBS trên máy này (chỉ dùng khi địa chỉ là máy này)
  public static Dictionary<string, object> LocalObsConfig() {
    string path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                               @"obs-studio\plugin_config\obs-websocket\config.json");
    if (!File.Exists(path)) return null;
    return DeckConfig.Json().Deserialize<Dictionary<string, object>>(File.ReadAllText(path, Encoding.UTF8));
  }

  // ---------- kết nối ----------

  void Run() {
    int backoff = 2000;
    while (true) {
      if (!Wanted) {
        Status = "";
        Thread.Sleep(2000);
        continue;
      }
      try {
        Connect();
        backoff = 2000;
        Receive();
      } catch (Exception ex) {
        string message = ex is AggregateException && ex.InnerException != null ? ex.InnerException.Message : ex.Message;
        if (Status != message) Log.Agent("OBS: " + message);
        Status = message;
      }
      Disconnect();
      Thread.Sleep(backoff);
      backoff = Math.Min(15000, backoff * 2);
    }
  }

  void Disconnect() {
    connected = false;
    ClientWebSocket old;
    lock (gate) {
      old = ws;
      ws = null;
    }
    if (old != null) {
      try { old.Abort(); } catch (Exception) { }
      old.Dispose();
    }
    foreach (var pair in waiting) pair.Value.Set();
  }

  void Connect() {
    var socket = new ClientWebSocket();
    var uri = new Uri("ws://" + settings.Host + ":" + settings.Port);
    if (!socket.ConnectAsync(uri, CancellationToken.None).Wait(3000)) throw new TimeoutException(I18n.T("obs.off"));
    lock (gate) ws = socket;
    var hello = ReadMessage(socket);
    if (Op(hello) != 0) throw new InvalidOperationException("OBS: Hello?");
    var d = (Dictionary<string, object>)hello["d"];
    var identify = new Dictionary<string, object> { { "rpcVersion", 1 }, { "eventSubscriptions", 1 | 4 | 8 | 64 } };
    object auth;
    if (d.TryGetValue("authentication", out auth) && auth is Dictionary<string, object>) {
      var a = (Dictionary<string, object>)auth;
      identify["authentication"] = Auth(password, (string)a["salt"], (string)a["challenge"]);
    }
    Send(socket, new Dictionary<string, object> { { "op", 1 }, { "d", identify } });
    var identified = ReadMessage(socket);
    if (Op(identified) != 2) throw new InvalidOperationException(I18n.T("obs.auth"));
    connected = true;
    Status = "OBS " + settings.Host + ":" + settings.Port;
    Log.Agent("Đã nối OBS ở " + settings.Host + ":" + settings.Port);
    // hỏi trạng thái ban đầu; trả lời đến trong Receive()
    new Thread(() => {
      try {
        Request("GetSceneList", null, false);
        Request("GetCurrentProgramScene", null, false);
        Request("GetInputList", null, false);
        Request("GetVirtualCamStatus", null, false);
        while (connected) {  // giờ ghi/live, khung rớt, CPU: mỗi giây
          if (!Wanted) {     // bộ phím không còn phím OBS, trang cài đặt đã đóng: ngắt
            Log.Agent("OBS: không còn cần, ngắt");
            Disconnect();
            break;
          }
          Request("GetRecordStatus", null, false);
          Request("GetStreamStatus", null, false);
          Request("GetStats", null, false);
          Thread.Sleep(1000);
        }
      } catch (Exception) {
        // OBS vừa ngắt giữa chừng (Request ném): vòng Run tự nối lại. Không để lọt ra ngoài: làm sập cả agent
      }
    }) { IsBackground = true, Name = "obs-poll" }.Start();
  }

  static string Auth(string pass, string salt, string challenge) {
    using (var sha = SHA256.Create()) {
      string secret = Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(pass + salt)));
      return Convert.ToBase64String(sha.ComputeHash(Encoding.UTF8.GetBytes(secret + challenge)));
    }
  }

  static int Op(Dictionary<string, object> m) {
    object op;
    return m != null && m.TryGetValue("op", out op) ? Convert.ToInt32(op) : -1;
  }

  static Dictionary<string, object> ReadMessage(ClientWebSocket socket) {
    var buffer = new byte[16384];
    using (var ms = new MemoryStream()) {
      while (true) {
        var task = socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
        var result = task.Result;
        if (result.MessageType == WebSocketMessageType.Close) throw new IOException("OBS đóng kết nối");
        ms.Write(buffer, 0, result.Count);
        if (result.EndOfMessage) break;
      }
      return DeckConfig.Json().Deserialize<Dictionary<string, object>>(Encoding.UTF8.GetString(ms.ToArray()));
    }
  }

  void Send(ClientWebSocket socket, Dictionary<string, object> message) {
    byte[] bytes = Encoding.UTF8.GetBytes(DeckConfig.Json().Serialize(message));
    lock (socket) socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, true, CancellationToken.None).Wait(3000);
  }

  void Receive() {
    ClientWebSocket socket;
    lock (gate) socket = ws;
    while (socket != null && socket.State == WebSocketState.Open) {
      var m = ReadMessage(socket);
      var d = m.ContainsKey("d") ? m["d"] as Dictionary<string, object> : null;
      if (d == null) continue;
      int op = Op(m);
      if (op == 5) OnEvent(Str(d, "eventType"), d.ContainsKey("eventData") ? d["eventData"] as Dictionary<string, object> : null);
      else if (op == 7) OnResponse(d);
    }
  }

  // Gửi yêu cầu; wait: chờ trả lời (lỗi thì ném), không chờ thì trả lời vào OnResponse
  Dictionary<string, object> Request(string type, Dictionary<string, object> data, bool wait) {
    ClientWebSocket socket;
    lock (gate) socket = ws;
    if (socket == null || !connected) throw new InvalidOperationException(I18n.T("obs.off"));
    string id = type + ":" + Interlocked.Increment(ref nextId);
    ManualResetEvent done = null;
    if (wait) waiting[id] = done = new ManualResetEvent(false);
    var d = new Dictionary<string, object> { { "requestType", type }, { "requestId", id } };
    if (data != null) d["requestData"] = data;
    Send(socket, new Dictionary<string, object> { { "op", 6 }, { "d", d } });
    if (!wait) return null;
    try {
      if (!done.WaitOne(3000)) throw new TimeoutException("OBS: " + type);
      Dictionary<string, object> answer;
      answers.TryRemove(id, out answer);
      if (answer == null) throw new InvalidOperationException(I18n.T("obs.off"));
      var status = answer["requestStatus"] as Dictionary<string, object>;
      if (status != null && !(bool)status["result"]) {
        throw new InvalidOperationException("OBS: " + (Str(status, "comment").Length > 0 ? Str(status, "comment") : type));
      }
      return answer.ContainsKey("responseData") ? answer["responseData"] as Dictionary<string, object> : null;
    } finally {
      ManualResetEvent ignored;
      waiting.TryRemove(id, out ignored);
      done.Dispose();
    }
  }

  static string Str(Dictionary<string, object> d, string key) {
    object v;
    return d != null && d.TryGetValue(key, out v) && v != null ? v.ToString() : "";
  }

  static bool Bool(Dictionary<string, object> d, string key) {
    object v;
    return d != null && d.TryGetValue(key, out v) && v is bool && (bool)v;
  }

  static double Num(Dictionary<string, object> d, string key) {
    object v;
    return d != null && d.TryGetValue(key, out v) && v != null ? Convert.ToDouble(v) : 0;
  }

  // "00:12:04.123" → "12:04", "01:02:03.000" → "1:02:03"
  static string Timecode(string tc) {
    if (string.IsNullOrEmpty(tc)) return "";
    var parts = tc.Split('.')[0].Split(':');
    if (parts.Length != 3) return tc;
    int h = int.Parse(parts[0]);
    return h > 0 ? h + ":" + parts[1] + ":" + parts[2] : parts[1] + ":" + parts[2];
  }

  void OnResponse(Dictionary<string, object> d) {
    string id = Str(d, "requestId"), type = Str(d, "requestType");
    var data = d.ContainsKey("responseData") ? d["responseData"] as Dictionary<string, object> : null;
    lock (gate) {
      switch (type) {
        case "GetCurrentProgramScene":
          scene = Str(data, "currentProgramSceneName").Length > 0 ? Str(data, "currentProgramSceneName") : Str(data, "sceneName");
          break;
        case "GetSceneList": {
          var list = data != null && data.ContainsKey("scenes") ? data["scenes"] as ArrayList : null;
          if (list != null) {
            scenes = list.Cast<Dictionary<string, object>>().OrderByDescending(s => Num(s, "sceneIndex"))
                         .Select(s => Str(s, "sceneName")).ToList();
          }
          if (Str(data, "currentProgramSceneName").Length > 0) scene = Str(data, "currentProgramSceneName");
          break;
        }
        case "GetInputList": {
          var list = data != null && data.ContainsKey("inputs") ? data["inputs"] as ArrayList : null;
          if (list != null) inputs = list.Cast<Dictionary<string, object>>().Select(s => Str(s, "inputName")).ToList();
          break;
        }
        case "GetRecordStatus":
          rec = Bool(data, "outputActive");
          recPaused = Bool(data, "outputPaused");
          recTime = rec ? Timecode(Str(data, "outputTimecode")) : "";
          break;
        case "GetStreamStatus":
          live = Bool(data, "outputActive");
          liveTime = live ? Timecode(Str(data, "outputTimecode")) : "";
          break;
        case "GetStats":
          cpu = Num(data, "cpuUsage");
          dropped = (long)(Num(data, "renderSkippedFrames") + Num(data, "outputSkippedFrames"));
          break;
        case "GetVirtualCamStatus":
          vcam = Bool(data, "outputActive");
          break;
        case "GetInputMute":  // AskLater: tên nguồn ở đuôi requestId sau dấu |
          if (id.Contains("|")) muted[id.Substring(id.IndexOf('|') + 1)] = Bool(data, "inputMuted");
          break;
        case "GetInputVolume":
          if (id.Contains("|")) volume[id.Substring(id.IndexOf('|') + 1)] = Num(data, "inputVolumeMul");
          break;
      }
    }
    ManualResetEvent done;
    if (waiting.TryGetValue(id, out done)) {
      answers[id] = d;
      done.Set();
    }
  }

  void OnEvent(string type, Dictionary<string, object> data) {
    lock (gate) {
      switch (type) {
        case "CurrentProgramSceneChanged": scene = Str(data, "sceneName"); break;
        case "SceneListChanged":
        case "SceneCreated":
        case "SceneRemoved":
        case "SceneNameChanged":
          ThreadPool.QueueUserWorkItem(_ => { try { Request("GetSceneList", null, false); } catch (Exception) { } });
          break;
        case "InputCreated":
        case "InputRemoved":
        case "InputNameChanged":
          ThreadPool.QueueUserWorkItem(_ => { try { Request("GetInputList", null, false); } catch (Exception) { } });
          break;
        case "RecordStateChanged":
          rec = Bool(data, "outputActive");
          recPaused = Str(data, "outputState") == "OBS_WEBSOCKET_OUTPUT_PAUSED";
          if (!rec) recTime = "";
          break;
        case "StreamStateChanged":
          live = Bool(data, "outputActive");
          if (!live) liveTime = "";
          break;
        case "VirtualcamStateChanged": vcam = Bool(data, "outputActive"); break;
        case "InputMuteStateChanged": muted[Str(data, "inputName")] = Bool(data, "inputMuted"); break;
        case "InputVolumeChanged": volume[Str(data, "inputName")] = Num(data, "inputVolumeMul"); break;
        case "ExitStarted": ThreadPool.QueueUserWorkItem(_ => Disconnect()); break;
      }
    }
  }

  // ---------- dùng từ bộ phím ----------

  public List<string> Scenes { get { lock (gate) return scenes.ToList(); } }
  public List<string> Inputs { get { lock (gate) return inputs.ToList(); } }

  public void Run(DeckAction a) {
    string target = a.Target ?? "";
    switch (a.Cmd) {
      case "scene": Request("SetCurrentProgramScene", new Dictionary<string, object> { { "sceneName", target } }, true); break;
      case "record": Request("ToggleRecord", null, true); break;
      case "record_pause": Request("ToggleRecordPause", null, true); break;
      case "stream": Request("ToggleStream", null, true); break;
      case "vcam": Request("ToggleVirtualCam", null, true); break;
      case "replay": Request("SaveReplayBuffer", null, true); break;
      case "mute": Request("ToggleInputMute", new Dictionary<string, object> { { "inputName", target } }, true); break;
      case "vol_up":
      case "vol_down": {
        var current = Request("GetInputVolume", new Dictionary<string, object> { { "inputName", target } }, true);
        double mul = Num(current, "inputVolumeMul");
        mul = Math.Max(0, Math.Min(1, Math.Round((mul + (a.Cmd == "vol_up" ? 0.05 : -0.05)) * 20) / 20));
        Request("SetInputVolume", new Dictionary<string, object> { { "inputName", target }, { "inputVolumeMul", mul } }, true);
        lock (gate) volume[target] = mul;
        break;
      }
      default: throw new InvalidOperationException(string.Format(I18n.T("action.unknown"), "obs " + a.Cmd));
    }
  }

  public bool IsOn(DeckAction a) {
    lock (gate) {
      switch (a.Cmd) {
        case "scene": return connected && scene == (a.Target ?? "");
        case "record": return rec;
        case "record_pause": return recPaused;
        case "stream": return live;
        case "vcam": return vcam;
        case "mute": {
          bool m;
          if (muted.TryGetValue(a.Target ?? "", out m)) return m;
          AskLater("GetInputMute", a.Target);
          return false;
        }
        default: return false;
      }
    }
  }

  public string VolumeText(string input) {
    lock (gate) {
      double v;
      if (input != null && volume.TryGetValue(input, out v)) return Math.Round(v * 100) + "%";
    }
    AskLater("GetInputVolume", input);
    return "";
  }

  // Hỏi mute/âm lượng của một nguồn chưa biết; trả lời ghi vào bảng theo tên nguồn (đuôi requestId)
  readonly HashSet<string> asked = new HashSet<string>();

  void AskLater(string type, string input) {
    if (!connected || string.IsNullOrEmpty(input) || !asked.Add(type + input)) return;
    ThreadPool.QueueUserWorkItem(_ => {
      try {
        ClientWebSocket socket;
        lock (gate) socket = ws;
        if (socket == null) return;
        string id = type + ":" + Interlocked.Increment(ref nextId) + "|" + input;
        Send(socket, new Dictionary<string, object> {
          { "op", 6 }, { "d", new Dictionary<string, object> { { "requestType", type }, { "requestId", id },
                                                                  { "requestData", new Dictionary<string, object> { { "inputName", input } } } } } });
      } catch (Exception) {
      } finally {
        Thread.Sleep(2000);
        lock (gate) asked.Remove(type + input);
      }
    });
  }

  // Gói OBS (0x1A) cho thanh tin của bo
  public byte[] InfoPayload() {
    lock (gate) {
      using (var ms = new MemoryStream())
      using (var w = new BinaryWriter(ms)) {
        int flags = (connected ? 1 : 0) | (rec ? 2 : 0) | (live ? 4 : 0) | (recPaused ? 8 : 0);
        w.Write((byte)flags);
        DeckSync.Text(w, live ? liveTime : rec ? recTime : "", 11);
        DeckSync.Text(w, scene, 23);
        w.Write((ushort)Math.Min(65535, dropped));
        w.Write((byte)Math.Max(0, Math.Min(100, Math.Round(cpu))));
        w.Flush();
        return ms.ToArray();
      }
    }
  }
}

}  // namespace CydAgent
