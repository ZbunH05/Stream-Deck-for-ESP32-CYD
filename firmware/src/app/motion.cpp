#include "motion.h"

#include <Arduino.h>
#include <math.h>
#include <string.h>

#include "../cyd/cyd_hw.h"
#include "../ui/fonts.h"
#include "theme.h"

extern "C" lv_result_t lv_inv_area(lv_display_t *disp, const lv_area_t *area_p);

namespace {

constexpr int32_t W = CYD_SCREEN_W, H = CYD_SCREEN_H;

constexpr float SPRING_OMEGA = 17.0f;

float spring(float seconds) { return 1.0f - (1.0f + SPRING_OMEGA * seconds) * expf(-SPRING_OMEGA * seconds); }
float spring_norm(uint32_t t, uint32_t total) { return t >= total ? 1.0f : spring(t / 1000.0f) / spring(total / 1000.0f); }

float ease_out_quart(float x) {
  const float rest = 1.0f - x;
  return 1.0f - rest * rest * rest * rest;
}

void use_bezier(lv_anim_t *a, float x1, float y1, float x2, float y2) {
  lv_anim_set_path_cb(a, lv_anim_path_custom_bezier3);
  lv_anim_set_bezier3_param(a, LV_BEZIER_VAL_FLOAT(x1), LV_BEZIER_VAL_FLOAT(y1), LV_BEZIER_VAL_FLOAT(x2),
                            LV_BEZIER_VAL_FLOAT(y2));
}

enum class Mode : uint8_t { Idle, Anim, Finger };
enum class Curve : uint8_t { Spring, EaseOut };

struct Slide {
  Mode mode = Mode::Idle;
  Curve curve = Curve::Spring;
  lv_obj_t *from = nullptr, *to = nullptr;
  bool back = false;
  bool scroll = true;
  int32_t shown = 0;
  int32_t anim_from = 0, anim_to = 0;
  uint32_t anim_t0 = 0, anim_ms = 0, last_step = 0;
  void (*shown_cb)(lv_obj_t *) = nullptr;
  void (*cancel_cb)(lv_obj_t *) = nullptr;
} slide;

constexpr uint32_t PUSH_MS = 420, REVEAL_MS = 460, STEP_MS = 16;

#if CYD_PERF
constexpr int TRACE_MAX = 40;
uint16_t trace_ms[TRACE_MAX];
int16_t trace_value[TRACE_MAX];
int trace_count = 0;
uint32_t trace_start = 0;
#endif

bool rendering = false;

void render_started(lv_event_t *) { rendering = true; }
void render_finished(lv_event_t *) { rendering = false; }

void region(bool of_to, int32_t &x1, int32_t &x2) {
  const int32_t s = slide.shown;
  if (of_to) {
    x1 = slide.back ? W - s : 0;
    x2 = slide.back ? W - 1 : s - 1;
  } else {
    x1 = slide.back ? 0 : s;
    x2 = slide.back ? W - s - 1 : W - 1;
  }
}

void clip_invalidated(lv_event_t *event) {
  if (slide.mode == Mode::Idle || rendering) return;
  lv_area_t *area = static_cast<lv_area_t *>(lv_event_get_param(event));
  int32_t x1, x2;
  region(lv_screen_active() == slide.to, x1, x2);
  if (x1 <= x2) {
    if (area->x1 < x1) area->x1 = x1;
    if (area->x2 > x2) area->x2 = x2;
    if (area->x1 <= area->x2) return;
  }
  area->x1 = area->x2 = x1 <= x2 ? x1 : 0;
  area->y1 = area->y2 = 0;
}

void activate(lv_obj_t *screen) {
  if (lv_screen_active() == screen) return;
  lv_display_t *disp = lv_display_get_default();
  lv_refr_now(disp);
  lv_display_enable_invalidation(disp, false);
  lv_screen_load(screen);
  lv_display_enable_invalidation(disp, true);
  lv_inv_area(disp, nullptr);
}

void slide_apply(int32_t q) {
  q = q < 0 ? 0 : q > W ? W : q;
  if (q == slide.shown) return;
  const bool grow = q > slide.shown;
  lv_obj_t *target = grow ? slide.to : slide.from;
  const int32_t lo = grow ? slide.shown : q, hi = grow ? q : slide.shown;
  activate(target);
  slide.shown = q;
  lv_area_t strip;
  strip.y1 = 0;
  strip.y2 = H - 1;
  strip.x1 = slide.back ? W - hi : lo;
  strip.x2 = slide.back ? W - lo - 1 : hi - 1;
  lv_obj_invalidate_area(target, &strip);
  if (slide.scroll) cyd_scroll_to(slide.back ? W - q : q);
  lv_refr_now(lv_display_get_default());
  cyd_scroll_apply();
#if CYD_PERF
  if (trace_count < TRACE_MAX) {
    trace_ms[trace_count] = (uint16_t)(millis() - trace_start);
    trace_value[trace_count++] = (int16_t)q;
  }
#endif
}

void island_flush();

void slide_finish() {
  const bool arrived = slide.shown >= W;
  lv_obj_t *final_screen = arrived ? slide.to : slide.from;
  activate(final_screen);
  slide.mode = Mode::Idle;
  if (slide.scroll) {
    cyd_scroll_to(0);
    cyd_scroll_apply();
  }
  cyd_input_block(false);
#if CYD_PERF
  Serial.printf("[PERF] truot %s%s: %d buoc:", slide.scroll ? (slide.back ? "lui" : "toi") : "quet mau",
                arrived ? "" : " (bat ve)", trace_count);
  for (int i = 0; i < trace_count; i++) Serial.printf(" %u/%d", trace_ms[i], trace_value[i]);
  Serial.println();
#endif
  void (*callback)(lv_obj_t *) = arrived ? slide.shown_cb : slide.cancel_cb;
  island_flush();
  if (callback) callback(final_screen);
}

void slide_start(lv_obj_t *from, lv_obj_t *to, bool back, bool scroll, Mode mode) {
  motion_island_hide(false);
  slide = Slide{};
  slide.from = from;
  slide.to = to;
  slide.back = back;
  slide.scroll = scroll;
  slide.mode = mode;
  lv_obj_update_layout(to);
  cyd_input_block(true);
#if CYD_PERF
  trace_count = 0;
  trace_start = millis();
#endif
}

void slide_animate(int32_t target, uint32_t ms, Curve curve) {
  slide.mode = Mode::Anim;
  slide.curve = curve;
  slide.anim_from = slide.shown;
  slide.anim_to = target;
  slide.anim_t0 = millis();
  slide.anim_ms = ms;
  slide.last_step = 0;
}

constexpr float PRESS_SCALE = 0.96f;

float release_spring(float seconds) {
  constexpr float ZETA = 0.62f, OMEGA = 6.2832f / 0.32f;
  const float damped = OMEGA * sqrtf(1.0f - ZETA * ZETA);
  return 1.0f - expf(-ZETA * OMEGA * seconds) * (cosf(damped * seconds) + ZETA * OMEGA / damped * sinf(damped * seconds));
}

int32_t release_path(const lv_anim_t *a) {
  if (a->act_time <= 0) return a->start_value;
  if ((uint32_t)a->act_time >= a->duration) return a->end_value;
  const float total = a->duration / 1000.0f;
  const float p = release_spring(a->act_time / 1000.0f) / release_spring(total);
  return a->start_value + (int32_t)lroundf((a->end_value - a->start_value) * p);
}

const lv_style_prop_t SHRINK_PROPS[] = {LV_STYLE_TRANSFORM_WIDTH, LV_STYLE_TRANSFORM_HEIGHT, LV_STYLE_PROP_INV};
lv_style_transition_dsc_t press_in, press_out;
lv_style_t style_press_out;

struct PressStyle {
  int8_t dw = 0, dh = 0;
  lv_style_t style;
};
PressStyle press_styles[12];
int press_style_count = 0;

const lv_style_t *press_style(int32_t width, int32_t height, float scale = PRESS_SCALE) {
  const int8_t dw = (int8_t)max(1L, lroundf(width * (1.0f - scale) / 2));
  const int8_t dh = (int8_t)max(1L, lroundf(height * (1.0f - scale) / 2));
  for (int index = 0; index < press_style_count; index++) {
    if (press_styles[index].dw == dw && press_styles[index].dh == dh) return &press_styles[index].style;
  }
  if (press_style_count == (int)(sizeof press_styles / sizeof press_styles[0])) return &press_styles[0].style;
  PressStyle &entry = press_styles[press_style_count++];
  entry.dw = dw;
  entry.dh = dh;
  lv_style_init(&entry.style);
  lv_style_set_transform_width(&entry.style, -dw);
  lv_style_set_transform_height(&entry.style, -dh);
  lv_style_set_transition(&entry.style, &press_in);
  return &entry.style;
}

struct BgFade {
  lv_obj_t *obj = nullptr;
  lv_color_t from, to;
};
BgFade bg_fades[2];

void bg_fade_step(lv_anim_t *a, int32_t v) {
  BgFade *fade = static_cast<BgFade *>(a->var);
  lv_obj_set_style_bg_color(fade->obj, lv_color_mix(fade->to, fade->from, (uint8_t)v), LV_PART_MAIN);
}

void bg_fade_done(lv_anim_t *a) { static_cast<BgFade *>(a->var)->obj = nullptr; }

void pop_step(void *obj, int32_t v) {
  lv_obj_t *target = static_cast<lv_obj_t *>(obj);
  lv_obj_set_style_translate_y(target, 6 * (1000 - v) / 1000, LV_PART_MAIN);
  lv_obj_set_style_opa(target, (lv_opa_t)(v >= 1000 ? 255 : v < 0 ? 0 : v * 255 / 1000), LV_PART_MAIN);
}

void arc_step(void *arc, int32_t value) { lv_arc_set_value(static_cast<lv_obj_t *>(arc), value); }
void bar_step(void *bar, int32_t value) { lv_bar_set_value(static_cast<lv_obj_t *>(bar), value, LV_ANIM_OFF); }
void width_step(void *obj, int32_t value) { lv_obj_set_width(static_cast<lv_obj_t *>(obj), value); }

void animate_value(void *var, lv_anim_exec_xcb_t exec, int32_t from, int32_t to, uint32_t ms, uint32_t delay) {
  lv_anim_t a;
  lv_anim_init(&a);
  lv_anim_set_var(&a, var);
  lv_anim_set_exec_cb(&a, exec);
  lv_anim_set_values(&a, from, to);
  lv_anim_set_duration(&a, ms);
  lv_anim_set_delay(&a, delay);
  use_bezier(&a, 0.2f, 0.9f, 0.25f, 1.0f);
  lv_anim_start(&a);
}

struct Count {
  lv_obj_t *label = nullptr;
  int32_t target = 0;
  CountFormat format = nullptr;
  bool held = false;
};
Count counts[10];

void count_show(Count *count, int32_t value) {
  char text[24];
  count->format(text, sizeof text, value);
  lv_label_set_text(count->label, text);
}

void count_step(lv_anim_t *a, int32_t value) { count_show(static_cast<Count *>(a->var), value); }
void count_done(lv_anim_t *a) { static_cast<Count *>(a->var)->label = nullptr; }

Count *count_slot(lv_obj_t *label) {
  Count *free_slot = nullptr;
  for (Count &count : counts) {
    if (count.label == label) return &count;
    if (!count.label && !free_slot) free_slot = &count;
  }
  return free_slot;
}

struct Roll {
  lv_obj_t *label = nullptr;
  char text[16] = "";
  bool swapped = false;
};
Roll rolls[4];

constexpr int32_t ROLL_RISE = 6;
constexpr int32_t ROLL_SWAP = 380;
constexpr uint32_t ROLL_MS = 520;

void roll_step(lv_anim_t *a, int32_t v) {
  Roll *roll = static_cast<Roll *>(a->var);
  int32_t rise, opa;
  if (v < ROLL_SWAP) {
    const int32_t p = v * 1000 / ROLL_SWAP, eased = p * p / 1000;
    rise = -ROLL_RISE * eased / 1000;
    opa = 255 - 255 * eased / 1000;
  } else {
    if (!roll->swapped) {
      lv_label_set_text(roll->label, roll->text);
      roll->swapped = true;
    }
    const int32_t q = (v - ROLL_SWAP) * 1000 / (1000 - ROLL_SWAP), left = 1000 - q, eased = 1000 - left * left / 1000;
    rise = ROLL_RISE * (1000 - eased) / 1000;
    opa = 255 * eased / 1000;
  }
  lv_obj_set_style_translate_y(roll->label, rise, LV_PART_MAIN);
  lv_obj_set_style_opa(roll->label, (lv_opa_t)opa, LV_PART_MAIN);
}

void roll_finish(Roll *roll) {
  if (!roll->label) return;
  if (!roll->swapped) lv_label_set_text(roll->label, roll->text);
  lv_obj_set_style_translate_y(roll->label, 0, LV_PART_MAIN);
  lv_obj_set_style_opa(roll->label, LV_OPA_COVER, LV_PART_MAIN);
  roll->label = nullptr;
}

void roll_done(lv_anim_t *a) { roll_finish(static_cast<Roll *>(a->var)); }

struct Island {
  lv_obj_t *box = nullptr, *icon = nullptr, *text = nullptr;
  lv_timer_t *timer = nullptr;
  bool visible = false, pending = false;
  char pending_text[96] = "";
  const char *pending_icon = "";
  uint32_t pending_color = 0;
} island;

constexpr int32_t ISLAND_W0 = 64, ISLAND_H0 = 24, ISLAND_W1 = 296, ISLAND_H1 = 44;
#if defined(CYD_PERF_DEMO) && CYD_PERF_DEMO == 3
constexpr uint32_t ISLAND_SHOW_MS = 600000;
#else
constexpr uint32_t ISLAND_SHOW_MS = 3200;
#endif

void island_size(void *, int32_t p) {
  const int32_t w = ISLAND_W0 + (ISLAND_W1 - ISLAND_W0) * p / 1000, h = ISLAND_H0 + (ISLAND_H1 - ISLAND_H0) * p / 1000;
  lv_obj_set_size(island.box, w < ISLAND_W0 ? ISLAND_W0 : w, h < ISLAND_H0 ? ISLAND_H0 : h);
  const int32_t opa = p <= 550 ? 0 : p >= 1000 ? 255 : (p - 550) * 255 / 450;
  lv_obj_set_style_opa(island.icon, (lv_opa_t)opa, LV_PART_MAIN);
  lv_obj_set_style_opa(island.text, (lv_opa_t)opa, LV_PART_MAIN);
}

void island_gone(lv_anim_t *) { lv_obj_add_flag(island.box, LV_OBJ_FLAG_HIDDEN); }

void island_timeout(lv_timer_t *) { motion_island_hide(true); }

void island_create() {
  island.box = lv_obj_create(lv_layer_top());
  lv_obj_remove_style_all(island.box);
  lv_obj_set_style_bg_opa(island.box, LV_OPA_COVER, 0);
  lv_obj_set_style_radius(island.box, LV_RADIUS_CIRCLE, 0);
  lv_obj_remove_flag(island.box, LV_OBJ_FLAG_SCROLLABLE);
  lv_obj_align(island.box, LV_ALIGN_TOP_MID, 0, 4);
  island.icon = lv_label_create(island.box);
  lv_obj_set_style_text_font(island.icon, &ui_font_ico_24, 0);
  lv_obj_align(island.icon, LV_ALIGN_LEFT_MID, 12, 0);
  island.text = lv_label_create(island.box);
  lv_obj_set_style_text_font(island.text, &ui_font_vn_12b, 0);
  lv_obj_set_style_text_color(island.text, lv_color_hex(theme().white), 0);
  lv_label_set_long_mode(island.text, LV_LABEL_LONG_MODE_DOTS);
  lv_obj_set_size(island.text, ISLAND_W1 - 56, 20);
  lv_obj_align(island.text, LV_ALIGN_LEFT_MID, 44, 0);
  lv_obj_add_flag(island.box, LV_OBJ_FLAG_HIDDEN);
  island.timer = lv_timer_create(island_timeout, ISLAND_SHOW_MS, nullptr);
  lv_timer_pause(island.timer);
  motion_island_restyle();
}

void island_flush() {
  if (!island.pending) return;
  island.pending = false;
  motion_island_show(island.pending_text, island.pending_icon, island.pending_color);
}

}

