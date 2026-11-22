#include "cyd_hw.h"

#include <Arduino.h>
#include <Preferences.h>
#include <SPI.h>
#include <TFT_eSPI.h>
#include <XPT2046_Touchscreen.h>
#include <esp_heap_caps.h>

#define XPT2046_IRQ 36
#define XPT2046_MOSI 32
#define XPT2046_MISO 39
#define XPT2046_CLK 25
#define XPT2046_CS 33

static constexpr int TOUCH_X_MIN = 200, TOUCH_X_MAX = 3700;
static constexpr int TOUCH_Y_MIN = 240, TOUCH_Y_MAX = 3800;

#ifndef CYD_FLIP_DEFAULT
#define CYD_FLIP_DEFAULT 0
#endif
static bool flipped = CYD_FLIP_DEFAULT;
static bool inverted = false;

static constexpr int LED_R = 4, LED_G = 16, LED_B = 17;
static constexpr uint32_t LED_STEP_MS = 180;
static uint8_t led_color = 0;
static int led_steps = 0;
static uint32_t led_next = 0;

static void led_write(uint8_t color) {
  digitalWrite(LED_R, (color & LED_RED) ? LOW : HIGH);
  digitalWrite(LED_G, (color & LED_GREEN) ? LOW : HIGH);
  digitalWrite(LED_B, (color & LED_BLUE) ? LOW : HIGH);
}

void cyd_led_flash(uint8_t color, int times) {
  led_color = color;
  led_steps = times * 2;
  led_next = 0;
}

static constexpr size_t DRAW_BUF_LINES = 16;
static constexpr size_t DRAW_BUF_BYTES = CYD_SCREEN_W * DRAW_BUF_LINES * sizeof(uint16_t);
static constexpr uint32_t LOOP_MAX_SLEEP_MS = 5;

static TFT_eSPI tft;
static SPIClass touchSpi(VSPI);
static XPT2046_Touchscreen touch(XPT2046_CS, XPT2046_IRQ);
static volatile bool input_blocked = false;

#if defined(ST7789_DRIVER)
static constexpr bool HW_SCROLL = false;
#else
static constexpr bool HW_SCROLL = true;
#endif
#ifndef CYD_SCROLL_INVERT
#define CYD_SCROLL_INVERT 0
#endif
static int32_t scroll_shown = 0, scroll_wanted = 0;

enum class Panel { Unknown, ILI9341, ILI9341_INV, ST7789 };

static constexpr uint32_t ID_ILI9341_INV = 0x8181B3;

static const char *panel_name(Panel p) {
  switch (p) {
    case Panel::ILI9341:     return "ILI9341";
    case Panel::ILI9341_INV: return "ILI9341_INV";
    case Panel::ST7789:      return "ST7789";
    default:                 return "UNKNOWN";
  }
}

static const char *panel_env(Panel p) { return p == Panel::ST7789 ? "cyd2usb" : "cyd"; }

#if defined(ST7789_DRIVER)
static constexpr Panel BUILD_PANEL = Panel::ST7789;
#else
static constexpr Panel BUILD_PANEL = Panel::ILI9341;
#endif

static uint8_t panel_read8(SPIClass &spi, uint8_t cmd) {
  spi.beginTransaction(SPISettings(1000000, MSBFIRST, SPI_MODE0));
  digitalWrite(TFT_CS, LOW);
  digitalWrite(TFT_DC, LOW);
  spi.transfer(cmd);
  digitalWrite(TFT_DC, HIGH);
  const uint8_t value = spi.transfer(0x00);
  digitalWrite(TFT_CS, HIGH);
  spi.endTransaction();
  return value;
}

static uint8_t ili9341_read_param(SPIClass &spi, uint8_t cmd, uint8_t index) {
  spi.beginTransaction(SPISettings(1000000, MSBFIRST, SPI_MODE0));
  digitalWrite(TFT_CS, LOW);
  digitalWrite(TFT_DC, LOW);
  spi.transfer(0xD9);
  digitalWrite(TFT_DC, HIGH);
  spi.transfer(0x10 + index);
  digitalWrite(TFT_CS, HIGH);
  digitalWrite(TFT_CS, LOW);
  digitalWrite(TFT_DC, LOW);
  spi.transfer(cmd);
  digitalWrite(TFT_DC, HIGH);
  const uint8_t value = spi.transfer(0x00);
  digitalWrite(TFT_CS, HIGH);
  spi.endTransaction();
  return value;
}

