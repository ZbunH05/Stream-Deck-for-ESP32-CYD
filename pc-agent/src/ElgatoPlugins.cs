// Plugin Stream Deck của Elgato: đọc manifest.json, cài (giải nén file .streamDeckPlugin), chạy tiến trình plugin.
// Định dạng theo tài liệu SDK của Elgato (docs.elgato.com/streamdeck/sdk): thư mục <uuid>.sdPlugin có manifest.json với
// UUID, Name, Actions (UUID, Name, Icon, States[Image, Title, ShowTitle], PropertyInspectorPath), CodePath / CodePathWin,
// Nodejs (plugin Node.js), PropertyInspectorPath, OS.
//
// Plugin cài ở %APPDATA%\CydDeck\plugins\<uuid>.sdPlugin. Tiến trình plugin (.exe hay node) chạy trong một Job Object
// "đóng là tắt": agent thoát (kể cả bị tắt ngang) thì mọi plugin tắt theo. Plugin HTML chạy trong Edge ẩn (ElgatoHost.cs).
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace CydAgent {

public sealed class PluginState {
  public string Image { get; set; }
  public string Title { get; set; }
  public bool ShowTitle { get; set; }
}

public sealed class PluginAction {
  public string Uuid { get; set; }
  public string Name { get; set; }
  public string Tooltip { get; set; }
  public string Icon { get; set; }
  public string PropertyInspector { get; set; }  // đường dẫn trong thư mục plugin
  public List<PluginState> States { get; set; }
  public bool Keypad { get; set; }  // dùng được trên phím (không phải chỉ núm xoay của Stream Deck +)
}

public sealed class PluginInfo {
  public string Uuid { get; set; }
  public string Name { get; set; }
  public string Author { get; set; }
  public string Version { get; set; }
  public string Icon { get; set; }
  public string Category { get; set; }
  public string Folder { get; set; }
  public string CodePath { get; set; }
  public string Runtime { get; set; }  // "exe", "node", "html"
  public string PropertyInspector { get; set; }
  public List<PluginAction> Actions { get; set; }
  public bool Enabled { get; set; }
  public string Error { get; set; }
}

static class ElgatoPlugins {
  public static string Dir { get { return Path.Combine(DeckConfig.Dir, "plugins"); } }
  static string DisabledPath { get { return Path.Combine(DeckConfig.Dir, "plugins-disabled.txt"); } }

  static string S(Dictionary<string, object> d, string key) {
    object v;
    return d != null && d.TryGetValue(key, out v) && v != null ? v.ToString() : "";
  }

  static Dictionary<string, object> D(Dictionary<string, object> d, string key) {
    object v;
    return d != null && d.TryGetValue(key, out v) ? v as Dictionary<string, object> : null;
  }

  static ArrayList A(Dictionary<string, object> d, string key) {
    object v;
    return d != null && d.TryGetValue(key, out v) ? v as ArrayList : null;
  }

  // Đọc một thư mục .sdPlugin
  public static PluginInfo Read(string folder) {
    var m = DeckConfig.Json().Deserialize<Dictionary<string, object>>(File.ReadAllText(Path.Combine(folder, "manifest.json"), Encoding.UTF8));
    var info = new PluginInfo {
      Folder = folder, Name = S(m, "Name"), Author = S(m, "Author"), Version = S(m, "Version"), Icon = S(m, "Icon"),
      Category = S(m, "Category"), PropertyInspector = S(m, "PropertyInspectorPath"), Actions = new List<PluginAction>(),
    };
    info.Uuid = S(m, "UUID");
    if (info.Uuid.Length == 0) {  // manifest cũ không có UUID: lấy theo tên thư mục
      string name = Path.GetFileName(folder.TrimEnd('\\'));
      info.Uuid = name.EndsWith(".sdPlugin", StringComparison.OrdinalIgnoreCase) ? name.Substring(0, name.Length - 9) : name;
    }
    string code = S(m, "CodePathWin");
    if (code.Length == 0) code = S(m, "CodePath");
    info.CodePath = code.Replace('/', '\\');
    string lower = info.CodePath.ToLowerInvariant();
    info.Runtime = D(m, "Nodejs") != null || lower.EndsWith(".js") || lower.EndsWith(".mjs") || lower.EndsWith(".cjs") ? "node"
                 : lower.EndsWith(".html") || lower.EndsWith(".htm") ? "html" : "exe";
    var os = A(m, "OS");
    if (os != null && os.Count > 0 && !os.Cast<object>().Any(o => S(o as Dictionary<string, object>, "Platform") == "windows")) {
      info.Error = I18n.T("plugin.unsupported_os");
    }
    var actions = A(m, "Actions");
    if (actions != null) {
      foreach (Dictionary<string, object> a in actions) {
        var action = new PluginAction {
          Uuid = S(a, "UUID"), Name = S(a, "Name"), Tooltip = S(a, "Tooltip"), Icon = S(a, "Icon"),
          PropertyInspector = S(a, "PropertyInspectorPath"), States = new List<PluginState>(), Keypad = true,
        };
        if (action.PropertyInspector.Length == 0) action.PropertyInspector = info.PropertyInspector;
        var controllers = A(a, "Controllers");
        if (controllers != null && controllers.Count > 0 && !controllers.Cast<object>().Any(c => (c ?? "").ToString() == "Keypad")) {
          action.Keypad = false;
        }
        var states = A(a, "States");
        if (states != null) {
          foreach (Dictionary<string, object> st in states) {
            object show;
            action.States.Add(new PluginState {
              Image = S(st, "Image"), Title = S(st, "Title"),
              ShowTitle = !(st.TryGetValue("ShowTitle", out show) && show is bool && !(bool)show),
            });
          }
        }
        if (action.States.Count == 0) action.States.Add(new PluginState { Image = action.Icon, ShowTitle = true });
        info.Actions.Add(action);
      }
    }
    return info;
  }

  static HashSet<string> Disabled() {
    try {
      if (File.Exists(DisabledPath)) return new HashSet<string>(File.ReadAllLines(DisabledPath).Where(l => l.Length > 0));
    } catch (Exception) {
    }
    return new HashSet<string>();
  }

  public static void SetEnabled(string uuid, bool enabled) {
    var set = Disabled();
    if (enabled) set.Remove(uuid);
    else set.Add(uuid);
    Directory.CreateDirectory(DeckConfig.Dir);
    File.WriteAllLines(DisabledPath, set.ToArray());
  }

  public static List<PluginInfo> List() {
    var list = new List<PluginInfo>();
    if (!Directory.Exists(Dir)) return list;
    var disabled = Disabled();
    foreach (var folder in Directory.GetDirectories(Dir)) {
      if (!File.Exists(Path.Combine(folder, "manifest.json"))) continue;
      try {
        var info = Read(folder);
        info.Enabled = !disabled.Contains(info.Uuid);
        list.Add(info);
      } catch (Exception ex) {
        list.Add(new PluginInfo { Uuid = Path.GetFileName(folder), Name = Path.GetFileName(folder), Folder = folder,
                                  Error = ex.Message, Actions = new List<PluginAction>() });
      }
    }
    return list.OrderBy(p => p.Name).ToList();
  }

  // Cài từ file .streamDeckPlugin (zip chứa <uuid>.sdPlugin/) hay zip tương tự; trả UUID
  public static string Install(string file) {
    if (Path.GetFileName(file).Equals("manifest.json", StringComparison.OrdinalIgnoreCase)) {  // thư mục .sdPlugin có sẵn
      string source = Path.GetDirectoryName(file);
      var plugin = Read(source);
      string dest = Path.Combine(Dir, plugin.Uuid + ".sdPlugin");
      if (string.Equals(Path.GetFullPath(source).TrimEnd('\\'), Path.GetFullPath(dest).TrimEnd('\\'), StringComparison.OrdinalIgnoreCase)) {
        return plugin.Uuid;
      }
      if (Directory.Exists(dest)) Directory.Delete(dest, true);
      CopyDirectory(source, dest);
      Log.Agent("Cài plugin " + plugin.Uuid + " " + plugin.Version + " (" + plugin.Runtime + ") từ thư mục");
      return plugin.Uuid;
    }
    string temp = Path.Combine(Path.GetTempPath(), "cyddeck-plugin-" + Guid.NewGuid().ToString("N"));
    try {
      ZipFile.ExtractToDirectory(file, temp);
      string folder = Directory.GetFiles(temp, "manifest.json", SearchOption.AllDirectories)
                          .Select(Path.GetDirectoryName).OrderBy(d => d.Length).FirstOrDefault();
      if (folder == null) throw new InvalidOperationException(I18n.T("plugin.bad_file"));
      var info = Read(folder);
      Directory.CreateDirectory(Dir);
      string target = Path.Combine(Dir, info.Uuid + ".sdPlugin");
      if (Directory.Exists(target)) Directory.Delete(target, true);
      Directory.Move(folder, target);
      Log.Agent("Cài plugin " + info.Uuid + " " + info.Version + " (" + info.Runtime + ")");
      return info.Uuid;
    } finally {
      try { if (Directory.Exists(temp)) Directory.Delete(temp, true); } catch (Exception) { }
    }
  }

  static void CopyDirectory(string from, string to) {
    Directory.CreateDirectory(to);
    foreach (var f in Directory.GetFiles(from)) File.Copy(f, Path.Combine(to, Path.GetFileName(f)));
    foreach (var d in Directory.GetDirectories(from)) CopyDirectory(d, Path.Combine(to, Path.GetFileName(d)));
  }

  public static void Uninstall(PluginInfo info) {
    if (Directory.Exists(info.Folder)) Directory.Delete(info.Folder, true);
    SetEnabled(info.Uuid, true);
  }

  // Đường dẫn file trong thư mục plugin, chặn ".." thoát ra ngoài
  public static string SafePath(PluginInfo info, string relative) {
    if (string.IsNullOrEmpty(relative)) return null;
    string full = Path.GetFullPath(Path.Combine(info.Folder, relative.Replace('/', '\\')));
    string root = Path.GetFullPath(info.Folder).TrimEnd('\\') + "\\";
    return full.StartsWith(root, StringComparison.OrdinalIgnoreCase) ? full : null;
  }

  // Ảnh trong manifest ghi không có đuôi: thử @2x.png (144 px, nét hơn khi thu về 72), .png, .svg, .jpg
  public static string ImageFile(PluginInfo info, string relative) {
    string basePath = SafePath(info, relative);
    if (basePath == null) return null;
    if (File.Exists(basePath) && Path.HasExtension(basePath)) return basePath;
    foreach (var ext in new[] { "@2x.png", ".png", ".svg", "@2x.jpg", ".jpg", ".gif" }) {
      if (File.Exists(basePath + ext)) return basePath + ext;
    }
    return null;
  }

  public static string NodePath() {
    foreach (var dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';')) {
      try {
        string candidate = Path.Combine(dir.Trim(), "node.exe");
        if (File.Exists(candidate)) return candidate;
      } catch (Exception) {
      }
    }
    string pf = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), @"nodejs\node.exe");
    return File.Exists(pf) ? pf : null;
  }

  // Tham số dòng lệnh theo kiểu Windows: bọc ngoặc kép, thoát ngoặc kép bên trong
  public static string Quote(string arg) {
    if (arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '"', '\t' }) < 0) return arg;
    var sb = new StringBuilder("\"");
    int backslashes = 0;
    foreach (char c in arg) {
      if (c == '\\') {
        backslashes++;
        continue;
      }
      if (c == '"') sb.Append('\\', backslashes * 2 + 1);
      else sb.Append('\\', backslashes);
      backslashes = 0;
      sb.Append(c);
    }
    sb.Append('\\', backslashes * 2);
    sb.Append('"');
    return sb.ToString();
  }
}