void motion_init() {
  lv_style_transition_dsc_init(&press_in, SHRINK_PROPS, lv_anim_path_ease_out, 110, 0, nullptr);
  lv_style_transition_dsc_init(&press_out, SHRINK_PROPS, release_path, 450, 0, nullptr);
  lv_style_init(&style_press_out);
  lv_style_set_transition(&style_press_out, &press_out);
  lv_display_t *disp = lv_display_get_default();
  lv_display_add_event_cb(disp, render_started, LV_EVENT_RENDER_START, nullptr);
  lv_display_add_event_cb(disp, render_finished, LV_EVENT_RENDER_READY, nullptr);
  lv_display_add_event_cb(disp, clip_invalidated, LV_EVENT_INVALIDATE_AREA, nullptr);
}

void motion_tick() {
  if (slide.mode != Mode::Anim) return;
  const uint32_t now = millis(), t = now - slide.anim_t0;
  if (now - slide.last_step < STEP_MS && t < slide.anim_ms) return;
  slide.last_step = now;
  float p = 1.0f;
  if (t < slide.anim_ms) p = slide.curve == Curve::Spring ? spring_norm(t, slide.anim_ms) : ease_out_quart((float)t / slide.anim_ms);
  slide_apply(slide.anim_from + (int32_t)lroundf(p * (float)(slide.anim_to - slide.anim_from)));
  if (t >= slide.anim_ms) slide_finish();
}

