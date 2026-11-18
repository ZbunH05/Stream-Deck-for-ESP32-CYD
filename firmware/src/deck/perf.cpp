// Đo hiệu năng hiệu ứng của Deck (build -DCYD_PERF=1 -DCYD_PERF_DEMO=4). Sau 5 giây bo tự chạy từng cảnh trên phím 2
// của trang đang hiện, đo số khung LVGL vẽ và thời gian vẽ mỗi khung, rồi in:
//   [PERF] <cảnh>: N khung / T ms = F khung/giây | vẽ tb A ms, max B ms | đẩy C điểm ảnh/khung
// Khung = một lần LVGL vẽ (RENDER_START → RENDER_READY). "khung/giây" là số khung chia thời gian cảnh: hoạt cảnh mượt
// cần ~60 (LV_DEF_REFR_PERIOD 16 ms). Cảnh lặp 3 lượt. Không cần PC, không cần chạm.
#include "perf.h"

#if CYD_PERF && defined(CYD_PERF_DEMO) && CYD_PERF_DEMO == 4

#include <Arduino.h>
#include <esp_timer.h>

#include "../app/motion.h"
#include "deck.h"

lv_obj_t *deck_demo_key(int slot);
void deck_demo_ripple(int slot, int32_t x, int32_t y);
void deck_demo_ring(int slot, int mode);

namespace {

constexpr int SLOT = 1;
uint32_t now_us() { return (uint32_t)esp_timer_get_time(); }

struct Meter {
  const char *name = nullptr;
  uint32_t t0 = 0, frames = 0, sum_us = 0, max_us = 0, px = 0, start_us = 0;
  uint32_t last_start = 0, gap_sum = 0, gaps = 0, gap_max = 0;  // khoảng giữa hai khung liền nhau (< 100 ms)
  uint32_t frame_px = 0;
} m;

void on_display(lv_event_t *e) {
  const lv_event_code_t code = lv_event_get_code(e);
  if (!m.name) return;
  if (code == LV_EVENT_RENDER_START) {
    m.start_us = now_us();
    m.frame_px = 0;
    if (m.last_start) {
      const uint32_t gap = m.start_us - m.last_start;
      if (gap < 100000) {
        m.gap_sum += gap;
        m.gaps++;
        if (gap > m.gap_max) m.gap_max = gap;
      }
    }
    m.last_start = m.start_us;
  } else if (code == LV_EVENT_RENDER_READY && m.start_us) {
    const uint32_t d = now_us() - m.start_us;
    m.frames++;
    m.sum_us += d;
    if (d > m.max_us) m.max_us = d;
    if (d > 20000) Serial.printf("[PERF]   khung cham %.1f ms, day %lu diem anh\n", d / 1000.0f, (unsigned long)m.frame_px);
    m.start_us = 0;
  } else if (code == LV_EVENT_FLUSH_START) {
    const lv_area_t *a = static_cast<const lv_area_t *>(lv_event_get_param(e));
    if (a) {
      m.px += lv_area_get_size(a);
      m.frame_px += lv_area_get_size(a);
    }
  }
}

void begin(const char *name) {
  m = Meter{};
  m.name = name;
  m.t0 = millis();
}

void end() {
  const uint32_t ms = millis() - m.t0;
  const uint32_t f = m.frames ? m.frames : 1;
  const float gap = m.gaps ? m.gap_sum / 1000.0f / m.gaps : 0;
  Serial.printf("[PERF] %s: %lu khung / %lu ms | luc chay %.1f khung/giay (cach tb %.1f ms, max %.1f ms) | ve tb %.1f ms, "
                "max %.1f ms | day %lu diem anh/khung\n",
                m.name, (unsigned long)m.frames, (unsigned long)ms, gap > 0 ? 1000.0f / gap : 0.0f, gap,
                m.gap_max / 1000.0f, m.sum_us / 1000.0f / f, m.max_us / 1000.0f, (unsigned long)(m.px / f));
  m.name = nullptr;
}

// Kịch bản: mỗi bước {lúc bắt đầu tính từ đầu lượt (ms), việc}
enum class Op : uint8_t { Begin, End, Press, Release, RingWait, RingOk, PageNext, PageBack, Info };
struct Step {
  uint32_t at;
  Op op;
  const char *name;
};
const Step STEPS[] = {
    {0, Op::Begin, "cham nhanh (gon song + lun + nha)"},
    {0, Op::Press, nullptr},
    {120, Op::Release, nullptr},
    {900, Op::End, nullptr},
    {1300, Op::Begin, "giu 0,7 s roi nha"},
    {1300, Op::Press, nullptr},
    {2000, Op::Release, nullptr},
    {2700, Op::End, nullptr},
    {3100, Op::Begin, "vien cho PC (nhip sang/mo)"},
    {3100, Op::RingWait, nullptr},
    {4300, Op::End, nullptr},
    {4300, Op::Begin, "vien ket qua (xanh roi tat)"},
    {4300, Op::RingOk, nullptr},
    {5700, Op::End, nullptr},
    {6100, Op::Begin, "lat trang (cuon phan cung, de so)"},
    {6100, Op::PageNext, nullptr},
    {6800, Op::End, nullptr},
    {7000, Op::PageBack, nullptr},
    // Đứng yên, PC gửi thanh tin mỗi giây (giờ, CPU, RAM, tiến độ bài hát đổi): chỉ nhãn số và vạch tiến độ được vẽ
    // lại, không phải 8 phím (trước 19/11/2026 mỗi gói thanh tin làm bo vẽ lại cả lưới phím)
    {7600, Op::Begin, "dung yen, thanh tin moi giay (5 goi)"},
    {7600, Op::Info, nullptr},
    {8600, Op::Info, nullptr},
    {9600, Op::Info, nullptr},
    {10600, Op::Info, nullptr},
    {11600, Op::Info, nullptr},
    {12500, Op::End, nullptr},
};
constexpr uint32_t ROUND_MS = 13000, START_MS = 5000;
constexpr int ROUNDS = 3;

size_t step = 0;
int round_no = 0;
uint32_t round_t0 = 0;
bool attached = false;

}  // namespace

