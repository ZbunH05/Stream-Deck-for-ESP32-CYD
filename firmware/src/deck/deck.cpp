#include "deck.h"

#include <Arduino.h>
#include <string.h>

#include "../app/icons.h"
#include "../app/motion.h"
#include "../app/theme.h"
#include "../cyd/cyd_hw.h"
#include "../ui/fonts.h"
#include "lang.h"
#include "link.h"
#include "ramimg.h"
#include "store.h"

namespace {

constexpr int32_t KEY = 72, GAP = 6, GRID_X = 7, GRID_Y = 6, RADIUS = 12;
constexpr int32_t BAR_X = 7, BAR_Y = 168, BAR_W = 306, BAR_H = 66, ARROW_W = 26;
constexpr int32_t STATS_W = 54;
constexpr int32_t ROW_W = BAR_W - 2 * ARROW_W - STATS_W - 6;
constexpr int PAGE_DOTS = 8;
constexpr lv_opa_t OFFLINE_OPA = 110;
constexpr uint32_t BADGE_MS = 1200;

DeckProfile profile;
DeckInfo info;

struct KeyView {
  lv_obj_t *btn, *icon, *title, *badge;
  lv_obj_t *ring;
  uint32_t badge_until;
  uint32_t ring_until;
};

struct BarView {
  lv_obj_t *box, *prev, *next, *prev_icon, *next_icon, *row, *dot, *unlink, *clock, *title, *sub, *stats;
  lv_obj_t *track, *fill;
  lv_obj_t *pages[PAGE_DOTS];
};

struct ScreenView {
  lv_obj_t *screen;
  KeyView keys[DECK_KEYS];
  BarView bar;
  int page;
};

ScreenView views[2];
int current = 0;
bool gestured = false;
int pressed_slot = -1;

const lv_style_prop_t HIGHLIGHT_PROPS[] = {LV_STYLE_RECOLOR_OPA, LV_STYLE_RECOLOR, LV_STYLE_PROP_INV};
lv_style_transition_dsc_t press_transition, release_transition;
lv_style_t st_screen, st_key, st_pressed, st_bar, st_ghost, st_title, st_icon, st_badge, st_dot;
lv_style_t st_clock, st_bar_title, st_bar_sub, st_stats, st_row, st_col, st_unlink, st_page_dot, st_ring;
lv_style_t st_track, st_fill;

lv_color_t col(Pal p) { return lv_color_hex(theme_color(p)); }

constexpr lv_obj_flag_t ui_flags(uint32_t value) { return static_cast<lv_obj_flag_t>(value); }

void styles_init() {
  lv_style_transition_dsc_init(&press_transition, HIGHLIGHT_PROPS, lv_anim_path_ease_out, 50, 0, nullptr);
  lv_style_transition_dsc_init(&release_transition, HIGHLIGHT_PROPS, lv_anim_path_ease_out, 260, 0, nullptr);

  lv_style_init(&st_screen);
  lv_style_set_bg_opa(&st_screen, LV_OPA_COVER);
  lv_style_set_bg_color(&st_screen, col(Pal::bg));

  lv_style_init(&st_key);
  lv_style_set_bg_opa(&st_key, LV_OPA_COVER);
  lv_style_set_bg_color(&st_key, col(Pal::key));
  lv_style_set_radius(&st_key, RADIUS);
  lv_style_set_transition(&st_key, &release_transition);

  lv_style_init(&st_pressed);
  lv_style_set_recolor(&st_pressed, col(Pal::white));
  lv_style_set_recolor_opa(&st_pressed, 26);
  lv_style_set_transition(&st_pressed, &press_transition);

  lv_style_init(&st_bar);
  lv_style_set_bg_opa(&st_bar, LV_OPA_COVER);
  lv_style_set_bg_color(&st_bar, col(Pal::bar));
  lv_style_set_radius(&st_bar, RADIUS);

  lv_style_init(&st_ghost);
  lv_style_set_radius(&st_ghost, RADIUS);
  lv_style_set_transition(&st_ghost, &release_transition);

  lv_style_init(&st_title);
  lv_style_set_text_font(&st_title, &ui_font_vn_12b);
  lv_style_set_text_color(&st_title, col(Pal::text));
  lv_style_set_text_align(&st_title, LV_TEXT_ALIGN_CENTER);

  lv_style_init(&st_icon);
  lv_style_set_text_font(&st_icon, &ui_font_ico_48);
  lv_style_set_text_color(&st_icon, col(Pal::text2));
  lv_style_set_text_align(&st_icon, LV_TEXT_ALIGN_CENTER);

  lv_style_init(&st_badge);
  lv_style_set_text_font(&st_badge, &ui_font_ico_18);
  lv_style_set_text_color(&st_badge, col(Pal::badge_bg));
  lv_style_set_text_align(&st_badge, LV_TEXT_ALIGN_CENTER);
  lv_style_set_bg_opa(&st_badge, LV_OPA_COVER);
  lv_style_set_radius(&st_badge, LV_RADIUS_CIRCLE);

  lv_style_init(&st_dot);
  lv_style_set_bg_opa(&st_dot, LV_OPA_COVER);
  lv_style_set_bg_color(&st_dot, col(Pal::red));
  lv_style_set_radius(&st_dot, LV_RADIUS_CIRCLE);

  lv_style_init(&st_clock);
  lv_style_set_text_font(&st_clock, &ui_font_nu_26);
  lv_style_set_text_color(&st_clock, col(Pal::text));

  lv_style_init(&st_bar_title);
  lv_style_set_text_font(&st_bar_title, &ui_font_vn_12b);
  lv_style_set_text_color(&st_bar_title, col(Pal::text));

  lv_style_init(&st_bar_sub);
  lv_style_set_text_font(&st_bar_sub, &ui_font_vn_12);
  lv_style_set_text_color(&st_bar_sub, col(Pal::text2));

  lv_style_init(&st_stats);
  lv_style_set_text_font(&st_stats, &ui_font_vn_12);
  lv_style_set_text_color(&st_stats, col(Pal::text2));
  lv_style_set_text_align(&st_stats, LV_TEXT_ALIGN_RIGHT);

  lv_style_init(&st_row);
  lv_style_set_layout(&st_row, LV_LAYOUT_FLEX);
  lv_style_set_flex_flow(&st_row, LV_FLEX_FLOW_ROW);
  lv_style_set_flex_cross_place(&st_row, LV_FLEX_ALIGN_CENTER);
  lv_style_set_flex_track_place(&st_row, LV_FLEX_ALIGN_CENTER);
  lv_style_set_pad_column(&st_row, 8);

  lv_style_init(&st_track);
  lv_style_set_bg_opa(&st_track, LV_OPA_COVER);
  lv_style_set_bg_color(&st_track, col(Pal::key));
  lv_style_set_radius(&st_track, 1);
  lv_style_init(&st_fill);
  lv_style_set_bg_opa(&st_fill, LV_OPA_COVER);
  lv_style_set_bg_color(&st_fill, col(Pal::accent));
  lv_style_set_radius(&st_fill, 1);

  lv_style_init(&st_ring);
  lv_style_set_radius(&st_ring, RADIUS);
  lv_style_set_border_width(&st_ring, 2);

  lv_style_init(&st_unlink);
  lv_style_set_text_font(&st_unlink, &ui_font_ico_24);
  lv_style_set_text_color(&st_unlink, col(Pal::orange));

  lv_style_init(&st_page_dot);
  lv_style_set_bg_opa(&st_page_dot, LV_OPA_COVER);
  lv_style_set_radius(&st_page_dot, LV_RADIUS_CIRCLE);

  lv_style_init(&st_col);
  lv_style_set_layout(&st_col, LV_LAYOUT_FLEX);
  lv_style_set_flex_flow(&st_col, LV_FLEX_FLOW_COLUMN);
}

lv_obj_t *box(lv_obj_t *parent) {
  lv_obj_t *obj = lv_obj_create(parent);
  lv_obj_remove_style_all(obj);
  lv_obj_remove_flag(obj, LV_OBJ_FLAG_SCROLLABLE);
  return obj;
}

lv_obj_t *label(lv_obj_t *parent, const lv_style_t *style) {
  lv_obj_t *obj = lv_label_create(parent);
  lv_obj_remove_style_all(obj);
  lv_obj_add_style(obj, style, 0);
  return obj;
}

void set_text(lv_obj_t *obj, const char *text) {
  const char *now = lv_label_get_text(obj);
  if (now && strcmp(now, text) == 0) return;
  lv_label_set_text(obj, text);
}

void show(lv_obj_t *obj, bool visible) {
  if (lv_obj_has_flag(obj, LV_OBJ_FLAG_HIDDEN) != visible) return;
  if (visible) lv_obj_remove_flag(obj, LV_OBJ_FLAG_HIDDEN);
  else lv_obj_add_flag(obj, LV_OBJ_FLAG_HIDDEN);
}

void text_color(lv_obj_t *obj, Pal pal) {
  const lv_color_t c = col(pal);
  if (!lv_color_eq(lv_obj_get_style_text_color(obj, LV_PART_MAIN), c)) lv_obj_set_style_text_color(obj, c, 0);
}

void bg_color(lv_obj_t *obj, Pal pal) {
  const lv_color_t c = col(pal);
  if (!lv_color_eq(lv_obj_get_style_bg_color(obj, LV_PART_MAIN), c)) lv_obj_set_style_bg_color(obj, c, 0);
}

void set_opa(lv_obj_t *obj, lv_opa_t opa) {
  if (lv_obj_get_style_opa(obj, LV_PART_MAIN) != opa) lv_obj_set_style_opa(obj, opa, 0);
}

void set_width(lv_obj_t *obj, int32_t w) {
  if (lv_obj_get_style_width(obj, LV_PART_MAIN) != w) lv_obj_set_width(obj, w);
}

void set_x(lv_obj_t *obj, int32_t x) {
  if (lv_obj_get_style_x(obj, LV_PART_MAIN) != x) lv_obj_set_x(obj, x);
}

const DeckPage *page_at(int index) {
  return index >= 0 && index < profile.page_count ? &profile.pages[index] : nullptr;
}

int top_of(int index) {
  for (int guard = 0; guard < DECK_MAX_PAGES; guard++) {
    const DeckPage *page = page_at(index);
    if (!page || page->parent < 0) return index;
    index = page->parent;
  }
  return index;
}

int depth_of(int index) {
  int depth = 0;
  for (int guard = 0; guard < DECK_MAX_PAGES; guard++) {
    const DeckPage *page = page_at(index);
    if (!page || page->parent < 0) break;
    index = page->parent;
    depth++;
  }
  return depth;
}

void bind(ScreenView &view, int index);

void page_shown(lv_obj_t *) {}

void go(int index, bool animate) {
  if (!page_at(index)) return;
  ScreenView &now = views[current];
  if (!animate || now.page < 0) {
    bind(now, index);
    if (lv_screen_active() != now.screen) lv_screen_load(now.screen);
    link_page(index);
    return;
  }
  if (now.page == index) return;
  const int from = now.page;
  bool back;
  if (depth_of(index) != depth_of(from)) {
    back = depth_of(index) < depth_of(from);
  } else {
    back = index < from;
  }
  const int next = 1 - current;
  bind(views[next], index);
  current = next;
  motion_push(views[next].screen, back, page_shown);
  link_page(index);
}

void flip(int step) {
  const int page = views[current].page;
  if (page < 0 || profile.pages[page].parent >= 0) return;
  const int to = page + step;
  if (to < 0 || to >= profile.top_pages) return;
  go(to, true);
}

constexpr int32_t RIPPLE_R0 = 7, RIPPLE_R1 = KEY / 2;
constexpr int32_t RIPPLE_MARGIN = 14;
constexpr uint32_t RIPPLE_MS = 420;
constexpr lv_opa_t RIPPLE_OPA = 80;

struct Ripple {
  lv_obj_t *obj = nullptr, *key = nullptr;
  int32_t cx = 0, cy = 0;
} ripple;

void ripple_step(void *, int32_t v) {
  const int32_t r = RIPPLE_R0 + (RIPPLE_R1 - RIPPLE_R0) * v / 1000;
  const int32_t x = ripple.cx + (KEY / 2 - ripple.cx) * v / 1000;
  const int32_t y = ripple.cy + (KEY / 2 - ripple.cy) * v / 1000;
  lv_obj_set_pos(ripple.obj, x - r, y - r);
  lv_obj_set_size(ripple.obj, 2 * r, 2 * r);
  const int32_t left = 1000 - v;
  lv_obj_set_style_bg_opa(ripple.obj, (lv_opa_t)(RIPPLE_OPA * left / 1000 * left / 1000), 0);
}

void ripple_stop() {
  if (!ripple.obj) return;
  lv_anim_delete(&ripple, ripple_step);
  lv_obj_add_flag(ripple.obj, LV_OBJ_FLAG_HIDDEN);
  ripple.key = nullptr;
}

void ripple_done(lv_anim_t *) { ripple_stop(); }

void ripple_begin(lv_obj_t *key, int32_t cx, int32_t cy);

void ripple_start(lv_obj_t *key) {
  lv_point_t point;
  lv_indev_get_point(lv_indev_active(), &point);
  lv_area_t area;
  lv_obj_get_coords(key, &area);
  ripple_begin(key, point.x - area.x1, point.y - area.y1);
}

void ripple_begin(lv_obj_t *key, int32_t cx, int32_t cy) {
  ripple_stop();
  if (!ripple.obj) {
    ripple.obj = lv_obj_create(key);
    lv_obj_remove_style_all(ripple.obj);
    lv_obj_remove_flag(ripple.obj, ui_flags(LV_OBJ_FLAG_CLICKABLE | LV_OBJ_FLAG_SCROLLABLE));
    lv_obj_set_style_radius(ripple.obj, LV_RADIUS_CIRCLE, 0);
    lv_obj_set_style_bg_color(ripple.obj, col(Pal::white), 0);
  } else if (lv_obj_get_parent(ripple.obj) != key) {
    lv_obj_set_parent(ripple.obj, key);
  }
  lv_obj_move_to_index(ripple.obj, 0);
  lv_obj_remove_flag(ripple.obj, LV_OBJ_FLAG_HIDDEN);

  ripple.cx = LV_CLAMP(RIPPLE_MARGIN, cx, KEY - RIPPLE_MARGIN);
  ripple.cy = LV_CLAMP(RIPPLE_MARGIN, cy, KEY - RIPPLE_MARGIN);
  ripple.key = key;
  ripple_step(nullptr, 0);

  lv_anim_t a;
  lv_anim_init(&a);
  lv_anim_set_var(&a, &ripple);
  lv_anim_set_exec_cb(&a, ripple_step);
  lv_anim_set_values(&a, 0, 1000);
  lv_anim_set_duration(&a, RIPPLE_MS);
  lv_anim_set_path_cb(&a, lv_anim_path_ease_out);
  lv_anim_set_completed_cb(&a, ripple_done);
  lv_anim_start(&a);
}

constexpr uint32_t RING_TIMEOUT_MS = 3000;

void ring_opa(void *obj, int32_t v) { lv_obj_set_style_border_opa(static_cast<lv_obj_t *>(obj), (lv_opa_t)v, 0); }

void ring_gone(lv_anim_t *a) { lv_obj_add_flag(static_cast<lv_obj_t *>(a->var), LV_OBJ_FLAG_HIDDEN); }

void ring_show(KeyView &k, Pal color) {
  lv_anim_delete(k.ring, ring_opa);
  lv_obj_set_style_border_color(k.ring, col(color), 0);
  lv_obj_set_style_border_opa(k.ring, LV_OPA_COVER, 0);
  lv_obj_remove_flag(k.ring, LV_OBJ_FLAG_HIDDEN);
}

void ring_wait(KeyView &k) {
  ring_show(k, Pal::white);
  lv_anim_t a;
  lv_anim_init(&a);
  lv_anim_set_var(&a, k.ring);
  lv_anim_set_exec_cb(&a, ring_opa);
  lv_anim_set_values(&a, 255, 70);
  lv_anim_set_duration(&a, 420);
  lv_anim_set_reverse_duration(&a, 420);
  lv_anim_set_repeat_count(&a, LV_ANIM_REPEAT_INFINITE);
  lv_anim_set_path_cb(&a, lv_anim_path_ease_in_out);
  lv_anim_start(&a);
  k.ring_until = millis() + RING_TIMEOUT_MS;
}

void ring_result(KeyView &k, bool ok) {
  k.ring_until = 0;
  ring_show(k, ok ? Pal::green : Pal::orange);
  lv_anim_t a;
  lv_anim_init(&a);
  lv_anim_set_var(&a, k.ring);
  lv_anim_set_exec_cb(&a, ring_opa);
  lv_anim_set_values(&a, 255, 0);
  lv_anim_set_duration(&a, 700);
  lv_anim_set_delay(&a, 450);
  lv_anim_set_path_cb(&a, lv_anim_path_ease_in);
  lv_anim_set_completed_cb(&a, ring_gone);
  lv_anim_start(&a);
}

void ring_clear(KeyView &k) {
  lv_anim_delete(k.ring, ring_opa);
  k.ring_until = 0;
  lv_obj_add_flag(k.ring, LV_OBJ_FLAG_HIDDEN);
}

void on_pressed(lv_event_t *) { gestured = false; }

bool is_command(KeyKind kind) { return kind == KeyKind::Action || kind == KeyKind::Toggle || kind == KeyKind::Plugin; }

void key_release(bool cancelled) {
  if (pressed_slot < 0) return;
  const int slot = pressed_slot;
  pressed_slot = -1;
  const int page = views[current].page;
  const DeckPage *p = page_at(page);
  if (!p) return;
  if (!cancelled && p->keys[slot].kind != KeyKind::Plugin) ring_wait(views[current].keys[slot]);
  link_key_up(page, slot, cancelled);
}

void on_key_pressed(lv_event_t *event) {
  gestured = false;
  if (motion_pushing()) return;
  ripple_start(static_cast<lv_obj_t *>(lv_event_get_current_target(event)));
  const int slot = (int)(intptr_t)lv_event_get_user_data(event);
  const int page = views[current].page;
  const DeckPage *p = page_at(page);
  if (!p || !is_command(p->keys[slot].kind) || !info.online) return;
  key_release(true);
  pressed_slot = slot;
  link_key_down(page, slot);
}

void on_key_released(lv_event_t *event) {
  const int slot = (int)(intptr_t)lv_event_get_user_data(event);
  if (pressed_slot == slot) {
    key_release(gestured || motion_pushing());
    return;
  }
  const DeckPage *p = page_at(views[current].page);
  if (!gestured && !motion_pushing() && p && is_command(p->keys[slot].kind) && !info.online) {
    motion_island_show(tr(Str::NoPc), ICON_P_UNLINK, theme().orange);
    deck_feedback(views[current].page, slot, false);
  }
}

void on_key_lost(lv_event_t *event) {
  if (pressed_slot == (int)(intptr_t)lv_event_get_user_data(event)) key_release(true);
}

void on_gesture(lv_event_t *) {
  if (motion_pushing()) return;
  const lv_dir_t dir = lv_indev_get_gesture_dir(lv_indev_active());
  if (dir != LV_DIR_LEFT && dir != LV_DIR_RIGHT) return;
  gestured = true;
  key_release(true);
  lv_indev_wait_release(lv_indev_active());
  flip(dir == LV_DIR_LEFT ? 1 : -1);
}

void on_key(lv_event_t *event) {
  if (gestured || motion_pushing()) return;
  const int slot = (int)(intptr_t)lv_event_get_user_data(event);
  const DeckPage *p = page_at(views[current].page);
  if (!p) return;
  const DeckKey &key = p->keys[slot];
  if (key.kind == KeyKind::Folder) go(key.target, true);
  else if (key.kind == KeyKind::Back && p->parent >= 0) go(p->parent, true);
}

void on_prev(lv_event_t *) { flip(-1); }
void on_next(lv_event_t *) { flip(1); }

void build_key(ScreenView &view, int slot) {
  KeyView &k = view.keys[slot];
  k.btn = box(view.screen);
  lv_obj_add_style(k.btn, &st_key, 0);
  lv_obj_set_pos(k.btn, GRID_X + (slot % 4) * (KEY + GAP), GRID_Y + (slot / 4) * (KEY + GAP));
  lv_obj_set_size(k.btn, KEY, KEY);
  lv_obj_add_flag(k.btn, LV_OBJ_FLAG_CLICKABLE);
  motion_press_feedback(k.btn, 0.92f);
  lv_obj_add_event_cb(k.btn, on_key_pressed, LV_EVENT_PRESSED, (void *)(intptr_t)slot);
  lv_obj_add_event_cb(k.btn, on_key_released, LV_EVENT_RELEASED, (void *)(intptr_t)slot);
  lv_obj_add_event_cb(k.btn, on_key_lost, LV_EVENT_PRESS_LOST, (void *)(intptr_t)slot);
  lv_obj_add_event_cb(k.btn, on_key, LV_EVENT_SHORT_CLICKED, (void *)(intptr_t)slot);

  k.icon = label(k.btn, &st_icon);
  lv_obj_set_size(k.icon, KEY, 48);
  lv_obj_set_pos(k.icon, 0, 4);
  lv_obj_add_flag(k.icon, LV_OBJ_FLAG_HIDDEN);

  k.title = label(k.btn, &st_title);
  lv_label_set_long_mode(k.title, LV_LABEL_LONG_MODE_DOTS);
  lv_obj_set_size(k.title, KEY - 6, 20);
  lv_obj_set_pos(k.title, 3, KEY - 21);

  k.ring = box(k.btn);
  lv_obj_add_style(k.ring, &st_ring, 0);
  lv_obj_remove_flag(k.ring, LV_OBJ_FLAG_CLICKABLE);
  lv_obj_set_pos(k.ring, 0, 0);
  lv_obj_set_size(k.ring, KEY, KEY);
  lv_obj_add_flag(k.ring, LV_OBJ_FLAG_HIDDEN);

  k.badge = label(k.btn, &st_badge);
  lv_obj_set_size(k.badge, 20, 20);
  lv_obj_set_pos(k.badge, KEY - 24, 4);
  lv_obj_add_flag(k.badge, LV_OBJ_FLAG_HIDDEN);
  k.badge_until = 0;
  ring_clear(k);
}

lv_obj_t *arrow(lv_obj_t *parent, int32_t x, const char *glyph, lv_event_cb_t cb, lv_obj_t **icon) {
  lv_obj_t *btn = box(parent);
  lv_obj_add_style(btn, &st_ghost, 0);
  lv_obj_add_style(btn, &st_pressed, LV_STATE_PRESSED);
  lv_obj_set_pos(btn, x, 0);
  lv_obj_set_size(btn, ARROW_W, BAR_H);
  lv_obj_add_flag(btn, LV_OBJ_FLAG_CLICKABLE);
  lv_obj_add_event_cb(btn, on_pressed, LV_EVENT_PRESSED, nullptr);
  lv_obj_add_event_cb(btn, cb, LV_EVENT_SHORT_CLICKED, nullptr);
  *icon = lv_label_create(btn);
  lv_obj_remove_style_all(*icon);
  lv_obj_set_style_text_font(*icon, &ui_font_ico_24, 0);
  lv_label_set_text_static(*icon, glyph);
  lv_obj_center(*icon);
  return btn;
}

void build_bar(ScreenView &view) {
  BarView &b = view.bar;
  b.box = box(view.screen);
  lv_obj_add_style(b.box, &st_bar, 0);
  lv_obj_set_pos(b.box, BAR_X, BAR_Y);
  lv_obj_set_size(b.box, BAR_W, BAR_H);

  b.prev = arrow(b.box, 0, ICON_NAV_BACK, on_prev, &b.prev_icon);
  b.next = arrow(b.box, BAR_W - ARROW_W, ICON_NAV_MORE, on_next, &b.next_icon);

  b.row = box(b.box);
  lv_obj_t *row = b.row;
  lv_obj_add_style(row, &st_row, 0);
  lv_obj_set_pos(row, ARROW_W + 2, 0);
  lv_obj_set_size(row, ROW_W, BAR_H);

  b.dot = box(row);
  lv_obj_add_style(b.dot, &st_dot, 0);
  lv_obj_set_size(b.dot, 10, 10);

  b.unlink = label(row, &st_unlink);
  lv_label_set_text_static(b.unlink, ICON_P_UNLINK);

  b.clock = label(row, &st_clock);

  lv_obj_t *column = box(row);
  lv_obj_add_style(column, &st_col, 0);
  lv_obj_set_height(column, 44);
  lv_obj_set_flex_grow(column, 1);

  b.title = label(column, &st_bar_title);
  lv_label_set_long_mode(b.title, LV_LABEL_LONG_MODE_DOTS);
  lv_obj_set_size(b.title, lv_pct(100), 20);

  b.sub = label(column, &st_bar_sub);
  lv_label_set_long_mode(b.sub, LV_LABEL_LONG_MODE_DOTS);
  lv_obj_set_size(b.sub, lv_pct(100), 20);

  b.track = box(column);
  lv_obj_add_style(b.track, &st_track, 0);
  lv_obj_set_size(b.track, lv_pct(100), 2);
  lv_obj_set_style_margin_top(b.track, 2, 0);
  b.fill = box(b.track);
  lv_obj_add_style(b.fill, &st_fill, 0);
  lv_obj_set_size(b.fill, 0, 2);
  lv_obj_add_flag(b.track, LV_OBJ_FLAG_HIDDEN);

  b.stats = label(b.box, &st_stats);
  lv_label_set_recolor(b.stats, true);
  lv_obj_set_size(b.stats, LV_SIZE_CONTENT, 40);
  lv_obj_align(b.stats, LV_ALIGN_RIGHT_MID, -(ARROW_W + 2), 0);

  for (int i = 0; i < PAGE_DOTS; i++) {
    b.pages[i] = box(b.box);
    lv_obj_add_style(b.pages[i], &st_page_dot, 0);
    lv_obj_set_size(b.pages[i], 4, 4);
    lv_obj_set_y(b.pages[i], BAR_H - 8);
    lv_obj_add_flag(b.pages[i], LV_OBJ_FLAG_HIDDEN);
  }
}

void build_view(ScreenView &view) {
  view.screen = lv_obj_create(nullptr);
  lv_obj_remove_style_all(view.screen);
  lv_obj_add_style(view.screen, &st_screen, 0);
  lv_obj_remove_flag(view.screen, LV_OBJ_FLAG_SCROLLABLE);
  lv_obj_add_event_cb(view.screen, on_gesture, LV_EVENT_GESTURE, nullptr);
  lv_obj_add_event_cb(view.screen, on_pressed, LV_EVENT_PRESSED, nullptr);
  for (int slot = 0; slot < DECK_KEYS; slot++) build_key(view, slot);
  build_bar(view);
  view.page = -1;
}

void bind_key(KeyView &k, const DeckKey &key, int page, int slot) {
  const bool on = key.kind == KeyKind::Toggle && key.on;
  const lv_image_dsc_t *ram = ramimg_find((uint8_t)page, (uint8_t)slot);
  const lv_image_dsc_t *normal = ram ? ram : key.image ? key.image : store_find(key.image_hash);
  const lv_image_dsc_t *lit = key.image_on ? key.image_on : store_find(key.image_on_hash);
  const lv_image_dsc_t *image = on && lit && !ram ? lit : normal;
  const char *title = on && key.title_on[0] ? key.title_on : key.title;
  if (key.live[0]) title = key.live;

  const lv_image_dsc_t *shown = key.kind == KeyKind::Back ? nullptr : image;
  lv_obj_set_style_bg_image_src(k.btn, shown, 0);
  lv_obj_set_style_radius(k.btn, shown ? 0 : RADIUS, 0);
  lv_obj_set_style_bg_color(k.btn, col(key.kind == KeyKind::Empty || key.kind == KeyKind::Back ? Pal::key_back : Pal::key), 0);
  if (key.kind == KeyKind::Empty) {
    lv_obj_remove_flag(k.btn, LV_OBJ_FLAG_CLICKABLE);
  } else {
    lv_obj_add_flag(k.btn, LV_OBJ_FLAG_CLICKABLE);
  }

  if (key.kind == KeyKind::Back) {
    set_text(k.icon, ICON_NAV_BACK);
    lv_obj_remove_flag(k.icon, LV_OBJ_FLAG_HIDDEN);
    title = key.title[0] ? key.title : tr(Str::Back);
  } else {
    lv_obj_add_flag(k.icon, LV_OBJ_FLAG_HIDDEN);
  }
  set_text(k.title, key.kind == KeyKind::Empty ? "" : title);
  lv_obj_set_style_text_color(k.title, col(key.kind == KeyKind::Back ? Pal::text2 : Pal::text), 0);

  lv_obj_add_flag(k.badge, LV_OBJ_FLAG_HIDDEN);
  k.badge_until = 0;
  ring_clear(k);
}

void bind_bar(ScreenView &view) {
  BarView &b = view.bar;
  const DeckPage *page = page_at(view.page);
  const bool folder = page && page->parent >= 0;
  const int top = top_of(view.page);
  char text[96];

  const bool can_prev = !folder && top > 0, can_next = !folder && top + 1 < profile.top_pages;
  text_color(b.prev_icon, can_prev ? Pal::text : Pal::muted);
  text_color(b.next_icon, can_next ? Pal::text : Pal::muted);

  const int dots = folder || profile.top_pages < 2 ? 0 : min<int>(profile.top_pages, PAGE_DOTS);
  const int32_t dots_x = (BAR_W - (dots * 4 + (dots - 1) * 5)) / 2;
  for (int i = 0; i < PAGE_DOTS; i++) {
    show(b.pages[i], i < dots);
    if (i >= dots) continue;
    set_x(b.pages[i], dots_x + i * 9);
    bg_color(b.pages[i], i == top ? Pal::text : Pal::muted);
  }

  const bool online = info.online;
  show(b.unlink, !online);
  show(b.clock, online);
  show(b.stats, online);
  set_width(b.row, online ? ROW_W : ROW_W + STATS_W + 2);

  if (!online) {
    show(b.dot, false);
    show(b.track, false);
    set_text(b.title, tr(Str::WaitTitle));
    set_text(b.sub, tr(Str::WaitSub));
    return;
  }

  if (page && page->bar == BarMode::Obs) {
    show(b.track, false);
    const bool busy = info.obs_connected && (info.obs_rec || info.obs_live);
    show(b.dot, busy);
    set_text(b.clock, busy && info.obs_time[0] ? info.obs_time : info.clock);
    if (!info.obs_connected) {
      set_text(b.title, tr(Str::ObsOff));
      set_text(b.sub, tr(Str::ObsOffSub));
    } else {
      set_text(b.title, tr(info.obs_live ? Str::ObsLive : info.obs_paused ? Str::ObsPaused
                                         : info.obs_rec ? Str::ObsRec : Str::ObsIdle));
      snprintf(text, sizeof text, tr(Str::SceneFmt), info.obs_scene[0] ? info.obs_scene : "–");
      set_text(b.sub, text);
    }
    snprintf(text, sizeof text, "%s #%06lX %u#\nCPU #%06lX %u%%#", tr(Str::Drop),
             (unsigned long)(info.obs_dropped ? theme().orange : theme().green), info.obs_dropped,
             (unsigned long)theme().green, info.obs_connected ? info.obs_cpu : info.cpu);
    set_text(b.stats, text);
    return;
  }

  show(b.dot, false);
  set_text(b.clock, info.clock);
  const bool media = info.media_title[0] != '\0';
  if (folder) {
    snprintf(text, sizeof text, tr(Str::FolderFmt), page->name);
  } else if (media) {
    snprintf(text, sizeof text, "%s", info.media_sub[0] ? info.media_sub : tr(Str::Playing));
  } else {
    snprintf(text, sizeof text, "%s", page ? page->name : "");
  }
  set_text(b.title, media ? info.media_title : profile.name);
  set_text(b.sub, text);
  const bool progress = media && info.progress <= 1000;
  show(b.track, progress);
  if (progress) {
    const int32_t w = lv_obj_get_width(b.track);
    set_width(b.fill, w * info.progress / 1000);
    bg_color(b.fill, info.playing ? Pal::accent : Pal::muted);
  }
  snprintf(text, sizeof text, "CPU #%06lX %u%%#\nRAM #%06lX %u%%#", (unsigned long)theme().green, info.cpu,
           (unsigned long)theme().accent, info.ram);
  set_text(b.stats, text);
}

void bind_opacity(ScreenView &view) {
  for (KeyView &k : view.keys) set_opa(k.btn, info.online ? (lv_opa_t)LV_OPA_COVER : OFFLINE_OPA);
}

void bind(ScreenView &view, int index) {
  view.page = index;
  const DeckPage &page = profile.pages[index];
  for (int slot = 0; slot < DECK_KEYS; slot++) bind_key(view.keys[slot], page.keys[slot], index, slot);
  bind_opacity(view);
  bind_bar(view);
}

}

