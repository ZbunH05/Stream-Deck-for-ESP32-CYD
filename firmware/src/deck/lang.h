// Chữ trên bo bằng hai ngôn ngữ: Tiếng Việt và English. Ngôn ngữ do CydAgent gửi trong gói LAYOUT_BEGIN (người dùng
// chọn trong trang cài đặt), lưu trong NVS ("cyd"/"lang") để bộ phím mẫu lúc chưa nối PC cũng đúng ngôn ngữ.
// Bo chỉ có font Be Vietnam Pro: đủ chữ Latin và tiếng Việt.
#pragma once

#include <stdint.h>

enum class Lang : uint8_t { Vi = 0, En = 1 };

enum class Str : uint8_t {
  WaitTitle,     // Đang chờ máy tính…
  WaitSub,       // Mở CydAgent trên PC
  NoPc,          // viên thông báo khi bấm phím lệnh lúc chưa nối
  Back,          // Quay ra
  FolderFmt,     // Thư mục %s
  Playing,       // Đang phát (khi app không cho biết nghệ sĩ)
  Profile,       // Hồ sơ: %s
  ObsRec,        // OBS · đang ghi
  ObsLive,       // OBS · đang live
  ObsIdle,       // OBS · đang chờ
  ObsPaused,     // OBS · tạm dừng ghi
  ObsOff,        // OBS chưa nối
  ObsOffSub,     // Bật WebSocket trong OBS
  SceneFmt,      // Cảnh: %s
  Drop,          // Rớt
  Count
};

Lang lang();
void lang_set(Lang value);  // lưu NVS nếu khác
void lang_load();           // đọc NVS, gọi trước khi dựng bộ phím mẫu
const char *tr(Str id);
