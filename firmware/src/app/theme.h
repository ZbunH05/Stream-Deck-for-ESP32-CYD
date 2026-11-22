#pragma once

#include <stdint.h>

#include "palette.h"

struct Theme {
#define THEME_FIELD(name, light, dark, role) uint32_t name;
  THEME_COLORS(THEME_FIELD)
#undef THEME_FIELD
};

enum class Pal : uint8_t {
#define THEME_ENUM(name, light, dark, role) name,
  THEME_COLORS(THEME_ENUM)
#undef THEME_ENUM
};

const Theme &theme();
bool theme_dark();
uint32_t theme_color(Pal color);

void theme_load();

void theme_toggle();
