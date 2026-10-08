# Regenerates src/BetterMouse/app.ico (the exe icon) with the same mouse glyph as the tray icon.
param([string]$Out = (Join-Path $PSScriptRoot '..\src\BetterMouse\app.ico'))

Add-Type -AssemblyName System.Drawing
Add-Type -ReferencedAssemblies System.Drawing -TypeDefinition @'
using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

public static class IconMaker
{
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

    public static byte[] Png(int size)
    {
        var color = Color.FromArgb(0x22, 0xA3, 0x55);
        using (var bmp = new Bitmap(size, size, PixelFormat.Format32bppArgb))
        {
            using (var g = Graphics.FromImage(bmp))
            {
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.Clear(Color.Transparent);
                float s = size / 32f;
                using (var path = RoundedRect(new RectangleF(6.5f * s, 1.5f * s, 19f * s, 29f * s), 9.5f * s))
                using (var fill = new SolidBrush(color))
                using (var outline = new Pen(Color.FromArgb(0x13, 0x5A, 0x2F), Math.Max(1f, 1.4f * s)))
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
            using (var ms = new MemoryStream())
            {
                bmp.Save(ms, ImageFormat.Png);
                return ms.ToArray();
            }
        }
    }

    public static void WriteIco(string path, int[] sizes)
    {
        var images = new byte[sizes.Length][];
        for (int i = 0; i < sizes.Length; i++) images[i] = Png(sizes[i]);
        using (var fs = File.Create(path))
        using (var w = new BinaryWriter(fs))
        {
            w.Write((short)0); w.Write((short)1); w.Write((short)sizes.Length);
            int offset = 6 + 16 * sizes.Length;
            for (int i = 0; i < sizes.Length; i++)
            {
                w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                w.Write((byte)(sizes[i] >= 256 ? 0 : sizes[i]));
                w.Write((byte)0); w.Write((byte)0);
                w.Write((short)1); w.Write((short)32);
                w.Write(images[i].Length); w.Write(offset);
                offset += images[i].Length;
            }
            foreach (var img in images) w.Write(img);
        }
    }
}
'@

[IconMaker]::WriteIco([IO.Path]::GetFullPath($Out), [int[]](16, 20, 24, 32, 40, 48, 64, 256))
Write-Host "Wrote $Out"
