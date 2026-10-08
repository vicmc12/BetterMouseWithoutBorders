using System;
using System.Collections.Generic;
using System.Drawing;
using System.Runtime.InteropServices;
using static BetterMouse.Native;

namespace BetterMouse
{
    /// <summary>
    /// Controlled side: replays the other PC's mouse and keyboard with SendInput (works for a
    /// normal user; Windows only blocks it for elevated windows, the lock screen and UAC prompts).
    /// Remembers every key/button it pressed so it can always release them.
    /// </summary>
    internal sealed class Injector
    {
        static readonly int InputSize = Marshal.SizeOf<INPUT>();

        readonly object gate = new object();
        readonly Action<byte[]> send;
        readonly Dictionary<ushort, (ushort scan, bool ext)> keysDown = new Dictionary<ushort, (ushort, bool)>();
        readonly HashSet<MouseButton> buttonsDown = new HashSet<MouseButton>();
        bool controlled;
        Edge returnEdge;
        Point cursor;
        volatile bool resync;

        public event Action<bool> ControlledChanged;

        public Injector(Action<byte[]> send)
        {
            this.send = send;
        }

        public bool IsControlled { get { lock (gate) return controlled; } }

        /// <summary>The local user moved the real mouse: re-read the cursor before the next remote move.</summary>
        public void MarkLocalMouseMoved() => resync = true;

        public void Enter(Edge edge, double ratio)
        {
            bool changed;
            lock (gate)
            {
                var layout = ScreenLayout.Refresh();
                var p = layout.EntryPoint(edge, ratio);
                MoveTo(p);
                SetCursorPos(p.X, p.Y); // belt and braces: the entry point must never be missed
                cursor = p;
                var expected = p;
                System.Threading.ThreadPool.QueueUserWorkItem(_ => CheckEntry(expected));
                returnEdge = edge;
                resync = false;
                changed = !controlled;
                controlled = true;
            }
            Log.Info($"Now controlled by the other PC (entered at {edge} edge)");
            if (changed) ControlledChanged?.Invoke(true);
        }

        int lastNudge = Environment.TickCount - 600_000;

        /// <summary>
        /// The user is active on the other PC: reset this PC's idle timer (what Teams uses for
        /// "Away", and what the screen saver / auto-lock count from) with a zero-pixel mouse move.
        /// Nothing moves on screen. Skipped while someone uses this PC directly or while it is
        /// being controlled (real input flows then anyway).
        /// </summary>
        public void Nudge(string otherPc)
        {
            lock (gate)
                if (controlled) return;
            if (IdleMs() < 10_000) return;
            if (unchecked(Environment.TickCount - lastNudge) > 120_000)
                Log.Info($"Keeping this PC active while you work on {otherPc} (mirrored activity)");
            lastNudge = Environment.TickCount;
            SendMouse(MOUSEEVENTF_MOVE, 0, 0, 0);
        }

        static uint IdleMs()
        {
            var info = new LASTINPUTINFO { cbSize = (uint)Marshal.SizeOf<LASTINPUTINFO>() };
            return GetLastInputInfo(ref info) ? unchecked((uint)Environment.TickCount - info.dwTime) : 0;
        }

        /// <summary>Diagnostics: shortly after entering, is the cursor where we put it?</summary>
        void CheckEntry(Point expected)
        {
            System.Threading.Thread.Sleep(150);
            lock (gate)
            {
                if (!controlled || resync || !GetCursorPos(out var actual)) return;
                long dx = actual.X - cursor.X, dy = actual.Y - cursor.Y;
                if (dx * dx + dy * dy <= 60 * 60) return;
                Log.Warn($"Cursor entered at {expected.X},{expected.Y} but is at {actual.X},{actual.Y} " +
                         $"(expected near {cursor.X},{cursor.Y}). Another program moved it, or Windows blocked " +
                         "the input (a program running as administrator in front?). Re-placing it.");
                SetCursorPos(cursor.X, cursor.Y);
            }
        }

        public void Move(int dx, int dy)
        {
            byte[] leave = null;
            lock (gate)
            {
                if (!controlled) return;
                var layout = ScreenLayout.Current;
                if (resync)
                {
                    resync = false;
                    if (GetCursorPos(out var p)) cursor = new Point(p.X, p.Y);
                }
                if (layout.CrossesBack(cursor, dx, dy, returnEdge, out var next, out var ratio))
                {
                    controlled = false;
                    ReleaseAllLocked();
                    leave = Msg.Leave(ratio);
                }
                else if (next != cursor)
                {
                    MoveTo(next);
                    cursor = next;
                }
            }
            if (leave != null)
            {
                send(leave);
                Log.Info("Cursor went back to the other PC");
                ControlledChanged?.Invoke(false);
            }
        }

