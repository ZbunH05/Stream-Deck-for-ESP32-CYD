// CYD Deck (firmware: ..\..\firmware): bộ phím người dùng đặt trong trang cài đặt (DeckWeb.cs), lưu ở
// %APPDATA%\CydDeck\deck.json, cùng ảnh tải lên ở %APPDATA%\CydDeck\images\. Ở đây: mô hình dữ liệu, bộ phím mặc định,
// dựng các gói LAYOUT_* và vẽ ảnh phím (KeyArt.cs) gửi xuống bo (DeckSync.cs).
using System;
using System.Collections.Generic;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Web.Script.Serialization;

namespace CydAgent {

// Tên thuộc tính giữ nguyên trong deck.json và trong JavaScript của trang cài đặt (web\index.html).
public sealed class DeckIcon {
  public string Type { get; set; }   // "glyph" (Segoe Fluent Icons), "app" (icon của file .exe), "image" (ảnh tải lên), "none"
  public string Glyph { get; set; }  // mã hex, ví dụ "E756"
  public string Path { get; set; }   // app: đường dẫn file; image: tên file trong thư mục images
  public string Bg { get; set; }     // "#RRGGBB" nền phím
  public string Fg { get; set; }     // "#RRGGBB" màu icon chữ
}

public sealed class DeckAction {
  // "none", "run" (chạy chương trình), "open" (web, file, thư mục, ms-settings:...), "hotkey", "text", "media",
  // "volume", "mic", "lock"
  public string Type { get; set; }
  public string Path { get; set; }    // run
  public string Args { get; set; }    // run
  public string Dir { get; set; }     // run
  public bool Admin { get; set; }     // run: quyền admin (Task Scheduler, AdminTasks trong DeckActions.cs)
  public string Target { get; set; }  // open
  public string Keys { get; set; }    // hotkey, ví dụ "Ctrl+Shift+Esc"
  public string Text { get; set; }    // text
  public string Cmd { get; set; }     // media: playpause/next/prev/stop; volume: up/down/mute; mic: mute
}

public sealed class DeckKeyConfig {
  public string Kind { get; set; }  // "empty", "action", "toggle", "folder", "back"
  public string Title { get; set; }
  public string TitleOn { get; set; }
  public DeckIcon Icon { get; set; }
  public DeckIcon IconOn { get; set; }
  public DeckAction Action { get; set; }
  public DeckAction Hold { get; set; }  // giữ 0,5 s
  public int Target { get; set; }       // folder: số thứ tự trang (trong deck.json) mở ra
  public string Live { get; set; }      // "volume": nhãn hiện âm lượng loa
}

public sealed class DeckPageConfig {
  public string Name { get; set; }
  public int Parent { get; set; }  // -1: trang chính; thư mục: trang chứa phím mở nó
  public string Bar { get; set; }  // "info", "obs"
  public List<DeckKeyConfig> Keys { get; set; }
}

public sealed class DeckConfigData {
  public int Version { get; set; }
  public string Name { get; set; }
  public List<DeckPageConfig> Pages { get; set; }
}

// Một bộ phím đã chuẩn hoá để gửi xuống bo: trang chính trước, thư mục sau (bo lật trang trong phần trang chính).
public sealed class DeckLayout {
  public DeckConfigData Config;
  public int[] BoardOf;   // trang trong deck.json → trang trên bo
  public int[] ConfigOf;  // trang trên bo → trang trong deck.json
  public int TopPages;
  public List<byte[]> Frames = new List<byte[]>();  // LAYOUT_BEGIN, LAYOUT_PAGE..., LAYOUT_END
  public uint Crc;
  public Dictionary<uint, byte[]> Images = new Dictionary<uint, byte[]>();  // mã ảnh → điểm ảnh RGB565 đảo byte

