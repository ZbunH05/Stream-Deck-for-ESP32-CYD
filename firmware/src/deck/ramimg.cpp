// Xem ramimg.h.
//
// Ảnh mới luôn nhận vào một bộ đệm riêng ("đang nhận"); nhận đủ thì tráo với bộ đệm của ô. Ảnh đang hiện trên phím
// không bao giờ bị ghi dở. Bộ nhớ: RAMIMG_SLOTS + 1 bộ đệm 10 KB, cấp phát dần khi cần.
#include "ramimg.h"

#include <Arduino.h>
#include <stdlib.h>
#include <string.h>

#include "store.h"

namespace {

struct Slot {
  int16_t page = -1, key = -1;
  uint8_t *pixels = nullptr;  // ảnh đang dùng (ready)
  bool ready = false;
  uint32_t used_ms = 0;
  lv_image_dsc_t dsc;
};

Slot slots[RAMIMG_SLOTS];

uint8_t *incoming = nullptr;  // ảnh đang nhận
int16_t in_page = -1, in_key = -1;
uint32_t in_filled = 0;
int16_t evicted_page = -1, evicted_key = -1;

Slot *find(uint8_t page, uint8_t key) {
  for (Slot &s : slots) {
    if (s.page == page && s.key == key) return &s;
  }
  return nullptr;
}

// Ô cho (trang, phím): ô cũ của nó, ô trống, hay ô dùng lâu nhất
Slot *take(uint8_t page, uint8_t key) {
  Slot *s = find(page, key);
  if (s) return s;
  Slot *oldest = nullptr;
  for (Slot &c : slots) {
    if (c.page < 0) return &c;
    if (!oldest || c.used_ms < oldest->used_ms) oldest = &c;
  }
  return oldest;
}

void make_dsc(Slot &s) {
  lv_image_dsc_t &d = s.dsc;
  memset(&d, 0, sizeof d);
  d.header.magic = LV_IMAGE_HEADER_MAGIC;
  d.header.cf = LV_COLOR_FORMAT_RGB565_SWAPPED;
  d.header.w = 72;
  d.header.h = 72;
  d.header.stride = 144;
  d.data_size = STORE_IMG_BYTES;
  d.data = s.pixels;
}

}  // namespace

int rle_decode(const uint8_t *in, size_t in_len, uint8_t *out, size_t out_max) {
  size_t i = 0, o = 0;
  while (i < in_len) {
    const uint8_t n = in[i++];
    if (n < 0x80) {
      const size_t bytes = ((size_t)n + 1) * 2;
      if (i + bytes > in_len || o + bytes > out_max) return -1;
      memcpy(out + o, in + i, bytes);
      i += bytes;
      o += bytes;
    } else {
      const size_t count = (size_t)n - 0x7E;
      if (i + 2 > in_len || o + count * 2 > out_max) return -1;
      const uint8_t hi = in[i], lo = in[i + 1];
      i += 2;
      for (size_t k = 0; k < count; k++) {
        out[o++] = hi;
        out[o++] = lo;
      }
    }
  }
  return (int)o;
}

bool ramimg_write(uint8_t page, uint8_t key, uint32_t offset, const uint8_t *data, size_t length, bool *complete) {
  *complete = false;
  if (offset == 0) {
    in_page = page;
    in_key = key;
    in_filled = 0;
  }
  if (in_page != page || in_key != key || offset != in_filled || offset + length > STORE_IMG_BYTES) return false;
  if (!incoming) {
    incoming = static_cast<uint8_t *>(malloc(STORE_IMG_BYTES));
    if (!incoming) return false;
  }
  memcpy(incoming + offset, data, length);
  in_filled = offset + length;
  if (in_filled < STORE_IMG_BYTES) return true;

  Slot *s = take(page, key);
  if (s->page >= 0 && (s->page != page || s->key != key)) {  // lấy ô của phím khác: phím đó phải vẽ lại ảnh gốc
    evicted_page = s->page;
    evicted_key = s->key;
  }
  uint8_t *old = s->pixels;  // bộ đệm cũ của ô thành bộ "đang nhận" lần sau
  s->pixels = incoming;
  incoming = old;
  s->page = page;
  s->key = key;
  s->ready = true;
  s->used_ms = millis();
  make_dsc(*s);
  in_page = in_key = -1;
  in_filled = 0;
  *complete = true;
  return true;
}

const lv_image_dsc_t *ramimg_find(uint8_t page, uint8_t key) {
  Slot *s = find(page, key);
  if (!s || !s->ready) return nullptr;
  s->used_ms = millis();
  return &s->dsc;
}

void ramimg_clear(uint8_t page, uint8_t key) {
  Slot *s = find(page, key);
  if (!s) return;
  s->page = s->key = -1;
  s->ready = false;  // giữ bộ đệm để dùng lại
}

bool ramimg_take_evicted(uint8_t *page, uint8_t *key) {
  if (evicted_page < 0) return false;
  *page = (uint8_t)evicted_page;
  *key = (uint8_t)evicted_key;
  evicted_page = evicted_key = -1;
  return true;
}

void ramimg_clear_all() {
  for (Slot &s : slots) {
    s.page = s.key = -1;
    s.ready = false;
  }
  in_page = in_key = -1;
  in_filled = 0;
}
