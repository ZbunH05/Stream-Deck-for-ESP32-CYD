using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;

public static class KeyArt {
    public const int Size = 72, Radius = 12;
    public const int StripHeight = 24;

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

    public static void Glyph(Graphics g, string fontName, char code, float px, Color color, bool withTitle) {
        using (var family = new FontFamily(fontName)) Glyph(g, family, code, px, color, withTitle);
    }

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

    public static void Picture(Graphics g, Image icon, float px, bool withTitle) {
        float centerY = withTitle ? (Size - StripHeight) / 2f + 2 : Size / 2f;
        g.DrawImage(icon, new RectangleF((Size - px) / 2f, centerY - px / 2f, px, px));
    }

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

    public static void Ring(Graphics g, Color color, float width) {
        float inset = width / 2f;
        using (var path = RoundRect(new RectangleF(inset, inset, Size - width, Size - width), Radius - inset))
        using (var pen = new Pen(color, width)) {
            g.DrawPath(pen, path);
        }
    }

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

    public static void FolderMark(Graphics g) {
        using (var brush = new SolidBrush(Color.FromArgb(190, 255, 255, 255))) {
            g.FillPolygon(brush, new[] { new PointF(Size - 18, 6), new PointF(Size - 6, 6), new PointF(Size - 6, 18) });
        }
    }

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

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    static extern uint GetGlyphIndicesW(IntPtr hdc, string text, int count, [Out] ushort[] indices, uint flags);
    [DllImport("gdi32.dll")]
    static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    const uint GGI_MARK_NONEXISTING_GLYPHS = 1;

    public static bool HasGlyph(string fontName, char code) {
        using (var font = new Font(fontName, 32, GraphicsUnit.Pixel)) {
            if (font.Name != fontName) return false;
            using (var bmp = new Bitmap(1, 1))
            using (var g = Graphics.FromImage(bmp)) {
                IntPtr hdc = g.GetHdc();
                IntPtr hfont = font.ToHfont();
                try {
                    IntPtr old = SelectObject(hdc, hfont);
                    var index = new ushort[1];
                    uint n = GetGlyphIndicesW(hdc, code.ToString(), 1, index, GGI_MARK_NONEXISTING_GLYPHS);
                    SelectObject(hdc, old);
                    return n == 1 && index[0] != 0xFFFF;
                } finally {
                    DeleteObject(hfont);
                    g.ReleaseHdc(hdc);
                }
            }
        }
    }
}
