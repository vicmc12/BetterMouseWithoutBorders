using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace BetterMouse
{
    /// <summary>Asynchronous file log, so the input hook thread never waits on the disk.</summary>
    internal static class Log
    {
        const long MaxBytes = 2 * 1024 * 1024;
        static readonly BlockingCollection<string> Queue = new BlockingCollection<string>(4096);
        static string path;
        static Thread writer;

        public static string FilePath => path;

        public static void Init(string directory)
        {
            try
            {
                Directory.CreateDirectory(directory);
                path = Path.Combine(directory, AppInfo.FileName + ".log");
                var info = new FileInfo(path);
                if (info.Exists && info.Length > MaxBytes)
                {
                    var old = path + ".old";
                    if (File.Exists(old)) File.Delete(old);
                    File.Move(path, old);
                }
            }
            catch
            {
                path = null;
            }
            if (writer == null)
            {
                writer = new Thread(WriteLoop) { IsBackground = true, Name = "BetterMouse log", Priority = ThreadPriority.BelowNormal };
                writer.Start();
            }
        }

        public static void Info(string message) => Write("INFO ", message);
        public static void Warn(string message) => Write("WARN ", message);

        public static void Error(string message, Exception ex = null) =>
            Write("ERROR", ex == null ? message : message + ": " + ex);

        /// <summary>Waits briefly for queued lines to reach the disk (on exit).</summary>
        public static void Flush()
        {
            for (int i = 0; i < 20 && Queue.Count > 0; i++) Thread.Sleep(25);
        }

        static void Write(string level, string message)
        {
            var line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " " + level + " " + message;
            Debug.WriteLine(line);
            Queue.TryAdd(line); // drops lines rather than block if the disk is stuck
        }

        static void WriteLoop()
        {
            var sb = new StringBuilder();
            foreach (var line in Queue.GetConsumingEnumerable())
            {
                sb.Clear().AppendLine(line);
                while (Queue.TryTake(out var more)) sb.AppendLine(more);
                if (path == null) continue;
                try { File.AppendAllText(path, sb.ToString(), Encoding.UTF8); }
                catch { /* logging must never break the app */ }
            }
        }
    }
}
