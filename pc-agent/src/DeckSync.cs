// CYD Deck: nói chuyện với bo đang chạy firmware CYD Deck (giao thức ở ..\..\firmware\src\deck\link.h).
//   - Gửi bộ phím (LAYOUT_*) khi CRC bo báo khác bộ phím hiện tại, rồi gửi những ảnh bo chưa có (IMG_*), mỗi gói chờ
//     bo trả lời rồi mới gửi gói sau (bo xoá/ghi flash giữa chừng).
//   - Mỗi giây: thanh tin (INFO) và trạng thái phím bật/tắt, chữ sống (STATE).
//   - Bo báo "[DECK] bam <trang> <phím> <giữ>": chạy lệnh của phím, trả ACK.
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

namespace CydAgent {

sealed class DeckSync {
  public const byte LAYOUT_BEGIN = 0x11, LAYOUT_PAGE = 0x12, LAYOUT_END = 0x13, IMG_BEGIN = 0x14, IMG_DATA = 0x15,
                    IMG_END = 0x16, STATE = 0x17, INFO = 0x18, ACK = 0x19;
  const int CHUNK = 480;

  readonly Link link;
  readonly object gate = new object();
  readonly AutoResetEvent wake = new AutoResetEvent(false);
  readonly BlockingCollection<string> replies = new BlockingCollection<string>();
  DeckLayout layout;
  volatile bool connected, syncWanted;
  uint boardCrc;
  readonly Dictionary<int, string> sentState = new Dictionary<int, string>();
  int stateAge;
  public volatile string Status = "Chưa nối bo CYD Deck";
  public volatile string LastPress = "";

  internal DeckSync(Link link) {
    this.link = link;
    layout = DeckConfig.Build(DeckConfig.Load());
    new Thread(Worker) { IsBackground = true, Name = "deck-sync" }.Start();
  }

  public DeckLayout Layout { get { lock (gate) return layout; } }
  public bool Connected { get { return connected; } }

  // Bộ phím mới từ trang cài đặt: dựng gói và ảnh rồi gửi xuống bo
  public void Apply(DeckConfigData config) {
    var built = DeckConfig.Build(config);
    lock (gate) layout = built;
    syncWanted = true;
    wake.Set();
  }

  // ---------- dòng log của bo (luồng đọc cổng COM) ----------

