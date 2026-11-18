// Sinh ảnh cho bộ phím mẫu (src/deck/sample.cpp): src/deck/sample_images.c (mảng RGB565 đảo byte cho LVGL) và ảnh
// xem trước tools\bin\preview\*.png (đúng màu sau RGB565). Bo chỉ dùng bộ phím mẫu khi chưa nhận bộ phím từ CydAgent.
//
// Icon: Material Symbols Rounded của Google (Apache License 2.0), không dùng logo hay font của hãng khác, để ảnh mẫu
// phát hành được cùng mã nguồn. Font không nằm trong repo (15 MB); tải hai file từ
//   https://github.com/google/material-design-icons/tree/master/variablefont
//   MaterialSymbolsRounded[FILL,GRAD,opsz,wght].ttf và .codepoints
// rồi chạy: tools\make_sample_images.cmd <file .ttf> <file .codepoints>
// GDI+ đọc kiểu mặc định của font biến thiên (nét viền, đậm 400), đúng kiểu cần.
//
// Ảnh phím thật của người dùng do CydAgent vẽ trên PC bằng cùng KeyArt.cs.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.IO;
using System.Text;

static class MakeSampleImages {
    class Spec {
        public string Name;   // tên trong C: deck_img_<Name>
        public string Icon;   // tên icon Material Symbols
        public Color Background;
        public bool Folder, Ring;
    }

    static Spec S(string name, string icon, int rgb) {
        return new Spec { Name = name, Icon = icon, Background = Color.FromArgb(rgb | unchecked((int)0xFF000000)) };
    }

    static int Main(string[] args) {
        if (args.Length < 3) {
            Console.WriteLine("Dung: make_sample_images.exe <thu muc du an> <MaterialSymbolsRounded.ttf> <.codepoints>");
            return 2;
        }
        string root = args[0];
        var codes = new Dictionary<string, char>();
        foreach (var line in File.ReadAllLines(args[2])) {
            var parts = line.Split(' ');
            if (parts.Length == 2) codes[parts[0]] = (char)Convert.ToInt32(parts[1], 16);
        }
        var fonts = new PrivateFontCollection();
        fonts.AddFontFile(args[1]);
        FontFamily family = fonts.Families[0];

        const int Gray = 0x2B2C33, Purple = 0x4A3A8C, Red = 0xB3261E, Green = 0x2E7D4F, Media = 0x1A9E4B;
        var specs = new List<Spec> {
            // trang 1: làm việc và nhạc
            S("terminal", "terminal", 0x012456),
            S("web", "language", 0x0E6E8C),
            S("play", "play_arrow", Media),
            S("pause", "pause", Media),
            S("next", "skip_next", Gray),
            S("mic", "mic", Green),
            S("mic_off", "mic_off", Red),
            S("headphones", "headphones", 0x3B4BC8),
            S("obs", "podcasts", Purple),
            S("lock", "lock", Gray),
            // trang 2: công cụ
            S("folder", "folder", 0x8A5A00),
            S("docs", "description", 0x1B4F9C),
            S("perf", "monitoring", 0x2E6B3F),
            S("snip", "screenshot_region", 0x0E6E8C),
            S("text", "signature", Gray),
            S("vol_down", "volume_down", Gray),
            S("vol_up", "volume_up", Gray),
            S("mute", "volume_mute", Gray),
            S("muted", "volume_off", Red),
            // thư mục OBS
            S("scene_screen", "desktop_windows", Purple),
            S("scene_camera", "videocam", Purple),
            S("scene_split", "grid_view", Purple),
            S("rec", "radio_button_checked", Gray),
            S("rec_on", "radio_button_checked", Red),
            S("live", "sensors", Gray),
            S("live_on", "sensors", Red),
            S("music", "music_note", Gray),
            S("replay", "replay", Gray),
        };
        foreach (var s in specs) {
            if (s.Name == "obs") s.Folder = true;
        }
        // cảnh OBS đang phát: cùng ảnh, thêm viền xanh lá
        foreach (var name in new[] { "scene_screen", "scene_camera", "scene_split" }) {
            var basis = specs.Find(s => s.Name == name);
            specs.Add(new Spec { Name = name + "_on", Icon = basis.Icon, Background = basis.Background, Ring = true });
        }

        string previewDir = Path.Combine(root, @"tools\bin\preview");
        Directory.CreateDirectory(previewDir);
        var c = new StringBuilder();
        var h = new StringBuilder();
        c.AppendLine("// Sinh bởi tools/make_sample_images.cs, đừng sửa tay. Ảnh phím mẫu 72×72 RGB565 đảo byte.");
        c.AppendLine("// Icon: Material Symbols Rounded, Google, Apache License 2.0 (assets/fonts/MaterialSymbols-LICENSE.txt).");
        c.AppendLine("#include \"sample_images.h\"");
        h.AppendLine("// Sinh bởi tools/make_sample_images.cs, đừng sửa tay.");
        h.AppendLine("#pragma once");
        h.AppendLine("#include <lvgl.h>");
        h.AppendLine("#ifdef __cplusplus");
        h.AppendLine("extern \"C\" {");
        h.AppendLine("#endif");
        int failed = 0;
        foreach (var s in specs) {
            char code;
            if (!codes.TryGetValue(s.Icon, out code)) {
                Console.WriteLine("Khong co icon " + s.Icon);
                failed++;
                continue;
            }
            Graphics g;
            byte[] px;
            using (var bmp = KeyArt.Begin(s.Background, out g)) {
                using (g) {
                    KeyArt.Glyph(g, family, code, 38, Color.White, true);
                    if (s.Ring) KeyArt.Ring(g, Color.FromArgb(0x3D, 0xD2, 0x7A), 3);
                    KeyArt.TitleStrip(g);
                    if (s.Folder) KeyArt.FolderMark(g);
                }
                px = KeyArt.ToRgb565Swapped(bmp);
            }
            using (var preview = KeyArt.FromRgb565Swapped(px)) preview.Save(Path.Combine(previewDir, s.Name + ".png"), ImageFormat.Png);

            c.AppendFormat("\nstatic const uint8_t px_{0}[{1}] = {{", s.Name, px.Length);
            for (int i = 0; i < px.Length; i++) {
                if (i % 24 == 0) c.Append("\n ");
                c.AppendFormat(" 0x{0:x2},", px[i]);
            }
            c.AppendLine("\n};");
            c.AppendFormat("const lv_image_dsc_t deck_img_{0} = {{\n", s.Name);
            c.AppendLine("  .header = {.magic = LV_IMAGE_HEADER_MAGIC, .cf = LV_COLOR_FORMAT_RGB565_SWAPPED, .flags = 0, .w = 72, .h = 72, .stride = 144},");
            c.AppendFormat("  .data_size = {0},\n  .data = px_{1},\n}};\n", px.Length, s.Name);
            h.AppendFormat("extern const lv_image_dsc_t deck_img_{0};\n", s.Name);
        }
        h.AppendLine("#ifdef __cplusplus");
        h.AppendLine("}");
        h.AppendLine("#endif");
        File.WriteAllText(Path.Combine(root, @"src\deck\sample_images.c"), c.ToString(), new UTF8Encoding(false));
        File.WriteAllText(Path.Combine(root, @"src\deck\sample_images.h"), h.ToString(), new UTF8Encoding(false));
        Console.WriteLine(string.Format("Da sinh {0} anh mau, {1} loi. Xem truoc: {2}", specs.Count, failed, previewDir));
        return failed == 0 ? 0 : 1;
    }
}
