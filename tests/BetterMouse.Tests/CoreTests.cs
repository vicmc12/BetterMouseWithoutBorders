using System;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using Xunit;

namespace BetterMouse.Tests
{
    public class CryptoTests
    {
        static (SecureChannel a, SecureChannel b) Pair()
        {
            var k = Enumerable.Range(0, 4).Select(i => Crypto.RandomBytes(32)).ToArray();
            return (new SecureChannel(k[0], k[1], k[2], k[3]), new SecureChannel(k[2], k[3], k[0], k[1]));
        }

        static byte[] Open(SecureChannel ch, byte[] frame)
        {
            int len = (int)SecureChannel.ReadUInt32(frame, 0);
            var body = frame.Skip(4).ToArray();
            return ch.Open(body, 0, len) ? body.Take(len).ToArray() : null;
        }

        [Fact]
        public void SealedFramesRoundTripInOrder()
        {
            var (a, b) = Pair();
            for (int i = 0; i < 200; i++)
            {
                var msg = Crypto.RandomBytes(1 + i * 7);
                var frame = a.Seal(msg);
                Assert.NotEqual(msg, frame.Skip(4).Take(msg.Length).ToArray()); // encrypted
                Assert.Equal(msg, Open(b, frame));
            }
        }

        [Fact]
        public void TamperedFrameIsRejected()
        {
            var (a, b) = Pair();
            var frame = a.Seal(Encoding.UTF8.GetBytes("hello"));
            frame[5] ^= 1;
            Assert.Null(Open(b, frame));
        }

        [Fact]
        public void ReplayedOrReorderedFrameIsRejected()
        {
            var (a, b) = Pair();
            var f1 = a.Seal(new byte[] { 1 });
            var f2 = a.Seal(new byte[] { 2 });
            Assert.Null(Open(b, f2));      // out of order
            Assert.NotNull(Open(b, f1));
            Assert.Null(Open(b, (byte[])f1.Clone())); // replay
        }

        [Fact]
        public void DifferentKeysDeriveDifferentMasterKeys()
        {
            Assert.Equal(Crypto.DeriveMasterKey("secret-1"), Crypto.DeriveMasterKey("  secret-1 "));
            Assert.NotEqual(Crypto.DeriveMasterKey("secret-1"), Crypto.DeriveMasterKey("secret-2"));
        }

        [Fact]
        public void GeneratedKeysLookRight()
        {
            var k = Crypto.GenerateSecurityKey();
            Assert.Matches("^[A-Z2-9]{4}-[A-Z2-9]{4}-[A-Z2-9]{4}-[A-Z2-9]{4}$", k);
        }
    }

    public class ProtocolTests
    {
        [Fact]
        public void InputMessagesRoundTrip()
        {
            var m = Msg.MouseMove(-12, 34567);
            var r = new PacketReader(m);
            Assert.Equal(MsgType.MouseMove, (MsgType)m[0]);
            Assert.Equal(-12, r.Int32());
            Assert.Equal(34567, r.Int32());

            m = Msg.Key(0xA2, 0x1D, true, true);
            r = new PacketReader(m);
            Assert.Equal(0xA2, r.UInt16());
            Assert.Equal(0x1D, r.UInt16());
            Assert.Equal(3, r.Byte());

            m = Msg.Wheel(-120, true);
            r = new PacketReader(m);
            Assert.Equal(-120, r.Int16());
            Assert.True(r.Bool());

            m = Msg.Enter(Edge.Left, 0.25);
            r = new PacketReader(m);
            Assert.Equal(Edge.Left, (Edge)r.Byte());
            Assert.Equal(0.25, r.Double());
        }

        [Fact]
        public void EndPointParsing()
        {
            Assert.True(NetworkManager.TryParseEndPoint("192.168.1.5", 15155, out var ep));
            Assert.Equal(15155, ep.Port);
            Assert.True(NetworkManager.TryParseEndPoint("10.0.0.2:4000", 15155, out ep));
            Assert.Equal(4000, ep.Port);
            Assert.True(NetworkManager.TryParseEndPoint("fe80::1%12", 15155, out ep));
            Assert.True(NetworkManager.TryParseEndPoint("[fe80::1%12]:4000", 15155, out ep));
            Assert.Equal(4000, ep.Port);
            Assert.False(NetworkManager.TryParseEndPoint("my-desktop", 15155, out _));
            Assert.Equal(new[] { "a", "10.0.0.1", "b" }, NetworkManager.SplitAddresses(" a, 10.0.0.1 ;b ").ToArray());
        }
    }

