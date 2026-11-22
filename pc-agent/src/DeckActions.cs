using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Threading;

namespace CydAgent {

static class Input {
  [StructLayout(LayoutKind.Sequential)]
  struct KEYBDINPUT {
    public ushort wVk, wScan;
    public uint dwFlags, time;
    public IntPtr dwExtraInfo;
  }
  [StructLayout(LayoutKind.Explicit)]
  struct INPUT {
    [FieldOffset(0)] public uint type;
    [FieldOffset(8)] public KEYBDINPUT ki;
    [FieldOffset(8)] public long pad0;
    [FieldOffset(16)] public long pad1;
    [FieldOffset(24)] public long pad2;
    [FieldOffset(32)] public long pad3;
  }
  const uint INPUT_KEYBOARD = 1, KEYEVENTF_KEYUP = 2, KEYEVENTF_UNICODE = 4, KEYEVENTF_EXTENDEDKEY = 1;

  [DllImport("user32.dll", SetLastError = true)]
  static extern uint SendInput(uint count, INPUT[] inputs, int size);
  [DllImport("user32.dll")]
  public static extern bool LockWorkStation();

  static INPUT Key(ushort vk, bool up) {
    var input = new INPUT { type = INPUT_KEYBOARD };
    input.ki.wVk = vk;
    input.ki.dwFlags = (up ? KEYEVENTF_KEYUP : 0) | (Extended(vk) ? KEYEVENTF_EXTENDEDKEY : 0);
    return input;
  }

  static bool Extended(ushort vk) {
    return (vk >= 0x21 && vk <= 0x2E) || vk == 0x5B || vk == 0x5C || (vk >= 0xA6 && vk <= 0xB7);
  }

  static readonly Dictionary<string, ushort> Names = new Dictionary<string, ushort>(StringComparer.OrdinalIgnoreCase) {
    {"ctrl", 0x11}, {"control", 0x11}, {"shift", 0x10}, {"alt", 0x12}, {"win", 0x5B}, {"meta", 0x5B},
    {"esc", 0x1B}, {"escape", 0x1B}, {"tab", 0x09}, {"enter", 0x0D}, {"space", 0x20}, {"backspace", 0x08},
    {"delete", 0x2E}, {"del", 0x2E}, {"insert", 0x2D}, {"home", 0x24}, {"end", 0x23}, {"pageup", 0x21},
    {"pagedown", 0x22}, {"up", 0x26}, {"down", 0x28}, {"left", 0x25}, {"right", 0x27}, {"printscreen", 0x2C},
    {"pause", 0x13}, {"capslock", 0x14}, {"menu", 0x5D}, {"plus", 0xBB}, {"minus", 0xBD}, {"comma", 0xBC},
    {"period", 0xBE}, {"slash", 0xBF}, {"backquote", 0xC0}, {"semicolon", 0xBA}, {"quote", 0xDE},
    {"bracketleft", 0xDB}, {"bracketright", 0xDD}, {"backslash", 0xDC}, {"equal", 0xBB},
    {"volumemute", 0xAD}, {"volumedown", 0xAE}, {"volumeup", 0xAF}, {"medianext", 0xB0}, {"mediaprev", 0xB1},
    {"mediastop", 0xB2}, {"mediaplaypause", 0xB3},
  };