  public DeckKeyConfig KeyAt(int boardPage, int slot) {
    if (boardPage < 0 || boardPage >= ConfigOf.Length || slot < 0 || slot >= 8) return null;
    return Config.Pages[ConfigOf[boardPage]].Keys[slot];
  }
}

public static class DeckConfig {
  public const int KEYS = 8, MAX_PAGES = 16, TITLE_BYTES = 23;
  public static readonly string Dir =
      System.IO.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "CydDeck");
  public static string FilePath { get { return System.IO.Path.Combine(Dir, "deck.json"); } }
  public static string ImagesDir { get { return System.IO.Path.Combine(Dir, "images"); } }

  public static JavaScriptSerializer Json() { return new JavaScriptSerializer { MaxJsonLength = int.MaxValue }; }

  // ---------- đọc, ghi, chuẩn hoá ----------

  public static DeckConfigData Load() {
    try {
      if (File.Exists(FilePath)) return Normalize(Json().Deserialize<DeckConfigData>(File.ReadAllText(FilePath, Encoding.UTF8)));
    } catch (Exception ex) {
      Log.Agent("deck.json hỏng, dùng bộ phím mặc định: " + ex.Message);
    }
    return Normalize(Default());
  }

  public static void Save(DeckConfigData config) {
    Directory.CreateDirectory(Dir);
    string temp = FilePath + ".tmp";
    File.WriteAllText(temp, Json().Serialize(config), new UTF8Encoding(false));
    if (File.Exists(FilePath)) File.Replace(temp, FilePath, FilePath + ".bak");
    else File.Move(temp, FilePath);
  }

  static DeckAction None() { return new DeckAction { Type = "none" }; }

  public static DeckConfigData Normalize(DeckConfigData c) {
    if (c == null) c = Default();
    if (string.IsNullOrWhiteSpace(c.Name)) c.Name = "Mặc định";
    if (c.Pages == null || c.Pages.Count == 0) c.Pages = Default().Pages;
    if (c.Pages.Count > MAX_PAGES) c.Pages = c.Pages.Take(MAX_PAGES).ToList();
    c.Version = 1;
    for (int p = 0; p < c.Pages.Count; p++) {
      var page = c.Pages[p];
      if (page.Name == null) page.Name = "Trang " + (p + 1);
      if (page.Parent < -1 || page.Parent >= c.Pages.Count || page.Parent == p) page.Parent = -1;
      if (page.Bar != "obs") page.Bar = "info";
      if (page.Keys == null) page.Keys = new List<DeckKeyConfig>();
      while (page.Keys.Count < KEYS) page.Keys.Add(new DeckKeyConfig { Kind = "empty" });
      if (page.Keys.Count > KEYS) page.Keys = page.Keys.Take(KEYS).ToList();
      for (int k = 0; k < KEYS; k++) {
        var key = page.Keys[k] ?? new DeckKeyConfig();
        page.Keys[k] = key;
        if (!new[] { "empty", "action", "toggle", "folder", "back" }.Contains(key.Kind)) key.Kind = "empty";
        if (key.Title == null) key.Title = "";
        if (key.TitleOn == null) key.TitleOn = "";
        if (key.Live == null) key.Live = "";
        if (key.Icon == null) key.Icon = new DeckIcon { Type = "none" };
        if (key.Action == null) key.Action = None();
        if (key.Hold == null) key.Hold = None();
        if (key.Kind == "folder" && (key.Target < 0 || key.Target >= c.Pages.Count || key.Target == p)) key.Kind = "empty";
      }
    }
    return c;
  }

  // ---------- bộ phím mặc định (lần đầu chạy) ----------

  static DeckIcon G(string glyph, string bg) { return new DeckIcon { Type = "glyph", Glyph = glyph, Bg = bg, Fg = "#FFFFFF" }; }
  static DeckIcon App(string path) { return new DeckIcon { Type = "app", Path = path, Bg = "#1B1C22" }; }
  static DeckKeyConfig K(string kind, string title, DeckIcon icon, DeckAction action) {
    return new DeckKeyConfig { Kind = kind, Title = title, Icon = icon, Action = action };
  }
  static DeckKeyConfig Empty() { return new DeckKeyConfig { Kind = "empty" }; }
  static DeckAction Run(string path, bool admin) { return new DeckAction { Type = "run", Path = path, Args = "", Dir = "", Admin = admin }; }