DeckProfile &deck_profile() { return profile; }
DeckInfo &deck_info() { return info; }

void deck_init() {
  styles_init();
  build_view(views[0]);
  build_view(views[1]);
  lv_indev_t *indev = lv_indev_get_next(nullptr);
  if (indev) lv_indev_set_long_press_time(indev, 500);
  Serial.printf("[DECK] Da dung 2 man, heap trong %lu KB\n", (unsigned long)(ESP.getFreeHeap() >> 10));
}

void deck_tick() {
  const uint32_t now = millis();
  for (ScreenView &view : views) {
    for (KeyView &k : view.keys) {
      if (k.badge_until && k.badge_until != UINT32_MAX && (int32_t)(now - k.badge_until) >= 0) {
        lv_obj_add_flag(k.badge, LV_OBJ_FLAG_HIDDEN);
        k.badge_until = 0;
      }
      if (k.ring_until && (int32_t)(now - k.ring_until) >= 0) {
        ring_result(k, false);
        Serial.println("[DECK] PC khong tra loi lenh");
      }
    }
  }
}

void deck_go(int page, bool animate) { go(page, animate); }

int deck_page() { return views[current].page; }

void deck_refresh() {
  ScreenView &view = views[current];
  if (view.page < 0) return;
  if (view.page >= profile.page_count) view.page = 0;
  bind(view, view.page);
}

