#pragma once

#include <stdint.h>

enum class Lang : uint8_t { Vi = 0, En = 1 };

enum class Str : uint8_t {
  WaitTitle,
  WaitSub,
  NoPc,
  Back,
  FolderFmt,
  Playing,
  Profile,
  ObsRec,
  ObsLive,
  ObsIdle,
  ObsPaused,
  ObsOff,
  ObsOffSub,
  SceneFmt,
  Drop,
  Count
};

Lang lang();
void lang_set(Lang value);
void lang_load();
const char *tr(Str id);
