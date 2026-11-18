// Lớp phần cứng của CYD (ESP32-2432S028R). Xem cyd_hw.h.
//
// Chân nối lấy theo tài liệu phần cứng CYD:
//   https://github.com/witnessmenow/ESP32-Cheap-Yellow-Display/blob/main/PINS.md
// Màn hình: bus HSPI (chân khai báo trong platformio.ini cho TFT_eSPI)
// Cảm ứng XPT2046: bus VSPI riêng, chân 25/32/39/33, ngắt ở 36

#include "cyd_hw.h"

#include <Arduino.h>
#include <Preferences.h>
#include <SPI.h>
#include <TFT_eSPI.h>
#include <XPT2046_Touchscreen.h>
#include <esp_heap_caps.h>

// ---------------------------------------------------------------------------
// Cảm ứng điện trở XPT2046
// ---------------------------------------------------------------------------
#define XPT2046_IRQ 36
#define XPT2046_MOSI 32
#define XPT2046_MISO 39
#define XPT2046_CLK 25
#define XPT2046_CS 33

// Dải giá trị thô của cảm ứng (theo ví dụ witnessmenow, mỗi bo lệch một chút).
// Chạm vào 4 góc rồi xem dòng "[CYD] cham ... (tho x,y)" trên Serial để chỉnh.
static constexpr int TOUCH_X_MIN = 200, TOUCH_X_MAX = 3700;
static constexpr int TOUCH_Y_MIN = 240, TOUCH_Y_MAX = 3800;

// Lật màn hình 180°: TFT_eSPI xoay 3 thay vì 1, toạ độ cảm ứng lật theo
#ifndef CYD_FLIP_DEFAULT
#define CYD_FLIP_DEFAULT 0
#endif
static bool flipped = CYD_FLIP_DEFAULT;
static bool inverted = false;

// Đèn LED RGB: sáng khi chân ở mức LOW
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

// Bộ đệm vẽ của LVGL: hai bộ, mỗi bộ 16 dòng (10 KB; CYD không có PSRAM). LVGL vẽ vào bộ này trong lúc DMA đẩy bộ kia
// ra màn, nên CPU không phải đứng chờ SPI. Cỡ bộ đệm là thế cân bằng đo trên bo:
//  - mỗi dải vẽ tốn ~5 ms cố định (duyệt cây đối tượng, xếp lại chữ): hai bộ 12 dòng còn chậm hơn một bộ 24 dòng không DMA;
//  - hai bộ 24 dòng nhanh nhất nhưng heap trống thấp nhất còn ~23 KB lúc chạy thường, quá sát cho HTTP + JSON thời tiết.
// Từ 17/11/2026 bớt từ 20 xuống 16 dòng để bù heap cho giao diện H: chuyển trang giờ chỉ vẽ dải dọc hẹp (một dải 30 px
// vừa 170 dòng mỗi lần), nên chỉ lần vẽ cả màn (lúc khởi động) và cuộn danh sách việc chậm đi chút ít.
static constexpr size_t DRAW_BUF_LINES = 16;
static constexpr size_t DRAW_BUF_BYTES = CYD_SCREEN_W * DRAW_BUF_LINES * sizeof(uint16_t);
static constexpr uint32_t LOOP_MAX_SLEEP_MS = 5;  // loop() ngủ tối đa chừng này giữa hai lần gọi LVGL

static TFT_eSPI tft;
static SPIClass touchSpi(VSPI);
static XPT2046_Touchscreen touch(XPT2046_CS, XPT2046_IRQ);
static volatile bool input_blocked = false;

// Cuộn ngang bằng phần cứng (xem cyd_hw.h). ST7789 (env cyd2usb) có lệnh cuộn nhưng hướng quét khác, chưa thử trên bo.
#if defined(ST7789_DRIVER)
static constexpr bool HW_SCROLL = false;
#else
static constexpr bool HW_SCROLL = true;
#endif
// Bo lạ mà lúc chuyển trang thấy hình trượt ngược chiều (trang cũ nhảy lung tung): build với -DCYD_SCROLL_INVERT=1
#ifndef CYD_SCROLL_INVERT
#define CYD_SCROLL_INVERT 0
#endif
static int32_t scroll_shown = 0, scroll_wanted = 0;