void deck_refresh_key(int key) {
  ScreenView &view = views[current];
  if (view.page < 0 || key < 0 || key >= DECK_KEYS) return;
  KeyView &k = view.keys[key];
  const DeckKey &def = profile.pages[view.page].keys[key];
  if (def.kind == KeyKind::Back || def.kind == KeyKind::Empty) return;
  const lv_image_dsc_t *ram = ramimg_find((uint8_t)view.page, (uint8_t)key);
  const lv_image_dsc_t *normal = ram ? ram : def.image ? def.image : store_find(def.image_hash);
  const lv_image_dsc_t *lit = def.image_on ? def.image_on : store_find(def.image_on_hash);
  const bool on = def.kind == KeyKind::Toggle && def.on;
  const lv_image_dsc_t *image = on && lit && !ram ? lit : normal;
  if (lv_obj_get_style_bg_image_src(k.btn, LV_PART_MAIN) != image) lv_obj_set_style_bg_image_src(k.btn, image, 0);
  if (ram) lv_obj_invalidate(k.btn);
  const int32_t radius = image ? 0 : RADIUS;
  if (lv_obj_get_style_radius(k.btn, LV_PART_MAIN) != radius) lv_obj_set_style_radius(k.btn, radius, 0);
  const char *title = on && def.title_on[0] ? def.title_on : def.title;
  set_text(k.title, def.live[0] ? def.live : title);
}

