using System;
using System.IO;
using System.Windows.Forms;
using Microsoft.Win32;

namespace CydAgent {

static class Autostart {
  const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
  const string ApprovedKey = @"Software\Microsoft\Windows\CurrentVersion\Explorer\StartupApproved\Run";
  const string ValueName = "CYD Deck";

  static string Command { get { return "\"" + Application.ExecutablePath + "\""; } }

  static string LegacyShortcut {
    get { return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Startup), "CYD PC Agent.lnk"); }
  }

  public static bool Enabled {
    get {
      try {
        using (var run = Registry.CurrentUser.OpenSubKey(RunKey)) {
          var value = run == null ? null : run.GetValue(ValueName) as string;
          if (value == null || !string.Equals(value.Trim(), Command, StringComparison.OrdinalIgnoreCase)) return false;
        }
        using (var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey)) {
          var state = approved == null ? null : approved.GetValue(ValueName) as byte[];
          return state == null || state.Length == 0 || (state[0] & 1) == 0;
        }
      } catch (Exception) {
        return false;
      }
    }
  }

  public static void Set(bool on) {
    using (var run = Registry.CurrentUser.CreateSubKey(RunKey)) {
      if (on) run.SetValue(ValueName, Command);
      else run.DeleteValue(ValueName, false);
    }
    using (var approved = Registry.CurrentUser.OpenSubKey(ApprovedKey, true)) {
      if (approved != null) approved.DeleteValue(ValueName, false);
    }
    RemoveLegacy();
    Log.Agent(on ? "Bật khởi động cùng Windows" : "Tắt khởi động cùng Windows");
  }

  public static void MigrateLegacy() {
    if (!File.Exists(LegacyShortcut)) return;
    try {
      Set(true);
      Log.Agent("Chuyển lối tắt Startup cũ sang mục Run của Windows");
    } catch (Exception ex) {
      Log.Agent("Không chuyển được lối tắt Startup cũ: " + ex.Message);
    }
  }

  static void RemoveLegacy() {
    try {
      if (File.Exists(LegacyShortcut)) File.Delete(LegacyShortcut);
    } catch (Exception) {
    }
  }
}

}
