// Vẽ ảnh một phím 72×72 cho CYD Deck: nền phím bo góc 12 trên nền đen của màn, icon ở giữa phía trên, dải tối ở đáy
// cho nhãn (bo vẽ nhãn bằng LVGL), tam giác nhỏ ở góc nếu là thư mục. Rồi đổi sang RGB565 đảo byte, đúng định dạng
// LVGL vẽ thẳng lên màn (LV_COLOR_FORMAT_RGB565_SWAPPED) mà không cần chuyển đổi nào.
//
// Dùng chung cho CydAgent (ảnh phím người dùng đặt trong trang cài đặt, DeckConfig.cs) và
// ..\..\firmware\tools\make_sample_images.cs (ảnh mẫu nhúng vào firmware).
// C# 5 (csc.exe của .NET Framework 4 có sẵn trong Windows): không có $"", ?., nameof.
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;

public static class KeyArt {
    public const int Size = 72, Radius = 12;
    public const int StripHeight = 24;  // dải tối ở đáy, bo vẽ nhãn vn_12b (dòng 20 px) vào đây

    public static GraphicsPath RoundRect(RectangleF r, float radius) {
        var path = new GraphicsPath();
        float d = radius * 2;
        path.AddArc(r.X, r.Y, d, d, 180, 90);
        path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
        path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
        path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
        path.CloseFigure();
        return path;
    }

    // Ảnh nền phím: hình chữ nhật bo góc tô `background`, phần ngoài góc bo là màu nền màn (đen)
    public static Bitmap Begin(Color background, out Graphics g) {
        var bmp = new Bitmap(Size, Size, PixelFormat.Format32bppArgb);
        g = Graphics.FromImage(bmp);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.Clear(Color.Black);
        using (var path = RoundRect(new RectangleF(0, 0, Size, Size), Radius))
        using (var brush = new SolidBrush(background)) {
            g.FillPath(brush, path);
        }
        return bmp;
    }

    // Icon chữ của font Segoe Fluent Icons / Segoe MDL2 Assets, trắng, căn giữa phần trên (chừa dải nhãn)
    public static void Glyph(Graphics g, string fontName, char code, float px, Color color, bool withTitle) {
        using (var family = new FontFamily(fontName)) Glyph(g, family, code, px, color, withTitle);
    }

    // Như trên, với font nạp từ file (PrivateFontCollection), ví dụ Material Symbols cho ảnh phím mẫu
    public static void Glyph(Graphics g, FontFamily family, char code, float px, Color color, bool withTitle) {
        using (var font = new Font(family, px, FontStyle.Regular, GraphicsUnit.Pixel))
        using (var brush = new SolidBrush(color))
        using (var format = new StringFormat(StringFormat.GenericTypographic)) {
            format.Alignment = StringAlignment.Center;
            format.LineAlignment = StringAlignment.Center;
            float centerY = withTitle ? (Size - StripHeight) / 2f + 2 : Size / 2f;
            g.DrawString(code.ToString(), font, brush, new RectangleF(0, centerY - px, Size, px * 2), format);
        }
    }

    // Icon app (ảnh có kênh alpha), cỡ `px`, căn như Glyph
    public static void Picture(Graphics g, Image icon, float px, bool withTitle) {
        float centerY = withTitle ? (Size - StripHeight) / 2f + 2 : Size / 2f;
        g.DrawImage(icon, new RectangleF((Size - px) / 2f, centerY - px / 2f, px, px));
    }

    // Ảnh người dùng chọn phủ kín phím (cắt bớt cạnh dài cho vừa hình vuông), giữ góc bo, như ảnh phím của Elgato
    public static void Cover(Graphics g, Image image) {
        float scale = Math.Max((float)Size / image.Width, (float)Size / image.Height);
        float w = image.Width * scale, h = image.Height * scale;
        using (var clip = RoundRect(new RectangleF(0, 0, Size, Size), Radius)) {
            var old = g.Clip;
            g.SetClip(clip);
            g.DrawImage(image, new RectangleF((Size - w) / 2f, (Size - h) / 2f, w, h));
            g.Clip = old;
        }
    }

    // Vòng viền trong (cảnh OBS đang phát...)
    public static void Ring(Graphics g, Color color, float width) {
        float inset = width / 2f;
        using (var path = RoundRect(new RectangleF(inset, inset, Size - width, Size - width), Radius - inset))
        using (var pen = new Pen(color, width)) {
            g.DrawPath(pen, path);
        }
    }

    // Dải tối ở đáy cho nhãn: từ trong suốt lên đen 55 %, cắt theo góc bo của phím
    public static void TitleStrip(Graphics g) {
        var area = new RectangleF(0, Size - StripHeight - 6, Size, StripHeight + 6);
        using (var clip = RoundRect(new RectangleF(0, 0, Size, Size), Radius))
        using (var brush = new LinearGradientBrush(area, Color.FromArgb(0, 0, 0, 0), Color.FromArgb(140, 0, 0, 0), 90f)) {
            var old = g.Clip;
            g.SetClip(clip);
            g.FillRectangle(brush, area);
            g.Clip = old;
        }
    }

