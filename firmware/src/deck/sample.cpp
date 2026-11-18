// Bộ phím mẫu: trang 1 làm việc và nhạc, trang 2 công cụ, thư mục OBS. Ảnh ở sample_images.c (sinh bằng
// tools/make_sample_images.cs từ Material Symbols). Bo chỉ dùng bộ này khi chưa nhận bộ phím từ CydAgent; ảnh chụp
// màn hình (shot.cpp) luôn dùng bộ này.
#include "sample.h"

#include <string.h>

#include "deck.h"
#include "sample_images.h"

namespace {

void key(DeckPage &page, int slot, KeyKind kind, const char *title, const lv_image_dsc_t *image,
         const lv_image_dsc_t *image_on = nullptr, const char *title_on = "") {
  DeckKey &k = page.keys[slot];
  k = DeckKey{};
  k.kind = kind;
  strlcpy(k.title, title, sizeof k.title);
  strlcpy(k.title_on, title_on, sizeof k.title_on);
  k.image = image;
  k.image_on = image_on;
}

DeckPage &page(DeckProfile &p, int index, const char *name, int parent = -1, BarMode bar = BarMode::Info) {
  DeckPage &page = p.pages[index];
  page = DeckPage{};
  strlcpy(page.name, name, sizeof page.name);
  page.parent = (int8_t)parent;
  page.bar = bar;
  return page;
}

}  // namespace

void sample_load() {
  DeckProfile &p = deck_profile();
  p = DeckProfile{};
  strlcpy(p.name, "Mặc định", sizeof p.name);
  p.top_pages = 2;
  p.page_count = 3;

  DeckPage &work = page(p, 0, "Làm việc");
  key(work, 0, KeyKind::Action, "Terminal", &deck_img_terminal);
  key(work, 1, KeyKind::Action, "Web", &deck_img_web);
  key(work, 2, KeyKind::Toggle, "Phát", &deck_img_play, &deck_img_pause, "Tạm dừng");
  key(work, 3, KeyKind::Action, "Bài sau", &deck_img_next);
  key(work, 4, KeyKind::Toggle, "Mic", &deck_img_mic, &deck_img_mic_off, "Mic tắt");
  key(work, 5, KeyKind::Action, "Tai nghe", &deck_img_headphones);
  key(work, 6, KeyKind::Folder, "OBS", &deck_img_obs);
  work.keys[6].target = 2;
  key(work, 7, KeyKind::Action, "Khóa máy", &deck_img_lock);

  DeckPage &tools = page(p, 1, "Công cụ");
  key(tools, 0, KeyKind::Action, "Thư mục", &deck_img_folder);
  key(tools, 1, KeyKind::Action, "Tài liệu", &deck_img_docs);
  key(tools, 2, KeyKind::Action, "Hiệu năng", &deck_img_perf);
  key(tools, 3, KeyKind::Action, "Cắt màn", &deck_img_snip);
  key(tools, 4, KeyKind::Action, "Chữ ký", &deck_img_text);
  key(tools, 5, KeyKind::Action, "Nhỏ lại", &deck_img_vol_down);
  key(tools, 6, KeyKind::Action, "To lên", &deck_img_vol_up);
  key(tools, 7, KeyKind::Toggle, "Tắt tiếng", &deck_img_mute, &deck_img_muted, "Đang tắt");

  DeckPage &obs = page(p, 2, "OBS", 0, BarMode::Obs);
  key(obs, 0, KeyKind::Back, "Quay ra", nullptr);
  key(obs, 1, KeyKind::Toggle, "Màn hình", &deck_img_scene_screen, &deck_img_scene_screen_on);
  key(obs, 2, KeyKind::Toggle, "Camera", &deck_img_scene_camera, &deck_img_scene_camera_on);
  key(obs, 3, KeyKind::Toggle, "Ghép", &deck_img_scene_split, &deck_img_scene_split_on);
  key(obs, 4, KeyKind::Toggle, "Ghi hình", &deck_img_rec, &deck_img_rec_on, "Đang ghi");
  key(obs, 5, KeyKind::Toggle, "Live", &deck_img_live, &deck_img_live_on, "Đang live");
  key(obs, 6, KeyKind::Action, "Nhạc nền", &deck_img_music);
  key(obs, 7, KeyKind::Action, "Chụp lại", &deck_img_replay);
}

void sample_demo_state() {
  DeckProfile &p = deck_profile();
  p.pages[0].keys[2].on = true;  // đang phát nhạc: phím hiện Tạm dừng
  p.pages[0].keys[4].on = true;  // mic đang tắt
  p.pages[2].keys[1].on = true;  // cảnh Màn hình đang phát
  p.pages[2].keys[4].on = true;  // đang ghi
  strlcpy(p.pages[2].keys[6].live, "35%", sizeof p.pages[2].keys[6].live);

  DeckInfo &i = deck_info();
  i.online = true;
  strlcpy(i.clock, "14:32", sizeof i.clock);
  strlcpy(i.media_title, "Lofi chill mix", sizeof i.media_title);
  strlcpy(i.media_source, "Trình phát nhạc", sizeof i.media_source);
  i.cpu = 23;
  i.ram = 41;
  i.obs_rec = true;
  strlcpy(i.obs_time, "12:04", sizeof i.obs_time);
  strlcpy(i.obs_scene, "Màn hình", sizeof i.obs_scene);
  i.obs_dropped = 0;
}
