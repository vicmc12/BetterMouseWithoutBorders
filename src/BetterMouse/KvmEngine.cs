using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Drawing;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using static BetterMouse.Native;

namespace BetterMouse
{
    /// <summary>
    /// Owns this PC's mouse and keyboard: low-level hooks (no admin needed) on a dedicated
    /// high-priority thread, edge detection, and capture while the other PC is being controlled.
    /// While controlling the other PC the local cursor is parked, hidden under a tiny invisible
    /// window, and every local input event is swallowed and forwarded instead.
    /// All state below is touched only on the hook thread; other threads go through Post().
    /// </summary>
    internal sealed class KvmEngine : IDisposable
    {
        /// <summary>Tag on every event we inject, so our own hooks ignore it.</summary>
        public static readonly IntPtr InjectedMarker = new IntPtr(0x424D5742);

        const uint WM_APP_WAKE = WM_APP + 1;
        static int instanceCounter;
        // A window class keeps the WndProc of whoever registered it first, so each engine gets its own.
        readonly string overlayClass = "BetterMouseCursorHider" + Interlocked.Increment(ref instanceCounter);
        const int OverlaySize = 96;
        const uint RehookIntervalMs = 60_000;
        static readonly IntPtr RehookTimer = new IntPtr(1);
        static readonly IntPtr WatchdogTimer = new IntPtr(2);
        const uint WatchdogIntervalMs = 250;
        const uint RepeatWindowMs = 1500;

        readonly Action<byte[]> send;
        /// <summary>Connected and recently heard from: safe to hand the cursor over / keep it there.</summary>
        readonly Func<bool> isConnected;
        readonly ConcurrentQueue<Action> commands = new ConcurrentQueue<Action>();
        readonly ManualResetEventSlim ready = new ManualResetEventSlim(false);
        Thread thread;
        uint threadId;

        IntPtr mouseHook, keyboardHook, overlay, blankCursor;
        LowLevelProc mouseProc, keyboardProc;
        WndProc overlayProc;

        bool remote;
        Point park;
        Point exitPoint;
        Edge exitEdge;

        // Stale-event filter. Windows computes each mouse event's position before our hook sees it.
        // When we move the cursor from a posted command (cursor returned, watchdog, hotkey), an
        // event already computed from the OLD position can arrive right after, and would snap the
        // cursor back (e.g. to the hidden spot in the middle of the screen). Shortly after every
        // warp, moves that are closer to the old position than to the new one are dropped.
        const int StaleWindowMs = 300;
        Point staleOld, staleNew;
        uint staleTick;
        bool staleArmed;
        bool edgeSwitching = true;
        Edge peerSide = Edge.Right;

        /// <summary>Keys physically down, with the time of their latest down event.</summary>
        readonly Dictionary<uint, uint> held = new Dictionary<uint, uint>();
        /// <summary>Pressed on this PC before switching: their release must still reach this PC.</summary>
        readonly HashSet<uint> passUps = new HashSet<uint>();
        /// <summary>Pressed on the other PC (or a hotkey): swallow their repeats and release here.</summary>
        readonly HashSet<uint> eatUps = new HashSet<uint>();
        readonly HashSet<uint> forwarded = new HashSet<uint>();
        readonly HashSet<MouseButton> remoteButtons = new HashSet<MouseButton>();
        readonly HashSet<MouseButton> eatButtonUps = new HashSet<MouseButton>();

        /// <summary>Raised on the hook thread when we start/stop controlling the other PC.</summary>
        public event Action<bool> RemoteModeChanged;
        /// <summary>Raised on the hook thread for every real local mouse move. Keep handlers trivial.</summary>
        public event Action LocalMouseMoved;
        /// <summary>Ctrl+Alt+F1 while this PC is not controlling the other one (maybe it is being controlled).</summary>
        public event Action TakeBackRequested;
        /// <summary>The user is really using this PC (raised on the hook thread, at most every 30 s).</summary>
        public event Action LocalActivity;

        const int ActivityIntervalMs = 30_000;
        int lastActivity;
        bool activityPrimed;

