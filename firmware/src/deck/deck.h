// CYD Deck: bộ phím kiểu Stream Deck (Elgato) trên màn 320×240, bố cục kiểu Stream Deck Neo: 8 phím 72×72 (4×2)
// ở trên, thanh tin 306×66 ở dưới (giờ, nhạc đang phát hay OBS, CPU/RAM; hai mép lật trang).
//
// Dữ liệu: một hồ sơ (DeckProfile) gồm các trang. Trang chính đứng trước (lật bằng mũi tên hay vuốt ngang), thư mục
// đứng sau (mở bằng phím thư mục, phím ‹ ở ô đầu để quay ra). Ảnh phím là lv_image_dsc_t RGB565 đảo byte: ảnh mẫu
// nhúng trong firmware (sample_images.c), kho ảnh trong flash (store.h) hay ảnh plugin trong RAM (ramimg.h).
//
// Giao diện có đúng hai màn LVGL dùng luân phiên: trang đang hiện và trang sắp trượt vào. Đổi trang là đổ nội dung trang
// mới vào màn đang ẩn rồi trượt bằng cuộn phần cứng (motion_push), nên số đối tượng LVGL không tăng theo số trang.
// Chỉ gọi từ luồng giao diện (loop).
#pragma once

#include <lvgl.h>
#include <stdint.h>

constexpr int DECK_KEYS = 8;
constexpr int DECK_MAX_PAGES = 16;
constexpr int DECK_TITLE = 24;  // byte UTF-8 kể cả '\0'

enum class KeyKind : uint8_t {
  Empty,   // ô trống: phím tối, không bấm được
  Action,  // bấm là PC chạy lệnh (viền trắng chờ PC trả lời)
  Toggle,  // như Action, ảnh và nhãn theo trạng thái bật/tắt PC báo về
  Folder,  // mở trang `target`
  Back,    // quay ra trang cha (bo tự vẽ phím này: ‹ Quay ra)
  Plugin,  // hành động của plugin Stream Deck: PC nhận nhấn/nhả, plugin tự đổi ảnh, nhãn, báo ✓/⚠
};

enum class BarMode : uint8_t { Info, Obs };

struct DeckKey {
  KeyKind kind = KeyKind::Empty;
  char title[DECK_TITLE] = "";
  char title_on[DECK_TITLE] = "";  // nhãn khi Toggle đang bật (trống: dùng title)
  const lv_image_dsc_t *image = nullptr;
  const lv_image_dsc_t *image_on = nullptr;  // ảnh khi Toggle đang bật (null: dùng image)
  uint32_t image_hash = 0;     // ảnh trong kho flash (store.h), dùng khi image là null
  uint32_t image_on_hash = 0;
  uint8_t target = 0;  // Folder: số trang mở ra
  bool on = false;     // Toggle: PC báo đang bật
  char live[DECK_TITLE] = "";  // nhãn PC đặt lúc chạy (âm lượng 62 %, setTitle của plugin...), trống: dùng nhãn
};

struct DeckPage {
  char name[DECK_TITLE] = "";
  int8_t parent = -1;  // thư mục: trang mở nó; trang chính: -1
  BarMode bar = BarMode::Info;
  DeckKey keys[DECK_KEYS];
};

struct DeckProfile {
  char name[DECK_TITLE] = "";
  uint8_t page_count = 0;  // tổng số trang
  uint8_t top_pages = 0;   // số trang chính (đứng đầu mảng)
  DeckPage pages[DECK_MAX_PAGES];
};

// Thanh tin. PC gửi khi có gì đổi và ít nhất 2 giây một lần; 4 giây không có gì thì coi như mất nối, thanh báo
// "Đang chờ máy tính…" và phím mờ đi.
struct DeckInfo {
  bool online = false;
  char clock[6] = "--:--";
  uint8_t cpu = 0, ram = 0;  // %
  // nhạc đang phát (Windows: GlobalSystemMediaTransportControls)
  bool playing = false;
  char media_title[48] = "";  // trống: không có gì đang phát
  char media_sub[48] = "";    // "nghệ sĩ · app"
  uint16_t progress = 0xFFFF;  // ‰, 0xFFFF: không biết
  // OBS (thanh ở trang có bar = Obs)
  bool obs_connected = false, obs_rec = false, obs_live = false, obs_paused = false;
  char obs_time[12] = "";
  char obs_scene[DECK_TITLE] = "";
  uint16_t obs_dropped = 0;
  uint8_t obs_cpu = 0;
};

DeckProfile &deck_profile();  // sửa xong gọi deck_refresh()
DeckInfo &deck_info();        // sửa xong gọi deck_refresh_info()

void deck_init();  // sau motion_init(): dựng hai màn, chưa hiện trang nào
void deck_tick();  // mỗi vòng loop(): ẩn dấu ✓/⚠ đã hết giờ

void deck_go(int page, bool animate);  // mở trang (animate: trượt, tiến hay lùi tuỳ quan hệ hai trang)
int deck_page();
void deck_refresh();       // đổ lại trang đang hiện (sau khi đổi hồ sơ, ảnh, trạng thái phím)
void deck_refresh_info();  // chỉ thanh tin và độ mờ của phím (chỉ đặt lại những gì đổi)
void deck_refresh_key(int key);  // một phím của trang đang hiện (trạng thái bật/tắt, nhãn, ảnh RAM)

// PC báo kết quả lệnh của phím: dấu ✓ xanh hay ⚠ cam ở góc phím ~1,2 s và viền cùng màu. hold: giữ luôn (ảnh chụp)
void deck_feedback(int page, int key, bool ok, bool hold = false);
// Đổi hồ sơ (PC chuyển app): viên thông báo "Hồ sơ: <tên>" ở đỉnh màn
void deck_profile_switched();
