// Hiệu ứng chuyển động kiểu iOS cho giao diện: trang trượt theo lò xo hoặc chạy theo ngón tay, màu mới quét từ trái
// khi đổi theme, ô lún nhẹ khi chạm, số đếm lên, vòng cung và thanh chạy tới số mới, giờ đổi chữ mềm, thông báo nở ra
// ở đỉnh màn như Đảo động của iPhone. Chỉ gọi từ luồng giao diện (loop).
//
// Bo không có PSRAM và đẩy hình qua SPI: vẽ lại cả màn mất 40–80 ms, nên hoạt cảnh nào bắt LVGL vẽ cả màn mỗi khung
// (trượt trang của LVGL, mờ dần, phóng to) chỉ ra được vài khung. Ở đây chỉ dùng loại rẻ: dời ảnh bằng lệnh cuộn của chip
// màn và vẽ từng dải, hoặc hoạt cảnh trên vùng nhỏ. Không dùng lớp vẽ (opa_layered, xoay, phóng to) hay đổ bóng.
#pragma once

#include <lvgl.h>
#include <stddef.h>

void motion_init();
// Gọi mỗi vòng loop(), sau lv_timer_handler(): chạy bước kế của hoạt cảnh trượt (vẽ dải mới, cuộn màn)
void motion_tick();

// ---------- trượt trang ----------
// Mở `screen`: back=false trang mới vào từ phải (đi sâu vào), true vào từ trái (quay lại). Trong lúc trượt cảm ứng bị
// bỏ qua. `shown` (có thể null) được gọi khi trang đã nằm yên. Không có cuộn phần cứng thì đổi trang tức thì.
void motion_push(lv_obj_t *screen, bool back, void (*shown)(lv_obj_t *screen));
bool motion_pushing();  // đang trượt (hoạt cảnh, theo tay, hay quét màu)
void motion_finish();   // xong ngay hoạt cảnh đang chạy

// Vuốt từ mép trái, trang chạy theo ngón tay (view.cpp nhận ra cử chỉ): `to` là trang hiện ra từ bên trái.
// move: ngón tay đã kéo bao nhiêu px. end: tốc độ lúc nhả (px/ms); quá 1/3 màn hoặc vuốt nhanh thì đi tiếp (gọi
// `shown`), không thì trang bật về chỗ cũ (gọi `cancelled` với trang cũ).
void motion_drag_begin(lv_obj_t *to, void (*shown)(lv_obj_t *screen), void (*cancelled)(lv_obj_t *screen));
void motion_drag_move(int32_t offset);
void motion_drag_end(float velocity);

// Đổi theme: gọi trước khi đổi màu các đối tượng; màu mới hiện dần từ trái sang phải trong ~0,45 s
void motion_reveal_begin();

bool motion_visible(lv_obj_t *obj);  // đối tượng ở trang đang hiện và không có trang nào đang trượt

// ---------- phản hồi khi chạm ----------
// Như nút của iOS và HarmonyOS: chạm xuống là nút thu lại còn ~96 % (đúng tỉ lệ ngang/dọc của nút, 110 ms, chậm dần);
// nhả tay thì bật về bằng lò xo, nảy nhẹ (~0,45 s), ngắt giữa chừng được. Lớp phủ sáng/sẫm khi nhấn nằm ở style dùng
// chung (src/ui/styles.cpp). Gọi sau khi đã đặt cỡ cho đối tượng (độ thu tính theo cỡ).
// scale: độ thu khi nhấn (phím của Deck dùng 0,92 cho cảm giác lún sâu hơn).
void motion_press_feedback(lv_obj_t *obj, float scale = 0.96f);

// Đổi màu nền đặt riêng của đối tượng mềm mại (~0,26 s) thay vì nhảy màu; trang không hiện thì đặt thẳng
void motion_bg_to(lv_obj_t *obj, uint32_t rgb, uint32_t ms = 260);

// Đối tượng vừa hiện ra (dấu tích của việc vừa đánh dấu xong...): trồi lên 6 px và rõ dần, có nảy nhẹ (~0,35 s)
void motion_pop_in(lv_obj_t *obj);

// ---------- số và thanh ----------
// Vòng cung / thanh chạy tới `value` theo đường cong chậm dần (sau `delay` ms). Trang không hiện, đang trượt, hay chênh
// ít (Pomodoro nhích từng giây) thì đặt thẳng, không tốn khung vẽ nào.
void motion_arc_to(lv_obj_t *arc, int32_t value, uint32_t ms, uint32_t delay = 0);
void motion_bar_to(lv_obj_t *bar, int32_t value, uint32_t ms, uint32_t delay = 0);
void motion_width_to(lv_obj_t *obj, int32_t width, uint32_t ms, uint32_t delay = 0);

// Số đếm lên: hold hiện số 0 và nhớ đích (trang sắp trượt vào), release cho mọi số đang giữ đếm lên cùng lúc (trang đã
// nằm yên, mỗi số trễ thêm `stagger` ms), finish đặt thẳng số cuối (trượt bị huỷ). `format` viết số ra chữ, phải giống
// hệt cách view.cpp viết chữ cuối. cancel: bỏ đếm của nhãn khi dữ liệu mới về.
typedef void (*CountFormat)(char *out, size_t size, int32_t value);
void motion_count_hold(lv_obj_t *label, int32_t target, CountFormat format);
void motion_count_release(uint32_t ms, uint32_t stagger);
void motion_count_finish();
void motion_count_cancel(lv_obj_t *label);
bool motion_count_holding(lv_obj_t *label);

// Đổi chữ của nhãn như số trên màn khoá iPhone: chữ cũ trôi lên và mờ đi, chữ mới trôi lên từ dưới. Cùng lúc tối đa
// 4 nhãn (giờ, phút, icon và chữ của nút chính Pomodoro). Trả về false nếu không đang hiện (khi đó người gọi tự đặt chữ).
bool motion_roll_text(lv_obj_t *label, const char *text);

// ---------- thông báo ----------
// Viên đen ở đỉnh màn nở ra theo lò xo, icon và chữ hiện dần, tự thu lại sau 3,2 s. Đang trượt trang thì đợi xong.
void motion_island_show(const char *text, const char *icon, uint32_t icon_color);
void motion_island_hide(bool animated);
void motion_island_restyle();  // sau khi đổi theme
