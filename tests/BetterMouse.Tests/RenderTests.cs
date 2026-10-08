using System;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Windows.Forms;
using Xunit;

namespace BetterMouse.Tests
{
    /// <summary>Opt-in (BM_RENDER=folder): renders the settings window and tray icons to PNG for a visual check.</summary>
    public class RenderTests
    {
        [Fact]
        public void RenderSettingsWindowAndIcons()
        {
            var folder = Environment.GetEnvironmentVariable("BM_RENDER");
            if (string.IsNullOrEmpty(folder)) return;
            Directory.CreateDirectory(folder);

            // WinForms gets its own STA thread so its sync context never touches xUnit's threads.
            Exception error = null;
            var t = new System.Threading.Thread(() =>
            {
                try { Render(folder); }
                catch (Exception ex) { error = ex; }
            });
            t.SetApartmentState(System.Threading.ApartmentState.STA);
            t.Start();
            Assert.True(t.Join(30000), "render timed out");
            if (error != null) throw error;
        }

        static void Render(string folder)
        {
            using (var form = new SettingsForm(new Settings(), firstRun: true))
            {
                form.StartPosition = FormStartPosition.Manual;
                form.Location = new Point(-5000, -5000);
                form.ShowInTaskbar = false;
                form.Show();
                Application.DoEvents();
                using (var bmp = new Bitmap(form.Width, form.Height))
                {
                    form.DrawToBitmap(bmp, new Rectangle(Point.Empty, form.Size));
                    bmp.Save(Path.Combine(folder, "settings.png"), ImageFormat.Png);
                }
                form.Close();
            }

            var colors = new[] { Icons.Offline, Icons.Online, Icons.Remote, Icons.Controlled, Icons.Problem };
            using (var strip = new Bitmap(colors.Length * 72, 72))
            using (var g = Graphics.FromImage(strip))
            {
                g.Clear(Color.White);
                for (int i = 0; i < colors.Length; i++)
                {
                    using (var big = Icons.Render(64, colors[i])) g.DrawImage(big, i * 72 + 4, 4);
                }
                strip.Save(Path.Combine(folder, "icons.png"), ImageFormat.Png);
            }
        }
    }
}