        /// <summary>Real (not injected) input on this PC while it is not controlling the other one.</summary>
        void NoteLocalActivity()
        {
            int now = Environment.TickCount;
            if (activityPrimed && unchecked(now - lastActivity) < ActivityIntervalMs) return;
            activityPrimed = true;
            lastActivity = now;
            LocalActivity?.Invoke();
        }

        public KvmEngine(Action<byte[]> send, Func<bool> isConnected)
        {
            this.send = send;
            this.isConnected = isConnected;
        }

        public void Start()
        {
            thread = new Thread(Run) { IsBackground = true, Name = "BetterMouse input", Priority = ThreadPriority.Highest };
            thread.Start();
            ready.Wait(5000);
        }

        public void Dispose()
        {
            if (thread == null) return;
            Post(() =>
            {
                if (remote) LeaveRemote(null, isConnected());
                PostQuitMessage(0);
            });
            thread.Join(2000);
            thread = null;
        }

        // ------------------------------------------------------------ commands (any thread)

        public void Configure(bool switching, Edge side) => Post(() =>
        {
            edgeSwitching = switching;
            peerSide = side;
        });

        /// <summary>Same as Ctrl+Alt+F2.</summary>
        public void SwitchToOther() => Post(() =>
        {
            if (!remote && isConnected()) EnterRemote(peerSide, -1, ScreenLayout.Current.MonitorAt(CursorPos()));
        });

        /// <summary>Same as Ctrl+Alt+F1.</summary>
        public void SwitchToThis() => Post(() =>
        {
            if (remote) LeaveRemote(null, isConnected());
        });

        /// <summary>Connection lost, session locked, sleep...: always give the cursor back to this PC.</summary>
        public void ForceLocal(string reason) => Post(() =>
        {
            if (!remote) return;
            Log.Info("Cursor returned to this PC: " + reason);
            LeaveRemote(null, isConnected());
        });

        /// <summary>The other PC's own mouse crossed over to us; stop capturing.</summary>
        public void OtherTookControl() => Post(() =>
        {
            if (remote) LeaveRemote(null, false);
        });

        /// <summary>The cursor left the other PC through the edge it entered.</summary>
        public void CursorReturned(double ratio) => Post(() =>
        {
            if (remote) LeaveRemote(ScreenLayout.Refresh().EntryPoint(exitEdge, ratio), false);
        });

        /// <summary>After unlock/resume key-up events may have been missed.</summary>
        public void ResetKeyState() => Post(() =>
        {
            held.Clear();
            passUps.Clear();
            eatUps.Clear();
            eatButtonUps.Clear();
        });

        void Post(Action action)
        {
            commands.Enqueue(action);
            if (threadId != 0) PostThreadMessage(threadId, WM_APP_WAKE, IntPtr.Zero, IntPtr.Zero);
        }

        // ------------------------------------------------------------ hook thread

        void Run()
        {
            threadId = GetCurrentThreadId();
            try
            {
                ScreenLayout.Refresh();
                CreateOverlay();
                mouseProc = MouseHookProc;
                keyboardProc = KeyboardHookProc;
                InstallHooks();
            }
            catch (Exception ex)
            {
                Log.Error("Input engine failed to start", ex);
            }
            finally
            {
                ready.Set();
            }
            DrainCommands();

            while (GetMessage(out var msg, IntPtr.Zero, 0, 0) > 0)
            {
                if (msg.hwnd == IntPtr.Zero && msg.message == WM_APP_WAKE)
                {
                    DrainCommands();
                    continue;
                }
                TranslateMessage(ref msg);
                DispatchMessage(ref msg);
            }

            RemoveHooks();
            if (overlay != IntPtr.Zero) DestroyWindow(overlay);
            UnregisterClass(overlayClass, GetModuleHandle(null));
            if (blankCursor != IntPtr.Zero) DestroyCursor(blankCursor);
        }

        void DrainCommands()
        {
            while (commands.TryDequeue(out var a))
            {
                try { a(); }
                catch (Exception ex) { Log.Error("Input command failed", ex); }
            }
        }

