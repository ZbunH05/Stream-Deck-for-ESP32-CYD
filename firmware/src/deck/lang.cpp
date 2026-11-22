#include "lang.h"

#include <Arduino.h>
#include <Preferences.h>

namespace {

Lang current = Lang::En;

const char *const STRINGS[(int)Str::Count][2] = {
    {"Đang chờ máy tính…", "Waiting for computer…"},
    {"Mở CydAgent trên PC", "Start CydAgent on the PC"},
    {"Chưa nối máy tính: mở CydAgent trên PC", "Not connected: start CydAgent on the PC"},
    {"Quay ra", "Back"},
    {"Thư mục %s", "Folder %s"},
    {"Đang phát", "Playing"},
    {"Hồ sơ: %s", "Profile: %s"},
    {"OBS · đang ghi", "OBS · recording"},
    {"OBS · đang live", "OBS · live"},
    {"OBS · đang chờ", "OBS · idle"},
    {"OBS · tạm dừng ghi", "OBS · rec paused"},
    {"OBS chưa nối", "OBS not connected"},
    {"Bật WebSocket trong OBS", "Enable WebSocket in OBS"},
    {"Cảnh: %s", "Scene: %s"},
    {"Rớt", "Drop"},
};

}

Lang lang() { return current; }

const char *tr(Str id) { return STRINGS[(int)id][(int)current]; }

void lang_load() {
  Preferences prefs;
  if (prefs.begin("cyd", true)) {
    current = prefs.getUChar("lang", (uint8_t)Lang::En) == (uint8_t)Lang::Vi ? Lang::Vi : Lang::En;
    prefs.end();
  }
}

void lang_set(Lang value) {
  if (value == current) return;
  current = value;
  Preferences prefs;
  if (prefs.begin("cyd", false)) {
    prefs.putUChar("lang", (uint8_t)value);
    prefs.end();
  }
}
