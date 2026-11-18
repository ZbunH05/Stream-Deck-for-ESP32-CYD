// Xem deck.h.
#include "deck.h"

#include <Arduino.h>
#include <string.h>

#include "../app/icons.h"
#include "../app/motion.h"
#include "../app/theme.h"
#include "../cyd/cyd_hw.h"
#include "../ui/fonts.h"
#include "link.h"
#include "store.h"

namespace {

// ---------- bố cục (px) ----------
constexpr int32_t KEY = 72, GAP = 6, GRID_X = 7, GRID_Y = 6, RADIUS = 12;
constexpr int32_t BAR_X = 7, BAR_Y = 168, BAR_W = 306, BAR_H = 66, ARROW_W = 26;
constexpr int32_t STATS_W = 54;                            // chỗ dành cho cột CPU/RAM (nhãn tự rộng theo chữ)
constexpr int32_t ROW_W = BAR_W - 2 * ARROW_W - STATS_W - 6;  // hàng giữa: giờ + hai dòng chữ
constexpr int PAGE_DOTS = 8;
constexpr lv_opa_t OFFLINE_OPA = 110;    // phím khi chưa nối PC: vẫn nhìn ra, rõ là chưa dùng được
constexpr uint32_t BADGE_MS = 1200;

DeckProfile profile;
DeckInfo info;

struct KeyView {
  lv_obj_t *btn, *icon, *title, *badge;
  lv_obj_t *ring;  // viền kết quả: lớp con bo góc 12 (phím có ảnh vẽ không bo góc)
  uint32_t badge_until;  // 0: không hiện; UINT32_MAX: giữ luôn
  uint32_t ring_until;   // viền trắng đang chờ PC trả lời tới lúc này (0: không chờ)
};

struct BarView {
  lv_obj_t *box, *prev, *next, *prev_icon, *next_icon, *row, *dot, *unlink, *clock, *title, *sub, *stats;
  lv_obj_t *pages[PAGE_DOTS];
};

struct ScreenView {
  lv_obj_t *screen;
  KeyView keys[DECK_KEYS];
  BarView bar;
  int page;
};

ScreenView views[2];
int current = 0;       // views[current] đang hiện
bool gestured = false;  // lần chạm này đã thành cử chỉ vuốt: không tính là bấm phím

// ---------- style dùng chung ----------
// Như CYD-EEZ (src/ui/styles.cpp): chạm xuống phủ trắng ~10 % trong 50 ms, nhả tay nhạt dần 260 ms; độ thu và lò xo
// khi nhả là motion_press_feedback.
const lv_style_prop_t HIGHLIGHT_PROPS[] = {LV_STYLE_RECOLOR_OPA, LV_STYLE_RECOLOR, LV_STYLE_PROP_INV};
lv_style_transition_dsc_t press_transition, release_transition;
lv_style_t st_screen, st_key, st_pressed, st_bar, st_ghost, st_title, st_icon, st_badge, st_dot;
lv_style_t st_clock, st_bar_title, st_bar_sub, st_stats, st_row, st_col, st_unlink, st_page_dot, st_ring;

lv_color_t col(Pal p) { return lv_color_hex(theme_color(p)); }

// Ghép cờ đối tượng trong C++ (A | B của hai giá trị enum ra int)
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

  // nút trong suốt (mũi tên lật trang): chỉ thấy lớp phủ khi nhấn
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

  // dấu ✓/⚠: nhãn icon có nền tròn (một đối tượng), màu nền đặt lúc hiện
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

