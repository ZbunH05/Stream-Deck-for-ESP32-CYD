// CYD Deck: nhạc đang phát trên Windows (Spotify, trình duyệt, YouTube Music...), qua WinRT
// GlobalSystemMediaTransportControlsSessionManager: cùng nguồn với bảng nhạc ở góc màn hình của Windows.
//
// Dịch bằng csc của .NET Framework 4 với siêu dữ liệu WinRT có sẵn trong Windows (System32\WinMetadata\*.winmd), không
// cần Windows SDK. Vì vậy không dùng AsTask/await (cần siêu dữ liệu "Windows" của SDK) và không đăng ký sự kiện WinRT
// (C# 5 không làm được với các file đó): chờ thao tác bất đồng bộ bằng cách đọc Status, và đọc trạng thái mỗi giây.
// Đo 18/11/2026: một lượt đọc đầy đủ ~15 ms, phần lớn là TryGetMediaPropertiesAsync; nên tên bài chỉ đọc lại khi app phát
// đổi, trạng thái đổi, hay vị trí nhảy (đổi bài), và ít nhất 5 giây một lần.
using System;
using System.Threading;
using Windows.Foundation;
using Windows.Media.Control;

namespace CydAgent {

sealed class MediaSnapshot {
  public bool Has, Playing;
  public string Title = "", Artist = "", App = "";
  public int Permille = -1;  // tiến độ ‰, -1: không biết

  public bool SameAs(MediaSnapshot o) {
    return o != null && Has == o.Has && Playing == o.Playing && Title == o.Title && Artist == o.Artist && App == o.App &&
           Permille == o.Permille;
  }
}

sealed class DeckMedia {
  GlobalSystemMediaTransportControlsSessionManager manager;
  DateTime managerAt = DateTime.MinValue;
  string lastApp = "";
  GlobalSystemMediaTransportControlsSessionPlaybackStatus lastStatus;
  DateTime propsAt = DateTime.MinValue;
  double lastPosition = -1;
  string title = "", artist = "";

  static R Wait<R>(IAsyncOperation<R> op) {
    var until = DateTime.UtcNow.AddSeconds(2);
    while (op.Status == AsyncStatus.Started && DateTime.UtcNow < until) Thread.Sleep(3);
    if (op.Status != AsyncStatus.Completed) throw new TimeoutException("WinRT không trả lời");
    return op.GetResults();
  }

  // Gọi mỗi giây (luồng đo của agent)
  public MediaSnapshot Read() {
    var snap = new MediaSnapshot();
    try {
      if (manager == null) {
        if ((DateTime.UtcNow - managerAt).TotalSeconds < 10) return snap;  // lỗi lần trước: thử lại sau
        managerAt = DateTime.UtcNow;
        manager = Wait(GlobalSystemMediaTransportControlsSessionManager.RequestAsync());
      }
      var session = manager.GetCurrentSession();
      if (session == null) {
        lastApp = "";
        return snap;
      }
      string app = session.SourceAppUserModelId ?? "";
      var status = session.GetPlaybackInfo().PlaybackStatus;
      var timeline = session.GetTimelineProperties();
      double length = (timeline.EndTime - timeline.StartTime).TotalSeconds;
      double position = (timeline.Position - timeline.StartTime).TotalSeconds;
      bool playing = status == GlobalSystemMediaTransportControlsSessionPlaybackStatus.Playing;
      // app chỉ báo vị trí lúc đổi trạng thái hay tua: tự cộng thời gian đã trôi khi đang phát
      if (playing) position += (DateTimeOffset.Now - timeline.LastUpdatedTime).TotalSeconds;

      bool jumped = lastPosition >= 0 && Math.Abs(position - lastPosition) > 5 && position < 3;
      if (app != lastApp || status != lastStatus || jumped || (DateTime.UtcNow - propsAt).TotalSeconds >= 5) {
        var props = Wait(session.TryGetMediaPropertiesAsync());
        title = (props.Title ?? "").Trim();
        artist = (props.Artist ?? "").Trim();
        propsAt = DateTime.UtcNow;
      }
      lastApp = app;
      lastStatus = status;
      lastPosition = position;

      snap.Has = title.Length > 0;
      snap.Playing = playing;
      snap.Title = title;
      snap.Artist = artist;
      snap.App = FriendlyApp(app);
      if (length > 1) snap.Permille = (int)Math.Max(0, Math.Min(1000, Math.Round(position * 1000 / length)));
    } catch (Exception ex) {
      Log.Agent("Không đọc được nhạc đang phát: " + ex.Message);
      manager = null;
    }
    return snap;
  }

  // Tên app từ AppUserModelId ("Spotify.exe", "Chrome", "MSEdge", "308046B0AF4A39CB" của Firefox...)
  public static string FriendlyApp(string id) {
    string lower = id.ToLowerInvariant();
    if (lower.Contains("spotify")) return "Spotify";
    if (lower.Contains("msedge")) return "Edge";
    if (lower.Contains("chrome")) return "Chrome";
    if (lower.Contains("firefox") || lower.Contains("308046b0af4a39cb")) return "Firefox";
    if (lower.Contains("coccoc")) return "Cốc Cốc";
    if (lower.Contains("youtube")) return "YouTube Music";
    if (lower.Contains("zunemusic") || lower.Contains("media.player")) return "Media Player";
    if (lower.Contains("vlc")) return "VLC";
    if (lower.Contains("foobar")) return "foobar2000";
    if (lower == "browser") return "Browser";
    string name = id;
    int bang = name.LastIndexOf('!');
    if (bang >= 0) name = name.Substring(bang + 1);
    if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - 4);
    int dot = name.LastIndexOf('.');
    if (dot > 0 && dot < name.Length - 1 && name.IndexOf('_') > 0) name = name.Substring(dot + 1);  // gói Store
    return name.Length > 20 ? name.Substring(0, 20) : name;
  }
}

}  // namespace CydAgent
