// CYD PC Agent: chương trình ở khay hệ thống Windows, nói chuyện với bo CYD qua cổng COM của cáp USB. Ban đầu viết cho
// trang Máy tính của firmware CYD-EEZ (số liệu như Task Manager: gói STATS, PROCS), nay phục vụ thêm firmware CYD Deck.
// Không qua mạng, không cần cài gì thêm: dịch bằng csc.exe có sẵn trong .NET Framework 4 của Windows
// (build.cmd), nên mã giữ ở cú pháp C# 5.
//
// Cách lấy số liệu tham khảo agent của PhoneBox (github.com/ersinkoc/phonebox, MIT) và Task Manager:
//   CPU       PDH "% Processor Utility" (đúng con số Task Manager hiện), tốc độ từ "% Processor Performance"
//   RAM       GlobalMemoryStatusEx
//   GPU       NVML của driver NVIDIA (tải, nhiệt độ, VRAM); máy không có NVIDIA thì PDH "GPU Engine"
//   Mạng      thống kê byte của các card mạng thật (Ethernet, Wi-Fi), bỏ card ảo
//   Tiến trình NtQuerySystemInformation: một lần gọi lấy đủ mọi tiến trình (thời gian CPU, bộ nhớ riêng), không phải mở
//             từng tiến trình như System.Diagnostics.Process; gom theo tên như thẻ Processes của Task Manager
//
// Giao thức (PC → bo, nhị phân, little-endian): A5 5A | loại 1 byte | độ dài 2 byte | dữ liệu | CRC-8 (đa thức 0x07)
// tính trên loại + độ dài + dữ liệu. Chi tiết từng loại ở WriteHello/WriteStats/WriteProcs, bên bo ở src/app/pc_link.cpp.
// Bo → PC là các dòng log chữ; dòng "[PC] ... xem=1 sx=0" cho biết trang Máy tính đang mở và cách sắp xếp tiến trình,
// nên danh sách tiến trình chỉ được đo và gửi khi có người xem.
//
// Từ 18/11/2026 agent còn phục vụ firmware CYD Deck (..\..\firmware): bo trả lời bằng dòng "[DECK] ..." thì agent
// chuyển sang chế độ Deck (DeckSync.cs: gửi bộ phím, ảnh, thanh tin; chạy lệnh khi bấm phím) và có trang cài đặt phím
// (DeckWeb.cs, web\index.html). Bo chạy CYD-EEZ thì vẫn như cũ.
//
// Cổng COM chỉ một chương trình mở được: tools/cyd.ps1 gửi "pause" tới 127.0.0.1:47830 trước khi nạp hay đọc log, rồi
// "resume" khi xong. Menu ở khay hệ thống cũng có mục tạm nhả cổng.
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.IO;
using System.IO.Ports;
using System.Linq;
using System.Management;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Windows.Forms;
using Microsoft.Win32;

[assembly: System.Reflection.AssemblyTitle("CYD PC Agent")]
[assembly: System.Reflection.AssemblyProduct("CYD-EEZ, CYD Deck")]
[assembly: System.Reflection.AssemblyVersion("1.1.0.0")]

namespace CydAgent {

static class Native {
  [StructLayout(LayoutKind.Sequential)]
  public struct MEMORYSTATUSEX {
    public uint dwLength, dwMemoryLoad;
    public ulong ullTotalPhys, ullAvailPhys, ullTotalPageFile, ullAvailPageFile, ullTotalVirtual, ullAvailVirtual,
        ullAvailExtendedVirtual;
  }