void motion_push(lv_obj_t *screen, bool back, void (*shown)(lv_obj_t *screen)) {
  motion_finish();
  if (lv_screen_active() == screen || !cyd_scroll_supported()) {
    lv_screen_load(screen);
    if (shown) shown(screen);
    return;
  }
  slide_start(lv_screen_active(), screen, back, true, Mode::Anim);
  slide.shown_cb = shown;
  slide_animate(W, PUSH_MS, Curve::Spring);
}

bool motion_pushing() { return slide.mode != Mode::Idle; }

void motion_finish() {
  if (slide.mode == Mode::Idle) return;
  slide_apply(slide.mode == Mode::Finger ? W : slide.anim_to);
  slide_finish();
}

void motion_drag_begin(lv_obj_t *to, void (*shown)(lv_obj_t *screen), void (*cancelled)(lv_obj_t *screen)) {
  motion_finish();
  if (!cyd_scroll_supported() || lv_screen_active() == to) return;
  slide_start(lv_screen_active(), to, true, true, Mode::Finger);
  slide.shown_cb = shown;
  slide.cancel_cb = cancelled;
}

void motion_drag_move(int32_t offset) {
  if (slide.mode != Mode::Finger) return;
  const uint32_t now = millis();
  if (now - slide.last_step < STEP_MS) return;
  slide.last_step = now;
  slide_apply(offset);
}