// ---------------------------------------------------------------------------
// Nhận dạng chip màn hình. Các loại đã gặp trên CYD 2.8":
//   ILI9341      bản gốc 1 cổng micro-USB, màn TN                  -> env cyd
//   ILI9341_INV  màn IPS chạy lệnh ILI9341 nhưng phải bật đảo màu -> env cyd (tự đảo màu)
//                (ID DA/DB/DC = 81 81 B3, đo trên bo thật ngày 2026-11-13)
//   ST7789       bản 2 cổng USB (CYD2USB)                          -> env cyd2usb
// ---------------------------------------------------------------------------
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

// Gửi một lệnh rồi đọc 1 byte trả về (thanh ghi 8 bit, không có chu kỳ đệm)
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

// ILI9341: đọc tham số thứ `index` của lệnh `cmd` qua lệnh ẩn 0xD9
// (cùng cách TFT_eSPI::readcommand8 và Adafruit_ILI9341 dùng)
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

// Đọc ID trước khi TFT_eSPI chiếm bus HSPI; tft.init() sau đó sẽ reset mềm màn hình.
static Panel probe_panel(uint32_t &id_out) {
  pinMode(TFT_CS, OUTPUT);
  digitalWrite(TFT_CS, HIGH);
  pinMode(TFT_DC, OUTPUT);
  digitalWrite(TFT_DC, HIGH);

  SPIClass spi(HSPI);
  spi.begin(TFT_SCLK, TFT_MISO, TFT_MOSI, -1);
  delay(10);

  // RDID1..3 (DAh, DBh, DCh): ST7789 trả về 85 85 52
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
    panel = Panel::ILI9341;  // RDID4 (D3h) của ILI9341 = 00 93 41
  }

  spi.end();
  return panel;
}

// Khi firmware build nhầm loại màn: hiện dải cảnh báo đỏ ở đáy màn hình (lớp trên cùng)
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

// ---------------------------------------------------------------------------
// Cuộn phần cứng
// ---------------------------------------------------------------------------
// VSCRSADD (0x37) đặt dòng bộ nhớ hiện ra đầu tiên. Màn nằm ngang (MADCTL có MV) thì dòng bộ nhớ là toạ độ x:
//  - xoay 1 (MV): dòng = x, nên tăng VSP thì ảnh dời sang trái;
//  - xoay 3 (MV|MX|MY, màn lật 180°): dòng = 319 − x, nên chiều ngược lại.
// Phải gọi khi DMA đã đẩy xong (không gửi lệnh chen giữa một lần đẩy hình).
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

// ---------------------------------------------------------------------------
// Cầu nối LVGL <-> phần cứng
// ---------------------------------------------------------------------------
static uint32_t tick_ms() { return millis(); }

static void lvgl_log(lv_log_level_t level, const char *buf) {
  LV_UNUSED(level);
  Serial.print("[LVGL] ");
  Serial.print(buf);
}

// LVGL vẽ xong một vùng: giao cho DMA đẩy ra màn rồi trả về ngay, CPU vẽ tiếp vùng sau vào bộ đệm còn lại.
// LVGL đã vẽ theo RGB565 đảo byte (LV_COLOR_FORMAT_RGB565_SWAPPED), đúng thứ tự SPI, nên không phải đảo nữa.
// Không gọi lv_display_flush_ready() ở đây: khi cần dùng lại bộ đệm, LVGL gọi flush_wait_cb để đợi DMA xong.
// Đang chuyển trang: vùng cuối của khung đã đẩy đi thì mới dời ảnh, để dải trang mới vừa vẽ lộ ra đúng lúc.
static void flush_cb(lv_display_t *disp, const lv_area_t *area, uint8_t *px_map) {
  const uint32_t w = lv_area_get_width(area);
  const uint32_t h = lv_area_get_height(area);
  tft.dmaWait();  // thường đã xong (LVGL đợi trước khi gọi), để chắc không đổi cửa sổ vẽ khi DMA còn chạy
  tft.setAddrWindow(area->x1, area->y1, w, h);
  tft.pushPixelsDMA(reinterpret_cast<uint16_t *>(px_map), w * h);
  if (scroll_wanted != scroll_shown && lv_display_flush_is_last(disp)) cyd_scroll_apply();
}

