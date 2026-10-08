using System;
using System.Collections.Concurrent;
using System.Collections.Specialized;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using static BetterMouse.Native;

namespace BetterMouse
{
    /// <summary>
    /// Shares the clipboard (text, images, files) with the other PC. Clipboard access happens on
    /// the UI (STA) thread; incoming data is reassembled on a worker thread so a big transfer
    /// never delays mouse and keyboard handling.
    /// </summary>
    internal sealed class ClipboardSync : IDisposable
    {
        readonly Control ui;
        readonly Func<Settings> settings;
        readonly Action<IFrameSource> sendBulk;
        readonly Func<bool> connected;
        readonly ListenerWindow window;
        readonly System.Windows.Forms.Timer debounce;
        readonly BlockingCollection<byte[]> incoming = new BlockingCollection<byte[]>();
        readonly Thread worker;
        readonly object gate = new object();
        uint ignoreSequence;
        bool changedWhileOffline;
        string lastHash;
        int nextId;
        OutgoingClip outgoing;
        IncomingClip receiving;

        /// <summary>Short user-facing messages (shown as tray balloons).</summary>
        public event Action<string> Notice;

        public ClipboardSync(Control ui, Func<Settings> settings, Action<IFrameSource> sendBulk, Func<bool> connected)
        {
            this.ui = ui;
            this.settings = settings;
            this.sendBulk = sendBulk;
            this.connected = connected;
            IncomingClip.CleanupOldFolders(0);

            debounce = new System.Windows.Forms.Timer { Interval = 250 };
            debounce.Tick += (s, e) => { debounce.Stop(); OnClipboardChanged(); };
            window = new ListenerWindow(() => { debounce.Stop(); debounce.Start(); });

            worker = new Thread(ReceiveLoop) { IsBackground = true, Name = "BetterMouse clipboard" };
            worker.Start();
        }

        public void Dispose()
        {
            debounce.Dispose();
            window.Dispose();
            incoming.CompleteAdding();
        }

        /// <summary>Called on the network reader thread; just queues.</summary>
        public void Receive(byte[] message)
        {
            if (!incoming.IsAddingCompleted) incoming.Add(message);
        }

        /// <summary>If something was copied while the link was down, send it now.</summary>
        public void OnConnected()
        {
            ui.BeginInvoke((Action)(() =>
            {
                if (!changedWhileOffline) return;
                changedWhileOffline = false;
                OnClipboardChanged();
            }));
        }

        public void OnDisconnected()
        {
            lock (gate)
            {
                outgoing?.Cancel();
                outgoing = null;
                lastHash = null; // resend after reconnect even if unchanged
            }
            incoming.Add(new[] { (byte)MsgType.ClipCancel, (byte)0, (byte)0, (byte)0, (byte)0 }); // aborts any partial receive
        }

        // ------------------------------------------------------------ sending (UI thread)

        void OnClipboardChanged()
        {
            if (GetClipboardSequenceNumber() == ignoreSequence) return; // our own write
            var s = settings();
            if (!s.ShareClipboard) return;
            if (!connected())
            {
                changedWhileOffline = true;
                return;
            }
            long limit = s.MaxClipboardMB * 1024L * 1024;

            IDataObject data = Retry(() => Clipboard.GetDataObject());
            if (data == null) return;

            try
            {
                string[] files = null;
                string text = null;
                byte[] png = null;
                Image image = null;

                if (data.GetDataPresent(DataFormats.FileDrop))
                {
                    if (!s.ShareFiles) return;
                    files = data.GetData(DataFormats.FileDrop) as string[];
                }
                else if (data.GetDataPresent(DataFormats.UnicodeText) || data.GetDataPresent(DataFormats.Text))
                {
                    text = data.GetData(DataFormats.UnicodeText) as string ?? data.GetData(DataFormats.Text) as string;
                }
                else if (data.GetDataPresent("PNG"))
                {
                    png = (data.GetData("PNG") as MemoryStream)?.ToArray();
                }
                if (files == null && text == null && png == null && data.GetDataPresent(DataFormats.Bitmap))
                {
                    image = data.GetData(DataFormats.Bitmap) as Image;
                }
                if (files == null && text == null && png == null && image == null) return;

                int id = Interlocked.Increment(ref nextId);
                // Heavy work (walking folders, PNG encoding) off the UI thread.
                Task.Run(() =>
                {
                    OutgoingClip clip = null;
                    try
                    {
                        string problem = null;
                        if (files != null) clip = OutgoingClip.ForFiles(id, files, limit, out problem);
                        else if (text != null)
                        {
                            var bytes = Encoding.UTF8.GetBytes(text);
                            if (bytes.Length <= limit) clip = OutgoingClip.ForBytes(id, ClipKind.Text, bytes);
                            else problem = "Copied text is larger than the sharing limit";
                        }
                        else
                        {
                            if (png == null)
                                using (image)
                                using (var ms = new MemoryStream())
                                {
                                    image.Save(ms, ImageFormat.Png);
                                    png = ms.ToArray();
                                }
                            if (png.Length <= limit) clip = OutgoingClip.ForBytes(id, ClipKind.Image, png);
                            else problem = "Copied image is larger than the sharing limit";
                        }
                        if (problem != null) Notice?.Invoke(problem);
                        if (clip == null) return;

                        lock (gate)
                        {
                            if (clip.Hash == lastHash) { clip.Dispose(); return; }
                            lastHash = clip.Hash;
                            outgoing?.Cancel();
                            outgoing = clip;
                        }
                        Log.Info($"Clipboard: sending {clip.Description}");
                        sendBulk(clip);
                    }
                    catch (Exception ex)
                    {
                        Log.Error("Clipboard read failed", ex);
                        clip?.Dispose();
                    }
                });
            }
            catch (Exception ex)
            {
                Log.Error("Clipboard read failed", ex);
            }
        }

