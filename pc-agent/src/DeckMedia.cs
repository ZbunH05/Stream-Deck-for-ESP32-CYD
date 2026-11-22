using System;
using System.Threading;
using Windows.Foundation;
using Windows.Media.Control;

namespace CydAgent {

sealed class MediaSnapshot {
  public bool Has, Playing;
  public string Title = "", Artist = "", App = "";
  public int Permille = -1;

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

  public MediaSnapshot Read() {
    var snap = new MediaSnapshot();
    try {
      if (manager == null) {
        if ((DateTime.UtcNow - managerAt).TotalSeconds < 10) return snap;
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
    if (dot > 0 && dot < name.Length - 1 && name.IndexOf('_') > 0) name = name.Substring(dot + 1);
    return name.Length > 20 ? name.Substring(0, 20) : name;
  }
}

}