  [DllImport("kernel32.dll", SetLastError = true)]
  public static extern bool GlobalMemoryStatusEx(ref MEMORYSTATUSEX m);
  [DllImport("kernel32.dll")]
  public static extern bool GetSystemTimes(out long idle, out long kernel, out long user);
  [DllImport("kernel32.dll")]
  public static extern ulong GetTickCount64();
  [DllImport("kernel32.dll", SetLastError = true)]
  public static extern IntPtr OpenProcess(uint access, bool inherit, uint pid);
  [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
  public static extern bool QueryFullProcessImageNameW(IntPtr process, uint flags, StringBuilder name, ref uint size);
  [DllImport("kernel32.dll")]
  public static extern bool CloseHandle(IntPtr handle);
  [DllImport("ntdll.dll")]
  public static extern int NtQuerySystemInformation(int infoClass, IntPtr buffer, int length, out int needed);
  [DllImport("user32.dll")]
  public static extern bool DestroyIcon(IntPtr icon);

  [StructLayout(LayoutKind.Sequential)]
  public struct PDH_FMT_COUNTERVALUE {
    public uint CStatus;
    public uint pad;
    public double doubleValue;
  }
  public const uint PDH_FMT_DOUBLE = 0x200, PDH_FMT_NOCAP100 = 0x8000, PDH_MORE_DATA = 0x800007D2;
  [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
  public static extern uint PdhOpenQueryW(string source, IntPtr user, out IntPtr query);
  [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
  public static extern uint PdhAddEnglishCounterW(IntPtr query, string path, IntPtr user, out IntPtr counter);
  [DllImport("pdh.dll")]
  public static extern uint PdhCollectQueryData(IntPtr query);
  [DllImport("pdh.dll")]
  public static extern uint PdhGetFormattedCounterValue(IntPtr counter, uint format, IntPtr type,
                                                        out PDH_FMT_COUNTERVALUE value);
  [DllImport("pdh.dll", CharSet = CharSet.Unicode)]
  public static extern uint PdhGetFormattedCounterArrayW(IntPtr counter, uint format, ref uint size, ref uint count,
                                                         IntPtr buffer);
}

// ---------------------------------------------------------------------------------------------------------------
// Bộ đếm hiệu năng (PDH). Dùng tên tiếng Anh (PdhAddEnglishCounter): tên hiện trong Performance Monitor đổi theo ngôn
// ngữ Windows. Bộ đếm dạng "tốc độ" cần hai lần thu mới có số, nên lần đầu trả về null.
// ---------------------------------------------------------------------------------------------------------------
sealed class Pdh {
  IntPtr query = IntPtr.Zero;

  public Pdh() {
    if (Native.PdhOpenQueryW(null, IntPtr.Zero, out query) != 0) query = IntPtr.Zero;
  }

  public IntPtr Add(string path) {
    if (query == IntPtr.Zero) return IntPtr.Zero;
    IntPtr counter;
    return Native.PdhAddEnglishCounterW(query, path, IntPtr.Zero, out counter) == 0 ? counter : IntPtr.Zero;
  }

  public void Collect() {
    if (query != IntPtr.Zero) Native.PdhCollectQueryData(query);
  }

  public static double? Value(IntPtr counter, bool cap) {
    if (counter == IntPtr.Zero) return null;
    Native.PDH_FMT_COUNTERVALUE v;
    uint format = Native.PDH_FMT_DOUBLE | (cap ? 0 : Native.PDH_FMT_NOCAP100);
    if (Native.PdhGetFormattedCounterValue(counter, format, IntPtr.Zero, out v) != 0 || v.CStatus > 1) return null;
    return v.doubleValue;
  }

  // Bộ đếm có dấu *: mọi thể hiện (tên, giá trị)
  public static List<KeyValuePair<string, double>> Array(IntPtr counter) {
    var list = new List<KeyValuePair<string, double>>();
    if (counter == IntPtr.Zero) return list;
    uint size = 0, count = 0;
    uint format = Native.PDH_FMT_DOUBLE | Native.PDH_FMT_NOCAP100;
    if (Native.PdhGetFormattedCounterArrayW(counter, format, ref size, ref count, IntPtr.Zero) != Native.PDH_MORE_DATA ||
        size == 0) {
      return list;
    }
    IntPtr buffer = Marshal.AllocHGlobal((int)size);
    try {
      if (Native.PdhGetFormattedCounterArrayW(counter, format, ref size, ref count, buffer) != 0) return list;
      int itemSize = IntPtr.Size + 16;  // PDH_FMT_COUNTERVALUE_ITEM_W: tên (con trỏ), rồi PDH_FMT_COUNTERVALUE
      for (int i = 0; i < count; i++) {
        IntPtr item = buffer + i * itemSize;
        string name = Marshal.PtrToStringUni(Marshal.ReadIntPtr(item));
        uint status = (uint)Marshal.ReadInt32(item, IntPtr.Size);
        if (status > 1) continue;
        double value = BitConverter.Int64BitsToDouble(Marshal.ReadInt64(item, IntPtr.Size + 8));
        list.Add(new KeyValuePair<string, double>(name, value));
      }
    } finally {
      Marshal.FreeHGlobal(buffer);
    }
    return list;
  }
}

// ---------------------------------------------------------------------------------------------------------------
// NVML: thư viện quản lý GPU đi kèm driver NVIDIA (C:\Windows\System32\nvml.dll). Rẻ hơn hẳn gọi nvidia-smi mỗi giây.
// ---------------------------------------------------------------------------------------------------------------
static class Nvml {
  [StructLayout(LayoutKind.Sequential)]
  struct Utilization {
    public uint gpu, memory;
  }
  [StructLayout(LayoutKind.Sequential)]
  struct Memory {
    public ulong total, free, used;
  }
  // Bản 2 tách phần driver giữ riêng ra khỏi "used", khớp với nvidia-smi và Task Manager
  [StructLayout(LayoutKind.Sequential)]
  struct Memory2 {
    public uint version;
    public ulong total, reserved, free, used;
  }
  [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetMemoryInfo_v2")]
  static extern int GetMemory2(IntPtr device, ref Memory2 m);
  [DllImport("nvml.dll", EntryPoint = "nvmlInit_v2")]
  static extern int Init();
  [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetHandleByIndex_v2")]
  static extern int GetHandle(uint index, out IntPtr device);
  [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetUtilizationRates")]
  static extern int GetUtilization(IntPtr device, out Utilization u);
  [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetMemoryInfo")]
  static extern int GetMemory(IntPtr device, out Memory m);
  [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetTemperature")]
  static extern int GetTemperature(IntPtr device, int sensor, out uint temp);
  [DllImport("nvml.dll", EntryPoint = "nvmlDeviceGetName")]
  static extern int GetName(IntPtr device, StringBuilder name, uint length);

  static IntPtr device = IntPtr.Zero;
  static bool tried, memoryV2 = true;
  public static string Name = "";

  public static bool Ready {
    get {
      if (!tried) {
        tried = true;
        try {
          if (Init() == 0 && GetHandle(0, out device) == 0) {
            var sb = new StringBuilder(96);
            if (GetName(device, sb, 96) == 0) Name = sb.ToString();
          } else {
            device = IntPtr.Zero;
          }
        } catch (Exception) {  // máy không có NVIDIA: không có nvml.dll
          device = IntPtr.Zero;
        }
      }
      return device != IntPtr.Zero;
    }
  }

  public static bool Read(out uint util, out uint temp, out ulong usedMb, out ulong totalMb) {
    util = 0;
    temp = 0;
    usedMb = totalMb = 0;
    if (!Ready) return false;
    Utilization u;
    if (GetUtilization(device, out u) != 0) return false;
    util = u.gpu;
    if (GetTemperature(device, 0, out temp) != 0) temp = 0;
    if (memoryV2) {
      var m2 = new Memory2 { version = (uint)Marshal.SizeOf(typeof(Memory2)) | (2u << 24) };
      try {
        if (GetMemory2(device, ref m2) == 0) {
          usedMb = m2.used >> 20;
          totalMb = m2.total >> 20;
          return true;
        }
      } catch (EntryPointNotFoundException) {  // driver cũ
      }
      memoryV2 = false;
    }
    Memory m;
    if (GetMemory(device, out m) == 0) {
      usedMb = m.used >> 20;
      totalMb = m.total >> 20;
    }
    return true;
  }
}

// ---------------------------------------------------------------------------------------------------------------
// Tiến trình
// ---------------------------------------------------------------------------------------------------------------
sealed class ProcGroup {
  public string Name;
  public double Cpu;   // %
  public ulong Mem;    // byte, bộ nhớ riêng đang dùng (cột Memory của Task Manager)
  public int Count;
}

sealed class ProcSampler {
  sealed class Entry {
    public long Create, Time;  // 100 ns
    public string Key;
    public bool Seen;
  }

  const int SystemProcessInformation = 5;
  const int STATUS_INFO_LENGTH_MISMATCH = unchecked((int)0xC0000004);
  IntPtr buffer = IntPtr.Zero;
  int bufferSize = 0;
  readonly Dictionary<long, Entry> entries = new Dictionary<long, Entry>();
  readonly Dictionary<string, string> titles = new Dictionary<string, string>();  // tên file → tên hiển thị
  long lastTicks;
  public int Count;  // số tiến trình lần đo gần nhất

  bool Query() {
    if (buffer == IntPtr.Zero) {
      bufferSize = 1 << 20;
      buffer = Marshal.AllocHGlobal(bufferSize);
    }
    for (int attempt = 0; attempt < 4; attempt++) {
      int needed;
      int status = Native.NtQuerySystemInformation(SystemProcessInformation, buffer, bufferSize, out needed);
      if (status == 0) return true;
      if (status != STATUS_INFO_LENGTH_MISMATCH) return false;
      Marshal.FreeHGlobal(buffer);
      bufferSize = Math.Max(needed + (64 << 10), bufferSize * 2);
      buffer = Marshal.AllocHGlobal(bufferSize);
    }
    return false;
  }

  // Tên như Task Manager: phần "Mô tả" của file chạy (Google Chrome, Visual Studio Code...), không có thì tên file.
  // Đọc một lần cho mỗi tên file rồi nhớ.
  // Tiến trình của hệ thống (svchost chạy bằng tài khoản SYSTEM...) có cái không mở được: thử lại với tiến trình khác
  // cùng tên, tối đa vài lần, rồi mới thôi.
  readonly Dictionary<string, int> titleTries = new Dictionary<string, int>();
  const int TITLE_TRIES = 12;

  string Title(string key, string image, long pid) {
    string title;
    int tries;
    titleTries.TryGetValue(key, out tries);
    if (titles.TryGetValue(key, out title) && tries < 0) return title;
    if (title == null) {
      title = image.EndsWith(".exe", StringComparison.OrdinalIgnoreCase) ? image.Substring(0, image.Length - 4) : image;
    }
    bool done = tries >= TITLE_TRIES;
    IntPtr handle = done ? IntPtr.Zero : Native.OpenProcess(0x1000 /* PROCESS_QUERY_LIMITED_INFORMATION */, false, (uint)pid);
    if (handle != IntPtr.Zero) {
      done = true;
      try {
        var path = new StringBuilder(1024);
        uint size = (uint)path.Capacity;
        if (Native.QueryFullProcessImageNameW(handle, 0, path, ref size)) {
          string description = FileVersionInfo.GetVersionInfo(path.ToString()).FileDescription;
          if (!string.IsNullOrWhiteSpace(description) && description.Trim().Length <= 48) title = description.Trim();
        }
      } catch (Exception) {
      } finally {
        Native.CloseHandle(handle);
      }
    }
    titles[key] = title;
    titleTries[key] = done ? -1 : tries + 1;
    return title;
  }

  // Đo một lượt. scale: tỉ lệ đưa phần trăm theo thời gian CPU về thang "% Processor Utility" của Task Manager.
  public List<ProcGroup> Sample(double scale) {
    var groups = new Dictionary<string, ProcGroup>();
    if (!Query()) return new List<ProcGroup>();
    long now = Stopwatch.GetTimestamp();
    double elapsed = lastTicks == 0 ? 0 : (now - lastTicks) * 1e7 / Stopwatch.Frequency;  // 100 ns
    lastTicks = now;
    double capacity = elapsed * Environment.ProcessorCount;
    foreach (var e in entries.Values) e.Seen = false;

    int offset = 0, count = 0;
    while (true) {
      IntPtr p = buffer + offset;
      int next = Marshal.ReadInt32(p, 0);
      long pid = Marshal.ReadIntPtr(p, 80).ToInt64();
      if (pid != 0) {  // bỏ "System Idle Process"
        count++;
        long create = Marshal.ReadInt64(p, 32);
        long time = Marshal.ReadInt64(p, 40) + Marshal.ReadInt64(p, 48);  // thời gian người dùng + nhân
        ulong privateWs = (ulong)Marshal.ReadInt64(p, 8);
        Entry e;
        double cpu = 0;
        if (entries.TryGetValue(pid, out e) && e.Create == create) {
          if (capacity > 0) cpu = Math.Max(0, time - e.Time) * 100.0 / capacity * scale;
        } else {
          int length = Marshal.ReadInt16(p, 56);
          IntPtr name = Marshal.ReadIntPtr(p, 64);
          string image = length > 0 && name != IntPtr.Zero ? Marshal.PtrToStringUni(name, length / 2) : "System";
          e = new Entry { Create = create, Key = image.ToLowerInvariant() };
          entries[pid] = e;
          int tries;
          if (!titleTries.TryGetValue(e.Key, out tries) || (tries >= 0 && tries < TITLE_TRIES)) Title(e.Key, image, pid);
        }
        e.Time = time;
        e.Seen = true;
        ProcGroup g;
        if (!groups.TryGetValue(e.Key, out g)) {
          g = new ProcGroup { Name = titles[e.Key] };
          groups[e.Key] = g;
        }
        g.Cpu += cpu;
        g.Mem += privateWs;
        g.Count++;
      }
      if (next == 0) break;
      offset += next;
    }
    Count = count;
    foreach (long dead in entries.Where(pair => !pair.Value.Seen).Select(pair => pair.Key).ToList()) entries.Remove(dead);
    return groups.Values.ToList();
  }
}

// ---------------------------------------------------------------------------------------------------------------
// Mạng: tổng byte nhận/gửi của các card mạng thật đang chạy. Card ảo (Hyper-V, WSL, VPN...) bị bỏ vì lưu lượng của
// chúng đi qua card thật, cộng vào sẽ đếm hai lần.
// ---------------------------------------------------------------------------------------------------------------
sealed class NetSampler {
  NetworkInterface[] adapters = new NetworkInterface[0];
  DateTime listedAt = DateTime.MinValue;
  long lastIn = -1, lastOut = -1;
  long lastTicks;

  static readonly string[] VIRTUAL = {"virtual", "hyper-v", "vethernet", "vmware", "virtualbox", "tap-", "tun",
                                      "wan miniport", "loopback", "bluetooth", "vpn", "wireguard", "tailscale",
                                      "zerotier", "pseudo"};

  void List() {
    try {
      adapters = NetworkInterface.GetAllNetworkInterfaces()
                     .Where(ni => ni.OperationalStatus == OperationalStatus.Up &&
                                  (ni.NetworkInterfaceType == NetworkInterfaceType.Ethernet ||
                                   ni.NetworkInterfaceType == NetworkInterfaceType.Wireless80211 ||
                                   ni.NetworkInterfaceType == NetworkInterfaceType.GigabitEthernet) &&
                                  !VIRTUAL.Any(v => (ni.Description + " " + ni.Name).ToLowerInvariant().Contains(v)))
                     .ToArray();
    } catch (Exception) {
      adapters = new NetworkInterface[0];
    }
    listedAt = DateTime.UtcNow;
    lastIn = lastOut = -1;  // danh sách đổi: lượt này chưa có số
  }

  public void Sample(out uint down, out uint up) {
    down = up = 0;
    if ((DateTime.UtcNow - listedAt).TotalSeconds > 30) List();
    long totalIn = 0, totalOut = 0;
    foreach (var ni in adapters) {
      try {
        var s = ni.GetIPStatistics();
        totalIn += s.BytesReceived;
        totalOut += s.BytesSent;
      } catch (Exception) {
      }
    }
    long now = Stopwatch.GetTimestamp();
    double seconds = (now - lastTicks) / (double)Stopwatch.Frequency;
    if (lastIn >= 0 && seconds > 0.2) {
      down = (uint)Math.Max(0, Math.Min(uint.MaxValue, (totalIn - lastIn) / seconds));
      up = (uint)Math.Max(0, Math.Min(uint.MaxValue, (totalOut - lastOut) / seconds));
    }
    lastIn = totalIn;
    lastOut = totalOut;
    lastTicks = now;
  }
}

// ---------------------------------------------------------------------------------------------------------------
// Số liệu mỗi giây
// ---------------------------------------------------------------------------------------------------------------
sealed class Stats {
  public double Cpu;          // %
  public int CpuMhz;          // 0: không đọc được
  public double Gpu = -1;     // %, -1: không có
  public int GpuTemp = -1;    // °C
  public ulong RamUsedMb, RamTotalMb, VramUsedMb, VramTotalMb;
  public uint Down, Up;       // byte/giây
  public ulong UptimeS;
  public int Procs;
}

sealed class Sampler {
  readonly Pdh pdh = new Pdh();
  readonly IntPtr cpuUtility, cpuPerformance, cpuFrequency, gpuEngine;
  long lastIdle = -1, lastKernel, lastUser;
  public readonly ProcSampler Procs = new ProcSampler();
  readonly NetSampler net = new NetSampler();
  double timeCpu;  // % CPU tính theo thời gian (GetSystemTimes), để quy đổi phần trăm của từng tiến trình

  public Sampler() {
    cpuUtility = pdh.Add(@"\Processor Information(_Total)\% Processor Utility");
    cpuPerformance = pdh.Add(@"\Processor Information(_Total)\% Processor Performance");
    cpuFrequency = pdh.Add(@"\Processor Information(_Total)\Processor Frequency");
    if (!Nvml.Ready) gpuEngine = pdh.Add(@"\GPU Engine(*)\Utilization Percentage");
    pdh.Collect();
    SystemTimes();
  }

  double? SystemTimes() {
    long idle, kernel, user;
    if (!Native.GetSystemTimes(out idle, out kernel, out user)) return null;
    double? result = null;
    if (lastIdle >= 0) {
      long total = (kernel - lastKernel) + (user - lastUser);  // thời gian nhân đã gồm thời gian rảnh
      if (total > 0) result = Math.Max(0, Math.Min(100, (total - (idle - lastIdle)) * 100.0 / total));
    }
    lastIdle = idle;
    lastKernel = kernel;
    lastUser = user;
    return result;
  }

  // Tải GPU như Task Manager khi không có NVML: cộng mọi tiến trình theo từng engine, lấy engine bận nhất
  double? GpuFromEngines() {
    var engines = new Dictionary<string, double>();
    foreach (var item in Pdh.Array(gpuEngine)) {
      int at = item.Key.IndexOf("_luid_", StringComparison.Ordinal);
      string key = at >= 0 ? item.Key.Substring(at) : item.Key;
      double sum;
      engines.TryGetValue(key, out sum);
      engines[key] = sum + item.Value;
    }
    if (engines.Count == 0) return null;
    return Math.Min(100, engines.Values.Max());
  }

  // light: chỉ CPU và RAM (bo Deck không hiện GPU, mạng, tốc độ CPU), đỡ gọi NVML/PDH GPU mỗi giây
  public Stats Sample(bool light = false) {
    pdh.Collect();
    var s = new Stats();
    double? byTime = SystemTimes();
    double? utility = Pdh.Value(cpuUtility, true);
    timeCpu = byTime ?? 0;
    s.Cpu = utility ?? byTime ?? 0;
    double? performance = Pdh.Value(cpuPerformance, false), frequency = Pdh.Value(cpuFrequency, false);
    if (performance.HasValue && frequency.HasValue) s.CpuMhz = (int)Math.Round(performance.Value * frequency.Value / 100);

    var mem = new Native.MEMORYSTATUSEX();
    mem.dwLength = (uint)Marshal.SizeOf(typeof(Native.MEMORYSTATUSEX));
    if (Native.GlobalMemoryStatusEx(ref mem)) {
      s.RamTotalMb = mem.ullTotalPhys >> 20;
      s.RamUsedMb = (mem.ullTotalPhys - mem.ullAvailPhys) >> 20;
    }
    if (light) return s;

    uint util, temp;
    ulong used, total;
    if (Nvml.Read(out util, out temp, out used, out total)) {
      s.Gpu = util;
      s.GpuTemp = temp > 0 ? (int)temp : -1;
      s.VramUsedMb = used;
      s.VramTotalMb = total;
    } else {
      double? gpu = GpuFromEngines();
      s.Gpu = gpu ?? -1;
    }

    net.Sample(out s.Down, out s.Up);
    s.UptimeS = Native.GetTickCount64() / 1000;
    s.Procs = Procs.Count;
    return s;
  }

  // Tỉ lệ đưa % CPU theo thời gian về % Processor Utility: CPU chạy turbo thì Task Manager hiện số cao hơn thời gian thật
  public double ProcScale(Stats s) {
    if (timeCpu < 1 || s.Cpu <= 0) return 1;
    return Math.Max(0.5, Math.Min(2.5, s.Cpu / timeCpu));
  }
}

// ---------------------------------------------------------------------------------------------------------------
// Gói tin gửi xuống bo
// ---------------------------------------------------------------------------------------------------------------
static class Frames {
  public const byte HELLO = 0x01, STATS = 0x02, PROCS = 0x03;
  public const int PROC_ROWS = 8;  // bằng PC_PROC_ROWS bên bo
  public const int NAME_BYTES = 47;

  static byte Crc8(byte[] data, int start, int length) {
    byte crc = 0;
    for (int i = start; i < start + length; i++) {
      crc ^= data[i];
      for (int bit = 0; bit < 8; bit++) crc = (byte)((crc & 0x80) != 0 ? (crc << 1) ^ 0x07 : crc << 1);
    }
    return crc;
  }

  public static byte[] Frame(byte type, byte[] payload) {
    var frame = new byte[payload.Length + 6];
    frame[0] = 0xA5;
    frame[1] = 0x5A;
    frame[2] = type;
    frame[3] = (byte)(payload.Length & 0xFF);
    frame[4] = (byte)(payload.Length >> 8);
    Buffer.BlockCopy(payload, 0, frame, 5, payload.Length);
    frame[frame.Length - 1] = Crc8(frame, 2, payload.Length + 3);
    return frame;
  }

  // Chuỗi UTF-8 cắt gọn tới `max` byte, không cắt giữa một ký tự
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

  // HELLO: phiên bản giao thức (1), tên máy
  public static byte[] Hello() {
    using (var ms = new MemoryStream())
    using (var w = new BinaryWriter(ms)) {
      w.Write((byte)1);
      Text(w, Environment.MachineName, 31);
      w.Flush();
      return Frame(HELLO, ms.ToArray());
    }
  }

  // STATS (41 byte): CPU ×10, MHz, GPU ×10 (0xFFFF: không có), nhiệt GPU (0xFF: không có), RAM dùng/tổng (MB),
  // VRAM dùng/tổng (MB, tổng 0: không có), tải về/tải lên (byte/giây), thời gian bật máy (giây), giờ UTC (Unix), số
  // tiến trình
  public static byte[] Stats(Stats s) {
    using (var ms = new MemoryStream())
    using (var w = new BinaryWriter(ms)) {
      w.Write((ushort)Math.Round(Math.Max(0, Math.Min(100, s.Cpu)) * 10));
      w.Write((ushort)Math.Max(0, Math.Min(65535, s.CpuMhz)));
      w.Write(s.Gpu < 0 ? (ushort)0xFFFF : (ushort)Math.Round(Math.Min(100, s.Gpu) * 10));
      w.Write(s.GpuTemp < 0 ? (byte)0xFF : (byte)Math.Min(254, s.GpuTemp));
      w.Write((uint)s.RamUsedMb);
      w.Write((uint)s.RamTotalMb);
      w.Write((uint)s.VramUsedMb);
      w.Write((uint)s.VramTotalMb);
      w.Write(s.Down);
      w.Write(s.Up);
      w.Write((uint)Math.Min(uint.MaxValue, s.UptimeS));
      w.Write((uint)(DateTime.UtcNow - new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc)).TotalSeconds);
      w.Write((ushort)Math.Min(65535, s.Procs));
      w.Flush();
      return Frame(STATS, ms.ToArray());
    }
  }

  // PROCS: cách sắp xếp (0 CPU, 1 bộ nhớ), số dòng, rồi mỗi dòng: CPU ×10, bộ nhớ (KB), số tiến trình cùng tên, tên
  public static byte[] Procs(List<ProcGroup> groups, int sort) {
    var top = (sort == 1 ? groups.OrderByDescending(g => g.Mem) : groups.OrderByDescending(g => g.Cpu).ThenByDescending(g => g.Mem))
                  .Take(PROC_ROWS)
                  .ToList();
    using (var ms = new MemoryStream())
    using (var w = new BinaryWriter(ms)) {
      w.Write((byte)sort);
      w.Write((byte)top.Count);
      foreach (var g in top) {
        w.Write((ushort)Math.Round(Math.Min(100, g.Cpu) * 10));
        w.Write((uint)Math.Min(uint.MaxValue, g.Mem >> 10));
        w.Write((ushort)Math.Min(65535, g.Count));
        Text(w, g.Name, NAME_BYTES);
      }
      w.Flush();
      return Frame(PROCS, ms.ToArray());
    }
  }
}

// ---------------------------------------------------------------------------------------------------------------
// Nhật ký: logs\agent.log (agent) và logs\board.log (log Serial của bo, vì agent đang giữ cổng COM). Đầy 1 MB thì đổi
// sang *.old.log.
// ---------------------------------------------------------------------------------------------------------------
static class Log {
  static readonly object gate = new object();
  public static string Dir;

  static void Append(string file, string line) {
    lock (gate) {
      try {
        string path = Path.Combine(Dir, file + ".log");
        var info = new FileInfo(path);
        if (info.Exists && info.Length > (1 << 20)) {
          string old = Path.Combine(Dir, file + ".old.log");
          if (File.Exists(old)) File.Delete(old);
          File.Move(path, old);
        }
        File.AppendAllText(path, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss ") + line + "\r\n", Encoding.UTF8);
      } catch (Exception) {
      }
    }
  }

  public static void Agent(string line) { Append("agent", line); }
  public static void Board(string line) { Append("board", line); }
}

// ---------------------------------------------------------------------------------------------------------------
// Cổng COM tới bo
// ---------------------------------------------------------------------------------------------------------------
sealed class Link {
  readonly object gate = new object();
  readonly string fixedPort;
  SerialPort port;
  Thread reader;
  DateTime openedAt, helloAt, searchAt = DateTime.MinValue, pausedUntil = DateTime.MinValue;
  readonly Dictionary<string, DateTime> notCyd = new Dictionary<string, DateTime>();
  public volatile bool Confirmed;  // bo đã trả lời (firmware có trang Máy tính)
  public volatile bool Viewing;    // trang Máy tính đang mở trên bo
  public volatile int Sort;        // 0: theo CPU, 1: theo bộ nhớ
  public volatile bool WantProcs;  // bo vừa mở trang hay đổi cách sắp xếp: gửi danh sách ngay
  public volatile bool IsDeck;     // bo chạy firmware CYD Deck (trả lời "[DECK] ...")
  public Action<string> DeckLine;  // mỗi dòng "[DECK] ..." của bo (luồng đọc cổng COM)
  public Action Disconnected;
  public string PortName = "";
  public string Status = I18n.T("link.searching");

  public Link(string fixedPort) { this.fixedPort = fixedPort; }

  public bool Paused { get { return DateTime.UtcNow < pausedUntil; } }

  public void Pause(int seconds) {
    lock (gate) {
      pausedUntil = DateTime.UtcNow.AddSeconds(Math.Max(1, seconds));
      Close("tạm nhả cổng " + seconds + " giây");
      Status = I18n.T("link.released");
    }
  }

  public void Resume() {
    lock (gate) pausedUntil = DateTime.MinValue;
    searchAt = DateTime.MinValue;
  }

  // Tìm cổng của chip USB-serial trên bo theo mã hãng USB, như tools/cyd.ps1: WCH (CH340/CH9102, 1A86), Silabs (CP210x,
  // 10C4), Espressif (303A). Trước 18/11/2026 lọc theo tên có "USB Serial" nên agent mở nhầm cổng COM của điện thoại
  // Samsung (VID 04E8, "USB Serial Device") và gửi gói chào vào đó.
  static List<string> CandidatePorts() {
    var result = new List<string>();
    try {
      using (var searcher = new ManagementObjectSearcher(
                 "SELECT Name, PNPDeviceID FROM Win32_PnPEntity WHERE Name LIKE '%(COM%'")) {
        foreach (ManagementObject device in searcher.Get()) {
          string name = (device["Name"] as string) ?? "";
          string id = ((device["PNPDeviceID"] as string) ?? "").ToUpperInvariant();
          if (!(id.Contains("VID_1A86") || id.Contains("VID_10C4") || id.Contains("VID_303A"))) continue;
          int at = name.LastIndexOf("(COM", StringComparison.Ordinal);
          if (at >= 0) result.Add(name.Substring(at + 1).TrimEnd(')'));
        }
      }
    } catch (Exception) {
    }
    return result;
  }

  void Close(string reason) {
    SerialPort old = port;
    port = null;
    Confirmed = false;
    Viewing = false;
    baudIndex = 0;  // lần mở sau dò lại từ 921600 (đường đổi tốc độ trong Tick tăng lại sau khi đóng)
    bool wasDeck = IsDeck;
    IsDeck = false;
    if (wasDeck && Disconnected != null) Disconnected();
    if (old != null) {
      try { old.Close(); } catch (Exception) { }
      try { old.Dispose(); } catch (Exception) { }
      Log.Agent("Đóng " + PortName + ": " + reason);
    }
  }

  // Deck nói ở 921600 baud, CYD-EEZ ở 115200: mở cổng ở tốc độ đầu, không ai trả lời sau vài giây thì thử tốc độ sau
  static readonly int[] BAUDS = { 921600, 115200 };
  int baudIndex = 0;
  public int Baud { get { return BAUDS[baudIndex]; } }

  void Open(string name) {
    var sp = new SerialPort(name, BAUDS[baudIndex], Parity.None, 8, StopBits.One);
    sp.DtrEnable = false;  // DTR/RTS nối vào chân EN/IO0 của ESP32: bật lên là bo khởi động lại
    sp.RtsEnable = false;
    sp.Handshake = Handshake.None;
    sp.ReadTimeout = 500;
    sp.WriteTimeout = 1000;
    sp.Open();
    port = sp;
    PortName = name;
    openedAt = DateTime.UtcNow;
    helloAt = DateTime.MinValue;
    Confirmed = false;
    Status = string.Format(I18n.T("link.hello"), name, BAUDS[baudIndex]);
    Log.Agent("Mở " + name);
    reader = new Thread(() => ReadLoop(sp)) { IsBackground = true, Name = "board-log" };
    reader.Start();
  }

  void ReadLoop(SerialPort sp) {
    var buffer = new byte[512];
    var line = new List<byte>(256);
    while (true) {
      int n;
      try {
        n = sp.Read(buffer, 0, buffer.Length);
      } catch (TimeoutException) {
        continue;
      } catch (Exception) {
        return;  // cổng đã đóng
      }
      for (int i = 0; i < n; i++) {
        byte b = buffer[i];
        if (b == '\n') {
          string text = Encoding.UTF8.GetString(line.ToArray()).TrimEnd('\r');
          line.Clear();
          if (text.Length > 0) OnLine(text);
        } else if (line.Count < 4096) {
          line.Add(b);
        }
      }
    }
  }

  // Dòng "[PC] ... xem=1 sx=0" của bo (src/app/pc_link.cpp)
  void OnLine(string text) {
    Log.Board(text);
    if (text.Contains("[DECK] ")) {
      if (!Confirmed) Log.Agent("Bo CYD Deck đã trả lời ở " + PortName);
      Confirmed = true;
      IsDeck = true;
      if (DeckLine != null) DeckLine(text);
      return;
    }
    int at = text.IndexOf("[PC]", StringComparison.Ordinal);
    if (at < 0) return;
    if (!Confirmed) Log.Agent("Bo đã trả lời ở " + PortName);
    Confirmed = true;
    int view = text.IndexOf("xem=", at, StringComparison.Ordinal);
    int sort = text.IndexOf("sx=", at, StringComparison.Ordinal);
    if (view >= 0 && view + 4 < text.Length) {
      bool viewing = text[view + 4] == '1';
      if (viewing && !Viewing) WantProcs = true;
      Viewing = viewing;
    }
    if (sort >= 0 && sort + 3 < text.Length) {
      int value = text[sort + 3] == '1' ? 1 : 0;
      if (value != Sort) WantProcs = true;
      Sort = value;
    }
  }

  // Gọi mỗi giây: mở cổng nếu chưa mở, chào bo cho tới khi bo trả lời
  public void Tick() {
    lock (gate) {
      if (Paused) return;
      if (port == null) {
        if (DateTime.UtcNow < searchAt) return;
        searchAt = DateTime.UtcNow.AddSeconds(5);
        var ports = fixedPort != null ? new List<string> { fixedPort } : CandidatePorts();
        foreach (string name in ports) {
          DateTime until;
          if (notCyd.TryGetValue(name, out until) && DateTime.UtcNow < until) continue;
          try {
            Open(name);
            break;
          } catch (Exception ex) {
            Status = string.Format(I18n.T("link.busy"), name, ex.GetType().Name);
          }
        }
        if (port == null) {
          if (ports.Count == 0) Status = I18n.T("link.none");
          return;
        }
      }
      if (!Confirmed) {
        double waited = (DateTime.UtcNow - openedAt).TotalSeconds;
        if (baudIndex + 1 < BAUDS.Length && waited > 4) {
          // không phải bo Deck ở 921600: mở lại ở tốc độ sau (CYD-EEZ) ngay, không phạt 1 phút
          string name = PortName;
          Close("không trả lời ở " + BAUDS[baudIndex] + " baud, thử " + BAUDS[baudIndex + 1]);
          baudIndex++;
          try { Open(name); } catch (Exception) { baudIndex = 0; }
          return;
        }
        if (waited > 20) {
          // Cổng không trả lời: thiết bị khác, hoặc firmware cũ chưa có trang Máy tính. Thử lại sau 1 phút.
          notCyd[PortName] = DateTime.UtcNow.AddSeconds(60);
          Close("không có trả lời, có thể là firmware chưa có trang Máy tính");
          baudIndex = 0;
          Status = string.Format(I18n.T("link.silent"), PortName);
          return;
        }
        if ((DateTime.UtcNow - helloAt).TotalSeconds >= 2) {
          helloAt = DateTime.UtcNow;
          Send(Frames.Hello());
        }
      } else {
        Status = string.Format(I18n.T(IsDeck ? "link.deck" : "link.board"), PortName);
        if ((DateTime.UtcNow - helloAt).TotalSeconds >= 10) {  // để bo vừa khởi động lại biết tên máy
          helloAt = DateTime.UtcNow;
          Send(Frames.Hello());
        }
      }
    }
  }

  public void Send(byte[] frame) {
    lock (gate) {
      if (port == null) return;
      try {
        port.Write(frame, 0, frame.Length);
      } catch (Exception ex) {
        Close("ghi lỗi (" + ex.GetType().Name + "), có thể đã rút cáp");
        Status = I18n.T("link.lost");
        searchAt = DateTime.UtcNow.AddSeconds(2);
      }
    }
  }

  public void Shutdown() {
    lock (gate) Close("thoát");
  }
}

// ---------------------------------------------------------------------------------------------------------------
// Điều khiển cục bộ (chỉ 127.0.0.1): "pause <giây>", "resume", "status", "quit", "ui" (mở trang cài đặt Deck),
// "url" (địa chỉ trang cài đặt kèm mã), "plugin <file>" (cài plugin Stream Deck từ file .streamDeckPlugin hay
// manifest.json, như nút Cài plugin của trang cài đặt). tools/cyd.ps1 dùng để nhả cổng COM.
// ---------------------------------------------------------------------------------------------------------------
sealed class Control {
  public const int PORT = 47830;
  readonly Link link;
  readonly Action quit, openUi;
  readonly Func<string> uiUrl;
  readonly Func<string, string> installPlugin;

  public Control(Link link, Action quit, Action openUi, Func<string> uiUrl, Func<string, string> installPlugin) {
    this.link = link;
    this.quit = quit;
    this.openUi = openUi;
    this.uiUrl = uiUrl;
    this.installPlugin = installPlugin;
    var thread = new Thread(Run) { IsBackground = true, Name = "control" };
    thread.Start();
  }

  void Run() {
    TcpListener listener;
    try {
      listener = new TcpListener(IPAddress.Loopback, PORT);
      listener.Start();
    } catch (Exception ex) {
      Log.Agent("Không mở được cổng điều khiển " + PORT + ": " + ex.Message);
      return;
    }
    while (true) {
      try {
        using (TcpClient client = listener.AcceptTcpClient()) {
          client.ReceiveTimeout = 2000;
          var stream = client.GetStream();
          var reader = new StreamReader(stream, Encoding.UTF8);
          var writer = new StreamWriter(stream, new UTF8Encoding(false)) { AutoFlush = true };
          string line = (reader.ReadLine() ?? "").Trim();
          string command = line.ToLowerInvariant();
          string[] parts = command.Split(' ');
          if (parts[0] == "pause") {
            int seconds = 600;
            if (parts.Length > 1) int.TryParse(parts[1], out seconds);
            link.Pause(seconds);
            writer.WriteLine("ok nha cong");
          } else if (parts[0] == "resume") {
            link.Resume();
            writer.WriteLine("ok");
          } else if (parts[0] == "ui") {  // mở trang cài đặt Deck
            openUi();
            writer.WriteLine("ok");
          } else if (parts[0] == "url" && uiUrl != null) {  // địa chỉ trang cài đặt (có mã), để thử từ máy này
            writer.WriteLine(uiUrl());
          } else if (parts[0] == "plugin" && line.Length > 7) {
            try {
              writer.WriteLine("ok " + installPlugin(line.Substring(7).Trim().Trim('"')));
            } catch (Exception ex) {
              writer.WriteLine("loi " + ex.Message);
            }
          } else if (parts[0] == "quit") {
            writer.WriteLine("ok");
            quit();
          } else {
            writer.WriteLine(link.Status);
          }
        }
      } catch (Exception) {
      }
    }
  }
}

// ---------------------------------------------------------------------------------------------------------------
// Ứng dụng: icon ở khay hệ thống, luồng đo mỗi giây
// ---------------------------------------------------------------------------------------------------------------
sealed class AgentApp : ApplicationContext {
  readonly Link link;
  readonly DeckSync deck;
  readonly DeckWeb web;
  readonly ElgatoHost plugins;
  readonly Sampler sampler = new Sampler();
  readonly NotifyIcon tray;
  readonly Icon iconOnline, iconOffline;
  readonly ToolStripMenuItem statusItem;
  readonly SynchronizationContext ui;
  volatile bool running = true;
  List<ProcGroup> lastProcs = new List<ProcGroup>();
  int procsAge = 1000;

  static Icon MakeIcon(Color color) {
    using (var bmp = new Bitmap(16, 16))
    using (var g = Graphics.FromImage(bmp)) {
      g.SmoothingMode = SmoothingMode.AntiAlias;
      using (var pen = new Pen(color, 2f))
      using (var brush = new SolidBrush(color)) {
        g.DrawRectangle(pen, 1.5f, 2f, 13f, 9f);  // màn hình
        g.FillRectangle(brush, 6, 12, 4, 2);       // chân
        g.FillRectangle(brush, 4, 14, 8, 1);
      }
      IntPtr handle = bmp.GetHicon();
      Icon icon = (Icon)Icon.FromHandle(handle).Clone();
      Native.DestroyIcon(handle);
      return icon;
    }
  }

  public AgentApp(string fixedPort) {
    ui = SynchronizationContext.Current ?? new WindowsFormsSynchronizationContext();
    link = new Link(fixedPort);
    deck = new DeckSync(link);
    deck.Obs = new DeckObs();
    deck.Obs.Needed = () => DeckConfig.UsesObs(deck.Layout.Config);
    plugins = new ElgatoHost(deck);
    deck.Plugins = plugins;
    link.DeckLine = deck.OnLine;
    link.Disconnected = deck.OnDisconnected;
    web = new DeckWeb(deck, link, plugins, Pick, () => ui.Post(_ => BuildMenu(), null));
    iconOnline = MakeIcon(Color.FromArgb(46, 204, 113));
    iconOffline = MakeIcon(Color.FromArgb(160, 160, 160));
    statusItem = new ToolStripMenuItem(link.Status) { Enabled = false };
    tray = new NotifyIcon { Icon = iconOffline, Text = "CYD PC Agent", Visible = true };
    BuildMenu();
    new Control(link, () => ui.Post(_ => Quit(), null), () => web.Open(), () => web.Url, file => {
      string uuid = ElgatoPlugins.Install(file);
      plugins.Restart(uuid);
      return uuid;
    });
    tray.DoubleClick += (s, e) => web.Open();
    var thread = new Thread(Loop) { IsBackground = true, Name = "sampler" };
    thread.Start();
    Log.Agent("Khởi động" + (Nvml.Ready ? ", GPU " + Nvml.Name + " qua NVML" : ", GPU qua bộ đếm hiệu năng"));
  }

  void Loop() {
    long next = Stopwatch.GetTimestamp();
    while (running) {
      try {
        bool deckMode = link.Confirmed && link.IsDeck;
        Stats stats = sampler.Sample(deckMode);
        link.Tick();
        // Danh sách tiến trình: mỗi giây khi trang Máy tính đang mở, 5 giây một lần khi không (chỉ để đếm số tiến
        // trình). Bo Deck không dùng: không đo (NtQuerySystemInformation cả nghìn tiến trình mỗi lần).
        bool want = link.Confirmed && !link.IsDeck && (link.Viewing || link.WantProcs);
        if (!deckMode && (want || ++procsAge >= 5)) {
          lastProcs = sampler.Procs.Sample(sampler.ProcScale(stats));
          procsAge = 0;
          stats.Procs = sampler.Procs.Count;
        }
        if (link.Confirmed && link.IsDeck) {
          deck.Tick(stats);
        } else if (link.Confirmed) {
          link.Send(Frames.Stats(stats));
          if (want) {
            link.WantProcs = false;
            link.Send(Frames.Procs(lastProcs, link.Sort));
          }
        }
        string status = link.Status;
        bool online = link.Confirmed;
        ui.Post(_ => ShowStatus(status, online), null);
      } catch (Exception ex) {
        Log.Agent("Lỗi vòng đo: " + ex);
      }
      next += Stopwatch.Frequency;
      long wait = (next - Stopwatch.GetTimestamp()) * 1000 / Stopwatch.Frequency;
      if (wait < 0) {
        next = Stopwatch.GetTimestamp();
        wait = 0;
      }
      Thread.Sleep((int)Math.Min(1000, wait));
    }
  }

  // Menu khay theo ngôn ngữ đang chọn (dựng lại khi đổi ngôn ngữ trong trang cài đặt)
  void BuildMenu() {
    var menu = new ContextMenuStrip();
    menu.Items.Add(statusItem);
    menu.Items.Add(new ToolStripSeparator());
    menu.Items.Add(I18n.T("menu.settings"), null, (s, e) => web.Open());
    menu.Items.Add(new ToolStripSeparator());
    menu.Items.Add(I18n.T("menu.pause"), null, (s, e) => link.Pause(600));
    menu.Items.Add(I18n.T("menu.resume"), null, (s, e) => link.Resume());
    menu.Items.Add(I18n.T("menu.logs"), null, (s, e) => Process.Start("explorer.exe", "\"" + Log.Dir + "\""));
    menu.Items.Add(new ToolStripSeparator());
    menu.Items.Add(I18n.T("menu.quit"), null, (s, e) => Quit());
    var old = tray.ContextMenuStrip;
    tray.ContextMenuStrip = menu;
    if (old != null) {
      old.Items.Remove(statusItem);
      old.Dispose();
    }
  }

  // Hộp chọn file cho trang cài đặt (luồng giao diện, STA). kind: "exe", "image", "any"
  string Pick(string kind) {
    string result = null;
    ui.Send(_ => {
      using (var owner = new Form { TopMost = true, ShowInTaskbar = false, StartPosition = FormStartPosition.CenterScreen,
                                    Size = new Size(1, 1), Opacity = 0 })
      using (var dialog = new OpenFileDialog()) {
        dialog.Title = I18n.T("pick.title");
        string all = I18n.T("pick.all") + "|*.*";
        dialog.Filter = kind == "exe" ? I18n.T("pick.programs") + " (*.exe;*.cmd;*.bat;*.ps1;*.lnk;*.msc)|*.exe;*.cmd;*.bat;*.ps1;*.lnk;*.msc|" + all
                      : kind == "image" ? I18n.T("pick.images") + " (*.png;*.jpg;*.ico;*.gif;*.bmp)|*.png;*.jpg;*.jpeg;*.ico;*.gif;*.bmp"
                      : kind == "plugin" ? I18n.T("pick.plugins") + " (*.streamDeckPlugin;*.zip;manifest.json)|*.streamDeckPlugin;*.zip;manifest.json|" + all
                      : all;
        dialog.DereferenceLinks = false;
        owner.Show();
        owner.Activate();
        if (dialog.ShowDialog(owner) == DialogResult.OK) result = dialog.FileName;
      }
    }, null);
    return result;
  }

  string shownStatus;
  bool? shownOnline;

  void ShowStatus(string status, bool online) {
    if (status == shownStatus && online == shownOnline) return;  // đặt lại mỗi giây làm khay vẽ lại, tốn handle GDI
    shownStatus = status;
    shownOnline = online;
    statusItem.Text = status;
    string tip = "CYD PC Agent: " + status;
    tray.Text = tip.Length > 63 ? tip.Substring(0, 63) : tip;
    tray.Icon = online ? iconOnline : iconOffline;
  }

  public void OpenUi() { web.Open(); }

  void Quit() {
    running = false;
    plugins.Shutdown();  // tắt tiến trình của các plugin
    link.Shutdown();
    tray.Visible = false;
    Log.Agent("Thoát");
    ExitThread();
  }
}

static class Program {
  // --dump <file>: đo hai lượt cách nhau 1 giây rồi ghi số liệu ra file và thoát (để kiểm tra, không mở cổng COM).
  static void Dump(string path) {
    var sampler = new Sampler();
    sampler.Procs.Sample(1);
    Thread.Sleep(1000);
    Stats s = sampler.Sample();
    var groups = sampler.Procs.Sample(sampler.ProcScale(s));
    var sb = new StringBuilder();
    sb.AppendLine("May: " + Environment.MachineName);
    sb.AppendFormat("CPU {0:0.0}% @ {1} MHz | RAM {2}/{3} MB | GPU {4:0.0}% {5}°C VRAM {6}/{7} MB ({8})\r\n", s.Cpu,
                    s.CpuMhz, s.RamUsedMb, s.RamTotalMb, s.Gpu, s.GpuTemp, s.VramUsedMb, s.VramTotalMb,
                    Nvml.Ready ? "NVML " + Nvml.Name : "PDH");
    sb.AppendFormat("Mang: ve {0} B/s, len {1} B/s | bat may {2} s | {3} tien trinh, ti le CPU {4:0.00}\r\n", s.Down,
                    s.Up, s.UptimeS, sampler.Procs.Count, sampler.ProcScale(s));
    sb.AppendLine("-- theo CPU --");
    foreach (var g in groups.OrderByDescending(x => x.Cpu).Take(10)) {
      sb.AppendFormat("{0,6:0.0}%  {1,10:0.0} MB  x{2,-3} {3}\r\n", g.Cpu, g.Mem / 1048576.0, g.Count, g.Name);
    }
    sb.AppendLine("-- theo bo nho --");
    foreach (var g in groups.OrderByDescending(x => x.Mem).Take(10)) {
      sb.AppendFormat("{0,6:0.0}%  {1,10:0.0} MB  x{2,-3} {3}\r\n", g.Cpu, g.Mem / 1048576.0, g.Count, g.Name);
    }
    sb.AppendFormat("Goi STATS {0} byte, PROCS {1} byte\r\n", Frames.Stats(s).Length, Frames.Procs(groups, 0).Length);
    File.WriteAllText(path, sb.ToString(), Encoding.UTF8);
  }

  [STAThread]
  static void Main(string[] args) {
    string baseDir = AppDomain.CurrentDomain.BaseDirectory;
    Log.Dir = Path.Combine(Path.GetDirectoryName(baseDir.TrimEnd('\\')) ?? baseDir, "logs");
    Directory.CreateDirectory(Log.Dir);
    string port = null;
    bool openUi = false;
    for (int i = 0; i < args.Length; i++) {
      if (args[i] == "--deck-hashes" && i + 1 < args.Length) {  // mã ảnh từng phím của deck.json (kiểm tra vẽ ảnh ổn định)
        var layout = DeckConfig.Build(DeckConfig.Load());
        var sb = new StringBuilder();
        sb.AppendLine("layout " + layout.Crc.ToString("x8"));
        foreach (var pair in layout.Images) sb.AppendLine(pair.Key.ToString("x8"));
        File.WriteAllText(args[i + 1], sb.ToString());
        return;
      }
      if (args[i] == "--dump" && i + 1 < args.Length) {
        Dump(args[i + 1]);
        return;
      }
      if (args[i] == "--port" && i + 1 < args.Length) port = args[++i].ToUpperInvariant();
      if (args[i] == "--deck-ui") openUi = true;
    }
    bool created;
    using (var mutex = new Mutex(true, "CydPcAgent", out created)) {
      if (!created) return;  // đã có một agent đang chạy
      Application.EnableVisualStyles();
      Application.SetCompatibleTextRenderingDefault(false);
      var app = new AgentApp(port);
      if (openUi) app.OpenUi();
      Application.Run(app);
    }
  }
}

}  // namespace CydAgent
