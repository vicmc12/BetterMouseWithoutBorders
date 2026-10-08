using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.Linq;
using System.Runtime.InteropServices;
using System.Threading;
using Xunit;
using Xunit.Abstractions;

namespace BetterMouse.Tests
{
    /// <summary>
    /// Drives the real low-level hooks on this desktop. Opt-in only (BM_INTERACTIVE=1) because it
    /// briefly takes over the mouse; a watchdog kills the process after a few seconds no matter what.
    /// </summary>
    public class InteractiveTests
    {
        readonly ITestOutputHelper output;

        public InteractiveTests(ITestOutputHelper output)
        {
            this.output = output;
            SetProcessDpiAwarenessContext(new IntPtr(-4)); // like the app's manifest: per-monitor v2
        }

        [DllImport("user32.dll")] static extern bool SetProcessDpiAwarenessContext(IntPtr value);

        [StructLayout(LayoutKind.Sequential)]
        struct CURSORINFO { public int cbSize; public int flags; public IntPtr hCursor; public Native.POINT pt; }

        [DllImport("user32.dll")] static extern bool GetCursorInfo(ref CURSORINFO ci);
        [DllImport("user32.dll")] static extern IntPtr LoadCursor(IntPtr inst, IntPtr name);

        static void InjectMove(int dx, int dy) => Send(new Native.INPUT
        {
            type = Native.INPUT_MOUSE,
            U = new Native.InputUnion { mi = new Native.MOUSEINPUT { dx = dx, dy = dy, dwFlags = Native.MOUSEEVENTF_MOVE } }
        });

        static void InjectKey(ushort vk, bool up) => Send(new Native.INPUT
        {
            type = Native.INPUT_KEYBOARD,
            U = new Native.InputUnion { ki = new Native.KEYBDINPUT { wVk = vk, dwFlags = up ? Native.KEYEVENTF_KEYUP : 0 } }
        });

        static void Send(Native.INPUT input) => Native.SendInput(1, new[] { input }, Marshal.SizeOf<Native.INPUT>());

        static bool WaitFor(Func<bool> f, int ms)
        {
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < ms) { if (f()) return true; Thread.Sleep(5); }
            return f();
        }

        [Fact]
        public void EnterCaptureForwardAndReturn()
        {
            if (Environment.GetEnvironmentVariable("BM_INTERACTIVE") != "1") return;
            new Thread(() => { Thread.Sleep(4000); Environment.Exit(3); }) { IsBackground = true }.Start();

            Native.GetCursorPos(out var original);
            var sent = new ConcurrentQueue<byte[]>();
            var kvm = new KvmEngine(m => sent.Enqueue(m), () => true);
            kvm.Start();
            try
            {
                Native.SetCursorPos(900, 500);
                kvm.SwitchToOther();
                Assert.True(WaitFor(() => sent.Any(m => m[0] == (byte)MsgType.Enter), 1000), "no Enter sent");
                Thread.Sleep(50);
                Native.GetCursorPos(out var parked);
                var ci = new CURSORINFO { cbSize = Marshal.SizeOf<CURSORINFO>() };
                GetCursorInfo(ref ci);
                var arrow = LoadCursor(IntPtr.Zero, new IntPtr(32512));
                output.WriteLine($"parked at {parked.X},{parked.Y}; cursor handle {ci.hCursor} (arrow {arrow}), flags {ci.flags}");

                InjectMove(10, -3);
                Assert.True(WaitFor(() => sent.Any(m => m[0] == (byte)MsgType.MouseMove), 1000), "move not forwarded");
                var move = new PacketReader(sent.First(m => m[0] == (byte)MsgType.MouseMove));
                output.WriteLine($"forwarded move {move.Int32()},{move.Int32()}");
                Native.GetCursorPos(out var still);
                Assert.Equal(parked.X, still.X);
                Assert.Equal(parked.Y, still.Y);

                InjectKey(0x87, false); // F24: harmless if it ever leaked
                InjectKey(0x87, true);
                Assert.True(WaitFor(() => sent.Count(m => m[0] == (byte)MsgType.Key) == 2, 1000), "keys not forwarded");

                kvm.SwitchToThis();
                Assert.True(WaitFor(() => sent.Any(m => m[0] == (byte)MsgType.EndControl), 1000), "no EndControl");

                int before = sent.Count;
                InjectMove(5, 0);
                Thread.Sleep(100);
                Assert.Equal(before, sent.Count); // local again: nothing forwarded
                Native.GetCursorPos(out var local);
                output.WriteLine($"after return the cursor moves locally: {local.X},{local.Y}");
                Assert.NotEqual(parked.X, local.X);
                output.WriteLine("messages: " + string.Join(", ", sent.Select(m => (MsgType)m[0])));
            }
            finally
            {
                kvm.Dispose();
                Native.SetCursorPos(original.X, original.Y);
            }
        }

