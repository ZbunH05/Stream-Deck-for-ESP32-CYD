#pragma once

#include <lvgl.h>

constexpr int CYD_SCREEN_W = 320;
constexpr int CYD_SCREEN_H = 240;

void cyd_init();

void cyd_loop();

bool cyd_flipped();
void cyd_set_flipped(bool flipped);

bool cyd_inverted();
void cyd_set_inverted(bool inverted);

bool cyd_scroll_supported();
void cyd_scroll_to(int32_t offset);
void cyd_scroll_apply();

void cyd_input_block(bool blocked);

struct CydTouch {
  bool pressed;
  int16_t x, y;
  int16_t start_x, start_y;
  uint32_t start_ms, ms;
};
void cyd_touch_state(CydTouch *out);

enum LedColor : uint8_t { LED_RED = 1, LED_GREEN = 2, LED_BLUE = 4 };
void cyd_led_flash(uint8_t color, int times);

#if CYD_PERF
uint16_t cyd_read_pixel(int32_t x, int32_t y);
#endif