        public void Button(MouseButton button, bool down)
        {
            lock (gate)
            {
                if (down)
                {
                    if (!controlled) return;
                    buttonsDown.Add(button);
                }
                else if (!buttonsDown.Remove(button)) return;
                SendButton(button, down);
            }
        }

        public void Wheel(short delta, bool horizontal)
        {
            lock (gate)
            {
                if (!controlled) return;
                SendMouse(horizontal ? MOUSEEVENTF_HWHEEL : MOUSEEVENTF_WHEEL, 0, 0, unchecked((uint)delta));
            }
        }

        public void Key(ushort vk, ushort scan, bool extended, bool up)
        {
            lock (gate)
            {
                if (!up)
                {
                    if (!controlled) return;
                    keysDown[vk] = (scan, extended);
                }
                else if (!keysDown.Remove(vk)) return;
                SendKey(vk, scan, extended, up);
            }
        }

        /// <summary>
        /// This PC wants its cursor back (Ctrl+Alt+F1 pressed here, session locked...): stop obeying
        /// the other PC and tell it to take its own cursor back.
        /// </summary>
        public void ReturnControl()
        {
            byte[] leave;
            lock (gate)
            {
                if (!controlled) return;
                controlled = false;
                ReleaseAllLocked();
                leave = Msg.Leave(ScreenLayout.Current.RatioAlong(cursor, returnEdge));
            }
            send(leave);
            Log.Info("Took control back from the other PC");
            ControlledChanged?.Invoke(false);
        }

        /// <summary>Control ended (hotkey on the other PC, disconnect, ...).</summary>
        public void EndControl()
        {
            bool changed;
            lock (gate)
            {
                changed = controlled;
                controlled = false;
                ReleaseAllLocked();
            }
            if (changed) ControlledChanged?.Invoke(false);
        }

        void ReleaseAllLocked()
        {
            foreach (var kv in keysDown) SendKey(kv.Key, kv.Value.scan, kv.Value.ext, true);
            keysDown.Clear();
            foreach (var b in buttonsDown) SendButton(b, false);
            buttonsDown.Clear();
        }

        static void MoveTo(Point p)
        {
            // The virtual desktop as the union of the monitors in physical pixels: the same map the
            // rest of the app uses (GetSystemMetrics can report scaled sizes on high-DPI laptops).
            var v = ScreenLayout.Current.Bounds;
            SendMouse(MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK,
                Normalize(p.X - v.X, Math.Max(1, v.Width)), Normalize(p.Y - v.Y, Math.Max(1, v.Height)), 0);
        }

        /// <summary>Maps a pixel offset to the 0..65535 range so Windows lands exactly on that pixel.</summary>
        internal static int Normalize(int offset, int size)
        {
            if (size <= 1) return 0;
            long n = ((long)offset * 65536 + size - 1) / size;
            return (int)Math.Min(65535, Math.Max(0, n));
        }

        static void SendButton(MouseButton button, bool down)
        {
            switch (button)
            {
                case MouseButton.Left: SendMouse(down ? MOUSEEVENTF_LEFTDOWN : MOUSEEVENTF_LEFTUP, 0, 0, 0); break;
                case MouseButton.Right: SendMouse(down ? MOUSEEVENTF_RIGHTDOWN : MOUSEEVENTF_RIGHTUP, 0, 0, 0); break;
                case MouseButton.Middle: SendMouse(down ? MOUSEEVENTF_MIDDLEDOWN : MOUSEEVENTF_MIDDLEUP, 0, 0, 0); break;
                case MouseButton.X1: SendMouse(down ? MOUSEEVENTF_XDOWN : MOUSEEVENTF_XUP, 0, 0, XBUTTON1); break;
                case MouseButton.X2: SendMouse(down ? MOUSEEVENTF_XDOWN : MOUSEEVENTF_XUP, 0, 0, XBUTTON2); break;
            }
        }

        static void SendMouse(uint flags, int dx, int dy, uint data)
        {
            var input = new INPUT
            {
                type = INPUT_MOUSE,
                U = new InputUnion
                {
                    mi = new MOUSEINPUT { dx = dx, dy = dy, mouseData = data, dwFlags = flags, dwExtraInfo = KvmEngine.InjectedMarker }
                }
            };
            SendInput(1, new[] { input }, InputSize);
        }

        static void SendKey(ushort vk, ushort scan, bool extended, bool up)
        {
            uint flags = (extended ? KEYEVENTF_EXTENDEDKEY : 0) | (up ? KEYEVENTF_KEYUP : 0);
            var input = new INPUT
            {
                type = INPUT_KEYBOARD,
                U = new InputUnion
                {
                    ki = new KEYBDINPUT { wVk = vk, wScan = scan, dwFlags = flags, dwExtraInfo = KvmEngine.InjectedMarker }
                }
            };
            SendInput(1, new[] { input }, InputSize);
        }
    }
}
