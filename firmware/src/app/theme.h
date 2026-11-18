// Màu của giao diện (bảng ở palette.h). Chép từ CYD-EEZ để motion.cpp dùng lại nguyên vẹn; CYD Deck chỉ có theme tối
// (theme.cpp), theme_toggle() không làm gì.
#pragma once

#include <stdint.h>

#include "palette.h"

struct Theme {
#define THEME_FIELD(name, light, dark, role) uint32_t name;
  THEME_COLORS(THEME_FIELD)
#undef THEME_FIELD
};

// Tên của từng màu trong bảng, để style nhớ "màu thẻ" thay vì một giá trị cố định: Pal::card, Pal::text2...
enum class Pal : uint8_t {
#define THEME_ENUM(name, light, dark, role) name,
  THEME_COLORS(THEME_ENUM)
#undef THEME_ENUM
};

const Theme &theme();  // theme đang dùng
bool theme_dark();
uint32_t theme_color(Pal color);  // giá trị 0xRRGGBB của màu đó ở theme đang dùng

// Đọc lựa chọn đã lưu. Gọi trước khi dựng giao diện (ui_init), để giao diện dựng thẳng bằng đúng màu.
void theme_load();

// Đổi sang theme kia và lưu lại. Người gọi tô lại style (ui_repaint) và vẽ lại các màu đặt lúc chạy (view.cpp).
void theme_toggle();
