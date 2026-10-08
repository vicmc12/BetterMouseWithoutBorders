using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;

namespace BetterMouse
{
    /// <summary>One clipboard payload streamed to the other PC in small chunks at low priority.</summary>
    internal sealed class OutgoingClip : IFrameSource
    {
        public const int ChunkSize = 32 * 1024;
        const int MaxEntries = 20000;

        sealed class Entry
        {
            public string FullPath, Relative;
            public long Length;
            public bool IsDirectory;
        }

        readonly byte[] data;
        readonly List<Entry> entries;
        IEnumerator<byte[]> frames;
        volatile bool cancelled;

        public int Id { get; }
        public ClipKind Kind { get; }
        public long TotalBytes { get; }
        public string Hash { get; }
        public string Description { get; }

        OutgoingClip(int id, ClipKind kind, byte[] data, List<Entry> entries, long total, string hash, string description)
        {
            Id = id;
            Kind = kind;
            this.data = data;
            this.entries = entries;
            TotalBytes = total;
            Hash = hash;
            Description = description;
        }

        public static OutgoingClip ForBytes(int id, ClipKind kind, byte[] bytes)
        {
            var hash = kind + ":" + Sha(bytes);
            var what = kind == ClipKind.Text ? "text" : "image";
            return new OutgoingClip(id, kind, bytes, null, bytes.Length, hash, $"{what} ({Size(bytes.Length)})");
        }

        /// <summary>Collects the copied files/folders; returns null (with a reason) if they can't be sent.</summary>
        public static OutgoingClip ForFiles(int id, string[] paths, long limitBytes, out string problem)
        {
            problem = null;
            var list = new List<Entry>();
            long total = 0;
            var hash = new StringBuilder("files:");
            try
            {
                foreach (var raw in paths ?? Array.Empty<string>())
                {
                    var path = raw.TrimEnd('\\', '/');
                    var name = Path.GetFileName(path);
                    if (string.IsNullOrEmpty(name)) continue; // a whole drive: not supported
                    if (Directory.Exists(path))
                    {
                        list.Add(new Entry { FullPath = path, Relative = name, IsDirectory = true });
                        foreach (var e in Directory.EnumerateFileSystemEntries(path, "*", SearchOption.AllDirectories))
                        {
                            var rel = name + "\\" + e.Substring(path.Length).TrimStart('\\');
                            if (Directory.Exists(e))
                            {
                                list.Add(new Entry { FullPath = e, Relative = rel, IsDirectory = true });
                            }
                            else
                            {
                                var fi = new FileInfo(e);
                                list.Add(new Entry { FullPath = e, Relative = rel, Length = fi.Length });
                                total += fi.Length;
                                hash.Append(rel).Append('|').Append(fi.Length).Append('|').Append(fi.LastWriteTimeUtc.Ticks).Append(';');
                            }
                            if (list.Count > MaxEntries) { problem = $"Too many files (over {MaxEntries})"; return null; }
                            if (total > limitBytes) break;
                        }
                    }
                    else if (File.Exists(path))
                    {
                        var fi = new FileInfo(path);
                        list.Add(new Entry { FullPath = path, Relative = name, Length = fi.Length });
                        total += fi.Length;
                        hash.Append(path).Append('|').Append(fi.Length).Append('|').Append(fi.LastWriteTimeUtc.Ticks).Append(';');
                    }
                    if (total > limitBytes)
                    {
                        problem = $"Copied files are larger than the {Size(limitBytes)} sharing limit (change it in Settings)";
                        return null;
                    }
                }
            }
            catch (Exception ex)
            {
                problem = "Could not read the copied files: " + ex.Message;
                return null;
            }
            if (list.Count == 0) return null;
            int files = list.Count(e => !e.IsDirectory);
            return new OutgoingClip(id, ClipKind.Files, null, list, total, Sha(Encoding.UTF8.GetBytes(hash.ToString())),
                $"{files} file{(files == 1 ? "" : "s")} ({Size(total)})");
        }

        public void Cancel() => cancelled = true;

        public byte[] NextFrame()
        {
            if (frames == null) frames = Frames().GetEnumerator();
            return frames.MoveNext() ? frames.Current : null;
        }

        IEnumerable<byte[]> Frames()
        {
            yield return Msg.ClipBegin(Id, Kind, TotalBytes, entries?.Count ?? 0);

            if (Kind != ClipKind.Files)
            {
                for (int o = 0; o < data.Length; o += ChunkSize)
                {
                    if (cancelled) { yield return Msg.ClipCancel(Id); yield break; }
                    yield return Msg.ClipData(Id, data, o, Math.Min(ChunkSize, data.Length - o));
                }
                yield return Msg.ClipEnd(Id);
                yield break;
            }

            var buffer = new byte[ChunkSize];
            foreach (var e in entries)
            {
                if (cancelled) { yield return Msg.ClipCancel(Id); yield break; }
                yield return Msg.ClipEntry(Id, e.Relative, e.Length, e.IsDirectory);
                if (e.IsDirectory || e.Length == 0) continue;

                var fs = TryOpen(e.FullPath);
                if (fs == null)
                {
                    Log.Warn("Clipboard: cannot read " + e.FullPath);
                    yield return Msg.ClipCancel(Id);
                    yield break;
                }
                using (fs)
                {
                    long remaining = e.Length;
                    while (remaining > 0)
                    {
                        if (cancelled) { yield return Msg.ClipCancel(Id); yield break; }
                        int n = ReadSafe(fs, buffer, (int)Math.Min(buffer.Length, remaining));
                        if (n <= 0)
                        {
                            Log.Warn("Clipboard: " + e.FullPath + " changed while sending");
                            yield return Msg.ClipCancel(Id);
                            yield break;
                        }
                        remaining -= n;
                        yield return Msg.ClipData(Id, buffer, 0, n);
                    }
                }
            }
            yield return Msg.ClipEnd(Id);
        }

