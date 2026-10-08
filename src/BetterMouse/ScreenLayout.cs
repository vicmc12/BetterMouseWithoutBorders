using System;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using static BetterMouse.Native;

namespace BetterMouse
{
    /// <summary>
    /// Immutable snapshot of the monitor layout in physical pixels, plus the geometry rules for
    /// crossing to the other PC. A crossing edge is an outer edge: a monitor edge with no other
    /// monitor anywhere beyond it, so moving between your own monitors never jumps PCs.
    /// </summary>
    internal sealed class ScreenLayout
    {
        static volatile ScreenLayout current;

        public readonly Rectangle[] Monitors;
        public readonly Rectangle Primary;
        public readonly Rectangle Bounds;

        public ScreenLayout(Rectangle[] monitors, int primaryIndex = 0)
        {
            if (monitors == null || monitors.Length == 0) throw new ArgumentException("No monitors");
            Monitors = monitors;
            Primary = monitors[Math.Max(0, Math.Min(primaryIndex, monitors.Length - 1))];
            var b = monitors[0];
            foreach (var m in monitors) b = Rectangle.Union(b, m);
            Bounds = b;
        }

        public static ScreenLayout Current => current ?? Refresh();

        public static ScreenLayout Refresh()
        {
            var layout = Capture();
            current = layout;
            return layout;
        }

        static ScreenLayout Capture()
        {
            var list = new List<Rectangle>();
            int primary = 0;
            try
            {
                EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, (IntPtr h, IntPtr dc, ref RECT r, IntPtr d) =>
                {
                    var mi = new MONITORINFO { cbSize = System.Runtime.InteropServices.Marshal.SizeOf<MONITORINFO>() };
                    if (GetMonitorInfo(h, ref mi))
                    {
                        if ((mi.dwFlags & MONITORINFOF_PRIMARY) != 0) primary = list.Count;
                        var rc = mi.rcMonitor;
                        list.Add(Rectangle.FromLTRB(rc.Left, rc.Top, rc.Right, rc.Bottom));
                    }
                    return true;
                }, IntPtr.Zero);
            }
            catch (Exception ex)
            {
                Log.Warn("EnumDisplayMonitors failed: " + ex.Message);
            }
            if (list.Count == 0)
                list.Add(new Rectangle(0, 0, Math.Max(1, GetSystemMetrics(SM_CXSCREEN)), Math.Max(1, GetSystemMetrics(SM_CYSCREEN))));
            return new ScreenLayout(list.ToArray(), primary);
        }

        public Point PrimaryCenter => Center(Primary);

        public static Point Center(Rectangle r) => new Point(r.X + r.Width / 2, r.Y + r.Height / 2);

        /// <summary>The monitor containing the point, else the nearest one.</summary>
        public Rectangle MonitorAt(Point p)
        {
            var best = Monitors[0];
            long bestDistance = long.MaxValue;
            foreach (var m in Monitors)
            {
                if (m.Contains(p)) return m;
                long d = DistanceSquared(m, p);
                if (d < bestDistance) { bestDistance = d; best = m; }
            }
            return best;
        }

        public Point Clamp(Point p)
        {
            var m = MonitorAt(p);
            return new Point(Math.Min(Math.Max(p.X, m.Left), m.Right - 1), Math.Min(Math.Max(p.Y, m.Top), m.Bottom - 1));
        }

        public bool HasMonitorBeyond(Rectangle m, Edge e)
        {
            switch (e)
            {
                case Edge.Left: return Monitors.Any(o => o.Right <= m.Left);
                case Edge.Right: return Monitors.Any(o => o.Left >= m.Right);
                case Edge.Top: return Monitors.Any(o => o.Bottom <= m.Top);
                default: return Monitors.Any(o => o.Top >= m.Bottom);
            }
        }

        /// <summary>True when the pointer touches an outer edge of the desktop on side <paramref name="e"/>.</summary>
        public bool TouchesEdge(Point p, Edge e)
        {
            var m = MonitorAt(p);
            bool atEdge;
            switch (e)
            {
                case Edge.Left: atEdge = p.X <= m.Left; break;
                case Edge.Right: atEdge = p.X >= m.Right - 1; break;
                case Edge.Top: atEdge = p.Y <= m.Top; break;
                default: atEdge = p.Y >= m.Bottom - 1; break;
            }
            return atEdge && !HasMonitorBeyond(m, e);
        }

