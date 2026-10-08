using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Windows.Forms;

namespace BetterMouse
{
    /// <summary>
    /// "Where is the other PC?" — this PC's screen in the middle and four places around it.
    /// Click a place (or use the arrow keys) to put the other PC's screen there.
    /// </summary>
    internal sealed class LayoutPicker : Control
    {
        static readonly Color ThisColor = Color.FromArgb(0x2F, 0x7C, 0xF6);
        static readonly Color OtherColor = Color.FromArgb(0x22, 0xA3, 0x55);
        static readonly Edge[] All = { Edge.Left, Edge.Right, Edge.Top, Edge.Bottom };

        Edge side = Edge.Right;
        Edge? hover;

        public string ThisName { get; set; } = "This PC";
        public string OtherName { get; set; } = "Other PC";

        public event EventHandler SideChanged;

        public LayoutPicker()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw |
                     ControlStyles.UserPaint | ControlStyles.Selectable, true);
            TabStop = true;
            Cursor = Cursors.Hand;
            AccessibleRole = AccessibleRole.RadioButton;
            UpdateAccessibleName();
        }

        public Edge Side
        {
            get => side;
            set
            {
                if (side == value) return;
                side = value;
                UpdateAccessibleName();
                Invalidate();
                SideChanged?.Invoke(this, EventArgs.Empty);
            }
        }

        void UpdateAccessibleName() => AccessibleName = $"The other PC is {side.Describe()} this PC";

        Rectangle Screen(Edge? where)
        {
            // Three 16:9 screens must fit across and three down, with gaps and a small margin.
            int gap = Math.Max(4, Math.Min(Width, Height) / 25), margin = 3;
            int h = Math.Min((Width - 2 * gap - 2 * margin) * 9 / (3 * 16), (Height - 2 * gap - 2 * margin) / 3);
            int w = h * 16 / 9, gapX = gap, gapY = gap;
            int cx = (Width - w) / 2, cy = (Height - h) / 2;
            switch (where)
            {
                case null: return new Rectangle(cx, cy, w, h);
                case Edge.Left: return new Rectangle(cx - w - gapX, cy, w, h);
                case Edge.Right: return new Rectangle(cx + w + gapX, cy, w, h);
                case Edge.Top: return new Rectangle(cx, cy - h - gapY, w, h);
                default: return new Rectangle(cx, cy + h + gapY, w, h);
            }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            var g = e.Graphics;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.Clear(BackColor);

            foreach (var slot in All)
            {
                if (slot == side) continue;
                var r = Screen(slot);
                using (var pen = new Pen(hover == slot ? OtherColor : SystemColors.ControlDark, 1.5f) { DashStyle = DashStyle.Dash })
                using (var path = Rounded(r, 6))
                {
                    if (hover == slot)
                        using (var fill = new SolidBrush(Color.FromArgb(40, OtherColor))) g.FillPath(fill, path);
                    g.DrawPath(pen, path);
                }
                TextRenderer.DrawText(g, hover == slot ? "put it here" : slot.Describe(), Font, r, SystemColors.GrayText,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak);
            }

            DrawScreen(g, Screen(null), ThisColor, ThisName, "this PC");
            DrawScreen(g, Screen(side), OtherColor, OtherName, "other PC");

            // Arrow from this PC towards the other one: "move the mouse this way".
            var a = Screen(null);
            var b = Screen(side);
            var from = new PointF(a.X + a.Width / 2f, a.Y + a.Height / 2f);
            var to = new PointF(b.X + b.Width / 2f, b.Y + b.Height / 2f);
            var mid = new PointF((from.X + to.X) / 2, (from.Y + to.Y) / 2);
            using (var pen = new Pen(Color.FromArgb(220, 40, 40, 40), 2.5f) { CustomEndCap = new AdjustableArrowCap(4, 4) })
            {
                float dx = to.X - from.X, dy = to.Y - from.Y, len = (float)Math.Sqrt(dx * dx + dy * dy);
                float span = Math.Min(len * 0.18f, 14f);
                g.DrawLine(pen, mid.X - dx / len * span, mid.Y - dy / len * span, mid.X + dx / len * span, mid.Y + dy / len * span);
            }

            if (Focused)
            {
                var f = Screen(side);
                f.Inflate(3, 3);
                ControlPaint.DrawFocusRectangle(g, f);
            }
        }

        void DrawScreen(Graphics g, Rectangle r, Color color, string name, string caption)
        {
            using (var path = Rounded(r, 6))
            using (var fill = new SolidBrush(color))
            using (var pen = new Pen(Color.FromArgb(color.R * 6 / 10, color.G * 6 / 10, color.B * 6 / 10), 1.5f))
            {
                g.FillPath(fill, path);
                g.DrawPath(pen, path);
            }
            using (var bold = new Font(Font, FontStyle.Bold))
            {
                var nameRect = new Rectangle(r.X + 2, r.Y + 2, r.Width - 4, r.Height / 2);
                var capRect = new Rectangle(r.X + 2, r.Y + r.Height / 2, r.Width - 4, r.Height / 2 - 2);
                TextRenderer.DrawText(g, name, bold, nameRect, Color.White,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.Bottom | TextFormatFlags.EndEllipsis | TextFormatFlags.SingleLine);
                TextRenderer.DrawText(g, caption, Font, capRect, Color.FromArgb(230, 255, 255, 255),
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.Top | TextFormatFlags.SingleLine);
            }
        }

        static GraphicsPath Rounded(Rectangle r, int radius)
        {
            int d = radius * 2;
            var p = new GraphicsPath();
            p.AddArc(r.X, r.Y, d, d, 180, 90);
            p.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            p.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            p.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            p.CloseFigure();
            return p;
        }

        Edge? SlotAt(Point p)
        {
            foreach (var slot in All)
                if (Screen(slot).Contains(p)) return slot;
            return null;
        }

        protected override void OnMouseMove(MouseEventArgs e)
        {
            var h = SlotAt(e.Location);
            if (h == side) h = null;
            if (h != hover) { hover = h; Invalidate(); }
            base.OnMouseMove(e);
        }

        protected override void OnMouseLeave(EventArgs e)
        {
            hover = null;
            Invalidate();
            base.OnMouseLeave(e);
        }

        protected override void OnMouseDown(MouseEventArgs e)
        {
            Focus();
            var slot = SlotAt(e.Location);
            if (slot.HasValue)
            {
                hover = null;
                Side = slot.Value;
            }
            base.OnMouseDown(e);
        }

        protected override bool IsInputKey(Keys keyData) =>
            keyData == Keys.Left || keyData == Keys.Right || keyData == Keys.Up || keyData == Keys.Down || base.IsInputKey(keyData);

        protected override void OnKeyDown(KeyEventArgs e)
        {
            switch (e.KeyCode)
            {
                case Keys.Left: Side = Edge.Left; break;
                case Keys.Right: Side = Edge.Right; break;
                case Keys.Up: Side = Edge.Top; break;
                case Keys.Down: Side = Edge.Bottom; break;
            }
            base.OnKeyDown(e);
        }

        protected override void OnGotFocus(EventArgs e) { Invalidate(); base.OnGotFocus(e); }
        protected override void OnLostFocus(EventArgs e) { Invalidate(); base.OnLostFocus(e); }
    }
}
