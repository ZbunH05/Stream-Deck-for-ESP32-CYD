#pragma once

#include <lvgl.h>
#include <stdint.h>

constexpr int DECK_KEYS = 8;
constexpr int DECK_MAX_PAGES = 16;
constexpr int DECK_TITLE = 24;

enum class KeyKind : uint8_t {
  Empty,
  Action,
  Toggle,
  Folder,
  Back,
  Plugin,
};

enum class BarMode : uint8_t { Info, Obs };

struct DeckKey {
  KeyKind kind = KeyKind::Empty;
  char title[DECK_TITLE] = "";
  char title_on[DECK_TITLE] = "";
  const lv_image_dsc_t *image = nullptr;
  const lv_image_dsc_t *image_on = nullptr;
  uint32_t image_hash = 0;
  uint32_t image_on_hash = 0;
  uint8_t target = 0;
  bool on = false;
  char live[DECK_TITLE] = "";
};

struct DeckPage {
  char name[DECK_TITLE] = "";
  int8_t parent = -1;
  BarMode bar = BarMode::Info;
  DeckKey keys[DECK_KEYS];
};

struct DeckProfile {
  char name[DECK_TITLE] = "";
  uint8_t page_count = 0;
  uint8_t top_pages = 0;
  DeckPage pages[DECK_MAX_PAGES];
};

struct DeckInfo {
  bool online = false;
  char clock[6] = "--:--";
  uint8_t cpu = 0, ram = 0;
  bool playing = false;
  char media_title[48] = "";
  char media_sub[48] = "";
  uint16_t progress = 0xFFFF;
  bool obs_connected = false, obs_rec = false, obs_live = false, obs_paused = false;
  char obs_time[12] = "";
  char obs_scene[DECK_TITLE] = "";
  uint16_t obs_dropped = 0;
  uint8_t obs_cpu = 0;
};

DeckProfile &deck_profile();
DeckInfo &deck_info();

void deck_init();
void deck_tick();

void deck_go(int page, bool animate);
int deck_page();
void deck_refresh();
void deck_refresh_info();
void deck_refresh_key(int key);

void deck_feedback(int page, int key, bool ok, bool hold = false);
void deck_profile_switched();