static Panel probe_panel(uint32_t &id_out) {
  pinMode(TFT_CS, OUTPUT);
  digitalWrite(TFT_CS, HIGH);
  pinMode(TFT_DC, OUTPUT);
  digitalWrite(TFT_DC, HIGH);

  SPIClass spi(HSPI);
  spi.begin(TFT_SCLK, TFT_MISO, TFT_MOSI, -1);
  delay(10);

  const uint8_t id1 = panel_read8(spi, 0xDA);
  const uint8_t id2 = panel_read8(spi, 0xDB);
  const uint8_t id3 = panel_read8(spi, 0xDC);
  id_out = (uint32_t(id1) << 16) | (uint32_t(id2) << 8) | id3;

  Panel panel = Panel::Unknown;
  if (id1 == 0x85 && id3 == 0x52) {
    panel = Panel::ST7789;
  } else if (id_out == ID_ILI9341_INV) {
    panel = Panel::ILI9341_INV;
  } else if (ili9341_read_param(spi, 0xD3, 2) == 0x93 && ili9341_read_param(spi, 0xD3, 3) == 0x41) {
    panel = Panel::ILI9341;
  }

  spi.end();
  return panel;
}

static void show_panel_warning(Panel detected) {
  lv_obj_t *label = lv_label_create(lv_layer_top());
  lv_label_set_text_fmt(label, "Man hinh la %s nhung firmware build cho %s.\nNap lai bang: cyd.cmd -Env %s",
                        panel_name(detected), panel_name(BUILD_PANEL), panel_env(detected));
  lv_obj_set_width(label, CYD_SCREEN_W);
  lv_obj_set_style_bg_color(label, lv_color_hex(0xB00020), 0);
  lv_obj_set_style_bg_opa(label, LV_OPA_COVER, 0);
  lv_obj_set_style_text_color(label, lv_color_white(), 0);
  lv_obj_set_style_pad_all(label, 6, 0);
  lv_obj_align(label, LV_ALIGN_BOTTOM_MID, 0, 0);
}

static void scroll_write(int32_t offset) {
  const bool reverse = flipped != (CYD_SCROLL_INVERT != 0);
  const uint16_t vsp = reverse ? (CYD_SCREEN_W - offset) % CYD_SCREEN_W : offset;
  tft.writecommand(0x37);
  tft.writedata(vsp >> 8);
  tft.writedata(vsp & 0xFF);
  scroll_shown = offset;
}

bool cyd_scroll_supported() { return HW_SCROLL; }

void cyd_scroll_to(int32_t offset) {
  scroll_wanted = ((offset % CYD_SCREEN_W) + CYD_SCREEN_W) % CYD_SCREEN_W;
}

void cyd_scroll_apply() {
  if (!HW_SCROLL || scroll_wanted == scroll_shown) return;
  tft.dmaWait();
  scroll_write(scroll_wanted);
}

void cyd_input_block(bool blocked) { input_blocked = blocked; }

static uint32_t tick_ms() { return millis(); }

static void lvgl_log(lv_log_level_t level, const char *buf) {
  LV_UNUSED(level);
  Serial.print("[LVGL] ");
  Serial.print(buf);
}

static void flush_cb(lv_display_t *disp, const lv_area_t *area, uint8_t *px_map) {
  const uint32_t w = lv_area_get_width(area);
  const uint32_t h = lv_area_get_height(area);
  tft.dmaWait();
  tft.setAddrWindow(area->x1, area->y1, w, h);
  tft.pushPixelsDMA(reinterpret_cast<uint16_t *>(px_map), w * h);
  if (scroll_wanted != scroll_shown && lv_display_flush_is_last(disp)) cyd_scroll_apply();
}

static void flush_wait_cb(lv_display_t *disp) {
  LV_UNUSED(disp);
  tft.dmaWait();
}

static CydTouch touch_state = {};

static constexpr uint32_t RELEASE_HOLD_MS = 50;
static uint32_t last_contact_ms = 0;

void cyd_touch_state(CydTouch *out) { *out = touch_state; }