        [Fact]
        public void SilentPeerGivesTheCursorBackAutomatically()
        {
            if (Environment.GetEnvironmentVariable("BM_INTERACTIVE") != "1") return;
            new Thread(() => { Thread.Sleep(4000); Environment.Exit(3); }) { IsBackground = true }.Start();

            Native.GetCursorPos(out var original);
            bool peerAlive = true;
            var sent = new ConcurrentQueue<byte[]>();
            var kvm = new KvmEngine(m => sent.Enqueue(m), () => Volatile.Read(ref peerAlive));
            kvm.Start();
            try
            {
                Native.SetCursorPos(900, 500);
                kvm.SwitchToOther();
                Assert.True(WaitFor(() => sent.Any(m => m[0] == (byte)MsgType.Enter), 1000));
                Volatile.Write(ref peerAlive, false); // Wi-Fi drops while controlling the other PC
                var sw = Stopwatch.StartNew();
                InjectMove(5, 0);
                Thread.Sleep(600);
                int count = sent.Count;
                InjectMove(5, 0);
                Thread.Sleep(50);
                Assert.Equal(count, sent.Count); // no longer forwarding: the local mouse works again
                output.WriteLine($"local again after <= {sw.ElapsedMilliseconds} ms");
            }
            finally
            {
                kvm.Dispose();
                Native.SetCursorPos(original.X, original.Y);
            }
        }

        [Fact]
        public void ControlledSideInjectsAndHandsBackAtTheEntryEdge()
        {
            if (Environment.GetEnvironmentVariable("BM_INTERACTIVE") != "1") return;
            new Thread(() => { Thread.Sleep(4000); Environment.Exit(3); }) { IsBackground = true }.Start();

            Native.GetCursorPos(out var original);
            // A local engine whose crossing edge is the same right edge: it must ignore injected input.
            var engineSent = new ConcurrentQueue<byte[]>();
            var kvm = new KvmEngine(m => engineSent.Enqueue(m), () => true);
            kvm.Configure(true, Edge.Right);
            kvm.Start();
            kvm.Configure(true, Edge.Right);
            var injectorSent = new ConcurrentQueue<byte[]>();
            var injector = new Injector(m => injectorSent.Enqueue(m));
            try
            {
                var layout = ScreenLayout.Refresh();
                injector.Enter(Edge.Right, 0.5);
                Thread.Sleep(30);
                Native.GetCursorPos(out var p);
                output.WriteLine($"entered at {p.X},{p.Y} (desktop {layout})");
                Assert.Equal(layout.Bounds.Right - 3, p.X);

                injector.Move(-100, 20);
                Thread.Sleep(30);
                Native.GetCursorPos(out var p2);
                Assert.Equal(p.X - 100, p2.X);
                Assert.Equal(p.Y + 20, p2.Y);

                injector.Key(0x87, 0, false, false); // F24 held...
                Assert.True(injector.IsControlled);

                injector.Move(150, 0);                // ...pushes through the right edge -> back to the other PC
                Assert.False(injector.IsControlled);
                Assert.True(injectorSent.TryDequeue(out var leave) && leave[0] == (byte)MsgType.Leave, "no Leave sent");
                output.WriteLine($"handed back at ratio {new PacketReader(leave).Double():0.000}");

                injector.Move(-50, 0);                // ignored once control ended
                Thread.Sleep(30);
                Native.GetCursorPos(out var p3);
                Assert.Equal(layout.Bounds.Right - 3 - 100, p3.X);

                Thread.Sleep(100);
                Assert.Empty(engineSent); // the local engine never mistook injected input for the user's
            }
            finally
            {
                injector.EndControl();
                kvm.Dispose();
                Native.SetCursorPos(original.X, original.Y);
            }
        }
    }
}
