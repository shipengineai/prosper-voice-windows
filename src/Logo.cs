// The Prosper logo: a dark glass tile with a gold "P" whose bowl is open on the right like a voice, and three waveform
// bars beside it (the same bars as the Prosper bar). Drawn in code, so the .exe icon, the tray icon, the window icons
// and the headers all come from this one drawing, crisp at every size.
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace Prosper
{
    static class Logo
    {
        static readonly Color GoldLight = Color.FromArgb(247, 222, 156), GoldDeep = Color.FromArgb(196, 146, 58);

        /// <summary>The tile at [r]. Below 30 px the waveform is left out so the P stays clear.</summary>
        public static void Draw(Graphics g, RectangleF r)
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            float s = Math.Min(r.Width, r.Height), x0 = r.X + (r.Width - s) / 2, y0 = r.Y + (r.Height - s) / 2;
            Func<float, float> X = u => x0 + u * s, Y = v => y0 + v * s;
            bool tiny = s < 30;

            // The tile: deep glass, lighter at the top, with a gold hairline and a faint top highlight.
            var tile = new RectangleF(X(0.02f), Y(0.02f), s * 0.96f, s * 0.96f);
            using (var p = Floating.Round(tile, s * 0.25f))
            {
                using (var b = new LinearGradientBrush(tile, Color.FromArgb(46, 46, 52), Color.FromArgb(12, 12, 14), 90f)) g.FillPath(b, p);
                using (var glow = new GraphicsPath())
                {
                    glow.AddEllipse(X(-0.2f), Y(0.45f), s * 1.4f, s * 1.1f); // warm light from below, inside the tile only
                    using (var pb = new PathGradientBrush(glow) { CenterColor = Color.FromArgb(tiny ? 0 : 54, GoldDeep), SurroundColors = new[] { Color.FromArgb(0, GoldDeep) } })
                    {
                        var clip = g.Clip; g.SetClip(p, CombineMode.Intersect); g.FillPath(pb, glow); g.Clip = clip;
                    }
                }
                // Glass catching light along the top.
                var sheen = new RectangleF(tile.X, tile.Y, tile.Width, tile.Height * 0.5f);
                using (var b = new LinearGradientBrush(sheen, Color.FromArgb(tiny ? 0 : 24, 255, 255, 255), Color.FromArgb(0, 255, 255, 255), 90f) { WrapMode = WrapMode.TileFlipXY })
                {
                    var clip = g.Clip; g.SetClip(p, CombineMode.Intersect); g.FillRectangle(b, sheen); g.Clip = clip;
                }
                using (var pen = new Pen(Color.FromArgb(tiny ? 110 : 100, GoldDeep), Math.Max(1f, s * 0.012f))) g.DrawPath(pen, p);
            }

            // The P: one gold stroke, stem first, then the bowl, which stops short of closing.
            float sw = s * (tiny ? 0.15f : 0.11f);
            float stem = tiny ? 0.34f : 0.31f, top = 0.24f, bottom = 0.77f, bowlR = tiny ? 0.17f : 0.15f, bowlCx = stem + 0.14f, bowlCy = top + bowlR;
            using (var gold = new LinearGradientBrush(new RectangleF(X(0), Y(0.12f), s, s * 0.76f), GoldLight, GoldDeep, 90f) { WrapMode = WrapMode.TileFlipXY })
            using (var pen = new Pen(gold, sw) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
            using (var path = new GraphicsPath())
            {
                path.AddLine(X(stem), Y(bottom), X(stem), Y(top));
                path.AddLine(X(stem), Y(top), X(bowlCx), Y(top));
                path.AddArc(X(bowlCx - bowlR), Y(top), s * bowlR * 2, s * bowlR * 2, 270, 180);
                path.AddLine(X(bowlCx), Y(top + bowlR * 2), X(stem + (tiny ? 0 : 0.09f)), Y(top + bowlR * 2)); // a small gap: open, like a voice
                g.DrawPath(pen, path);
            }

            if (tiny) return;
            // The voice: three bars rising from the P's foot, tallest in the middle like the bar's waveform.
            float[] heights = { 0.10f, 0.19f, 0.12f };
            using (var pen = new Pen(Color.FromArgb(235, 250, 250, 250), s * 0.062f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                for (int i = 0; i < heights.Length; i++)
                {
                    float bx = 0.555f + i * 0.105f, cy = 0.665f;
                    g.DrawLine(pen, X(bx), Y(cy - heights[i] / 2), X(bx), Y(cy + heights[i] / 2));
                }
        }

        public static Bitmap Image(int size)
        {
            var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp)) { g.Clear(Color.Transparent); Draw(g, new RectangleF(0, 0, size, size)); }
            return bmp;
        }

        static readonly int[] Sizes = { 16, 20, 24, 32, 40, 48, 64, 128, 256 };

        /// <summary>A Windows .ico with every size: 32-bit images for the small ones, PNG for 256 (Vista+).</summary>
        public static byte[] Ico()
        {
            var frames = new byte[Sizes.Length][];
            for (int i = 0; i < Sizes.Length; i++) using (var b = Image(Sizes[i])) frames[i] = Sizes[i] >= 256 ? Png(b) : Dib(b);
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write((short)0); w.Write((short)1); w.Write((short)Sizes.Length);
                int offset = 6 + 16 * Sizes.Length;
                for (int i = 0; i < Sizes.Length; i++)
                {
                    w.Write((byte)(Sizes[i] >= 256 ? 0 : Sizes[i])); w.Write((byte)(Sizes[i] >= 256 ? 0 : Sizes[i]));
                    w.Write((byte)0); w.Write((byte)0); w.Write((short)1); w.Write((short)32);
                    w.Write(frames[i].Length); w.Write(offset); offset += frames[i].Length;
                }
                foreach (var f in frames) w.Write(f);
                w.Flush(); return ms.ToArray();
            }
        }

        /// <summary>The logo as a data: URI, for the pages shown in the built-in browser.</summary>
        public static string DataUri(int size) { using (var b = Image(size)) return "data:image/png;base64," + Convert.ToBase64String(Png(b)); }

        static byte[] Png(Bitmap b) { using (var ms = new MemoryStream()) { b.Save(ms, ImageFormat.Png); return ms.ToArray(); } }

        static byte[] Dib(Bitmap b)
        {
            int n = b.Width;
            using (var ms = new MemoryStream())
            using (var w = new BinaryWriter(ms))
            {
                w.Write(40); w.Write(n); w.Write(n * 2); w.Write((short)1); w.Write((short)32);
                w.Write(0); w.Write(0); w.Write(0); w.Write(0); w.Write(0); w.Write(0);
                for (int y = n - 1; y >= 0; y--) // bottom-up, BGRA
                    for (int x = 0; x < n; x++) { var c = b.GetPixel(x, y); w.Write(c.B); w.Write(c.G); w.Write(c.R); w.Write(c.A); }
                w.Write(new byte[((n + 31) / 32) * 4 * n]); // the AND mask: unused, the alpha says it all
                w.Flush(); return ms.ToArray();
            }
        }

        static Icon app;
        /// <summary>The window and tray icon, every size in one.</summary>
        public static Icon AppIcon { get { return app ?? (app = new Icon(new MemoryStream(Ico()))); } }

        public static Icon Small(int size) { return new Icon(new MemoryStream(Ico()), size, size); }
    }
}