static void touch_read_cb(lv_indev_t *indev, lv_indev_data_t *data) {
  LV_UNUSED(indev);
  const uint32_t now = millis();
  touch_state.ms = now;
#if defined(CYD_PERF_DEMO) && CYD_PERF_DEMO == 3
  touch_state.pressed = false;
  data->state = LV_INDEV_STATE_RELEASED;
  return;
#endif
  if (touch.tirqTouched() && touch.touched()) {
    const TS_Point p = touch.getPoint();
    int32_t x = constrain(map(p.x, TOUCH_X_MIN, TOUCH_X_MAX, 0, CYD_SCREEN_W - 1), 0, CYD_SCREEN_W - 1);
    int32_t y = constrain(map(p.y, TOUCH_Y_MIN, TOUCH_Y_MAX, 0, CYD_SCREEN_H - 1), 0, CYD_SCREEN_H - 1);
    if (flipped) {
      x = CYD_SCREEN_W - 1 - x;
      y = CYD_SCREEN_H - 1 - y;
    }
    if (!touch_state.pressed) {
      touch_state.start_x = x;
      touch_state.start_y = y;
      touch_state.start_ms = now;
      Serial.printf("[CYD] cham x=%d y=%d (tho %d,%d)\n", (int)x, (int)y, p.x, p.y);
    }
    touch_state.pressed = true;
    touch_state.x = x;
    touch_state.y = y;
    last_contact_ms = now;
  } else if (touch_state.pressed && now - last_contact_ms < RELEASE_HOLD_MS) {
  } else {
    touch_state.pressed = false;
  }
  data->point.x = touch_state.x;
  data->point.y = touch_state.y;
  data->state = touch_state.pressed && !input_blocked ? LV_INDEV_STATE_PRESSED : LV_INDEV_STATE_RELEASED;
}