static void flush_wait_cb(lv_display_t *disp) {
  LV_UNUSED(disp);
  tft.dmaWait();
}

static CydTouch touch_state = {};

void cyd_touch_state(CydTouch *out) { *out = touch_state; }

static void touch_read_cb(lv_indev_t *indev, lv_indev_data_t *data) {
  LV_UNUSED(indev);
  const uint32_t now = millis();
  touch_state.ms = now;
#if defined(CYD_PERF_DEMO) && CYD_PERF_DEMO == 3
  // Bản chụp màn hình (cyd.cmd shot): bỏ qua cảm ứng, kẻo ai chạm màn lúc đang chụp làm lệch ảnh (17/11/2026 đã gặp:
  // một lần chạm nút đổi theme giữa chừng làm cả lượt sau sai theme)
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
  } else {
    touch_state.pressed = false;
  }
  // Đang chuyển trang hay trang đang chạy theo ngón tay: LVGL thấy như không ai chạm
  data->point.x = touch_state.x;
  data->point.y = touch_state.y;
  data->state = touch_state.pressed && !input_blocked ? LV_INDEV_STATE_PRESSED : LV_INDEV_STATE_RELEASED;
}

// ---------------------------------------------------------------------------
// API công khai
// ---------------------------------------------------------------------------
void cyd_init() {
  // Gói số liệu từ PC (app/pc_link.cpp) đến qua Serial. Bộ đệm nhận mặc định 256 byte chỉ đủ ~20 ms ở 115200 baud,
  // mà một khung LVGL nặng (chuyển trang, đổi theme) có thể giữ loop() lâu hơn: nâng lên 1 KB. Phải đặt trước begin().
  Serial.setRxBufferSize(1024);
  Serial.begin(115200);
  delay(50);
  Serial.println();
  Serial.printf("[CYD] CYD-Deck khoi dong | env=%s | LVGL %d.%d.%d | Arduino-ESP32 %d.%d.%d\n",
                panel_env(BUILD_PANEL), LVGL_VERSION_MAJOR, LVGL_VERSION_MINOR, LVGL_VERSION_PATCH,
                ESP_ARDUINO_VERSION_MAJOR, ESP_ARDUINO_VERSION_MINOR, ESP_ARDUINO_VERSION_PATCH);
  Serial.printf("[CYD] %s rev%d, %lu MHz, flash %lu MB, heap trong %lu KB\n", ESP.getChipModel(),
                (int)ESP.getChipRevision(), (unsigned long)ESP.getCpuFreqMHz(),
                (unsigned long)(ESP.getFlashChipSize() >> 20), (unsigned long)(ESP.getFreeHeap() >> 10));

  // 1) Nhận dạng chip màn hình. Dòng CYD_PANEL=... được tools/cyd.ps1 đọc để tự chọn env.
  uint32_t id = 0;
  const Panel panel = probe_panel(id);
  Serial.printf("CYD_PANEL=%s id=%06lX build=%s\n", panel_name(panel), (unsigned long)id, panel_name(BUILD_PANEL));
  const bool mismatch = panel != Panel::Unknown && panel_env(panel) != panel_env(BUILD_PANEL);
  if (mismatch) {
    Serial.printf("[CYD] CANH BAO: man hinh la %s nhung firmware build cho %s -> nap lai bang env %s\n",
                  panel_name(panel), panel_name(BUILD_PANEL), panel_env(panel));
  }

  // 2) Màn hình, nằm ngang 320x240; lật 180° nếu đã bật (giữ đồng hồ 2 giây để đổi)
  // Đảo màu: tự bật cho màn ILI9341_INV. Bo lạ có thể ép tay trong platformio.ini: -DCYD_INVERT=1 (luôn đảo) hoặc
  // -DCYD_INVERT=0 (không đảo). Từ 17/11/2026 trang Cài đặt cũng bật/tắt được; lựa chọn đó lưu trong NVS (khoá "inv")
  // và thắng giá trị tự nhận.
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
    // Vùng cuộn là cả 320 dòng bộ nhớ (không có phần cố định trên/dưới), bắt đầu ở dòng 0
    tft.writecommand(0x33);  // VSCRDEF: TFA 0, VSA 320, BFA 0
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
  // CYD2USB: chỉnh lại đường cong gamma cho màu đẹp hơn (witnessmenow, cyd.md)
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

  // 3) Cảm ứng trên bus VSPI riêng
  touchSpi.begin(XPT2046_CLK, XPT2046_MISO, XPT2046_MOSI, XPT2046_CS);
  touch.begin(touchSpi);
  touch.setRotation(1);

  // 4) LVGL
  lv_init();
  lv_tick_set_cb(tick_ms);
  lv_log_register_print_cb(lvgl_log);

  void *draw_buf1 = heap_caps_malloc(DRAW_BUF_BYTES, MALLOC_CAP_DMA | MALLOC_CAP_INTERNAL);
  void *draw_buf2 = heap_caps_malloc(DRAW_BUF_BYTES, MALLOC_CAP_DMA | MALLOC_CAP_INTERNAL);
  if (draw_buf1 == nullptr || draw_buf2 == nullptr) {
    Serial.println("[CYD] LOI: khong du RAM cho bo dem ve LVGL");
    while (true) delay(1000);
  }
  // Đẩy hình bằng DMA. Bus HSPI chỉ có màn hình nên giữ giao dịch SPI mở suốt (CS luôn ở mức thấp).
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
  // LVGL đọc cảm ứng mỗi LV_DEF_REFR_PERIOD (16 ms từ 17/11/2026, trước là 20 ms). Hai ngưỡng sau tính theo mỗi lần
  // đọc, nên đổi theo tỉ lệ 16/20 để cuộn quán tính và vuốt nhanh vẫn y như trước: hãm 10 % → 8 % mỗi lần đọc (quãng trôi và
  // thời gian trôi giữ nguyên), tốc độ tối thiểu của cử chỉ vuốt 3 → 2 px mỗi lần đọc (0,15 px/ms ≈ 0,125 px/ms).
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
  tft.dmaWait();  // không gửi lệnh cho màn khi DMA còn đang đẩy hình
  tft.setRotation(flipped ? 3 : 1);  // chỉ đổi hướng quét của màn, chế độ đảo màu giữ nguyên
  if (HW_SCROLL) {
    scroll_wanted = 0;
    scroll_write(0);  // chiều cuộn đổi theo hướng quét: về gốc cho chắc
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
  tft.invertDisplay(value);  // chip màn tự đảo khi quét ra: không phải vẽ lại gì
  Serial.printf("[CYD] Dao mau: %s\n", value ? "bat" : "tat");
}

void cyd_loop() {
  // lv_timer_handler() trả về số ms tới việc kế tiếp của LVGL. Trước đây loop() luôn ngủ 5 ms, nên khi đang có
  // hoạt cảnh mỗi khung phải chờ thêm tới 5 ms; giờ chỉ ngủ đúng tới lúc LVGL cần chạy lại (ít nhất 1 ms để nhường CPU).
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
  return tft.readPixel(x, y);  // tự đóng rồi mở lại giao dịch SPI ghi đang giữ
}
#endif
