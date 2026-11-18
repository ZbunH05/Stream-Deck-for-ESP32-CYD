// CYD Deck: trang cài đặt phím (web\index.html), phục vụ ở http://127.0.0.1:47831 và mở bằng Edge ở chế độ cửa sổ app
// (menu khay "Cài đặt Deck…", hay lệnh "ui" tới cổng điều khiển 47830).
//
// An toàn: chỉ nghe ở 127.0.0.1. Mọi /api/ cần header X-Deck-Token đúng mã ngẫu nhiên tạo lúc agent khởi động (mã nằm
// trong địa chỉ mở cửa sổ). Trang web lạ trong trình duyệt không gửi được header tự đặt mà không qua kiểm tra CORS (agent
// không bao giờ cho phép), nên không thể tự sửa bộ phím hay tạo lệnh admin. Header Host phải là 127.0.0.1/localhost
// (chặn DNS rebinding).
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
  readonly Func<string, string> pick;  // mở hộp chọn file trên luồng giao diện, trả đường dẫn (null: bỏ)
  readonly string token;
  readonly string webDir;
  public string AdminMessage = "";

  public DeckWeb(DeckSync deck, Link link, Func<string, string> pick) {
    this.deck = deck;
    this.link = link;
    this.pick = pick;
    var bytes = new byte[16];
    using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(bytes);
    token = BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();
    string baseDir = AppDomain.CurrentDomain.BaseDirectory.TrimEnd('\\');
    webDir = Path.Combine(Path.GetDirectoryName(baseDir) ?? baseDir, "web");
    new Thread(Run) { IsBackground = true, Name = "deck-web" }.Start();
  }

  public string Url { get { return "http://127.0.0.1:" + PORT + "/?t=" + token; } }

  // Cửa sổ cài đặt: Edge ở chế độ app (không thanh địa chỉ); không có Edge thì trình duyệt mặc định
  public void Open() {
    string x86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
    string edge = Path.Combine(x86, @"Microsoft\Edge\Application\msedge.exe");
    try {
      if (File.Exists(edge)) {
        Process.Start(edge, "--app=\"" + Url + "\" --window-size=1180,800");
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

  // ---------- HTTP tối giản: một yêu cầu mỗi kết nối ----------

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
        string path = target.Split('?')[0];

        if (method == "GET" && (path == "/" || path == "/index.html")) {
          Respond(stream, 200, "text/html; charset=utf-8", File.ReadAllBytes(Path.Combine(webDir, "index.html")));
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

  // ---------- API ----------

  sealed class RenderRequest {
    public DeckIcon Icon { get; set; }
    public bool Title { get; set; }
    public bool Folder { get; set; }
  }

  sealed class UploadRequest {
    public string Name { get; set; }
    public string Data { get; set; }  // base64
  }

  sealed class TestRequest {
    public DeckKeyConfig Key { get; set; }
    public bool Hold { get; set; }
  }

  sealed class PickRequest {
    public string Kind { get; set; }  // "exe", "any", "image"
  }

  object Api(string method, string path, string body) {
    var json = DeckConfig.Json();
    switch (path) {
      case "/api/state":
        return new {
          config = deck.Layout.Config, connected = deck.Connected, port = link.PortName, status = deck.Status,
          linkStatus = link.Status, lastPress = deck.LastPress, admin = AdminMessage, glyphs = Glyphs.Available(),
        };
      case "/api/config": {
        var config = DeckConfig.Normalize(json.Deserialize<DeckConfigData>(body));
        DeckConfig.Save(config);
        var admin = config.Pages.SelectMany(p => p.Keys)
                        .SelectMany(k => new[] { k.Kind == "action" || k.Kind == "toggle" ? k.Action : null, k.Hold })
                        .Where(a => a != null && a.Type == "run" && a.Admin && !string.IsNullOrWhiteSpace(a.Path));
        AdminMessage = AdminTasks.Sync(admin);  // có lệnh admin mới: Windows hiện một hộp UAC
        deck.Apply(config);
        return new { ok = true, message = "Đã lưu" + (AdminMessage.Length > 0 ? ". " + AdminMessage : ""), config };
      }
      case "/api/render": {
        var r = json.Deserialize<RenderRequest>(body);
        byte[] px = DeckConfig.Render(r.Icon, r.Title, r.Folder);
        if (px == null) return new { png = "" };
        using (var bmp = KeyArt.FromRgb565Swapped(px))
        using (var ms = new MemoryStream()) {
          bmp.Save(ms, ImageFormat.Png);
          return new { png = "data:image/png;base64," + Convert.ToBase64String(ms.ToArray()) };
        }
      }
      case "/api/upload": {
        var r = json.Deserialize<UploadRequest>(body);
        byte[] data = Convert.FromBase64String(r.Data ?? "");
        using (var stream = new MemoryStream(data))
        using (var image = Image.FromStream(stream)) {  // kiểm tra đúng là ảnh
          if (image.Width < 8 || image.Height < 8) throw new InvalidOperationException("Ảnh quá nhỏ");
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
        if (key == null) throw new InvalidOperationException("Thiếu phím");
        DeckAction action = r.Hold ? key.Hold : key.Action;
        DeckActions.Run(action);
        return new { ok = true, message = "Đã chạy" };
      }
      case "/api/open-folder":
        Directory.CreateDirectory(DeckConfig.Dir);
        Process.Start("explorer.exe", "\"" + DeckConfig.Dir + "\"");
        return new { ok = true };
    }
    throw new InvalidOperationException("Không có " + path);
  }
}

// Danh sách icon Segoe Fluent Icons cho ô chọn icon (tên tiếng Việt để tìm). Chỉ đưa ra những ký tự font có.
static class Glyphs {
  static readonly string[,] All = {
    {"E756", "dòng lệnh terminal powershell cmd"}, {"E943", "mã code lập trình"}, {"E774", "web trình duyệt internet"},
    {"E8B7", "thư mục folder"}, {"E8A5", "tài liệu file"}, {"E715", "thư email"}, {"E787", "lịch"},
    {"E722", "máy ảnh camera"}, {"E714", "video phim"}, {"E720", "mic micro"}, {"EC54", "tắt mic"},
    {"E767", "loa âm lượng"}, {"E74F", "tắt tiếng"}, {"E993", "nhỏ lại âm lượng"}, {"E995", "to lên âm lượng"},
    {"E768", "phát play"}, {"E769", "tạm dừng pause"}, {"E893", "bài sau next"}, {"E892", "bài trước previous"},
    {"E71A", "dừng stop"}, {"EC4F", "nhạc nốt nhạc"}, {"E7F6", "tai nghe"}, {"E7F5", "loa ngoài"},
    {"E72E", "khoá lock"}, {"E7E8", "nguồn tắt máy power"}, {"E713", "cài đặt settings"}, {"E721", "tìm kiếm search"},
    {"E80F", "nhà home"}, {"E8C8", "sao chép copy"}, {"E77F", "dán paste"}, {"E8C6", "cắt cut"},
    {"E7A7", "hoàn tác undo"}, {"E7A6", "làm lại redo"}, {"E74E", "lưu save"}, {"E749", "in print"},
    {"E70F", "sửa edit bút"}, {"E74D", "xoá delete thùng rác"}, {"E710", "thêm add cộng"}, {"E72C", "làm mới refresh"},
    {"E7A8", "cắt màn chụp crop"}, {"E7F4", "màn hình monitor"}, {"E93E", "phát trực tiếp live stream"},
    {"E7C8", "ghi hình record"}, {"E8BD", "trò chuyện chat"}, {"E716", "người people"}, {"E7FC", "trò chơi game"},
    {"E706", "độ sáng brightness"}, {"E701", "wifi mạng"}, {"E702", "bluetooth"}, {"E709", "máy bay"},
    {"E8EF", "máy tính calculator"}, {"E917", "đồng hồ clock"}, {"E896", "tải xuống download"},
    {"E898", "tải lên upload"}, {"E71B", "liên kết link"}, {"E718", "ghim pin"}, {"E734", "ngôi sao star"},
    {"EB51", "tim heart"}, {"E7C1", "cờ flag"}, {"EA18", "khiên bảo mật shield"}, {"EBE8", "lỗi bug"},
    {"E765", "bàn phím keyboard"}, {"E962", "chuột mouse"}, {"E717", "điện thoại phone"}, {"E753", "đám mây cloud"},
    {"E7BF", "giỏ hàng"}, {"E804", "xe ô tô"}, {"E707", "bản đồ map"}, {"EB9F", "ảnh photo"}, {"E8D2", "chữ font"},
    {"E76E", "mặt cười emoji"}, {"E9D9", "chẩn đoán task manager"}, {"E895", "đồng bộ sync"}, {"E7EF", "khởi động lại"},
    {"E708", "chế độ tối moon"}, {"E8AF", "điều khiển remote"}, {"E8F4", "danh sách list"}, {"E8FD", "gạch đầu dòng"},
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

}  // namespace CydAgent
