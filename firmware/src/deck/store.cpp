#include "store.h"

#include <Arduino.h>
#include <esp_partition.h>
#include <string.h>

namespace {

constexpr uint32_t LAYOUT_BYTES = 16384;
constexpr uint32_t SLOT_BYTES = 12288;
constexpr uint32_t HEADER_BYTES = 16;
constexpr uint32_t MAGIC_IMG = 0x474D4944;
constexpr uint32_t MAGIC_LAYOUT = 0x59414C44;
constexpr int MAX_SLOTS = 220;

struct Header {
  uint32_t magic, hash, size, reserved;
};

const esp_partition_t *part = nullptr;
const uint8_t *base = nullptr;
esp_partition_mmap_handle_t handle;
int slots = 0;
uint32_t slot_hash[MAX_SLOTS];
bool slot_valid[MAX_SLOTS];
lv_image_dsc_t dsc[MAX_SLOTS];

int pending = -1;
uint32_t pending_hash = 0, pending_next = 0, pending_crc = 0;

uint32_t slot_offset(int slot) { return LAYOUT_BYTES + (uint32_t)slot * SLOT_BYTES; }

void make_dsc(int slot) {
  lv_image_dsc_t &d = dsc[slot];
  memset(&d, 0, sizeof d);
  d.header.magic = LV_IMAGE_HEADER_MAGIC;
  d.header.cf = LV_COLOR_FORMAT_RGB565_SWAPPED;
  d.header.w = 72;
  d.header.h = 72;
  d.header.stride = 144;
  d.data_size = STORE_IMG_BYTES;
  d.data = base + slot_offset(slot) + HEADER_BYTES;
}

uint32_t crc_table[256];

void crc_init() {
  for (uint32_t i = 0; i < 256; i++) {
    uint32_t c = i;
    for (int k = 0; k < 8; k++) c = c & 1 ? 0xEDB88320u ^ (c >> 1) : c >> 1;
    crc_table[i] = c;
  }
}

}

uint32_t store_crc32(uint32_t crc, const uint8_t *data, size_t length) {
  crc = ~crc;
  for (size_t i = 0; i < length; i++) crc = crc_table[(crc ^ data[i]) & 0xFF] ^ (crc >> 8);
  return ~crc;
}

bool store_init() {
  crc_init();
  part = esp_partition_find_first((esp_partition_type_t)0x01, (esp_partition_subtype_t)0x40, "deckimg");
  if (!part) {
    Serial.println("[KHO] LOI: khong thay phan vung deckimg (partitions.csv)");
    return false;
  }
  const void *ptr = nullptr;
  if (esp_partition_mmap(part, 0, part->size, ESP_PARTITION_MMAP_DATA, &ptr, &handle) != ESP_OK) {
    Serial.println("[KHO] LOI: khong mmap duoc deckimg");
    part = nullptr;
    return false;
  }
  base = static_cast<const uint8_t *>(ptr);
  slots = (int)((part->size - LAYOUT_BYTES) / SLOT_BYTES);
  if (slots > MAX_SLOTS) slots = MAX_SLOTS;
  int used = 0;
  for (int slot = 0; slot < slots; slot++) {
    Header h;
    memcpy(&h, base + slot_offset(slot), sizeof h);
    slot_valid[slot] = h.magic == MAGIC_IMG && h.size == STORE_IMG_BYTES;
    slot_hash[slot] = slot_valid[slot] ? h.hash : 0;
    if (slot_valid[slot]) {
      make_dsc(slot);
      used++;
    }
  }
  Serial.printf("[KHO] %d/%d o anh dang dung\n", used, slots);
  return true;
}

int store_slots() { return slots; }

int store_used() {
  int used = 0;
  for (int slot = 0; slot < slots; slot++) used += slot_valid[slot] ? 1 : 0;
  return used;
}

const lv_image_dsc_t *store_find(uint32_t hash) {
  if (!part || hash == 0) return nullptr;
  for (int slot = 0; slot < slots; slot++) {
    if (slot_valid[slot] && slot_hash[slot] == hash) return &dsc[slot];
  }
  return nullptr;
}

int store_begin(uint32_t hash, const uint32_t *keep, int keep_count) {
  if (!part) return -1;
  if (store_find(hash)) return 1;
  int chosen = -1;
  for (int slot = 0; slot < slots && chosen < 0; slot++) {
    if (!slot_valid[slot]) chosen = slot;
  }
  for (int slot = 0; slot < slots && chosen < 0; slot++) {
    bool used = false;
    for (int i = 0; i < keep_count && !used; i++) used = keep[i] == slot_hash[slot];
    if (!used) chosen = slot;
  }
  if (chosen < 0) return -1;
  slot_valid[chosen] = false;
  slot_hash[chosen] = 0;
  if (esp_partition_erase_range(part, slot_offset(chosen), SLOT_BYTES) != ESP_OK) return -1;
  pending = chosen;
  pending_hash = hash;
  pending_next = 0;
  pending_crc = 0;
  return 0;
}

bool store_write(uint32_t hash, uint32_t offset, const uint8_t *data, size_t length) {
  if (pending < 0 || hash != pending_hash || offset != pending_next) return false;
  if (offset + length > STORE_IMG_BYTES) return false;
  if (esp_partition_write(part, slot_offset(pending) + HEADER_BYTES + offset, data, length) != ESP_OK) return false;
  pending_crc = store_crc32(pending_crc, data, length);
  pending_next += length;
  return true;
}

bool store_finish(uint32_t hash) {
  if (pending < 0 || hash != pending_hash) return false;
  const int slot = pending;
  pending = -1;
  if (pending_next != STORE_IMG_BYTES || pending_crc != hash) return false;
  if (store_crc32(0, base + slot_offset(slot) + HEADER_BYTES, STORE_IMG_BYTES) != hash) {
    Serial.println("[KHO] LOI: doc lai qua mmap khong khop (cache cu?)");
    return false;
  }
  const Header h = {MAGIC_IMG, hash, STORE_IMG_BYTES, 0};
  if (esp_partition_write(part, slot_offset(slot), &h, sizeof h) != ESP_OK) return false;
  slot_hash[slot] = hash;
  slot_valid[slot] = true;
  make_dsc(slot);
  return true;
}

bool store_save_layout(const uint8_t *data, size_t length) {
  if (!part || length + HEADER_BYTES > LAYOUT_BYTES) return false;
  if (esp_partition_erase_range(part, 0, LAYOUT_BYTES) != ESP_OK) return false;
  if (esp_partition_write(part, HEADER_BYTES, data, length) != ESP_OK) return false;
  const Header h = {MAGIC_LAYOUT, (uint32_t)length, store_crc32(0, data, length), 0};
  return esp_partition_write(part, 0, &h, sizeof h) == ESP_OK;
}

const uint8_t *store_layout(size_t *length) {
  if (!part) return nullptr;
  Header h;
  memcpy(&h, base, sizeof h);
  if (h.magic != MAGIC_LAYOUT || h.hash == 0 || h.hash + HEADER_BYTES > LAYOUT_BYTES) return nullptr;
  if (store_crc32(0, base + HEADER_BYTES, h.hash) != h.size) return nullptr;
  *length = h.hash;
  return base + HEADER_BYTES;
}