  public void OnLine(string text) {
    int at = text.IndexOf("[DECK] ", StringComparison.Ordinal);
    if (at < 0) return;
    string[] parts = text.Substring(at + 7).Split(new[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
    if (parts.Length == 0) return;
    switch (parts[0]) {
      case "chao":  // chao 1 <ảnh có>/<ô> <crc bộ phím> <tên máy>
        if (parts.Length >= 4) {
          uint crc;
          uint.TryParse(parts[3], NumberStyles.HexNumber, null, out crc);
          bool fresh = !connected;
          boardCrc = crc;
          connected = true;
          if (fresh) {
            Log.Agent("Bo CYD Deck: " + parts[2] + " ảnh, bộ phím " + parts[3]);
            syncWanted = true;
            wake.Set();
          }
        }
        break;
      case "bam":  // bam <trang> <phím> <giữ>
        int page, key, hold;
        if (parts.Length >= 4 && int.TryParse(parts[1], out page) && int.TryParse(parts[2], out key) &&
            int.TryParse(parts[3], out hold)) {
          ThreadPool.QueueUserWorkItem(_ => Press(page, key, hold != 0));
        }
        break;
      case "anh":
      case "bo":
        replies.Add(string.Join(" ", parts));
        break;
    }
  }

  public void OnDisconnected() {
    connected = false;
    lock (gate) sentState.Clear();
    Status = "Chưa nối bo CYD Deck";
  }

  // ---------- bấm phím ----------

  void Press(int page, int slot, bool hold) {
    var current = Layout;
    var key = current.KeyAt(page, slot);
    bool ok = false;
    string name = key == null ? "?" : (key.Title.Length > 0 ? key.Title : "phím " + (slot + 1));
    try {
      if (key == null) throw new InvalidOperationException("Không có phím này trong bộ phím hiện tại");
      DeckAction action = hold && key.Hold != null && key.Hold.Type != "none" ? key.Hold : key.Action;
      DeckActions.Run(action);
      ok = true;
      LastPress = DateTime.Now.ToString("HH:mm:ss") + " " + name + ": xong";
      if (key.Kind == "toggle" || key.Live == "volume") {
        Thread.Sleep(60);  // Windows cập nhật trạng thái tắt tiếng
        SendStates(true);
      }
    } catch (Exception ex) {
      LastPress = DateTime.Now.ToString("HH:mm:ss") + " " + name + ": " + ex.Message;
      Log.Agent("Phím " + page + "/" + slot + " (" + name + ") lỗi: " + ex.Message);
    }
    link.Send(Frames.Frame(ACK, new[] { (byte)page, (byte)slot, (byte)(ok ? 1 : 0) }));
  }

  // ---------- mỗi giây ----------

  public void Tick(Stats s) {
    if (!connected) return;
    using (var ms = new MemoryStream())
    using (var w = new BinaryWriter(ms)) {
      w.Write((byte)0);
      Text(w, DateTime.Now.ToString("HH:mm"), 5);
      w.Write((byte)Math.Round(Math.Max(0, Math.Min(100, s.Cpu))));
      w.Write((byte)(s.RamTotalMb > 0 ? Math.Round(s.RamUsedMb * 100.0 / s.RamTotalMb) : 0));
      Text(w, "", 47);  // bài đang phát: chưa có (cần WinRT)
      Text(w, "", 23);
      w.Flush();
      link.Send(Frames.Frame(INFO, ms.ToArray()));
    }
    SendStates(++stateAge >= 5);  // gửi lại cả bộ 5 giây một lần (bo vừa khởi động lại)
  }

  void SendStates(bool all) {
    var current = Layout;
    bool? mic = null, speaker = null;
    int? volume = null;
    bool micRead = false, speakerRead = false, volumeRead = false;
    if (all) stateAge = 0;
    for (int page = 0; page < current.ConfigOf.Length; page++) {
      for (int slot = 0; slot < DeckConfig.KEYS; slot++) {
        var key = current.KeyAt(page, slot);
        if (key == null || (key.Kind != "toggle" && key.Live != "volume")) continue;
        bool on = false;
        if (key.Kind == "toggle" && key.Action != null) {
          if (key.Action.Type == "mic") {
            if (!micRead) { mic = Audio.Muted(true); micRead = true; }
            on = mic == true;
          } else if (key.Action.Type == "volume" && key.Action.Cmd == "mute") {
            if (!speakerRead) { speaker = Audio.Muted(false); speakerRead = true; }
            on = speaker == true;
          }
        }
        string live = "";
        if (key.Live == "volume") {
          if (!volumeRead) { volume = Audio.Percent(); volumeRead = true; }
          if (volume.HasValue) live = (on ? "Tắt · " : "") + volume.Value + "%";
        }
        int id = page * 16 + slot;
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
          Text(w, live, 11);
          w.Flush();
          link.Send(Frames.Frame(STATE, ms.ToArray()));
        }
      }
    }
  }

  // ---------- đồng bộ bộ phím và ảnh ----------

  void Worker() {
    while (true) {
      wake.WaitOne(2000);
      if (!connected || !syncWanted) continue;
      syncWanted = false;
      try {
        if (!Sync()) {
          syncWanted = true;  // thử lại sau vài giây
          Thread.Sleep(3000);
        }
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
      Status = "Đang gửi bộ phím xuống bo…";
      foreach (var frame in current.Frames) link.Send(frame);
      string reply = Reply("bo ", 5000);
      if (reply == null || !reply.EndsWith(" ok")) {
        Status = "Bo không nhận bộ phím (" + (reply ?? "không trả lời") + ")";
        Log.Agent(Status);
        return false;
      }
      boardCrc = current.Crc;
      Log.Agent("Đã gửi bộ phím " + current.Crc.ToString("x8"));
    }
    int index = 0, sent = 0;
    foreach (var pair in current.Images) {
      index++;
      if (!ReferenceEquals(current, Layout)) return true;  // bộ phím vừa đổi: lượt sau gửi bộ mới
      Status = "Đang gửi ảnh " + index + "/" + current.Images.Count + "…";
      int result = Upload(pair.Key, pair.Value);
      if (result < 0) {
        Status = "Gửi ảnh lỗi, sẽ thử lại";
        return false;
      }
      sent += result;
    }
    Status = "Bo đã có đủ " + current.Images.Count + " ảnh" + (sent > 0 ? " (vừa gửi " + sent + ")" : "");
    if (sent > 0) Log.Agent("Đã gửi " + sent + " ảnh phím xuống bo");
    lock (gate) sentState.Clear();
    return true;
  }

  // 1: đã gửi; 0: bo có sẵn; -1: lỗi
  int Upload(uint hash, byte[] px) {
    string id = hash.ToString("x8");
    link.Send(Frames.Frame(IMG_BEGIN, BitConverter.GetBytes(hash)));
    string reply = Reply("anh " + id, 3000);
    if (reply == null || reply.EndsWith(" loi")) return -1;
    if (reply.EndsWith(" co")) return 0;
    for (int offset = 0; offset < px.Length; offset += CHUNK) {
      int length = Math.Min(CHUNK, px.Length - offset);
      var payload = new byte[6 + length];
      BitConverter.GetBytes(hash).CopyTo(payload, 0);
      BitConverter.GetBytes((ushort)offset).CopyTo(payload, 4);
      Buffer.BlockCopy(px, offset, payload, 6, length);
      link.Send(Frames.Frame(IMG_DATA, payload));
      reply = Reply("anh " + id, 3000);
      if (reply == null || reply.EndsWith(" loi")) return -1;
    }
    link.Send(Frames.Frame(IMG_END, BitConverter.GetBytes(hash)));
    reply = Reply("anh " + id, 3000);
    return reply != null && reply.EndsWith(" xong") ? 1 : -1;
  }

  static void Text(BinaryWriter w, string text, int max) {
    byte[] bytes = Encoding.UTF8.GetBytes(text ?? "");
    int length = Math.Min(bytes.Length, max);
    while (length < bytes.Length && length > 0 && (bytes[length] & 0xC0) == 0x80) length--;
    w.Write((byte)length);
    w.Write(bytes, 0, length);
  }
}

}  // namespace CydAgent