        void InstallHooks()
        {
            var module = GetModuleHandle(null);
            mouseHook = SetWindowsHookEx(WH_MOUSE_LL, mouseProc, module, 0);
            if (mouseHook == IntPtr.Zero) Log.Error("Mouse hook failed, error " + Marshal.GetLastWin32Error());
            keyboardHook = SetWindowsHookEx(WH_KEYBOARD_LL, keyboardProc, module, 0);
            if (keyboardHook == IntPtr.Zero) Log.Error("Keyboard hook failed, error " + Marshal.GetLastWin32Error());
        }

        void RemoveHooks()
        {
            if (mouseHook != IntPtr.Zero) UnhookWindowsHookEx(mouseHook);
            if (keyboardHook != IntPtr.Zero) UnhookWindowsHookEx(keyboardHook);
            mouseHook = keyboardHook = IntPtr.Zero;
        }

        void CreateOverlay()
        {
            var module = GetModuleHandle(null);
            // AND mask all ones + XOR mask all zeros = fully transparent cursor.
            var andMask = Enumerable.Repeat((byte)0xFF, 32 * 32 / 8).ToArray();
            var xorMask = new byte[32 * 32 / 8];
            blankCursor = CreateCursor(module, 0, 0, 32, 32, andMask, xorMask);

            overlayProc = OverlayWndProc;
            var wc = new WNDCLASSEX
            {
                cbSize = Marshal.SizeOf<WNDCLASSEX>(),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(overlayProc),
                hInstance = module,
                hCursor = blankCursor,
                lpszClassName = overlayClass,
            };
            RegisterClassEx(ref wc);
            overlay = CreateWindowEx(WS_EX_TOPMOST | WS_EX_TOOLWINDOW | WS_EX_LAYERED | WS_EX_NOACTIVATE,
                overlayClass, AppInfo.Name, WS_POPUP, 0, 0, OverlaySize, OverlaySize,
                IntPtr.Zero, IntPtr.Zero, module, IntPtr.Zero);
            if (overlay == IntPtr.Zero)
            {
                Log.Warn("Cursor-hiding window unavailable, error " + Marshal.GetLastWin32Error());
                return;
            }
            SetLayeredWindowAttributes(overlay, 0, 1, LWA_ALPHA); // practically invisible, still hit-testable
            // Windows silently drops a low-level hook that ever answers too slowly; re-arm periodically.
            SetTimer(overlay, RehookTimer, RehookIntervalMs, IntPtr.Zero);
        }

        IntPtr OverlayWndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
        {
            switch (msg)
            {
                case WM_MOUSEACTIVATE:
                    return new IntPtr(MA_NOACTIVATE);
                case WM_DISPLAYCHANGE:
                case WM_SETTINGCHANGE:
                    ScreenLayout.Refresh();
                    break;
                case WM_TIMER:
                    if (wParam == WatchdogTimer)
                    {
                        // Wi-Fi dropped while controlling the other PC: give the cursor back now
                        // instead of leaving this PC's mouse dead until the link times out.
                        if (remote && !isConnected())
                        {
                            Log.Info("Cursor returned to this PC: the other PC stopped responding");
                            LeaveRemote(null, false);
                        }
                    }
                    else if (!remote)
                    {
                        RemoveHooks();
                        InstallHooks();
                    }
                    return IntPtr.Zero;
            }
            return DefWindowProc(hWnd, msg, wParam, lParam);
        }

        /// <summary>
        /// Parks the cursor at <see cref="park"/> under the invisible window, so it disappears.
        /// Order matters: the window must already be there when the cursor arrives, because the
        /// arrival is what makes Windows ask the window under it for its (blank) cursor.
        /// </summary>
        void HideCursor()
        {
            if (overlay != IntPtr.Zero)
                SetWindowPos(overlay, HWND_TOPMOST, park.X - OverlaySize / 2, park.Y - OverlaySize / 2,
                    OverlaySize, OverlaySize, SWP_NOACTIVATE | SWP_SHOWWINDOW);
            SetCursorPos(park.X, park.Y);
            if (GetCursorPos(out var actual) && (actual.X != park.X || actual.Y != park.Y))
            {
                // A ClipCursor (e.g. a game) kept it elsewhere: park there instead.
                park = new Point(actual.X, actual.Y);
                if (overlay != IntPtr.Zero)
                    SetWindowPos(overlay, HWND_TOPMOST, park.X - OverlaySize / 2, park.Y - OverlaySize / 2,
                        OverlaySize, OverlaySize, SWP_NOACTIVATE | SWP_SHOWWINDOW);
            }
            if (blankCursor != IntPtr.Zero) SetCursor(blankCursor); // don't wait for Windows to notice
        }

