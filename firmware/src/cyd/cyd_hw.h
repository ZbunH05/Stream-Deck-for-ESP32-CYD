// Lớp phần cứng của CYD (ESP32-2432S028R): màn hình, cảm ứng và LVGL.
// main.cpp chỉ cần gọi cyd_init() một lần và cyd_loop() trong loop().
#pragma once

#include <lvgl.h>

// Kích thước logic (màn nằm ngang); toạ độ trong src/ui/screens.cpp tính theo đúng cỡ này
constexpr int CYD_SCREEN_W = 320;
constexpr int CYD_SCREEN_H = 240;

// Bật Serial, nhận dạng chip màn hình, khởi tạo màn, cảm ứng và LVGL.
void cyd_init();

// Cho LVGL vẽ lại và đọc cảm ứng. Gọi liên tục trong loop().
void cyd_loop();

// Lật màn hình 180° (cả hình và cảm ứng). Lưu trong bo, mặc định theo CYD_FLIP_DEFAULT trong platformio.ini.
bool cyd_flipped();
void cyd_set_flipped(bool flipped);

// Đảo màu (lệnh INVON/INVOFF của chip màn). Màn IPS mã 81 81 B3 cần bật. Mặc định tự nhận theo mã màn; đổi ở trang
// Cài đặt thì lưu trong bo (NVS "cyd"/"inv") và lần sau khởi động dùng lựa chọn đó.
bool cyd_inverted();
void cyd_set_inverted(bool inverted);

// Cuộn ngang bằng phần cứng, cho hoạt cảnh chuyển trang (app/motion.cpp). Chip màn chỉ cuộn theo dòng bộ nhớ, mà
// màn nằm ngang thì dòng bộ nhớ là cột trên màn, nên dời cả ảnh sang ngang chỉ tốn một lệnh (0x37, VSCRSADD).
// Ảnh dời sang trái `offset` px, vòng tròn 320 px: cột x của bộ nhớ hiện ở chỗ (x − offset) mod 320.
// Lệnh được gửi ngay sau lần đẩy hình cuối của khung LVGL kế tiếp, cùng lúc với dải cột vừa vẽ.
bool cyd_scroll_supported();  // false với env cyd2usb (ST7789): chưa thử chiều cuộn trên chip đó
void cyd_scroll_to(int32_t offset);
void cyd_scroll_apply();  // gửi ngay lệnh cuộn đang chờ, khi không còn khung nào để vẽ

// Bỏ qua cảm ứng (trong lúc chuyển trang, như iOS): LVGL thấy như không ai chạm
void cyd_input_block(bool blocked);

// Điểm chạm thô mới nhất (toạ độ màn, đã tính lật màn), đọc được cả khi LVGL đang bị bỏ qua cảm ứng: để trang chạy
// theo ngón tay khi vuốt từ mép trái (view.cpp). Cập nhật mỗi lần LVGL đọc cảm ứng (20 ms).
struct CydTouch {
  bool pressed;
  int16_t x, y;              // điểm hiện tại
  int16_t start_x, start_y;  // điểm đặt ngón tay xuống của lần chạm này
  uint32_t start_ms, ms;     // lúc đặt ngón tay, lúc đọc
};
void cyd_touch_state(CydTouch *out);

// Đèn LED RGB ở mặt sau bo (chân 4, 16, 17)
enum LedColor : uint8_t { LED_RED = 1, LED_GREEN = 2, LED_BLUE = 4 };
void cyd_led_flash(uint8_t color, int times);  // nháy không chặn, do cyd_loop() điều khiển

#if CYD_PERF
// Đọc lại màu một điểm ảnh (RGB565) từ bộ nhớ màn hình qua SPI: bản đo hiệu năng dùng để tự kiểm tra màu.
uint16_t cyd_read_pixel(int32_t x, int32_t y);
#endif
