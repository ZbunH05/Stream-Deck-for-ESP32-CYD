// Xem theme.h. CYD Deck chỉ có theme tối (palette.h): không đọc, không lưu lựa chọn trong NVS.
#include "theme.h"

#include <Arduino.h>

namespace {

#define THEME_DARK_VALUE(name, light, dark, role) dark,
const Theme DARK = {THEME_COLORS(THEME_DARK_VALUE)};
const uint32_t DARK_VALUES[] = {THEME_COLORS(THEME_DARK_VALUE)};
#undef THEME_DARK_VALUE

}  // namespace

const Theme &theme() { return DARK; }
bool theme_dark() { return true; }
uint32_t theme_color(Pal color) { return DARK_VALUES[static_cast<uint8_t>(color)]; }

void theme_load() {}
void theme_toggle() {}
