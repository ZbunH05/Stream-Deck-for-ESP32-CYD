// Kho ảnh phím và bộ phím đã lưu, trong phân vùng flash "deckimg" (partitions.csv, ~2,4 MB).
//
//   0 .. 16 KB      bộ phím đã lưu: đầu mục {magic "DLAY", độ dài, CRC-32} rồi các gói LAYOUT_* nối nhau (link.cpp)
//   16 KB ..        các ô ảnh 12 KB: đầu ô {magic "DIMG", mã ảnh, cỡ, 0} 16 byte rồi 72×72 RGB565 đảo byte
//
// Mã ảnh = CRC-32 (như zlib) của 10.368 byte điểm ảnh: PC và bo cùng tính, nên bo biết ảnh nào đã có mà không phải
// nhận lại, và kiểm tra được ảnh nhận đủ, đúng.
//
// Bo đọc cả phân vùng qua esp_partition_mmap: lv_image_dsc_t.data trỏ thẳng vào flash (LVGL đọc qua cache như đọc
// font), không tốn RAM cho ảnh. Ghi flash chỉ ở luồng giao diện (link.cpp), lúc PC gửi ảnh mới.
#pragma once

#include <lvgl.h>
#include <stddef.h>
#include <stdint.h>

constexpr uint32_t STORE_IMG_BYTES = 72 * 72 * 2;

bool store_init();  // gọi một lần lúc khởi động
int store_slots();
int store_used();

// Ảnh có mã `hash` (null: chưa có)
const lv_image_dsc_t *store_find(uint32_t hash);

// Nhận một ảnh: begin chọn ô (ô trống, hoặc ô có ảnh không nằm trong `keep`) và xoá ô đó; write ghi tuần tự từng
// đoạn; finish kiểm tra CRC rồi ghi đầu ô. begin trả 1 nếu đã có ảnh này, 0 nếu sẵn sàng nhận, -1 nếu lỗi.
int store_begin(uint32_t hash, const uint32_t *keep, int keep_count);
bool store_write(uint32_t hash, uint32_t offset, const uint8_t *data, size_t length);
bool store_finish(uint32_t hash);

// Bộ phím đã lưu
bool store_save_layout(const uint8_t *data, size_t length);
const uint8_t *store_layout(size_t *length);  // null nếu chưa có

uint32_t store_crc32(uint32_t crc, const uint8_t *data, size_t length);  // bắt đầu bằng crc = 0