        /// <summary>Where along the edge the point is, 0..1 across the whole desktop.</summary>
        public double RatioAlong(Point p, Edge e)
        {
            double r = e.IsHorizontal()
                ? (p.Y - Bounds.Top + 0.5) / Bounds.Height
                : (p.X - Bounds.Left + 0.5) / Bounds.Width;
            return Math.Min(1, Math.Max(0, r));
        }

        /// <summary>
        /// Point just inside edge <paramref name="e"/> at <paramref name="ratio"/> along it
        /// (negative ratio = centre of the primary monitor).
        /// </summary>
        public Point EntryPoint(Edge e, double ratio, int inset = 2)
        {
            if (ratio < 0 || double.IsNaN(ratio)) return PrimaryCenter;
            ratio = Math.Min(1, ratio);
            if (e.IsHorizontal())
            {
                int y = Math.Min(Bounds.Bottom - 1, Bounds.Top + (int)Math.Floor(ratio * Bounds.Height));
                var row = Monitors.Where(m => m.Top <= y && y < m.Bottom).ToList();
                Rectangle pick;
                if (row.Count > 0)
                {
                    pick = e == Edge.Left ? row.OrderBy(m => m.Left).First() : row.OrderByDescending(m => m.Right).First();
                }
                else
                {
                    pick = Monitors.OrderBy(m => y < m.Top ? m.Top - y : y - (m.Bottom - 1))
                                   .ThenBy(m => e == Edge.Left ? m.Left : -m.Right).First();
                    y = Math.Min(Math.Max(y, pick.Top), pick.Bottom - 1);
                }
                int x = e == Edge.Left ? pick.Left + inset : pick.Right - 1 - inset;
                x = Math.Min(Math.Max(x, pick.Left), pick.Right - 1);
                return new Point(x, y);
            }
            else
            {
                int x = Math.Min(Bounds.Right - 1, Bounds.Left + (int)Math.Floor(ratio * Bounds.Width));
                var column = Monitors.Where(m => m.Left <= x && x < m.Right).ToList();
                Rectangle pick;
                if (column.Count > 0)
                {
                    pick = e == Edge.Top ? column.OrderBy(m => m.Top).First() : column.OrderByDescending(m => m.Bottom).First();
                }
                else
                {
                    pick = Monitors.OrderBy(m => x < m.Left ? m.Left - x : x - (m.Right - 1))
                                   .ThenBy(m => e == Edge.Top ? m.Top : -m.Bottom).First();
                    x = Math.Min(Math.Max(x, pick.Left), pick.Right - 1);
                }
                int y = e == Edge.Top ? pick.Top + inset : pick.Bottom - 1 - inset;
                y = Math.Min(Math.Max(y, pick.Top), pick.Bottom - 1);
                return new Point(x, y);
            }
        }

        /// <summary>
        /// Controlled side: applies a move. Returns true when it pushes the cursor through the
        /// outer edge <paramref name="returnEdge"/>, i.e. control should go back to the other PC.
        /// </summary>
        public bool CrossesBack(Point cur, int dx, int dy, Edge returnEdge, out Point next, out double ratio)
        {
            var m = MonitorAt(cur);
            var target = new Point(cur.X + dx, cur.Y + dy);
            bool crossing;
            switch (returnEdge)
            {
                case Edge.Left: crossing = dx < 0 && target.X <= m.Left; break;
                case Edge.Right: crossing = dx > 0 && target.X >= m.Right - 1; break;
                case Edge.Top: crossing = dy < 0 && target.Y <= m.Top; break;
                default: crossing = dy > 0 && target.Y >= m.Bottom - 1; break;
            }
            crossing = crossing && !HasMonitorBeyond(m, returnEdge);
            next = Clamp(target);
            ratio = crossing ? RatioAlong(next, returnEdge) : 0;
            return crossing;
        }

        static long DistanceSquared(Rectangle r, Point p)
        {
            long dx = p.X < r.Left ? r.Left - p.X : p.X >= r.Right ? p.X - (r.Right - 1) : 0;
            long dy = p.Y < r.Top ? r.Top - p.Y : p.Y >= r.Bottom ? p.Y - (r.Bottom - 1) : 0;
            return dx * dx + dy * dy;
        }

        public override string ToString() =>
            string.Join(" ", Monitors.Select(m => $"[{m.X},{m.Y} {m.Width}x{m.Height}{(m == Primary ? " primary" : "")}]"));
    }
}
