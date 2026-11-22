#pragma once

#include <lvgl.h>
#include <stddef.h>

void motion_init();
void motion_tick();

void motion_push(lv_obj_t *screen, bool back, void (*shown)(lv_obj_t *screen));
bool motion_pushing();
void motion_finish();

void motion_drag_begin(lv_obj_t *to, void (*shown)(lv_obj_t *screen), void (*cancelled)(lv_obj_t *screen));
void motion_drag_move(int32_t offset);
void motion_drag_end(float velocity);

void motion_reveal_begin();

bool motion_visible(lv_obj_t *obj);

void motion_press_feedback(lv_obj_t *obj, float scale = 0.96f);

void motion_bg_to(lv_obj_t *obj, uint32_t rgb, uint32_t ms = 260);

void motion_pop_in(lv_obj_t *obj);

void motion_arc_to(lv_obj_t *arc, int32_t value, uint32_t ms, uint32_t delay = 0);
void motion_bar_to(lv_obj_t *bar, int32_t value, uint32_t ms, uint32_t delay = 0);
void motion_width_to(lv_obj_t *obj, int32_t width, uint32_t ms, uint32_t delay = 0);

typedef void (*CountFormat)(char *out, size_t size, int32_t value);
void motion_count_hold(lv_obj_t *label, int32_t target, CountFormat format);
void motion_count_release(uint32_t ms, uint32_t stagger);
void motion_count_finish();
void motion_count_cancel(lv_obj_t *label);
bool motion_count_holding(lv_obj_t *label);

bool motion_roll_text(lv_obj_t *label, const char *text);

void motion_island_show(const char *text, const char *icon, uint32_t icon_color);
void motion_island_hide(bool animated);
void motion_island_restyle();
