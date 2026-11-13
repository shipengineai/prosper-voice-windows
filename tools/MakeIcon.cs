// Writes the app icon (prosper.ico) and logo PNGs from the drawing in src\Logo.cs. build.cmd runs it before
// compiling the app, so the .exe's own icon is the same logo.
using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;

namespace Prosper
{
    static class MakeIcon
    {
        static void Main(string[] args)
        {
            string dir = args.Length > 0 ? args[0] : ".";
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, "prosper.ico"), Logo.Ico());
            using (var b = Logo.Image(512)) b.Save(Path.Combine(dir, "logo.png"), ImageFormat.Png);
            if (Array.IndexOf(args, "--sheet") < 0) return;
            // Every size side by side, on dark and on light, to check the small ones by eye.
            int[] sizes = { 16, 20, 24, 32, 48, 64, 128, 256 };
            using (var sheet = new Bitmap(760, 620))
            using (var g = Graphics.FromImage(sheet))
            {
                g.Clear(Color.FromArgb(17, 17, 19));
                using (var light = new SolidBrush(Color.FromArgb(243, 243, 240))) g.FillRectangle(light, 0, 310, 760, 310);
                for (int row = 0; row < 2; row++)
                {
                    int x = 16;
                    foreach (var s in sizes) { using (var b = Logo.Image(s)) g.DrawImage(b, x, row * 310 + 20 + (256 - s) / 2); x += s + 16; if (x > 700) break; }
                }
                sheet.Save(Path.Combine(dir, "logo-sheet.png"), ImageFormat.Png);
            }
        }
    }
}
