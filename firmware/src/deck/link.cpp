// Xem link.h.
#include "link.h"

#include <Arduino.h>
#include <stdlib.h>
#include <string.h>

#include "deck.h"
#include "lang.h"
#include "ramimg.h"
#include "sample.h"
#include "store.h"

namespace {

enum : uint8_t {
  HELLO = 0x01,
  LAYOUT_BEGIN = 0x11,
  LAYOUT_PAGE = 0x12,
  LAYOUT_END = 0x13,
  IMG_BEGIN = 0x14,
  IMG_DATA = 0x15,
  IMG_END = 0x16,
  STATE = 0x17,
  INFO = 0x18,
  ACK = 0x19,
  OBS = 0x1A,
  KEY_IMAGE = 0x1B,
};

constexpr uint16_t MAX_PAYLOAD = 640;
constexpr uint32_t ONLINE_MS = 4000;
constexpr size_t LAYOUT_MAX = 16384 - 16;
constexpr size_t CHUNK_MAX = 2048;  // một gói ảnh giải nén ra tối đa chừng này byte (agent chia gói theo đó)

enum class Rx : uint8_t { Magic1, Magic2, Type, Length1, Length2, Payload, Crc };
Rx rx = Rx::Magic1;
uint8_t frame_type = 0, crc = 0;
uint16_t frame_length = 0, received = 0;
uint8_t payload[MAX_PAYLOAD];
uint8_t chunk[CHUNK_MAX];
uint32_t bad_frames = 0;
uint32_t last_info_ms = 0;

// bộ phím đang nhận: các gói 11/12 chép nguyên vào đây, LAYOUT_END kiểm CRC rồi áp dụng và lưu vào flash
uint8_t *incoming = nullptr;
size_t incoming_length = 0;
bool incoming_ok = false;
uint32_t layout_crc = 0;  // CRC của bộ phím đang dùng (0: bộ phím mẫu)

uint8_t crc8(uint8_t value, uint8_t byte) {
  value ^= byte;
  for (int bit = 0; bit < 8; bit++) value = value & 0x80 ? (uint8_t)((value << 1) ^ 0x07) : (uint8_t)(value << 1);
  return value;
}

struct Reader {
  const uint8_t *p, *end;
  bool ok = true;
  uint8_t u8() {
    if (p >= end) {
      ok = false;
      return 0;
    }
    return *p++;
  }
  uint16_t u16() {
    const uint16_t low = u8();
    return (uint16_t)(low | u8() << 8);
  }
  uint32_t u32() {
    const uint32_t low = u16();
    return low | (uint32_t)u16() << 16;
  }
  void text(char *out, size_t size) {
    const uint8_t length = u8();
    size_t at = 0;
    for (uint8_t i = 0; i < length; i++) {
      const uint8_t c = u8();
      if (at + 1 < size) out[at++] = (char)c;
    }
    out[at] = '\0';
  }
  bool more() const { return p < end; }
};

// Phần dữ liệu ảnh của gói (sau phần đầu đã đọc): giải nén vào `chunk`. Trả số byte, -1 nếu hỏng.
int decode_chunk(Reader &r) {
  const uint8_t encoding = r.u8();
  if (!r.ok) return -1;
  const size_t length = (size_t)(r.end - r.p);
  if (encoding == 0) {
    if (length > CHUNK_MAX) return -1;
    memcpy(chunk, r.p, length);
    return (int)length;
  }
  return encoding == 1 ? rle_decode(r.p, length, chunk, CHUNK_MAX) : -1;
}

// ---------- bộ phím ----------

// Áp dụng bộ phím từ các gói 11/12 nối nhau (vừa nhận, hoặc đọc lại từ flash lúc khởi động). Dựng xong cả bộ rồi mới
// chép đè, nên gói hỏng giữa chừng không để lại bộ phím nửa cũ nửa mới.
bool apply_layout(const uint8_t *blob, size_t length) {
  static DeckProfile fresh;  // ~10 KB: không đặt trên stack của loop()
  fresh = DeckProfile{};
  bool begun = false;
  Lang language = lang();
  size_t at = 0;
  while (at + 3 <= length) {
    const uint8_t type = blob[at];
    const uint16_t size = (uint16_t)(blob[at + 1] | blob[at + 2] << 8);
    if (at + 3 + size > length) return false;
    Reader r{blob + at + 3, blob + at + 3 + size};
    if (type == LAYOUT_BEGIN) {
      fresh.page_count = r.u8();
      fresh.top_pages = r.u8();
      r.text(fresh.name, sizeof fresh.name);
      if (r.more()) language = r.u8() == 0 ? Lang::Vi : Lang::En;
      if (fresh.page_count > DECK_MAX_PAGES) fresh.page_count = DECK_MAX_PAGES;
      if (fresh.top_pages > fresh.page_count) fresh.top_pages = fresh.page_count;
      begun = r.ok;
    } else if (type == LAYOUT_PAGE && begun) {
      const uint8_t index = r.u8();
      if (index >= fresh.page_count) return false;
      DeckPage &page = fresh.pages[index];
      page.parent = (int8_t)r.u8();
      page.bar = r.u8() == 1 ? BarMode::Obs : BarMode::Info;
      r.text(page.name, sizeof page.name);
      for (DeckKey &key : page.keys) {
        const uint8_t kind = r.u8();
        key.kind = kind <= (uint8_t)KeyKind::Plugin ? (KeyKind)kind : KeyKind::Empty;
        key.target = r.u8();
        key.image_hash = r.u32();
        key.image_on_hash = r.u32();
        r.text(key.title, sizeof key.title);
        r.text(key.title_on, sizeof key.title_on);
      }
      if (!r.ok) return false;
    }
    at += 3 + size;
  }
  if (!begun || fresh.page_count == 0) return false;
  lang_set(language);
  ramimg_clear_all();
  deck_profile() = fresh;  // trạng thái bật/tắt, nhãn lúc chạy và ảnh RAM PC gửi lại ngay sau đó
  return true;
}

// Mọi mã ảnh bộ phím đang dùng: khi kho đầy chỉ ghi đè ảnh không còn dùng
int keep_list(uint32_t *out, int max) {
  const DeckProfile &p = deck_profile();
  int n = 0;
  for (int i = 0; i < p.page_count; i++) {
    for (const DeckKey &key : p.pages[i].keys) {
      if (key.image_hash && n < max) out[n++] = key.image_hash;
      if (key.image_on_hash && n < max) out[n++] = key.image_on_hash;
    }
  }
  return n;
}

// ---------- từng loại gói ----------

void on_hello(Reader &r) {
  r.u8();
  char host[32];
  r.text(host, sizeof host);
  Serial.printf("[DECK] chao 2 %d/%d %08lx %s\n", store_used(), store_slots(), (unsigned long)layout_crc, host);
  // PC vừa (nối) lại: báo trang đang hiện để nó gửi ảnh RAM và trạng thái của trang này
  if (deck_page() >= 0) link_page(deck_page());
}

void keep_frame() {
  if (!incoming || !incoming_ok) return;
  if (incoming_length + 3 + frame_length > LAYOUT_MAX) {
    incoming_ok = false;
    return;
  }
  incoming[incoming_length++] = frame_type;
  incoming[incoming_length++] = (uint8_t)(frame_length & 0xFF);
  incoming[incoming_length++] = (uint8_t)(frame_length >> 8);
  memcpy(incoming + incoming_length, payload, frame_length);
  incoming_length += frame_length;
}

void on_layout_end(Reader &r) {
  const uint32_t want = r.u32();
  bool ok = incoming && incoming_ok && r.ok;
  const uint32_t got = ok ? store_crc32(0, incoming, incoming_length) : 0;
  ok = ok && got == want && apply_layout(incoming, incoming_length);
  if (ok) {
    layout_crc = got;
    if (!store_save_layout(incoming, incoming_length)) Serial.println("[KHO] LOI: khong luu duoc bo phim");
    deck_refresh();
    Serial.printf("[DECK] bo %08lx ok\n", (unsigned long)got);
    link_page(deck_page());
  } else {
    Serial.printf("[DECK] bo %08lx loi\n", (unsigned long)got);
  }
  free(incoming);
  incoming = nullptr;
}

void on_img_begin(Reader &r) {
  const uint32_t hash = r.u32();
  if (!r.ok) return;
  static uint32_t keep[DECK_MAX_PAGES * DECK_KEYS * 2];
  const int n = keep_list(keep, sizeof keep / sizeof keep[0]);
  const int status = store_begin(hash, keep, n);
  Serial.printf("[DECK] anh %08lx %s\n", (unsigned long)hash, status > 0 ? "co" : status == 0 ? "san" : "loi");
}

void on_img_data(Reader &r) {
  const uint32_t hash = r.u32();
  const uint16_t offset = r.u16();
  const int length = decode_chunk(r);
  if (length >= 0 && store_write(hash, offset, chunk, (size_t)length)) {
    Serial.printf("[DECK] anh %08lx ok %u\n", (unsigned long)hash, (unsigned)(offset + length));
  } else {
    Serial.printf("[DECK] anh %08lx loi\n", (unsigned long)hash);
  }
}

void on_img_end(Reader &r) {
  const uint32_t hash = r.u32();
  if (!r.ok) return;
  const bool ok = store_finish(hash);
  Serial.printf("[DECK] anh %08lx %s\n", (unsigned long)hash, ok ? "xong" : "loi");
  if (ok) deck_refresh();
}

void on_key_image(Reader &r) {
  const uint8_t page = r.u8(), key = r.u8();
  const uint16_t offset = r.u16();
  if (!r.ok || key >= DECK_KEYS) return;
  if (offset == 0xFFFF) {  // về ảnh của bộ phím
    ramimg_clear(page, key);
    if (deck_page() == page) deck_refresh_key(key);
    return;
  }
  const int length = decode_chunk(r);
  bool complete = false;
  if (length < 0 || !ramimg_write(page, key, offset, chunk, (size_t)length, &complete)) {
    Serial.printf("[DECK] ram %u %u loi\n", page, key);
    return;
  }
  if (!complete) return;
  Serial.printf("[DECK] ram %u %u ok\n", page, key);
  uint8_t lost_page, lost_key;
  if (ramimg_take_evicted(&lost_page, &lost_key) && deck_page() == lost_page) deck_refresh_key(lost_key);
  if (deck_page() == page) deck_refresh_key(key);
}

void on_state(Reader &r) {
  const uint8_t page = r.u8(), key = r.u8(), on = r.u8();
  char live[sizeof(DeckKey::live)];
  r.text(live, sizeof live);
  if (!r.ok) return;
  DeckProfile &p = deck_profile();
  if (page >= p.page_count || key >= DECK_KEYS) return;
  DeckKey &k = p.pages[page].keys[key];
  if (k.on == (on != 0) && strcmp(k.live, live) == 0) return;
  k.on = on != 0;
  strlcpy(k.live, live, sizeof k.live);
  if (deck_page() == page) deck_refresh_key(key);
}

void on_info(Reader &r) {
  DeckInfo next = deck_info();
  const uint8_t flags = r.u8();
  r.text(next.clock, sizeof next.clock);
  next.cpu = r.u8();
  next.ram = r.u8();
  r.text(next.media_title, sizeof next.media_title);
  r.text(next.media_sub, sizeof next.media_sub);
  next.progress = r.u16();
  if (!r.ok) return;
  next.playing = (flags & 1) != 0;
  next.online = true;
  last_info_ms = millis();
  DeckInfo &info = deck_info();
  const bool changed = info.online != next.online || info.playing != next.playing || info.cpu != next.cpu ||
                       info.ram != next.ram || info.progress != next.progress || strcmp(info.clock, next.clock) ||
                       strcmp(info.media_title, next.media_title) || strcmp(info.media_sub, next.media_sub);
  info = next;
  if (changed) deck_refresh_info();
}

void on_obs(Reader &r) {
  DeckInfo next = deck_info();
  const uint8_t flags = r.u8();
  r.text(next.obs_time, sizeof next.obs_time);
  r.text(next.obs_scene, sizeof next.obs_scene);
  next.obs_dropped = r.u16();
  next.obs_cpu = r.u8();
  if (!r.ok) return;
  next.obs_connected = flags & 1;
  next.obs_rec = flags & 2;
  next.obs_live = flags & 4;
  next.obs_paused = flags & 8;
  DeckInfo &info = deck_info();
  const bool changed = info.obs_connected != next.obs_connected || info.obs_rec != next.obs_rec ||
                       info.obs_live != next.obs_live || info.obs_paused != next.obs_paused ||
                       info.obs_dropped != next.obs_dropped || info.obs_cpu != next.obs_cpu ||
                       strcmp(info.obs_time, next.obs_time) || strcmp(info.obs_scene, next.obs_scene);
  info = next;
  if (changed) deck_refresh_info();
}

void on_ack(Reader &r) {
  const uint8_t page = r.u8(), key = r.u8(), ok = r.u8();
  if (r.ok) deck_feedback(page, key, ok != 0);
}

void dispatch() {
  Reader r{payload, payload + frame_length};
  switch (frame_type) {
    case HELLO: on_hello(r); break;
    case LAYOUT_BEGIN:
      free(incoming);
      incoming = static_cast<uint8_t *>(malloc(LAYOUT_MAX));
      incoming_length = 0;
      incoming_ok = incoming != nullptr;
      keep_frame();
      break;
    case LAYOUT_PAGE: keep_frame(); break;
    case LAYOUT_END: on_layout_end(r); break;
    case IMG_BEGIN: on_img_begin(r); break;
    case IMG_DATA: on_img_data(r); break;
    case IMG_END: on_img_end(r); break;
    case STATE: on_state(r); break;
    case INFO: on_info(r); break;
    case ACK: on_ack(r); break;
    case OBS: on_obs(r); break;
    case KEY_IMAGE: on_key_image(r); break;
    default: break;  // gói của trang Máy tính CYD-EEZ (STATS, PROCS): bỏ qua
  }
}

}  // namespace