        static FileStream TryOpen(string path)
        {
            try { return new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, ChunkSize); }
            catch { return null; }
        }

        static int ReadSafe(FileStream fs, byte[] buffer, int count)
        {
            try { return fs.Read(buffer, 0, count); }
            catch { return -1; }
        }

        public void Dispose()
        {
            frames?.Dispose();
            frames = null;
        }

        internal static string Sha(byte[] bytes)
        {
            using (var sha = SHA256.Create()) return Convert.ToBase64String(sha.ComputeHash(bytes));
        }

        internal static string Size(long bytes) =>
            bytes >= 1024 * 1024 ? $"{bytes / (1024.0 * 1024):0.#} MB" : bytes >= 1024 ? $"{bytes / 1024.0:0.#} KB" : $"{bytes} bytes";
    }

    /// <summary>Reassembles a clipboard payload from the other PC (files go straight to a temp folder).</summary>
    internal sealed class IncomingClip : IDisposable
    {
        static int folderCounter;
        readonly long limit;
        MemoryStream memory;
        FileStream current;
        long currentRemaining;
        long received;
        readonly List<string> topLevel = new List<string>();

        public int Id { get; }
        public ClipKind Kind { get; }
        public long TotalBytes { get; }
        public string Folder { get; }
        public bool Failed { get; private set; }
        public IReadOnlyList<string> TopLevelPaths => topLevel;

        public static string TempRoot => Path.Combine(Path.GetTempPath(), AppInfo.FileName, "Clipboard");

        public IncomingClip(int id, ClipKind kind, long totalBytes, long limitBytes)
        {
            Id = id;
            Kind = kind;
            TotalBytes = totalBytes;
            limit = limitBytes;
            if (totalBytes > limitBytes || totalBytes < 0) { Failed = true; return; }
            if (kind == ClipKind.Files)
            {
                CleanupOldFolders();
                Folder = Path.Combine(TempRoot, DateTime.Now.ToString("yyyyMMdd-HHmmss") + "-" + Interlocked.Increment(ref folderCounter));
                Directory.CreateDirectory(Folder);
            }
            else
            {
                memory = new MemoryStream((int)Math.Min(totalBytes, 64 * 1024 * 1024));
            }
        }

        public void BeginEntry(string relative, long length, bool isDirectory)
        {
            if (Failed || Kind != ClipKind.Files) return;
            if (currentRemaining != 0) { Fail("entry started before previous file finished"); return; }
            CloseCurrent();
            var full = SafeCombine(Folder, relative);
            if (full == null) { Fail("unsafe path " + relative); return; }
            var top = full.Substring(Folder.Length + 1).Split('\\')[0];
            var topPath = Path.Combine(Folder, top);
            if (!topLevel.Contains(topPath)) topLevel.Add(topPath);

            if (isDirectory)
            {
                Directory.CreateDirectory(full);
                return;
            }
            Directory.CreateDirectory(Path.GetDirectoryName(full));
            current = new FileStream(full, FileMode.Create, FileAccess.Write, FileShare.Read, OutgoingClip.ChunkSize);
            currentRemaining = length;
            if (length == 0) CloseCurrent();
        }

        public void Append(ArraySegment<byte> data)
        {
            if (Failed) return;
            received += data.Count;
            if (received > TotalBytes || received > limit) { Fail("more data than announced"); return; }
            if (Kind != ClipKind.Files)
            {
                memory.Write(data.Array, data.Offset, data.Count);
                return;
            }
            if (current == null || data.Count > currentRemaining) { Fail("unexpected file data"); return; }
            current.Write(data.Array, data.Offset, data.Count);
            currentRemaining -= data.Count;
            if (currentRemaining == 0) CloseCurrent();
        }

        /// <summary>True if everything announced arrived.</summary>
        public bool Complete()
        {
            CloseCurrent();
            if (Failed) return false;
            if (received != TotalBytes || currentRemaining != 0) { Fail("transfer incomplete"); return false; }
            return true;
        }

        public byte[] Bytes => memory?.ToArray();

        void Fail(string why)
        {
            if (Failed) return;
            Failed = true;
            Log.Warn("Clipboard receive aborted: " + why);
            CloseCurrent();
        }

        void CloseCurrent()
        {
            try { current?.Dispose(); } catch { }
            current = null;
        }

        public void Dispose()
        {
            CloseCurrent();
            memory = null;
            if (Failed && Folder != null)
                try { Directory.Delete(Folder, true); } catch { }
        }

        /// <summary>Rejects absolute paths and ".." so the other PC can only write inside our temp folder.</summary>
        internal static string SafeCombine(string root, string relative)
        {
            if (string.IsNullOrWhiteSpace(relative)) return null;
            relative = relative.Replace('/', '\\');
            if (Path.IsPathRooted(relative) || relative.Contains(':')) return null;
            if (relative.Split('\\').Any(part => part == ".." || part == "." || part.Length == 0)) return null;
            if (relative.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return null;
            string full;
            try { full = Path.GetFullPath(Path.Combine(root, relative)); }
            catch { return null; }
            return full.StartsWith(root.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase) ? full : null;
        }

        /// <summary>Keeps only the most recent received folder (older clipboard contents are gone anyway).</summary>
        public static void CleanupOldFolders(int keep = 1)
        {
            try
            {
                if (!Directory.Exists(TempRoot)) return;
                var dirs = new DirectoryInfo(TempRoot).GetDirectories().OrderByDescending(d => d.CreationTimeUtc).Skip(keep);
                foreach (var d in dirs)
                    try { d.Delete(true); } catch { }
            }
            catch { }
        }
    }
}
