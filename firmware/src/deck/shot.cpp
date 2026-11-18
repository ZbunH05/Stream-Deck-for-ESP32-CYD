// Xem shot.h. Như cyd.cmd shot của CYD-EEZ (perf.cpp): mở lần lượt từng cảnh với dữ liệu mẫu cố định, đợi hoạt cảnh
// xong hẳn rồi đọc lại 320×240 điểm ảnh từ bộ nhớ màn (cần -DSPI_READ_FREQUENCY=6000000), in theo từng dòng:
//   [SHOT] tong <số ảnh>   [SHOT] begin <tên> 0   [SHOT] <y> <n>:<màu> ...   [SHOT] end <tên>   ...   [SHOT] xong
// tools/cyd.ps1 đọc các dòng này ra PNG. Bo bỏ qua cảm ứng suốt lúc chụp (cyd_hw.cpp).
#include "shot.h"

#if defined(CYD_PERF) && CYD_PERF && defined(CYD_PERF_DEMO) && CYD_PERF_DEMO == 3

#include <Arduino.h>
#include <string.h>

#include "../app/motion.h"
#include "../cyd/cyd_hw.h"
#include "deck.h"
#include "sample.h"

namespace {

const char *const NAMES[] = {"trang1", "trang2", "obs", "hoso", "cho-pc"};
constexpr int COUNT = sizeof NAMES / sizeof NAMES[0];
constexpr uint32_t START_MS = 4000;

int step = -1;
uint32_t opened_ms = 0, quiet_since = 0;
bool done = false;
char row[3072];

void dump(const char *name) {
  Serial.printf("\n[SHOT] begin %s 0\n", name);
  for (int y = 0; y < CYD_SCREEN_H; y++) {
    int len = snprintf(row, sizeof row, "[SHOT] %d", y);
    uint16_t color = cyd_read_pixel(0, y);
    int run = 1;
    for (int x = 1; x < CYD_SCREEN_W; x++) {
      const uint16_t next = cyd_read_pixel(x, y);
      if (next == color) {
        run++;
        continue;
      }
      len += snprintf(row + len, sizeof row - len, " %x:%x", run, color);
      color = next;
      run = 1;
    }
    len += snprintf(row + len, sizeof row - len, " %x:%x\n", run, color);
    Serial.write(reinterpret_cast<const uint8_t *>(row), len);
  }
  Serial.printf("[SHOT] end %s\n", name);
}

void open(int index) {
  DeckProfile &p = deck_profile();
  DeckInfo &info = deck_info();
  switch (index) {
    case 0:
      deck_go(0, false);
      deck_feedback(0, 4, true, true);  // phím Mic vừa bấm: PC báo xong
      break;
    case 1:
      deck_go(1, true);
      break;
    case 2:
      deck_go(2, true);
      break;
    case 3:  // PC chuyển app: hồ sơ đổi, viên thông báo ở đỉnh màn
      deck_go(0, true);
      strlcpy(p.name, "Chỉnh video", sizeof p.name);
      deck_profile_switched();
      break;
    case 4:
      motion_island_hide(false);
      strlcpy(p.name, "Mặc định", sizeof p.name);
      info.online = false;
      strlcpy(info.clock, "--:--", sizeof info.clock);
      deck_refresh();
      break;
  }
}

}  // namespace

void shot_loop() {
  const uint32_t now = millis();
  if (done || now < START_MS) return;
  lv_display_trigger_activity(nullptr);
  if (step < 0) {
    Serial.printf("\n[SHOT] tong %d\n", COUNT);
    sample_load();  // ảnh chụp luôn dùng bộ phím mẫu, dù bo đã có bộ phím từ PC
    sample_demo_state();
    step = 0;
    opened_ms = now;
    open(0);
    return;
  }
  if (motion_pushing() || lv_anim_count_running() > 0) {
    quiet_since = 0;
    return;
  }
  if (!quiet_since) quiet_since = now;
  if (now - quiet_since < 700 || now - opened_ms < 1500) return;
  dump(NAMES[step]);
  if (++step == COUNT) {
    Serial.println("[SHOT] xong");
    done = true;
    return;
  }
  opened_ms = millis();
  quiet_since = 0;
  open(step);
}

#endif
