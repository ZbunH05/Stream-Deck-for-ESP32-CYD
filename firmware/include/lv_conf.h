/**
 * lv_conf.h: cấu hình LVGL 9.5 cho CYD (ESP32-2432S028R, ESP32-WROOM-32, không có PSRAM).
 *
 * Chỉ ghi những mục khác mặc định. Mọi mục không ghi ở đây LVGL tự lấy giá trị
 * mặc định trong .pio/libdeps/<env>/lvgl/src/lv_conf_internal.h.
 */
#ifndef LV_CONF_H
#define LV_CONF_H

/* Màu 16-bit RGB565, đúng định dạng của màn ILI9341/ST7789 */
#define LV_COLOR_DEPTH 16

/* Bộ nhớ: dùng malloc của ESP32 thay cho vùng nhớ tĩnh 64 KB */
#define LV_USE_STDLIB_MALLOC LV_STDLIB_CLIB

/* LVGL chạy trong loop() của Arduino, không cần RTOS riêng */
#define LV_USE_OS LV_OS_NONE

/* Chu kỳ vẽ lại, chạy hoạt cảnh và đọc cảm ứng (ms). 16 ms ≈ 60 lần/giây, cùng nhịp với bước trượt trang
 * (motion.cpp): trang chạy theo ngón tay mượt hơn so với 20 ms trước 17/11/2026. */
#define LV_DEF_REFR_PERIOD 16

/* Không bật LV_OBJ_STYLE_CACHE: đo trên bo (14/11/2026) nó làm vẽ cả màn chậm thêm 5–9 %. */

/* Mật độ điểm ảnh ước lượng của màn 2.8" 320x240 */
#define LV_DPI_DEF 130

/* Không dùng theme mặc định: giao diện tự có style dùng chung (src/ui/styles.cpp). Theme mặc định gắn thêm 3–9 style
 * vào mỗi đối tượng (tốn heap, LVGL tra thuộc tính chậm hơn khi vẽ); phần nó góp vào giao diện đã chép sang
 * styles.cpp. */
#define LV_USE_THEME_DEFAULT 0
#define LV_USE_THEME_SIMPLE 0 /* tắt cả hai theme dự phòng, kẻo LVGL dùng chúng thay theme mặc định */
#define LV_USE_THEME_MONO 0

/* Nhãn không cần chọn chữ, cũng không có chữ dài cả nghìn dòng: bỏ hai phần này bớt 20 byte mỗi nhãn (~250 nhãn) */
#define LV_LABEL_TEXT_SELECTION 0
#define LV_LABEL_LONG_TXT_HINT 0

/* Các hàm vẽ "nóng" của LVGL (pha màu, tô nền, vẽ chữ, mặt nạ góc bo) chạy từ IRAM thay vì đọc lệnh qua cache flash
 * (32 KB, dễ trượt khi một khung vẽ cả chữ, thẻ, vòng cung). LVGL đánh dấu các hàm đó bằng LV_ATTRIBUTE_FAST_MEM. */
#if !defined(__ASSEMBLER__) && !defined(CYD_NO_IRAM) /* -DCYD_NO_IRAM=1: bản so A/B (cyd.cmd shot -ExtraFlags) */
#include "esp_attr.h"
#define LV_ATTRIBUTE_FAST_MEM IRAM_ATTR
#endif

/* Chỉ biên dịch đường vẽ cho định dạng màu giao diện cần, để các hàm vẽ nóng ở trên vừa IRAM (còn trống rất ít).
 * Cần (đã gặp 17/11/2026 khi so ảnh A/B bằng cyd.cmd shot, tắt là mất hình):
 * - RGB565_SWAPPED: bộ đệm vẽ của màn;
 * - RGB888: LVGL vẽ dải chuyển sắc qua một dòng màu RGB888 (khối giờ xanh, thanh nhiệt độ);
 * - ARGB8888: phần chỉ số của thanh bo góc được vẽ qua một lớp ARGB8888 (thanh nhiệt độ 7 ngày, thanh Pomodoro).
 * Không cần: RGB565 thường (màn dùng bản đảo byte, giao diện không có ảnh) và các định dạng còn lại. */
#ifndef CYD_NO_IRAM
#define LV_DRAW_SW_SUPPORT_RGB565 0
#define LV_DRAW_SW_SUPPORT_RGB565A8 0
#define LV_DRAW_SW_SUPPORT_XRGB8888 0
#define LV_DRAW_SW_SUPPORT_ARGB8888_PREMULTIPLIED 0
#define LV_DRAW_SW_SUPPORT_L8 0
#define LV_DRAW_SW_SUPPORT_AL88 0
#define LV_DRAW_SW_SUPPORT_A8 0
#define LV_DRAW_SW_SUPPORT_I1 0
#endif

/* Nhớ mặt nạ góc bo của 16 bán kính thay vì 4: giao diện dùng ~13 bán kính khác nhau trên hai trang cạnh nhau (thẻ 16,
 * chấm 10/11, vòng cung 13..78...), nhớ 4 thì mỗi dải vẽ lại phải tính lại. Mỗi mặt nạ chỉ vài trăm byte. */
#define LV_DRAW_SW_CIRCLE_CACHE_SIZE 16

/* In cảnh báo/lỗi của LVGL ra Serial (src/cyd/cyd_hw.cpp đăng ký hàm in) */
#define LV_USE_LOG 1
#define LV_LOG_LEVEL LV_LOG_LEVEL_WARN
#define LV_LOG_PRINTF 0

/* Font mặc định (chỉ dùng cho dòng báo sai loại màn của cyd_hw.cpp; giao diện dùng font riêng ở src/ui/fonts) */
#define LV_FONT_MONTSERRAT_14 1
#define LV_FONT_DEFAULT &lv_font_montserrat_14

/* Không biên dịch ví dụ và demo của LVGL cho build nhanh hơn */
#define LV_BUILD_EXAMPLES 0
#define LV_BUILD_DEMOS 0

#endif /* LV_CONF_H */
