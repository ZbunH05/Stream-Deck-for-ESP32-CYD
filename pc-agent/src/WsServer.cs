// Máy chủ WebSocket tối giản (RFC 6455) trên TcpListener, chỉ nghe ở 127.0.0.1: đường nói chuyện với plugin Stream Deck
// (SDK của Elgato dùng WebSocket JSON) và với trang chạy plugin HTML. Không dùng HttpListener (cần quyền URL ACL).
//
// Chỉ nhận khung văn bản (và ping/close); khung từ client phải có mặt nạ như chuẩn. Header Origin: kết nối từ trình
// duyệt luôn có Origin; chỉ chấp nhận Origin của trang cài đặt (http://127.0.0.1:47831) hoặc không có Origin (tiến
// trình plugin .exe/Node), để trang web lạ mở trong trình duyệt không nói chuyện được với plugin.
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace CydAgent {

sealed class WsConnection {
  readonly TcpClient client;
  readonly NetworkStream stream;
  readonly object writeGate = new object();
  public volatile bool Open = true;
  public string Origin = "";
  public object Tag;  // ElgatoHost gắn: plugin hay Property Inspector nào

  public WsConnection(TcpClient client, NetworkStream stream) {
    this.client = client;
    this.stream = stream;
  }

  public void Send(string text) {
    if (!Open) return;
    byte[] payload = Encoding.UTF8.GetBytes(text);
    var header = new List<byte> { 0x81 };  // FIN + text
    if (payload.Length < 126) {
      header.Add((byte)payload.Length);
    } else if (payload.Length < 65536) {
      header.Add(126);
      header.Add((byte)(payload.Length >> 8));
      header.Add((byte)payload.Length);
    } else {
      header.Add(127);
      for (int i = 7; i >= 0; i--) header.Add((byte)((long)payload.Length >> (8 * i)));
    }
    try {
      lock (writeGate) {
        stream.Write(header.ToArray(), 0, header.Count);
        stream.Write(payload, 0, payload.Length);
      }
    } catch (Exception) {
      Close();
    }
  }

  void SendControl(byte opcode, byte[] payload) {
    try {
      lock (writeGate) {
        stream.WriteByte((byte)(0x80 | opcode));
        stream.WriteByte((byte)payload.Length);
        stream.Write(payload, 0, payload.Length);
      }
    } catch (Exception) {
    }
  }

  public void Close() {
    if (!Open) return;
    Open = false;
    SendControl(0x8, new byte[0]);
    try { client.Close(); } catch (Exception) { }
  }

  static byte[] ReadExactly(NetworkStream s, int count) {
    var buffer = new byte[count];
    int read = 0;
    while (read < count) {
      int n = s.Read(buffer, read, count - read);
      if (n <= 0) throw new IOException("đóng");
      read += n;
    }
    return buffer;
  }

  // Đọc một tin nhắn văn bản trọn vẹn; null khi đóng
  public string Receive() {
    var message = new MemoryStream();
    while (true) {
      byte[] head = ReadExactly(stream, 2);
      bool fin = (head[0] & 0x80) != 0;
      int opcode = head[0] & 0x0F;
      bool masked = (head[1] & 0x80) != 0;
      long length = head[1] & 0x7F;
      if (length == 126) {
        byte[] ext = ReadExactly(stream, 2);
        length = (ext[0] << 8) | ext[1];
      } else if (length == 127) {
        byte[] ext = ReadExactly(stream, 8);
        length = 0;
        for (int i = 0; i < 8; i++) length = (length << 8) | ext[i];
      }
      if (length > 32 << 20) throw new IOException("tin nhắn quá lớn");
      byte[] mask = masked ? ReadExactly(stream, 4) : null;
      byte[] payload = ReadExactly(stream, (int)length);
      if (mask != null) {
        for (int i = 0; i < payload.Length; i++) payload[i] ^= mask[i % 4];
      }
      if (opcode == 0x8) return null;  // close
      if (opcode == 0x9) {             // ping → pong
        SendControl(0xA, payload);
        continue;
      }
      if (opcode == 0xA) continue;
      message.Write(payload, 0, payload.Length);
      if (fin) return Encoding.UTF8.GetString(message.ToArray());
    }
  }
}

sealed class WsServer {
  readonly TcpListener listener;
  readonly Func<string, bool> originAllowed;
  public readonly int Port;
  public Action<WsConnection, string> OnMessage;
  public Action<WsConnection> OnClose;

  public WsServer(Func<string, bool> originAllowed) {
    this.originAllowed = originAllowed;
    listener = new TcpListener(IPAddress.Loopback, 0);  // cổng ngẫu nhiên, như app của Elgato
    listener.Start();
    Port = ((IPEndPoint)listener.LocalEndpoint).Port;
    new Thread(Accept) { IsBackground = true, Name = "ws-accept" }.Start();
  }

  void Accept() {
    while (true) {
      try {
        var client = listener.AcceptTcpClient();
        new Thread(() => Serve(client)) { IsBackground = true, Name = "ws-conn" }.Start();
      } catch (Exception) {
        Thread.Sleep(200);
      }
    }
  }

  void Serve(TcpClient client) {
    WsConnection conn = null;
    try {
      client.NoDelay = true;
      var stream = client.GetStream();
      var head = new StringBuilder();
      int b, newlines = 0;
      while ((b = stream.ReadByte()) >= 0) {  // phần đầu HTTP, kết thúc bằng dòng trống
        head.Append((char)b);
        newlines = b == '\n' ? newlines + 1 : b == '\r' ? newlines : 0;
        if (newlines == 2 || head.Length > 16384) break;
      }
      var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
      foreach (var line in head.ToString().Split(new[] { "\r\n" }, StringSplitOptions.RemoveEmptyEntries)) {
        int colon = line.IndexOf(':');
        if (colon > 0) headers[line.Substring(0, colon).Trim()] = line.Substring(colon + 1).Trim();
      }
      string key, origin;
      headers.TryGetValue("Origin", out origin);
      if (!headers.TryGetValue("Sec-WebSocket-Key", out key) || !originAllowed(origin ?? "")) {
        byte[] deny = Encoding.ASCII.GetBytes("HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\nConnection: close\r\n\r\n");
        stream.Write(deny, 0, deny.Length);
        client.Close();
        return;
      }
      string accept;
      using (var sha = SHA1.Create()) {
        accept = Convert.ToBase64String(sha.ComputeHash(Encoding.ASCII.GetBytes(key + "258EAFA5-E914-47DA-95CA-C5AB0DC85B11")));
      }
      byte[] ok = Encoding.ASCII.GetBytes("HTTP/1.1 101 Switching Protocols\r\nUpgrade: websocket\r\nConnection: Upgrade\r\n" +
                                          "Sec-WebSocket-Accept: " + accept + "\r\n\r\n");
      stream.Write(ok, 0, ok.Length);
      conn = new WsConnection(client, stream) { Origin = origin ?? "" };
      while (true) {
        string text = conn.Receive();
        if (text == null) break;
        try {
          if (OnMessage != null) OnMessage(conn, text);
        } catch (Exception ex) {  // một tin nhắn lạ không được làm đứt kết nối của plugin
          Log.Agent("Plugin: lỗi khi xử lý tin nhắn: " + ex.Message);
        }
      }
    } catch (Exception) {
    } finally {
      if (conn != null) {
        conn.Open = false;
        if (OnClose != null) OnClose(conn);
      }
      try { client.Close(); } catch (Exception) { }
    }
  }
}

}  // namespace CydAgent