        // ------------------------------------------------------------ receiving (worker thread)

        void ReceiveLoop()
        {
            foreach (var m in incoming.GetConsumingEnumerable())
            {
                try { HandleIncoming(m); }
                catch (Exception ex)
                {
                    Log.Error("Clipboard receive failed", ex);
                    receiving?.Dispose();
                    receiving = null;
                }
            }
        }

        void HandleIncoming(byte[] m)
        {
            var r = new PacketReader(m);
            switch ((MsgType)m[0])
            {
                case MsgType.ClipBegin:
                {
                    int id = r.Int32();
                    var kind = (ClipKind)r.Byte();
                    long total = r.Int64();
                    receiving?.Dispose();
                    receiving = null;
                    var s = settings();
                    if (!s.ShareClipboard || (kind == ClipKind.Files && !s.ShareFiles)) return;
                    receiving = new IncomingClip(id, kind, total, s.MaxClipboardMB * 1024L * 1024);
                    if (receiving.Failed)
                        Notice?.Invoke($"The other PC copied {OutgoingClip.Size(total)}, more than this PC's sharing limit");
                    break;
                }
                case MsgType.ClipEntry:
                {
                    int id = r.Int32();
                    if (receiving?.Id != id) return;
                    receiving.BeginEntry(r.String(), r.Int64(), r.Bool());
                    break;
                }
                case MsgType.ClipData:
                {
                    int id = r.Int32();
                    if (receiving?.Id != id) return;
                    receiving.Append(r.Rest());
                    break;
                }
                case MsgType.ClipEnd:
                {
                    int id = r.Int32();
                    var clip = receiving;
                    if (clip?.Id != id) return;
                    receiving = null;
                    if (clip.Complete()) ui.BeginInvoke((Action)(() => Apply(clip)));
                    else clip.Dispose();
                    break;
                }
                case MsgType.ClipCancel:
                {
                    receiving?.Dispose();
                    receiving = null;
                    break;
                }
            }
        }

        void Apply(IncomingClip clip)
        {
            try
            {
                var data = new DataObject();
                string hash, what;
                switch (clip.Kind)
                {
                    case ClipKind.Text:
                    {
                        var bytes = clip.Bytes;
                        data.SetData(DataFormats.UnicodeText, Encoding.UTF8.GetString(bytes));
                        hash = ClipKind.Text + ":" + OutgoingClip.Sha(bytes);
                        what = "text";
                        break;
                    }
                    case ClipKind.Image:
                    {
                        var bytes = clip.Bytes;
                        var bitmap = new Bitmap(new MemoryStream(bytes));
                        data.SetData(DataFormats.Bitmap, true, bitmap);
                        data.SetData("PNG", false, new MemoryStream(bytes));
                        hash = ClipKind.Image + ":" + OutgoingClip.Sha(bytes);
                        what = "image";
                        break;
                    }
                    default:
                    {
                        var list = new StringCollection();
                        foreach (var p in clip.TopLevelPaths) list.Add(p);
                        if (list.Count == 0) return;
                        data.SetFileDropList(list);
                        data.SetData("Preferred DropEffect", new MemoryStream(BitConverter.GetBytes(1))); // DROPEFFECT_COPY
                        hash = null;
                        what = $"{list.Count} item(s), {OutgoingClip.Size(clip.TotalBytes)}";
                        break;
                    }
                }

                bool ok = Retry(() => { Clipboard.SetDataObject(data, true, 5, 50); return true; });
                if (!ok) return;
                ignoreSequence = GetClipboardSequenceNumber();
                lock (gate) lastHash = hash;
                Log.Info("Clipboard: received " + what);
            }
            catch (Exception ex)
            {
                Log.Error("Could not set the clipboard", ex);
            }
            finally
            {
                clip.Dispose();
            }
        }

        static T Retry<T>(Func<T> action) where T : class
        {
            for (int i = 0; i < 6; i++)
            {
                try { return action(); }
                catch (ExternalException) { Thread.Sleep(40); } // clipboard busy in another app
            }
            return null;
        }

        static bool Retry(Func<bool> action)
        {
            for (int i = 0; i < 6; i++)
            {
                try { return action(); }
                catch (ExternalException) { Thread.Sleep(40); }
            }
            return false;
        }

        /// <summary>Message-only window that receives WM_CLIPBOARDUPDATE.</summary>
        sealed class ListenerWindow : NativeWindow, IDisposable
        {
            readonly Action changed;

            public ListenerWindow(Action changed)
            {
                this.changed = changed;
                CreateHandle(new CreateParams { Parent = HWND_MESSAGE });
                if (!AddClipboardFormatListener(Handle))
                    Log.Warn("Clipboard listener failed, error " + Marshal.GetLastWin32Error());
            }

            protected override void WndProc(ref Message m)
            {
                if ((uint)m.Msg == WM_CLIPBOARDUPDATE) changed();
                base.WndProc(ref m);
            }

            public void Dispose()
            {
                if (Handle != IntPtr.Zero)
                {
                    RemoveClipboardFormatListener(Handle);
                    DestroyHandle();
                }
            }
        }
    }
}
