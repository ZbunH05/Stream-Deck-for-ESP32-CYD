// Ảnh phím giữ trong RAM: ảnh plugin đổi liên tục (đồng hồ, biểu đồ CPU, bìa...) không ghi vào flash mỗi lần đổi.
// Mỗi ô 72×72 RGB565 đảo byte (10.368 byte), tối đa RAMIMG_SLOTS ô, cấp phát khi cần; hết chỗ thì lấy ô dùng lâu nhất.
// Ảnh gắn với (trang, phím); PC gửi lại khi trang hiện ra nên mất ô cũ cũng không sao.
//
// Nén RLE dùng cho cả gói ảnh vào kho flash (IMG_DATA) và gói ảnh RAM (KEY_IMAGE), tính theo điểm ảnh 16 bit:
//   byte n < 0x80: n + 1 điểm ảnh viết thẳng theo sau (2 byte mỗi điểm, đúng thứ tự RGB565 đảo byte)
//   byte n ≥ 0x80: điểm ảnh theo sau lặp lại n − 0x7E lần (2..129)
#pragma once

#include <lvgl.h>
#include <stddef.h>
#include <stdint.h>

constexpr int RAMIMG_SLOTS = 8;

// Giải nén `in` vào `out` (tối đa out_max byte). Trả số byte đã ghi, -1 nếu dữ liệu hỏng hay tràn.
int rle_decode(const uint8_t *in, size_t in_len, uint8_t *out, size_t out_max);

// Ghi một đoạn đã giải nén vào ảnh RAM của (trang, phím); offset 0 bắt đầu ảnh mới. Trả true khi ảnh đã đủ.
bool ramimg_write(uint8_t page, uint8_t key, uint32_t offset, const uint8_t *data, size_t length, bool *complete);
const lv_image_dsc_t *ramimg_find(uint8_t page, uint8_t key);  // null: không có ảnh RAM (dùng ảnh của bộ phím)
void ramimg_clear(uint8_t page, uint8_t key);
// Ảnh vừa nhận lấy mất ô của phím khác: trả phím đó (một lần) để giao diện vẽ lại nó bằng ảnh của bộ phím
bool ramimg_take_evicted(uint8_t *page, uint8_t *key);
void ramimg_clear_all();  // bộ phím mới