// Job Object: tiến trình con tắt khi agent tắt
static class Job {
  [StructLayout(LayoutKind.Sequential)]
  struct BASIC_LIMIT {
    public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
    public uint LimitFlags;
    public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
    public uint ActiveProcessLimit;
    public UIntPtr Affinity;
    public uint PriorityClass, SchedulingClass;
  }
  [StructLayout(LayoutKind.Sequential)]
  struct IO_COUNTERS {
    public ulong a, b, c, d, e, f;
  }
  [StructLayout(LayoutKind.Sequential)]
  struct EXTENDED_LIMIT {
    public BASIC_LIMIT Basic;
    public IO_COUNTERS Io;
    public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
  }
  [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
  static extern IntPtr CreateJobObject(IntPtr attributes, string name);
  [DllImport("kernel32.dll")]
  static extern bool SetInformationJobObject(IntPtr job, int infoClass, ref EXTENDED_LIMIT info, int size);
  [DllImport("kernel32.dll")]
  static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);

  static IntPtr handle = IntPtr.Zero;

  public static void Add(Process p) {
    if (handle == IntPtr.Zero) {
      handle = CreateJobObject(IntPtr.Zero, null);
      var info = new EXTENDED_LIMIT();
      info.Basic.LimitFlags = 0x2000;  // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
      SetInformationJobObject(handle, 9 /* JobObjectExtendedLimitInformation */, ref info, Marshal.SizeOf(typeof(EXTENDED_LIMIT)));
    }
    try { AssignProcessToJobObject(handle, p.Handle); } catch (Exception) { }
  }
}

}  // namespace CydAgent
