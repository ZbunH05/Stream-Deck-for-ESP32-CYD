// Chữ của agent bằng hai ngôn ngữ (Tiếng Việt, English): trạng thái, thông báo lỗi trả về trang cài đặt, menu khay.
// Ngôn ngữ lưu trong deck.json (DeckConfigData.Lang); lần đầu lấy theo ngôn ngữ hiển thị của Windows. Trang cài đặt có
// bảng chuỗi riêng (web\index.html), bo có bảng riêng (firmware/src/deck/lang.cpp).
using System;
using System.Collections.Generic;
using System.Globalization;

namespace CydAgent {

static class I18n {
  public static volatile string Lang = DefaultLang();

  public static string DefaultLang() {
    return CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "vi" ? "vi" : "en";
  }

  static readonly Dictionary<string, string[]> S = new Dictionary<string, string[]> {
    // { khoá, { Việt, English } }
    {"sync.layout", new[] {"Đang gửi bộ phím xuống bo…", "Sending the layout to the board…"}},
    {"sync.layout_failed", new[] {"Bo không nhận bộ phím", "The board rejected the layout"}},
    {"sync.images", new[] {"Đang gửi ảnh {0}/{1}…", "Sending image {0}/{1}…"}},
    {"sync.image_failed", new[] {"Gửi ảnh lỗi, sẽ thử lại", "Image upload failed, retrying"}},
    {"sync.done", new[] {"Bo đã có đủ {0} ảnh", "The board has all {0} images"}},
    {"sync.old_firmware", new[] {"Firmware trên bo đã cũ: nạp lại firmware CYD Deck", "The board runs old firmware: flash CYD Deck again"}},
    {"press.done", new[] {"xong", "done"}},
    {"live.muted", new[] {"Tắt", "Off"}},
    {"obs.off", new[] {"Chưa nối OBS (bật WebSocket trong OBS: Tools → WebSocket Server Settings)", "OBS is not connected (enable it in OBS: Tools → WebSocket Server Settings)"}},
    {"obs.auth", new[] {"OBS từ chối mật khẩu", "OBS rejected the password"}},
    {"obs.no_config", new[] {"Không thấy cấu hình WebSocket của OBS trên máy này", "No OBS WebSocket settings found on this PC"}},
    {"obs.no_scenes", new[] {"OBS chưa có cảnh nào", "OBS has no scenes"}},
    {"action.none", new[] {"Phím chưa có lệnh", "The key has no action"}},
    {"action.no_program", new[] {"Chưa chọn chương trình", "No program selected"}},
    {"action.no_target", new[] {"Chưa có địa chỉ để mở", "Nothing to open"}},
    {"action.uac_denied", new[] {"Đã từ chối UAC", "UAC was declined"}},
    {"action.cannot_run", new[] {"Không chạy được {0}: {1}", "Cannot run {0}: {1}"}},
    {"action.unknown", new[] {"Không biết lệnh \"{0}\"", "Unknown action \"{0}\""}},
    {"action.lock_failed", new[] {"Không khoá được máy", "Could not lock the PC"}},
    {"action.no_hotkey", new[] {"Chưa có tổ hợp phím", "No key combination"}},
    {"action.bad_key", new[] {"Không hiểu phím \"{0}\"", "Unknown key \"{0}\""}},
    {"action.input_blocked", new[] {"Windows chặn phím ảo (cửa sổ đang mở chạy bằng quyền admin?)", "Windows blocked the keystrokes (is the active window elevated?)"}},
    {"action.no_mic", new[] {"Không có mic", "No microphone"}},
    {"action.no_speaker", new[] {"Không có loa", "No speaker"}},
    {"web.saved", new[] {"Đã lưu", "Saved"}},
    {"web.ran", new[] {"Đã chạy", "Done"}},
    {"web.image_small", new[] {"Ảnh quá nhỏ", "Image is too small"}},
    {"admin.already", new[] {"Lệnh admin đã đăng ký sẵn.", "Admin actions are already registered."}},
    {"admin.failed", new[] {"Không đăng ký được lệnh admin (xem logs\\agent.log). Phím admin sẽ hỏi UAC mỗi lần.", "Could not register admin actions (see logs\\agent.log). Admin keys will ask for UAC every time."}},
    {"admin.declined", new[] {"Bạn đã từ chối UAC: phím admin sẽ hỏi UAC mỗi lần bấm.", "UAC was declined: admin keys will ask for UAC on every press."}},
    {"admin.done", new[] {"Đã đăng ký {0} lệnh admin: bấm phím là chạy, không hỏi UAC.", "Registered {0} admin actions: they run without a UAC prompt."}},
    {"plugin.installed", new[] {"Đã cài plugin {0}", "Installed plugin {0}"}},
    {"plugin.removed", new[] {"Đã gỡ plugin {0}", "Removed plugin {0}"}},
    {"plugin.bad_file", new[] {"Không phải plugin Stream Deck (thiếu manifest.json)", "Not a Stream Deck plugin (manifest.json missing)"}},
    {"plugin.protected", new[] {"Plugin này được Elgato Marketplace mã hoá: chỉ chạy trong phần mềm Stream Deck của Elgato", "This plugin is encrypted by the Elgato Marketplace: it only runs in Elgato's Stream Deck app"}},
    {"plugin.no_node", new[] {"Plugin cần Node.js: cài Node.js rồi khởi động lại agent", "This plugin needs Node.js: install Node.js and restart the agent"}},
    {"plugin.unsupported_os", new[] {"Plugin không chạy trên Windows", "This plugin does not support Windows"}},
    {"menu.settings", new[] {"Cài đặt Deck…", "Deck settings…"}},
    {"menu.pause", new[] {"Tạm nhả cổng COM 10 phút (để nạp firmware)", "Release the COM port for 10 minutes (to flash firmware)"}},
    {"menu.resume", new[] {"Nối lại ngay", "Reconnect now"}},
    {"menu.logs", new[] {"Mở thư mục log", "Open the log folder"}},
    {"menu.quit", new[] {"Thoát", "Quit"}},
    {"link.searching", new[] {"Đang tìm bo CYD…", "Looking for the CYD board…"}},
    {"link.released", new[] {"Đã nhả cổng COM (đang nạp hay đọc log)", "COM port released (flashing or reading logs)"}},
    {"link.hello", new[] {"Đang chào bo ở {0} ({1} baud)…", "Greeting the board on {0} ({1} baud)…"}},
    {"link.busy", new[] {"{0} đang bận ({1})", "{0} is busy ({1})"}},
    {"link.none", new[] {"Không thấy bo CYD (cắm cáp USB)", "No CYD board found (plug in the USB cable)"}},
    {"link.silent", new[] {"{0} không trả lời (firmware cũ?)", "{0} does not answer (old firmware?)"}},
    {"link.deck", new[] {"Đã nối bo CYD Deck qua {0}", "CYD Deck connected on {0}"}},
    {"link.board", new[] {"Đã nối bo CYD qua {0}", "CYD board connected on {0}"}},
    {"link.lost", new[] {"Mất kết nối, đang tìm lại…", "Connection lost, searching again…"}},
    {"web.no_key", new[] {"Thiếu phím", "No key given"}},
    {"web.obs_saved", new[] {"Đã lưu cài đặt OBS", "OBS settings saved"}},
    {"web.obs_local", new[] {"Đã lấy cổng và mật khẩu từ OBS trên máy này", "Port and password taken from OBS on this PC"}},
    {"web.obs_local_off", new[] {"OBS chưa bật WebSocket server: trong OBS chọn Tools → WebSocket Server Settings → Enable WebSocket server", "The OBS WebSocket server is off: in OBS open Tools → WebSocket Server Settings → Enable WebSocket server"}},
    {"web.obs_not_local", new[] {"Chỉ lấy được mật khẩu khi OBS chạy trên máy này (địa chỉ 127.0.0.1)", "The password can only be read when OBS runs on this PC (address 127.0.0.1)"}},
    {"plugin.enabled", new[] {"Đã bật plugin {0}", "Enabled plugin {0}"}},
    {"plugin.disabled", new[] {"Đã tắt plugin {0}", "Disabled plugin {0}"}},
    {"plugin.restarted", new[] {"Đã chạy lại plugin {0}", "Restarted plugin {0}"}},
    {"plugin.crashed", new[] {"Plugin chết liên tục: xem log rồi bấm Chạy lại", "The plugin keeps crashing: check the log, then press Restart"}},
    {"plugin.none", new[] {"Không có plugin này", "No such plugin"}},
    {"pick.title", new[] {"Chọn file cho CYD Deck", "Choose a file for CYD Deck"}},
    {"pick.programs", new[] {"Chương trình", "Programs"}},
    {"pick.images", new[] {"Ảnh", "Images"}},
    {"pick.plugins", new[] {"Plugin Stream Deck", "Stream Deck plugins"}},
    {"pick.all", new[] {"Mọi file", "All files"}},
  };

  public static string T(string key) {
    string[] pair;
    if (!S.TryGetValue(key, out pair)) return key;
    return Lang == "vi" ? pair[0] : pair[1];
  }
}

}  // namespace CydAgent
