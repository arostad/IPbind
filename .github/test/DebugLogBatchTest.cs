// Proves Beta DebugLog coalesces INFO writes (SSD wear) and still flushes
// ERROR / Stop immediately. Compile this with a beta Version.cs and IPbind.cs.
using System;
using System.IO;
using System.Diagnostics;
using System.Threading;
using IPbind;

namespace IPbindTest
{
    static class DebugLogBatchTest
    {
        static int Main()
        {
            if (!BuildInfo.IsBeta)
            {
                Console.WriteLine("FAIL: DebugLogBatchTest must be compiled as beta (IsBeta is false)");
                return 1;
            }
            if (!DebugLog.Enabled)
            {
                Console.WriteLine("FAIL: DebugLog.Enabled is false in a beta build");
                return 1;
            }

            string dir = Path.Combine(Path.GetTempPath(), "ipbind-debuglog-batch-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                DebugLog.SetLogDirectoryForTest(dir);
                DebugLog.Start();
                DebugLog.Flush();
                DebugLog.ResetDiskWriteCount();

                string marker = "batch-" + Guid.NewGuid().ToString("N");
                int count = 1000;
                // Thread.Sleep(3) is ~15 ms on Windows, so 1000 sleeps would
                // take ~15 s and trip the 5 s writer timer three times.
                // Spin to spread the 1000 INFO lines over about 3 seconds.
                Stopwatch emit = Stopwatch.StartNew();
                for (int i = 0; i < count; i++)
                {
                    DebugLog.Write("INFO", marker + " " + i);
                    long targetMs = (long)(i + 1) * 3000 / count;
                    while (emit.ElapsedMilliseconds < targetMs)
                        Thread.SpinWait(80);
                }
                // Wait for the 500-line early flush to land; do not call Flush()
                // (that would be an extra write and would hide a per-line pulse bug).
                // Count writes once the 1000 INFO lines are on disk. Do not
                // require the queue to be empty: first-chance DEBUG lines from
                // the runtime may still be sitting there (under 500) and must
                // not be flushed by calling Flush() here.
                if (!WaitForInfoOnDisk(dir, marker, count, 1000))
                {
                    Console.WriteLine("FAIL: not all 1000 INFO lines reached disk (queued=" + DebugLog.QueuedCount + ", writes=" + DebugLog.DiskWriteCount + ", sizes=" + JoinSizes(DebugLog.DiskWriteSizes) + ")");
                    return 1;
                }

                int writes = DebugLog.DiskWriteCount;
                string sizes = JoinSizes(DebugLog.DiskWriteSizes);
                if (writes > 2)
                {
                    Console.WriteLine("FAIL: 1000 INFO lines over ~3s caused " + writes + " file writes (expected <= 2); sizes=" + sizes + " queued=" + DebugLog.QueuedCount);
                    return 1;
                }

                string errMarker = "error-" + Guid.NewGuid().ToString("N");
                DebugLog.Write("ERROR", errMarker);
                if (!WaitForLine(dir, errMarker, 1000))
                {
                    Console.WriteLine("FAIL: ERROR line did not hit disk within 1s");
                    return 1;
                }

                DebugLog.Stop();
                if (DebugLog.QueuedCount != 0)
                {
                    Console.WriteLine("FAIL: Stop() left QueuedCount=" + DebugLog.QueuedCount);
                    return 1;
                }
                string body = ReadAllLogs(dir);
                for (int i = 0; i < count; i++)
                {
                    if (body.IndexOf(marker + " " + i, StringComparison.Ordinal) < 0)
                    {
                        Console.WriteLine("FAIL: Stop() left INFO line " + i + " off disk");
                        return 1;
                    }
                }
                if (body.IndexOf(errMarker, StringComparison.Ordinal) < 0)
                {
                    Console.WriteLine("FAIL: Stop() left the ERROR line off disk");
                    return 1;
                }

                Console.WriteLine("OK: 1000 INFO over ~3s -> " + writes + " writes; ERROR hit disk; Stop() emptied the queue");
                return 0;
            }
            finally
            {
                try { if (Directory.Exists(dir)) Directory.Delete(dir, true); } catch { }
            }
        }

        static string JoinSizes(int[] sizes)
        {
            if (sizes == null || sizes.Length == 0) return "(none)";
            string[] parts = new string[sizes.Length];
            for (int i = 0; i < sizes.Length; i++) parts[i] = sizes[i].ToString();
            return string.Join(",", parts);
        }

        static bool WaitForInfoOnDisk(string dir, string marker, int count, int timeoutMs)
        {
            int start = Environment.TickCount;
            while (unchecked(Environment.TickCount - start) < timeoutMs)
            {
                if (CountMarkers(dir, marker) >= count) return true;
                Thread.Sleep(20);
            }
            return CountMarkers(dir, marker) >= count;
        }

        static int CountMarkers(string dir, string marker)
        {
            string body = ReadAllLogs(dir);
            int n = 0;
            int i = 0;
            while (true)
            {
                int at = body.IndexOf(marker, i, StringComparison.Ordinal);
                if (at < 0) return n;
                n++;
                i = at + marker.Length;
            }
        }

        static bool WaitForLine(string dir, string text, int timeoutMs)
        {
            int start = Environment.TickCount;
            while (unchecked(Environment.TickCount - start) < timeoutMs)
            {
                if (ReadAllLogs(dir).IndexOf(text, StringComparison.Ordinal) >= 0)
                    return true;
                Thread.Sleep(20);
            }
            return ReadAllLogs(dir).IndexOf(text, StringComparison.Ordinal) >= 0;
        }

        static string ReadAllLogs(string dir)
        {
            try
            {
                if (!Directory.Exists(dir)) return "";
                string[] files = Directory.GetFiles(dir, "ipbind-beta-*.log");
                System.Text.StringBuilder sb = new System.Text.StringBuilder();
                Array.Sort(files);
                foreach (string f in files)
                {
                    try { sb.Append(File.ReadAllText(f)); } catch { }
                }
                return sb.ToString();
            }
            catch
            {
                return "";
            }
        }
    }
}