void motion_drag_end(float velocity) {
  if (slide.mode != Mode::Finger) return;
  const bool go = velocity > 0.45f || (slide.shown > W / 3 && velocity > -0.3f);
  const int32_t target = go ? W : 0, distance = abs(target - slide.shown);
  slide_animate(target, 160 + distance * 180 / W, Curve::EaseOut);
}

void motion_reveal_begin() {
  motion_finish();
  lv_obj_t *screen = lv_screen_active();
  slide_start(screen, screen, false, false, Mode::Anim);
  slide_animate(W, REVEAL_MS, Curve::Spring);
}

bool motion_visible(lv_obj_t *obj) { return slide.mode == Mode::Idle && lv_obj_get_screen(obj) == lv_screen_active(); }

void motion_press_feedback(lv_obj_t *obj, float scale) {
  lv_obj_add_style(obj, &style_press_out, LV_STATE_DEFAULT);
  lv_obj_add_style(obj,
                   press_style(lv_obj_get_style_width(obj, LV_PART_MAIN), lv_obj_get_style_height(obj, LV_PART_MAIN), scale),
                   LV_STATE_PRESSED);
}

void motion_bg_to(lv_obj_t *obj, uint32_t rgb, uint32_t ms) {
  const lv_color_t to = lv_color_hex(rgb);
  BgFade *fade = nullptr;
  for (BgFade &slot : bg_fades) {
    if (slot.obj == obj) {
      if (lv_color_eq(slot.to, to)) return;
      lv_anim_delete(&slot, nullptr);
      slot.obj = nullptr;
    }
  }
  lv_style_value_t current;
  const bool has_local = lv_obj_get_local_style_prop(obj, LV_STYLE_BG_COLOR, &current, LV_PART_MAIN) == LV_STYLE_RES_FOUND;
  if (has_local && lv_color_eq(current.color, to)) return;
  for (BgFade &slot : bg_fades) {
    if (!slot.obj) {
      fade = &slot;
      break;
    }
  }
  if (!has_local || !fade || ms == 0 || !motion_visible(obj)) {
    lv_obj_set_style_bg_color(obj, to, LV_PART_MAIN);
    return;
  }
  fade->obj = obj;
  fade->from = current.color;
  fade->to = to;
  lv_anim_t a;
  lv_anim_init(&a);
  lv_anim_set_var(&a, fade);
  lv_anim_set_custom_exec_cb(&a, bg_fade_step);
  lv_anim_set_values(&a, 0, 255);
  lv_anim_set_duration(&a, ms);
  lv_anim_set_path_cb(&a, lv_anim_path_ease_in_out);
  lv_anim_set_completed_cb(&a, bg_fade_done);
  lv_anim_start(&a);
}