    public class ScreenLayoutTests
    {
        static readonly ScreenLayout Single = new ScreenLayout(new[] { new Rectangle(0, 0, 1920, 1080) });

        // [1920x1080 primary][2560x1440 to the right, top-aligned]
        static readonly ScreenLayout Dual = new ScreenLayout(new[]
        {
            new Rectangle(0, 0, 1920, 1080),
            new Rectangle(1920, 0, 2560, 1440),
        });

        [Fact]
        public void SingleMonitorEdges()
        {
            Assert.True(Single.TouchesEdge(new Point(1919, 500), Edge.Right));
            Assert.True(Single.TouchesEdge(new Point(2500, 500), Edge.Right)); // unclipped hook point
            Assert.False(Single.TouchesEdge(new Point(1918, 500), Edge.Right));
            Assert.True(Single.TouchesEdge(new Point(0, 500), Edge.Left));
            Assert.True(Single.TouchesEdge(new Point(10, 0), Edge.Top));
            Assert.True(Single.TouchesEdge(new Point(10, 1079), Edge.Bottom));
        }

        [Fact]
        public void InnerEdgesBetweenOwnMonitorsNeverCross()
        {
            Assert.False(Dual.TouchesEdge(new Point(1919, 500), Edge.Right));     // into monitor 2
            Assert.False(Dual.TouchesEdge(new Point(1919, 1079), Edge.Right));
            Assert.True(Dual.TouchesEdge(new Point(4479, 1300), Edge.Right));     // outer right edge
            Assert.True(Dual.TouchesEdge(new Point(0, 100), Edge.Left));
            Assert.False(Dual.TouchesEdge(new Point(1920, 100), Edge.Left));      // monitor 1 is left of it
            Assert.True(Dual.TouchesEdge(new Point(100, 1079), Edge.Bottom));     // bottom of the shorter one
        }

        [Fact]
        public void EntryPointsLandJustInside()
        {
            Assert.Equal(new Point(2, 540), Single.EntryPoint(Edge.Left, 0.5));
            Assert.Equal(new Point(1917, 0), Single.EntryPoint(Edge.Right, 0.0));
            Assert.Equal(new Point(960, 2), Single.EntryPoint(Edge.Top, 0.5));
            Assert.Equal(new Point(960, 540), Single.EntryPoint(Edge.Left, -1)); // centre (hotkey)

            // Left edge of the dual layout at 90% height: only monitor 2 covers y=1296, its left edge
            // is the leftmost at that height.
            Assert.Equal(new Point(1922, 1296), Dual.EntryPoint(Edge.Left, 0.9));
            Assert.Equal(new Point(4477, 144), Dual.EntryPoint(Edge.Right, 0.1));
        }

        [Fact]
        public void RatioMapsBetweenDifferentResolutions()
        {
            var a = Single;
            var b = new ScreenLayout(new[] { new Rectangle(0, 0, 3840, 2160) });
            var ratio = a.RatioAlong(new Point(1919, 270), Edge.Right);
            var entry = b.EntryPoint(Edge.Left, ratio);
            Assert.Equal(2, entry.X);
            Assert.InRange(entry.Y, 539, 542);
        }

        [Fact]
        public void ControlledSideReturnsOnlyThroughTheEntryEdge()
        {
            var s = Single;
            Assert.True(s.CrossesBack(new Point(2, 500), -2, 0, Edge.Left, out _, out var ratio));
            Assert.InRange(ratio, 0.46, 0.47);
            Assert.False(s.CrossesBack(new Point(2, 500), -1, 0, Edge.Left, out var next, out _));
            Assert.Equal(new Point(1, 500), next);
            Assert.False(s.CrossesBack(new Point(1919, 500), 50, 0, Edge.Left, out next, out _)); // other edges clamp
            Assert.Equal(new Point(1919, 500), next);
            Assert.False(s.CrossesBack(new Point(5, 500), -10, 0, Edge.Right, out next, out _));
            Assert.Equal(new Point(0, 500), next);
        }

        [Fact]
        public void AbsoluteCoordinateNormalizationHitsExactPixels()
        {
            foreach (int size in new[] { 1080, 1366, 1920, 2560, 3840, 5760, 7680 })
            {
                for (int px = 0; px < size; px += 7)
                {
                    int n = Injector.Normalize(px, size);
                    Assert.InRange(n, 0, 65535);
                    Assert.Equal(px, (int)((long)n * size / 65536)); // Windows' mapping back to pixels
                }
                int last = Injector.Normalize(size - 1, size);
                Assert.Equal(size - 1, (int)((long)last * size / 65536));
            }
        }
    }