        void HideOverlay()
        {
            if (overlay != IntPtr.Zero) ShowWindow(overlay, SW_HIDE);
        }

        // ------------------------------------------------------------ switching

        void EnterRemote(Edge edge, double ratio, Rectangle monitor, Point? from = null)
        {
            var oldPos = from ?? CursorPos();
            remote = true;
            exitEdge = edge;
            // Where the cursor reappears if control comes back any way other than through the
            // edge (watchdog, sleep, lock, hotkey): the border point it left from, not the middle.
            exitPoint = ratio < 0 ? oldPos : ScreenLayout.Current.EntryPoint(edge, ratio);
            passUps.Clear();
            foreach (var vk in held.Keys)
                if (!eatUps.Contains(vk)) passUps.Add(vk);
            forwarded.Clear();
            remoteButtons.Clear();

            park = ScreenLayout.Center(monitor);
            HideCursor();
            ArmStaleFilter(oldPos, park);
            if (overlay != IntPtr.Zero) SetTimer(overlay, WatchdogTimer, WatchdogIntervalMs, IntPtr.Zero);

            send(Msg.Enter(edge.Opposite(), ratio));
            Log.Info(ratio < 0 ? "Switched to the other PC (hotkey/menu)" : $"Crossed the {edge} edge to the other PC");
            RemoteModeChanged?.Invoke(true);
        }

        void LeaveRemote(Point? target, bool notifyPeer)
        {
            remote = false;
            eatUps.UnionWith(forwarded);
            forwarded.Clear();
            passUps.Clear();
            eatButtonUps.UnionWith(remoteButtons);
            remoteButtons.Clear();

            if (overlay != IntPtr.Zero) KillTimer(overlay, WatchdogTimer);
            HideOverlay();
            var p = target ?? exitPoint;
            SetCursorPos(p.X, p.Y);
            ArmStaleFilter(park, p);
            if (notifyPeer) send(Msg.EndControl);
            RemoteModeChanged?.Invoke(false);
        }

        void ArmStaleFilter(Point oldPos, Point newPos)
        {
            ReportStaleDrops();
            staleOld = oldPos;
            staleNew = newPos;
            staleTick = unchecked((uint)Environment.TickCount); // same clock as the hook's event time
            staleArmed = true;
        }

        internal static bool IsStale(Point pt, Point oldPos, Point newPos) =>
            DistanceSquared(pt, oldPos) < DistanceSquared(pt, newPos);

        /// <summary>
        /// An event is stale if the mouse produced it before we moved the cursor: Windows may have
        /// computed its position from the old spot. Events produced afterwards are never dropped,
        /// however fast the mouse moves. Same-millisecond events fall back to the distance test.
        /// </summary>
        internal static bool IsStaleEvent(Point pt, uint eventTime, uint warpTick, Point oldPos, Point newPos)
        {
            int age = unchecked((int)(eventTime - warpTick));
            if (age < 0) return true;
            if (age == 0) return IsStale(pt, oldPos, newPos);
            return false;
        }

        bool IsStaleMove(Point pt, uint eventTime)
        {
            if (!staleArmed) return false;
            if (unchecked((int)(eventTime - staleTick)) > StaleWindowMs)
            {
                staleArmed = false;
                ReportStaleDrops();
                return false;
            }
            if (!IsStaleEvent(pt, eventTime, staleTick, staleOld, staleNew)) return false;
            staleDrops++;
            return true;
        }

        int staleDrops;

        void ReportStaleDrops()
        {
            if (staleDrops == 0) return;
            Log.Info($"Ignored {staleDrops} mouse event(s) computed before the switch (would have moved the cursor away from the border)");
            staleDrops = 0;
        }