void motion_pop_in(lv_obj_t *obj) {
  lv_anim_delete(obj, pop_step);
  pop_step(obj, 0);
  lv_anim_t a;
  lv_anim_init(&a);
  lv_anim_set_var(&a, obj);
  lv_anim_set_exec_cb(&a, pop_step);
  lv_anim_set_values(&a, 0, 1000);
  lv_anim_set_duration(&a, 360);
  use_bezier(&a, 0.34f, 1.36f, 0.64f, 1.0f);
  lv_anim_start(&a);
}

void motion_arc_to(lv_obj_t *arc, int32_t value, uint32_t ms, uint32_t delay) {
  lv_anim_t *running = lv_anim_get(arc, arc_step);
  if (running && running->end_value == value) return;
  const int32_t from = lv_arc_get_value(arc);
  if (running) lv_anim_delete(arc, arc_step);
  const int32_t range = lv_arc_get_max_value(arc) - lv_arc_get_min_value(arc);
  if (from == value) return;
  if (ms == 0 || !motion_visible(arc) || (delay == 0 && abs(value - from) * 100 < range * 3)) {
    lv_arc_set_value(arc, value);
    return;
  }
  animate_value(arc, arc_step, from, value, ms, delay);
}

void motion_bar_to(lv_obj_t *bar, int32_t value, uint32_t ms, uint32_t delay) {
  lv_anim_t *running = lv_anim_get(bar, bar_step);
  if (running && running->end_value == value) return;
  const int32_t from = lv_bar_get_value(bar);
  if (running) lv_anim_delete(bar, bar_step);
  const int32_t range = lv_bar_get_max_value(bar) - lv_bar_get_min_value(bar);
  if (from == value) return;
  if (ms == 0 || !motion_visible(bar) || (delay == 0 && abs(value - from) * 100 < range * 3)) {
    lv_bar_set_value(bar, value, LV_ANIM_OFF);
    return;
  }
  animate_value(bar, bar_step, from, value, ms, delay);
}

