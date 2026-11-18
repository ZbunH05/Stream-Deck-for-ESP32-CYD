// Đường nối với PC qua cáp USB (cổng Serial của bo, 921600 baud), bên PC là CydAgent (pc-agent\src\Deck*.cs trong
// repo). Giao thức phiên bản 2.
//
// PC → bo: gói nhị phân A5 5A | loại | độ dài 2 byte (LE) | dữ liệu | CRC-8 (đa thức 0x07, trên loại + độ dài + dữ liệu).
//   01 HELLO        phiên bản, tên máy                     → "[DECK] chao 2 <ảnh có>/<ô> <CRC bộ phím> <tên máy>"
//   11 LAYOUT_BEGIN số trang, số trang chính, tên hồ sơ, ngôn ngữ (0 Việt, 1 English)
//   12 LAYOUT_PAGE  số trang, trang cha, kiểu thanh, tên, rồi 8 phím {kiểu, đích, mã ảnh, mã ảnh bật, nhãn, nhãn bật}
//                   kiểu: 0 trống, 1 lệnh, 2 bật/tắt, 3 thư mục, 4 quay ra, 5 plugin
//   13 LAYOUT_END   CRC-32 của các gói 11/12 (loại + độ dài + dữ liệu) → "[DECK] bo <crc> ok|loi"
//   14 IMG_BEGIN    mã ảnh                                  → "[DECK] anh <mã> co|san|loi"
//   15 IMG_DATA     mã ảnh, vị trí (byte đã giải nén), cách nén (0 thô, 1 RLE: ramimg.h), dữ liệu
//                                                           → "[DECK] anh <mã> ok <vị trí kế>|loi"
//   16 IMG_END      mã ảnh                                  → "[DECK] anh <mã> xong|loi"
//   17 STATE        trang, phím, bật/tắt, nhãn lúc chạy (≤ 23 byte)
//   18 INFO         cờ (bit 0 đang phát), giờ "HH:MM", CPU %, RAM %, tên bài, "nghệ sĩ · app", tiến độ ‰ (0xFFFF không có)
//   19 ACK          trang, phím, thành công
//   1A OBS          cờ (bit 0 đã nối, 1 đang ghi, 2 đang live, 3 tạm dừng ghi), thời gian, cảnh, khung rớt, CPU % của OBS
//   1B KEY_IMAGE    trang, phím, vị trí (0xFFFF: xoá ảnh RAM), cách nén, dữ liệu → khi đủ ảnh "[DECK] ram <trang> <phím> ok"
// PC gửi INFO khi có gì đổi và ít nhất 2 giây một lần: 4 giây không có gói INFO nào thì bo coi như mất nối.
//
// Bo → PC: các dòng log chữ "[DECK] ...":
//   "xuong <trang> <phím>", "len <trang> <phím>", "huy <trang> <phím>" (vuốt hay kéo ra ngoài phím lệnh đang đè),
//   "trang <trang>" (trang vừa hiện).
// PC chờ dòng trả lời của mỗi gói ảnh rồi mới gửi gói sau, nên bo xoá/ghi flash (vài chục ms) mà không tràn bộ đệm.
#pragma once

#include <stdint.h>

void link_init();  // sau store_init(): nạp bộ phím đã lưu (nếu có), không thì bộ mẫu
void link_poll();  // mỗi vòng loop()
void link_key_down(int page, int key);
void link_key_up(int page, int key, bool cancelled);
void link_page(int page);