    // Tam giác nhỏ ở góc trên phải: phím mở thư mục (giống Elgato)
    public static void FolderMark(Graphics g) {
        using (var brush = new SolidBrush(Color.FromArgb(190, 255, 255, 255))) {
            g.FillPolygon(brush, new[] { new PointF(Size - 18, 6), new PointF(Size - 6, 6), new PointF(Size - 6, 18) });
        }
    }

    // RGB565 đảo byte (byte cao trước), làm tròn từng kênh
    public static byte[] ToRgb565Swapped(Bitmap bmp) {
        var data = bmp.LockBits(new Rectangle(0, 0, Size, Size), ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var src = new byte[data.Stride * Size];
        Marshal.Copy(data.Scan0, src, 0, src.Length);
        bmp.UnlockBits(data);
        var dst = new byte[Size * Size * 2];
        for (int y = 0; y < Size; y++) {
            for (int x = 0; x < Size; x++) {
                int o = y * data.Stride + x * 4;
                int b = src[o], gr = src[o + 1], r = src[o + 2];
                int r5 = (r * 31 + 127) / 255, g6 = (gr * 63 + 127) / 255, b5 = (b * 31 + 127) / 255;
                int c = (r5 << 11) | (g6 << 5) | b5;
                int i = (y * Size + x) * 2;
                dst[i] = (byte)(c >> 8);
                dst[i + 1] = (byte)(c & 0xFF);
            }
        }
        return dst;
    }

    // Xem trước đúng màu sẽ lên màn (sau RGB565)
    public static Bitmap FromRgb565Swapped(byte[] px) {
        var bmp = new Bitmap(Size, Size, PixelFormat.Format32bppArgb);
        for (int y = 0; y < Size; y++) {
            for (int x = 0; x < Size; x++) {
                int i = (y * Size + x) * 2, c = (px[i] << 8) | px[i + 1];
                int r = ((c >> 11) & 31) * 255 / 31, gr = ((c >> 5) & 63) * 255 / 63, b = (c & 31) * 255 / 31;
                bmp.SetPixel(x, y, Color.FromArgb(r, gr, b));
            }
        }
        return bmp;
    }

    // ---------- icon của file .exe (cỡ lớn, có alpha) qua IShellItemImageFactory, như Explorer ----------
    [ComImport, Guid("bcc18b79-ba16-442f-80c4-8a59c30c463b"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    interface IShellItemImageFactory {
        [PreserveSig] int GetImage(NativeSize size, int flags, out IntPtr bitmap);
    }

    [StructLayout(LayoutKind.Sequential)]
    struct NativeSize { public int cx, cy; }

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, PreserveSig = false)]
    static extern void SHCreateItemFromParsingName(string path, IntPtr bindContext,
        [MarshalAs(UnmanagedType.LPStruct)] Guid riid, [MarshalAs(UnmanagedType.Interface)] out IShellItemImageFactory item);

    [DllImport("gdi32.dll")] static extern bool DeleteObject(IntPtr handle);

    const int SIIGBF_BIGGERSIZEOK = 0x1, SIIGBF_ICONONLY = 0x4;

    public static Bitmap FileIcon(string path, int px) {
        IShellItemImageFactory factory;
        SHCreateItemFromParsingName(path, IntPtr.Zero, typeof(IShellItemImageFactory).GUID, out factory);
        IntPtr hbitmap;
        var size = new NativeSize { cx = px, cy = px };
        int hr = factory.GetImage(size, SIIGBF_ICONONLY | SIIGBF_BIGGERSIZEOK, out hbitmap);
        Marshal.ReleaseComObject(factory);
        if (hr != 0) throw new Exception(string.Format("Khong lay duoc icon cua {0} (0x{1:X8})", path, hr));
        try {
            // FromHbitmap bỏ kênh alpha khi đọc; dữ liệu 32 bit vẫn còn alpha (premultiplied) nên đọc lại theo PArgb
            using (var raw = Image.FromHbitmap(hbitmap)) {
                var data = raw.LockBits(new Rectangle(0, 0, raw.Width, raw.Height), ImageLockMode.ReadOnly, raw.PixelFormat);
                try {
                    using (var view = new Bitmap(data.Width, data.Height, data.Stride, PixelFormat.Format32bppPArgb, data.Scan0)) {
                        return new Bitmap(view);
                    }
                } finally {
                    raw.UnlockBits(data);
                }
            }
        } finally {
            DeleteObject(hbitmap);
        }
    }

    // Font icon có ký tự này không (Segoe Fluent Icons có trên Windows 11, Segoe MDL2 Assets trên Windows 10)
    public static bool HasGlyph(string fontName, char code) {
        using (var font = new Font(fontName, 32, GraphicsUnit.Pixel)) {
            if (font.Name != fontName) return false;
        }
        var face = new System.Windows.Media.GlyphTypeface(new Uri(FontFile(fontName)));
        ushort glyph;
        return face.CharacterToGlyphMap.TryGetValue(code, out glyph);
    }

    static string FontFile(string fontName) {
        string dir = Environment.GetFolderPath(Environment.SpecialFolder.Fonts);
        return System.IO.Path.Combine(dir, fontName == "Segoe Fluent Icons" ? "SegoeIcons.ttf" : "segmdl2.ttf");
    }
}
