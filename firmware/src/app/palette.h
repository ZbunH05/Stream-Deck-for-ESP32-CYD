// Bảng màu của CYD Deck: một theme tối như Stream Deck của Elgato (nền đen, phím xám đậm, chữ trắng).
//
// Mỗi dòng: X(tên, sáng, tối, vai). Deck chỉ có theme tối nên hai cột giống nhau; giữ dạng hai cột để dùng lại
// nguyên theme.h và motion.cpp của CYD-EEZ. Vai chỉ để đọc: B nền, F chữ và icon, K màu cố định.
//
// Xám chọn sao cho sau RGB565 vẫn trung tính (xanh lá 6 bit = 2 × đỏ 5 bit), như ghi chú của CYD-EEZ: #1C1C1E của iOS
// ra 3/7/3 và ngả xanh lá trên màn TFT.
#pragma once

#define THEME_COLORS(X) \
  /* nền */ \
  X(bg, 0x000000, 0x000000, B) \
  X(key, 0x212125, 0x212125, B)          /* phím chưa có ảnh: 4/8/4 */ \
  X(key_back, 0x141416, 0x141416, B)     /* phím ‹ của thư mục, tối hơn phím thường */ \
  X(bar, 0x18181B, 0x18181B, B)          /* thanh tin: 3/6/3 */ \
  X(badge_bg, 0x000000, 0x000000, B) \
  /* chữ, icon */ \
  X(text, 0xF2F2F5, 0xF2F2F5, F) \
  X(text2, 0x9A9AA2, 0x9A9AA2, F)        /* chữ phụ trên thanh tin: 19/38/20 */ \
  X(muted, 0x5A5A62, 0x5A5A62, F)        /* mũi tên lật trang khi không còn trang: 11/22/12 */ \
  X(accent, 0x5AA2FF, 0x5AA2FF, F) \
  X(green, 0x3DD27A, 0x3DD27A, F) \
  X(red, 0xFF5A52, 0xFF5A52, F) \
  X(orange, 0xFFB02E, 0xFFB02E, F) \
  /* giữ nguyên */ \
  X(white, 0xFFFFFF, 0xFFFFFF, K) \
  X(island, 0x050505, 0x050505, K) \
  X(island_border, 0x3A3A3C, 0x3A3A3C, K)