  public static DeckConfigData Default() {
    string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
    string x86 = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86);
    string win = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
    string ps = System.IO.Path.Combine(win, @"System32\WindowsPowerShell\v1.0\powershell.exe");
    string cmd = System.IO.Path.Combine(win, @"System32\cmd.exe");
    string code = System.IO.Path.Combine(local, @"Programs\Microsoft VS Code\Code.exe");
    string edge = System.IO.Path.Combine(x86, @"Microsoft\Edge\Application\msedge.exe");

    var work = new List<DeckKeyConfig> {
      K("action", "PowerShell", G("E756", "#012456"), Run(ps, false)),
      K("action", "CMD admin", G("E756", "#7A1212"), Run(cmd, true)),
      File.Exists(code) ? K("action", "VS Code", App(code), Run(code, false)) : Empty(),
      File.Exists(edge) ? K("action", "Edge", App(edge), Run(edge, false)) : Empty(),
      K("action", "Phát/Dừng", G("E768", "#1A9E4B"), new DeckAction { Type = "media", Cmd = "playpause" }),
      K("action", "Bài sau", G("E893", "#2B2C33"), new DeckAction { Type = "media", Cmd = "next" }),
      new DeckKeyConfig { Kind = "toggle", Title = "Mic", TitleOn = "Mic tắt", Icon = G("E720", "#2E7D4F"),
                          IconOn = G("EC54", "#B3261E"), Action = new DeckAction { Type = "mic", Cmd = "mute" } },
      new DeckKeyConfig { Kind = "folder", Title = "Hệ thống", Icon = G("E713", "#4A3A8C"), Target = 2 },
    };
    var tools = new List<DeckKeyConfig> {
      K("action", "Thư mục", App(System.IO.Path.Combine(win, "explorer.exe")), Run(System.IO.Path.Combine(win, "explorer.exe"), false)),
      K("action", "Task Mgr", App(System.IO.Path.Combine(win, @"System32\Taskmgr.exe")),
        new DeckAction { Type = "hotkey", Keys = "Ctrl+Shift+Esc" }),
      K("action", "Cắt màn", G("E7A8", "#0E6E8C"), new DeckAction { Type = "hotkey", Keys = "Win+Shift+S" }),
      K("action", "Khóa máy", G("E72E", "#2B2C33"), new DeckAction { Type = "lock" }),
      K("action", "Nhỏ lại", G("E993", "#2B2C33"), new DeckAction { Type = "volume", Cmd = "down" }),
      K("action", "To lên", G("E995", "#2B2C33"), new DeckAction { Type = "volume", Cmd = "up" }),
      new DeckKeyConfig { Kind = "toggle", Title = "Loa", TitleOn = "Tắt tiếng", Icon = G("E767", "#2B2C33"),
                          IconOn = G("E74F", "#B3261E"), Action = new DeckAction { Type = "volume", Cmd = "mute" }, Live = "volume" },
      Empty(),
    };
    var system = new List<DeckKeyConfig> {
      new DeckKeyConfig { Kind = "back", Title = "Quay ra" },
      K("action", "Cài đặt", G("E713", "#2B2C33"), new DeckAction { Type = "open", Target = "ms-settings:" }),
      K("action", "PS admin", G("E756", "#7A1212"), Run(ps, true)),
      K("action", "Thiết bị", G("E7F4", "#2B2C33"), new DeckAction { Type = "open", Target = "devmgmt.msc" }),
      Empty(), Empty(), Empty(), Empty(),
    };
    return new DeckConfigData {
      Version = 1, Name = "Mặc định",
      Pages = new List<DeckPageConfig> {
        new DeckPageConfig { Name = "Làm việc", Parent = -1, Bar = "info", Keys = work },
        new DeckPageConfig { Name = "Công cụ", Parent = -1, Bar = "info", Keys = tools },
        new DeckPageConfig { Name = "Hệ thống", Parent = 0, Bar = "info", Keys = system },
      }
    };
  }

  // ---------- gửi xuống bo ----------

  static byte[] Utf8(string text, int max) {
    byte[] bytes = Encoding.UTF8.GetBytes(text ?? "");
    if (bytes.Length <= max) return bytes;
    int length = max;
    while (length > 0 && (bytes[length] & 0xC0) == 0x80) length--;
    var cut = new byte[length];
    Buffer.BlockCopy(bytes, 0, cut, 0, length);
    return cut;
  }

  static void Text(BinaryWriter w, string text, int max) {
    byte[] bytes = Utf8(text, max);
    w.Write((byte)bytes.Length);
    w.Write(bytes);
  }

  static byte KindCode(string kind) {
    switch (kind) {
      case "action": return 1;
      case "toggle": return 2;
      case "folder": return 3;
      case "back": return 4;
      default: return 0;
    }
  }

  public static DeckLayout Build(DeckConfigData config) {
    var layout = new DeckLayout { Config = config };
    int n = config.Pages.Count;
    var order = Enumerable.Range(0, n).Where(i => config.Pages[i].Parent < 0)
                    .Concat(Enumerable.Range(0, n).Where(i => config.Pages[i].Parent >= 0)).ToList();
    layout.ConfigOf = order.ToArray();
    layout.BoardOf = new int[n];
    for (int b = 0; b < n; b++) layout.BoardOf[order[b]] = b;
    layout.TopPages = order.Count(i => config.Pages[i].Parent < 0);

    var payloads = new List<KeyValuePair<byte, byte[]>>();
    using (var ms = new MemoryStream())
    using (var w = new BinaryWriter(ms)) {
      w.Write((byte)n);
      w.Write((byte)layout.TopPages);
      Text(w, config.Name, TITLE_BYTES);
      w.Flush();
      payloads.Add(new KeyValuePair<byte, byte[]>(DeckSync.LAYOUT_BEGIN, ms.ToArray()));
    }
    for (int b = 0; b < n; b++) {
      var page = config.Pages[order[b]];
      using (var ms = new MemoryStream())
      using (var w = new BinaryWriter(ms)) {
        w.Write((byte)b);
        w.Write((sbyte)(page.Parent < 0 ? -1 : layout.BoardOf[page.Parent]));
        w.Write((byte)(page.Bar == "obs" ? 1 : 0));
        Text(w, page.Name, TITLE_BYTES);
        foreach (var key in page.Keys) {
          bool folder = key.Kind == "folder";
          w.Write(KindCode(key.Kind));
          w.Write((byte)(folder ? layout.BoardOf[key.Target] : 0));
          uint normal = 0, lit = 0;
          if (key.Kind != "empty" && key.Kind != "back") {
            normal = AddImage(layout, key.Icon, key.Title, folder);
            if (key.Kind == "toggle" && key.IconOn != null && key.IconOn.Type != "none") {
              lit = AddImage(layout, key.IconOn, key.TitleOn.Length > 0 ? key.TitleOn : key.Title, false);
            }
          }
          w.Write(normal);
          w.Write(lit);
          Text(w, key.Title, TITLE_BYTES);
          Text(w, key.TitleOn, TITLE_BYTES);
        }
        w.Flush();
        payloads.Add(new KeyValuePair<byte, byte[]>(DeckSync.LAYOUT_PAGE, ms.ToArray()));
      }
    }
    // CRC-32 của các gói như bo lưu: loại + độ dài 2 byte + dữ liệu (link.cpp, keep_frame)
    uint crc = 0;
    foreach (var pair in payloads) {
      var head = new[] { pair.Key, (byte)(pair.Value.Length & 0xFF), (byte)(pair.Value.Length >> 8) };
      crc = Crc32.Update(crc, head);
      crc = Crc32.Update(crc, pair.Value);
      layout.Frames.Add(Frames.Frame(pair.Key, pair.Value));
    }
    layout.Crc = crc;
    layout.Frames.Add(Frames.Frame(DeckSync.LAYOUT_END, BitConverter.GetBytes(crc)));
    return layout;
  }

  // ---------- ảnh phím ----------

  static readonly Dictionary<string, byte[]> renderCache = new Dictionary<string, byte[]>();
  static readonly object renderGate = new object();

  static uint AddImage(DeckLayout layout, DeckIcon icon, string title, bool folder) {
    byte[] px;
    try {
      px = Render(icon, title.Length > 0, folder);
    } catch (Exception ex) {
      Log.Agent("Không vẽ được ảnh phím: " + ex.Message);
      return 0;
    }
    if (px == null) return 0;
    uint hash = Crc32.Update(0, px);
    layout.Images[hash] = px;
    return hash;
  }

  static Color ParseColor(string hex, Color fallback) {
    try {
      if (!string.IsNullOrEmpty(hex) && hex.StartsWith("#") && hex.Length == 7) {
        return Color.FromArgb(Convert.ToInt32(hex.Substring(1, 2), 16), Convert.ToInt32(hex.Substring(3, 2), 16),
                              Convert.ToInt32(hex.Substring(5, 2), 16));
      }
    } catch (Exception) {
    }
    return fallback;
  }

  // Điểm ảnh RGB565 đảo byte của một phím (null: phím không có ảnh, bo chỉ vẽ nền và nhãn)
  public static byte[] Render(DeckIcon icon, bool withTitle, bool folder) {
    if (icon == null || icon.Type == null || icon.Type == "none") return null;
    string cacheKey = Json().Serialize(icon) + "|" + withTitle + "|" + folder;
    lock (renderGate) {
      byte[] cached;
      if (renderCache.TryGetValue(cacheKey, out cached)) return cached;
      Color bg = ParseColor(icon.Bg, Color.FromArgb(0x2B, 0x2C, 0x33));
      Graphics g;
      byte[] px;
      using (var bmp = KeyArt.Begin(bg, out g)) {
        using (g) {
          if (icon.Type == "glyph") {
            int code = Convert.ToInt32(icon.Glyph ?? "E700", 16);
            KeyArt.Glyph(g, "Segoe Fluent Icons", (char)code, 30, ParseColor(icon.Fg, Color.White), withTitle);
          } else if (icon.Type == "app") {
            string file = Environment.ExpandEnvironmentVariables(icon.Path ?? "");
            using (var picture = KeyArt.FileIcon(file, 96)) KeyArt.Picture(g, picture, 40, withTitle);
          } else if (icon.Type == "image") {
            string file = System.IO.Path.Combine(ImagesDir, System.IO.Path.GetFileName(icon.Path ?? ""));
            using (var stream = new MemoryStream(File.ReadAllBytes(file)))
            using (var picture = Image.FromStream(stream)) {
              KeyArt.Cover(g, picture);
            }
          }
          if (withTitle) KeyArt.TitleStrip(g);
          if (folder) KeyArt.FolderMark(g);
        }
        px = KeyArt.ToRgb565Swapped(bmp);
      }
      if (renderCache.Count > 400) renderCache.Clear();
      renderCache[cacheKey] = px;
      return px;
    }
  }
}

// CRC-32 như zlib, giống store_crc32 của bo (src/deck/store.cpp)
public static class Crc32 {
  static readonly uint[] table = MakeTable();

  static uint[] MakeTable() {
    var t = new uint[256];
    for (uint i = 0; i < 256; i++) {
      uint c = i;
      for (int k = 0; k < 8; k++) c = (c & 1) != 0 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
      t[i] = c;
    }
    return t;
  }

  public static uint Update(uint crc, byte[] data) {
    crc = ~crc;
    foreach (byte b in data) crc = table[(crc ^ b) & 0xFF] ^ (crc >> 8);
    return ~crc;
  }
}

}  // namespace CydAgent
