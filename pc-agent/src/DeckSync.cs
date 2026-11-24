using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace CydAgent {

interface IDeckPlugins {
  void KeyDown(int boardPage, int slot);
  void KeyUp(int boardPage, int slot);
  void PageShown(int boardPage, int previous);
  void LayoutChanged(DeckLayout layout);
}

sealed class DeckSync {
  public const byte LAYOUT_BEGIN = 0x11, LAYOUT_PAGE = 0x12, LAYOUT_END = 0x13, IMG_BEGIN = 0x14, IMG_DATA = 0x15,
                    IMG_END = 0x16, STATE = 0x17, INFO = 0x18, ACK = 0x19, OBS = 0x1A, KEY_IMAGE = 0x1B, SCREEN_FLIP = 0x1C;
  const int CHUNK_DECODED = 2048, CHUNK_ENCODED = 600;
  const int HOLD_MS = 500;

  readonly Link link;
  readonly object gate = new object();
  readonly AutoResetEvent wake = new AutoResetEvent(false);
  readonly BlockingCollection<string> replies = new BlockingCollection<string>();
  DeckLayout layout;
  volatile bool connected, syncWanted;
  uint boardCrc;
  volatile int visiblePage = -1;
  volatile int flip = -1;
  readonly Dictionary<int, DateTime> downAt = new Dictionary<int, DateTime>();
  readonly Dictionary<int, string> sentState = new Dictionary<int, string>();
  readonly Dictionary<int, byte[]> ramImages = new Dictionary<int, byte[]>();
  readonly HashSet<int> ramDirty = new HashSet<int>();
  readonly Dictionary<int, string> pluginTitle = new Dictionary<int, string>();
  readonly Dictionary<int, bool> pluginOn = new Dictionary<int, bool>();
  byte[] lastInfo, lastObs;
  DateTime infoAt = DateTime.MinValue, obsAt = DateTime.MinValue;
  int stateAge;
  public readonly DeckMedia Media = new DeckMedia();
  public MediaSnapshot LastMedia = new MediaSnapshot();
  public DeckObs Obs;
  public IDeckPlugins Plugins;
  public volatile string Status = "";
  public volatile string LastPress = "";

  internal DeckSync(Link link) {
    this.link = link;
    layout = DeckConfig.Build(DeckConfig.Load());
    new Thread(Worker) { IsBackground = true, Name = "deck-sync" }.Start();
  }

  public DeckLayout Layout { get { lock (gate) return layout; } }
  public bool Connected { get { return connected; } }
  public int VisiblePage { get { return visiblePage; } }
  public int Flip { get { return flip; } }

  public bool SetFlip(bool on) {
    if (!connected || flip < 0) return false;
    link.Send(Frames.Frame(SCREEN_FLIP, new[] { (byte)(on ? 1 : 0) }));
    return true;
  }

  public void Apply(DeckConfigData config) {
    var built = DeckConfig.Build(config);
    lock (gate) {
      layout = built;
      ramImages.Clear();
      ramDirty.Clear();
      sentState.Clear();
      pluginTitle.Clear();
      pluginOn.Clear();
    }
    if (Plugins != null) Plugins.LayoutChanged(built);
    syncWanted = true;
    wake.Set();
  }

  public void OnLine(string text) {
    int at = text.IndexOf("[DECK] ", StringComparison.Ordinal);
    if (at < 0) return;
    string[] parts = text.Substring(at + 7).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
    if (parts.Length == 0) return;
    int a = -1, b = -1;
    if (parts.Length >= 2) int.TryParse(parts[1], out a);
    if (parts.Length >= 3) int.TryParse(parts[2], out b);
    switch (parts[0]) {
      case "chao":
        if (parts.Length >= 4) {
          if (a < 2) {
            Status = I18n.T("sync.old_firmware");
            return;
          }
          uint crc;
          uint.TryParse(parts[3], NumberStyles.HexNumber, null, out crc);
          bool fresh = !connected;
          boardCrc = crc;
          connected = true;
          if (fresh) {
            Log.Agent("Bo CYD Deck: " + parts[2] + " ảnh, bộ phím " + parts[3]);
            lock (gate) {
              sentState.Clear();
              lastInfo = lastObs = null;
            }
            syncWanted = true;
            wake.Set();
          }
        }
        break;
      case "xuong":
        if (a >= 0 && b >= 0) KeyDown(a, b);
        break;
      case "len":
      case "huy":
        if (a >= 0 && b >= 0) KeyUp(a, b, parts[0] == "huy");
        break;
      case "trang":
        if (a >= 0) PageShown(a);
        break;
      case "lat":
        if (a == 0 || a == 1) flip = a;
        break;
      case "anh":
      case "bo":
      case "ram":
        replies.Add(string.Join(" ", parts));
        break;
    }
  }

  public void OnDisconnected() {
    connected = false;
    visiblePage = -1;
    flip = -1;
    lock (gate) {
      sentState.Clear();
      downAt.Clear();
    }
    Status = "";
  }

  void KeyDown(int page, int slot) {
    lock (gate) downAt[page * 16 + slot] = DateTime.UtcNow;
    var key = Layout.KeyAt(page, slot);
    if (key != null && key.Kind == "plugin" && Plugins != null) Plugins.KeyDown(page, slot);
  }

  void KeyUp(int page, int slot, bool cancelled) {
    DateTime down;
    bool known;
    lock (gate) {
      known = downAt.TryGetValue(page * 16 + slot, out down);
      downAt.Remove(page * 16 + slot);
    }
    var key = Layout.KeyAt(page, slot);
    if (key == null) return;
    if (key.Kind == "plugin") {
      if (Plugins != null) Plugins.KeyUp(page, slot);
      return;
    }
    if (cancelled) return;
    bool held = known && (DateTime.UtcNow - down).TotalMilliseconds >= HOLD_MS;
    ThreadPool.QueueUserWorkItem(_ => Press(page, slot, key, held));
  }

  void Press(int page, int slot, DeckKeyConfig key, bool held) {
    bool ok = false;
    string name = key.Title.Length > 0 ? key.Title : "#" + (slot + 1);
    try {
      DeckAction action = held && key.Hold != null && key.Hold.Type != "none" ? key.Hold : key.Action;
      if (action != null && action.Type == "obs") {
        if (Obs == null) throw new InvalidOperationException(I18n.T("obs.off"));
        Obs.Run(action);
      } else {
        DeckActions.Run(action);
      }
      ok = true;
      LastPress = DateTime.Now.ToString("HH:mm:ss") + " " + name + ": " + I18n.T("press.done");
      if (key.Kind == "toggle" || !string.IsNullOrEmpty(key.Live)) {
        Thread.Sleep(80);
        SendStates(true);
      }
    } catch (Exception ex) {
      LastPress = DateTime.Now.ToString("HH:mm:ss") + " " + name + ": " + ex.Message;
      Log.Agent("Phím " + page + "/" + slot + " (" + name + ") lỗi: " + ex.Message);
    }
    Feedback(page, slot, ok);
  }

  public void Feedback(int page, int slot, bool ok) {
    link.Send(Frames.Frame(ACK, new[] { (byte)page, (byte)slot, (byte)(ok ? 1 : 0) }));
  }

  void PageShown(int page) {
    int previous = visiblePage;
    if (page == previous) return;
    visiblePage = page;
    lock (gate) {
      foreach (var id in ramImages.Keys.Where(id => id / 16 == page).ToList()) ramDirty.Add(id);
      sentState.Clear();
    }
    if (Plugins != null) Plugins.PageShown(page, previous);
    wake.Set();
  }

  public void SetKeyImage(int page, int slot, byte[] px) {
    int id = page * 16 + slot;
    lock (gate) {
      byte[] old;
      bool had = ramImages.TryGetValue(id, out old);
      if (had && SameBytes(old, px)) return;
      if (!had && px == null) return;
      ramImages[id] = px;
      ramDirty.Add(id);
    }
    wake.Set();
  }

  public void SetKeyTitle(int page, int slot, string title) {
    lock (gate) {
      if (title == null) pluginTitle.Remove(page * 16 + slot);
      else pluginTitle[page * 16 + slot] = title;
    }
    SendStates(false);
  }

  public void SetKeyState(int page, int slot, bool on) {
    lock (gate) pluginOn[page * 16 + slot] = on;
    SendStates(false);
  }

  static bool SameBytes(byte[] a, byte[] b) {
    if (a == null || b == null) return a == b;
    if (a.Length != b.Length) return false;
    for (int i = 0; i < a.Length; i++) {
      if (a[i] != b[i]) return false;
    }
    return true;
  }

  public void Tick(Stats s) {
    if (!connected) return;
    var media = Media.Read();
    LastMedia = media;
    byte[] info;
    using (var ms = new MemoryStream())
    using (var w = new BinaryWriter(ms)) {
      w.Write((byte)(media.Playing ? 1 : 0));
      Text(w, DateTime.Now.ToString("HH:mm"), 5);
      w.Write((byte)Math.Round(Math.Max(0, Math.Min(100, s.Cpu))));
      w.Write((byte)(s.RamTotalMb > 0 ? Math.Round(s.RamUsedMb * 100.0 / s.RamTotalMb) : 0));
      Text(w, media.Has ? media.Title : "", 47);
      string sub = media.Artist.Length > 0 && media.App.Length > 0 ? media.Artist + " · " + media.App
                 : media.Artist.Length > 0 ? media.Artist : media.App;
      Text(w, media.Has ? sub : "", 47);
      w.Write((ushort)(media.Has && media.Permille >= 0 ? media.Permille : 0xFFFF));
      w.Flush();
      info = ms.ToArray();
    }
    if (lastInfo == null || !SameBytes(info, lastInfo) || (DateTime.UtcNow - infoAt).TotalSeconds >= 2) {
      link.Send(Frames.Frame(INFO, info));
      lastInfo = info;
      infoAt = DateTime.UtcNow;
    }
    if (Obs != null && Obs.Wanted) {
      byte[] obs = Obs.InfoPayload();
      if (lastObs == null || !SameBytes(obs, lastObs) || (DateTime.UtcNow - obsAt).TotalSeconds >= 5) {
        link.Send(Frames.Frame(OBS, obs));
        lastObs = obs;
        obsAt = DateTime.UtcNow;
      }
    }
    SendStates(++stateAge >= 10);
  }

  void SendStates(bool all) {
    if (!connected) return;
    var current = Layout;
    if (all) stateAge = 0;
    bool? mic = null, speaker = null;
    int? volume = null;
    bool micRead = false, speakerRead = false, volumeRead = false;
    for (int page = 0; page < current.ConfigOf.Length; page++) {
      for (int slot = 0; slot < DeckConfig.KEYS; slot++) {
        var key = current.KeyAt(page, slot);
        if (key == null || key.Kind == "empty" || key.Kind == "back" || key.Kind == "folder") continue;
        int id = page * 16 + slot;
        bool on = false;
        string live = "";
        if (key.Kind == "plugin") {
          bool v;
          string t;
          lock (gate) {
            on = pluginOn.TryGetValue(id, out v) && v;
            if (pluginTitle.TryGetValue(id, out t)) live = t ?? "";
          }
        } else {
          var a = key.Action;
          if (key.Kind == "toggle" && a != null) {
            if (a.Type == "mic") {
              if (!micRead) { mic = Audio.Muted(true); micRead = true; }
              on = mic == true;
            } else if (a.Type == "volume" && a.Cmd == "mute") {
              if (!speakerRead) { speaker = Audio.Muted(false); speakerRead = true; }
              on = speaker == true;
            } else if (a.Type == "media" && (a.Cmd ?? "playpause") == "playpause") {
              on = LastMedia.Playing;
            } else if (a.Type == "obs" && Obs != null) {
              on = Obs.IsOn(a);
            }
          }
          if (key.Live == "volume") {
            if (!volumeRead) { volume = Audio.Percent(); volumeRead = true; }
            if (volume.HasValue) live = (on && a != null && a.Type == "volume" ? I18n.T("live.muted") + " · " : "") + volume.Value + "%";
          } else if (key.Live == "obsvolume" && Obs != null && a != null) {
            live = Obs.VolumeText(a.Target);
          }
        }
        if (key.Kind != "toggle" && key.Kind != "plugin" && live.Length == 0) continue;
        string value = (on ? "1" : "0") + live;
        lock (gate) {
          string old;
          if (!all && sentState.TryGetValue(id, out old) && old == value) continue;
          sentState[id] = value;
        }
        using (var ms = new MemoryStream())
        using (var w = new BinaryWriter(ms)) {
          w.Write((byte)page);
          w.Write((byte)slot);
          w.Write((byte)(on ? 1 : 0));
          Text(w, live, 23);
          w.Flush();
          link.Send(Frames.Frame(STATE, ms.ToArray()));
        }
      }
    }
  }

  void Worker() {
    while (true) {
      wake.WaitOne(2000);
      if (!connected) continue;
      try {
        if (syncWanted) {
          syncWanted = false;
          if (!Sync()) {
            syncWanted = true;
            Thread.Sleep(3000);
            continue;
          }
        }
        SendRamImages();
      } catch (Exception ex) {
        Log.Agent("Đồng bộ bộ phím lỗi: " + ex);
        syncWanted = true;
        Thread.Sleep(3000);
      }
    }
  }

  string Reply(string prefix, int timeoutMs) {
    var until = DateTime.UtcNow.AddMilliseconds(timeoutMs);
    while (true) {
      int left = (int)(until - DateTime.UtcNow).TotalMilliseconds;
      string line;
      if (left <= 0 || !replies.TryTake(out line, left)) return null;
      if (line.StartsWith(prefix, StringComparison.Ordinal)) return line;
    }
  }

  void Drain() {
    string ignored;
    while (replies.TryTake(out ignored)) {
    }
  }

  bool Sync() {
    var current = Layout;
    Drain();
    if (boardCrc != current.Crc) {
      Status = I18n.T("sync.layout");
      foreach (var frame in current.Frames) link.Send(frame);
      string reply = Reply("bo ", 5000);
      if (reply == null || !reply.EndsWith(" ok")) {
        Status = I18n.T("sync.layout_failed") + " (" + (reply ?? "-") + ")";
        Log.Agent(Status);
        return false;
      }
      boardCrc = current.Crc;
      Log.Agent("Đã gửi bộ phím " + current.Crc.ToString("x8"));
    }
    int index = 0, sent = 0;
    var watch = System.Diagnostics.Stopwatch.StartNew();
    foreach (var pair in current.Images) {
      index++;
      if (!ReferenceEquals(current, Layout)) return true;
      Status = string.Format(I18n.T("sync.images"), index, current.Images.Count);
      int result = Upload(pair.Key, pair.Value);
      if (result < 0) {
        Status = I18n.T("sync.image_failed");
        return false;
      }
      sent += result;
    }
    Status = string.Format(I18n.T("sync.done"), current.Images.Count);
    if (sent > 0) Log.Agent("Đã gửi " + sent + " ảnh phím xuống bo trong " + watch.ElapsedMilliseconds + " ms");
    lock (gate) sentState.Clear();
    return true;
  }

  int Upload(uint hash, byte[] px) {
    string id = hash.ToString("x8");
    link.Send(Frames.Frame(IMG_BEGIN, BitConverter.GetBytes(hash)));
    string reply = Reply("anh " + id, 3000);
    if (reply == null || reply.EndsWith(" loi")) return -1;
    if (reply.EndsWith(" co")) return 0;
    foreach (var chunk in Rle.Chunks(px, CHUNK_DECODED, CHUNK_ENCODED)) {
      var payload = new byte[7 + chunk.Data.Length];
      BitConverter.GetBytes(hash).CopyTo(payload, 0);
      BitConverter.GetBytes((ushort)chunk.Offset).CopyTo(payload, 4);
      payload[6] = 1;
      Buffer.BlockCopy(chunk.Data, 0, payload, 7, chunk.Data.Length);
      link.Send(Frames.Frame(IMG_DATA, payload));
      reply = Reply("anh " + id, 3000);
      if (reply == null || reply.EndsWith(" loi")) return -1;
    }
    link.Send(Frames.Frame(IMG_END, BitConverter.GetBytes(hash)));
    reply = Reply("anh " + id, 3000);
    return reply != null && reply.EndsWith(" xong") ? 1 : -1;
  }

  void SendRamImages() {
    while (connected) {
      int id = -1;
      byte[] px = null;
      lock (gate) {
        foreach (int candidate in ramDirty) {
          if (candidate / 16 != visiblePage) continue;
          id = candidate;
          break;
        }
        if (id < 0) return;
        ramDirty.Remove(id);
        ramImages.TryGetValue(id, out px);
      }
      byte page = (byte)(id / 16), slot = (byte)(id % 16);
      if (px == null) {
        link.Send(Frames.Frame(KEY_IMAGE, new byte[] { page, slot, 0xFF, 0xFF }));
        continue;
      }
      foreach (var chunk in Rle.Chunks(px, CHUNK_DECODED, CHUNK_ENCODED)) {
        var payload = new byte[5 + chunk.Data.Length];
        payload[0] = page;
        payload[1] = slot;
        BitConverter.GetBytes((ushort)chunk.Offset).CopyTo(payload, 2);
        payload[4] = 1;
        Buffer.BlockCopy(chunk.Data, 0, payload, 5, chunk.Data.Length);
        link.Send(Frames.Frame(KEY_IMAGE, payload));
      }
      if (Reply("ram " + page + " " + slot, 1500) == null) {
        Log.Agent("Bo không nhận ảnh RAM " + page + "/" + slot);
        lock (gate) ramDirty.Add(id);
        return;
      }
    }
  }

  public static void Text(BinaryWriter w, string text, int max) {
    byte[] bytes = Encoding.UTF8.GetBytes(text ?? "");
    int length = Math.Min(bytes.Length, max);
    while (length < bytes.Length && length > 0 && (bytes[length] & 0xC0) == 0x80) length--;
    w.Write((byte)length);
    w.Write(bytes, 0, length);
  }
}

static class Rle {
  public sealed class Chunk {
    public int Offset;
    public byte[] Data;
  }

  static bool Same(byte[] px, int a, int b) { return px[2 * a] == px[2 * b] && px[2 * a + 1] == px[2 * b + 1]; }

  public static List<Chunk> Chunks(byte[] px, int maxDecoded, int maxEncoded) {
    var list = new List<Chunk>();
    int pixels = px.Length / 2, i = 0;
    while (i < pixels) {
      var outBuf = new List<byte>(maxEncoded);
      int start = i;
      while (i < pixels) {
        int room = maxDecoded / 2 - (i - start);
        if (room <= 0) break;
        int run = 1;
        while (i + run < pixels && run < 129 && run < room && Same(px, i, i + run)) run++;
        if (run >= 2) {
          if (outBuf.Count + 3 > maxEncoded) break;
          outBuf.Add((byte)(run + 0x7E));
          outBuf.Add(px[2 * i]);
          outBuf.Add(px[2 * i + 1]);
          i += run;
          continue;
        }
        int lit = 1;
        while (i + lit < pixels && lit < 128 && lit < room && !(i + lit + 1 < pixels && Same(px, i + lit, i + lit + 1))) lit++;
        int fit = Math.Min(lit, (maxEncoded - outBuf.Count - 1) / 2);
        if (fit <= 0) break;
        outBuf.Add((byte)(fit - 1));
        for (int k = 0; k < fit; k++) {
          outBuf.Add(px[2 * (i + k)]);
          outBuf.Add(px[2 * (i + k) + 1]);
        }
        i += fit;
      }
      list.Add(new Chunk { Offset = start * 2, Data = outBuf.ToArray() });
    }
    return list;
  }
}

}