void motion_width_to(lv_obj_t *obj, int32_t width, uint32_t ms, uint32_t delay) {
  lv_anim_t *running = lv_anim_get(obj, width_step);
  if (running && running->end_value == width) return;
  const int32_t from = lv_obj_get_style_width(obj, LV_PART_MAIN);
  if (running) lv_anim_delete(obj, width_step);
  if (from == width) return;
  if (ms == 0 || !motion_visible(obj)) {
    lv_obj_set_width(obj, width);
    return;
  }
  animate_value(obj, width_step, from, width, ms, delay);
}

void motion_count_hold(lv_obj_t *label, int32_t target, CountFormat format) {
  Count *count = count_slot(label);
  if (!count) return;
  lv_anim_delete(count, nullptr);
  count->label = label;
  count->target = target;
  count->format = format;
  count->held = true;
  count_show(count, 0);
}

void motion_count_release(uint32_t ms, uint32_t stagger) {
  uint32_t delay = 0;
  for (Count &count : counts) {
    if (!count.label || !count.held) continue;
    count.held = false;
    lv_anim_t a;
    lv_anim_init(&a);
    lv_anim_set_var(&a, &count);
    lv_anim_set_custom_exec_cb(&a, count_step);
    lv_anim_set_values(&a, 0, count.target);
    lv_anim_set_duration(&a, ms);
    lv_anim_set_delay(&a, delay);
    use_bezier(&a, 0.2f, 0.9f, 0.25f, 1.0f);
    lv_anim_set_completed_cb(&a, count_done);
    lv_anim_start(&a);
    delay += stagger;
  }
}