void link_init() {
  lang_load();
  size_t length = 0;
  const uint8_t *blob = store_layout(&length);
  if (blob && apply_layout(blob, length)) {
    layout_crc = store_crc32(0, blob, length);
    Serial.printf("[DECK] Nap bo phim da luu (%u byte, %08lx)\n", (unsigned)length, (unsigned long)layout_crc);
  } else {
    sample_load();
    Serial.println("[DECK] Chua co bo phim tu PC: dung bo phim mau");
  }
}

void link_poll() {
  int budget = 4096;  // không giữ loop() quá lâu khi PC gửi dồn (921600 baud ≈ 90 KB/s)
  while (budget-- > 0 && Serial.available() > 0) {
    const uint8_t b = (uint8_t)Serial.read();
    switch (rx) {
      case Rx::Magic1:
        if (b == 0xA5) rx = Rx::Magic2;
        break;
      case Rx::Magic2:
        rx = b == 0x5A ? Rx::Type : b == 0xA5 ? Rx::Magic2 : Rx::Magic1;
        break;
      case Rx::Type:
        frame_type = b;
        crc = crc8(0, b);
        rx = Rx::Length1;
        break;
      case Rx::Length1:
        frame_length = b;
        crc = crc8(crc, b);
        rx = Rx::Length2;
        break;
      case Rx::Length2:
        frame_length |= (uint16_t)b << 8;
        crc = crc8(crc, b);
        received = 0;
        if (frame_length > MAX_PAYLOAD) {
          bad_frames++;
          rx = Rx::Magic1;
        } else {
          rx = frame_length ? Rx::Payload : Rx::Crc;
        }
        break;
      case Rx::Payload:
        payload[received++] = b;
        crc = crc8(crc, b);
        if (received == frame_length) rx = Rx::Crc;
        break;
      case Rx::Crc:
        rx = Rx::Magic1;
        if (b == crc) {
          dispatch();
        } else if (++bad_frames % 20 == 1) {
          Serial.printf("[DECK] Goi hong (CRC), %lu goi\n", (unsigned long)bad_frames);
        }
        break;
    }
  }
  DeckInfo &info = deck_info();
#if defined(CYD_PERF_DEMO) && (CYD_PERF_DEMO == 3 || CYD_PERF_DEMO == 4)
  return;  // ảnh chụp và bản đo: trạng thái "đã nối" là dữ liệu mẫu, không có PC gửi INFO
#endif
  if (info.online && millis() - last_info_ms > ONLINE_MS) {
    info.online = false;
    info.media_title[0] = '\0';
    deck_refresh_info();
    Serial.println("[DECK] Mat noi voi PC");
  }
}

void link_key_down(int page, int key) { Serial.printf("[DECK] xuong %d %d\n", page, key); }

void link_key_up(int page, int key, bool cancelled) {
  Serial.printf("[DECK] %s %d %d\n", cancelled ? "huy" : "len", page, key);
}

void link_page(int page) { Serial.printf("[DECK] trang %d\n", page); }