  // hàng giữa thanh tin: [chấm REC] [giờ] [cột chữ]
  lv_style_init(&st_row);
  lv_style_set_layout(&st_row, LV_LAYOUT_FLEX);
  lv_style_set_flex_flow(&st_row, LV_FLEX_FLOW_ROW);
  lv_style_set_flex_cross_place(&st_row, LV_FLEX_ALIGN_CENTER);
  lv_style_set_flex_track_place(&st_row, LV_FLEX_ALIGN_CENTER);
  lv_style_set_pad_column(&st_row, 8);

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

// Đổi chữ chỉ khi khác (lv_label_set_text luôn xin vẽ lại)
void set_text(lv_obj_t *obj, const char *text) {
  const char *now = lv_label_get_text(obj);
  if (now && strcmp(now, text) == 0) return;
  lv_label_set_text(obj, text);
}

// ---------- điều hướng ----------

const DeckPage *page_at(int index) {
  return index >= 0 && index < profile.page_count ? &profile.pages[index] : nullptr;
}

// Trang chính chứa `index` (thư mục lồng nhau: đi ngược lên tới trang chính)
int top_of(int index) {
  for (int guard = 0; guard < DECK_MAX_PAGES; guard++) {
    const DeckPage *page = page_at(index);
    if (!page || page->parent < 0) return index;
    index = page->parent;
  }
  return index;
}

// Độ sâu trong cây thư mục (trang chính là 0), để biết đi tới trang kia là "tiến vào" hay "lùi ra"
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
    return;
  }
  if (now.page == index) return;
  const int from = now.page;
  bool back;
  if (depth_of(index) != depth_of(from)) {
    back = depth_of(index) < depth_of(from);  // vào thư mục: tiến; quay ra: lùi
  } else {
    back = index < from;  // lật trang chính: trang sau vào từ phải
  }
  const int next = 1 - current;
  bind(views[next], index);
  current = next;
  motion_push(views[next].screen, back, page_shown);
  Serial.printf("[DECK] Trang %d (%s)\n", index, profile.pages[index].name);
}

void flip(int step) {
  const int page = views[current].page;
  if (page < 0 || profile.pages[page].parent >= 0) return;  // trong thư mục: không lật trang
  const int to = page + step;
  if (to < 0 || to >= profile.top_pages) return;
  go(to, true);
}

// ---------- gợn sóng khi chạm ----------
// Vòng tròn trắng lan ra từ chỗ ngón tay chạm rồi mờ đi, như nút Material. Tâm vòng trôi dần từ chỗ chạm về giữa phím
// trong lúc vòng lớn lên, tới cuối là hình tròn nội tiếp phím (đường kính 72). Cả tâm lẫn bán kính đi tuyến tính theo
// cùng một tham số, mà hai đầu đều nằm gọn trong phím bo góc 12, nên vòng không bao giờ chạm góc bo: không cần
// clip_corner. (Bản đầu bật clip_corner, LVGL vẽ con của phím qua lớp ARGB8888 mỗi khung: đo 18/11/2026 chạm phím chỉ
// được 10–13 khung/giây, xem src/deck/perf.cpp.) Một vòng dùng chung cho cả hai màn: mỗi lúc chỉ một ngón tay.

constexpr int32_t RIPPLE_R0 = 7, RIPPLE_R1 = KEY / 2;  // bán kính đầu và cuối (px)
constexpr int32_t RIPPLE_MARGIN = 14;                  // tâm lúc đầu cách mép phím ít nhất chừng này
constexpr uint32_t RIPPLE_MS = 420;
constexpr lv_opa_t RIPPLE_OPA = 80;

struct Ripple {
  lv_obj_t *obj = nullptr, *key = nullptr;
  int32_t cx = 0, cy = 0;  // chỗ chạm (đã kéo vào trong RIPPLE_MARGIN)
} ripple;