        static long DistanceSquared(Point a, Point b)
        {
            long dx = a.X - b.X, dy = a.Y - b.Y;
            return dx * dx + dy * dy;
        }

        // ------------------------------------------------------------ mouse

        IntPtr MouseHookProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode == HC_ACTION)
            {
                try
                {
                    var info = Marshal.PtrToStructure<MSLLHOOKSTRUCT>(lParam);
                    if (info.dwExtraInfo != InjectedMarker && OnMouse((uint)wParam.ToInt64(), ref info))
                        return new IntPtr(1);
                }
                catch (Exception ex)
                {
                    Log.Error("Mouse hook", ex);
                }
            }
            return CallNextHookEx(mouseHook, nCode, wParam, lParam);
        }

        /// <returns>true to swallow the event.</returns>
        bool OnMouse(uint msg, ref MSLLHOOKSTRUCT info)
        {
            var pt = new Point(info.pt.X, info.pt.Y);

            if (!remote)
            {
                // Produced before our last warp, so maybe computed from the old cursor spot: drop
                // it, or it would yank the cursor back to the middle of the screen.
                if (msg == WM_MOUSEMOVE && IsStaleMove(pt, info.time)) return true;
                NoteLocalActivity();

                if (msg == WM_MOUSEMOVE)
                {
                    LocalMouseMoved?.Invoke();
                    if (edgeSwitching && !AnyButtonDown() && isConnected())
                    {
                        var layout = ScreenLayout.Current;
                        if (layout.TouchesEdge(pt, peerSide))
                        {
                            EnterRemote(peerSide, layout.RatioAlong(pt, peerSide), layout.MonitorAt(pt), pt);
                            return true;
                        }
                    }
                    return false;
                }
                var local = ButtonOf(msg, info.mouseData, out bool localDown);
                if (local.HasValue)
                {
                    // Release of a button that was pressed while controlling the other PC.
                    if (!localDown && eatButtonUps.Remove(local.Value)) return true;
                    if (localDown) eatButtonUps.Remove(local.Value);
                }
                return false;
            }

            switch (msg)
            {
                case WM_MOUSEMOVE:
                {
                    if (GetCursorPos(out var cur) && (cur.X != park.X || cur.Y != park.Y))
                    {
                        // The cursor isn't parked (another program moved it, e.g. a mouse
                        // jiggler): re-park and re-hide it first, and drop this one event.
                        HideCursor();
                        return true;
                    }
                    // Produced before we parked the cursor: its delta would be measured from the
                    // wrong spot and throw the other PC's cursor across its screen.
                    if (IsStaleMove(pt, info.time)) return true;
                    int dx = pt.X - park.X, dy = pt.Y - park.Y;
                    if (dx == 0 && dy == 0) return false;
                    send(Msg.MouseMove(dx, dy));
                    return true;
                }
                case WM_MOUSEWHEEL:
                    send(Msg.Wheel(unchecked((short)(info.mouseData >> 16)), false));
                    return true;
                case WM_MOUSEHWHEEL:
                    send(Msg.Wheel(unchecked((short)(info.mouseData >> 16)), true));
                    return true;
            }

            var button = ButtonOf(msg, info.mouseData, out bool down);
            if (button.HasValue)
            {
                if (down) remoteButtons.Add(button.Value);
                else remoteButtons.Remove(button.Value);
                send(Msg.MouseButton(button.Value, down));
            }
            return true;
        }

        static MouseButton? ButtonOf(uint msg, uint mouseData, out bool down)
        {
            down = false;
            switch (msg)
            {
                case WM_LBUTTONDOWN: down = true; return MouseButton.Left;
                case WM_LBUTTONUP: return MouseButton.Left;
                case WM_RBUTTONDOWN: down = true; return MouseButton.Right;
                case WM_RBUTTONUP: return MouseButton.Right;
                case WM_MBUTTONDOWN: down = true; return MouseButton.Middle;
                case WM_MBUTTONUP: return MouseButton.Middle;
                case WM_XBUTTONDOWN: down = true; return (mouseData >> 16) == XBUTTON1 ? MouseButton.X1 : MouseButton.X2;
                case WM_XBUTTONUP: return (mouseData >> 16) == XBUTTON1 ? MouseButton.X1 : MouseButton.X2;
                default: return null;
            }
        }

        /// <summary>No switching mid-drag: a held button would end up stuck on one of the PCs.</summary>
        static bool AnyButtonDown() =>
            IsDown(VK_LBUTTON) || IsDown(VK_RBUTTON) || IsDown(VK_MBUTTON) || IsDown(VK_XBUTTON1) || IsDown(VK_XBUTTON2);

        static bool IsDown(int vk) => (GetAsyncKeyState(vk) & 0x8000) != 0;

        static Point CursorPos() => GetCursorPos(out var p) ? new Point(p.X, p.Y) : ScreenLayout.Current.PrimaryCenter;

        // ------------------------------------------------------------ keyboard

        IntPtr KeyboardHookProc(int nCode, IntPtr wParam, IntPtr lParam)
        {
            if (nCode == HC_ACTION)
            {
                try
                {
                    var info = Marshal.PtrToStructure<KBDLLHOOKSTRUCT>(lParam);
                    if (info.dwExtraInfo != InjectedMarker && OnKey(ref info)) return new IntPtr(1);
                }
                catch (Exception ex)
                {
                    Log.Error("Keyboard hook", ex);
                }
            }
            return CallNextHookEx(keyboardHook, nCode, wParam, lParam);
        }

        /// <returns>true to swallow the event.</returns>
        bool OnKey(ref KBDLLHOOKSTRUCT k)
        {
            uint vk = k.vkCode;
            bool up = (k.flags & LLKHF_UP) != 0;
            if (!remote) NoteLocalActivity();
            bool repeat = !up && held.TryGetValue(vk, out var last) && unchecked(k.time - last) < RepeatWindowMs;
            if (up) held.Remove(vk);
            else held[vk] = k.time;

            // Hotkeys, always active: Ctrl+Alt+F1 = this PC, Ctrl+Alt+F2 = other PC.
            if (!up && (vk == VK_F1 || vk == VK_F2) && CtrlAltDown())
            {
                eatUps.Add(vk);
                if (!repeat)
                {
                    if (vk == VK_F1)
                    {
                        if (remote)
                        {
                            Log.Info("Switched back to this PC (hotkey)");
                            LeaveRemote(null, isConnected());
                        }
                        else
                        {
                            TakeBackRequested?.Invoke();
                        }
                    }
                    else if (!remote && isConnected())
                    {
                        EnterRemote(peerSide, -1, ScreenLayout.Current.MonitorAt(CursorPos()));
                    }
                }
                return true;
            }

            if (up)
            {
                if (eatUps.Remove(vk)) return true;
                if (!remote) return false;
                if (passUps.Remove(vk)) return false;
                forwarded.Remove(vk);
                Forward(ref k, true);
                return true;
            }

            if (!repeat)
            {
                // A fresh press: forget stale bookkeeping from a release we never saw.
                eatUps.Remove(vk);
                passUps.Remove(vk);
            }
            if (eatUps.Contains(vk)) return true;   // auto-repeat of a key that belongs to the other PC
            if (!remote) return false;
            if (passUps.Contains(vk)) return false; // auto-repeat of a key that belongs to this PC
            forwarded.Add(vk);
            Forward(ref k, false);
            return true;
        }

        bool CtrlAltDown()
        {
            if (remote)
            {
                // Swallowed keys never reach the async key state; use our own record.
                return (held.ContainsKey(VK_LCONTROL) || held.ContainsKey(VK_RCONTROL) || held.ContainsKey(VK_CONTROL)) &&
                       (held.ContainsKey(VK_LMENU) || held.ContainsKey(VK_RMENU) || held.ContainsKey(VK_MENU));
            }
            return IsDown((int)VK_CONTROL) && IsDown((int)VK_MENU);
        }

        void Forward(ref KBDLLHOOKSTRUCT k, bool up) =>
            send(Msg.Key((ushort)k.vkCode, (ushort)k.scanCode, (k.flags & LLKHF_EXTENDED) != 0, up));
    }
}
