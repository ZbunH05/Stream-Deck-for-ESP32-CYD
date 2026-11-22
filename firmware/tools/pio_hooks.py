import os

Import("env")

def skip_lvgl_arm_asm(env, node):
    name = node.name
    if name.endswith(".S") and name.startswith(("lv_blend_helium", "lv_blend_neon")):
        return None
    return node

env.AddBuildMiddleware(skip_lvgl_arm_asm)

speed = os.environ.get("CYD_UPLOAD_SPEED")
if speed:
    env.Replace(UPLOAD_SPEED=speed)
    print("[pio_hooks] Toc do nap: " + speed)