    public class ClipboardTransferTests
    {
        static IncomingClip Pump(OutgoingClip clip, long limit = 1L << 30)
        {
            IncomingClip incoming = null;
            byte[] frame;
            while ((frame = clip.NextFrame()) != null)
            {
                var r = new PacketReader(frame);
                switch ((MsgType)frame[0])
                {
                    case MsgType.ClipBegin:
                        int id = r.Int32();
                        var kind = (ClipKind)r.Byte();
                        incoming = new IncomingClip(id, kind, r.Int64(), limit);
                        break;
                    case MsgType.ClipEntry:
                        r.Int32();
                        incoming.BeginEntry(r.String(), r.Int64(), r.Bool());
                        break;
                    case MsgType.ClipData:
                        r.Int32();
                        incoming.Append(r.Rest());
                        break;
                    case MsgType.ClipEnd:
                        Assert.True(incoming.Complete());
                        break;
                }
            }
            return incoming;
        }

        [Fact]
        public void TextSurvivesChunking()
        {
            var text = string.Concat(Enumerable.Repeat("héllo wörld ✓ ", 20000));
            var bytes = Encoding.UTF8.GetBytes(text);
            using (var clip = OutgoingClip.ForBytes(7, ClipKind.Text, bytes))
            using (var incoming = Pump(clip))
                Assert.Equal(text, Encoding.UTF8.GetString(incoming.Bytes));
        }

        [Fact]
        public void FoldersAndFilesArriveIntact()
        {
            var src = Path.Combine(Path.GetTempPath(), "bm-test-" + Guid.NewGuid().ToString("N"));
            var dir = Path.Combine(src, "Project");
            Directory.CreateDirectory(Path.Combine(dir, "sub", "empty"));
            var big = Crypto.RandomBytes(OutgoingClip.ChunkSize * 3 + 123);
            File.WriteAllBytes(Path.Combine(dir, "sub", "big.bin"), big);
            File.WriteAllText(Path.Combine(dir, "a.txt"), "abc");
            File.WriteAllBytes(Path.Combine(dir, "zero.dat"), new byte[0]);
            var single = Path.Combine(src, "single.txt");
            File.WriteAllText(single, "single");
            try
            {
                using (var clip = OutgoingClip.ForFiles(1, new[] { dir, single }, 1L << 30, out var problem))
                {
                    Assert.Null(problem);
                    using (var incoming = Pump(clip))
                    {
                        Assert.Equal(2, incoming.TopLevelPaths.Count);
                        var outDir = incoming.TopLevelPaths.First(p => p.EndsWith("Project"));
                        Assert.Equal(big, File.ReadAllBytes(Path.Combine(outDir, "sub", "big.bin")));
                        Assert.Equal("abc", File.ReadAllText(Path.Combine(outDir, "a.txt")));
                        Assert.Empty(File.ReadAllBytes(Path.Combine(outDir, "zero.dat")));
                        Assert.True(Directory.Exists(Path.Combine(outDir, "sub", "empty")));
                        Assert.Equal("single", File.ReadAllText(incoming.TopLevelPaths.First(p => p.EndsWith("single.txt"))));
                    }
                }
            }
            finally
            {
                Directory.Delete(src, true);
            }
        }

        [Fact]
        public void OversizedFilesAreRefused()
        {
            var f = Path.GetTempFileName();
            File.WriteAllBytes(f, new byte[5000]);
            try
            {
                Assert.Null(OutgoingClip.ForFiles(1, new[] { f }, 4000, out var problem));
                Assert.Contains("limit", problem);
            }
            finally { File.Delete(f); }
        }

        [Fact]
        public void PathTraversalIsBlocked()
        {
            var root = Path.Combine(Path.GetTempPath(), "root");
            Assert.Null(IncomingClip.SafeCombine(root, @"..\evil.txt"));
            Assert.Null(IncomingClip.SafeCombine(root, @"a\..\..\evil.txt"));
            Assert.Null(IncomingClip.SafeCombine(root, @"C:\Windows\evil.txt"));
            Assert.Null(IncomingClip.SafeCombine(root, @"\\server\share\x"));
            Assert.Null(IncomingClip.SafeCombine(root, @"a:b"));
            Assert.Equal(Path.Combine(root, "ok", "file.txt"), IncomingClip.SafeCombine(root, @"ok\file.txt"));
        }
    }
}
