"""PlatformIO pre-build hook của CYD Deck (PlatformIO chỉ nhận hook viết bằng Python).

1) Bỏ qua mã hợp ngữ ARM (Helium/NEON) đi kèm LVGL: ESP32 không dùng tới, và file .S
   này làm build lỗi "esp_attr.h: No such file or directory".

2) Đổi tốc độ nạp tạm thời qua biến môi trường CYD_UPLOAD_SPEED
   (tools/cyd.ps1 dùng 115200 khi nạp ở 921600 bị lỗi).

Deck không có Wi-Fi, không cần nhúng khoá hay mật khẩu nào lúc build.
"""

import os

Import("env")  # noqa: F821  (biến do PlatformIO/SCons cung cấp)


def skip_lvgl_arm_asm(env, node):
    name = node.name
    if name.endswith(".S") and name.startswith(("lv_blend_helium", "lv_blend_neon")):
        return None
    return node


env.AddBuildMiddleware(skip_lvgl_arm_asm)  # noqa: F821

speed = os.environ.get("CYD_UPLOAD_SPEED")
if speed:
    env.Replace(UPLOAD_SPEED=speed)  # noqa: F821
    print("[pio_hooks] Toc do nap: " + speed)