void ripple_step(void *, int32_t v) {  // v: 0..1000 (đã qua đường cong chậm dần)
  const int32_t r = RIPPLE_R0 + (RIPPLE_R1 - RIPPLE_R0) * v / 1000;
  const int32_t x = ripple.cx + (KEY / 2 - ripple.cx) * v / 1000;
  const int32_t y = ripple.cy + (KEY / 2 - ripple.cy) * v / 1000;
  lv_obj_set_pos(ripple.obj, x - r, y - r);
  lv_obj_set_size(ripple.obj, 2 * r, 2 * r);
  const int32_t left = 1000 - v;  // rõ ngay từ đầu, mờ nhanh dần về cuối
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

// cx, cy: chỗ chạm tính từ góc trên trái của phím
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
  lv_obj_move_to_index(ripple.obj, 0);  // trên ảnh, dưới nhãn và dấu ✓
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

// ---------- viền kết quả ----------
// Bấm phím lệnh: viền trắng 2 px nhịp sáng/mờ trong lúc chờ PC. PC báo xong: viền xanh lá rồi tắt dần; lỗi hay quá
// 3 giây không trả lời: viền cam. Viền vẽ đè lên ảnh, theo đúng góc bo của phím.

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

// ---------- sự kiện ----------

void on_pressed(lv_event_t *) { gestured = false; }

void on_key_pressed(lv_event_t *event) {
  gestured = false;
  if (motion_pushing()) return;
  ripple_start(static_cast<lv_obj_t *>(lv_event_get_current_target(event)));
}

void on_gesture(lv_event_t *) {
  if (motion_pushing()) return;
  const lv_dir_t dir = lv_indev_get_gesture_dir(lv_indev_active());
  if (dir != LV_DIR_LEFT && dir != LV_DIR_RIGHT) return;
  gestured = true;
  lv_indev_wait_release(lv_indev_active());  // phím đang bị đè không nhận "bấm" khi nhả tay
  flip(dir == LV_DIR_LEFT ? 1 : -1);
}

void on_key(lv_event_t *event) {
  if (gestured || motion_pushing()) return;
  const int slot = (int)(intptr_t)lv_event_get_user_data(event);
  const bool hold = lv_event_get_code(event) == LV_EVENT_LONG_PRESSED;
  const int page = views[current].page;
  const DeckPage *p = page_at(page);
  if (!p) return;
  const DeckKey &key = p->keys[slot];
  switch (key.kind) {
    case KeyKind::Folder:
      if (!hold) go(key.target, true);
      break;
    case KeyKind::Back:
      if (!hold && p->parent >= 0) go(p->parent, true);
      break;
    case KeyKind::Action:
    case KeyKind::Toggle:
      if (!info.online) {
        motion_island_show("Chưa nối máy tính: mở CydAgent trên PC", ICON_P_UNLINK, theme().orange);
        deck_feedback(page, slot, false);
        break;
      }
      ring_wait(views[current].keys[slot]);
      link_press(page, slot, hold);
      break;
    case KeyKind::Empty:
      break;
  }
}

void on_prev(lv_event_t *) { flip(-1); }
void on_next(lv_event_t *) { flip(1); }

// ---------- dựng ----------

void build_key(ScreenView &view, int slot) {
  KeyView &k = view.keys[slot];
  k.btn = box(view.screen);
  // Không có lớp phủ khi nhấn (recolor) như nút của CYD-EEZ: phủ màu lên ảnh làm LVGL vẽ từng điểm ảnh một (rất chậm),
  // và phủ cả góc đen vẽ sẵn trong ảnh. Phản hồi khi chạm là gợn sóng + lún.
  lv_obj_add_style(k.btn, &st_key, 0);
  lv_obj_set_pos(k.btn, GRID_X + (slot % 4) * (KEY + GAP), GRID_Y + (slot / 4) * (KEY + GAP));
  lv_obj_set_size(k.btn, KEY, KEY);
  lv_obj_add_flag(k.btn, LV_OBJ_FLAG_CLICKABLE);
  motion_press_feedback(k.btn, 0.92f);  // lún sâu hơn nút của CYD-EEZ (0,96): phím là thứ duy nhất để bấm
  lv_obj_add_event_cb(k.btn, on_key_pressed, LV_EVENT_PRESSED, nullptr);
  lv_obj_add_event_cb(k.btn, on_key, LV_EVENT_SHORT_CLICKED, (void *)(intptr_t)slot);
  lv_obj_add_event_cb(k.btn, on_key, LV_EVENT_LONG_PRESSED, (void *)(intptr_t)slot);

  // icon do bo tự vẽ (phím ‹ của thư mục); phím có ảnh thì ẩn
  k.icon = label(k.btn, &st_icon);
  lv_obj_set_size(k.icon, KEY, 48);
  lv_obj_set_pos(k.icon, 0, 4);
  lv_obj_add_flag(k.icon, LV_OBJ_FLAG_HIDDEN);

  // nhãn nằm trên dải tối mà ảnh đã vẽ sẵn ở đáy phím
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

  // chưa nối PC: icon mất nối thay cho giờ
  b.unlink = label(row, &st_unlink);
  lv_label_set_text_static(b.unlink, ICON_P_UNLINK);

  b.clock = label(row, &st_clock);

  lv_obj_t *column = box(row);
  lv_obj_add_style(column, &st_col, 0);
  lv_obj_set_height(column, 40);
  lv_obj_set_flex_grow(column, 1);

  b.title = label(column, &st_bar_title);
  lv_label_set_long_mode(b.title, LV_LABEL_LONG_MODE_DOTS);
  lv_obj_set_size(b.title, lv_pct(100), 20);

  b.sub = label(column, &st_bar_sub);
  lv_label_set_long_mode(b.sub, LV_LABEL_LONG_MODE_DOTS);
  lv_obj_set_size(b.sub, lv_pct(100), 20);

  b.stats = label(b.box, &st_stats);
  lv_label_set_recolor(b.stats, true);
  lv_obj_set_size(b.stats, LV_SIZE_CONTENT, 40);  // không bao giờ xuống dòng, canh phải sát mũi tên
  lv_obj_align(b.stats, LV_ALIGN_RIGHT_MID, -(ARROW_W + 2), 0);

  // chấm trang ở đáy thanh (trang chính), như Stream Deck Neo
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

// ---------- đổ dữ liệu ----------

void bind_key(KeyView &k, const DeckKey &key) {
  const bool on = key.kind == KeyKind::Toggle && key.on;
  const lv_image_dsc_t *normal = key.image ? key.image : store_find(key.image_hash);
  const lv_image_dsc_t *lit = key.image_on ? key.image_on : store_find(key.image_on_hash);
  const lv_image_dsc_t *image = on && lit ? lit : normal;  // ảnh chưa về kho: phím chỉ có nền và nhãn
  const char *title = on && key.title_on[0] ? key.title_on : key.title;
  if (key.live[0]) title = key.live;

  const lv_image_dsc_t *shown = key.kind == KeyKind::Back ? nullptr : image;
  lv_obj_set_style_bg_image_src(k.btn, shown, 0);
  // Ảnh đã có sẵn góc bo (KeyArt.cs): vẽ không bo góc thì LVGL chép thẳng ảnh (nhanh nhất). Bo góc 12 thì mỗi lần vẽ
  // phải che góc từng dòng, và khi đang lún (transform) còn rơi vào đường vẽ từng điểm ảnh.
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
    title = key.title[0] ? key.title : "Quay ra";
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
  lv_obj_set_style_text_color(b.prev_icon, col(can_prev ? Pal::text : Pal::muted), 0);
  lv_obj_set_style_text_color(b.next_icon, col(can_next ? Pal::text : Pal::muted), 0);

  // chấm trang: chỉ ở trang chính và khi có hơn một trang
  const int dots = folder || profile.top_pages < 2 ? 0 : min<int>(profile.top_pages, PAGE_DOTS);
  const int32_t dots_x = (BAR_W - (dots * 4 + (dots - 1) * 5)) / 2;
  for (int i = 0; i < PAGE_DOTS; i++) {
    if (i >= dots) {
      lv_obj_add_flag(b.pages[i], LV_OBJ_FLAG_HIDDEN);
      continue;
    }
    lv_obj_remove_flag(b.pages[i], LV_OBJ_FLAG_HIDDEN);
    lv_obj_set_x(b.pages[i], dots_x + i * 9);
    lv_obj_set_style_bg_color(b.pages[i], col(i == top ? Pal::text : Pal::muted), 0);
  }

  const bool online = info.online;
  if (online) {
    lv_obj_add_flag(b.unlink, LV_OBJ_FLAG_HIDDEN);
    lv_obj_remove_flag(b.clock, LV_OBJ_FLAG_HIDDEN);
    lv_obj_remove_flag(b.stats, LV_OBJ_FLAG_HIDDEN);
  } else {
    lv_obj_remove_flag(b.unlink, LV_OBJ_FLAG_HIDDEN);
    lv_obj_add_flag(b.clock, LV_OBJ_FLAG_HIDDEN);
    lv_obj_add_flag(b.stats, LV_OBJ_FLAG_HIDDEN);
  }
  // không có cột CPU/RAM thì chữ dùng luôn chỗ đó
  lv_obj_set_width(b.row, online ? ROW_W : ROW_W + STATS_W + 2);

  if (!online) {
    lv_obj_add_flag(b.dot, LV_OBJ_FLAG_HIDDEN);
    set_text(b.title, "Đang chờ máy tính…");
    set_text(b.sub, "Mở CydAgent trên PC");
    return;
  }

  if (page && page->bar == BarMode::Obs) {
    const bool busy = info.obs_rec || info.obs_live;
    if (busy) lv_obj_remove_flag(b.dot, LV_OBJ_FLAG_HIDDEN);
    else lv_obj_add_flag(b.dot, LV_OBJ_FLAG_HIDDEN);
    set_text(b.clock, busy && info.obs_time[0] ? info.obs_time : info.clock);
    set_text(b.title, info.obs_live ? "OBS · đang live" : info.obs_rec ? "OBS · đang ghi" : "OBS · đang chờ");
    snprintf(text, sizeof text, "Cảnh: %s", info.obs_scene[0] ? info.obs_scene : "–");
    set_text(b.sub, text);
    snprintf(text, sizeof text, "Rớt #%06lX %u#\nCPU #%06lX %u%%#", (unsigned long)theme().green, info.obs_dropped,
             (unsigned long)theme().green, info.cpu);
    set_text(b.stats, text);
    return;
  }

  lv_obj_add_flag(b.dot, LV_OBJ_FLAG_HIDDEN);
  set_text(b.clock, info.clock);
  // Dòng trên: bài đang phát (không có thì tên hồ sơ). Dòng dưới: nguồn phát, hay thư mục / tên trang đang mở.
  if (folder) {
    snprintf(text, sizeof text, "Thư mục %s", page->name);
  } else if (info.media_title[0]) {
    snprintf(text, sizeof text, "%s", info.media_source[0] ? info.media_source : "Đang phát");
  } else {
    snprintf(text, sizeof text, "%s", page ? page->name : "");
  }
  set_text(b.title, info.media_title[0] ? info.media_title : profile.name);
  set_text(b.sub, text);
  snprintf(text, sizeof text, "CPU #%06lX %u%%#\nRAM #%06lX %u%%#", (unsigned long)theme().green, info.cpu,
           (unsigned long)theme().accent, info.ram);
  set_text(b.stats, text);
}

void bind_opacity(ScreenView &view) {
  for (KeyView &k : view.keys) lv_obj_set_style_opa(k.btn, info.online ? (lv_opa_t)LV_OPA_COVER : OFFLINE_OPA, 0);
}

void bind(ScreenView &view, int index) {
  view.page = index;
  const DeckPage &page = profile.pages[index];
  for (int slot = 0; slot < DECK_KEYS; slot++) bind_key(view.keys[slot], page.keys[slot]);
  bind_opacity(view);
  bind_bar(view);
}

}  // namespace

DeckProfile &deck_profile() { return profile; }
DeckInfo &deck_info() { return info; }

void deck_init() {
  styles_init();
  build_view(views[0]);
  build_view(views[1]);
  lv_indev_t *indev = lv_indev_get_next(nullptr);
  if (indev) lv_indev_set_long_press_time(indev, 500);  // giữ 0,5 s: lệnh phụ của phím
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
      if (k.ring_until && (int32_t)(now - k.ring_until) >= 0) {  // PC không trả lời
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
  // chỉ ảnh và nhãn: dấu ✓/⚠ và viền đang chạy giữ nguyên (khác deck_refresh)
  ScreenView &view = views[current];
  if (view.page < 0 || key < 0 || key >= DECK_KEYS) return;
  KeyView &k = view.keys[key];
  const DeckKey &def = profile.pages[view.page].keys[key];
  if (def.kind == KeyKind::Back || def.kind == KeyKind::Empty) return;
  const lv_image_dsc_t *normal = def.image ? def.image : store_find(def.image_hash);
  const lv_image_dsc_t *lit = def.image_on ? def.image_on : store_find(def.image_on_hash);
  const bool on = def.kind == KeyKind::Toggle && def.on;
  lv_obj_set_style_bg_image_src(k.btn, on && lit ? lit : normal, 0);
  lv_obj_set_style_radius(k.btn, (on && lit) || normal ? 0 : RADIUS, 0);
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
  snprintf(text, sizeof text, "Hồ sơ: %s", profile.name);
  motion_island_show(text, ICON_S_SYNC, theme().accent);
}

#if CYD_PERF
// Bản đo hiệu năng (perf.cpp): bấm, viền... một phím của trang đang hiện mà không cần ngón tay
lv_obj_t *deck_demo_key(int slot) { return views[current].keys[slot].btn; }
void deck_demo_ripple(int slot, int32_t x, int32_t y) { ripple_begin(views[current].keys[slot].btn, x, y); }
void deck_demo_ring(int slot, int mode) {  // 0: chờ PC, 1: xong, 2: lỗi
  KeyView &k = views[current].keys[slot];
  if (mode == 0) ring_wait(k);
  else ring_result(k, mode == 1);
}
#endif
