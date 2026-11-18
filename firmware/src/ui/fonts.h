// Font LVGL của giao diện, sinh bằng lv_font_conv từ assets/fonts (lệnh sinh lại ghi ở đầu mỗi file trong
// src/ui/fonts/ và trong README, mục "Font"). Chỉ có những ký tự giao diện dùng, nên mỗi font chỉ vài chục KB flash.
#pragma once

#include <lvgl.h>

#ifdef __cplusplus
extern "C" {
#endif

// Be Vietnam Pro, đủ dấu tiếng Việt
extern const lv_font_t ui_font_vn_12;   // Regular 12, dòng 20 px
extern const lv_font_t ui_font_vn_12b;  // SemiBold 12, dòng 20 px
extern const lv_font_t ui_font_vn_14;   // Regular 14, dòng 22 px
extern const lv_font_t ui_font_vn_16b;  // SemiBold 16, dòng 24 px

// Nunito ExtraBold: chỉ chữ số và vài dấu (cho số liệu tròn trịa)
extern const lv_font_t ui_font_nu_26;  // dòng 24 px
extern const lv_font_t ui_font_nu_16;  // dòng 16 px

// CydIcons (Material Symbols Rounded), mã icon ở src/app/icons.h
extern const lv_font_t ui_font_ico_18;
extern const lv_font_t ui_font_ico_24;
extern const lv_font_t ui_font_ico_48;

#ifdef __cplusplus
}
#endif