void motion_count_finish() {
  for (Count &count : counts) {
    if (!count.label) continue;
    lv_anim_delete(&count, nullptr);
    count_show(&count, count.target);
    count.label = nullptr;
  }
}

void motion_count_cancel(lv_obj_t *label) {
  for (Count &count : counts) {
    if (count.label != label) continue;
    lv_anim_delete(&count, nullptr);
    count.label = nullptr;
  }
}

bool motion_count_holding(lv_obj_t *label) {
  for (const Count &count : counts) {
    if (count.label == label && count.held) return true;
  }
  return false;
}

bool motion_roll_text(lv_obj_t *label, const char *text) {
  for (Roll &roll : rolls) {
    if (roll.label != label) continue;
    lv_anim_delete(&roll, nullptr);
    roll_finish(&roll);
  }
  if (!motion_visible(label)) return false;
  Roll *roll = nullptr;
  for (Roll &slot : rolls) {
    if (!slot.label) {
      roll = &slot;
      break;
    }
  }
  if (!roll) return false;
  roll->label = label;
  strlcpy(roll->text, text, sizeof roll->text);
  roll->swapped = false;
  lv_anim_t a;
  lv_anim_init(&a);
  lv_anim_set_var(&a, roll);
  lv_anim_set_custom_exec_cb(&a, roll_step);
  lv_anim_set_values(&a, 0, 1000);
  lv_anim_set_duration(&a, ROLL_MS);
  lv_anim_set_completed_cb(&a, roll_done);
  lv_anim_start(&a);
  return true;
}

void motion_island_show(const char *text, const char *icon, uint32_t icon_color) {
  if (!island.box) island_create();
  if (slide.mode != Mode::Idle) {
    strlcpy(island.pending_text, text, sizeof island.pending_text);
    island.pending_icon = icon;
    island.pending_color = icon_color;
    island.pending = true;
    return;
  }
  lv_label_set_text(island.text, text);
  lv_label_set_text(island.icon, icon);
  lv_obj_set_style_text_color(island.icon, lv_color_hex(icon_color), 0);
  lv_timer_reset(island.timer);
  lv_timer_resume(island.timer);
  if (island.visible) return;
  island.visible = true;
  lv_anim_delete(&island, island_size);
  lv_obj_remove_flag(island.box, LV_OBJ_FLAG_HIDDEN);
  island_size(nullptr, 0);
  lv_anim_t a;
  lv_anim_init(&a);
  lv_anim_set_var(&a, &island);
  lv_anim_set_exec_cb(&a, island_size);
  lv_anim_set_values(&a, 0, 1000);
  lv_anim_set_duration(&a, 520);
  use_bezier(&a, 0.34f, 1.36f, 0.64f, 1.0f);
  lv_anim_start(&a);
}

void motion_island_hide(bool animated) {
  if (!island.box || !island.visible) return;
  island.visible = false;
  lv_timer_pause(island.timer);
  lv_anim_delete(&island, island_size);
  if (!animated || slide.mode != Mode::Idle) {
    lv_obj_add_flag(island.box, LV_OBJ_FLAG_HIDDEN);
    return;
  }
  lv_anim_t a;
  lv_anim_init(&a);
  lv_anim_set_var(&a, &island);
  lv_anim_set_exec_cb(&a, island_size);
  lv_anim_set_values(&a, 1000, 0);
  lv_anim_set_duration(&a, 260);
  use_bezier(&a, 0.4f, 0.0f, 1.0f, 1.0f);
  lv_anim_set_completed_cb(&a, island_gone);
  lv_anim_start(&a);
}

void motion_island_restyle() {
  if (!island.box) return;
  const Theme &t = theme();
  lv_obj_set_style_bg_color(island.box, lv_color_hex(t.island), 0);
  lv_obj_set_style_border_width(island.box, theme_dark() ? 1 : 0, 0);
  lv_obj_set_style_border_color(island.box, lv_color_hex(t.island_border), 0);
  lv_obj_set_style_text_color(island.text, lv_color_hex(t.white), 0);
}