void perf_loop() {
  if (!attached) {
    lv_display_t *disp = lv_display_get_default();
    lv_display_add_event_cb(disp, on_display, LV_EVENT_ALL, nullptr);
    attached = true;
    Serial.println("[PERF] Bat do hieu ung Deck");
  }
  const uint32_t now = millis();
  if (round_no >= ROUNDS || now < START_MS) return;
  if (!round_t0) {
    round_t0 = now;
    deck_info().online = true;
    deck_go(0, false);
  }
  lv_display_trigger_activity(nullptr);
  while (step < sizeof STEPS / sizeof STEPS[0] && now - round_t0 >= STEPS[step].at) {
    const Step &s = STEPS[step++];
    lv_obj_t *key = deck_demo_key(SLOT);
    switch (s.op) {
      case Op::Begin: begin(s.name); break;
      case Op::End: end(); break;
      case Op::Press:
        lv_obj_add_state(key, LV_STATE_PRESSED);
        deck_demo_ripple(SLOT, 20, 22);
        break;
      case Op::Release: lv_obj_remove_state(key, LV_STATE_PRESSED); break;
      case Op::RingWait: deck_demo_ring(SLOT, 0); break;
      case Op::RingOk: deck_demo_ring(SLOT, 1); break;
      case Op::PageNext: deck_go(1, true); break;
      case Op::PageBack: deck_go(0, true); break;
      case Op::Info: {
        DeckInfo &info = deck_info();
        info.online = true;
        info.cpu = (uint8_t)((info.cpu + 7) % 100);
        info.ram = (uint8_t)(40 + info.cpu % 5);
        info.progress = (uint16_t)((info.progress > 1000 ? 0 : info.progress) + 12);
        strlcpy(info.media_title, "Lofi chill mix", sizeof info.media_title);
        strlcpy(info.media_sub, "Lofi Girl · YouTube", sizeof info.media_sub);
        info.playing = true;
        deck_refresh_info();
        break;
      }
    }
  }
  if (now - round_t0 >= ROUND_MS) {
    step = 0;
    round_t0 = 0;
    if (++round_no == ROUNDS) Serial.printf("[PERF] xong, heap trong %lu KB\n", (unsigned long)(ESP.getFreeHeap() >> 10));
  }
}

#endif
