// Đường nối với PC qua cáp USB (cổng Serial của bo, 115200 baud), bên PC là CydAgent ở chế độ Deck
// (pc-agent\src\Deck*.cs trong repo).
//
// PC → bo: gói nhị phân như trang Máy tính của CYD-EEZ: A5 5A | loại | độ dài 2 byte (LE) | dữ liệu | CRC-8 (0x07).
//   01 HELLO        phiên bản, tên máy                        → bo trả dòng "[DECK] chao 1 <ảnh có>/<ô> <CRC bộ phím>"
//   11 LAYOUT_BEGIN số trang, số trang chính, tên hồ sơ
//   12 LAYOUT_PAGE  số trang, trang cha, kiểu thanh, tên, rồi 8 phím {kiểu, đích, mã ảnh, mã ảnh bật, nhãn, nhãn bật}
//   13 LAYOUT_END   CRC-32 của các gói 11/12 (loại + độ dài + dữ liệu) → "[DECK] bo <crc> ok", "[DECK] can <mã>..."
//   14 IMG_BEGIN    mã ảnh                                    → "[DECK] anh <mã> co|san|loi"
//   15 IMG_DATA     mã ảnh, vị trí, ≤ 480 byte                → "[DECK] anh <mã> ok <vị trí kế>" hay "... loi"
//   16 IMG_END      mã ảnh                                    → "[DECK] anh <mã> xong|loi"
//   17 STATE        trang, phím, bật/tắt, chữ sống
//   18 INFO         cờ, giờ "HH:MM", CPU %, RAM %, bài đang phát, nguồn phát (mỗi giây; 4 giây không có = mất nối)
//   19 ACK          trang, phím, thành công
// Bo → PC: các dòng log chữ "[DECK] ...": "[DECK] bam <trang> <phím> <giữ lâu 0/1>" khi người dùng bấm phím lệnh.
// PC chờ dòng trả lời của mỗi gói ảnh rồi mới gửi gói sau, nên bo xoá/ghi flash (vài chục ms) mà không tràn bộ đệm.
#pragma once

void link_init();                               // sau store_init(): nạp bộ phím đã lưu (nếu có), không thì bộ mẫu
void link_poll();                               // mỗi vòng loop()
void link_press(int page, int key, bool hold);  // người dùng bấm phím Action/Toggle
