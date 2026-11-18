// CYD Deck: Stream Deck trên màn CYD (ESP32-2432S028R), nối PC qua cáp USB.
//
//   src/cyd/   phần cứng: màn hình, cảm ứng, LED, cuộn phần cứng (chép từ CYD-EEZ)
//   src/app/   hiệu ứng (motion, chép từ CYD-EEZ), bảng màu tối, font icon
//   src/ui/    font
//   src/deck/  bộ phím: giao diện (deck), bộ phím mẫu (sample), nối PC (link), chụp màn hình (shot)

#include <Arduino.h>

#include "app/motion.h"
#include "cyd/cyd_hw.h"
#include "deck/deck.h"
#include "deck/link.h"
#include "deck/perf.h"
#include "deck/sample.h"
#include "deck/shot.h"
#include "deck/store.h"

void setup() {
  cyd_init();     // Serial, màn hình, cảm ứng, LVGL
  motion_init();  // hoạt cảnh, kể cả kiểu "lún khi nhấn" mà phím gắn vào lúc dựng
  deck_init();    // hai màn dùng luân phiên
  store_init();   // kho ảnh phím trong flash (phân vùng deckimg)
  link_init();    // bộ phím đã lưu từ CydAgent; chưa có thì bộ phím mẫu
  deck_go(0, false);
  Serial.println("[APP] San sang");
}

void loop() {
  cyd_loop();     // LVGL vẽ lại, đọc cảm ứng
  motion_tick();  // bước kế của trang đang trượt
  link_poll();    // byte từ PC qua cáp USB
  deck_tick();    // ẩn dấu ✓/⚠ đã hết giờ
  shot_loop();    // chỉ có trong bản chụp màn hình
  perf_loop();    // chỉ có trong bản đo hiệu năng (perf.h)
}