void deck_refresh_info() {
  ScreenView &view = views[current];
  if (view.page < 0) return;
  bind_opacity(view);
  bind_bar(view);
}

void deck_feedback(int page, int key, bool ok, bool hold) {
  ScreenView &view = views[current];
  if (view.page != page || key < 0 || key >= DECK_KEYS) return;
  KeyView &k = view.keys[key];
  set_text(k.badge, ok ? ICON_T_CHECK : ICON_T_LATE);
  lv_obj_set_style_bg_color(k.badge, col(ok ? Pal::green : Pal::orange), 0);
  lv_obj_remove_flag(k.badge, LV_OBJ_FLAG_HIDDEN);
  k.badge_until = hold ? UINT32_MAX : millis() + BADGE_MS;
  if (k.badge_until == 0) k.badge_until = 1;
  motion_pop_in(k.badge);
  if (!hold) ring_result(k, ok);
}

void deck_profile_switched() {
  char text[48];
  snprintf(text, sizeof text, tr(Str::Profile), profile.name);
  motion_island_show(text, ICON_S_SYNC, theme().accent);
}

#if CYD_PERF
lv_obj_t *deck_demo_key(int slot) { return views[current].keys[slot].btn; }
void deck_demo_ripple(int slot, int32_t x, int32_t y) { ripple_begin(views[current].keys[slot].btn, x, y); }
void deck_demo_ring(int slot, int mode) {
  KeyView &k = views[current].keys[slot];
  if (mode == 0) ring_wait(k);
  else ring_result(k, mode == 1);
}
#endif