void cyd_init() {
  Serial.setRxBufferSize(16384);
  Serial.begin(921600);
  delay(50);
  Serial.println();
  Serial.printf("[CYD] CYD-Deck khoi dong | env=%s | LVGL %d.%d.%d | Arduino-ESP32 %d.%d.%d\n",
                panel_env(BUILD_PANEL), LVGL_VERSION_MAJOR, LVGL_VERSION_MINOR, LVGL_VERSION_PATCH,
                ESP_ARDUINO_VERSION_MAJOR, ESP_ARDUINO_VERSION_MINOR, ESP_ARDUINO_VERSION_PATCH);
  Serial.printf("[CYD] %s rev%d, %lu MHz, flash %lu MB, heap trong %lu KB\n", ESP.getChipModel(),
                (int)ESP.getChipRevision(), (unsigned long)ESP.getCpuFreqMHz(),
                (unsigned long)(ESP.getFlashChipSize() >> 20), (unsigned long)(ESP.getFreeHeap() >> 10));

  uint32_t id = 0;
  const Panel panel = probe_panel(id);
  Serial.printf("CYD_PANEL=%s id=%06lX build=%s\n", panel_name(panel), (unsigned long)id, panel_name(BUILD_PANEL));
  const bool mismatch = panel != Panel::Unknown && panel_env(panel) != panel_env(BUILD_PANEL);
  if (mismatch) {
    Serial.printf("[CYD] CANH BAO: man hinh la %s nhung firmware build cho %s -> nap lai bang env %s\n",
                  panel_name(panel), panel_name(BUILD_PANEL), panel_env(panel));
  }

#if defined(CYD_INVERT)
  bool invert = CYD_INVERT;
#else
  bool invert = panel == Panel::ILI9341_INV;
#endif
  Preferences prefs;
  if (prefs.begin("cyd", true)) {
    flipped = prefs.getBool("flip", CYD_FLIP_DEFAULT);
    if (prefs.isKey("inv")) invert = prefs.getBool("inv", invert);
    prefs.end();
  }
  inverted = invert;
  tft.init();
  tft.setRotation(flipped ? 3 : 1);
  Serial.printf("[CYD] Lat man hinh 180 do: %s\n", flipped ? "bat" : "tat");
  if (HW_SCROLL) {
    tft.writecommand(0x33);
    tft.writedata(0);
    tft.writedata(0);
    tft.writedata(CYD_SCREEN_W >> 8);
    tft.writedata(CYD_SCREEN_W & 0xFF);
    tft.writedata(0);
    tft.writedata(0);
    scroll_write(0);
  }

  if (invert) tft.invertDisplay(true);
  Serial.printf("[CYD] Dao mau: %s\n", invert ? "bat" : "tat");
#if defined(ST7789_DRIVER)
  tft.writecommand(0x26);
  tft.writedata(2);
  delay(120);
  tft.writecommand(0x26);
  tft.writedata(1);
#endif
  tft.fillScreen(TFT_BLACK);

  pinMode(LED_R, OUTPUT);
  pinMode(LED_G, OUTPUT);
  pinMode(LED_B, OUTPUT);
  led_write(0);

  touchSpi.begin(XPT2046_CLK, XPT2046_MISO, XPT2046_MOSI, XPT2046_CS);
  touch.begin(touchSpi);
  touch.setRotation(1);

  lv_init();
  lv_tick_set_cb(tick_ms);
  lv_log_register_print_cb(lvgl_log);

  void *draw_buf1 = heap_caps_malloc(DRAW_BUF_BYTES, MALLOC_CAP_DMA | MALLOC_CAP_INTERNAL);
  void *draw_buf2 = heap_caps_malloc(DRAW_BUF_BYTES, MALLOC_CAP_DMA | MALLOC_CAP_INTERNAL);
  if (draw_buf1 == nullptr || draw_buf2 == nullptr) {
    Serial.println("[CYD] LOI: khong du RAM cho bo dem ve LVGL");
    while (true) delay(1000);
  }
  tft.initDMA();
  tft.startWrite();

  lv_display_t *disp = lv_display_create(CYD_SCREEN_W, CYD_SCREEN_H);
  lv_display_set_color_format(disp, LV_COLOR_FORMAT_RGB565_SWAPPED);
  lv_display_set_flush_cb(disp, flush_cb);
  lv_display_set_flush_wait_cb(disp, flush_wait_cb);
  lv_display_set_buffers(disp, draw_buf1, draw_buf2, DRAW_BUF_BYTES, LV_DISPLAY_RENDER_MODE_PARTIAL);

  lv_indev_t *indev = lv_indev_create();
  lv_indev_set_type(indev, LV_INDEV_TYPE_POINTER);
  lv_indev_set_read_cb(indev, touch_read_cb);
  lv_indev_set_scroll_throw(indev, 8);
  lv_indev_set_gesture_min_velocity(indev, 2);

  if (mismatch) show_panel_warning(panel);

  Serial.printf("[CYD] San sang: man %dx%d, heap trong %lu KB\n", CYD_SCREEN_W, CYD_SCREEN_H,
                (unsigned long)(ESP.getFreeHeap() >> 10));
}

bool cyd_flipped() { return flipped; }

void cyd_set_flipped(bool value) {
  flipped = value;
  Preferences prefs;
  if (prefs.begin("cyd", false)) {
    prefs.putBool("flip", value);
    prefs.end();
  }
  tft.dmaWait();
  tft.setRotation(flipped ? 3 : 1);
  if (HW_SCROLL) {
    scroll_wanted = 0;
    scroll_write(0);
  }
  lv_obj_invalidate(lv_screen_active());
  lv_obj_invalidate(lv_layer_top());
  Serial.printf("[CYD] Lat man hinh 180 do: %s\n", flipped ? "bat" : "tat");
}

bool cyd_inverted() { return inverted; }

void cyd_set_inverted(bool value) {
  inverted = value;
  Preferences prefs;
  if (prefs.begin("cyd", false)) {
    prefs.putBool("inv", value);
    prefs.end();
  }
  tft.dmaWait();
  tft.invertDisplay(value);
  Serial.printf("[CYD] Dao mau: %s\n", value ? "bat" : "tat");
}

void cyd_loop() {
  const uint32_t next_ms = lv_timer_handler();
  if (led_steps > 0 && millis() >= led_next) {
    led_write(led_steps % 2 == 0 ? led_color : 0);
    led_steps--;
    led_next = millis() + LED_STEP_MS;
  }
  delay(next_ms < 1 ? 1 : next_ms > LOOP_MAX_SLEEP_MS ? LOOP_MAX_SLEEP_MS : next_ms);
}

#if CYD_PERF
uint16_t cyd_read_pixel(int32_t x, int32_t y) {
  tft.dmaWait();
  return tft.readPixel(x, y);
}
#endif