  static ushort Parse(string name) {
    ushort vk;
    if (Names.TryGetValue(name, out vk)) return vk;
    if (name.Length == 1) {
      char c = char.ToUpperInvariant(name[0]);
      if ((c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9')) return c;
    }
    int f;
    if (name.Length >= 2 && (name[0] == 'F' || name[0] == 'f') && int.TryParse(name.Substring(1), out f) && f >= 1 && f <= 24) {
      return (ushort)(0x70 + f - 1);
    }
    throw new ArgumentException(string.Format(I18n.T("action.bad_key"), name));
  }

  public static void Hotkey(string combo) {
    var keys = (combo ?? "").Split('+').Select(s => s.Trim()).Where(s => s.Length > 0).Select(Parse).ToList();
    if (keys.Count == 0) throw new ArgumentException(I18n.T("action.no_hotkey"));
    var inputs = new List<INPUT>();
    foreach (var vk in keys) inputs.Add(Key(vk, false));
    for (int i = keys.Count - 1; i >= 0; i--) inputs.Add(Key(keys[i], true));
    Send(inputs.ToArray());
  }

  public static void Tap(ushort vk) { Send(new[] { Key(vk, false), Key(vk, true) }); }

  public static void Type(string text) {
    var inputs = new List<INPUT>();
    foreach (char c in text ?? "") {
      if (c == '\r') continue;
      if (c == '\n') {
        inputs.Add(Key(0x0D, false));
        inputs.Add(Key(0x0D, true));
        continue;
      }
      foreach (bool up in new[] { false, true }) {
        var input = new INPUT { type = INPUT_KEYBOARD };
        input.ki.wScan = c;
        input.ki.dwFlags = KEYEVENTF_UNICODE | (up ? KEYEVENTF_KEYUP : 0);
        inputs.Add(input);
      }
    }
    if (inputs.Count > 0) Send(inputs.ToArray());
  }

  static void Send(INPUT[] inputs) {
    uint sent = SendInput((uint)inputs.Length, inputs, Marshal.SizeOf(typeof(INPUT)));
    if (sent != inputs.Length) {
      throw new InvalidOperationException(I18n.T("action.input_blocked") + ", " +
                                          Marshal.GetLastWin32Error());
    }
  }
}

static class Audio {
  [ComImport, Guid("BCDE0395-E52F-467C-8E3D-C4579291692E")]
  class MMDeviceEnumeratorCom {
  }

  [ComImport, Guid("A95664D2-9614-4F35-A746-DE8DB63617E6"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
  interface IMMDeviceEnumerator {
    int NotImpl1();
    [PreserveSig] int GetDefaultAudioEndpoint(int dataFlow, int role, out IMMDevice device);
  }

  [ComImport, Guid("D666063F-1587-4E43-81F1-B948E807363F"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
  interface IMMDevice {
    [PreserveSig] int Activate(ref Guid iid, int clsCtx, IntPtr activationParams,
                               [MarshalAs(UnmanagedType.IUnknown)] out object iface);
  }

  [ComImport, Guid("5CDF2C82-841E-4546-9722-0CF74078229A"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
  interface IAudioEndpointVolume {
    int RegisterControlChangeNotify(IntPtr notify);
    int UnregisterControlChangeNotify(IntPtr notify);
    int GetChannelCount(out uint count);
    int SetMasterVolumeLevel(float level, ref Guid context);
    int SetMasterVolumeLevelScalar(float level, ref Guid context);
    int GetMasterVolumeLevel(out float level);
    int GetMasterVolumeLevelScalar(out float level);
    int SetChannelVolumeLevel(uint channel, float level, ref Guid context);
    int SetChannelVolumeLevelScalar(uint channel, float level, ref Guid context);
    int GetChannelVolumeLevel(uint channel, out float level);
    int GetChannelVolumeLevelScalar(uint channel, out float level);
    int SetMute([MarshalAs(UnmanagedType.Bool)] bool mute, ref Guid context);
    int GetMute([MarshalAs(UnmanagedType.Bool)] out bool mute);
  }

  const int RENDER = 0, CAPTURE = 1, MULTIMEDIA = 1, CLSCTX_ALL = 23;

  static readonly object gate = new object();
  static IMMDeviceEnumerator enumerator;
  static readonly IAudioEndpointVolume[] cached = new IAudioEndpointVolume[2];
  static readonly DateTime[] cachedAt = new DateTime[2];

  static IAudioEndpointVolume Endpoint(bool mic) {
    lock (gate) {
      int i = mic ? 1 : 0;
      if (cached[i] != null && (DateTime.UtcNow - cachedAt[i]).TotalSeconds < 5) return cached[i];
      if (cached[i] != null) {
        Marshal.ReleaseComObject(cached[i]);
        cached[i] = null;
      }
      if (enumerator == null) enumerator = (IMMDeviceEnumerator)new MMDeviceEnumeratorCom();
      IMMDevice device;
      if (enumerator.GetDefaultAudioEndpoint(mic ? CAPTURE : RENDER, MULTIMEDIA, out device) != 0 || device == null) {
        return null;
      }
      try {
        Guid iid = typeof(IAudioEndpointVolume).GUID;
        object volume;
        if (device.Activate(ref iid, CLSCTX_ALL, IntPtr.Zero, out volume) != 0) return null;
        cached[i] = volume as IAudioEndpointVolume;
        cachedAt[i] = DateTime.UtcNow;
        return cached[i];
      } finally {
        Marshal.ReleaseComObject(device);
      }
    }
  }

  public static bool? Muted(bool mic) {
    try {
      var v = Endpoint(mic);
      if (v == null) return null;
      bool muted;
      v.GetMute(out muted);
      return muted;
    } catch (Exception) {
      return null;
    }
  }

  public static int? Percent() {
    try {
      var v = Endpoint(false);
      if (v == null) return null;
      float level;
      v.GetMasterVolumeLevelScalar(out level);
      return (int)Math.Round(level * 100);
    } catch (Exception) {
      return null;
    }
  }

  public static void ToggleMute(bool mic) {
    var v = Endpoint(mic);
    if (v == null) throw new InvalidOperationException(I18n.T(mic ? "action.no_mic" : "action.no_speaker"));
    bool muted;
    v.GetMute(out muted);
    Guid context = Guid.Empty;
    v.SetMute(!muted, ref context);
  }

  public static void Step(int delta) {
    var v = Endpoint(false);
    if (v == null) throw new InvalidOperationException(I18n.T("action.no_speaker"));
    float level;
    v.GetMasterVolumeLevelScalar(out level);
    Guid context = Guid.Empty;
    float next = Math.Max(0, Math.Min(1, (float)Math.Round(level * 50 + delta) / 50f));
    v.SetMasterVolumeLevelScalar(next, ref context);
  }
}

static class AdminTasks {
  const string FOLDER = "CydDeck";
  static string RecordPath { get { return Path.Combine(DeckConfig.Dir, "admin-tasks.txt"); } }

  public static string NameOf(DeckAction a) {
    string dir = string.IsNullOrWhiteSpace(a.Dir) ? "<home>" : a.Dir;
    return Hash((a.Path ?? "") + "\n" + (a.Args ?? "") + "\n" + dir);
  }

  static string LegacyNameOf(DeckAction a) { return Hash((a.Path ?? "") + "\n" + (a.Args ?? "") + "\n" + (a.Dir ?? "")); }

  static string Hash(string spec) {
    return "k-" + Crc32.Update(0, Encoding.UTF8.GetBytes(spec.ToLowerInvariant())).ToString("x8");
  }

  static HashSet<string> Registered() {
    try {
      if (File.Exists(RecordPath)) return new HashSet<string>(File.ReadAllLines(RecordPath).Where(l => l.Length > 0));
    } catch (Exception) {
    }
    return new HashSet<string>();
  }

  public static bool IsRegistered(DeckAction a) { return Registered().Contains(NameOf(a)); }

  static string Xml(string user, string sid, DeckAction a) {
    Func<string, string> esc = s => System.Security.SecurityElement.Escape(s ?? "");
    var sb = new StringBuilder();
    sb.AppendLine("<?xml version=\"1.0\" encoding=\"UTF-16\"?>");
    sb.AppendLine("<Task version=\"1.2\" xmlns=\"http://schemas.microsoft.com/windows/2004/02/mit/task\">");
    sb.AppendLine("  <RegistrationInfo><Author>CYD Deck</Author><Description>" + esc("Phím CYD Deck: " + a.Path + " " + a.Args) +
                  "</Description><SecurityDescriptor>D:(A;;FA;;;SY)(A;;FA;;;BA)(A;;GRGX;;;" + sid +
                  ")</SecurityDescriptor></RegistrationInfo>");
    sb.AppendLine("  <Principals><Principal id=\"Author\"><UserId>" + esc(user) +
                  "</UserId><LogonType>InteractiveToken</LogonType><RunLevel>HighestAvailable</RunLevel></Principal></Principals>");
    sb.AppendLine("  <Settings><MultipleInstancesPolicy>Parallel</MultipleInstancesPolicy>" +
                  "<DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries><StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>" +
                  "<ExecutionTimeLimit>PT0S</ExecutionTimeLimit><Priority>4</Priority><Enabled>true</Enabled></Settings>");
    sb.Append("  <Actions Context=\"Author\"><Exec><Command>" + esc(a.Path) + "</Command>");
    if (!string.IsNullOrWhiteSpace(a.Args)) sb.Append("<Arguments>" + esc(a.Args) + "</Arguments>");
    sb.Append("<WorkingDirectory>" + esc(DeckActions.WorkingDir(a)) + "</WorkingDirectory>");
    sb.AppendLine("</Exec></Actions>");
    sb.AppendLine("</Task>");
    return sb.ToString();
  }

  public static string Sync(IEnumerable<DeckAction> wanted) {
    var actions = wanted.GroupBy(NameOf).Select(g => g.First()).ToList();
    var names = new HashSet<string>(actions.Select(NameOf));
    var registered = Registered();
    if (names.SetEquals(registered)) return names.Count == 0 ? "" : I18n.T("admin.already");

    string work = Path.Combine(DeckConfig.Dir, "admin-tasks");
    if (Directory.Exists(work)) Directory.Delete(work, true);
    Directory.CreateDirectory(work);
    var identity = WindowsIdentity.GetCurrent();
    var script = new StringBuilder();
    script.AppendLine("@echo off");
    script.AppendLine("chcp 65001 >nul");
    foreach (string old in registered.Where(n => !names.Contains(n))) {
      script.AppendLine("schtasks /Delete /TN \"\\" + FOLDER + "\\" + old + "\" /F >nul 2>&1");
    }
    int index = 0;
    foreach (var a in actions) {
      string xmlPath = Path.Combine(work, "t" + index++ + ".xml");
      File.WriteAllText(xmlPath, Xml(identity.Name, identity.User.Value, a), Encoding.Unicode);
      script.AppendLine("schtasks /Create /TN \"\\" + FOLDER + "\\" + NameOf(a) + "\" /XML \"" + xmlPath + "\" /F >nul || exit /b 1");
    }
    script.AppendLine("exit /b 0");
    string batch = Path.Combine(work, "dang-ky.cmd");
    File.WriteAllText(batch, script.ToString(), new UTF8Encoding(false));
    try {
      var info = new ProcessStartInfo("cmd.exe", "/c \"" + batch + "\"") {
        Verb = "runas", UseShellExecute = true, WindowStyle = ProcessWindowStyle.Hidden,
      };
      using (var p = Process.Start(info)) {
        p.WaitForExit(60000);
        if (!p.HasExited || p.ExitCode != 0) {
          Log.Agent("Đăng ký tác vụ admin lỗi, mã " + (p.HasExited ? p.ExitCode.ToString() : "quá giờ"));
          return I18n.T("admin.failed");
        }
      }
    } catch (System.ComponentModel.Win32Exception) {
      return I18n.T("admin.declined");
    } finally {
      try { Directory.Delete(work, true); } catch (Exception) { }
    }
    File.WriteAllLines(RecordPath, names.ToArray());
    Log.Agent("Đã đăng ký " + names.Count + " lệnh admin trong Task Scheduler \\" + FOLDER);
    return string.Format(I18n.T("admin.done"), names.Count);
  }

  public static bool Run(DeckAction a) {
    var registered = Registered();
    string name = registered.Contains(NameOf(a)) ? NameOf(a) : registered.Contains(LegacyNameOf(a)) ? LegacyNameOf(a) : null;
    if (name == null) return false;
    var info = new ProcessStartInfo("schtasks.exe", "/Run /TN \"\\" + FOLDER + "\\" + name + "\"") {
      UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true,
    };
    using (var p = Process.Start(info)) {
      p.WaitForExit(10000);
      if (p.HasExited && p.ExitCode == 0) return true;
      Log.Agent("schtasks /Run " + name + " lỗi: " + p.StandardError.ReadToEnd().Trim());
      return false;
    }
  }
}

static class DeckActions {
  public static void Run(DeckAction a) {
    if (a == null || a.Type == null || a.Type == "none") throw new InvalidOperationException(I18n.T("action.none"));
    switch (a.Type) {
      case "run": RunProgram(a); break;
      case "open":
        if (string.IsNullOrWhiteSpace(a.Target)) throw new InvalidOperationException(I18n.T("action.no_target"));
        Process.Start(new ProcessStartInfo(a.Target) { UseShellExecute = true });
        break;
      case "hotkey": Input.Hotkey(a.Keys); break;
      case "text": Input.Type(a.Text); break;
      case "media":
        switch (a.Cmd) {
          case "next": Input.Tap(0xB0); break;
          case "prev": Input.Tap(0xB1); break;
          case "stop": Input.Tap(0xB2); break;
          default: Input.Tap(0xB3); break;
        }
        break;
      case "volume":
        if (a.Cmd == "up") Audio.Step(1);
        else if (a.Cmd == "down") Audio.Step(-1);
        else Audio.ToggleMute(false);
        break;
      case "mic": Audio.ToggleMute(true); break;
      case "lock":
        if (!Input.LockWorkStation()) throw new InvalidOperationException(I18n.T("action.lock_failed"));
        break;
      default: throw new InvalidOperationException(string.Format(I18n.T("action.unknown"), a.Type));
    }
  }

  public static string WorkingDir(DeckAction a) {
    if (!string.IsNullOrWhiteSpace(a.Dir)) {
      string dir = Environment.ExpandEnvironmentVariables(a.Dir.Trim());
      if (Directory.Exists(dir)) return dir;
    }
    return Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
  }

  static void RunProgram(DeckAction a) {
    if (string.IsNullOrWhiteSpace(a.Path)) throw new InvalidOperationException(I18n.T("action.no_program"));
    if (a.Admin && AdminTasks.Run(a)) return;
    var info = new ProcessStartInfo(Environment.ExpandEnvironmentVariables(a.Path)) {
      Arguments = a.Args ?? "", UseShellExecute = true, WorkingDirectory = WorkingDir(a),
    };
    if (a.Admin) info.Verb = "runas";
    try {
      Process.Start(info);
    } catch (System.ComponentModel.Win32Exception ex) {
      if (ex.NativeErrorCode == 1223) throw new InvalidOperationException(I18n.T("action.uac_denied"));
      throw new InvalidOperationException(string.Format(I18n.T("action.cannot_run"), Path.GetFileName(a.Path), ex.Message));
    }
  }

  public static bool? StateOf(DeckAction a) {
    if (a == null) return null;
    if (a.Type == "mic") return Audio.Muted(true);
    if (a.Type == "volume" && a.Cmd == "mute") return Audio.Muted(false);
    return null;
  }
}

}
