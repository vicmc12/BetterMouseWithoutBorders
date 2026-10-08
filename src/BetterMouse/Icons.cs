using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Windows.Forms;

namespace BetterMouse
{
    /// <summary>Tray icons drawn at runtime: a little mouse whose colour shows the link state.</summary>
    internal static class Icons
    {
        public static readonly Color Offline = Color.FromArgb(0x8A, 0x8F, 0x98);
        public static readonly Color Online = Color.FromArgb(0x22, 0xA3, 0x55);
        public static readonly Color Remote = Color.FromArgb(0x2F, 0x7C, 0xF6);
        public static readonly Color Controlled = Color.FromArgb(0x8B, 0x5C, 0xF6);
        public static readonly Color Problem = Color.FromArgb(0xD9, 0x3F, 0x3F);

        public static Icon Create(Color color)
        {
            int size = Math.Max(16, SystemInformation.SmallIconSize.Width);
            using (var bmp = Render(size, color))
                return Icon.FromHandle(bmp.GetHicon()); // a handful of icons for the app's lifetime
        }

        public static Bitmap Render(int size, Color color)
        {
            var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb);
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                float s = size / 32f;
                var body = new RectangleF(6.5f * s, 1.5f * s, 19f * s, 29f * s);
                using (var path = RoundedRect(body, 9.5f * s))
                using (var fill = new SolidBrush(color))
                using (var outline = new Pen(Darken(color, 0.55f), Math.Max(1f, 1.4f * s)))
                {
                    g.FillPath(fill, path);
                    g.DrawPath(outline, path);
                }
                using (var line = new Pen(Color.FromArgb(235, 255, 255, 255), Math.Max(1f, 1.6f * s)))
                {
                    g.DrawLine(line, 16f * s, 3f * s, 16f * s, 12.5f * s);
                    g.DrawLine(line, 7.5f * s, 12.5f * s, 24.5f * s, 12.5f * s);
                }
                using (var wheel = RoundedRect(new RectangleF(14.2f * s, 5f * s, 3.6f * s, 5.5f * s), 1.8f * s))
                using (var white = new SolidBrush(Color.White))
                    g.FillPath(white, wheel);
            }
            return bmp;
        }

        static GraphicsPath RoundedRect(RectangleF r, float radius)
        {
            float d = Math.Min(radius * 2, Math.Min(r.Width, r.Height));
            var p = new GraphicsPath();
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        static Color Darken(Color c, float f) =>
            Color.FromArgb(c.A, (int)(c.R * f), (int)(c.G * f), (int)(c.B * f));
    }
}
