// IPbind - Static IP Binder for accessing air-gapped industrial automation systems
// A genuine compiled .NET WinForms application (no script wrapper).
// Binds multiple static IPv4 addresses to a chosen LAN interface for reaching
// air-gapped equipment web GUIs across different IP ranges, with one-click DHCP revert.
// By default no gateway or DNS is set; optional Gateway support adds a gateway (with a
// high route metric) and DNS servers for one chosen address. Keep DHCP (Windows 10 1703+)
// adds the static addresses alongside the DHCP address instead of replacing it. Local network configuration is the app's purpose; update checks are the
// only intentional outbound internet traffic.
//
// DPI handling: the layout is scaled explicitly in code from the real device DPI read
// at startup, so it renders correctly at any Windows scaling (100/125/150/175%) without
// relying on WinForms auto-scaling. Version metadata is generated per-build in Version.cs.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
using System.Globalization;
using System.IO;
using Microsoft.Win32;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

[assembly: AssemblyDescription("Easily accesses air-gapped equipment across multiple IP ranges by binding static IPv4 addresses to a LAN interface, with one-click DHCP revert.")]
[assembly: AssemblyCompany("Andy Rostad")]
[assembly: AssemblyCopyright("Copyright © 2026 Andy Rostad. MIT License.")]
// AssemblyTitle / AssemblyProduct / AssemblyVersion / AssemblyFileVersion /
// AssemblyInformationalVersion and BuildInfo (Version, Channel, DisplayVersion,
// AppName, ReleaseTag) are generated fresh on every compile into Version.cs by
// Build-Exe.ps1. -Channel beta builds the separate "IPbind Beta" app.
// DisplayVersion is Version plus a trailing "b" in Beta, and every on-screen
// version in Beta must use it (title bar, About, main-window update text,
// console, prompts, installer wizard, and Apps & Features). Parsed versions,
// version.txt, file versions, tags, and updater comparisons stay the numeric
// Version (strip a trailing b if a display string is parsed). Prod display
// is unchanged.

namespace IPbind
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            DebugLog.Start();
            try
            {
                if (UpdateChecker.TryRunApplyUpdate(args)) return;
                UpdateChecker.BeginHelperCleanup(args);
                InstalledVersionSync.Run();
                // Older in-app updates only replaced IPbind.exe, so a 2.3.128
                // (or 2.3.141) install can already be running this build while
                // still using the original unins000.exe. Refresh that before
                // the main window appears. Returns true when a helper was
                // started and this process should exit.
                if (UpdateChecker.TryStartUninstallerRefresh()) return;

                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                WindowTheme.ApplyPreferredAppMode();
                Application.Run(new MainForm());
            }
            finally
            {
                DebugLog.Stop();
            }
        }
    }

    static class DebugLog
    {
        public static readonly bool Enabled = BuildInfo.IsBeta;

        const int KeepDays = 14;
        const long MaxTotalBytes = 10L * 1024L * 1024L;
        const int FlushEveryMs = 5000;
        const int FlushAtCount = 500;
        const int UiStallMs = 250;
        const int PaintWarnPerMinute = 300;
        const int FirstChanceMaxPerWindow = 20;
        const int FirstChanceWindowMs = 10000;

        static readonly ConcurrentQueue<string> queue = new ConcurrentQueue<string>();
        static readonly AutoResetEvent pulse = new AutoResetEvent(false);
        static readonly object startGate = new object();
        static readonly object diskGate = new object();
        static Thread writer;
        static volatile bool started;
        static volatile bool stopping;
        static string logDir;
        static string logDirOverride;
        static int queuedCount;
        static int diskWrites;
        static readonly System.Collections.Generic.List<int> diskWriteSizes = new System.Collections.Generic.List<int>();
        static System.Threading.Timer rotateTimer;
        static SynchronizationContext uiContext;
        static int lastUiTick;
        static int lastStallLogTick;
        static Thread watchdog;
        static System.Threading.Timer metricsTimer;
        static System.Threading.Timer paintTimer;
        static readonly List<PaintHook> paintHooks = new List<PaintHook>();
        static readonly object hookGate = new object();
        static readonly Queue<long> workingSets = new Queue<long>();
        static readonly Queue<int> gdiCounts = new Queue<int>();
        static readonly Queue<int> userCounts = new Queue<int>();
        static int firstChanceCount;
        static int firstChanceWindowStart;
        static bool firstChanceSuppressed;
        static readonly Dictionary<string, string> linkState = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        [DllImport("user32.dll")]
        static extern int GetGuiResources(IntPtr hProcess, int flags);
        [DllImport("advapi32.dll", SetLastError = true)]
        static extern bool GetTokenInformation(IntPtr token, int cls, IntPtr buf, int len, out int ret);
        [DllImport("shcore.dll")]
        static extern int GetDpiForMonitor(IntPtr monitor, int type, out uint dpiX, out uint dpiY);
        [StructLayout(LayoutKind.Sequential)]
        struct POINT { public int X, Y; }
        [DllImport("user32.dll")]
        static extern IntPtr MonitorFromPoint(POINT pt, int flags);

        public static void Start()
        {
            if (!Enabled) return;
            lock (startGate)
            {
                if (started) return;
                started = true;
            }
            try
            {
                if (!string.IsNullOrEmpty(logDirOverride))
                    logDir = logDirOverride;
                else
                    logDir = Path.Combine(
                        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                        BuildInfo.AppName, "logs");
                Directory.CreateDirectory(logDir);
                Rotate(CurrentLogPath());
                rotateTimer = new System.Threading.Timer(delegate
                {
                    try
                    {
                        if (!string.IsNullOrEmpty(logDir)) Rotate(CurrentLogPath());
                    }
                    catch { }
                }, null, 60 * 60 * 1000, 60 * 60 * 1000);
            }
            catch (Exception ex)
            {
                started = false;
                try { System.Diagnostics.Debug.WriteLine("DebugLog start failed: " + ex.Message); } catch { }
                return;
            }

            writer = new Thread(WriterLoop);
            writer.IsBackground = true;
            writer.Name = "IPbind-DebugLog";
            writer.Start();

            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += OnThreadException;
            AppDomain.CurrentDomain.UnhandledException += OnUnhandledException;
            AppDomain.CurrentDomain.FirstChanceException += OnFirstChance;

            try
            {
                NetworkChange.NetworkAddressChanged += OnNetworkAddressChanged;
                NetworkChange.NetworkAvailabilityChanged += OnNetworkAvailabilityChanged;
            }
            catch { }
            try
            {
                SystemEvents.DisplaySettingsChanged += OnDisplaySettingsChanged;
                SystemEvents.UserPreferenceChanged += OnUserPreferenceChanged;
                SystemEvents.PowerModeChanged += OnPowerModeChanged;
            }
            catch { }

            lastUiTick = Environment.TickCount;
            watchdog = new Thread(WatchdogLoop);
            watchdog.IsBackground = true;
            watchdog.Name = "IPbind-UiWatchdog";
            watchdog.Start();

            metricsTimer = new System.Threading.Timer(delegate { SampleMetrics(); }, null, 30 * 1000, 5 * 60 * 1000);
            paintTimer = new System.Threading.Timer(delegate { ReportPaints(); }, null, 60 * 1000, 60 * 1000);

            Write("INFO", "Debug log started (local only, never sent). fileDir=" + logDir);
            LogStartup();
            LogElevation();
            Flush();
        }

        public static void Stop()
        {
            if (!Enabled || !started) return;
            stopping = true;
            try
            {
                Application.ThreadException -= OnThreadException;
                AppDomain.CurrentDomain.UnhandledException -= OnUnhandledException;
                AppDomain.CurrentDomain.FirstChanceException -= OnFirstChance;
                NetworkChange.NetworkAddressChanged -= OnNetworkAddressChanged;
                NetworkChange.NetworkAvailabilityChanged -= OnNetworkAvailabilityChanged;
                SystemEvents.DisplaySettingsChanged -= OnDisplaySettingsChanged;
                SystemEvents.UserPreferenceChanged -= OnUserPreferenceChanged;
                SystemEvents.PowerModeChanged -= OnPowerModeChanged;
            }
            catch { }
            try { if (metricsTimer != null) metricsTimer.Dispose(); } catch { }
            try { if (paintTimer != null) paintTimer.Dispose(); } catch { }
            try { if (rotateTimer != null) rotateTimer.Dispose(); } catch { }
            pulse.Set();
            try { if (writer != null) writer.Join(2000); } catch { }
            FlushToDisk();
        }

        // Test hook: redirect logs away from the real Beta logs folder.
        public static void SetLogDirectoryForTest(string dir)
        {
            logDirOverride = dir;
        }

        public static int DiskWriteCount
        {
            get { return diskWrites; }
        }

        public static void ResetDiskWriteCount()
        {
            Interlocked.Exchange(ref diskWrites, 0);
            lock (diskWriteSizes) { diskWriteSizes.Clear(); }
        }

        public static int[] DiskWriteSizes
        {
            get { lock (diskWriteSizes) { return diskWriteSizes.ToArray(); } }
        }

        public static int QueuedCount
        {
            get { return queuedCount; }
        }

        public static void Write(string level, string message)
        {
            if (!Enabled || !started) return;
            if (message == null) message = "";
            string line = DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff", CultureInfo.InvariantCulture)
                + " " + level + " " + message;
            queue.Enqueue(line);
            int n = Interlocked.Increment(ref queuedCount);
            // Do not pulse the writer for every line (SSD wear). ERROR flushes
            // immediately; other levels wait for the 5 s loop or a 500-line batch.
            if (level != null && string.Equals(level, "ERROR", StringComparison.OrdinalIgnoreCase))
                FlushToDisk();
            else if (n == FlushAtCount)
                pulse.Set();
        }

        public static void Debug(string message) { Write("DEBUG", message); }
        public static void Info(string message) { Write("INFO", message); }
        public static void Warn(string message) { Write("WARN", message); }
        public static void Error(string message) { Write("ERROR", message); }

        public static void Exception(string where, System.Exception ex)
        {
            if (!Enabled || !started) return;
            if (ex == null) { Write("ERROR", where + ": (null exception)"); return; }
            Write("ERROR", where + ": " + ex.GetType().FullName + ": " + ex.Message + "\r\n" + ex.ToString());
        }

        public static void Flush()
        {
            if (!Enabled || !started) return;
            FlushToDisk();
        }

        public static IDisposable Time(string name)
        {
            return new ActionTimer(name);
        }

        public static void WatchForm(Form form)
        {
            if (!Enabled || !started || form == null) return;
            form.Load += delegate
            {
                uiContext = SynchronizationContext.Current;
                LogForm("open", form);
                HookPaints(form);
                try { form.BeginInvoke(new MethodInvoker(delegate { Heartbeat(); })); } catch { }
            };
            form.FormClosed += delegate { LogForm("close", form); };
            form.Shown += delegate { LogForm("shown", form); };
        }

        public static void Heartbeat()
        {
            lastUiTick = Environment.TickCount;
            if (uiContext == null) uiContext = SynchronizationContext.Current;
        }

        public static void LogSettingsFile(string path, string action)
        {
            if (!Enabled || !started) return;
            try
            {
                if (!File.Exists(path))
                {
                    Write("INFO", "settings " + action + " missing " + path);
                    return;
                }
                string body = File.ReadAllText(path);
                if (body.Length > 8000) body = body.Substring(0, 8000) + "\r\n...[truncated]";
                Write("INFO", "settings " + action + " " + path + " (" + body.Length + " chars)\r\n" + body);
            }
            catch (Exception ex)
            {
                Write("WARN", "settings " + action + " " + path + " failed: " + ex.Message);
            }
        }

        public static void LogProc(string file, string args, int exit, string output, string stderr, long ms)
        {
            if (!Enabled || !started) return;
            string err = string.IsNullOrEmpty(stderr) ? "" : " stderr=" + OneLine(stderr);
            string outp = string.IsNullOrEmpty(output) ? "" : " stdout=" + OneLine(output);
            Write("DEBUG", "proc " + file + " " + args + " exit=" + exit + " " + ms + "ms" + err + outp);
        }

        public static void LogHttp(string url, int status, string detail)
        {
            if (!Enabled || !started) return;
            Write("INFO", "http GET " + url + " status=" + status + (string.IsNullOrEmpty(detail) ? "" : " " + detail));
        }

        static string OneLine(string s)
        {
            if (s == null) return "";
            s = s.Replace("\r", " ").Replace("\n", " ").Trim();
            if (s.Length > 2000) s = s.Substring(0, 2000) + "...";
            return s;
        }

        static int MonitorDpi(Screen screen)
        {
            try
            {
                POINT pt = new POINT();
                pt.X = screen.Bounds.X + 1;
                pt.Y = screen.Bounds.Y + 1;
                IntPtr mon = MonitorFromPoint(pt, 2);
                uint x, y;
                if (mon != IntPtr.Zero && GetDpiForMonitor(mon, 0, out x, out y) == 0 && x > 0)
                    return (int)x;
            }
            catch { }
            return DpiWindow.SystemDpi();
        }

        static void LogStartup()
        {
            try
            {
                OperatingSystem os = Environment.OSVersion;
                string osBuild = "";
                try
                {
                    object v = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "CurrentBuildNumber", "");
                    object ubr = Registry.GetValue(@"HKEY_LOCAL_MACHINE\SOFTWARE\Microsoft\Windows NT\CurrentVersion", "UBR", null);
                    osBuild = (v ?? "").ToString();
                    if (ubr != null) osBuild += "." + ubr.ToString();
                }
                catch { }
                Write("INFO", "startup version=" + BuildInfo.DisplayVersion
                    + " channel=" + BuildInfo.Channel
                    + " os=" + os.VersionString
                    + " build=" + osBuild
                    + " bitness=" + (Environment.Is64BitProcess ? "64" : "32")
                    + " clr=" + Environment.Version);
                foreach (Screen screen in Screen.AllScreens)
                {
                    int dpi = MonitorDpi(screen);
                    Write("INFO", "monitor " + (screen.Primary ? "primary" : "secondary")
                        + " bounds=" + screen.Bounds
                        + " work=" + screen.WorkingArea
                        + " dpi=" + dpi
                        + " scale=" + (dpi / 96f).ToString("0.##", CultureInfo.InvariantCulture));
                }
            }
            catch (Exception ex)
            {
                Write("WARN", "startup probe failed: " + ex.Message);
            }
        }

        static void LogElevation()
        {
            try
            {
                WindowsIdentity id = WindowsIdentity.GetCurrent();
                WindowsPrincipal principal = new WindowsPrincipal(id);
                bool admin = principal.IsInRole(WindowsBuiltInRole.Administrator);
                int elevType = 0;
                try
                {
                    int ret;
                    IntPtr buf = Marshal.AllocHGlobal(4);
                    try
                    {
                        if (GetTokenInformation(id.Token, 18, buf, 4, out ret) && ret >= 4)
                            elevType = Marshal.ReadInt32(buf);
                    }
                    finally { Marshal.FreeHGlobal(buf); }
                }
                catch { }
                string typeName = elevType == 1 ? "Default" : elevType == 2 ? "Full" : elevType == 3 ? "Limited" : ("unknown-" + elevType);
                Write("INFO", "elevation isAdmin=" + admin + " tokenType=" + typeName + " user=" + id.Name);
            }
            catch (Exception ex)
            {
                Write("WARN", "elevation probe failed: " + ex.Message);
            }
        }

        static void LogForm(string action, Form form)
        {
            try
            {
                string title = form.Text ?? form.GetType().Name;
                Rectangle b = form.Bounds;
                Screen screen = null;
                try { screen = Screen.FromControl(form); } catch { }
                int dpi = DpiWindow.SystemDpi();
                try { if (form.IsHandleCreated) { int w = DpiWindow.WindowDpi(form.Handle); if (w > 0) dpi = w; } } catch { }
                Write("INFO", "form " + action + " type=" + form.GetType().Name
                    + " title='" + title + "'"
                    + " bounds=" + b
                    + " monitor=" + (screen == null ? "?" : screen.DeviceName + " " + screen.Bounds)
                    + " dpi=" + dpi
                    + " scale=" + (dpi / 96f).ToString("0.##", CultureInfo.InvariantCulture));
            }
            catch (Exception ex)
            {
                Write("WARN", "form " + action + " log failed: " + ex.Message);
            }
        }

        static string CurrentLogPath()
        {
            string name = "ipbind-beta-" + DateTime.Now.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture) + ".log";
            return Path.Combine(logDir, name);
        }

        static void WriterLoop()
        {
            while (true)
            {
                pulse.WaitOne(FlushEveryMs);
                FlushToDisk();
                while (queuedCount >= FlushAtCount)
                    FlushToDisk();
                if (stopping && queue.IsEmpty) break;
            }
        }

        static void FlushToDisk()
        {
            if (string.IsNullOrEmpty(logDir)) return;
            lock (diskGate)
            {
                string path = CurrentLogPath();
                StringBuilder batch = new StringBuilder();
                string line;
                int taken = 0;
                while (queue.TryDequeue(out line))
                {
                    batch.Append(line).Append("\r\n");
                    taken++;
                }
                if (taken > 0) Interlocked.Add(ref queuedCount, -taken);
                if (batch.Length == 0) return;
                try
                {
                    byte[] bytes = Encoding.UTF8.GetBytes(batch.ToString());
                    using (FileStream fs = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.Read))
                    {
                        fs.Write(bytes, 0, bytes.Length);
                        fs.Flush();
                    }
                    Interlocked.Increment(ref diskWrites);
                    lock (diskWriteSizes) { diskWriteSizes.Add(taken); }
                }
                catch { }
            }
        }

        static void Rotate(string currentPath)
        {
            try
            {
                DateTime cutoff = DateTime.Now.Date.AddDays(-KeepDays);
                List<FileInfo> files = new List<FileInfo>();
                foreach (string f in Directory.GetFiles(logDir, "ipbind-beta-*.log"))
                    files.Add(new FileInfo(f));
                files.Sort(delegate(FileInfo a, FileInfo b) { return a.LastWriteTime.CompareTo(b.LastWriteTime); });
                long total = 0;
                foreach (FileInfo fi in files) total += fi.Length;
                foreach (FileInfo fi in files)
                {
                    bool old = fi.LastWriteTime < cutoff;
                    bool over = total > MaxTotalBytes && !string.Equals(fi.FullName, currentPath, StringComparison.OrdinalIgnoreCase);
                    if (!old && !over) continue;
                    try
                    {
                        total -= fi.Length;
                        fi.Delete();
                    }
                    catch { }
                }
            }
            catch { }
        }

        static void WatchdogLoop()
        {
            while (!stopping)
            {
                Thread.Sleep(50);
                HeartbeatKick();
                if (uiContext == null) continue;
                int idle = Environment.TickCount - lastUiTick;
                if (idle > UiStallMs && lastUiTick != 0)
                {
                    int now = Environment.TickCount;
                    if (now - lastStallLogTick > 2000)
                    {
                        lastStallLogTick = now;
                        Write("WARN", "UI thread stall " + idle + "ms (threshold " + UiStallMs + "ms)");
                    }
                }
            }
        }

        static void HeartbeatKick()
        {
            SynchronizationContext ctx = uiContext;
            if (ctx == null) return;
            try { ctx.Post(delegate { Heartbeat(); }, null); } catch { }
        }

        static void SampleMetrics()
        {
            if (stopping) return;
            try
            {
                Process proc = Process.GetCurrentProcess();
                proc.Refresh();
                long ws = proc.WorkingSet64;
                int gdi = 0, user = 0;
                try { gdi = GetGuiResources(proc.Handle, 0); } catch { }
                try { user = GetGuiResources(proc.Handle, 1); } catch { }
                Write("DEBUG", "metrics workingSet=" + ws + " gdi=" + gdi + " user=" + user
                    + " handles=" + proc.HandleCount + " threads=" + proc.Threads.Count);
                lock (hookGate)
                {
                    workingSets.Enqueue(ws);
                    gdiCounts.Enqueue(gdi);
                    userCounts.Enqueue(user);
                    while (workingSets.Count > 3) { workingSets.Dequeue(); gdiCounts.Dequeue(); userCounts.Dequeue(); }
                    if (workingSets.Count == 3 && IsRising(workingSets) && IsRisingInt(gdiCounts) && IsRisingInt(userCounts))
                        Write("WARN", "possible leak: working set, GDI, and USER handles all kept growing over the last 15 minutes (ws=" + ws + " gdi=" + gdi + " user=" + user + ")");
                }
            }
            catch (Exception ex)
            {
                Write("DEBUG", "metrics failed: " + ex.Message);
            }
        }

        static bool IsRising(Queue<long> q)
        {
            long prev = -1;
            foreach (long v in q)
            {
                if (prev >= 0 && v <= prev) return false;
                prev = v;
            }
            return true;
        }

        static bool IsRisingInt(Queue<int> q)
        {
            int prev = -1;
            foreach (int v in q)
            {
                if (prev >= 0 && v <= prev) return false;
                prev = v;
            }
            return true;
        }

        static void ReportPaints()
        {
            if (stopping) return;
            List<PaintHook> hooks;
            lock (hookGate) hooks = new List<PaintHook>(paintHooks);
            StringBuilder sb = new StringBuilder();
            foreach (PaintHook hook in hooks)
            {
                int n = Interlocked.Exchange(ref hook.Counter.Value, 0);
                if (n <= 0) continue;
                sb.Append(hook.Name).Append("=").Append(n).Append(" ");
                if (n >= PaintWarnPerMinute)
                    Write("WARN", "paint loop? " + hook.Name + " painted " + n + " times in the last minute (threshold " + PaintWarnPerMinute + ")");
            }
            if (sb.Length > 0) Write("DEBUG", "paints/min " + sb.ToString().Trim());
        }

        public static void HookPaints(Control root)
        {
            if (!Enabled || !started || root == null) return;
            HookOne(root);
            foreach (Control child in root.Controls) HookPaints(child);
        }

        static void HookOne(Control c)
        {
            try
            {
                if (c.IsHandleCreated) AttachHook(c);
                c.HandleCreated += delegate { AttachHook(c); };
            }
            catch { }
        }

        static void AttachHook(Control c)
        {
            try
            {
                if (!c.IsHandleCreated) return;
                PaintHook hook = new PaintHook();
                hook.Name = c.GetType().Name + (string.IsNullOrEmpty(c.Name) ? (string.IsNullOrEmpty(c.Text) ? "" : (":" + TrimName(c.Text))) : (":" + c.Name));
                hook.AssignHandle(c.Handle);
                lock (hookGate) paintHooks.Add(hook);
            }
            catch { }
        }

        static string TrimName(string s)
        {
            s = (s ?? "").Replace("\r", " ").Replace("\n", " ").Trim();
            if (s.Length > 40) s = s.Substring(0, 40);
            return s;
        }

        sealed class PaintCount
        {
            public int Value;
        }

        sealed class PaintHook : NativeWindow
        {
            public string Name = "";
            public readonly PaintCount Counter = new PaintCount();
            public int Count { get { return Counter.Value; } set { Counter.Value = value; } }
            protected override void WndProc(ref Message m)
            {
                if (m.Msg == 0x000F) Interlocked.Increment(ref Counter.Value);
                base.WndProc(ref m);
            }
        }

        sealed class ActionTimer : IDisposable
        {
            readonly string name;
            readonly Stopwatch watch;
            public ActionTimer(string name)
            {
                this.name = name;
                watch = Stopwatch.StartNew();
            }
            public void Dispose()
            {
                watch.Stop();
                Write("DEBUG", "ui action '" + name + "' " + watch.ElapsedMilliseconds + "ms");
                Heartbeat();
            }
        }

        static void OnThreadException(object sender, ThreadExceptionEventArgs e)
        {
            Exception("Application.ThreadException", e.Exception);
        }

        static void OnUnhandledException(object sender, UnhandledExceptionEventArgs e)
        {
            System.Exception ex = e.ExceptionObject as System.Exception;
            if (ex == null) ex = new System.Exception(Convert.ToString(e.ExceptionObject));
            Exception("AppDomain.UnhandledException (term=" + e.IsTerminating + ")", ex);
        }

        static void OnFirstChance(object sender, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs e)
        {
            if (!Enabled || !started || e == null || e.Exception == null) return;
            int now = Environment.TickCount;
            if (now - firstChanceWindowStart > FirstChanceWindowMs)
            {
                firstChanceWindowStart = now;
                firstChanceCount = 0;
                firstChanceSuppressed = false;
            }
            firstChanceCount++;
            if (firstChanceCount > FirstChanceMaxPerWindow)
            {
                if (!firstChanceSuppressed)
                {
                    firstChanceSuppressed = true;
                    Write("DEBUG", "first-chance exceptions rate-limited (" + FirstChanceMaxPerWindow + " / " + (FirstChanceWindowMs / 1000) + "s)");
                }
                return;
            }
            System.Exception ex = e.Exception;
            string loc = "?";
            if (ex.TargetSite != null)
                loc = (ex.TargetSite.DeclaringType != null ? ex.TargetSite.DeclaringType.FullName + "." : "") + ex.TargetSite.Name;
            Write("DEBUG", "first-chance " + ex.GetType().FullName + " at " + loc + ": " + ex.Message);
        }

        static void OnNetworkAddressChanged(object sender, EventArgs e)
        {
            Write("INFO", "NetworkChange.NetworkAddressChanged");
            LogLinkStates();
        }

        static void OnNetworkAvailabilityChanged(object sender, NetworkAvailabilityEventArgs e)
        {
            Write("INFO", "NetworkChange.NetworkAvailabilityChanged available=" + e.IsAvailable);
        }

        static void LogLinkStates()
        {
            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    string st = ni.OperationalStatus.ToString();
                    string prev;
                    lock (linkState)
                    {
                        if (!linkState.TryGetValue(ni.Id, out prev)) prev = null;
                        linkState[ni.Id] = st;
                    }
                    if (prev != st)
                        Write("INFO", "link " + (st == "Up" ? "up" : (st == "Down" ? "down" : st))
                            + " name='" + ni.Name + "' id=" + ni.Id + " type=" + ni.NetworkInterfaceType
                            + (prev == null ? " (first seen)" : (" was " + prev)));
                }
            }
            catch (Exception ex)
            {
                Write("DEBUG", "link probe failed: " + ex.Message);
            }
        }

        static void OnDisplaySettingsChanged(object sender, EventArgs e)
        {
            Write("INFO", "SystemEvents.DisplaySettingsChanged");
            LogStartup();
        }

        static void OnUserPreferenceChanged(object sender, UserPreferenceChangedEventArgs e)
        {
            if (e.Category == UserPreferenceCategory.VisualStyle
                || e.Category == UserPreferenceCategory.Color
                || e.Category == UserPreferenceCategory.General)
            {
                bool highContrast = false;
                try { highContrast = SystemInformation.HighContrast; } catch { }
                Write("INFO", "SystemEvents.UserPreferenceChanged category=" + e.Category + " highContrast=" + highContrast);
            }
        }

        static void OnPowerModeChanged(object sender, PowerModeChangedEventArgs e)
        {
            Write("INFO", "SystemEvents.PowerModeChanged " + e.Mode);
        }
    }

    // Display-only Beta suffix. File versions, version.txt, tags, and compares
    // stay numeric; strip a trailing "b" before parsing.
    internal static class VersionText
    {
        public static string Numeric(string version)
        {
            if (string.IsNullOrEmpty(version)) return version;
            char last = version[version.Length - 1];
            if (last == 'b' || last == 'B')
            {
                string trimmed = version.Substring(0, version.Length - 1);
                System.Version parsed;
                if (System.Version.TryParse(trimmed, out parsed))
                    return trimmed;
            }
            return version;
        }

        public static string ForDisplay(string version)
        {
            if (string.IsNullOrEmpty(version)) return version;
            string numeric = Numeric(version);
            if (BuildInfo.IsBeta) return numeric + "b";
            return numeric;
        }

        public static bool TryParse(string version, out System.Version result)
        {
            return System.Version.TryParse(Numeric(version), out result);
        }
    }

    // The in-app updater swaps only the exe, so the installer's Apps & Features entry
    // (which winget reads) would keep the old version. On launch, an installed copy
    // raises DisplayVersion to the running display version (Beta includes the trailing
    // "b"). Portable copies, other users' installs and any error are skipped silently.
    // Never lowers the numeric version. A numeric 2.3.139 entry is rewritten as
    // 2.3.139b on Beta so Settings > Installed apps matches the title bar.
    static class InstalledVersionSync
    {
        const string UninstallKeyFormat =
            @"Software\Microsoft\Windows\CurrentVersion\Uninstall\{0}_is1";
        const string ProdAppId = "{A42A329A-EDE5-4D63-B2C7-92947FE79E14}";
        const string BetaAppId = "{3E0BECC0-9CDB-47D6-BFFB-5A6B0DEEFC27}";

        public static void Run()
        {
            try
            {
                string dir = Path.GetDirectoryName(Path.GetFullPath(Application.ExecutablePath));
                if (string.IsNullOrEmpty(dir) || !File.Exists(Path.Combine(dir, "unins000.exe")))
                    return;
                string keyPath = string.Format(
                    UninstallKeyFormat, BuildInfo.IsBeta ? BetaAppId : ProdAppId);
                using (Microsoft.Win32.RegistryKey key =
                    Microsoft.Win32.Registry.CurrentUser.OpenSubKey(keyPath, true))
                {
                    if (key == null) return;
                    string location = key.GetValue("InstallLocation") as string;
                    if (string.IsNullOrEmpty(location)
                        || !string.Equals(
                            Path.GetFullPath(location).TrimEnd('\\'), dir.TrimEnd('\\'),
                            StringComparison.OrdinalIgnoreCase))
                        return;
                    string current = key.GetValue("DisplayVersion") as string;
                    string writeValue;
                    if (!ShouldWrite(current, BuildInfo.Version, BuildInfo.DisplayVersion, out writeValue))
                        return;
                    key.SetValue("DisplayVersion", writeValue,
                        Microsoft.Win32.RegistryValueKind.String);
                }
            }
            catch { }
        }

        // currentDisplay is the Apps & Features value (may be numeric or end in b).
        // runningNumeric is BuildInfo.Version; runningDisplay is BuildInfo.DisplayVersion.
        public static bool TryReadUninstallString(string installDirectory, out string uninstallString)
        {
            uninstallString = "";
            if (string.IsNullOrEmpty(installDirectory)) return false;
            string dir;
            try { dir = Path.GetFullPath(installDirectory).TrimEnd('\\'); }
            catch { return false; }
            string keyPath = string.Format(
                UninstallKeyFormat, BuildInfo.IsBeta ? BetaAppId : ProdAppId);
            try
            {
                using (Microsoft.Win32.RegistryKey key =
                    Microsoft.Win32.Registry.CurrentUser.OpenSubKey(keyPath, false))
                {
                    if (key == null) return false;
                    string location = key.GetValue("InstallLocation") as string;
                    if (string.IsNullOrEmpty(location)
                        || !string.Equals(
                            Path.GetFullPath(location).TrimEnd('\\'), dir,
                            StringComparison.OrdinalIgnoreCase))
                        return false;
                    uninstallString = key.GetValue("UninstallString") as string ?? "";
                    return true;
                }
            }
            catch
            {
                return false;
            }
        }

        public static bool ShouldWrite(string currentDisplay, string runningNumeric, string runningDisplay, out string writeValue)
        {
            writeValue = runningDisplay;
            if (string.Equals(currentDisplay, runningDisplay, StringComparison.Ordinal))
                return false;
            System.Version running, listed;
            if (!VersionText.TryParse(runningNumeric, out running))
                return false;
            if (!string.IsNullOrEmpty(currentDisplay)
                && VersionText.TryParse(currentDisplay, out listed)
                && listed > running)
                return false;
            return true;
        }
    }

    static class PrivateFile
    {
        static FileSecurity CurrentUserSecurity(FileSystemRights rights)
        {
            SecurityIdentifier user = WindowsIdentity.GetCurrent().User;
            if (user == null)
                throw new InvalidOperationException("Could not identify the current Windows user.");

            FileSecurity security = new FileSecurity();
            security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new FileSystemAccessRule(
                user, rights, InheritanceFlags.None, PropagationFlags.None,
                AccessControlType.Allow));
            return security;
        }

        public static FileStream Create(string path, FileSystemRights rights)
        {
            return new FileStream(
                path, FileMode.CreateNew, rights, FileShare.None, 4096,
                FileOptions.None, CurrentUserSecurity(rights));
        }

        public static void WriteAllText(string path, string contents)
        {
            try { File.Delete(path); } catch { }
            FileSystemRights rights =
                FileSystemRights.Read | FileSystemRights.Write | FileSystemRights.Modify;
            using (FileStream stream = Create(path, rights))
            using (StreamWriter writer = new StreamWriter(stream, new UTF8Encoding(false)))
                writer.Write(contents);
        }

        public static void CopyExecutable(string source, string destination)
        {
            FileSystemRights rights = FileSystemRights.Read | FileSystemRights.Write |
                FileSystemRights.Modify | FileSystemRights.ExecuteFile;
            using (FileStream input = new FileStream(
                source, FileMode.Open, FileAccess.Read, FileShare.Read))
            using (FileStream output = Create(destination, rights))
                input.CopyTo(output);
        }
    }

    class Adapter
    {
        public string Name;         // Windows connection name (real ID); never tidied
        public string Description;  // hardware description as Windows reports it
        public string Status;
    }

    // Display-only tidy for the interface dropdown. Matching, the remembered
    // adapter, and every netsh/WMI call keep using Adapter.Name and the raw
    // description; this string is never fed back into those paths.
    public static class AdapterDisplay
    {
        public static string CleanDescription(string description)
        {
            if (string.IsNullOrEmpty(description)) return "";
            string s = description;
            // (R) / (TM), with or without spaces, any case; also the symbol forms.
            s = Regex.Replace(s, @"\(\s*R\s*\)", " ", RegexOptions.IgnoreCase);
            s = Regex.Replace(s, @"\(\s*TM\s*\)", " ", RegexOptions.IgnoreCase);
            s = s.Replace("®", " ").Replace("™", " ");
            // Intel-style generation tags: "(16)", "(7)", "( 16 )". Parentheses
            // that are part of a real model name (letters or punctuation) stay.
            s = Regex.Replace(s, @"\(\s*\d+\s*\)", " ");
            s = Regex.Replace(s, @"\s+", " ").Trim();
            return s;
        }

        public static string Format(string name, string description, string status)
        {
            string n = name ?? "";
            string d = CleanDescription(description);
            string st = status ?? "";
            string text = d.Length > 0
                ? n + " | " + d + " [" + st + "]"
                : n + " [" + st + "]";
            return Regex.Replace(text, @"\s+", " ").Trim();
        }
    }

    sealed class UpdateInfo
    {
        public string RemoteVersion;
        public bool IsNewer;
        public string Error;
    }

    static class UpdateChecker
    {
        // Prod reads the Worker (usage count) and falls back to the latest release.
        // Beta builds never touch the Worker and read only the beta pre-release, so
        // a beta copy only ever updates from beta and a prod copy only from prod.
        const string WorkerVersionUrl =
            "https://ipbind-update-pings.dataspike.workers.dev/version.txt";
        const string ReleaseBaseUrl =
            "https://github.com/arostad/IPbind/releases/download/" + BuildInfo.ReleaseTag + "/";
        const string VersionUrl = ReleaseBaseUrl + "version.txt";
        const string ExeUrl = ReleaseBaseUrl + "IPbind.exe";
        const string ChecksumUrl = ReleaseBaseUrl + "IPbind.exe.sha256";
        const string ExeFileName = "IPbind.exe";
        const long MinExeBytes = 20L * 1024L;
        const long MaxExeBytes = 500L * 1024L * 1024L;
        const int MaxRedirects = 5;

        static readonly HashSet<string> TrustedDownloadHosts =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "ipbind-update-pings.dataspike.workers.dev",
                "github.com",
                "objects.githubusercontent.com",
                "release-assets.githubusercontent.com"
            };

        static readonly HttpClient Http = CreateClient();
        // sha256sum format: "<hash>  <filename>". Filename is IPbind.exe for
        // in-app exe updates and IPbind-Installer-v*.exe for installer updates.
        static readonly Regex ChecksumPattern = new Regex(
            @"\A(?<hash>[0-9a-fA-F]{64})  \S+[ \t]*(?:\r?\n)?\z",
            RegexOptions.CultureInvariant | RegexOptions.Compiled);

        public static bool IsInstallerManaged(string installDirectory)
        {
            return !string.IsNullOrEmpty(installDirectory)
                && File.Exists(Path.Combine(installDirectory, "unins000.exe"));
        }

        public static string InstallerFileName(string version)
        {
            return InstallerFileName(version, BuildInfo.IsBeta);
        }

        public static string InstallerFileName(string version, bool isBeta)
        {
            Version parsed;
            if (string.IsNullOrEmpty(version) || !Version.TryParse(version, out parsed))
                throw new ArgumentException("version");
            if (isBeta)
                return "IPbind-Installer-v" + version + "-beta.exe";
            return "IPbind-Installer-v" + version + ".exe";
        }

        public static string InstallerUrl(string version)
        {
            string fileName = InstallerFileName(version);
            // Prod uses the permanent v<version> release so a first-launch
            // uninstaller refresh for this build still works after a later
            // Latest publish. Beta keeps using the moving beta tag.
            if (BuildInfo.IsBeta)
                return ReleaseBaseUrl + fileName;
            return "https://github.com/arostad/IPbind/releases/download/v"
                + version + "/" + fileName;
        }

        public static bool TryParseChecksum(string text, out string hash)
        {
            hash = "";
            if (string.IsNullOrEmpty(text)) return false;
            Match match = ChecksumPattern.Match(text);
            if (!match.Success) return false;
            hash = match.Groups["hash"].Value.ToUpperInvariant();
            return true;
        }

        public static bool UninstallStringIsCurrent(string uninstallString)
        {
            return !string.IsNullOrEmpty(uninstallString)
                && uninstallString.IndexOf(
                    "/IPBINDPROMPT", StringComparison.OrdinalIgnoreCase) >= 0;
        }

        public static bool UninstallerNeedsRefresh(string installDirectory)
        {
            if (!IsInstallerManaged(installDirectory)) return false;
            string uninstallString;
            if (!InstalledVersionSync.TryReadUninstallString(
                    installDirectory, out uninstallString))
                return true;
            return !UninstallStringIsCurrent(uninstallString);
        }

        static HttpClient CreateClient()
        {
            EnableModernTls();

            IWebProxy systemProxy = WebRequest.GetSystemWebProxy();
            systemProxy.Credentials = CredentialCache.DefaultCredentials;
            HttpClientHandler handler = new HttpClientHandler();
            handler.AllowAutoRedirect = false;
            handler.UseProxy = true;
            handler.Proxy = systemProxy;

            HttpClient client = new HttpClient(handler);
            client.Timeout = TimeSpan.FromMinutes(15);
            client.DefaultRequestHeaders.UserAgent.ParseAdd("IPbind");
            client.DefaultRequestHeaders.CacheControl = new CacheControlHeaderValue
            {
                NoCache = true,
                NoStore = true
            };
            client.DefaultRequestHeaders.Pragma.ParseAdd("no-cache");
            return client;
        }

        static void EnableModernTls()
        {
            // Numeric casts keep this source buildable on Framework reference assemblies
            // that predate the enum names. TLS 1.2 = 3072; TLS 1.3 = 12288.
            SecurityProtocolType current = ServicePointManager.SecurityProtocol;
            SecurityProtocolType tls12 = (SecurityProtocolType)3072;
            SecurityProtocolType tls13 = (SecurityProtocolType)12288;
            try
            {
                ServicePointManager.SecurityProtocol = current | tls12 | tls13;
            }
            catch (NotSupportedException)
            {
                // Older Framework/Windows combinations reject the TLS 1.3 value.
                ServicePointManager.SecurityProtocol = current | tls12;
            }
        }

        static string MostSpecificMessage(Exception exception)
        {
            string message = "";
            Exception current = exception;
            for (int depth = 0; current != null && depth < 12; depth++)
            {
                if (!string.IsNullOrWhiteSpace(current.Message))
                    message = current.Message.Trim();
                current = current.InnerException;
            }
            return message;
        }

        static string DescribeWebFailure(WebException exception)
        {
            HttpWebResponse response = exception.Response as HttpWebResponse;
            if (response != null)
            {
                return "HTTP " + (int)response.StatusCode + " " +
                    response.StatusDescription;
            }

            switch (exception.Status)
            {
                case WebExceptionStatus.TrustFailure:
                    return "TLS certificate trust failure";
                case WebExceptionStatus.SecureChannelFailure:
                    return "TLS secure-channel failure";
                case WebExceptionStatus.ConnectFailure:
                    return "connection failed or was refused";
                case WebExceptionStatus.NameResolutionFailure:
                    return "DNS name resolution failed";
                case WebExceptionStatus.ProxyNameResolutionFailure:
                    return "proxy name resolution failed";
                case WebExceptionStatus.Timeout:
                    return "request timed out";
                default:
                    return exception.Status.ToString();
            }
        }

        static string DescribeError(Exception exception, string prefix)
        {
            if (exception == null) return prefix + ".";

            WebException web = null;
            System.Net.Sockets.SocketException socket = null;
            Exception current = exception;
            for (int depth = 0; current != null && depth < 12; depth++)
            {
                if (web == null) web = current as WebException;
                if (socket == null)
                    socket = current as System.Net.Sockets.SocketException;
                current = current.InnerException;
            }

            string detail = MostSpecificMessage(exception);
            if (web != null)
            {
                string summary = prefix + " (" + DescribeWebFailure(web) + ")";
                return string.IsNullOrEmpty(detail) ? summary + "." : summary + ": " + detail;
            }
            if (socket != null)
            {
                string socketFailure =
                    socket.SocketErrorCode == System.Net.Sockets.SocketError.ConnectionRefused
                    ? "connection refused"
                    : "socket " + socket.SocketErrorCode;
                string summary = prefix + " (" + socketFailure + ")";
                return string.IsNullOrEmpty(detail) ? summary + "." : summary + ": " + detail;
            }

            return string.IsNullOrEmpty(detail) ? prefix + "." : prefix + ": " + detail;
        }

        public static string DescribeError(Exception exception)
        {
            return DescribeError(exception, "Update failed");
        }

        static string FreshUrl(string url)
        {
            string separator = url.IndexOf('?') >= 0 ? "&" : "?";
            long unixMilliseconds =
                (long)(DateTime.UtcNow - new DateTime(1970, 1, 1)).TotalMilliseconds;
            return url + separator + "t=" + unixMilliseconds;
        }

        static HttpResponseMessage GetTrusted(string url)
        {
            Uri current = new Uri(FreshUrl(url), UriKind.Absolute);
            for (int redirect = 0; ; redirect++)
            {
                EnsureTrustedUri(current);
                HttpResponseMessage response = Http.GetAsync(
                    current, HttpCompletionOption.ResponseHeadersRead).GetAwaiter().GetResult();
                if (!IsRedirect(response.StatusCode)) return response;

                if (redirect >= MaxRedirects)
                {
                    response.Dispose();
                    throw new HttpRequestException("The download used too many redirects.");
                }

                Uri location = response.Headers.Location;
                response.Dispose();
                if (location == null)
                    throw new HttpRequestException("The download redirect had no destination.");
                current = location.IsAbsoluteUri ? location : new Uri(current, location);
            }
        }

        static void EnsureTrustedUri(Uri uri)
        {
            if (!string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                || !TrustedDownloadHosts.Contains(uri.IdnHost)
                || !string.IsNullOrEmpty(uri.UserInfo))
            {
                throw new HttpRequestException(
                    "The download was redirected to an untrusted location.");
            }
        }

        static bool IsRedirect(HttpStatusCode status)
        {
            int code = (int)status;
            return code == 301 || code == 302 || code == 303 || code == 307 || code == 308;
        }

        static string ReadChecksum()
        {
            return ReadChecksum(ChecksumUrl);
        }

        static string ReadChecksum(string url)
        {
            using (HttpResponseMessage response = GetTrusted(url))
            {
                response.EnsureSuccessStatusCode();
                using (Stream input = response.Content.ReadAsStreamAsync().GetAwaiter().GetResult())
                using (StreamReader reader = new StreamReader(
                    input, Encoding.ASCII, false))
                {
                    char[] buffer = new char[1025];
                    int count = reader.ReadBlock(buffer, 0, buffer.Length);
                    if (count > 1024)
                        throw new InvalidOperationException(
                            "The update checksum was not readable.");

                    string hash;
                    if (!TryParseChecksum(new string(buffer, 0, count), out hash))
                        throw new InvalidOperationException(
                            "The update checksum was not readable.");
                    return hash;
                }
            }
        }

        static void DownloadExe(string destination)
        {
            DownloadTrustedFile(ExeUrl, destination);
        }

        static void DownloadTrustedFile(string url, string destination)
        {
            using (HttpResponseMessage response = GetTrusted(url))
            {
                response.EnsureSuccessStatusCode();
                long? contentLength = response.Content.Headers.ContentLength;
                if (contentLength.HasValue && contentLength.Value > MaxExeBytes)
                    throw new InvalidOperationException(
                        "The update download was unexpectedly large.");

                using (Stream input = response.Content.ReadAsStreamAsync().GetAwaiter().GetResult())
                using (FileStream output = new FileStream(
                    destination, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                {
                    byte[] buffer = new byte[81920];
                    long total = 0;
                    long lastLogged = 0;
                    int read;
                    while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        total += read;
                        if (total > MaxExeBytes)
                            throw new InvalidOperationException(
                                "The update download was unexpectedly large.");
                        output.Write(buffer, 0, read);
                        if (total - lastLogged >= 256 * 1024)
                        {
                            lastLogged = total;
                            DebugLog.Debug("updater download progress " + total + " bytes");
                        }
                    }
                    DebugLog.Info("updater download finished bytes=" + total);
                }
            }
        }

        static string Sha256(string path)
        {
            using (Stream input = File.OpenRead(path))
            using (SHA256 sha = SHA256.Create())
            {
                byte[] bytes = sha.ComputeHash(input);
                StringBuilder text = new StringBuilder(bytes.Length * 2);
                foreach (byte b in bytes) text.Append(b.ToString("X2"));
                return text.ToString();
            }
        }

        static string LocalUpdateDirectory()
        {
            return Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                BuildInfo.AppName);
        }

        static bool LooksLikeSingleFileExtractPath(string path)
        {
            string full = Path.GetFullPath(path);
            string temp = Path.GetFullPath(Path.GetTempPath()).TrimEnd(
                Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (!full.StartsWith(
                    temp + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && !string.Equals(full, temp, StringComparison.OrdinalIgnoreCase))
                return false;

            return full.IndexOf(
                Path.DirectorySeparatorChar + ".net" + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase) >= 0;
        }

        static string ResolveInstalledExe()
        {
            string processPath = Application.ExecutablePath;
            if (!string.IsNullOrEmpty(processPath) && !LooksLikeSingleFileExtractPath(processPath))
                return Path.GetFullPath(processPath);

            return Path.GetFullPath(Path.Combine(LocalUpdateDirectory(), ExeFileName));
        }

        static string ChooseStagingDirectory(string installDirectory)
        {
            string probe = Path.Combine(
                installDirectory, ".IPbind-write-" + Guid.NewGuid().ToString("N") + ".tmp");
            try
            {
                Directory.CreateDirectory(installDirectory);
                using (new FileStream(
                    probe, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
                File.Delete(probe);
                return installDirectory;
            }
            catch
            {
                try { File.Delete(probe); } catch { }
                string fallback = LocalUpdateDirectory();
                Directory.CreateDirectory(fallback);
                return fallback;
            }
        }

        static string DownloadVerifiedExe(string installDirectory, out string expectedHash)
        {
            expectedHash = ReadChecksum();
            string stagingDirectory = ChooseStagingDirectory(installDirectory);
            for (int attempt = 0; attempt < 2; attempt++)
            {
                string pending = Path.Combine(
                    stagingDirectory, ExeFileName + "." + Guid.NewGuid().ToString("N") + ".new");
                try
                {
                    DownloadExe(pending);
                    long size = new FileInfo(pending).Length;
                    if (size < MinExeBytes)
                        throw new InvalidOperationException("Download failed.");
                    string actual = Sha256(pending);
                    DebugLog.Info("updater sha256 expected=" + expectedHash + " actual=" + actual
                        + (string.Equals(actual, expectedHash, StringComparison.OrdinalIgnoreCase) ? " ok" : " MISMATCH"));
                    if (string.Equals(
                        actual, expectedHash, StringComparison.OrdinalIgnoreCase))
                        return pending;

                    try { File.Delete(pending); } catch { }
                    if (attempt == 1)
                        throw new InvalidOperationException(
                            "The downloaded update failed its integrity check.");
                }
                catch
                {
                    try { File.Delete(pending); } catch { }
                    throw;
                }
            }

            throw new InvalidOperationException(
                "The downloaded update failed its integrity check.");
        }

        // Beta builds only: the prod Latest version, read straight from the GitHub
        // latest release (never the Worker, so it adds no usage count). Used by About
        // to show "Matches the current release" when beta and prod are the same version.
        const string ProdLatestVersionUrl =
            "https://github.com/arostad/IPbind/releases/download/latest/version.txt";

        public static bool BetaMatchesProdLatest()
        {
            if (!BuildInfo.IsBeta) return false;
            string remoteText;
            Version remote;
            string error;
            Version current;
            if (!TryReadVersion(ProdLatestVersionUrl, out remoteText, out remote, out error)) return false;
            if (!Version.TryParse(BuildInfo.Version, out current)) return false;
            return remote.CompareTo(current) == 0;
        }

        public static UpdateInfo Check()
        {
            string remoteText;
            Version remote;
            string error;
            if ((!BuildInfo.IsBeta
                    && TryReadVersion(WorkerVersionUrl, out remoteText, out remote, out error))
                || TryReadVersion(VersionUrl, out remoteText, out remote, out error))
            {
                Version current;
                if (!Version.TryParse(BuildInfo.Version, out current))
                    current = new Version(0, 0);
                bool newer = remote.CompareTo(current) > 0;
                DebugLog.Info("update check current=" + BuildInfo.Version + " remote=" + remoteText
                    + " compare=" + (newer ? "newer" : "same-or-older"));
                return new UpdateInfo
                {
                    RemoteVersion = remoteText,
                    IsNewer = newer,
                    Error = null
                };
            }

            return new UpdateInfo
            {
                RemoteVersion = remoteText,
                IsNewer = false,
                Error = error
            };
        }

        static bool TryReadVersion(
            string url, out string remoteText, out Version remote, out string error)
        {
            remoteText = "";
            remote = new Version(0, 0);
            error = "";
            try
            {
                using (HttpResponseMessage response = GetTrusted(url))
                {
                    DebugLog.LogHttp(url, (int)response.StatusCode, "update-check");
                    response.EnsureSuccessStatusCode();
                    string body = response.Content.ReadAsStringAsync().GetAwaiter().GetResult();
                    if (string.IsNullOrWhiteSpace(body))
                    {
                        error = "Could not read the latest version.";
                        return false;
                    }

                    string[] lines = body.Trim().Split(new char[] { '\n', '\r' });
                    remoteText = lines[0].Trim();
                    Version parsed;
                    if (!Version.TryParse(remoteText, out parsed))
                    {
                        error = "Latest version string was not readable.";
                        return false;
                    }

                    remote = parsed;
                    return true;
                }
            }
            catch (Exception ex)
            {
                error = DescribeError(ex, "Update check failed");
                return false;
            }
        }

        static string QuoteArgument(string value)
        {
            if (value == null) return "\"\"";

            StringBuilder quoted = new StringBuilder();
            quoted.Append('"');
            int backslashes = 0;
            foreach (char c in value)
            {
                if (c == '\\')
                {
                    backslashes++;
                    continue;
                }
                if (c == '"')
                {
                    quoted.Append('\\', backslashes * 2 + 1);
                    quoted.Append('"');
                    backslashes = 0;
                    continue;
                }
                quoted.Append('\\', backslashes);
                backslashes = 0;
                quoted.Append(c);
            }
            quoted.Append('\\', backslashes * 2);
            quoted.Append('"');
            return quoted.ToString();
        }

        static void StartApplication(string target, string helperPath)
        {
            ProcessStartInfo start = new ProcessStartInfo();
            start.FileName = target;
            start.Arguments = "--cleanup-update-helper " + QuoteArgument(helperPath);
            start.CreateNoWindow = false;
            start.UseShellExecute = false;
            if (Process.Start(start) == null)
                throw new InvalidOperationException("Could not restart IPbind.");
        }

        static void ReplaceTarget(string pending, string target)
        {
            bool sameVolume = string.Equals(
                Path.GetPathRoot(pending), Path.GetPathRoot(target),
                StringComparison.OrdinalIgnoreCase);

            for (int attempt = 0; attempt < 8; attempt++)
            {
                try
                {
                    if (File.Exists(target))
                    {
                        if (sameVolume)
                            File.Replace(pending, target, null);
                        else
                        {
                            File.Copy(pending, target, true);
                            File.Delete(pending);
                        }
                    }
                    else if (sameVolume)
                        File.Move(pending, target);
                    else
                    {
                        File.Copy(pending, target, false);
                        File.Delete(pending);
                    }
                    return;
                }
                catch
                {
                    if (attempt == 7) throw;
                    Thread.Sleep(1000);
                }
            }
        }

        static void ApplyUpdate(string[] args)
        {
            if (args.Length != 5)
                throw new ArgumentException("The update helper arguments were invalid.");

            string pending = Path.GetFullPath(args[1]);
            string target = Path.GetFullPath(args[2]);
            string expectedHash = args[3];
            int parentPid;
            if (!Regex.IsMatch(expectedHash ?? "", @"\A[0-9a-fA-F]{64}\z")
                || !int.TryParse(args[4], out parentPid) || parentPid <= 0
                || string.Equals(pending, target, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("The update helper arguments were invalid.");
            }

            WaitForParent(parentPid);

            FileInfo downloaded = new FileInfo(pending);
            if (!downloaded.Exists || downloaded.Length < MinExeBytes
                || downloaded.Length > MaxExeBytes)
            {
                throw new InvalidOperationException(
                    "The downloaded update has an unexpected size.");
            }
            if (!string.Equals(
                Sha256(pending), expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "The downloaded update failed its integrity check.");
            }

            ReplaceTarget(pending, target);
            if (!File.Exists(target))
                throw new InvalidOperationException("The updated executable is missing.");
        }

        static void WaitForParent(int parentPid)
        {
            try
            {
                using (Process parent = Process.GetProcessById(parentPid))
                    parent.WaitForExit();
            }
            catch (ArgumentException)
            {
                // The parent already exited before the helper queried it.
            }
        }

        static void ApplyInstallerUpdate(string[] args)
        {
            if (args.Length != 5)
                throw new ArgumentException("The update helper arguments were invalid.");

            if (string.IsNullOrEmpty(args[1]) || string.IsNullOrEmpty(args[2]))
                throw new ArgumentException("The update helper arguments were invalid.");
            string pending = Path.GetFullPath(args[1]);
            string installDir = Path.GetFullPath(args[2]);
            string expectedHash = args[3];
            int parentPid;
            if (!Regex.IsMatch(expectedHash ?? "", @"\A[0-9a-fA-F]{64}\z")
                || !int.TryParse(args[4], out parentPid) || parentPid <= 0
                || string.Equals(pending, installDir, StringComparison.OrdinalIgnoreCase))
            {
                throw new ArgumentException("The update helper arguments were invalid.");
            }

            WaitForParent(parentPid);

            FileInfo downloaded = new FileInfo(pending);
            if (!downloaded.Exists || downloaded.Length < MinExeBytes
                || downloaded.Length > MaxExeBytes)
            {
                throw new InvalidOperationException(
                    "The downloaded update has an unexpected size.");
            }
            if (!string.Equals(
                Sha256(pending), expectedHash, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    "The downloaded update failed its integrity check.");
            }

            ProcessStartInfo setup = new ProcessStartInfo();
            setup.FileName = pending;
            setup.Arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART "
                + "/NORESTARTAPPLICATIONS /DIR=" + QuoteArgument(installDir);
            setup.UseShellExecute = false;
            setup.CreateNoWindow = true;
            using (Process process = Process.Start(setup))
            {
                if (process == null)
                    throw new InvalidOperationException("Could not start the installer.");
                process.WaitForExit();
                if (process.ExitCode != 0)
                    throw new InvalidOperationException(
                        "The installer exited with code "
                        + process.ExitCode.ToString() + ".");
            }

            string target = Path.Combine(installDir, ExeFileName);
            if (!File.Exists(target))
                throw new InvalidOperationException("The updated executable is missing.");
        }

        public static bool TryRunApplyUpdate(string[] args)
        {
            if (args == null || args.Length == 0)
                return false;
            bool installerUpdate = string.Equals(
                args[0], "--apply-installer-update", StringComparison.Ordinal);
            if (!installerUpdate
                && !string.Equals(args[0], "--apply-update", StringComparison.Ordinal))
                return false;

            string pending = args.Length > 1 ? args[1] : "";
            string helperPath = Application.ExecutablePath;
            string errorFile = "";
            string target = "";
            try
            {
                if (installerUpdate)
                {
                    if (args.Length > 2)
                    {
                        string installDir = Path.GetFullPath(args[2]);
                        target = Path.Combine(installDir, ExeFileName);
                        errorFile = Path.Combine(installDir, "update-error.txt");
                    }
                    ApplyInstallerUpdate(args);
                    DebugLog.Info(
                        "update helper ApplyInstallerUpdate completed target=" + target);
                }
                else
                {
                    target = args.Length > 2 ? args[2] : "";
                    if (!string.IsNullOrEmpty(target))
                    {
                        target = Path.GetFullPath(target);
                        string directory = Path.GetDirectoryName(target);
                        if (!string.IsNullOrEmpty(directory))
                            errorFile = Path.Combine(directory, "update-error.txt");
                    }

                    ApplyUpdate(args);
                    DebugLog.Info("update helper ApplyUpdate completed target=" + target);
                }
                if (!string.IsNullOrEmpty(errorFile))
                    try { File.Delete(errorFile); } catch { }
            }
            catch (Exception ex)
            {
                DebugLog.Exception("update helper ApplyUpdate", ex);
                if (!string.IsNullOrEmpty(errorFile))
                {
                    try
                    {
                        PrivateFile.WriteAllText(
                            errorFile, "Update failed: " + MostSpecificMessage(ex));
                    }
                    catch { }
                }
            }
            finally
            {
                if (!string.IsNullOrEmpty(pending))
                    try { File.Delete(pending); } catch { }
            }

            if (!string.IsNullOrEmpty(target) && File.Exists(target))
            {
                try { StartApplication(target, helperPath); } catch { }
            }
            return true;
        }

        public static void BeginHelperCleanup(string[] args)
        {
            if (args == null || args.Length != 2
                || !string.Equals(
                    args[0], "--cleanup-update-helper", StringComparison.Ordinal))
                return;

            string helperPath;
            try { helperPath = Path.GetFullPath(args[1]); }
            catch { return; }
            if (string.Equals(
                helperPath, Application.ExecutablePath, StringComparison.OrdinalIgnoreCase))
                return;

            Thread cleanup = new Thread(delegate()
            {
                for (int attempt = 0; attempt < 20; attempt++)
                {
                    try
                    {
                        File.Delete(helperPath);
                        return;
                    }
                    catch { Thread.Sleep(250); }
                }
            });
            cleanup.IsBackground = true;
            cleanup.Start();
        }

        static string ReadReleaseVersion()
        {
            // Read GitHub version.txt (latest or beta), never the Worker.
            string remoteText;
            Version remote;
            string error;
            if (!TryReadVersion(VersionUrl, out remoteText, out remote, out error)
                || string.IsNullOrEmpty(remoteText))
            {
                throw new InvalidOperationException(
                    string.IsNullOrEmpty(error)
                        ? "Could not read the latest version."
                        : error);
            }
            return remoteText;
        }

        static string DownloadVerifiedInstaller(
            string version, string installDirectory, out string expectedHash)
        {
            string url = InstallerUrl(version);
            expectedHash = ReadChecksum(url + ".sha256");
            string stagingDirectory = ChooseStagingDirectory(installDirectory);
            string fileName = InstallerFileName(version);
            for (int attempt = 0; attempt < 2; attempt++)
            {
                string pending = Path.Combine(
                    stagingDirectory,
                    fileName + "." + Guid.NewGuid().ToString("N") + ".new");
                try
                {
                    DownloadTrustedFile(url, pending);
                    long size = new FileInfo(pending).Length;
                    if (size < MinExeBytes)
                        throw new InvalidOperationException("Download failed.");
                    string actual = Sha256(pending);
                    DebugLog.Info(
                        "updater installer sha256 expected=" + expectedHash
                        + " actual=" + actual
                        + (string.Equals(actual, expectedHash, StringComparison.OrdinalIgnoreCase)
                            ? " ok" : " MISMATCH"));
                    if (string.Equals(
                        actual, expectedHash, StringComparison.OrdinalIgnoreCase))
                        return pending;

                    try { File.Delete(pending); } catch { }
                    if (attempt == 1)
                        throw new InvalidOperationException(
                            "The downloaded update failed its integrity check.");
                }
                catch
                {
                    try { File.Delete(pending); } catch { }
                    throw;
                }
            }

            throw new InvalidOperationException(
                "The downloaded update failed its integrity check.");
        }

        static void StartInstallerHelper(
            string pending, string installDirectory, string expectedHash)
        {
            string stagingDirectory = ChooseStagingDirectory(installDirectory);
            string helper = Path.Combine(
                stagingDirectory,
                ".IPbind-update-helper-" + Guid.NewGuid().ToString("N") + ".exe");
            try
            {
                PrivateFile.CopyExecutable(Application.ExecutablePath, helper);

                ProcessStartInfo start = new ProcessStartInfo();
                start.FileName = helper;
                start.Arguments = "--apply-installer-update " + QuoteArgument(pending)
                    + " " + QuoteArgument(installDirectory) + " "
                    + QuoteArgument(expectedHash) + " "
                    + Process.GetCurrentProcess().Id.ToString(
                        System.Globalization.CultureInfo.InvariantCulture);
                start.CreateNoWindow = true;
                start.UseShellExecute = false;
                if (Process.Start(start) == null)
                    throw new InvalidOperationException("Could not start the update helper.");
            }
            catch
            {
                try { File.Delete(helper); } catch { }
                try { File.Delete(pending); } catch { }
                throw;
            }
        }

        static Form CreateFinishingUpdateForm()
        {
            Form form = new Form();
            form.Text = BuildInfo.AppName;
            form.FormBorderStyle = FormBorderStyle.FixedDialog;
            form.StartPosition = FormStartPosition.CenterScreen;
            form.ControlBox = false;
            form.MaximizeBox = false;
            form.MinimizeBox = false;
            form.ShowInTaskbar = true;
            form.ClientSize = new System.Drawing.Size(360, 72);
            Label label = new Label();
            label.Text = "Finishing the IPbind update...";
            label.AutoSize = false;
            label.Dock = DockStyle.Fill;
            label.TextAlign = System.Drawing.ContentAlignment.MiddleCenter;
            form.Controls.Add(label);
            return form;
        }

        // Called on startup when this install still has an older Inno
        // uninstaller (no /IPBINDPROMPT). Downloads this build's installer
        // and lets a helper run it silently after this process exits.
        // Returns true when the helper was started and Main should return.
        public static bool TryStartUninstallerRefresh()
        {
            string directory;
            try
            {
                string exe = ResolveInstalledExe();
                directory = Path.GetDirectoryName(exe);
            }
            catch
            {
                return false;
            }
            if (string.IsNullOrEmpty(directory) || !UninstallerNeedsRefresh(directory))
                return false;

            Form status = null;
            try
            {
                try
                {
                    status = CreateFinishingUpdateForm();
                    status.Show();
                    Application.DoEvents();
                }
                catch
                {
                    status = null;
                }

                string expectedHash;
                string pending = DownloadVerifiedInstaller(
                    BuildInfo.Version, directory, out expectedHash);
                StartInstallerHelper(pending, directory, expectedHash);
                DebugLog.Info(
                    "uninstaller refresh helper started version=" + BuildInfo.Version);
                return true;
            }
            catch (Exception ex)
            {
                DebugLog.Exception("uninstaller refresh", ex);
                return false;
            }
            finally
            {
                if (status != null)
                {
                    try { status.Close(); } catch { }
                    try { status.Dispose(); } catch { }
                }
            }
        }

        public static void DownloadAndRestart()
        {
            string exe = ResolveInstalledExe();
            string directory = Path.GetDirectoryName(exe);
            if (string.IsNullOrEmpty(directory))
                throw new InvalidOperationException("Could not resolve the install directory.");
            Directory.CreateDirectory(directory);

            if (IsInstallerManaged(directory))
            {
                string version = ReadReleaseVersion();
                string installerHash;
                string installerPending = DownloadVerifiedInstaller(
                    version, directory, out installerHash);
                StartInstallerHelper(installerPending, directory, installerHash);
                return;
            }

            string expectedHash;
            string pending = DownloadVerifiedExe(directory, out expectedHash);
            string stagingDirectory = ChooseStagingDirectory(directory);
            string helper = Path.Combine(
                stagingDirectory,
                ".IPbind-update-helper-" + Guid.NewGuid().ToString("N") + ".exe");
            try
            {
                PrivateFile.CopyExecutable(Application.ExecutablePath, helper);

                ProcessStartInfo start = new ProcessStartInfo();
                start.FileName = helper;
                start.Arguments = "--apply-update " + QuoteArgument(pending) + " " +
                    QuoteArgument(exe) + " " + QuoteArgument(expectedHash) + " " +
                    Process.GetCurrentProcess().Id.ToString(
                        System.Globalization.CultureInfo.InvariantCulture);
                start.CreateNoWindow = true;
                start.UseShellExecute = false;
                if (Process.Start(start) == null)
                    throw new InvalidOperationException("Could not start the update helper.");
            }
            catch
            {
                try { File.Delete(helper); } catch { }
                try { File.Delete(pending); } catch { }
                throw;
            }
        }
    }

    static class WindowTheme
    {
        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int val, int size);

        [DllImport("uxtheme.dll", CharSet = CharSet.Unicode)]
        public static extern int SetWindowTheme(IntPtr hWnd, string subApp, string subId);

        // uxtheme ordinal 135 (Win10 1903+): 0=Default, 1=AllowDark, 2=ForceDark.
        [DllImport("uxtheme.dll", EntryPoint = "#135")]
        static extern int SetPreferredAppMode(int mode);

        // uxtheme ordinal 133 (Win10 1809+).
        [DllImport("uxtheme.dll", EntryPoint = "#133")]
        static extern int AllowDarkModeForWindow(IntPtr hWnd, int allow);

        [DllImport("user32.dll")]
        static extern bool GetComboBoxInfo(IntPtr hwndCombo, ref ComboBoxInfo info);

        [StructLayout(LayoutKind.Sequential)]
        public struct RECT
        {
            public int Left, Top, Right, Bottom;
            public Rectangle ToRectangle()
            {
                return Rectangle.FromLTRB(Left, Top, Right, Bottom);
            }
        }

        [StructLayout(LayoutKind.Sequential)]
        public struct ComboBoxInfo
        {
            public int cbSize;
            public RECT rcItem;
            public RECT rcButton;
            public int stateButton;
            public IntPtr hwndCombo;
            public IntPtr hwndItem;
            public IntPtr hwndList;
        }

        public static bool IsAppDark()
        {
            try
            {
                object v = Microsoft.Win32.Registry.GetValue(
                    @"HKEY_CURRENT_USER\Software\Microsoft\Windows\CurrentVersion\Themes\Personalize",
                    "AppsUseLightTheme", 1);
                if (v is int) return ((int)v) == 0;   // 0 = dark, 1 = light
            }
            catch { }
            return false;
        }

        // Call once before any window is created so combo lists and other
        // common-controls popups can use DarkMode_CFD / DarkMode_Explorer.
        public static void ApplyPreferredAppMode()
        {
            try { SetPreferredAppMode(IsAppDark() ? 2 : 0); } catch { }   // ForceDark or Default
        }

        // Tint a form's non-client title bar after its handle has been created.
        public static void ApplyTitleBar(Form form, bool dark)
        {
            try
            {
                int on = dark ? 1 : 0;
                // attribute 20 = DWMWA_USE_IMMERSIVE_DARK_MODE (Win10 2004+/Win11);
                // older builds used 19, so fall back to it.
                if (DwmSetWindowAttribute(form.Handle, 20, ref on, 4) != 0)
                    DwmSetWindowAttribute(form.Handle, 19, ref on, 4);
            }
            catch { }
        }

        public static bool TryGetComboBoxInfo(IntPtr hwnd, out ComboBoxInfo info)
        {
            info = new ComboBoxInfo();
            info.cbSize = Marshal.SizeOf(typeof(ComboBoxInfo));
            try { return hwnd != IntPtr.Zero && GetComboBoxInfo(hwnd, ref info); }
            catch { return false; }
        }

        // DarkMode_Explorer does not style combo chrome (field, drop arrow, list).
        // DarkMode_CFD does, on the combo HWND and on its dropdown list HWND.
        public static void ApplyCombo(ComboBox box, bool dark)
        {
            if (box == null || !box.IsHandleCreated) return;
            try { AllowDarkModeForWindow(box.Handle, dark ? 1 : 0); } catch { }
            try { SetWindowTheme(box.Handle, dark ? "DarkMode_CFD" : null, null); } catch { }
            ComboBoxInfo info;
            if (!TryGetComboBoxInfo(box.Handle, out info)) return;
            if (info.hwndItem != IntPtr.Zero)
            {
                try { AllowDarkModeForWindow(info.hwndItem, dark ? 1 : 0); } catch { }
                try { SetWindowTheme(info.hwndItem, dark ? "DarkMode_CFD" : null, null); } catch { }
            }
            if (info.hwndList != IntPtr.Zero)
            {
                try { AllowDarkModeForWindow(info.hwndList, dark ? 1 : 0); } catch { }
                try { SetWindowTheme(info.hwndList, dark ? "DarkMode_CFD" : null, null); } catch { }
            }
        }
    }

    // Main-window dropdown (LAN interface and "Use gateway on"). Owner-draw
    // keeps the closed field free of the system-blue focus highlight. In dark
    // mode the OS still paints a white field and arrow (especially when the
    // box is empty and DrawItem never runs), so we theme with DarkMode_CFD and
    // overpaint the closed chrome ourselves. Layout is unchanged.
    sealed class ThemedComboBox : ComboBox
    {
        const int WM_PAINT = 0x000F;
        const int WM_ERASEBKGND = 0x0014;
        const int WM_PRINTCLIENT = 0x0318;

        [StructLayout(LayoutKind.Sequential)]
        struct PAINTSTRUCT
        {
            public IntPtr hdc;
            public int fErase;
            public int left, top, right, bottom;
            public int fRestore;
            public int fIncUpdate;
            public int r0, r1, r2, r3, r4, r5, r6, r7;
        }

        [DllImport("user32.dll")]
        static extern IntPtr BeginPaint(IntPtr hWnd, ref PAINTSTRUCT lpPaint);
        [DllImport("user32.dll")]
        static extern bool EndPaint(IntPtr hWnd, ref PAINTSTRUCT lpPaint);

        public bool UseDarkChrome;
        public int DebugPaintCount;

        public ThemedComboBox()
        {
            DrawMode = DrawMode.OwnerDrawFixed;
            DropDownStyle = ComboBoxStyle.DropDownList;
        }

        public void ApplyDark(bool dark)
        {
            UseDarkChrome = dark;
            if (dark) FlatStyle = FlatStyle.Standard;
            if (IsHandleCreated) WindowTheme.ApplyCombo(this, dark);
            Invalidate();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            if (UseDarkChrome) WindowTheme.ApplyCombo(this, true);
        }

        protected override void OnDropDown(EventArgs e)
        {
            if (UseDarkChrome) WindowTheme.ApplyCombo(this, true);
            base.OnDropDown(e);
        }

        protected override void OnDropDownClosed(EventArgs e)
        {
            base.OnDropDownClosed(e);
            Invalidate();
        }

        protected override void OnEnabledChanged(EventArgs e)
        {
            base.OnEnabledChanged(e);
            if (UseDarkChrome) WindowTheme.ApplyCombo(this, true);
            Invalidate();
        }

        // Dark closed chrome must be painted once, on the WM_PAINT DC.
        // The 2.3.136 path called base.WndProc (system paints the light/hot
        // chrome, then EndPaint) and then overpainted with Graphics.FromHwnd
        // after EndPaint. A focused DropDownList is also repainted on
        // mouse-move hot-track, so that two-pass paint flashed continuously
        // and FromHwnd-after-EndPaint could invalidate and loop.
        protected override void WndProc(ref Message m)
        {
            if (UseDarkChrome && m.Msg == WM_ERASEBKGND)
            {
                m.Result = (IntPtr)1;
                return;
            }
            if (UseDarkChrome && m.Msg == WM_PAINT)
            {
                DebugPaintCount++;
                PAINTSTRUCT ps = new PAINTSTRUCT();
                IntPtr hdc = BeginPaint(Handle, ref ps);
                try
                {
                    if (hdc != IntPtr.Zero)
                    {
                        using (Graphics g = Graphics.FromHdc(hdc))
                            DrawClosedChrome(g);
                    }
                }
                finally
                {
                    EndPaint(Handle, ref ps);
                }
                return;
            }
            if (UseDarkChrome && m.Msg == WM_PRINTCLIENT && m.WParam != IntPtr.Zero)
            {
                DebugPaintCount++;
                using (Graphics g = Graphics.FromHdc(m.WParam))
                    DrawClosedChrome(g);
                return;
            }
            base.WndProc(ref m);
        }

        void DrawClosedChrome(Graphics dest)
        {
            if (!IsHandleCreated) return;
            Rectangle client = ClientRectangle;
            if (client.Width <= 0 || client.Height <= 0) return;
            try
            {
                using (Bitmap bmp = new Bitmap(client.Width, client.Height))
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.Clear(BackColor);
                    WindowTheme.ComboBoxInfo info;
                    bool have = WindowTheme.TryGetComboBoxInfo(Handle, out info);
                    Color field = BackColor;
                    Color border = Color.FromArgb(80, 80, 80);
                    Color fore = Enabled ? ForeColor : Color.FromArgb(150, 150, 150);

                    using (SolidBrush brush = new SolidBrush(field))
                    {
                        g.FillRectangle(brush, client);
                        if (have)
                        {
                            Rectangle button = info.rcButton.ToRectangle();
                            if (button.Width <= 0)
                                button = new Rectangle(
                                    client.Right - Math.Max(16, client.Height),
                                    client.Top, Math.Max(16, client.Height), client.Height);
                            g.FillRectangle(brush, button);
                            DrawChevron(g, button, fore);
                        }
                    }

                    using (Pen pen = new Pen(border))
                        g.DrawRectangle(pen, client.X, client.Y, client.Width - 1, client.Height - 1);

                    string text = Text;
                    if (string.IsNullOrEmpty(text) && SelectedIndex >= 0 && SelectedIndex < Items.Count)
                        text = Items[SelectedIndex].ToString();
                    Rectangle item = have ? info.rcItem.ToRectangle() : client;
                    int pad = Math.Max(3, (int)Math.Round(3 * g.DpiX / 96f));
                    Rectangle textBounds = new Rectangle(
                        item.X + pad, item.Y,
                        Math.Max(0, item.Width - pad * 2), item.Height);
                    if (!string.IsNullOrEmpty(text) && textBounds.Width > 0)
                    {
                        TextRenderer.DrawText(
                            g, text, Font, textBounds, fore,
                            TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                            TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix |
                            TextFormatFlags.NoPadding);
                    }
                    dest.DrawImageUnscaled(bmp, 0, 0);
                }
            }
            catch { }
        }

        static void DrawChevron(Graphics g, Rectangle button, Color color)
        {
            if (button.Width <= 0 || button.Height <= 0) return;
            int cx = button.X + button.Width / 2;
            int cy = button.Y + button.Height / 2;
            int w = Math.Max(3, button.Height / 5);
            int h = Math.Max(2, button.Height / 7);
            Point[] pts = new Point[]
            {
                new Point(cx - w, cy - h / 2),
                new Point(cx + w, cy - h / 2),
                new Point(cx, cy + h + h / 2)
            };
            SmoothingMode old = g.SmoothingMode;
            g.SmoothingMode = SmoothingMode.AntiAlias;
            using (SolidBrush brush = new SolidBrush(color))
                g.FillPolygon(brush, pts);
            g.SmoothingMode = old;
        }
    }

    // Native sizing for the per-monitor dialogs (About, setup reminder). WinForms on
    // .NET Framework turns a client size into a window size with AdjustWindowRectEx,
    // which uses the frame metrics of the system DPI, not the window's own DPI, and it
    // reads screens through a cache. On a monitor whose scale differs from the system
    // scale that gives the wrong window size. These helpers size, place and measure
    // the window in physical pixels for the DPI the window really has.
    static class DpiWindow
    {
        [StructLayout(LayoutKind.Sequential)]
        public struct RECT { public int Left, Top, Right, Bottom; }

        [StructLayout(LayoutKind.Sequential)]
        struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public int dwFlags; }

        [StructLayout(LayoutKind.Sequential)]
        struct POINT { public int X, Y; }

        const int GWL_STYLE = -16, GWL_EXSTYLE = -20;
        const uint SWP_NOZORDER = 0x0004, SWP_NOACTIVATE = 0x0010;
        const int MONITOR_DEFAULTTONEAREST = 2;

        [DllImport("user32.dll")] static extern int GetDpiForWindow(IntPtr hwnd);
        [DllImport("user32.dll")] static extern int GetDpiForSystem();
        [DllImport("user32.dll")] static extern bool AdjustWindowRectExForDpi(ref RECT rect, int style, bool menu, int exStyle, int dpi);
        [DllImport("user32.dll")] static extern bool AdjustWindowRectEx(ref RECT rect, int style, bool menu, int exStyle);
        [DllImport("user32.dll", EntryPoint = "GetWindowLong")] static extern int GetWindowLong32(IntPtr hwnd, int index);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll")] static extern IntPtr MonitorFromRect(ref RECT rect, int flags);
        [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hwnd, out RECT rect);
        [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr hwnd, out RECT rect);
        [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr hwnd, ref POINT point);
        [DllImport("user32.dll")] static extern IntPtr GetWindowDpiAwarenessContext(IntPtr hwnd);
        [DllImport("user32.dll")] static extern IntPtr SetThreadDpiAwarenessContext(IntPtr context);

        public static readonly IntPtr PerMonitorV2 = new IntPtr(-4);

        // Runs native calls in the window's own DPI context so no coordinate
        // virtualization happens, whatever context the calling thread is in.
        static IntPtr Enter(IntPtr hwnd)
        {
            try
            {
                IntPtr ctx = hwnd != IntPtr.Zero ? GetWindowDpiAwarenessContext(hwnd) : PerMonitorV2;
                if (ctx == IntPtr.Zero) ctx = PerMonitorV2;
                return SetThreadDpiAwarenessContext(ctx);
            }
            catch { return IntPtr.Zero; }
        }

        static void Leave(IntPtr previous)
        {
            if (previous == IntPtr.Zero) return;
            try { SetThreadDpiAwarenessContext(previous); } catch { }
        }

        public static int WindowDpi(IntPtr hwnd)
        {
            try { return hwnd != IntPtr.Zero ? GetDpiForWindow(hwnd) : 0; }
            catch { return 0; }
        }

        public static int SystemDpi()
        {
            try { int dpi = GetDpiForSystem(); if (dpi > 0) return dpi; }
            catch { }
            try { using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) return (int)Math.Round(g.DpiX); }
            catch { return 96; }
        }

        // Window size for a client size, using the frame metrics of the given DPI.
        public static Size WindowSizeFor(IntPtr hwnd, Size client, int dpi)
        {
            RECT r = new RECT();
            r.Right = client.Width;
            r.Bottom = client.Height;
            int style = GetWindowLong32(hwnd, GWL_STYLE);
            int ex = GetWindowLong32(hwnd, GWL_EXSTYLE);
            bool ok = false;
            try { if (dpi > 0) ok = AdjustWindowRectExForDpi(ref r, style, false, ex, dpi); }
            catch { ok = false; }
            if (!ok)
            {
                r = new RECT();
                r.Right = client.Width;
                r.Bottom = client.Height;
                AdjustWindowRectEx(ref r, style, false, ex);
            }
            return new Size(r.Right - r.Left, r.Bottom - r.Top);
        }

        public static Rectangle WorkAreaFor(IntPtr hwnd, Rectangle bounds)
        {
            IntPtr prev = Enter(hwnd);
            try
            {
                RECT r = new RECT();
                r.Left = bounds.Left; r.Top = bounds.Top; r.Right = bounds.Right; r.Bottom = bounds.Bottom;
                IntPtr mon = MonitorFromRect(ref r, MONITOR_DEFAULTTONEAREST);
                MONITORINFO info = new MONITORINFO();
                info.cbSize = Marshal.SizeOf(typeof(MONITORINFO));
                if (mon != IntPtr.Zero && GetMonitorInfo(mon, ref info))
                    return Rectangle.FromLTRB(info.rcWork.Left, info.rcWork.Top, info.rcWork.Right, info.rcWork.Bottom);
            }
            catch { }
            finally { Leave(prev); }
            return Screen.PrimaryScreen.WorkingArea;
        }

        public static Rectangle Bounds(IntPtr hwnd)
        {
            IntPtr prev = Enter(hwnd);
            try
            {
                RECT r;
                if (GetWindowRect(hwnd, out r)) return Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
            }
            catch { }
            finally { Leave(prev); }
            return Rectangle.Empty;
        }

        // Client rectangle in screen pixels.
        public static Rectangle ClientBounds(IntPtr hwnd)
        {
            IntPtr prev = Enter(hwnd);
            try
            {
                RECT r;
                if (!GetClientRect(hwnd, out r)) return Rectangle.Empty;
                POINT p = new POINT();
                ClientToScreen(hwnd, ref p);
                return new Rectangle(p.X, p.Y, r.Right - r.Left, r.Bottom - r.Top);
            }
            catch { return Rectangle.Empty; }
            finally { Leave(prev); }
        }

        public static void SetBounds(IntPtr hwnd, Rectangle bounds)
        {
            IntPtr prev = Enter(hwnd);
            try { SetWindowPos(hwnd, IntPtr.Zero, bounds.X, bounds.Y, bounds.Width, bounds.Height, SWP_NOZORDER | SWP_NOACTIVATE); }
            catch { }
            finally { Leave(prev); }
        }

        // Keep the whole window, title bar first, inside one work area.
        public static Rectangle Clamp(Rectangle window, Rectangle work)
        {
            int x = window.X, y = window.Y;
            if (x + window.Width > work.Right) x = work.Right - window.Width;
            if (y + window.Height > work.Bottom) y = work.Bottom - window.Height;
            if (x < work.Left) x = work.Left;
            if (y < work.Top) y = work.Top;
            return new Rectangle(x, y, window.Width, window.Height);
        }

        // Use around paint, layout, and WndProc so a PMv2 dialog inside a
        // system-aware process is not coordinate-virtualized to the system DPI.
        public static IntPtr EnterContext(IntPtr hwnd)
        {
            return hwnd == IntPtr.Zero ? IntPtr.Zero : Enter(hwnd);
        }

        public static void LeaveContext(IntPtr previous)
        {
            Leave(previous);
        }

        public static Size ClientPixelSize(IntPtr hwnd)
        {
            if (hwnd == IntPtr.Zero) return Size.Empty;
            IntPtr prev = Enter(hwnd);
            try
            {
                RECT r;
                if (GetClientRect(hwnd, out r)) return new Size(r.Right - r.Left, r.Bottom - r.Top);
            }
            catch { }
            finally { Leave(prev); }
            return Size.Empty;
        }
    }

    // Base for the About and setup-reminder dialogs. The handle is created Per-Monitor
    // V2 (IPbind itself stays system-DPI aware), the content is laid out in code at
    // scale sc, and the window is sized natively so that its client area is exactly
    // the laid-out content at the DPI the window really has. On WM_DPICHANGED the
    // content is laid out at the new scale first, then the window is moved to the
    // top-left of the suggested rectangle with a size computed for the new DPI, in a
    // single SetWindowPos (no intermediate resize at the old position, which could
    // flip the window back to the other monitor).
    abstract class PerMonitorDialog : Form
    {
        const int WM_DPICHANGED = 0x02E0;
        const int WM_ENTERSIZEMOVE = 0x0231;
        const int WM_EXITSIZEMOVE = 0x0232;
        const int WM_GETDPISCALEDSIZE = 0x02E4;

        protected float sc = 1f;
        bool followSystemScale;
        Size layoutClient;
        bool deferResize;
        bool dpiChanging;
        bool inSizeMove;
        Rectangle layoutWork = Rectangle.Empty;
        Rectangle layoutWorkUsed = Rectangle.Empty;
        int pendingDpi;
        Rectangle pendingRect;

        // Test and diagnostics hooks (read by the UI harness).
        public Size LayoutClientSize { get { return layoutClient; } }
        public float LayoutScale { get { return sc; } }
        public readonly List<string> DpiLog = new List<string>();
        // When set (Width and Height > 0), CurrentWorkArea uses this instead of
        // the real monitor, so a test can simulate a 4K screen vs a 1080p one.
        public Rectangle SimulatedWorkArea;

        public void RelayoutForTest()
        {
            LayoutContent();
            if (IsHandleCreated) EnsureClientMatchesLayout("test-relayout");
        }

        protected abstract void LayoutContent();

        // scale is MainForm's system-DPI scale in the app, or a test scale. Only a
        // real system scale follows the window's own DPI when it is first shown.
        protected void InitScale(float scale)
        {
            sc = scale > 0f ? scale : 1f;
            followSystemScale = Math.Abs(sc - DpiWindow.SystemDpi() / 96f) < 0.01f;
        }

        protected int U(int value) { return (int)Math.Round(value * sc); }

        static string regularFamily, semiboldFamily;

        // The mockups were drawn in Segoe UI Variable Text (Windows 11), which is
        // lighter and a little narrower than classic Segoe UI. Use it when it is
        // installed; Windows 10 falls back to Segoe UI. Weights follow the mockups:
        // 400 Regular, 600 the "... Semibold" family (Regular style), 700 Bold.
        // GDI's FontStyle.Bold is 700, which read much heavier than the 600 asked for.
        // px is already in device pixels (callers multiply by sc exactly once).
        protected static Font NewUiFont(float px, FontStyle style, bool semibold)
        {
            if (regularFamily == null)
            {
                bool variable = HasFamily("Segoe UI Variable Text") && HasFamily("Segoe UI Variable Text Semibold");
                regularFamily = variable ? "Segoe UI Variable Text" : "Segoe UI";
                semiboldFamily = variable ? "Segoe UI Variable Text Semibold"
                    : (HasFamily("Segoe UI Semibold") ? "Segoe UI Semibold" : null);
            }
            if (semibold && semiboldFamily != null)
                return new Font(semiboldFamily, px, style & ~FontStyle.Bold, GraphicsUnit.Pixel);
            if (semibold) style |= FontStyle.Bold;
            return new Font(regularFamily, px, style, GraphicsUnit.Pixel);
        }

        static bool HasFamily(string name)
        {
            try
            {
                using (Font f = new Font(name, 12f, FontStyle.Regular, GraphicsUnit.Pixel))
                    return string.Equals(f.Name, name, StringComparison.OrdinalIgnoreCase);
            }
            catch { return false; }
        }

        int CurrentDpi()
        {
            int dpi = IsHandleCreated ? DpiWindow.WindowDpi(Handle) : 0;
            return dpi > 0 ? dpi : DpiWindow.SystemDpi();
        }

        // Work area the layout must fit: the target monitor during a DPI change,
        // otherwise the monitor the window is on. A test can override the real
        // monitor with SimulatedWorkArea (for example a 4K screen on a 1080p runner).
        protected Rectangle CurrentWorkArea()
        {
            if (!layoutWork.IsEmpty) return layoutWork;
            return DestinationWorkArea(Rectangle.Empty);
        }

        // Prefer the test hook when it is set, so ApplyDpi / clamp / OnShown do
        // not fall back to the runner's real 1080p work area and wrongly widen
        // or clip a simulated 4K layout (CI dpirtrip on 2.3.140, SHA 73a4e91).
        Rectangle DestinationWorkArea(Rectangle suggested)
        {
            if (SimulatedWorkArea.Width > 0 && SimulatedWorkArea.Height > 0)
                return SimulatedWorkArea;
            if (IsHandleCreated)
            {
                Rectangle hint = suggested.IsEmpty ? DpiWindow.Bounds(Handle) : suggested;
                return DpiWindow.WorkAreaFor(Handle, hint);
            }
            return Screen.PrimaryScreen.WorkingArea;
        }

        // Frame size (window minus client) at the window's DPI.
        protected Size ChromeSize()
        {
            if (!IsHandleCreated)
                return new Size(SystemInformation.FixedFrameBorderSize.Width * 2,
                    SystemInformation.CaptionHeight + SystemInformation.FixedFrameBorderSize.Height * 2);
            Size client = new Size(200, 200);
            Size win = DpiWindow.WindowSizeFor(Handle, client, CurrentDpi());
            return new Size(Math.Max(0, win.Width - client.Width), Math.Max(0, win.Height - client.Height));
        }

        // Call at the end of LayoutContent instead of setting ClientSize.
        protected void SetLayoutClientSize(Size client)
        {
            layoutClient = client;
            layoutWorkUsed = CurrentWorkArea();
            if (!IsHandleCreated) { ClientSize = client; return; }
            if (deferResize) return;
            Size win = DpiWindow.WindowSizeFor(Handle, client, CurrentDpi());
            Rectangle b = DpiWindow.Bounds(Handle);
            DpiWindow.SetBounds(Handle, new Rectangle(b.X, b.Y, win.Width, win.Height));
        }

        protected void FitOnScreen()
        {
            if (!IsHandleCreated) return;
            Rectangle b = DpiWindow.Bounds(Handle);
            Rectangle work = DestinationWorkArea(b);
            Rectangle fit = DpiWindow.Clamp(b, work);
            if (fit != b) DpiWindow.SetBounds(Handle, fit);
        }

        protected override void CreateHandle()
        {
            IntPtr previous = IntPtr.Zero;
            bool changed = false;
            try
            {
                previous = SetThreadDpiAwarenessContext(DpiWindow.PerMonitorV2);
                changed = previous != IntPtr.Zero && previous != DpiWindow.PerMonitorV2;
            }
            catch { }
            try { base.CreateHandle(); }
            finally
            {
                if (changed)
                {
                    try { SetThreadDpiAwarenessContext(previous); } catch { }
                }
            }
        }

        [DllImport("user32.dll")]
        static extern IntPtr SetThreadDpiAwarenessContext(IntPtr value);

        protected override void OnShown(EventArgs e)
        {
            // Opened on a monitor whose scale is not the system scale (for example
            // IPbind sits on a 100% secondary while the main screen is 150%): no
            // WM_DPICHANGED arrives, so adopt the window's real DPI here.
            if (followSystemScale && IsHandleCreated)
            {
                int dpi = DpiWindow.WindowDpi(Handle);
                if (dpi > 0 && Math.Abs(dpi / 96f - sc) > 0.001f)
                {
                    Rectangle b = DpiWindow.Bounds(Handle);
                    Log("shown: window dpi " + dpi + " differs from scale " + sc);
                    ApplyDpi(dpi, b, false);
                }
            }
            if (IsHandleCreated && !dpiChanging)
            {
                // The first layout ran before the window existed, against the
                // primary screen's work area. If the dialog opened on a monitor
                // with a different work area, lay it out again for that one.
                Rectangle b = DpiWindow.Bounds(Handle);
                Rectangle work = DestinationWorkArea(b);
                if (!work.IsEmpty && work != layoutWorkUsed)
                {
                    Log("shown: work area " + work + " differs from layout work area " + layoutWorkUsed);
                    ApplyDpi((int)Math.Round(sc * 96f), b, true, true);
                }
            }
            base.OnShown(e);
            EnsureClientMatchesLayout("shown");
        }

        protected override void WndProc(ref Message m)
        {
            IntPtr prev = IsHandleCreated ? DpiWindow.EnterContext(Handle) : IntPtr.Zero;
            try
            {
                WndProcCore(ref m);
            }
            finally { DpiWindow.LeaveContext(prev); }
        }

        void WndProcCore(ref Message m)
        {
            if (m.Msg == WM_DPICHANGED)
            {
                int dpi = (int)(m.WParam.ToInt64() & 0xFFFF);
                Rectangle suggested = Rectangle.Empty;
                if (m.LParam != IntPtr.Zero)
                {
                    DpiWindow.RECT r = (DpiWindow.RECT)Marshal.PtrToStructure(m.LParam, typeof(DpiWindow.RECT));
                    suggested = Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
                }
                if (suggested.IsEmpty && IsHandleCreated) suggested = DpiWindow.Bounds(Handle);
                Size oldSize = Size;
                int oldDpi = 0;
                try { oldDpi = DpiWindow.WindowDpi(Handle); } catch { }
                OnDpiMessage(dpi, suggested);
                DebugLog.Info("WM_DPICHANGED " + GetType().Name + " oldDpi=" + oldDpi + " newDpi=" + dpi
                    + " oldSize=" + oldSize + " newSize=" + Size + " suggested=" + suggested);
                m.Result = IntPtr.Zero;
                return;
            }
            if (m.Msg == WM_GETDPISCALEDSIZE && m.LParam != IntPtr.Zero && !dpiChanging)
            {
                // Sent just before WM_DPICHANGED. Lay out at the new scale now and
                // report the exact window size, so the rectangle Windows suggests
                // (and the one a drag in progress keeps using) already fits the
                // content instead of a linear guess from the old size.
                int dpi = (int)(m.WParam.ToInt64() & 0xFFFF);
                if (dpi > 0)
                {
                    SuspendLayout();
                    deferResize = true;
                    // This message arrives before the window moves, so the
                    // current work area is still the old monitor. Do not widen
                    // or tighten against that; ApplyDpi re-lays out against the
                    // destination work area once WM_DPICHANGED supplies it.
                    layoutWork = new Rectangle(0, 0, 16384, 16384);
                    try
                    {
                        sc = dpi / 96f;
                        LayoutContent();
                    }
                    finally
                    {
                        layoutWork = Rectangle.Empty;
                        deferResize = false;
                        ResumeLayout(false);
                    }
                    Size win = DpiWindow.WindowSizeFor(Handle, layoutClient, dpi);
                    Marshal.WriteInt32(m.LParam, 0, win.Width);
                    Marshal.WriteInt32(m.LParam, 4, win.Height);
                    Log("scaled-size dpi " + dpi + " -> " + win);
                    m.Result = (IntPtr)1;
                    return;
                }
            }
            if (m.Msg == WM_ENTERSIZEMOVE) inSizeMove = true;
            if (m.Msg == WM_EXITSIZEMOVE)
            {
                inSizeMove = false;
                base.WndProc(ref m);
                // After a drag, make sure the content scale matches the monitor
                // and the whole dialog, title bar included, is on that monitor.
                int dpi = DpiWindow.WindowDpi(Handle);
                if (dpi > 0 && Math.Abs(dpi / 96f - sc) > 0.001f)
                    ApplyDpi(dpi, DpiWindow.Bounds(Handle), true);
                EnsureClientMatchesLayout("exit-size-move");
                FitOnScreen();
                return;
            }
            base.WndProc(ref m);
        }

        void OnDpiMessage(int dpi, Rectangle suggested)
        {
            if (dpi <= 0) return;
            if (dpiChanging)
            {
                // A nested change (the window crossed back while we resized it):
                // remember the latest one and apply it after the current pass.
                pendingDpi = dpi;
                pendingRect = suggested;
                return;
            }
            ApplyDpi(dpi, suggested, !inSizeMove);
        }

        void ApplyDpi(int dpi, Rectangle suggested, bool clamp)
        {
            ApplyDpi(dpi, suggested, clamp, false);
        }

        void ApplyDpi(int dpi, Rectangle suggested, bool clamp, bool relayout)
        {
            dpiChanging = true;
            try
            {
                for (int pass = 0; pass < 4 && dpi > 0; pass++)
                {
                    pendingDpi = 0;
                    Log("dpi " + dpi + " suggested " + suggested + " clamp=" + clamp);
                    Rectangle work = DestinationWorkArea(suggested);
                    float newSc = dpi / 96f;
                    SuspendLayout();
                    deferResize = true;
                    layoutWork = work;
                    try
                    {
                        // Always re-lay out. WM_GETDPISCALEDSIZE may already have
                        // set sc and laid out against the old monitor's work area;
                        // skipping here left the popup far too wide after a
                        // 100% -> high-scale return trip (Andy's 2.3.139 catch).
                        sc = newSc;
                        LayoutContent();
                        relayout = false;
                    }
                    finally
                    {
                        deferResize = false;
                        layoutWork = Rectangle.Empty;
                        ResumeLayout(false);
                    }
                    int frameDpi = DpiWindow.WindowDpi(Handle);
                    if (frameDpi <= 0) frameDpi = dpi;
                    Size win = DpiWindow.WindowSizeFor(Handle, layoutClient, frameDpi);
                    Rectangle target = new Rectangle(suggested.X, suggested.Y, win.Width, win.Height);
                    if (clamp) target = DpiWindow.Clamp(target, work);
                    DpiWindow.SetBounds(Handle, target);
                    Log("placed " + DpiWindow.Bounds(Handle) + " client " + DpiWindow.ClientBounds(Handle).Size
                        + " layout " + layoutClient + " frameDpi " + frameDpi);
                    PerformLayout();
                    if (pendingDpi <= 0) break;
                    dpi = pendingDpi;
                    suggested = pendingRect;
                }
            }
            finally { dpiChanging = false; pendingDpi = 0; }
            Invalidate(true);
            if (IsHandleCreated)
                BeginInvoke((MethodInvoker)delegate { EnsureClientMatchesLayout("after-dpi"); });
        }

        // Safety net: if anything left the client area different from the laid-out
        // content (for example a frame size from the wrong DPI), resize to match.
        void EnsureClientMatchesLayout(string why)
        {
            if (!IsHandleCreated || IsDisposed || layoutClient.IsEmpty) return;
            Size client = DpiWindow.ClientBounds(Handle).Size;
            if (client == layoutClient) return;
            Rectangle b = DpiWindow.Bounds(Handle);
            Size win = new Size(b.Width + layoutClient.Width - client.Width, b.Height + layoutClient.Height - client.Height);
            Rectangle target = new Rectangle(b.X, b.Y, win.Width, win.Height);
            if (!inSizeMove) target = DpiWindow.Clamp(target, DestinationWorkArea(b));
            DpiWindow.SetBounds(Handle, target);
            Log(why + ": client was " + client + ", fixed to " + DpiWindow.ClientBounds(Handle).Size);
        }

        void Log(string line)
        {
            if (DpiLog.Count < 200) DpiLog.Add(line);
        }
    }

    // 20 CSS-px checkbox for Per-Monitor V2 dialogs (About and the DHCP reminder).
    // The themed WinForms glyph is sized at the process (system) DPI, so on a PMv2
    // dialog it looks right on the system-DPI monitor and blows out or shrinks after
    // a drag to a monitor at a different scale (Andy, 2.3.135, About on a 100%
    // secondary). This control paints the box and check from the HWND's current
    // pixel size so a stale scale cannot overflow the square.
    // Shared dark-mode checkbox chrome. Unchecked boxes use a dark gray fill
    // and a slightly lighter border; hover is a touch lighter; disabled is
    // dimmer. Checked stays the caller's blue with a white (or given) check.
    // Painted once onto the WM_PAINT DC; callers must never Invalidate from paint.
    static class CheckBoxChrome
    {
        public static readonly Color DarkFill = Color.FromArgb(0x3A, 0x3A, 0x3C);
        public static readonly Color DarkBorder = Color.FromArgb(0x6E, 0x6E, 0x73);
        public static readonly Color DarkHoverFill = Color.FromArgb(0x48, 0x48, 0x4A);
        public static readonly Color DarkHoverBorder = Color.FromArgb(0x8E, 0x8E, 0x93);
        public static readonly Color DarkDisabledFill = Color.FromArgb(0x2C, 0x2C, 0x2E);
        public static readonly Color DarkDisabledBorder = Color.FromArgb(0x55, 0x55, 0x58);
        public static readonly Color DarkDisabledCheck = Color.FromArgb(0x40, 0x7A, 0x9A);
        public static readonly Color LightFill = Color.White;
        public static readonly Color LightBorder = Color.FromArgb(0x5A, 0x5A, 0x5A);

        public static void DrawBox(Graphics g, Rectangle box, int radius, float penWidth,
            bool isChecked, bool hot, bool enabled, bool dark, Color checkedFill, Color checkMark)
        {
            Color fill, border;
            if (isChecked)
            {
                fill = enabled ? checkedFill : (dark ? DarkDisabledCheck : Color.FromArgb(0xA0, 0xA0, 0xA0));
                border = fill;
            }
            else if (!enabled)
            {
                fill = dark ? DarkDisabledFill : Color.FromArgb(0xF0, 0xF0, 0xF0);
                border = dark ? DarkDisabledBorder : Color.FromArgb(0xB0, 0xB0, 0xB0);
            }
            else if (hot)
            {
                fill = dark ? DarkHoverFill : Color.FromArgb(0xF3, 0xF3, 0xF3);
                border = dark ? DarkHoverBorder : LightBorder;
            }
            else
            {
                fill = dark ? DarkFill : LightFill;
                border = dark ? DarkBorder : LightBorder;
            }
            using (GraphicsPath path = Rounded(box, radius))
            {
                using (SolidBrush brush = new SolidBrush(fill)) g.FillPath(brush, path);
                using (Pen pen = new Pen(border, Math.Max(1f, penWidth)))
                    g.DrawPath(pen, path);
            }
            if (isChecked) DrawCheck(g, box, enabled ? checkMark : Color.FromArgb(0xE0, 0xE0, 0xE0));
        }

        public static void DrawCheck(Graphics g, Rectangle box, Color color)
        {
            float w = box.Width, h = box.Height;
            PointF[] mark = new PointF[]
            {
                new PointF(box.X + w * 0.22f, box.Y + h * 0.52f),
                new PointF(box.X + w * 0.42f, box.Y + h * 0.72f),
                new PointF(box.X + w * 0.78f, box.Y + h * 0.30f)
            };
            using (Pen pen = new Pen(color, Math.Max(1.6f, box.Width * 2f / 20f)))
            {
                pen.StartCap = LineCap.Round;
                pen.EndCap = LineCap.Round;
                pen.LineJoin = LineJoin.Round;
                g.DrawLines(pen, mark);
            }
        }

        public static GraphicsPath Rounded(Rectangle r, int radius)
        {
            int d = radius * 2;
            GraphicsPath path = new GraphicsPath();
            if (d <= 0 || d > r.Width || d > r.Height)
            {
                path.AddRectangle(r);
                return path;
            }
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    sealed class RoundCheckBox : CheckBox
    {
        public const int DesignBox = 20;
        public const int DesignRing = 3;
        public const int DesignSide = DesignBox + 2 * DesignRing;
        const int WM_PAINT = 0x000F;
        const int WM_ERASEBKGND = 0x0014;
        const int WM_PRINTCLIENT = 0x0318;

        public Color Surface = Color.Black, BoxFill = Color.White, BoxBorder = Color.Gray;
        public Color CheckedFill = Color.DodgerBlue, CheckMark = Color.Black, FocusColor = Color.White;
        public Color HoverFill, HoverBorder, DisabledFill, DisabledBorder;
        public bool DarkChrome;
        bool hot;

        public static int RingFor(float scale) { return Math.Max(2, (int)Math.Round(DesignRing * scale)); }

        public static int SideFor(float scale)
        {
            return Math.Max(1, (int)Math.Round(DesignBox * scale)) + 2 * RingFor(scale);
        }

        public RoundCheckBox()
        {
            SetStyle(ControlStyles.UserPaint | ControlStyles.AllPaintingInWmPaint |
                ControlStyles.OptimizedDoubleBuffer | ControlStyles.ResizeRedraw, true);
            AutoSize = false;
            UseMnemonic = false;
            UseCompatibleTextRendering = false;
            Text = "";
            Cursor = Cursors.Hand;
        }

        public void ApplyDialogColors(Color surface, Color fore, Color checkedFill, Color checkMark, bool dark)
        {
            Surface = surface;
            DarkChrome = dark;
            if (dark)
            {
                BoxFill = CheckBoxChrome.DarkFill;
                BoxBorder = CheckBoxChrome.DarkBorder;
                HoverFill = CheckBoxChrome.DarkHoverFill;
                HoverBorder = CheckBoxChrome.DarkHoverBorder;
                DisabledFill = CheckBoxChrome.DarkDisabledFill;
                DisabledBorder = CheckBoxChrome.DarkDisabledBorder;
            }
            else
            {
                BoxFill = CheckBoxChrome.LightFill;
                BoxBorder = CheckBoxChrome.LightBorder;
                HoverFill = Color.FromArgb(0xF3, 0xF3, 0xF3);
                HoverBorder = CheckBoxChrome.LightBorder;
                DisabledFill = Color.FromArgb(0xF0, 0xF0, 0xF0);
                DisabledBorder = Color.FromArgb(0xB0, 0xB0, 0xB0);
            }
            CheckedFill = checkedFill;
            CheckMark = checkMark;
            FocusColor = fore;
            Invalidate();
        }

        protected override void OnCheckedChanged(EventArgs e)
        {
            base.OnCheckedChanged(e);
            Invalidate();
        }

        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }
        protected override void OnMouseEnter(EventArgs e) { base.OnMouseEnter(e); if (!hot) { hot = true; Invalidate(); } }
        protected override void OnMouseLeave(EventArgs e) { base.OnMouseLeave(e); if (hot) { hot = false; Invalidate(); } }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); Invalidate(); }

        // Paint once on the WM_PAINT DC. Do not call base (which would let the
        // system paint a bright-white box) and never Invalidate from paint.
        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_ERASEBKGND)
            {
                m.Result = (IntPtr)1;
                return;
            }
            if (m.Msg == WM_PAINT)
            {
                ThemedComboBoxPaint(Handle);
                m.Result = IntPtr.Zero;
                return;
            }
            if (m.Msg == WM_PRINTCLIENT && m.WParam != IntPtr.Zero)
            {
                using (Graphics g = Graphics.FromHdc(m.WParam))
                    PaintBoxOn(g);
                return;
            }
            base.WndProc(ref m);
        }

        // BeginPaint/EndPaint live on ThemedComboBox; use the same pattern locally.
        [StructLayout(LayoutKind.Sequential)]
        struct PAINTSTRUCT
        {
            public IntPtr hdc;
            public int fErase;
            public int left, top, right, bottom;
            public int fRestore;
            public int fIncUpdate;
            public int r0, r1, r2, r3, r4, r5, r6, r7;
        }
        [DllImport("user32.dll")]
        static extern IntPtr BeginPaint(IntPtr hWnd, ref PAINTSTRUCT lpPaint);
        [DllImport("user32.dll")]
        static extern bool EndPaint(IntPtr hWnd, ref PAINTSTRUCT lpPaint);

        void ThemedComboBoxPaint(IntPtr hwnd)
        {
            PAINTSTRUCT ps = new PAINTSTRUCT();
            IntPtr hdc = BeginPaint(hwnd, ref ps);
            try
            {
                if (hdc != IntPtr.Zero)
                {
                    using (Graphics g = Graphics.FromHdc(hdc))
                        PaintBoxOn(g);
                }
            }
            finally { EndPaint(hwnd, ref ps); }
        }

        void PaintBoxOn(Graphics dest)
        {
            int width = Width, height = Height;
            IntPtr prev = IntPtr.Zero;
            if (IsHandleCreated)
            {
                prev = DpiWindow.EnterContext(Handle);
                Size px = DpiWindow.ClientPixelSize(Handle);
                if (px.Width > 0) width = px.Width;
                if (px.Height > 0) height = px.Height;
            }
            try
            {
                if (width <= 0 || height <= 0) return;
                using (Bitmap bmp = new Bitmap(width, height))
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    PaintBox(g, width, height);
                    dest.DrawImageUnscaled(bmp, 0, 0);
                }
            }
            finally { DpiWindow.LeaveContext(prev); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            // WM_PAINT is handled in WndProc so the system never paints a white box.
        }

        void PaintBox(Graphics g, int width, int height)
        {
            g.ResetTransform();
            g.PageUnit = GraphicsUnit.Pixel;
            g.Clear(Surface);
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;
            int side = Math.Min(width, height);
            if (side < 8) return;
            float paintScale = side / (float)DesignSide;
            int ring = Math.Max(2, (int)Math.Round(DesignRing * paintScale));
            int boxSide = Math.Max(4, side - 2 * ring - 1);
            Rectangle box = new Rectangle((width - boxSide) / 2, (height - boxSide) / 2, boxSide, boxSide);
            int rad = Math.Max(2, (int)Math.Round(3 * paintScale));
            CheckBoxChrome.DrawBox(g, box, rad, paintScale, Checked, hot, Enabled, DarkChrome, CheckedFill, CheckMark);
            if (Focused && ShowFocusCues)
            {
                Rectangle outer = new Rectangle(0, 0, width - 1, height - 1);
                using (GraphicsPath focus = CheckBoxChrome.Rounded(outer, rad + ring))
                using (Pen pen = new Pen(FocusColor, Math.Max(1f, paintScale)))
                    g.DrawPath(pen, focus);
            }
        }

        static GraphicsPath RoundedBox(Rectangle r, int radius)
        {
            int d = radius * 2;
            GraphicsPath path = new GraphicsPath();
            if (d <= 0 || d > r.Width || d > r.Height)
            {
                path.AddRectangle(r);
                return path;
            }
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }
    }

    // Main-window checkbox (Keep DHCP, Gateway support, Show all adapters).
    // In dark mode the box is owner-drawn with CheckBoxChrome; in light mode
    // the system checkbox is left alone so layout and TextInkRight stay the same.
    // Paint once on the WM_PAINT DC; never Invalidate from paint.
    sealed class ThemedCheckBox : CheckBox
    {
        const int WM_PAINT = 0x000F;
        const int WM_ERASEBKGND = 0x0014;
        const int WM_PRINTCLIENT = 0x0318;

        [StructLayout(LayoutKind.Sequential)]
        struct PAINTSTRUCT
        {
            public IntPtr hdc;
            public int fErase;
            public int left, top, right, bottom;
            public int fRestore;
            public int fIncUpdate;
            public int r0, r1, r2, r3, r4, r5, r6, r7;
        }
        [DllImport("user32.dll")]
        static extern IntPtr BeginPaint(IntPtr hWnd, ref PAINTSTRUCT lpPaint);
        [DllImport("user32.dll")]
        static extern bool EndPaint(IntPtr hWnd, ref PAINTSTRUCT lpPaint);

        public bool UseDarkChrome;
        bool hot;
        int cachedTextLeft = -1;
        int cachedBearing = 0;
        int cacheW, cacheH;
        string cacheText;
        Font cacheFont;

        public void ApplyDark(bool dark)
        {
            UseDarkChrome = dark;
            Invalidate();
        }

        protected override void OnCheckedChanged(EventArgs e) { base.OnCheckedChanged(e); Invalidate(); }
        protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); if (UseDarkChrome) Invalidate(); }
        protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); if (UseDarkChrome) Invalidate(); }
        protected override void OnMouseEnter(EventArgs e)
        {
            base.OnMouseEnter(e);
            if (UseDarkChrome && !hot) { hot = true; Invalidate(); }
        }
        protected override void OnMouseLeave(EventArgs e)
        {
            base.OnMouseLeave(e);
            if (UseDarkChrome && hot) { hot = false; Invalidate(); }
        }
        protected override void OnEnabledChanged(EventArgs e) { base.OnEnabledChanged(e); if (UseDarkChrome) Invalidate(); }
        protected override void OnSizeChanged(EventArgs e) { cachedTextLeft = -1; base.OnSizeChanged(e); }
        protected override void OnFontChanged(EventArgs e) { cachedTextLeft = -1; base.OnFontChanged(e); }
        protected override void OnTextChanged(EventArgs e) { cachedTextLeft = -1; base.OnTextChanged(e); }

        protected override void WndProc(ref Message m)
        {
            if (!UseDarkChrome)
            {
                base.WndProc(ref m);
                return;
            }
            if (m.Msg == WM_ERASEBKGND)
            {
                m.Result = (IntPtr)1;
                return;
            }
            if (m.Msg == WM_PAINT)
            {
                PAINTSTRUCT ps = new PAINTSTRUCT();
                IntPtr hdc = BeginPaint(Handle, ref ps);
                try
                {
                    if (hdc != IntPtr.Zero)
                    {
                        using (Graphics g = Graphics.FromHdc(hdc))
                            PaintChrome(g);
                    }
                }
                finally { EndPaint(Handle, ref ps); }
                m.Result = IntPtr.Zero;
                return;
            }
            if (m.Msg == WM_PRINTCLIENT && m.WParam != IntPtr.Zero)
            {
                using (Graphics g = Graphics.FromHdc(m.WParam))
                    PaintChrome(g);
                return;
            }
            base.WndProc(ref m);
        }

        void PaintChrome(Graphics dest)
        {
            Rectangle client = ClientRectangle;
            if (client.Width <= 0 || client.Height <= 0) return;
            try
            {
                using (Bitmap bmp = new Bitmap(Math.Max(1, client.Width), Math.Max(1, client.Height)))
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.Clear(BackColor);
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                    int glyph = Math.Max(13, (int)Math.Round(13f * (g.DpiX / 96f)));
                    if (glyph > client.Height - 2) glyph = Math.Max(12, client.Height - 2);
                    int boxY = client.Top + (client.Height - glyph) / 2;
                    Rectangle box = new Rectangle(client.Left + 1, boxY, glyph, glyph);
                    int rad = Math.Max(2, (int)Math.Round(2f * g.DpiX / 96f));
                    Color checkFill = Color.FromArgb(0x00, 0x63, 0xB1);
                    CheckBoxChrome.DrawBox(g, box, rad, 1f, Checked, hot, Enabled, true, checkFill, Color.White);
                    int textX = SystemTextOrigin();
                    if (textX < box.Right) textX = box.Right + Math.Max(3, (int)Math.Round(3f * g.DpiX / 96f));
                    Rectangle textBounds = new Rectangle(textX, client.Top, Math.Max(0, client.Right - textX), client.Height);
                    Color fore = Enabled ? ForeColor : Color.FromArgb(150, 150, 150);
                    if (!string.IsNullOrEmpty(Text) && textBounds.Width > 0)
                    {
                        TextRenderer.DrawText(g, Text, Font, textBounds, fore,
                            TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                            TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix |
                            TextFormatFlags.NoPadding);
                    }
                    if (Focused && ShowFocusCues && textBounds.Width > 4)
                    {
                        Size ts = TextRenderer.MeasureText(g, Text ?? "", Font,
                            textBounds.Size, TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
                        Rectangle focus = new Rectangle(textBounds.X, textBounds.Y + Math.Max(0, (textBounds.Height - ts.Height) / 2),
                            Math.Min(ts.Width + 2, textBounds.Width), ts.Height);
                        ControlPaint.DrawFocusRectangle(g, focus, fore, BackColor);
                    }
                    dest.DrawImageUnscaled(bmp, 0, 0);
                }
            }
            catch { }
        }

        // Match the system checkbox caption origin so "Gateway support" still
        // lines up with Refresh. Measured off a stock CheckBox of the same size.
        int SystemTextOrigin()
        {
            if (cachedTextLeft >= 0 && cacheW == Width && cacheH == Height
                && cacheText == Text && object.ReferenceEquals(cacheFont, Font))
                return cachedTextLeft - cachedBearing;
            cachedTextLeft = MeasureStockTextLeft();
            cachedBearing = MeasureDrawTextBearing();
            cacheW = Width;
            cacheH = Height;
            cacheText = Text;
            cacheFont = Font;
            if (cachedTextLeft < 0)
                cachedTextLeft = 16;
            return cachedTextLeft - cachedBearing;
        }

        int MeasureStockTextLeft()
        {
            try
            {
                int w = Math.Max(1, Width);
                int h = Math.Max(1, Height);
                using (CheckBox probe = new CheckBox())
                using (Bitmap withText = new Bitmap(w, h))
                using (Bitmap noText = new Bitmap(w, h))
                {
                    probe.Text = Text;
                    probe.Font = Font;
                    probe.Size = new Size(w, h);
                    probe.CheckAlign = CheckAlign;
                    probe.TextAlign = TextAlign;
                    probe.FlatStyle = FlatStyle;
                    probe.BackColor = Color.White;
                    probe.ForeColor = Color.Black;
                    probe.DrawToBitmap(withText, new Rectangle(0, 0, w, h));
                    probe.Text = "";
                    probe.DrawToBitmap(noText, new Rectangle(0, 0, w, h));
                    for (int x = 0; x < w; x++)
                    {
                        for (int y = 0; y < h; y++)
                        {
                            Color a = withText.GetPixel(x, y);
                            Color b = noText.GetPixel(x, y);
                            if ((a.R + a.G + a.B) + 40 < (b.R + b.G + b.B))
                                return x;
                        }
                    }
                }
            }
            catch { }
            return -1;
        }

        int MeasureDrawTextBearing()
        {
            if (string.IsNullOrEmpty(Text) || Width <= 0 || Height <= 0) return 0;
            try
            {
                int w = Math.Max(1, Width);
                int h = Math.Max(1, Height);
                using (Bitmap bmp = new Bitmap(w, h))
                using (Graphics g = Graphics.FromImage(bmp))
                {
                    g.Clear(Color.White);
                    TextRenderer.DrawText(g, Text, Font, new Rectangle(0, 0, w, h), Color.Black,
                        TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                        TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
                    for (int x = 0; x < w; x++)
                    {
                        for (int y = 0; y < h; y++)
                        {
                            Color c = bmp.GetPixel(x, y);
                            if (c.R + c.G + c.B < 765 - 300)
                                return x;
                        }
                    }
                }
            }
            catch { }
            return 0;
        }
    }

    sealed class AboutForm : PerMonitorDialog
    {
        readonly bool dark;
        readonly string updateCheckFile;
        readonly bool checkMatchOnShow;
        readonly Color bodyBack, footerBack, footerRule, fore, muted, quiet, linkColor, linkActive;
        readonly Color secondaryBack, secondaryBorder, secondaryFore, primaryBack, primaryFore;

        readonly PictureBox iconBox;
        readonly Label nameLabel;
        readonly Label versionLabel;
        readonly LinkLabel website;
        readonly Label siteSeparator;
        readonly Label matchLabel;
        readonly Label tagline;
        readonly LinkLabel creditLink;
        readonly LinkLabel sourceLink;
        readonly LinkLabel licenseLink;
        readonly RoundCheckBox launchCheckBox;
        readonly Label launchCheckLabel;
        readonly Label statusLabel;
        readonly Label privacyLabel;
        readonly Panel footer;
        readonly Button checkButton;
        readonly Button closeButton;
        Bitmap iconBitmap;
        readonly List<Font> fonts = new List<Font>();
        readonly List<Font> retiredFonts = new List<Font>();
        Font fontName, fontVersion, fontBody, fontPrivacy, fontStatus, fontButton, fontButtonBold;
        bool layingOut;
        bool settingLaunchCheck;
        UpdateInfo availableUpdate;

        const string PrivacyLine1 = "Each update check sends one anonymous request to the IPbind update server.";
        const string PrivacyLine2 = "It counts only the country the request came from, for a rough guesstimate of the app\u2019s popularity (or lack thereof) for my own curiosity. Your IP address is never stored, and no identifiers, cookies, or telemetry are used.";

        // Fixed design-pixel gaps (Andy, 2.3.136). If the privacy note needs
        // more room, only the window height grows. These are never reduced.
        const int GapAfterTagline = 18;
        const int GapCreditToLicense = 8;
        const int GapLicenseToCheckbox = 16;
        const int GapCheckboxToStatus = 16;
        const int GapAbovePrivacy = 18;
        const int GapPrivacyToFooter = 20;
        const int FooterPadX = 28, FooterPadTop = 16, FooterPadBot = 18, FooterBtnH = 32;

        public AboutForm(Form owner, bool dark, float scale, string updateCheckFile)
        {
            DebugLog.WatchForm(this);
            this.updateCheckFile = updateCheckFile;
            this.dark = dark;
            InitScale(scale);
            Text = "About " + BuildInfo.AppName;
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            try { Icon = owner.Icon; } catch { }

            bool launchEnabled = ReadLaunchCheckEnabled();
            checkMatchOnShow = BuildInfo.IsBeta && launchEnabled;

            if (dark)
            {
                bodyBack = Color.FromArgb(0x2B, 0x2B, 0x2B);
                footerBack = Color.FromArgb(0x20, 0x20, 0x20);
                footerRule = Color.FromArgb(0x1A, 0x1A, 0x1A);
                fore = Color.White;
                muted = Color.FromArgb(0xC8, 0xC8, 0xC8);
                quiet = Color.FromArgb(0x9A, 0x9A, 0x9A);
                linkColor = Color.FromArgb(0x60, 0xCD, 0xFF);
                linkActive = Color.FromArgb(0x82, 0xD8, 0xFF);
                secondaryBack = Color.FromArgb(0x33, 0x33, 0x33);
                secondaryBorder = Color.FromArgb(0x4A, 0x4A, 0x4A);
                secondaryFore = Color.White;
                primaryBack = Color.FromArgb(0x60, 0xCD, 0xFF);
                primaryFore = Color.FromArgb(0x10, 0x10, 0x10);
            }
            else
            {
                bodyBack = Color.White;
                footerBack = Color.FromArgb(0xF3, 0xF3, 0xF3);
                footerRule = Color.FromArgb(0xE5, 0xE5, 0xE5);
                fore = Color.FromArgb(0x1A, 0x1A, 0x1A);
                muted = Color.FromArgb(0x3B, 0x3B, 0x3B);
                quiet = Color.FromArgb(0x6B, 0x6B, 0x6B);
                linkColor = Color.FromArgb(0x00, 0x5F, 0xB8);
                linkActive = Color.FromArgb(0x00, 0x47, 0x8A);
                secondaryBack = Color.White;
                secondaryBorder = Color.FromArgb(0xD0, 0xD0, 0xD0);
                secondaryFore = Color.FromArgb(0x1A, 0x1A, 0x1A);
                primaryBack = Color.FromArgb(0x00, 0x5F, 0xB8);
                primaryFore = Color.White;
            }

            BackColor = bodyBack;
            ForeColor = fore;

            iconBox = new PictureBox();
            iconBox.BackColor = Color.Transparent;
            iconBox.SizeMode = PictureBoxSizeMode.Normal;
            iconBox.TabStop = false;
            nameLabel = MakeLabel();
            nameLabel.Text = BuildInfo.AppName;
            versionLabel = MakeLabel();
            versionLabel.Text = "Version " + BuildInfo.DisplayVersion;
            website = MakeLink("ipbind.com", "ipbind.com", "https://ipbind.com/");
            matchLabel = MakeLabel();
            matchLabel.Text = "Matches the current release";
            matchLabel.Visible = false;
            tagline = MakeLabel();
            tagline.Text = "One-click Static IP Binder for easily accessing air-gapped equipment spread across multiple IP ranges";
            creditLink = MakeLink("App created by Andy Rostad", "Andy Rostad", "https://github.com/arostad");
            sourceLink = MakeLink("GitHub project page", "GitHub project page", "https://github.com/arostad/IPbind");
            siteSeparator = MakeLabel();
            siteSeparator.Text = "\u00B7";
            siteSeparator.AccessibleRole = AccessibleRole.Separator;
            licenseLink = MakeLink("Released under the MIT License", "MIT License",
                "https://github.com/arostad/IPbind/blob/main/LICENSE");

            launchCheckBox = new RoundCheckBox();
            launchCheckBox.UseMnemonic = false;
            launchCheckBox.AutoSize = false;
            launchCheckBox.Checked = launchEnabled;
            // Shown in installed and portable copies alike, whatever was picked at install.
            launchCheckBox.Visible = true;
            launchCheckBox.CheckedChanged += LaunchCheckChanged;
            launchCheckLabel = MakeLabel();
            launchCheckLabel.Text = "Check for updates at launch (recommended)";
            launchCheckLabel.UseMnemonic = false;
            launchCheckLabel.Cursor = Cursors.Hand;
            launchCheckLabel.Click += delegate { launchCheckBox.Checked = !launchCheckBox.Checked; };
            launchCheckBox.AccessibleName = launchCheckLabel.Text;

            statusLabel = MakeLabel();
            statusLabel.Text = "";
            privacyLabel = MakeLabel();
            privacyLabel.Text = PrivacyLine1 + "\r\n" + PrivacyLine2;

            footer = new Panel();
            footer.BackColor = footerBack;
            footer.Paint += delegate(object sender, PaintEventArgs e)
            {
                using (Pen pen = new Pen(footerRule))
                    e.Graphics.DrawLine(pen, 0, 0, footer.Width, 0);
            };
            checkButton = new Button();
            checkButton.Text = "Check for updates";
            checkButton.FlatStyle = FlatStyle.Flat;
            checkButton.FlatAppearance.BorderSize = 1;
            checkButton.Cursor = Cursors.Hand;
            checkButton.Click += CheckUpdates;
            closeButton = new Button();
            closeButton.Text = "Close";
            closeButton.FlatStyle = FlatStyle.Flat;
            closeButton.FlatAppearance.BorderSize = 1;
            closeButton.Cursor = Cursors.Hand;
            closeButton.DialogResult = DialogResult.OK;
            footer.Controls.Add(checkButton);
            footer.Controls.Add(closeButton);
            AcceptButton = closeButton;
            CancelButton = closeButton;

            Controls.Add(iconBox);
            Controls.Add(nameLabel);
            Controls.Add(versionLabel);
            Controls.Add(website);
            Controls.Add(siteSeparator);
            Controls.Add(sourceLink);
            Controls.Add(matchLabel);
            Controls.Add(tagline);
            Controls.Add(creditLink);
            Controls.Add(licenseLink);
            Controls.Add(launchCheckBox);
            Controls.Add(launchCheckLabel);
            Controls.Add(statusLabel);
            Controls.Add(privacyLabel);
            Controls.Add(footer);

            LayoutContent();
            Shown += delegate
            {
                FitOnScreen();
                if (checkMatchOnShow) StartReleaseMatchCheck();
            };
        }

        Label MakeLabel()
        {
            Label label = new Label();
            label.AutoSize = false;
            label.UseMnemonic = false;
            label.BackColor = Color.Transparent;
            return label;
        }

        LinkLabel MakeLink(string text, string linkedText, string url)
        {
            LinkLabel label = new LinkLabel();
            label.AutoSize = false;
            label.UseMnemonic = false;
            label.BackColor = Color.Transparent;
            label.Text = text;
            label.LinkBehavior = LinkBehavior.AlwaysUnderline;
            int start = text.IndexOf(linkedText, StringComparison.Ordinal);
            if (start < 0) start = 0;
            label.LinkArea = new LinkArea(start, linkedText.Length);
            Uri safeUrl;
            label.Links[0].LinkData = TryGetHttpsUrl(url, out safeUrl) ? safeUrl.AbsoluteUri : url;
            if (url != null && url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            {
                label.LinkClicked += delegate(object sender, LinkLabelLinkClickedEventArgs e)
                {
                    try
                    {
                        Uri destination;
                        if (!TryGetHttpsUrl(e.Link.LinkData as string, out destination))
                            return;
                        ProcessStartInfo info = new ProcessStartInfo(destination.AbsoluteUri);
                        info.UseShellExecute = true;
                        Process.Start(info);
                    }
                    catch { }
                };
            }
            return label;
        }

        Font MakeFont(float px, FontStyle style)
        {
            Font font = NewUiFont(px * sc, style, false);
            fonts.Add(font);
            return font;
        }

        Font MakeSemibold(float px)
        {
            Font font = NewUiFont(px * sc, FontStyle.Regular, true);
            fonts.Add(font);
            return font;
        }

        const TextFormatFlags WrapFlags = TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl |
            TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;
        const TextFormatFlags LineFlags = TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding |
            TextFormatFlags.SingleLine;

        Size MeasureWrap(string text, Font font, int maxWidth)
        {
            using (Graphics g = Graphics.FromHwnd(IntPtr.Zero))
            {
                Size size = TextRenderer.MeasureText(g, text, font, new Size(maxWidth, int.MaxValue), WrapFlags);
                if (size.Height < font.Height) size.Height = font.Height;
                return size;
            }
        }

        Size MeasureLine(string text, Font font)
        {
            using (Graphics g = Graphics.FromHwnd(IntPtr.Zero))
            {
                Size size = TextRenderer.MeasureText(g, text, font, new Size(int.MaxValue, int.MaxValue), LineFlags);
                if (size.Height < font.Height) size.Height = font.Height;
                return size;
            }
        }


        int LinkWidth(string text, Font font)
        {
            // NoPadding is tight; LinkLabel wraps (and our single-line height
            // then clips the second line) if the width is even 1 CSS px short.
            return MeasureLine(text, font).Width + U(16);
        }

        // Glyph centered on the first line of the label, same pattern as the
        // DHCP reminder. The caption lives on a Label so it uses the same GDI
        // TextRenderer as the rest of About (and stays clickable).
        int LayoutLaunchCheck(int x, int y, int textW, int chkBox, Size textNeed)
        {
            int firstH = MeasureLine(launchCheckLabel.Text ?? "Ag", launchCheckLabel.Font).Height;
            int rowH = Math.Max(chkBox, textNeed.Height + U(6));
            launchCheckLabel.Location = new Point(x + chkBox + U(12), y);
            launchCheckLabel.Size = new Size(textW, rowH);
            int boxY = y + (firstH - chkBox) / 2;
            int ring = RoundCheckBox.RingFor(sc);
            launchCheckBox.Size = new Size(chkBox + 2 * ring, chkBox + 2 * ring);
            launchCheckBox.Location = new Point(x - ring, boxY - ring);
            launchCheckBox.Visible = true;
            launchCheckLabel.Visible = true;
            return rowH;
        }

        protected override void LayoutContent()
        {
            if (layingOut) return;
            layingOut = true;
            try
            {
                List<Font> previous = new List<Font>(fonts);
                fonts.Clear();
                fontName = MakeSemibold(22f);
                fontVersion = MakeFont(14f, FontStyle.Bold);
                fontBody = MakeFont(14f, FontStyle.Regular);
                fontPrivacy = MakeFont(12f, FontStyle.Regular);
                fontStatus = MakeFont(13f, FontStyle.Regular);
                fontButton = MakeFont(14f, FontStyle.Regular);
                fontButtonBold = MakeSemibold(14f);
                Font = MakeFont(9f, FontStyle.Regular);

                nameLabel.Font = fontName; nameLabel.ForeColor = fore;
                versionLabel.Font = fontVersion; versionLabel.ForeColor = fore;
                website.Font = fontBody;
                StyleLink(website);
                matchLabel.Font = MakeFont(12f, FontStyle.Regular);
                matchLabel.ForeColor = quiet;
                tagline.Font = fontBody; tagline.ForeColor = muted;
                StyleLink(creditLink); creditLink.Font = fontBody; creditLink.ForeColor = fore;
                StyleLink(sourceLink); sourceLink.Font = fontBody;
                siteSeparator.Font = fontBody; siteSeparator.ForeColor = quiet;
                StyleLink(licenseLink); licenseLink.Font = fontBody; licenseLink.ForeColor = fore;
                launchCheckBox.Font = fontBody;
                launchCheckBox.ForeColor = fore;
                launchCheckBox.BackColor = bodyBack;
                launchCheckBox.Text = "";
                launchCheckBox.ApplyDialogColors(bodyBack, fore, primaryBack, primaryFore, dark);
                launchCheckLabel.Font = fontBody;
                launchCheckLabel.ForeColor = fore;
                launchCheckLabel.BackColor = Color.Transparent;
                launchCheckBox.AccessibleName = launchCheckLabel.Text;
                statusLabel.Font = fontStatus; statusLabel.ForeColor = quiet;
                privacyLabel.Font = fontPrivacy; privacyLabel.ForeColor = quiet;
                checkButton.Font = fontButton;
                checkButton.BackColor = secondaryBack;
                checkButton.ForeColor = secondaryFore;
                checkButton.FlatAppearance.BorderColor = secondaryBorder;
                closeButton.Font = fontButtonBold;
                closeButton.BackColor = primaryBack;
                closeButton.ForeColor = primaryFore;
                closeButton.FlatAppearance.BorderColor = primaryBack;

                int iconPx = Math.Max(1, U(64));
                Bitmap newIcon = LoadAppIcon(iconPx);
                Image oldImage = iconBox.Image;
                iconBox.Image = newIcon;
                if (oldImage != null && oldImage != newIcon) oldImage.Dispose();
                iconBitmap = newIcon;

                int width = U(500);
                int padX = U(28);
                int padTop = U(22);
                // The privacy note wraps inside the 500px mockup width. Extra
                // lines make the dialog taller; gaps below stay as named above.
                int inner = width - 2 * padX;
                int y = padTop;

                iconBox.Location = new Point(padX, y);
                iconBox.Size = new Size(iconPx, iconPx);
                int stackX = padX + iconPx + U(16);
                int stackW = width - padX - stackX;
                Size nameSize = MeasureLine(nameLabel.Text, fontName);
                Size verSize = MeasureLine(versionLabel.Text, fontVersion);
                Size siteSize = MeasureLine(website.Text, fontBody);
                int stackH = nameSize.Height + U(2) + verSize.Height + U(2) + siteSize.Height;
                {
                    int lineW = siteSize.Width + 2 * U(8) + MeasureLine(siteSeparator.Text, fontBody).Width
                        + MeasureLine(sourceLink.Text, fontBody).Width;
                    if (lineW + U(4) > stackW)
                        stackH += U(2) + MeasureLine(sourceLink.Text, fontBody).Height;
                }
                if (matchLabel.Visible)
                    stackH += U(2) + MeasureLine(matchLabel.Text, matchLabel.Font).Height;
                int heroH = Math.Max(iconPx, stackH);
                iconBox.Top = y + (heroH - iconPx) / 2;
                int stackY = y + (heroH - stackH) / 2;
                nameLabel.Location = new Point(stackX, stackY);
                nameLabel.Size = new Size(stackW, nameSize.Height);
                versionLabel.Location = new Point(stackX, nameLabel.Bottom + U(2));
                versionLabel.Size = new Size(stackW, verSize.Height);
                // "ipbind.com  ·  GitHub project page" on one line; the GitHub link
                // drops under the website only if the line would not fit.
                Size srcSize = MeasureLine(sourceLink.Text, fontBody);
                Size sepSize = MeasureLine(siteSeparator.Text, fontBody);
                int sepGap = U(8);
                int oneLineW = siteSize.Width + sepGap + sepSize.Width + sepGap + srcSize.Width;
                bool siteOneLine = oneLineW + U(4) <= stackW;
                website.Location = new Point(stackX, versionLabel.Bottom + U(2));
                // LinkLabel needs slack or it wraps; the slack sits under the separator.
                website.Size = new Size(Math.Min(stackW, siteSize.Width + U(16)), siteSize.Height);
                int siteBottom = website.Bottom;
                if (siteOneLine)
                {
                    siteSeparator.Visible = true;
                    siteSeparator.Location = new Point(stackX + siteSize.Width + sepGap, website.Top);
                    siteSeparator.Size = new Size(sepSize.Width + U(16), siteSize.Height);
                    int srcX = siteSeparator.Left + sepSize.Width + sepGap;
                    sourceLink.Location = new Point(srcX, website.Top);
                    sourceLink.Size = new Size(Math.Max(1, Math.Min(stackX + stackW - srcX, LinkWidth(sourceLink.Text, fontBody))), srcSize.Height);
                    siteSeparator.BringToFront();
                    sourceLink.BringToFront();
                }
                else
                {
                    siteSeparator.Visible = false;
                    sourceLink.Location = new Point(stackX, website.Bottom + U(2));
                    sourceLink.Size = new Size(Math.Min(stackW, LinkWidth(sourceLink.Text, fontBody)), srcSize.Height);
                    siteBottom = sourceLink.Bottom;
                }
                int afterHero = Math.Max(iconBox.Bottom, siteBottom);
                if (matchLabel.Visible)
                {
                    Size matchSize = MeasureLine(matchLabel.Text, matchLabel.Font);
                    matchLabel.Location = new Point(stackX, siteBottom + U(2));
                    matchLabel.Size = new Size(stackW, matchSize.Height);
                    afterHero = Math.Max(iconBox.Bottom, matchLabel.Bottom);
                }
                y = afterHero + U(16);

                Size tagSize = MeasureWrap(tagline.Text, fontBody, inner);
                tagline.Location = new Point(padX, y);
                tagline.Size = new Size(inner, tagSize.Height);
                y = tagline.Bottom + U(GapAfterTagline);

                // Credit and license stacked, left-aligned, nothing to their right.
                Size creditNeed = MeasureLine(creditLink.Text, fontBody);
                creditLink.Location = new Point(padX, y);
                creditLink.Size = new Size(Math.Min(inner, LinkWidth(creditLink.Text, fontBody)), creditNeed.Height);
                y = creditLink.Bottom + U(GapCreditToLicense);
                Size licNeed = MeasureLine(licenseLink.Text, fontBody);
                licenseLink.Location = new Point(padX, y);
                licenseLink.Size = new Size(Math.Min(inner, LinkWidth(licenseLink.Text, fontBody)), licNeed.Height);
                y = licenseLink.Bottom;

                y += U(GapLicenseToCheckbox);
                int chkBox = U(20);
                Size boxNeed = MeasureWrap(launchCheckLabel.Text, fontBody, inner - chkBox - U(12));
                y += LayoutLaunchCheck(padX, y, inner - chkBox - U(12), chkBox, boxNeed);
                if (!string.IsNullOrEmpty(statusLabel.Text))
                {
                    y += U(GapCheckboxToStatus);
                    Size st = MeasureWrap(statusLabel.Text, fontStatus, inner);
                    statusLabel.Location = new Point(padX, y);
                    statusLabel.Size = new Size(inner, st.Height);
                    statusLabel.Visible = true;
                    y = statusLabel.Bottom;
                }
                else
                {
                    statusLabel.Visible = false;
                    statusLabel.Size = new Size(inner, 0);
                }

                y += U(GapAbovePrivacy);
                Size priv = MeasureWrap(privacyLabel.Text, fontPrivacy, inner);
                privacyLabel.Location = new Point(padX, y);
                privacyLabel.Size = new Size(inner, priv.Height);
                y = privacyLabel.Bottom + U(GapPrivacyToFooter);

                int footerPadX = U(FooterPadX);
                int footerPadTop = U(FooterPadTop);
                int footerPadBot = U(FooterPadBot);
                int btnH = U(FooterBtnH);
                Size checkNeed = MeasureLine(checkButton.Text, fontButton);
                Size closeNeed = MeasureLine(closeButton.Text, fontButtonBold);
                int checkW = Math.Max(U(96), checkNeed.Width + U(32));
                int closeW = Math.Max(U(96), closeNeed.Width + U(32));
                int footerH = footerPadTop + btnH + footerPadBot;
                footer.Location = new Point(0, y);
                footer.Size = new Size(width, footerH);
                checkButton.Size = new Size(checkW, btnH);
                closeButton.Size = new Size(closeW, btnH);
                checkButton.Location = new Point(footerPadX, footerPadTop);
                closeButton.Location = new Point(width - footerPadX - closeW, footerPadTop);

                SetLayoutClientSize(new Size(width, y + footerH));

                // Keep the previous fonts alive until the form is disposed.
                // Disposing here can paint a button with a freed Font
                // (GDI+ "Parameter is not valid") after a DPI change.
                retiredFonts.AddRange(previous);
            }
            finally { layingOut = false; }
        }

        void StyleLink(LinkLabel label)
        {
            label.LinkColor = linkColor;
            label.ActiveLinkColor = linkActive;
            label.VisitedLinkColor = linkColor;
            label.DisabledLinkColor = quiet;
        }

        void SetStatus(string text)
        {
            statusLabel.Text = text ?? "";
            LayoutContent();
        }

        Bitmap LoadAppIcon(int px)
        {
            try
            {
                Stream stream = typeof(AboutForm).Assembly.GetManifestResourceStream("IPbind.app.ico");
                if (stream != null)
                {
                    using (stream)
                    {
                        Bitmap frame = LoadBestIconFrame(stream, px);
                        if (frame != null)
                            return ScaleIconFrame(frame, px);
                    }
                }
            }
            catch { }
            try
            {
                Icon src = Icon ?? Icon.ExtractAssociatedIcon(Application.ExecutablePath);
                if (src != null)
                {
                    using (Icon sized = new Icon(src, px, px))
                    using (Bitmap raw = sized.ToBitmap())
                        return ScaleIconFrame(new Bitmap(raw), px);
                }
            }
            catch { }
            return new Bitmap(px, px);
        }

        Bitmap ScaleIconFrame(Bitmap frame, int px)
        {
            if (frame.Width == px && frame.Height == px)
                return frame;
            Bitmap dest = new Bitmap(px, px);
            dest.SetResolution(96f * sc, 96f * sc);
            using (Graphics g = Graphics.FromImage(dest))
            {
                g.Clear(Color.Transparent);
                g.InterpolationMode = InterpolationMode.HighQualityBicubic;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                g.SmoothingMode = SmoothingMode.HighQuality;
                g.CompositingQuality = CompositingQuality.HighQuality;
                g.DrawImage(frame, 0, 0, px, px);
            }
            frame.Dispose();
            return dest;
        }

        // IPbind's .ico frames are PNG-compressed; System.Drawing Icon often
        // fails to decode those and returns a generic bitmap instead.
        static Bitmap LoadBestIconFrame(Stream stream, int px)
        {
            byte[] data;
            using (MemoryStream copy = new MemoryStream())
            {
                stream.CopyTo(copy);
                data = copy.ToArray();
            }
            if (data.Length < 6) return null;
            if (BitConverter.ToUInt16(data, 2) != 1) return null;
            int count = BitConverter.ToUInt16(data, 4);
            if (count <= 0) return null;

            int bestOff = 0, bestSize = 0, bestW = -1, bestScore = int.MaxValue;
            for (int i = 0; i < count; i++)
            {
                int dir = 6 + i * 16;
                if (dir + 16 > data.Length) break;
                int w = data[dir];
                if (w == 0) w = 256;
                int size = BitConverter.ToInt32(data, dir + 8);
                int off = BitConverter.ToInt32(data, dir + 12);
                int score = (w >= px) ? (w - px) : (10000 + (px - w));
                if (score < bestScore || (score == bestScore && w > bestW))
                {
                    bestScore = score;
                    bestW = w;
                    bestOff = off;
                    bestSize = size;
                }
            }
            if (bestSize <= 0 || bestOff < 0 || bestOff + bestSize > data.Length)
                return null;

            byte[] blob = new byte[bestSize];
            Buffer.BlockCopy(data, bestOff, blob, 0, bestSize);
            if (blob.Length >= 8 && blob[0] == 0x89 && blob[1] == 0x50
                && blob[2] == 0x4E && blob[3] == 0x47)
            {
                using (MemoryStream png = new MemoryStream(blob))
                using (Image img = Image.FromStream(png))
                    return new Bitmap(img);
            }
            return null;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            WindowTheme.ApplyTitleBar(this, dark);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                foreach (Font font in fonts) font.Dispose();
                fonts.Clear();
                foreach (Font font in retiredFonts) font.Dispose();
                retiredFonts.Clear();
                if (iconBitmap != null)
                {
                    if (iconBox != null) iconBox.Image = null;
                    iconBitmap.Dispose();
                    iconBitmap = null;
                }
            }
            base.Dispose(disposing);
        }

        void StartReleaseMatchCheck()
        {
            if (!BuildInfo.IsBeta) return;
            Thread thread = new Thread(delegate()
            {
                bool match;
                try { match = UpdateChecker.BetaMatchesProdLatest(); }
                catch { match = false; }
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        if (IsDisposed) return;
                        if (matchLabel.Visible == match) return;
                        matchLabel.Visible = match;
                        LayoutContent();
                    });
                }
                catch { }
            });
            thread.IsBackground = true;
            thread.Start();
        }

        bool ReadLaunchCheckEnabled()
        {
            try
            {
                if (!File.Exists(updateCheckFile)) return true;
                return !string.Equals(
                    File.ReadAllText(updateCheckFile).Trim(), "manual", StringComparison.OrdinalIgnoreCase);
            }
            catch { return true; }
        }

        bool WriteLaunchCheckMode(string mode)
        {
            try
            {
                string dir = Path.GetDirectoryName(updateCheckFile);
                if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(updateCheckFile, mode);
                return true;
            }
            catch (Exception ex)
            {
                SetStatus("Could not save the update setting: " + ex.Message);
                return false;
            }
        }

        void SetLaunchCheckSilently(bool value)
        {
            settingLaunchCheck = true;
            try { launchCheckBox.Checked = value; }
            finally { settingLaunchCheck = false; }
        }

        // Update checkbox (installed and portable): turning checks ON needs consent to the installer's
        // privacy text (Cancel reverts); turning them OFF saves "manual" at once.
        void LaunchCheckChanged(object sender, EventArgs e)
        {
            if (settingLaunchCheck) return;
            if (launchCheckBox.Checked)
            {
                bool agreed;
                using (PrivacyConsentForm consent = new PrivacyConsentForm(dark, sc))
                    agreed = consent.ShowDialog(this) == DialogResult.OK;
                if (!agreed || !WriteLaunchCheckMode("auto"))
                {
                    SetLaunchCheckSilently(false);
                    return;
                }
                SetStatus("Updates will now be checked at launch.");
            }
            else
            {
                if (!WriteLaunchCheckMode("manual"))
                {
                    SetLaunchCheckSilently(true);
                    return;
                }
                SetStatus("Automatic update checks are off. Use Check for updates any time.");
            }
        }

        static bool TryGetHttpsUrl(string value, out Uri uri)
        {
            uri = null;
            Uri parsed;
            if (string.IsNullOrWhiteSpace(value)
                || !Uri.TryCreate(value, UriKind.Absolute, out parsed)
                || !string.Equals(
                    parsed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrEmpty(parsed.Host))
                return false;

            uri = parsed;
            return true;
        }

        void CheckUpdates(object sender, EventArgs e)
        {
            StartReleaseMatchCheck();
            checkButton.Enabled = false;
            SetStatus("Checking...");
            Thread thread = new Thread(delegate()
            {
                UpdateInfo info;
                try { info = UpdateChecker.Check(); }
                catch (Exception ex)
                {
                    info = new UpdateInfo
                    {
                        Error = UpdateChecker.DescribeError(ex),
                        IsNewer = false
                    };
                }
                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        if (IsDisposed) return;
                        checkButton.Enabled = true;
                        if (!info.IsNewer)
                        {
                            SetStatus(string.IsNullOrEmpty(info.Error)
                                ? "You're on the latest version (" + BuildInfo.DisplayVersion + ")."
                                : info.Error);
                            return;
                        }

                        availableUpdate = info;
                        SetStatus("Version " + VersionText.ForDisplay(info.RemoteVersion) + " is available.");
                        checkButton.Text = "Update now";
                        checkButton.Click -= CheckUpdates;
                        checkButton.Click += UpdateNow;
                        LayoutContent();
                    });
                }
                catch { }
            });
            thread.IsBackground = true;
            thread.Start();
        }

        void UpdateNow(object sender, EventArgs e)
        {
            if (availableUpdate == null) return;
            checkButton.Enabled = false;
            SetStatus("Downloading. The app will restart when it is ready.");
            Thread thread = new Thread(delegate()
            {
                try
                {
                    UpdateChecker.DownloadAndRestart();
                    try { BeginInvoke((MethodInvoker)delegate { Application.Exit(); }); }
                    catch { }
                }
                catch (Exception ex)
                {
                    try
                    {
                        BeginInvoke((MethodInvoker)delegate
                        {
                            if (IsDisposed) return;
                            SetStatus(UpdateChecker.DescribeError(ex));
                            checkButton.Enabled = true;
                        });
                    }
                    catch { }
                }
            });
            thread.IsBackground = true;
            thread.Start();
        }
    }

    // Shows the installer's privacy text (installer/privacy.txt, embedded at build
    // time as IPbind.privacy.txt so both are identical). "I Agree" returns OK.
    sealed class PrivacyConsentForm : Form
    {
        readonly float sc;
        readonly bool dark;

        public PrivacyConsentForm(bool dark, float scale)
        {
            DebugLog.WatchForm(this);
            this.dark = dark;
            sc = scale > 0f ? scale : 1f;
            Text = BuildInfo.AppName + " privacy";
            Font = new Font("Segoe UI", 9F);
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = Z(520, 400);

            Label intro = new Label();
            intro.Text = "Please read how " + BuildInfo.AppName + " uses the network before turning on update checks at launch.";
            intro.Location = P(16, 12);
            intro.Size = Z(488, 40);
            intro.TextAlign = ContentAlignment.MiddleLeft;

            TextBox text = new TextBox();
            text.Multiline = true;
            text.ReadOnly = true;
            text.WordWrap = true;
            text.ScrollBars = ScrollBars.Vertical;
            text.Location = P(16, 58);
            text.Size = Z(488, 282);
            text.Text = ReadPrivacyText();
            text.TabStop = false;

            Button agree = new Button();
            agree.Text = "I Agree";
            agree.DialogResult = DialogResult.OK;
            agree.Location = P(300, 354);
            agree.Size = Z(100, 32);

            Button cancel = new Button();
            cancel.Text = "Cancel";
            cancel.DialogResult = DialogResult.Cancel;
            cancel.Location = P(406, 354);
            cancel.Size = Z(98, 32);
            CancelButton = cancel;

            Controls.Add(intro);
            Controls.Add(text);
            Controls.Add(agree);
            Controls.Add(cancel);

            if (dark)
            {
                BackColor = Color.FromArgb(32, 32, 32);
                ForeColor = Color.FromArgb(230, 230, 230);
                intro.ForeColor = ForeColor;
                text.BackColor = Color.FromArgb(24, 24, 24);
                text.ForeColor = ForeColor;
                text.BorderStyle = BorderStyle.FixedSingle;
                foreach (Button b in new Button[] { agree, cancel })
                {
                    b.FlatStyle = FlatStyle.Flat;
                    b.BackColor = Color.FromArgb(50, 50, 50);
                    b.ForeColor = ForeColor;
                    b.FlatAppearance.BorderColor = Color.FromArgb(80, 80, 80);
                }
            }
            Shown += delegate { cancel.Focus(); text.Select(0, 0); };
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            WindowTheme.ApplyTitleBar(this, dark);
        }

        int U(int value) { return (int)Math.Round(value * sc); }
        Point P(int x, int y) { return new Point(U(x), U(y)); }
        Size Z(int width, int height) { return new Size(U(width), U(height)); }

        static string ReadPrivacyText()
        {
            try
            {
                using (Stream s = typeof(PrivacyConsentForm).Assembly
                    .GetManifestResourceStream("IPbind.privacy.txt"))
                {
                    if (s != null)
                        using (StreamReader r = new StreamReader(s, Encoding.UTF8, true))
                            return r.ReadToEnd().Replace("\r\n", "\n").Replace("\n", "\r\n").Trim();
                }
            }
            catch { }
            return "The privacy notice could not be loaded. See the Privacy section at https://ipbind.com/#privacy";
        }
    }

    // Shared main-window button captions. The DHCP reminder popup reads these
    // so the intro, replica and acknowledgement always match the real buttons.
    static class UiCopy
    {
        public const string ApplyButton = "Apply Static IPs";
        public const string DhcpButton = "Return to DHCP";
        public static readonly Color ApplyButtonBack = Color.FromArgb(46, 125, 50);
        public static readonly Color DhcpButtonBack = Color.FromArgb(25, 90, 160);
    }

    // Startup reminder: static addresses stay on the adapter until Return to DHCP.
    // Shown at launch until the user clicks Continue with "Don't show this again"
    // ticked (MainForm then writes dhcp-reminder-dismissed.txt in the settings folder).
    // Continue (and Enter) stay inactive until the acknowledgement is ticked. Exit is
    // always available: Exit IPbind, Esc, the title-bar X and Alt+F4 all return Cancel,
    // and MainForm then closes IPbind without touching any adapter or saving the
    // opt-out. Laid out in code from the real DPI scale; fonts, icons and the replica
    // are drawn in pixels at that scale. WM_DPICHANGED / DpiChanged re-lays out the
    // dialog if it moves to a monitor at a different scale. Standard title bar (no
    // WM_NCCALCSIZE). FixedDialog, so DWMWA_BORDER_COLOR-while-maximized does not apply.
    sealed class DhcpReminderForm : PerMonitorDialog
    {
        readonly bool dark;
        readonly Color bodyBack, footerBack, panelBack, panelBorder, fore, muted, introColor, amber, amberInk;
        readonly Color primaryBack, primaryFore, primaryDisabledBack, primaryDisabledBorder, primaryDisabledFore;
        readonly Color secondaryBack, secondaryBorder, secondaryFore;

        readonly VectorIcon warningIcon;
        readonly Label eyebrow;
        readonly Label title;
        readonly RunsLabel intro;
        readonly Panel card;
        readonly ButtonReplica replica;
        readonly Label cardHead;
        readonly Label cardDesc;
        readonly Label lead;
        readonly VectorIcon[] rowIcons;
        readonly Label[] rowHeads;
        readonly Label[] rowDescs;
        readonly Panel footer;
        readonly CheckBox understandBox;
        readonly CheckBox dontShowBox;
        readonly Label understandLabel;
        readonly Label dontShowLabel;
        readonly Button continueButton;
        readonly Button exitButton;

        readonly List<Font> fonts = new List<Font>();
        readonly List<Font> retiredFonts = new List<Font>();
        Font fontEyebrow, fontTitle, fontIntro, fontIntroBold, fontBody, fontBodyBold, fontSmall, fontButton, fontButtonRegular, fontReplica;
        bool layingOut;

        public bool DontShowAgain { get { return dontShowBox.Checked; } }

        public DhcpReminderForm(Form owner, bool dark, float scale)
        {
            DebugLog.WatchForm(this);
            this.dark = dark;
            InitScale(scale);
            Text = BuildInfo.AppName;
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            KeyPreview = true;
            try { Icon = owner.Icon; } catch { }

            if (dark)
            {
                // Spec dark palette; replica uses the real main-window button color.
                bodyBack = Color.FromArgb(0x2B, 0x2B, 0x2B);
                footerBack = Color.FromArgb(0x20, 0x20, 0x20);
                panelBack = Color.FromArgb(0x1F, 0x1F, 0x1F);
                panelBorder = Color.FromArgb(0x3A, 0x3A, 0x3A);
                fore = Color.White;
                introColor = Color.FromArgb(0xE4, 0xE4, 0xE4);
                muted = Color.FromArgb(0xC8, 0xC8, 0xC8);
                amber = Color.FromArgb(0xFF, 0xC8, 0x3D);
                amberInk = Color.FromArgb(0x1F, 0x1F, 0x1F);
                primaryBack = Color.FromArgb(0x60, 0xCD, 0xFF);
                primaryFore = Color.FromArgb(0x10, 0x10, 0x10);
                primaryDisabledBack = Color.FromArgb(0x2A, 0x2A, 0x2A);
                primaryDisabledBorder = Color.FromArgb(0x38, 0x38, 0x38);
                primaryDisabledFore = Color.FromArgb(0x8C, 0x8C, 0x8C);
                secondaryBack = Color.FromArgb(0x33, 0x33, 0x33);
                secondaryBorder = Color.FromArgb(0x4A, 0x4A, 0x4A);
                secondaryFore = Color.White;
            }
            else
            {
                // Light variant: app-like light surfaces, darker amber for contrast.
                bodyBack = Color.FromArgb(0xF5, 0xF5, 0xF5);
                footerBack = Color.FromArgb(0xEB, 0xEB, 0xEB);
                panelBack = Color.White;
                panelBorder = Color.FromArgb(0xC8, 0xC8, 0xC8);
                fore = Color.FromArgb(0x1A, 0x1A, 0x1A);
                introColor = Color.FromArgb(0x2A, 0x2A, 0x2A);
                muted = Color.FromArgb(0x5A, 0x5A, 0x5A);
                amber = Color.FromArgb(156, 92, 0);
                amberInk = Color.White;
                primaryBack = Color.FromArgb(25, 90, 160);
                primaryFore = Color.White;
                primaryDisabledBack = Color.FromArgb(0xE0, 0xE0, 0xE0);
                primaryDisabledBorder = Color.FromArgb(0xC0, 0xC0, 0xC0);
                primaryDisabledFore = Color.FromArgb(0x8A, 0x8A, 0x8A);
                secondaryBack = Color.FromArgb(0xF7, 0xF7, 0xF7);
                secondaryBorder = Color.FromArgb(0xB0, 0xB0, 0xB0);
                secondaryFore = fore;
            }

            BackColor = bodyBack;
            ForeColor = fore;
            Font = MakeFont(9f, FontStyle.Regular);

            warningIcon = new VectorIcon(VectorIcon.Kind.Warning, amber, amberInk, bodyBack);
            eyebrow = MakeLabel();
            eyebrow.Text = "BEFORE YOU START";
            title = MakeLabel();
            title.Text = "Return to DHCP before joining a live network";
            intro = new RunsLabel();
            Controls.Add(warningIcon);
            Controls.Add(eyebrow);
            Controls.Add(title);
            Controls.Add(intro);

            card = new Panel();
            card.BackColor = panelBack;
            card.Paint += PaintCardBorder;
            replica = new ButtonReplica();
            cardHead = MakeLabel();
            cardHead.Text = "Look for this button in the main window";
            cardDesc = MakeLabel();
            cardDesc.Text = "Clicking it removes the addresses you bound and returns your selected interface to DHCP, ready for a live network.";
            card.Controls.Add(replica);
            card.Controls.Add(cardHead);
            card.Controls.Add(cardDesc);
            Controls.Add(card);

            lead = MakeLabel();
            lead.Text = "If you skip it and plug into a live network (office, home, or public):";
            Controls.Add(lead);

            string[] heads = new string[]
            {
                "Your laptop may not have internet",
                "You could knock an existing device offline",
                "You could set off security alerts"
            };
            string[] descs = new string[]
            {
                "No web, email, or network drives until the addresses are removed.",
                "One of your addresses may already belong to something on that network.",
                "Network admins may block your laptop until it\u2019s sorted out."
            };
            VectorIcon.Kind[] kinds = new VectorIcon.Kind[]
            {
                VectorIcon.Kind.NoInternet,
                VectorIcon.Kind.Devices,
                VectorIcon.Kind.Shield
            };
            rowIcons = new VectorIcon[3];
            rowHeads = new Label[3];
            rowDescs = new Label[3];
            for (int i = 0; i < 3; i++)
            {
                rowIcons[i] = new VectorIcon(kinds[i], amber, amberInk, bodyBack);
                rowHeads[i] = MakeLabel();
                rowHeads[i].Text = heads[i];
                rowDescs[i] = MakeLabel();
                rowDescs[i].Text = descs[i];
                Controls.Add(rowIcons[i]);
                Controls.Add(rowHeads[i]);
                Controls.Add(rowDescs[i]);
            }

            footer = new Panel();
            footer.BackColor = footerBack;
            footer.Paint += delegate(object sender, PaintEventArgs e)
            {
                int y = 0;
                using (Pen pen = new Pen(dark ? Color.FromArgb(0x1A, 0x1A, 0x1A) : Color.FromArgb(0xD0, 0xD0, 0xD0)))
                    e.Graphics.DrawLine(pen, 0, y, footer.Width, y);
            };
            understandBox = new RoundCheckBox();
            understandBox.UseMnemonic = false;
            understandBox.AutoSize = false;
            understandBox.UseCompatibleTextRendering = false;
            understandBox.CheckedChanged += delegate { ApplyContinueState(); };
            understandLabel = MakeLabel();
            understandLabel.Click += delegate { understandBox.Checked = !understandBox.Checked; };
            dontShowBox = new RoundCheckBox();
            dontShowBox.UseMnemonic = false;
            dontShowBox.AutoSize = false;
            dontShowBox.UseCompatibleTextRendering = false;
            dontShowLabel = MakeLabel();
            dontShowLabel.Text = "Don\u2019t show this again";
            dontShowLabel.Click += delegate { dontShowBox.Checked = !dontShowBox.Checked; };
            continueButton = new Button();
            continueButton.Text = "Continue";
            continueButton.Enabled = false;
            continueButton.DialogResult = DialogResult.OK;
            continueButton.FlatStyle = FlatStyle.Flat;
            continueButton.FlatAppearance.BorderSize = 1;
            continueButton.Cursor = Cursors.Hand;
            exitButton = new Button();
            exitButton.Text = "Exit IPbind";
            exitButton.DialogResult = DialogResult.Cancel;
            exitButton.FlatStyle = FlatStyle.Flat;
            exitButton.FlatAppearance.BorderSize = 1;
            exitButton.Cursor = Cursors.Hand;
            footer.Controls.Add(understandBox);
            footer.Controls.Add(understandLabel);
            footer.Controls.Add(dontShowBox);
            footer.Controls.Add(dontShowLabel);
            footer.Controls.Add(continueButton);
            footer.Controls.Add(exitButton);
            Controls.Add(footer);
            AcceptButton = continueButton;
            CancelButton = exitButton;

            LayoutContent();
            Shown += delegate
            {
                FitOnScreen();
                understandBox.Focus();
            };
        }

        Label MakeLabel()
        {
            Label label = new Label();
            label.AutoSize = false;
            label.UseMnemonic = false;
            label.BackColor = Color.Transparent;
            return label;
        }

        Font MakeFont(float px, FontStyle style)
        {
            // Spec typography is in CSS pixels at 96 DPI. Multiply by sc so a
            // test scale on a 96-DPI machine and a real system-DPI scale both
            // render at the intended size (not points, which would be 33% larger).
            Font font = NewUiFont(px * sc, style, false);
            fonts.Add(font);
            return font;
        }

        Font MakeSemibold(float px)
        {
            Font font = NewUiFont(px * sc, FontStyle.Regular, true);
            fonts.Add(font);
            return font;
        }

        void DisposeFonts()
        {
            foreach (Font font in fonts) font.Dispose();
            fonts.Clear();
            foreach (Font font in retiredFonts) font.Dispose();
            retiredFonts.Clear();
        }

        const TextFormatFlags WrapFlags = TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl |
            TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;

        Size MeasureWrap(string text, Font font, int maxWidth)
        {
            using (Graphics g = Graphics.FromHwnd(IntPtr.Zero))
            {
                Size size = TextRenderer.MeasureText(g, text, font, new Size(maxWidth, int.MaxValue), WrapFlags);
                if (size.Width < 1) size.Width = maxWidth;
                if (size.Height < font.Height) size.Height = font.Height;
                return size;
            }
        }

        Size MeasureLine(string text, Font font)
        {
            using (Graphics g = Graphics.FromHwnd(IntPtr.Zero))
            {
                Size size = TextRenderer.MeasureText(g, text, font, new Size(int.MaxValue, int.MaxValue),
                    TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
                if (size.Height < font.Height) size.Height = font.Height;
                return size;
            }
        }

        int MaxClientHeight()
        {
            // The real work area of the dialog's monitor, in device pixels. No fixed
            // pixel cap: a 1040 px cap here made a 4K screen at 175% or 200% look
            // like a full 1080p one, so the dialog widened when it already fit.
            Rectangle work = CurrentWorkArea();
            int chromeH = ChromeSize().Height;
            return Math.Max(U(360), work.Height - chromeH - 16);
        }


        protected override void LayoutContent()
        {
            if (layingOut) return;
            layingOut = true;
            try
            {
                List<Font> previousFonts = new List<Font>(fonts);
                fonts.Clear();
                fontEyebrow = MakeSemibold(12f);
                fontTitle = MakeSemibold(22f);
                fontIntro = MakeFont(15f, FontStyle.Regular);
                fontIntroBold = MakeSemibold(15f);
                fontBody = MakeSemibold(15f);
                fontBodyBold = MakeSemibold(15f);
                fontSmall = MakeFont(13f, FontStyle.Regular);
                fontButton = MakeSemibold(14f);
                fontButtonRegular = MakeFont(14f, FontStyle.Regular);
                fontReplica = MakeFont(15f, FontStyle.Bold);
                Font = MakeFont(9f, FontStyle.Regular);

                eyebrow.Font = fontEyebrow;
                eyebrow.ForeColor = amber;
                title.Font = fontTitle;
                title.ForeColor = fore;
                cardHead.Font = fontBody;
                cardHead.ForeColor = fore;
                cardDesc.Font = fontSmall;
                cardDesc.ForeColor = muted;
                lead.Font = MakeFont(14f, FontStyle.Regular);
                lead.ForeColor = muted;
                for (int i = 0; i < 3; i++)
                {
                    rowHeads[i].Font = fontBody;
                    rowHeads[i].ForeColor = fore;
                    rowDescs[i].Font = fontSmall;
                    rowDescs[i].ForeColor = muted;
                }
                Font fontCheck = MakeFont(14f, FontStyle.Regular);
                understandBox.Font = fontCheck;
                understandBox.ForeColor = fore;
                understandBox.BackColor = footerBack;
                understandBox.Text = "";
                StyleCheck(understandBox);
                understandLabel.Font = fontCheck;
                understandLabel.ForeColor = fore;
                understandLabel.BackColor = Color.Transparent;
                understandLabel.Text = "I\u2019ll click " + UiCopy.DhcpButton + " before I plug into a live network";
                understandBox.AccessibleName = understandLabel.Text;
                dontShowBox.Font = fontCheck;
                dontShowBox.ForeColor = muted;
                dontShowBox.BackColor = footerBack;
                dontShowBox.Text = "";
                StyleCheck(dontShowBox);
                dontShowLabel.Font = fontCheck;
                dontShowLabel.ForeColor = muted;
                dontShowLabel.BackColor = Color.Transparent;
                dontShowLabel.Text = "Don\u2019t show this again";
                dontShowBox.AccessibleName = dontShowLabel.Text;
                continueButton.Font = fontButton;
                exitButton.Font = fontButtonRegular;
                exitButton.BackColor = secondaryBack;
                exitButton.ForeColor = secondaryFore;
                exitButton.FlatAppearance.BorderColor = secondaryBorder;
                ApplyContinueState();

                intro.Clear();
                intro.AddRun("The addresses you bind using this app stay on your selected interface until you click the ", fontIntro, introColor);
                intro.AddRun(UiCopy.DhcpButton, fontIntroBold, fore);
                intro.AddRun(" button.", fontIntro, introColor);

                replica.SetCaption(UiCopy.DhcpButton, fontReplica, UiCopy.DhcpButtonBack, Color.White, sc);

                // Tighten the spacing first; if it still does not fit the work area
                // (200% on a 1080p screen), widen the dialog so text wraps less.
                float tighten = 1f;
                int extraW = 0;
                const int maxAttempts = 40;
                for (int attempt = 0; attempt < maxAttempts; attempt++)
                {
                    int padX = LerpPad(32, 20, tighten);
                    int padY = LerpPad(28, 14, tighten);
                    int gap = LerpPad(20, 10, tighten);
                    int listGap = LerpPad(14, 8, tighten);
                    int footerPadX = LerpPad(32, 20, tighten);
                    int footerPadTop = LerpPad(18, 10, tighten);
                    int footerPadBot = LerpPad(22, 12, tighten);
                    int footerGap = LerpPad(16, 8, tighten);
                    int cardPadX = LerpPad(18, 12, tighten);
                    int cardPadY = LerpPad(16, 10, tighten);
                    int cardGap = LerpPad(18, 12, tighten);
                    int width = U(640) + extraW;
                    int maxW = width;
                    {
                        Rectangle work = CurrentWorkArea();
                        int chromeW = ChromeSize().Width;
                        maxW = Math.Max(U(420), work.Width - chromeW - 4);
                    }
                    if (width > maxW) width = maxW;
                    int inner = width - 2 * padX;

                    int y = padY;
                    int iconSize = U(36);
                    warningIcon.SetScale(iconSize, iconSize);
                    Size eyeSize = MeasureLine(eyebrow.Text, fontEyebrow);
                    Size titleSize = MeasureWrap(title.Text, fontTitle, inner - iconSize - U(14));
                    int headTextH = eyeSize.Height + U(2) + titleSize.Height;
                    int headH = Math.Max(iconSize, headTextH);
                    warningIcon.Location = new Point(padX, y + (headH - iconSize) / 2);
                    int textLeft = padX + iconSize + U(14);
                    int textW = inner - iconSize - U(14);
                    eyebrow.Location = new Point(textLeft, y + (headH - headTextH) / 2);
                    eyebrow.Size = new Size(textW, eyeSize.Height);
                    title.Location = new Point(textLeft, eyebrow.Bottom + U(2));
                    title.Size = new Size(textW, titleSize.Height);
                    y += headH + gap;

                    intro.Location = new Point(padX, y);
                    intro.FitWidth(inner);
                    y += intro.Height + gap;

                    replica.Recalculate();
                    Size headNeed = MeasureWrap(cardHead.Text, fontBody, Math.Max(U(80), inner - 2 * cardPadX - replica.Width - cardGap));
                    Size descNeed = MeasureWrap(cardDesc.Text, fontSmall, Math.Max(U(80), inner - 2 * cardPadX - replica.Width - cardGap));
                    int cardTextH = headNeed.Height + U(1) + descNeed.Height;
                    int cardH = Math.Max(replica.Height, cardTextH) + 2 * cardPadY;
                    card.Location = new Point(padX, y);
                    card.Size = new Size(inner, cardH);
                    replica.Location = new Point(cardPadX, cardPadY + (cardH - 2 * cardPadY - replica.Height) / 2);
                    int cardTextX = cardPadX + replica.Width + cardGap;
                    int cardTextW = inner - cardTextX - cardPadX;
                    if (cardTextW < U(80)) cardTextW = U(80);
                    headNeed = MeasureWrap(cardHead.Text, fontBody, cardTextW);
                    descNeed = MeasureWrap(cardDesc.Text, fontSmall, cardTextW);
                    cardTextH = headNeed.Height + U(1) + descNeed.Height;
                    int cardTextY = cardPadY + Math.Max(0, (cardH - 2 * cardPadY - cardTextH) / 2);
                    cardHead.Location = new Point(cardTextX, cardTextY);
                    cardHead.Size = new Size(cardTextW, headNeed.Height);
                    cardDesc.Location = new Point(cardTextX, cardHead.Bottom + U(1));
                    cardDesc.Size = new Size(cardTextW, descNeed.Height);
                    y += cardH + gap;

                    Size leadNeed = MeasureWrap(lead.Text, lead.Font, inner);
                    lead.Location = new Point(padX, y);
                    lead.Size = new Size(inner, leadNeed.Height);
                    y += lead.Height + listGap;

                    int rowIcon = U(22);
                    int rowTextX = padX + rowIcon + U(14);
                    int rowTextW = width - padX - rowTextX;
                    for (int i = 0; i < 3; i++)
                    {
                        rowIcons[i].SetScale(rowIcon, rowIcon);
                        Size rh = MeasureWrap(rowHeads[i].Text, fontBody, rowTextW);
                        Size rd = MeasureWrap(rowDescs[i].Text, fontSmall, rowTextW);
                        int rowH = Math.Max(rowIcon, rh.Height + U(1) + rd.Height);
                        rowIcons[i].Location = new Point(padX, y + U(1));
                        rowHeads[i].Location = new Point(rowTextX, y);
                        rowHeads[i].Size = new Size(rowTextW, rh.Height);
                        rowDescs[i].Location = new Point(rowTextX, rowHeads[i].Bottom + U(1));
                        rowDescs[i].Size = new Size(rowTextW, rd.Height);
                        y += rowH + (i == 2 ? padY : listGap);
                    }

                    int bodyBottom = y;
                    int chkBox = U(20);
                    int btnW = U(150);
                    int btnH = U(40);
                    int ackTextW = width - 2 * footerPadX - chkBox - U(12);
                    int ackH = LayoutCheckRow(understandBox, understandLabel, footerPadX, footerPadTop, ackTextW, chkBox);
                    int againTop = footerPadTop + ackH + U(8);
                    int againH = LayoutCheckRow(dontShowBox, dontShowLabel, footerPadX, againTop, ackTextW, chkBox);
                    int footerH = againTop + againH + footerGap + btnH + footerPadBot;
                    footer.Location = new Point(0, bodyBottom);
                    footer.Size = new Size(width, footerH);
                    int btnY = againTop + againH + footerGap;
                    exitButton.Size = new Size(btnW, btnH);
                    continueButton.Size = new Size(btnW, btnH);
                    exitButton.Location = new Point(width - footerPadX - btnW, btnY);
                    continueButton.Location = new Point(exitButton.Left - U(10) - btnW, btnY);

                    int clientH = bodyBottom + footerH;
                    int maxH = MaxClientHeight();
                    // Widen by at most 240 design px (1520 px at 200%).
                    bool widest = width >= maxW || extraW >= U(240);
                    if (clientH <= maxH || (tighten <= 0.01f && widest) || attempt == maxAttempts - 1)
                    {
                        SetLayoutClientSize(new Size(width, clientH));
                        break;
                    }
                    if (tighten > 0.01f) tighten = Math.Max(0f, tighten - 0.2f);
                    else extraW += U(40);
                }
                // Hold old fonts until Dispose. Freeing them here, while a
                // button paint is still queued, throws GDI+ "Parameter is not valid".
                retiredFonts.AddRange(previousFonts);
            }
            finally { layingOut = false; }
        }

        // Glyph centered on the first line of the label (MiddleLeft when the
        // text is one line; first-line center when it wraps). The caption lives
        // on a Label so it uses the same GDI TextRenderer and weight as body text.
        // The box control is the 20px square plus a focus-ring margin on each side.
        int LayoutCheckRow(CheckBox box, Label caption, int x, int y, int textW, int chkBox)
        {
            Size need = MeasureWrap(caption.Text ?? "", caption.Font, textW);
            int firstH = MeasureLine(caption.Text ?? "Ag", caption.Font).Height;
            int rowH = Math.Max(chkBox, need.Height + U(6));
            caption.Location = new Point(x + chkBox + U(12), y);
            caption.Size = new Size(textW, rowH);
            int boxY = y + (firstH - chkBox) / 2;
            int ring = RoundCheckBox.RingFor(sc);
            box.Size = new Size(chkBox + 2 * ring, chkBox + 2 * ring);
            box.Location = new Point(x - ring, boxY - ring);
            box.CheckAlign = ContentAlignment.MiddleLeft;
            box.TextAlign = ContentAlignment.MiddleLeft;
            return rowH;
        }

        int LerpPad(int full, int min, float tighten)
        {
            return U((int)Math.Round(min + (full - min) * tighten));
        }

        void ApplyContinueState()
        {
            bool on = understandBox.Checked;
            continueButton.Enabled = on;
            if (on)
            {
                continueButton.BackColor = primaryBack;
                continueButton.ForeColor = primaryFore;
                continueButton.FlatAppearance.BorderColor = primaryBack;
                continueButton.Cursor = Cursors.Hand;
            }
            else
            {
                continueButton.BackColor = primaryDisabledBack;
                continueButton.ForeColor = primaryDisabledFore;
                continueButton.FlatAppearance.BorderColor = primaryDisabledBorder;
                continueButton.Cursor = Cursors.Default;
            }
        }

        void PaintCardBorder(object sender, PaintEventArgs e)
        {
            Rectangle r = new Rectangle(0, 0, card.Width - 1, card.Height - 1);
            int rad = Math.Max(1, U(6));
            using (GraphicsPath path = RoundedRect(r, rad))
            using (Pen pen = new Pen(panelBorder))
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                e.Graphics.DrawPath(pen, path);
            }
        }

        static GraphicsPath RoundedRect(Rectangle r, int radius)
        {
            int d = radius * 2;
            GraphicsPath path = new GraphicsPath();
            if (d <= 0 || d > r.Width || d > r.Height)
            {
                path.AddRectangle(r);
                return path;
            }
            path.AddArc(r.X, r.Y, d, d, 180, 90);
            path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
            path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
            path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
            path.CloseFigure();
            return path;
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            WindowTheme.ApplyTitleBar(this, dark);
            // Scale stays at the value from the constructor (MainForm's system
            // DPI, or a test scale). A later move to another monitor is handled
            // by WM_DPICHANGED; do not overwrite a simulated scale here.
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) DisposeFonts();
            base.Dispose(disposing);
        }

        void StyleCheck(CheckBox box)
        {
            RoundCheckBox round = box as RoundCheckBox;
            if (round == null) return;
            round.ApplyDialogColors(footerBack, fore, primaryBack, primaryFore, dark);
        }

        // Non-interactive picture of the real Return to DHCP button: same caption,
        // color and typeface, sized to the text, no focus, hover or click.
        sealed class ButtonReplica : Control
        {
            string caption = "";
            Font font;
            Color fill, text;
            float sc = 1f;

            public ButtonReplica()
            {
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                    ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
                SetStyle(ControlStyles.Selectable, false);
                TabStop = false;
                Cursor = Cursors.Default;
            }

            public void SetCaption(string caption, Font font, Color fill, Color text, float sc)
            {
                this.caption = caption;
                this.font = font;
                this.fill = fill;
                this.text = text;
                this.sc = sc;
                Recalculate();
            }

            public void Recalculate()
            {
                if (font == null)
                {
                    Size = new Size((int)Math.Round(80 * sc), (int)Math.Round(32 * sc));
                    return;
                }
                Size need;
                using (Graphics g = Graphics.FromHwnd(IntPtr.Zero))
                    need = TextRenderer.MeasureText(g, caption, font, new Size(int.MaxValue, int.MaxValue),
                        TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
                // Mockup: a 176x60 button with 15px bold text, centered.
                int padX = (int)Math.Round(18 * sc);
                int padY = (int)Math.Round(10 * sc);
                Size = new Size(Math.Max(need.Width + 2 * padX, (int)Math.Round(176 * sc)),
                    Math.Max(need.Height + 2 * padY, (int)Math.Round(60 * sc)));
                Invalidate();
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
                int rad = Math.Max(1, (int)Math.Round(3 * sc));
                Rectangle r = new Rectangle(0, 0, Width, Height);
                using (GraphicsPath path = RoundedRect(new Rectangle(r.X, r.Y, r.Width - 1, r.Height - 1), rad))
                using (SolidBrush brush = new SolidBrush(fill))
                    e.Graphics.FillPath(brush, path);
                if (font != null)
                    TextRenderer.DrawText(e.Graphics, caption, font, ClientRectangle, text,
                        TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter |
                        TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine);
            }

            protected override void WndProc(ref Message m)
            {
                const int WM_SETCURSOR = 0x0020, WM_MOUSEMOVE = 0x0200, WM_LBUTTONDOWN = 0x0201,
                    WM_LBUTTONUP = 0x0202, WM_LBUTTONDBLCLK = 0x0203;
                if (m.Msg == WM_SETCURSOR || m.Msg == WM_MOUSEMOVE || m.Msg == WM_LBUTTONDOWN ||
                    m.Msg == WM_LBUTTONUP || m.Msg == WM_LBUTTONDBLCLK)
                {
                    if (m.Msg == WM_SETCURSOR) { m.Result = (IntPtr)1; return; }
                    return;
                }
                base.WndProc(ref m);
            }
        }

        sealed class VectorIcon : Control
        {
            public enum Kind { Warning, NoInternet, Devices, Shield }

            readonly Kind kind;
            readonly Color accent, ink, backdrop;

            public VectorIcon(Kind kind, Color accent, Color ink, Color backdrop)
            {
                this.kind = kind;
                this.accent = accent;
                this.ink = ink;
                this.backdrop = backdrop;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer |
                    ControlStyles.UserPaint | ControlStyles.SupportsTransparentBackColor | ControlStyles.ResizeRedraw, true);
                SetStyle(ControlStyles.Selectable, false);
                TabStop = false;
                BackColor = Color.Transparent;
            }

            public void SetScale(int width, int height)
            {
                Size = new Size(width, height);
                Invalidate();
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                Graphics g = e.Graphics;
                g.SmoothingMode = SmoothingMode.AntiAlias;
                g.PixelOffsetMode = PixelOffsetMode.HighQuality;
                if (Width < 2 || Height < 2) return;
                float vb = kind == Kind.Warning ? 36f : 22f;
                g.ScaleTransform(Width / vb, Height / vb);
                switch (kind)
                {
                    case Kind.Warning:
                        using (GraphicsPath tri = ParseSvgPath("M18 4.5L33 31H3L18 4.5z"))
                        using (SolidBrush fill = new SolidBrush(accent))
                        using (Pen stroke = new Pen(accent, 3f) { LineJoin = LineJoin.Round })
                        {
                            g.FillPath(fill, tri);
                            g.DrawPath(stroke, tri);
                        }
                        using (Pen stem = new Pen(ink, 3f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                            g.DrawLine(stem, 18f, 14f, 18f, 22f);
                        using (SolidBrush dot = new SolidBrush(ink))
                            g.FillEllipse(dot, 18f - 1.8f, 26.5f - 1.8f, 3.6f, 3.6f);
                        break;
                    case Kind.NoInternet:
                        using (Pen pen = new Pen(accent, 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                        {
                            g.DrawEllipse(pen, 11f - 8f, 11f - 8f, 16f, 16f);
                            using (GraphicsPath meridians = ParseSvgPath("M3 11h16M11 3c2.6 2.4 2.6 13.6 0 16M11 3c-2.6 2.4-2.6 13.6 0 16"))
                                g.DrawPath(pen, meridians);
                            using (Pen knock = new Pen(backdrop, 4f) { StartCap = LineCap.Round, EndCap = LineCap.Round })
                                g.DrawLine(knock, 4f, 4f, 18f, 18f);
                            g.DrawLine(pen, 4f, 4f, 18f, 18f);
                        }
                        break;
                    case Kind.Devices:
                        using (Pen pen = new Pen(accent, 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                        {
                            DrawRoundRect(g, pen, 2.5f, 4f, 7f, 6f, 1f);
                            DrawRoundRect(g, pen, 12.5f, 4f, 7f, 6f, 1f);
                            using (GraphicsPath stand = ParseSvgPath("M6 10v4h10v-4M11 14v4M8 18h6"))
                                g.DrawPath(pen, stand);
                        }
                        break;
                    case Kind.Shield:
                        using (Pen pen = new Pen(accent, 1.6f) { StartCap = LineCap.Round, EndCap = LineCap.Round, LineJoin = LineJoin.Round })
                        {
                            using (GraphicsPath shield = ParseSvgPath("M11 2.5l7 2.5v5.500c0 4.200-2.900 7.400-7 9-4.100-1.600-7-4.800-7-9V5l7-2.500z"))
                                g.DrawPath(pen, shield);
                            using (GraphicsPath bang = ParseSvgPath("M11 7.5v4.500M11 15v.2"))
                                g.DrawPath(pen, bang);
                        }
                        break;
                }
            }

            static void DrawRoundRect(Graphics g, Pen pen, float x, float y, float w, float h, float r)
            {
                using (GraphicsPath path = new GraphicsPath())
                {
                    float d = r * 2f;
                    path.AddArc(x, y, d, d, 180, 90);
                    path.AddArc(x + w - d, y, d, d, 270, 90);
                    path.AddArc(x + w - d, y + h - d, d, d, 0, 90);
                    path.AddArc(x, y + h - d, d, d, 90, 90);
                    path.CloseFigure();
                    g.DrawPath(pen, path);
                }
            }
        }

        static GraphicsPath ParseSvgPath(string d)
        {
            GraphicsPath path = new GraphicsPath();
            path.FillMode = FillMode.Winding;
            List<object> tokens = new List<object>();
            int i = 0;
            while (i < d.Length)
            {
                char c = d[i];
                if (char.IsWhiteSpace(c) || c == ',') { i++; continue; }
                if (char.IsLetter(c)) { tokens.Add(c); i++; continue; }
                int start = i;
                if (c == '+' || c == '-') i++;
                bool dot = false;
                while (i < d.Length)
                {
                    char n = d[i];
                    if (char.IsDigit(n)) { i++; continue; }
                    if (n == '.' && !dot) { dot = true; i++; continue; }
                    break;
                }
                if (i == start || (i == start + 1 && (d[start] == '+' || d[start] == '-')))
                { i++; continue; }
                float value;
                if (float.TryParse(d.Substring(start, i - start), System.Globalization.NumberStyles.Float,
                    System.Globalization.CultureInfo.InvariantCulture, out value))
                    tokens.Add(value);
            }

            float x = 0, y = 0, sx = 0, sy = 0;
            char cmd = 'M';
            int t = 0;
            while (t < tokens.Count)
            {
                if (tokens[t] is char)
                {
                    cmd = (char)tokens[t];
                    t++;
                    if (cmd == 'z' || cmd == 'Z')
                    {
                        path.CloseFigure();
                        x = sx; y = sy;
                        continue;
                    }
                }
                bool rel = char.IsLower(cmd);
                char op = char.ToUpperInvariant(cmd);
                if (op == 'M' || op == 'L')
                {
                    if (t + 1 >= tokens.Count || !(tokens[t] is float)) break;
                    float nx = (float)tokens[t], ny = (float)tokens[t + 1]; t += 2;
                    if (rel) { nx += x; ny += y; }
                    if (op == 'M') { path.StartFigure(); sx = nx; sy = ny; cmd = rel ? 'l' : 'L'; }
                    else path.AddLine(x, y, nx, ny);
                    x = nx; y = ny;
                }
                else if (op == 'H')
                {
                    if (t >= tokens.Count || !(tokens[t] is float)) break;
                    float nx = (float)tokens[t]; t++;
                    if (rel) nx += x;
                    path.AddLine(x, y, nx, y);
                    x = nx;
                }
                else if (op == 'V')
                {
                    if (t >= tokens.Count || !(tokens[t] is float)) break;
                    float ny = (float)tokens[t]; t++;
                    if (rel) ny += y;
                    path.AddLine(x, y, x, ny);
                    y = ny;
                }
                else if (op == 'C')
                {
                    if (t + 5 >= tokens.Count) break;
                    float x1 = (float)tokens[t], y1 = (float)tokens[t + 1];
                    float x2 = (float)tokens[t + 2], y2 = (float)tokens[t + 3];
                    float x3 = (float)tokens[t + 4], y3 = (float)tokens[t + 5];
                    t += 6;
                    if (rel) { x1 += x; y1 += y; x2 += x; y2 += y; x3 += x; y3 += y; }
                    path.AddBezier(x, y, x1, y1, x2, y2, x3, y3);
                    x = x3; y = y3;
                }
                else break;
            }
            return path;
        }

        // A wrapped line made of differently styled runs (e.g. a bold phrase inside
        // a sentence). Words are measured and drawn with GDI TextRenderer, the same
        // renderer Labels use, so sizes follow the current scale.
        // Measuring MUST use the IDeviceContext overload: on .NET Framework the
        // MeasureText(string, Font, Size, flags) overload drops NoPadding and adds
        // glyph-overhang padding to every word, while DrawText(Graphics, ...) honors
        // NoPadding, so each word was placed several pixels too far right (2.3.125's
        // wide gaps between words).
        sealed class RunsLabel : Control
        {
            sealed class Word
            {
                public string Text;
                public Font Font;
                public Color Color;
                public Point Location;
            }

            readonly List<Word> words = new List<Word>();
            bool pendingSpace;

            const TextFormatFlags Flags = TextFormatFlags.NoPadding | TextFormatFlags.NoPrefix |
                TextFormatFlags.SingleLine;

            public RunsLabel()
            {
                SetStyle(ControlStyles.SupportsTransparentBackColor | ControlStyles.OptimizedDoubleBuffer |
                    ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint, true);
                SetStyle(ControlStyles.Selectable, false);
                BackColor = Color.Transparent;
                TabStop = false;
            }

            public void Clear()
            {
                words.Clear();
                pendingSpace = false;
            }

            public void AddRun(string text, Font font, Color color)
            {
                int i = 0;
                while (i < text.Length)
                {
                    if (text[i] == ' ') { pendingSpace = true; i++; continue; }
                    int start = i;
                    while (i < text.Length && text[i] != ' ') i++;
                    Word word = new Word();
                    word.Text = (pendingSpace && words.Count > 0 ? " " : "") + text.Substring(start, i - start);
                    word.Font = font;
                    word.Color = color;
                    words.Add(word);
                    pendingSpace = false;
                }
            }

            // Lays the words out within maxWidth and sizes the control to fit.
            // Never mutate word.Text: a later FitWidth (the tighten loop) may
            // put a word back on the previous line, and a trimmed leading
            // space would be gone ("clickthe Return to DHCP").
            public void FitWidth(int maxWidth)
            {
                int x = 0, y = 0, lineHeight = 0, widest = 0;
                using (Graphics g = Graphics.FromHwnd(IntPtr.Zero))
                {
                    foreach (Word word in words)
                    {
                        int fullWidth = Measure(g, word.Text, word.Font);
                        if (x > 0 && x + fullWidth > maxWidth)
                        {
                            y += lineHeight;
                            x = 0;
                            lineHeight = 0;
                            fullWidth = Measure(g, DisplayText(word, true), word.Font);
                        }
                        word.Location = new Point(x, y);
                        x += fullWidth;
                        widest = Math.Max(widest, x);
                        lineHeight = Math.Max(lineHeight, word.Font.Height);
                    }
                }
                Size = new Size(Math.Min(maxWidth, Math.Max(widest + 2, 1)), y + lineHeight + 2);
            }

            static string DisplayText(Word word, bool lineStart)
            {
                if (lineStart && word.Text != null && word.Text.Length > 0 && word.Text[0] == ' ')
                    return word.Text.TrimStart(' ');
                return word.Text;
            }

            static int Measure(Graphics g, string text, Font font)
            {
                return TextRenderer.MeasureText(g, text, font, new Size(int.MaxValue, int.MaxValue), Flags).Width;
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                foreach (Word word in words)
                    TextRenderer.DrawText(e.Graphics, DisplayText(word, word.Location.X == 0),
                        word.Font, word.Location, word.Color, Flags);
            }
        }
    }

    // Per-area auto-hide for the console and IP-list vertical bars (2.3.138).
    // Pattern from IXSmon ScrollBarIdleState, implemented as a reserved-gutter
    // overlay so WinForms never toggles ScrollBars / ShowScrollBar (that would
    // change the client width and wrap-shift the text).
    sealed class ScrollBarIdleState
    {
        public static readonly TimeSpan HideAfter = TimeSpan.FromSeconds(5);
        readonly TimeSpan hideAfter;

        public ScrollBarIdleState() : this(DateTime.Now, null) { }

        public ScrollBarIdleState(DateTime now, TimeSpan? hideAfter)
        {
            this.hideAfter = hideAfter.HasValue ? hideAfter.Value : HideAfter;
            Visible = true;
            HideDueAt = now + this.hideAfter;
        }

        public bool Visible { get; private set; }
        public bool PointerOver { get; private set; }
        // Null-equivalent: DateTime.MinValue means no deadline (hovered or already hidden).
        public DateTime HideDueAt { get; private set; }

        public void PointerEntered()
        {
            PointerOver = true;
            Visible = true;
            HideDueAt = DateTime.MinValue;
        }

        public void PointerMoved()
        {
            PointerEntered();
        }

        public void PointerExited(DateTime now)
        {
            PointerOver = false;
            Visible = true;
            HideDueAt = now + hideAfter;
        }

        // Wheel, thumb drag, or keyboard scroll. Does not run for AppendText / auto-scroll.
        public void Scrolled(DateTime now)
        {
            Visible = true;
            HideDueAt = PointerOver ? DateTime.MinValue : now + hideAfter;
        }

        public void Deactivated()
        {
            PointerOver = false;
            Visible = false;
            HideDueAt = DateTime.MinValue;
        }

        // True when Visible flipped. Callers apply only then, never on every tick.
        public bool Tick(DateTime now)
        {
            if (Visible && !PointerOver && HideDueAt != DateTime.MinValue && now >= HideDueAt)
            {
                Visible = false;
                HideDueAt = DateTime.MinValue;
                return true;
            }
            return false;
        }
    }

    static class AutoHideScrollBars
    {
        public static readonly TimeSpan CheckEvery = TimeSpan.FromMilliseconds(250);
        static readonly List<AutoHideScrollBox> areas = new List<AutoHideScrollBox>();
        static System.Windows.Forms.Timer timer;
        static bool filterAdded;

        public static void Register(AutoHideScrollBox box)
        {
            if (box == null || areas.Contains(box)) return;
            areas.Add(box);
            if (timer == null)
            {
                timer = new System.Windows.Forms.Timer();
                timer.Interval = (int)CheckEvery.TotalMilliseconds;
                timer.Tick += OnTick;
                timer.Start();
            }
            if (!filterAdded)
            {
                Application.AddMessageFilter(new WheelFilter());
                filterAdded = true;
            }
            Form form = box.FindForm();
            if (form != null)
            {
                form.Deactivate -= OnDeactivate;
                form.Deactivate += OnDeactivate;
                form.FormClosed -= OnFormClosed;
                form.FormClosed += OnFormClosed;
            }
        }

        public static void Unregister(AutoHideScrollBox box)
        {
            areas.Remove(box);
        }

        static void OnTick(object sender, EventArgs e)
        {
            DateTime now = DateTime.Now;
            Point cursor;
            try { cursor = Cursor.Position; }
            catch { return; }
            for (int i = 0; i < areas.Count; i++)
            {
                AutoHideScrollBox box = areas[i];
                if (box.IsDisposed || !box.IsHandleCreated) continue;
                bool over = box.IsDragging || box.AreaContains(cursor);
                bool changed = false;
                if (over && !box.State.PointerOver)
                {
                    box.State.PointerEntered();
                    changed = true;
                }
                else if (!over && box.State.PointerOver)
                {
                    box.State.PointerExited(now);
                    changed = true;
                }
                if (box.State.Tick(now)) changed = true;
                if (changed) box.ApplyVisibility();
            }
        }

        static void OnDeactivate(object sender, EventArgs e)
        {
            for (int i = 0; i < areas.Count; i++)
            {
                AutoHideScrollBox box = areas[i];
                if (box.IsDisposed) continue;
                box.State.Deactivated();
                box.ApplyVisibility();
            }
        }

        static void OnFormClosed(object sender, EventArgs e)
        {
            Form form = sender as Form;
            if (form == null) return;
            form.Deactivate -= OnDeactivate;
            form.FormClosed -= OnFormClosed;
            for (int i = areas.Count - 1; i >= 0; i--)
            {
                if (areas[i].FindForm() == form) areas.RemoveAt(i);
            }
        }

        sealed class WheelFilter : IMessageFilter
        {
            const int WM_MOUSEWHEEL = 0x020A;

            public bool PreFilterMessage(ref Message m)
            {
                if (m.Msg != WM_MOUSEWHEEL) return false;
                Point cursor;
                try { cursor = Cursor.Position; }
                catch { return false; }
                for (int i = 0; i < areas.Count; i++)
                {
                    AutoHideScrollBox box = areas[i];
                    if (box.IsDisposed || !box.IsHandleCreated) continue;
                    if (!box.AreaContains(cursor)) continue;
                    box.HandleWheel(unchecked((int)(m.WParam.ToInt64() & 0xFFFFFFFF)));
                    return true;
                }
                return false;
            }
        }
    }

    // Multiline text box with the native vertical bar off and a thin overlay in a
    // permanently reserved right margin. Hover / wheel / keyboard / thumb-drag are
    // the only inputs that reset the hide deadline.
    sealed class AutoHideScrollBox : TextBox
    {
        const int WM_KEYDOWN = 0x0100;
        const int WM_SETREDRAW = 0x000B;
        const int EM_SETMARGINS = 0x00D3;
        const int EM_GETRECT = 0x00B2;
        const int EM_LINESCROLL = 0x00B6;
        const int EM_GETLINECOUNT = 0x00BA;
        const int EM_GETFIRSTVISIBLELINE = 0x00CE;
        const int EC_RIGHTMARGIN = 0x0002;
        const int GWL_STYLE = -16;
        const int ES_AUTOVSCROLL = 0x0040;
        const int WS_VSCROLL = 0x00200000;

        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll")]
        static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll", EntryPoint = "SendMessage", CharSet = CharSet.Auto)]
        static extern IntPtr SendMessageRect(IntPtr hWnd, int msg, IntPtr wParam, ref RECT lParam);
        [DllImport("user32.dll")]
        static extern int GetDpiForWindow(IntPtr hwnd);
        [DllImport("user32.dll", EntryPoint = "GetWindowLong")]
        static extern int GetWindowLong32(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll", EntryPoint = "SetWindowLong")]
        static extern int SetWindowLong32(IntPtr hWnd, int nIndex, int dwNewLong);
        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtr")]
        static extern IntPtr GetWindowLongPtr64(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtr")]
        static extern IntPtr SetWindowLongPtr64(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        public readonly ScrollBarIdleState State = new ScrollBarIdleState();
        readonly OverlayBar overlay;
        int gutter = 10;
        int lastPaintedVisible = -1;
        int lastFirstLine = -1;
        string areaName = "";
        public int DebugPaintCount;
        public bool IsDragging;

        public AutoHideScrollBox()
        {
            Multiline = true;
            ScrollBars = ScrollBars.None;
            overlay = new OverlayBar(this);
        }

        public void SetAreaName(string name) { areaName = name ?? ""; }

        public bool BarShown
        {
            get { return State.Visible && CanScroll(); }
        }

        public int GutterWidth { get { return gutter; } }

        public Rectangle OverlayScreenRect
        {
            get
            {
                if (overlay == null || !overlay.IsHandleCreated)
                    return Rectangle.Empty;
                return overlay.RectangleToScreen(overlay.ClientRectangle);
            }
        }

        public Rectangle ThumbScreenRect
        {
            get
            {
                Rectangle local = overlay.ThumbRect();
                if (local.IsEmpty || !overlay.IsHandleCreated) return Rectangle.Empty;
                return overlay.RectangleToScreen(local);
            }
        }

        public bool AreaContains(Point screen)
        {
            if (!IsHandleCreated) return false;
            if (RectangleToScreen(ClientRectangle).Contains(screen)) return true;
            if (overlay != null && overlay.IsHandleCreated
                && overlay.RectangleToScreen(overlay.ClientRectangle).Contains(screen))
                return true;
            return false;
        }

        public bool CanScroll()
        {
            return MaxFirstLine() > 0;
        }

        public int FirstVisibleLine()
        {
            if (!IsHandleCreated) return 0;
            return (int)SendMessage(Handle, EM_GETFIRSTVISIBLELINE, IntPtr.Zero, IntPtr.Zero);
        }

        public int LineCount()
        {
            if (!IsHandleCreated) return 0;
            return (int)SendMessage(Handle, EM_GETLINECOUNT, IntPtr.Zero, IntPtr.Zero);
        }

        public int VisibleLineCount()
        {
            if (!IsHandleCreated) return 1;
            RECT r = new RECT();
            SendMessageRect(Handle, EM_GETRECT, IntPtr.Zero, ref r);
            int h = r.Bottom - r.Top;
            int lh = Font != null ? Font.Height : 16;
            if (lh < 1) lh = 16;
            return Math.Max(1, h / lh);
        }

        public int MaxFirstLine()
        {
            return Math.Max(0, LineCount() - VisibleLineCount());
        }

        public void LineScroll(int lines)
        {
            if (!IsHandleCreated || lines == 0) return;
            SendMessage(Handle, EM_LINESCROLL, IntPtr.Zero, (IntPtr)lines);
            lastFirstLine = FirstVisibleLine();
        }

        public void ScrollToFirst(int first)
        {
            int max = MaxFirstLine();
            if (first < 0) first = 0;
            if (first > max) first = max;
            LineScroll(first - FirstVisibleLine());
        }

        // Update the thumb after AppendText / auto-scroll / a layout change.
        // Must not reset the hide deadline.
        public void SyncThumbOnly()
        {
            lastFirstLine = FirstVisibleLine();
            // Content may flip CanScroll (so the thumb should appear or
            // disappear) without counting as input. ApplyVisibility paints
            // that change; it must not be skipped or the bar can get stuck.
            ApplyVisibility();
            if (overlay != null)
            {
                overlay.BringToFront();
                if (State.Visible) overlay.Invalidate();
            }
        }

        public void HandleWheel(int wParam)
        {
            int delta = (short)((wParam >> 16) & 0xFFFF);
            int notches = delta / 120;
            if (notches == 0) return;
            int per = SystemInformation.MouseWheelScrollLines;
            int lines;
            if (per <= 0) lines = -notches * VisibleLineCount();
            else lines = -notches * per;
            LineScroll(lines);
            State.Scrolled(DateTime.Now);
            ApplyVisibility();
            SyncThumbOnly();
        }

        public void ApplyVisibility()
        {
            bool shown = BarShown;
            int flag = shown ? 1 : 0;
            if (flag != lastPaintedVisible)
            {
                lastPaintedVisible = flag;
                if (areaName.Length > 0)
                    DebugLog.Debug("scrollbar " + areaName + (shown ? " shown" : " hidden"));
                if (overlay != null)
                {
                    overlay.BringToFront();
                    overlay.Invalidate();
                }
            }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ScrollBars = ScrollBars.None;
            EnsureAutoVScroll();
            ApplyMargin();
            AutoHideScrollBars.Register(this);
            AttachOverlay();
            ApplyVisibility();
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            AutoHideScrollBars.Unregister(this);
            base.OnHandleDestroyed(e);
        }

        protected override void OnParentChanged(EventArgs e)
        {
            base.OnParentChanged(e);
            AttachOverlay();
        }

        protected override void OnLocationChanged(EventArgs e)
        {
            base.OnLocationChanged(e);
            PositionOverlay();
        }

        protected override void OnSizeChanged(EventArgs e)
        {
            base.OnSizeChanged(e);
            ApplyMargin();
            PositionOverlay();
            SyncThumbOnly();
        }

        protected override void OnFontChanged(EventArgs e)
        {
            base.OnFontChanged(e);
            ApplyMargin();
            PositionOverlay();
            SyncThumbOnly();
        }

        protected override void OnBackColorChanged(EventArgs e)
        {
            base.OnBackColorChanged(e);
            if (overlay != null) overlay.Invalidate();
        }

        void AttachOverlay()
        {
            if (overlay == null || Parent == null) return;
            if (overlay.Parent != Parent) Parent.Controls.Add(overlay);
            PositionOverlay();
            overlay.BringToFront();
            if (IsHandleCreated) AutoHideScrollBars.Register(this);
        }

        void PositionOverlay()
        {
            if (overlay == null || Parent == null) return;
            int frameX = Math.Max(0, (Width - ClientSize.Width) / 2);
            int frameY = Math.Max(0, (Height - ClientSize.Height) / 2);
            int w = Math.Max(1, gutter);
            overlay.SetBounds(
                Left + frameX + Math.Max(0, ClientSize.Width - w),
                Top + frameY,
                w,
                Math.Max(1, ClientSize.Height));
            overlay.BringToFront();
        }

        void ApplyMargin()
        {
            int dpi = 96;
            try
            {
                if (IsHandleCreated) dpi = GetDpiForWindow(Handle);
            }
            catch
            {
                try { using (Graphics g = CreateGraphics()) dpi = (int)Math.Round(g.DpiX); }
                catch { dpi = 96; }
            }
            if (dpi < 96) dpi = 96;
            int want = Math.Max(8, (int)Math.Round(10.0 * dpi / 96.0));
            gutter = want;
            if (!IsHandleCreated) return;
            int first = FirstVisibleLine();
            int sel = SelectionStart;
            int len = SelectionLength;
            SendMessage(Handle, WM_SETREDRAW, IntPtr.Zero, IntPtr.Zero);
            SendMessage(Handle, EM_SETMARGINS, (IntPtr)EC_RIGHTMARGIN, (IntPtr)(gutter << 16));
            int now = FirstVisibleLine();
            if (now != first) LineScroll(first - now);
            SendMessage(Handle, WM_SETREDRAW, (IntPtr)1, IntPtr.Zero);
            try
            {
                SelectionStart = sel;
                SelectionLength = len;
            }
            catch { }
            Invalidate();
        }

        void EnsureAutoVScroll()
        {
            if (!IsHandleCreated) return;
            try
            {
                long style = GetStyle(Handle);
                long next = (style | ES_AUTOVSCROLL) & ~WS_VSCROLL;
                if (next != style) SetStyle(Handle, next);
            }
            catch { }
        }

        static long GetStyle(IntPtr hwnd)
        {
            if (IntPtr.Size == 8) return GetWindowLongPtr64(hwnd, GWL_STYLE).ToInt64();
            return GetWindowLong32(hwnd, GWL_STYLE);
        }

        static void SetStyle(IntPtr hwnd, long style)
        {
            if (IntPtr.Size == 8) SetWindowLongPtr64(hwnd, GWL_STYLE, new IntPtr(style));
            else SetWindowLong32(hwnd, GWL_STYLE, (int)style);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_KEYDOWN)
            {
                int before = FirstVisibleLine();
                base.WndProc(ref m);
                int after = FirstVisibleLine();
                if (after == before)
                    after = KeyboardScroll((int)m.WParam.ToInt64(), before);
                if (after != before)
                {
                    lastFirstLine = after;
                    State.Scrolled(DateTime.Now);
                    ApplyVisibility();
                    SyncThumbOnly();
                }
                return;
            }
            base.WndProc(ref m);
        }

        // Read-only boxes (the console) often swallow PageUp/PageDown/Home/End
        // without scrolling once the native bar is gone. Do it ourselves when
        // the base handler left the first visible line unchanged.
        int KeyboardScroll(int key, int before)
        {
            const int VK_PRIOR = 0x21, VK_NEXT = 0x22, VK_END = 0x23, VK_HOME = 0x24;
            const int VK_UP = 0x26, VK_DOWN = 0x28;
            if (key == VK_PRIOR) ScrollToFirst(before - VisibleLineCount());
            else if (key == VK_NEXT) ScrollToFirst(before + VisibleLineCount());
            else if (key == VK_HOME) ScrollToFirst(0);
            else if (key == VK_END) ScrollToFirst(MaxFirstLine());
            else if (key == VK_UP) LineScroll(-1);
            else if (key == VK_DOWN) LineScroll(1);
            else return before;
            return FirstVisibleLine();
        }

        sealed class OverlayBar : Control
        {
            readonly AutoHideScrollBox box;
            bool dragging;
            int dragStartY;
            int dragStartFirst;

            public OverlayBar(AutoHideScrollBox box)
            {
                this.box = box;
                SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.UserPaint
                    | ControlStyles.OptimizedDoubleBuffer | ControlStyles.Selectable, true);
                TabStop = false;
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                box.DebugPaintCount++;
                Graphics g = e.Graphics;
                g.Clear(box.BackColor);
                if (!box.State.Visible || !box.CanScroll()) return;
                Rectangle thumb = ThumbRect();
                if (thumb.IsEmpty) return;
                Color fill = ThumbColor(false);
                if (dragging || (box.State.PointerOver && thumb.Contains(PointToClient(Cursor.Position))))
                    fill = ThumbColor(true);
                using (SolidBrush br = new SolidBrush(fill))
                using (GraphicsPath path = Rounded(thumb, Math.Max(2, thumb.Width / 2)))
                {
                    g.SmoothingMode = SmoothingMode.AntiAlias;
                    g.FillPath(br, path);
                }
            }

            public Rectangle ThumbRect()
            {
                int max = box.MaxFirstLine();
                if (max <= 0 || Height <= 0) return Rectangle.Empty;
                int pad = Math.Max(2, box.GutterWidth / 5);
                int track = Height - 2 * pad;
                if (track <= 0) return Rectangle.Empty;
                int total = box.LineCount();
                int vis = box.VisibleLineCount();
                int thumbH = Math.Max(Math.Max(12, box.GutterWidth), (int)((long)track * vis / Math.Max(1, total)));
                if (thumbH > track) thumbH = track;
                int first = box.FirstVisibleLine();
                int y = pad + (int)((long)(track - thumbH) * first / max);
                int x = Math.Max(1, (Width - Math.Max(4, Width - 2 * pad)) / 2);
                int w = Math.Max(4, Width - 2 * pad);
                return new Rectangle(x, y, w, thumbH);
            }

            Color ThumbColor(bool hot)
            {
                bool dark = box.BackColor.R < 80 && box.BackColor.G < 80 && box.BackColor.B < 80;
                if (dark) return hot ? Color.FromArgb(0x8E, 0x8E, 0x93) : Color.FromArgb(0x6E, 0x6E, 0x73);
                return hot ? Color.FromArgb(0x8E, 0x8E, 0x93) : Color.FromArgb(0xC6, 0xC6, 0xC8);
            }

            static GraphicsPath Rounded(Rectangle r, int radius)
            {
                GraphicsPath path = new GraphicsPath();
                int d = radius * 2;
                if (d <= 0 || d > r.Width || d > r.Height)
                {
                    path.AddRectangle(r);
                    return path;
                }
                path.AddArc(r.X, r.Y, d, d, 180, 90);
                path.AddArc(r.Right - d, r.Y, d, d, 270, 90);
                path.AddArc(r.Right - d, r.Bottom - d, d, d, 0, 90);
                path.AddArc(r.X, r.Bottom - d, d, d, 90, 90);
                path.CloseFigure();
                return path;
            }

            protected override void OnMouseDown(MouseEventArgs e)
            {
                base.OnMouseDown(e);
                if (e.Button != MouseButtons.Left) return;
                if (!box.CanScroll()) return;
                box.State.PointerEntered();
                box.ApplyVisibility();
                Rectangle thumb = ThumbRect();
                if (thumb.Contains(e.Location))
                {
                    dragging = true;
                    box.IsDragging = true;
                    dragStartY = e.Y;
                    dragStartFirst = box.FirstVisibleLine();
                    Capture = true;
                }
                else
                {
                    // Click in the track pages by one viewport.
                    int page = box.VisibleLineCount();
                    if (e.Y < thumb.Top) box.ScrollToFirst(box.FirstVisibleLine() - page);
                    else box.ScrollToFirst(box.FirstVisibleLine() + page);
                    box.State.Scrolled(DateTime.Now);
                    box.ApplyVisibility();
                    box.SyncThumbOnly();
                }
            }

            protected override void OnMouseMove(MouseEventArgs e)
            {
                base.OnMouseMove(e);
                if (!dragging) { Invalidate(); return; }
                int max = box.MaxFirstLine();
                if (max <= 0) return;
                int pad = Math.Max(2, box.GutterWidth / 5);
                int track = Height - 2 * pad - ThumbRect().Height;
                if (track < 1) track = 1;
                int delta = e.Y - dragStartY;
                int first = dragStartFirst + (int)Math.Round((double)delta * max / track);
                box.ScrollToFirst(first);
                box.State.Scrolled(DateTime.Now);
                box.ApplyVisibility();
                Invalidate();
            }

            protected override void OnMouseUp(MouseEventArgs e)
            {
                base.OnMouseUp(e);
                if (!dragging) return;
                dragging = false;
                box.IsDragging = false;
                Capture = false;
                box.State.Scrolled(DateTime.Now);
                box.ApplyVisibility();
                Invalidate();
            }

            protected override void OnMouseWheel(MouseEventArgs e)
            {
                box.HandleWheel(e.Delta << 16);
            }
        }
    }


    class MainForm : Form
    {
        static readonly string[] DefaultIPs = new string[] {
            "10.10.10.98/24",   "10.255.255.98/24", "10.255.128.98/24",
            "192.168.0.98/24",  "192.168.1.98/24",  "192.168.2.98/24",
            "192.168.4.98/24",  "192.168.255.98/24"
        };

        // Every default list IPbind has shipped before the current DefaultIPs (from
        // git history). A saved list that still equals one of these exactly was never
        // edited, so it is upgraded to the current defaults on launch. ADD the old
        // DefaultIPs here whenever the defaults change.
        static readonly string[][] PreviousDefaultSets = new string[][] {
            // 2.3.117 and earlier (all prod releases before 2.3.119)
            new string[] {
                "10.255.255.98/24", "10.255.128.98/24", "192.168.0.98/24",
                "192.168.1.98/24",  "192.168.4.98/24",  "192.168.255.98/24" },
            // briefly on the 2.3.118 development branch (never in a release)
            new string[] {
                "10.10.10.98/24",   "10.255.255.98/24", "10.255.128.98/24",
                "192.168.0.98/24",  "192.168.1.98/24",  "192.168.4.98/24",
                "192.168.255.98/24" }
        };

        static readonly Regex CidrRe =
            new Regex(@"^\s*(\d{1,3})\.(\d{1,3})\.(\d{1,3})\.(\d{1,3})\s*(/\s*(\d{1,2}))?\s*$");
        static readonly Regex VirtualRe =
            new Regex(@"virtual|hyper-?v|vmware|vbox|virtualbox|tap-|tunnel|vpn|loopback|wan miniport|bluetooth|pseudo|wi-?fi|wireless|cellular|wwan",
                      RegexOptions.IgnoreCase);

        readonly string AppDir =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), BuildInfo.AppName);
        readonly string StoreFile;
        readonly string AdapterFile;
        readonly string UpdateCheckFile;
        readonly string GatewayFile;
        readonly string DhcpReminderFile;   // exists once "Don't show this again" was confirmed
        readonly string KeepDhcpFile;       // "1"/"0": Keep DHCP preference (missing = on)

        // Optional Gateway support: one listed address may carry a gateway and DNS.
        // The high metric keeps this route from beating Wi-Fi/corporate default routes.
        const int GatewayMetric = 9999;
        const string IpLabelPlain =
            "IP addresses to bind - one per line, CIDR (e.g. 192.168.1.98/24). No gateway or DNS:";
        const string IpLabelGateway =
            "IP addresses to bind - one per line, CIDR (e.g. 192.168.1.98/24):";

        float sc = 1f;  // DPI scale factor (deviceDpi / 96), applied to all pixel coordinates

        ComboBox combo;
        CheckBox chkAll;
        TextBox txtIPs;
        Label lblIPs;
        CheckBox chkGateway;
        ToolTip gatewayTip;   // field so the tooltip lives as long as the form
        Label lblGwOn, lblGateway, lblDns1, lblDns2;
        ComboBox cboGwIp;
        TextBox txtGateway, txtDns1, txtDns2;
        string gwChoice = "";     // remembered "ip/prefix" that uses the gateway
        bool loadingGateway;
        TextBox console;
        Label lblStatus;
        Button btnBind, btnDhcp;
        Panel updateBanner;
        Label lblUpdateBanner;
        Button btnUpdateNow, btnUpdateLater;
        Button btnClear;
        Button btnAbout;
        CheckBox chkKeepDhcp;
        ToolTip keepDhcpTip;
        bool keepDhcpPref = true;     // what the user chose; the box is forced off where unsupported
        bool settingKeepDhcp;
        string coexistNoteFor;        // adapter whose "not available" note was already logged
        const string KeepDhcpTipOn =
            "On: DHCP stays on and these addresses are added alongside it.\r\n" +
            "Off: static addresses only, no DHCP.";
        const string KeepDhcpTipOff =
            "Needs Windows 10 version 1703 or later.\r\n" +
            "Apply uses static addresses only.";
        bool dark;
        bool launchUpdateCheckStarted;
        bool updateDismissed;
        bool shown;
        bool fittingWorkingArea;
        Rectangle fittedWorkingArea = Rectangle.Empty;
        Color BgCol, PanelCol, FgCol, SubCol, BorderCol, InputCol;
        readonly List<Adapter> adapters = new List<Adapter>();

        public MainForm()
        {
            StoreFile = Path.Combine(AppDir, "ip-list.txt");
            AdapterFile = Path.Combine(AppDir, "adapter.txt");
            UpdateCheckFile = Path.Combine(AppDir, "update-check.txt");
            GatewayFile = Path.Combine(AppDir, "gateway.txt");
            DhcpReminderFile = Path.Combine(AppDir, "dhcp-reminder-dismissed.txt");
            KeepDhcpFile = Path.Combine(AppDir, "keep-dhcp.txt");
            BuildUi();
            LoadKeepDhcpPref();
            LoadAdapters();
            LoadList();
            LoadGatewaySettings();
            Log(BuildInfo.AppName + " v" + BuildInfo.DisplayVersion + " started. Select an interface, edit the list if needed, then Apply or revert.");
            if (defaultsUpgraded)
                Log("Default address list updated to the latest defaults.", "INFO");
            if (BuildInfo.IsBeta)
                Log("Beta build: separate settings (%APPDATA%\\" + BuildInfo.AppName + "), updates only from the beta pre-release.");
            DebugLog.WatchForm(this);
            LogAdaptersRaw();
        }

        // scale helper: design pixels -> device pixels
        int U(int v) { return (int)Math.Round(v * sc); }
        Point P(int x, int y) { return new Point(U(x), U(y)); }
        Size Z(int w, int h) { return new Size(U(w), U(h)); }

        // ---------------- helpers ----------------
        void EnsureAppDir()
        {
            if (!Directory.Exists(AppDir)) Directory.CreateDirectory(AppDir);
        }

        void Log(string msg) { Log(msg, "INFO"); }
        void Log(string msg, string level)
        {
            string fileLevel = level == "ERR" ? "ERROR" : (string.IsNullOrEmpty(level) ? "INFO" : level);
            DebugLog.Write(fileLevel, msg);
            if (console.InvokeRequired)
            {
                console.BeginInvoke((Action<string, string>)LogConsole, msg, level);
                return;
            }
            LogConsole(msg, level);
        }

        void LogConsole(string msg, string level)
        {
            string line = "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + level + "  " + msg + "\r\n";
            console.AppendText(line);
            console.SelectionStart = console.TextLength;
            console.ScrollToCaret();
            AutoHideScrollBox box = console as AutoHideScrollBox;
            if (box != null) box.SyncThumbOnly();
        }

        bool TryCidr(string s, out string ip, out int prefix)
        {
            ip = null; prefix = 0;
            Match m = CidrRe.Match(s ?? "");
            if (!m.Success) return false;
            int[] o = new int[4];
            for (int i = 0; i < 4; i++)
            {
                o[i] = int.Parse(m.Groups[i + 1].Value);
                if (o[i] > 255) return false;
            }
            prefix = m.Groups[6].Success ? int.Parse(m.Groups[6].Value) : 24;
            if (prefix < 0 || prefix > 32) return false;
            ip = o[0] + "." + o[1] + "." + o[2] + "." + o[3];
            return true;
        }

        string MaskFromPrefix(int p)
        {
            uint m = p == 0 ? 0u : (0xFFFFFFFFu << (32 - p));
            return ((m >> 24) & 0xFF) + "." + ((m >> 16) & 0xFF) + "." + ((m >> 8) & 0xFF) + "." + (m & 0xFF);
        }

        string[] CanonicalList()
        {
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<string> outl = new List<string>();
            foreach (string l in txtIPs.Lines)
            {
                string t = (l ?? "").Trim();
                if (t.Length == 0) continue;
                string ip; int pfx;
                if (TryCidr(t, out ip, out pfx))
                {
                    string key = ip + "/" + pfx;
                    if (seen.Add(key)) outl.Add(key);
                }
                else if (seen.Add("raw:" + t)) outl.Add(t);
            }
            return outl.ToArray();
        }

        void SaveList(bool quiet)
        {
            EnsureAppDir();
            string[] lines = CanonicalList();
            File.WriteAllLines(StoreFile, lines); DebugLog.LogSettingsFile(StoreFile, "save-iplist");
            txtIPs.Lines = lines;
            if (!quiet) Log("Saved " + lines.Length + " address(es) to disk.", "OK");
        }

        // BEGIN DefaultsUpgrade (self-contained; exercised by a host-side test)
        // Order-, case- and whitespace-insensitive key for one saved line; CIDR entries
        // are canonicalised exactly like TryCidr (missing prefix = /24).
        static string DefaultsKey(string line)
        {
            string t = (line ?? "").Trim();
            if (t.Length == 0) return null;
            Match m = CidrRe.Match(t);
            if (m.Success)
            {
                int[] o = new int[4];
                bool ok = true;
                for (int i = 0; i < 4; i++)
                {
                    o[i] = int.Parse(m.Groups[i + 1].Value);
                    if (o[i] > 255) ok = false;
                }
                int prefix = m.Groups[6].Success ? int.Parse(m.Groups[6].Value) : 24;
                if (ok && prefix >= 0 && prefix <= 32)
                    return o[0] + "." + o[1] + "." + o[2] + "." + o[3] + "/" + prefix;
            }
            return Regex.Replace(t, @"\s+", "").ToLowerInvariant();
        }

        static HashSet<string> DefaultsKeySet(IEnumerable<string> lines)
        {
            HashSet<string> set = new HashSet<string>(StringComparer.Ordinal);
            foreach (string l in lines)
            {
                string k = DefaultsKey(l);
                if (k != null) set.Add(k);
            }
            return set;
        }

        // True only when the saved list is exactly an earlier shipped default list
        // (never edited) and differs from the current defaults.
        static bool IsUneditedOldDefaults(IEnumerable<string> saved, string[] current, string[][] previous)
        {
            HashSet<string> have = DefaultsKeySet(saved);
            if (have.Count == 0 || have.SetEquals(DefaultsKeySet(current))) return false;
            foreach (string[] old in previous)
                if (have.SetEquals(DefaultsKeySet(old))) return true;
            return false;
        }
        // END DefaultsUpgrade

        bool defaultsUpgraded;

        void LoadList()
        {
            if (File.Exists(StoreFile))
            {
                List<string> lines = new List<string>();
                DebugLog.LogSettingsFile(StoreFile, "load-iplist");
                foreach (string l in File.ReadAllLines(StoreFile))
                {
                    string t = (l ?? "").Trim();
                    if (t.Length > 0) lines.Add(t);
                }
                if (lines.Count > 0)
                {
                    if (IsUneditedOldDefaults(lines, DefaultIPs, PreviousDefaultSets))
                    {
                        txtIPs.Lines = DefaultIPs;
                        SaveList(true);
                        defaultsUpgraded = true;   // logged after the startup line
                        return;
                    }
                    txtIPs.Lines = lines.ToArray();
                    return;
                }
            }
            txtIPs.Lines = DefaultIPs;
            SaveList(true);
        }

        void RestoreDefaults()
        {
            SaveList(true);
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            List<string> merged = new List<string>();
            foreach (string l in txtIPs.Lines)
            {
                string t = (l ?? "").Trim();
                if (t.Length == 0) continue;
                string ip; int pfx;
                if (TryCidr(t, out ip, out pfx)) { string k = ip + "/" + pfx; if (seen.Add(k)) merged.Add(k); }
                else if (seen.Add("raw:" + t)) merged.Add(t);
            }
            int added = 0;
            foreach (string d in DefaultIPs)
            {
                string ip; int pfx; TryCidr(d, out ip, out pfx);
                string k = ip + "/" + pfx;
                if (seen.Add(k)) { merged.Add(k); added++; }
            }
            txtIPs.Lines = merged.ToArray();
            SaveList(true);
            Log("Restored defaults (added " + added + " missing; kept custom entries).", "OK");
        }

        void SaveAdapter(string name) { EnsureAppDir(); File.WriteAllText(AdapterFile, name); DebugLog.LogSettingsFile(AdapterFile, "save-adapter"); }
        string GetSavedAdapter() { return File.Exists(AdapterFile) ? File.ReadAllText(AdapterFile).Trim() : null; }

        // update-check.txt is written by the installer: "auto" (default) or "manual".
        // Only an explicit "manual" turns off the launch check; a missing or unreadable
        // file keeps automatic checks, so portable copies behave as before. Manual
        // checks from About always work.
        bool LaunchUpdateCheckEnabled()
        {
            try
            {
                if (!File.Exists(UpdateCheckFile)) return true;
                string mode = File.ReadAllText(UpdateCheckFile).Trim();
                return !string.Equals(mode, "manual", StringComparison.OrdinalIgnoreCase);
            }
            catch { return true; }
        }

        // ---------------- gateway support ----------------
        // gateway.txt holds key=value lines: enabled, ip, gateway, dns1, dns2. A missing
        // or unreadable file means Gateway support is off.
        void LoadGatewaySettings()
        {
            loadingGateway = true;
            try
            {
                bool enabled = false;
                string gw = "", d1 = "", d2 = "";
                gwChoice = "";
                try
                {
                    if (File.Exists(GatewayFile))
                    {
                        DebugLog.LogSettingsFile(GatewayFile, "load-gateway");
                        foreach (string raw in File.ReadAllLines(GatewayFile))
                        {
                            string l = (raw ?? "").Trim();
                            int eq = l.IndexOf('=');
                            if (eq <= 0) continue;
                            string k = l.Substring(0, eq).Trim().ToLowerInvariant();
                            string v = l.Substring(eq + 1).Trim();
                            if (v.Length > 40) v = v.Substring(0, 40);
                            if (k == "enabled") enabled = v == "1" || string.Equals(v, "true", StringComparison.OrdinalIgnoreCase);
                            else if (k == "ip")
                            {
                                string ip; int pfx;
                                if (TryCidr(v, out ip, out pfx)) gwChoice = ip + "/" + pfx;
                            }
                            else if (k == "gateway") gw = v;
                            else if (k == "dns1") d1 = v;
                            else if (k == "dns2") d2 = v;
                        }
                    }
                }
                catch { enabled = false; gw = ""; d1 = ""; d2 = ""; gwChoice = ""; }
                txtGateway.Text = gw;
                txtDns1.Text = d1;
                txtDns2.Text = d2;
                RefreshGatewayChoices();
                chkGateway.Checked = enabled;
                ApplyGatewayLayout();
            }
            finally { loadingGateway = false; }
        }

        void SaveGatewaySettings()
        {
            if (loadingGateway) return;
            try
            {
                EnsureAppDir();
                File.WriteAllLines(GatewayFile, new string[] {
                    "enabled=" + (chkGateway.Checked ? "1" : "0"),
                    "ip=" + gwChoice,
                    "gateway=" + txtGateway.Text.Trim(),
                    "dns1=" + txtDns1.Text.Trim(),
                    "dns2=" + txtDns2.Text.Trim()
                }); DebugLog.LogSettingsFile(GatewayFile, "save-gateway");
            }
            catch { }
        }

        // Fill "Use gateway on" from the valid entries currently in the list, keeping
        // the remembered choice selected whenever it is still present.
        void RefreshGatewayChoices()
        {
            if (cboGwIp == null) return;
            List<string> keys = new List<string>();
            foreach (string l in txtIPs.Lines)
            {
                string ip; int pfx;
                if (TryCidr(l, out ip, out pfx))
                {
                    string k = ip + "/" + pfx;
                    if (!keys.Contains(k)) keys.Add(k);
                }
            }
            bool same = keys.Count == cboGwIp.Items.Count;
            for (int i = 0; same && i < keys.Count; i++)
                if ((string)cboGwIp.Items[i] != keys[i]) same = false;
            if (!same)
            {
                cboGwIp.BeginUpdate();
                cboGwIp.Items.Clear();
                foreach (string k in keys) cboGwIp.Items.Add(k);
                cboGwIp.EndUpdate();
            }
            int idx = gwChoice.Length > 0 ? keys.IndexOf(gwChoice) : -1;
            if (cboGwIp.SelectedIndex != idx) cboGwIp.SelectedIndex = idx;
        }

        // Shows or hides the gateway rows. The IP list gives up exactly the height the
        // two rows need, so nothing below the list moves.
        void ApplyGatewayLayout()
        {
            bool on = chkGateway.Checked;
            txtIPs.Height = U(on ? 134 : 196);
            lblIPs.Text = on ? IpLabelGateway : IpLabelPlain;
            foreach (Control c in new Control[] {
                lblGwOn, cboGwIp, lblGateway, txtGateway, lblDns1, txtDns1, lblDns2, txtDns2 })
                c.Visible = on;
        }

        static bool TryIPv4(string s, out uint value)
        {
            value = 0;
            Match m = Regex.Match(s ?? "", @"^\s*(\d{1,3})\.(\d{1,3})\.(\d{1,3})\.(\d{1,3})\s*$");
            if (!m.Success) return false;
            for (int i = 1; i <= 4; i++)
            {
                int o = int.Parse(m.Groups[i].Value);
                if (o > 255) return false;
                value = (value << 8) | (uint)o;
            }
            return true;
        }

        static string IPv4Text(uint v)
        {
            return ((v >> 24) & 0xFF) + "." + ((v >> 16) & 0xFF) + "." + ((v >> 8) & 0xFF) + "." + (v & 0xFF);
        }

        // Usable as a gateway or DNS server: not 0.x, loopback, multicast/reserved, or broadcast.
        static bool IsUsableHost(uint v)
        {
            uint first = v >> 24;
            return first != 0 && first != 127 && first < 224;
        }

        sealed class GatewayPlan
        {
            public string Ip;       // chosen address (dotted)
            public int Prefix;
            public string Gateway;
            public string Dns1;     // may be null
            public string Dns2;     // may be null
        }

        // Returns true when Gateway support is off (plan = null) or every field is valid.
        // Logs the reason and returns false otherwise; nothing is applied in that case.
        bool TryBuildGatewayPlan(out GatewayPlan plan)
        {
            plan = null;
            if (!chkGateway.Checked) return true;

            string choice = cboGwIp.SelectedIndex >= 0 ? (string)cboGwIp.SelectedItem : null;
            if (choice == null)
            {
                Log("Gateway support is on, but no address is chosen in 'Use gateway on'. Pick one or turn Gateway support off.", "ERR");
                return false;
            }
            bool listed = false;
            foreach (string k in CanonicalList()) if (k == choice) listed = true;
            string ip; int pfx;
            if (!listed || !TryCidr(choice, out ip, out pfx))
            {
                Log("Gateway address '" + choice + "' is no longer in the IP list.", "ERR");
                return false;
            }
            if (pfx >= 32)
            {
                Log("A /32 address has no subnet, so it cannot use a gateway. Use a shorter prefix such as /24.", "ERR");
                return false;
            }

            string gwText = txtGateway.Text.Trim();
            uint gw, ipv;
            TryIPv4(ip, out ipv);
            if (gwText.Length == 0)
            {
                Log("Gateway support is on, but no gateway is entered. Enter one or turn Gateway support off.", "ERR");
                return false;
            }
            if (!TryIPv4(gwText, out gw) || !IsUsableHost(gw))
            {
                Log("Gateway '" + gwText + "' is not a valid IPv4 address.", "ERR");
                return false;
            }
            uint mask = pfx == 0 ? 0u : (0xFFFFFFFFu << (32 - pfx));
            uint net = ipv & mask;
            uint bcast = net | ~mask;
            if ((gw & mask) != net)
            {
                Log("Gateway " + IPv4Text(gw) + " is not in the same subnet as " + choice +
                    " (" + IPv4Text(net) + "/" + pfx + ").", "ERR");
                return false;
            }
            if (gw == ipv)
            {
                Log("Gateway " + IPv4Text(gw) + " is the same as the address it is for. Use the router's address.", "ERR");
                return false;
            }
            if (pfx <= 30 && (gw == net || gw == bcast))
            {
                Log("Gateway " + IPv4Text(gw) + " is the " + (gw == net ? "network" : "broadcast") +
                    " address of " + IPv4Text(net) + "/" + pfx + ", not a usable host.", "ERR");
                return false;
            }

            string d1Text = txtDns1.Text.Trim(), d2Text = txtDns2.Text.Trim();
            uint d1 = 0, d2 = 0;
            if (d2Text.Length > 0 && d1Text.Length == 0)
            {
                Log("DNS 2 is filled in but DNS 1 is empty. Put the first DNS server in DNS 1.", "ERR");
                return false;
            }
            if (d1Text.Length > 0 && (!TryIPv4(d1Text, out d1) || !IsUsableHost(d1)))
            {
                Log("DNS 1 '" + d1Text + "' is not a valid IPv4 address.", "ERR");
                return false;
            }
            if (d2Text.Length > 0 && (!TryIPv4(d2Text, out d2) || !IsUsableHost(d2)))
            {
                Log("DNS 2 '" + d2Text + "' is not a valid IPv4 address.", "ERR");
                return false;
            }
            if (d2Text.Length > 0 && d1 == d2)
            {
                Log("DNS 2 is the same as DNS 1. Clear DNS 2 or enter a different server.", "ERR");
                return false;
            }

            plan = new GatewayPlan();
            plan.Ip = ip;
            plan.Prefix = pfx;
            plan.Gateway = IPv4Text(gw);
            plan.Dns1 = d1Text.Length > 0 ? IPv4Text(d1) : null;
            plan.Dns2 = d2Text.Length > 0 ? IPv4Text(d2) : null;
            return true;
        }

        void LoadAdapters()
        {
            combo.Items.Clear();
            adapters.Clear();
            NetworkInterface[] nics;
            try { nics = NetworkInterface.GetAllNetworkInterfaces(); }
            catch (Exception ex) { Log("Could not list adapters: " + ex.Message, "ERR"); return; }

            foreach (NetworkInterface ni in nics)
            {
                if (ni.NetworkInterfaceType == NetworkInterfaceType.Loopback) continue;
                bool isEthernet =
                    ni.NetworkInterfaceType == NetworkInterfaceType.Ethernet ||
                    ni.NetworkInterfaceType == NetworkInterfaceType.GigabitEthernet ||
                    ni.NetworkInterfaceType == NetworkInterfaceType.FastEthernetT ||
                    ni.NetworkInterfaceType == NetworkInterfaceType.FastEthernetFx;
                if (!chkAll.Checked)
                {
                    if (!isEthernet) continue;
                    if (VirtualRe.IsMatch(ni.Description ?? "")) continue;
                }
                adapters.Add(new Adapter
                {
                    Name = ni.Name,
                    Description = ni.Description,
                    Status = ni.OperationalStatus.ToString()
                });
            }
            adapters.Sort(delegate (Adapter a, Adapter b)
            {
                return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            });
            foreach (Adapter a in adapters)
                combo.Items.Add(AdapterDisplay.Format(a.Name, a.Description, a.Status));

            // Keep long "Name | Description [Status]" entries readable when the list
            // opens. TextRenderer measures in device pixels; design padding and the
            // generous minimum are explicitly scaled through U().
            int dropDownWidth = Math.Max(combo.Width + U(40), U(800));
            foreach (object item in combo.Items)
            {
                int itemWidth =
                    TextRenderer.MeasureText(item.ToString(), combo.Font).Width + U(32);
                dropDownWidth = Math.Max(dropDownWidth, itemWidth);
            }
            combo.DropDownWidth = dropDownWidth;

            if (combo.Items.Count == 0)
            {
                Log("No wired LAN interfaces found. Tick 'Show all' to list every adapter.", "WARN");
                return;
            }
            string target = GetSavedAdapter();
            int idx = -1;
            if (target != null)
                for (int i = 0; i < adapters.Count; i++)
                    if (adapters[i].Name == target) idx = i;
            if (idx < 0)
                for (int i = 0; i < adapters.Count; i++)
                    if (adapters[i].Status == "Up") { idx = i; break; }
            if (idx < 0) idx = 0;
            combo.SelectedIndex = idx;
            Log("Listed " + adapters.Count + " interface(s)" + (chkAll.Checked ? " (all)" : " (wired only)") + ".");
        }

        // The real Windows connection name. Never parse the dropdown's display
        // string (that is tidied for reading only and is not an interface ID).
        string GetSelectedAlias()
        {
            if (combo.SelectedIndex < 0 || combo.SelectedIndex >= adapters.Count) return null;
            return adapters[combo.SelectedIndex].Name;
        }

        string AdapterStatus(string name)
        {
            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                    if (ni.Name == name) return ni.OperationalStatus.ToString();
            }
            catch { }
            return "Unknown";
        }

        int RunProc(string file, string args, out string output)
        {
            ProcessStartInfo psi = new ProcessStartInfo(file, args);
            psi.UseShellExecute = false;
            psi.RedirectStandardOutput = true;
            psi.RedirectStandardError = true;
            psi.CreateNoWindow = true;
            Stopwatch sw = Stopwatch.StartNew();
            try
            {
                using (Process p = Process.Start(psi))
                {
                    string o = p.StandardOutput.ReadToEnd();
                    string e = p.StandardError.ReadToEnd();
                    p.WaitForExit();
                    sw.Stop();
                    output = (o + e).Trim();
                    DebugLog.LogProc(file, args, p.ExitCode, o, e, sw.ElapsedMilliseconds);
                    return p.ExitCode;
                }
            }
            catch (Exception ex)
            {
                sw.Stop();
                output = ex.Message;
                DebugLog.LogProc(file, args, -1, "", ex.Message, sw.ElapsedMilliseconds);
                return -1;
            }
        }

        int Netsh(string args, out string output) { return RunProc("netsh", args, out output); }

        bool ValidateAdapterAlias(string alias)
        {
            if (string.IsNullOrEmpty(alias))
            {
                Log("Adapter validation failed: the interface name is empty.", "ERR");
                return false;
            }
            if (alias.IndexOfAny(new char[] { '"', '\r', '\n', '\0' }) >= 0)
            {
                Log("Adapter validation failed: the interface name contains unsafe characters.", "ERR");
                return false;
            }

            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                    if (string.Equals(ni.Name, alias, StringComparison.Ordinal))
                        return true;
                Log("Adapter validation failed: '" + alias +
                    "' is not a currently listed interface.", "ERR");
            }
            catch (Exception ex)
            {
                Log("Adapter validation failed: " + ex.Message, "ERR");
            }
            return false;
        }

        void ShowCurrentConfig(string alias)
        {
            if (!ValidateAdapterAlias(alias)) return;
            Log("Current IPv4 config for '" + alias + "':");
            string outp;
            Netsh("interface ipv4 show addresses name=\"" + alias + "\"", out outp);
            foreach (string raw in outp.Replace("\r", "").Split('\n'))
            {
                string t = raw.Trim();
                if (t.Length > 0) Log("  " + t);
            }
            // netsh lists DHCP and static addresses alike; say which is which.
            AdapterInfo info = GetAdapterInfo(alias);
            if (info == null || !info.Dhcp) return;
            List<string> dhcp = new List<string>(), stat = new List<string>();
            foreach (UnicastIPAddressInformation ua in info.V4)
            {
                if (ua.PrefixOrigin == PrefixOrigin.Dhcp) dhcp.Add(ua.Address.ToString());
                else if (ua.PrefixOrigin == PrefixOrigin.Manual) stat.Add(ua.Address + "/" + ua.PrefixLength);
            }
            if (stat.Count > 0)
                Log("  DHCP on" + (dhcp.Count > 0 ? " (" + string.Join(", ", dhcp.ToArray()) + ")" : "") +
                    ", plus static: " + string.Join(", ", stat.ToArray()));
        }

        // ---------------- Keep DHCP (DHCP/static coexistence) ----------------
        // Windows 10 1703+ can keep DHCP on while static addresses sit alongside it
        // (netsh "dhcpstaticipcoexistence"). Without it, adding an address silently turns
        // DHCP off. Support shows up as a line in "netsh interface ipv4 show interface".
        // IPbind records exactly what it added, so Return to DHCP removes only that:
        // "set address source=dhcp" does NOT remove statics while coexistence is on, and
        // "delete address ... gateway=all" would also delete the DHCP default route.

        // BEGIN CoexistParse (self-contained; exercised by a unit test)
        internal enum CoexistState { Unsupported, Disabled, Enabled }

        internal static CoexistState ParseCoexistence(string showInterfaceOutput)
        {
            foreach (string raw in (showInterfaceOutput ?? "").Replace("\r", "").Split('\n'))
            {
                string t = raw.Trim();
                if (!t.StartsWith("DHCP/Static IP coexistence", StringComparison.OrdinalIgnoreCase)) continue;
                int colon = t.IndexOf(':');
                string v = colon < 0 ? "" : t.Substring(colon + 1).Trim();
                return string.Equals(v, "enabled", StringComparison.OrdinalIgnoreCase)
                    ? CoexistState.Enabled : CoexistState.Disabled;
            }
            return CoexistState.Unsupported;
        }
        // END CoexistParse

        sealed class AdapterInfo
        {
            public string Guid;
            public int Index;
            public bool Dhcp;
            public readonly List<UnicastIPAddressInformation> V4 = new List<UnicastIPAddressInformation>();
            public readonly List<string> Gateways = new List<string>();
        }

        void LogAdaptersRaw()
        {
            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                    DebugLog.Debug("adapter raw name='" + ni.Name + "' desc='" + ni.Description
                        + "' id=" + ni.Id + " type=" + ni.NetworkInterfaceType
                        + " status=" + ni.OperationalStatus);
            }
            catch (Exception ex)
            {
                DebugLog.Debug("adapter enumeration failed: " + ex.Message);
            }
        }

        string CaptureAdapterState(string alias)
        {
            if (string.IsNullOrEmpty(alias)) return "(no adapter)";
            try
            {
                AdapterInfo info = GetAdapterInfo(alias);
                if (info == null) return alias + ": (not found)";
                StringBuilder sb = new StringBuilder();
                sb.Append(alias).Append(" dhcp=").Append(info.Dhcp);
                sb.Append(" ips=");
                for (int i = 0; i < info.V4.Count; i++)
                {
                    if (i > 0) sb.Append(",");
                    UnicastIPAddressInformation ua = info.V4[i];
                    sb.Append(ua.Address).Append("/").Append(ua.PrefixLength).Append("(").Append(ua.PrefixOrigin).Append(")");
                }
                sb.Append(" gateway=").Append(info.Gateways.Count == 0 ? "-" : string.Join(",", info.Gateways.ToArray()));
                string dns = "-";
                try
                {
                    foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                    {
                        if (ni.Name != alias) continue;
                        System.Collections.Generic.List<string> d = new System.Collections.Generic.List<string>();
                        foreach (System.Net.IPAddress a in ni.GetIPProperties().DnsAddresses)
                            if (a.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                                d.Add(a.ToString());
                        if (d.Count > 0) dns = string.Join(",", d.ToArray());
                        break;
                    }
                }
                catch { }
                string metric = "?";
                if (info.Index > 0)
                {
                    string outp;
                    Netsh("interface ipv4 show interface " + info.Index, out outp);
                    foreach (string raw in (outp ?? "").Split(new char[] { '\n', '\r' }))
                    {
                        string line = raw.Trim();
                        int idx = line.LastIndexOf("Metric", StringComparison.OrdinalIgnoreCase);
                        if (idx < 0) continue;
                        string rest = line.Substring(idx + 6).Trim().TrimStart(':').Trim();
                        int m;
                        if (int.TryParse(rest.Split(' ')[0], out m)) { metric = m.ToString(); break; }
                    }
                }
                sb.Append(" dns=").Append(dns).Append(" metric=").Append(metric);
                return sb.ToString();
            }
            catch (Exception ex)
            {
                return alias + ": (" + ex.Message + ")";
            }
        }

        void LogAdapterDiff(string when, string before, string after)
        {
            DebugLog.Info("adapter state " + when + " before: " + before);
            DebugLog.Info("adapter state " + when + " after:  " + after);
            if (before == after) DebugLog.Info("adapter state " + when + " diff: (unchanged)");
            else DebugLog.Info("adapter state " + when + " diff: changed");
        }

        AdapterInfo GetAdapterInfo(string alias)
        {
            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.Name != alias) continue;
                    AdapterInfo a = new AdapterInfo();
                    a.Guid = ni.Id;
                    IPInterfaceProperties props = ni.GetIPProperties();
                    try
                    {
                        IPv4InterfaceProperties p4 = props.GetIPv4Properties();
                        if (p4 != null) { a.Index = p4.Index; a.Dhcp = p4.IsDhcpEnabled; }
                    }
                    catch { }
                    foreach (UnicastIPAddressInformation ua in props.UnicastAddresses)
                        if (ua.Address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                            a.V4.Add(ua);
                    foreach (GatewayIPAddressInformation ga in props.GatewayAddresses)
                        a.Gateways.Add(ga.Address.ToString());
                    return a;
                }
            }
            catch { }
            return null;
        }

        CoexistState DetectCoexist(AdapterInfo info)
        {
            if (info == null || info.Index <= 0) return CoexistState.Unsupported;
            string outp;
            Netsh("interface ipv4 show interface " + info.Index, out outp);
            return ParseCoexistence(outp);
        }

        void LoadKeepDhcpPref()
        {
            try
            {
                if (File.Exists(KeepDhcpFile))
                {
                    DebugLog.LogSettingsFile(KeepDhcpFile, "load-keepdhcp");
                    keepDhcpPref = File.ReadAllText(KeepDhcpFile).Trim() != "0";
                }
            }
            catch { keepDhcpPref = true; }
            settingKeepDhcp = true;
            try { chkKeepDhcp.Checked = keepDhcpPref; }
            finally { settingKeepDhcp = false; }
        }

        void SaveKeepDhcpPref()
        {
            try { EnsureAppDir(); File.WriteAllText(KeepDhcpFile, keepDhcpPref ? "1" : "0"); DebugLog.LogSettingsFile(KeepDhcpFile, "save-keepdhcp"); }
            catch { }
        }

        // Enables the box only where Windows supports coexistence for this adapter.
        void RefreshKeepDhcp(string alias)
        {
            if (chkKeepDhcp == null) return;
            CoexistState st = alias == null ? CoexistState.Unsupported : DetectCoexist(GetAdapterInfo(alias));
            bool supported = st != CoexistState.Unsupported;
            settingKeepDhcp = true;
            try
            {
                chkKeepDhcp.Enabled = supported;
                chkKeepDhcp.Checked = supported && keepDhcpPref;
            }
            finally { settingKeepDhcp = false; }
            keepDhcpTip.SetToolTip(chkKeepDhcp, supported ? KeepDhcpTipOn : KeepDhcpTipOff);
            if (!supported && alias != null && coexistNoteFor != alias)
            {
                coexistNoteFor = alias;
                Log("Keep DHCP isn't available on '" + alias +
                    "' (needs Windows 10 version 1703 or later). Apply uses static addresses only.");
            }
        }

        // What a Keep DHCP Apply added to one adapter, stored per adapter GUID in the
        // app's settings folder so Return to DHCP works after a restart.
        sealed class KeepRecord
        {
            public string Adapter;
            public readonly List<string[]> Addresses = new List<string[]>();   // { ip, prefix }
            public string Gateway;     // null = none
            public bool DnsChanged;
        }

        string RecordPath(string guid)
        {
            string safe = Regex.Replace(guid ?? "", "[^0-9A-Fa-f-]", "");
            if (safe.Length == 0) return null;
            return Path.Combine(AppDir, "keep-dhcp-" + safe.ToLowerInvariant() + ".txt");
        }

        // Every value is re-validated, since it ends up in netsh commands.
        KeepRecord LoadRecord(string guid)
        {
            string path = RecordPath(guid);
            if (path == null || !File.Exists(path)) return null;
            try
            {
                KeepRecord r = new KeepRecord();
                foreach (string raw in File.ReadAllLines(path))
                {
                    string l = (raw ?? "").Trim();
                    int eq = l.IndexOf('=');
                    if (eq <= 0 || l.StartsWith("#")) continue;
                    string k = l.Substring(0, eq).Trim().ToLowerInvariant();
                    string v = l.Substring(eq + 1).Trim();
                    if (k == "address")
                    {
                        string ip; int pfx;
                        if (TryCidr(v, out ip, out pfx)) r.Addresses.Add(new string[] { ip, pfx.ToString() });
                    }
                    else if (k == "gateway")
                    {
                        uint g;
                        if (TryIPv4(v, out g) && IsUsableHost(g)) r.Gateway = IPv4Text(g);
                    }
                    else if (k == "dns") r.DnsChanged = v == "1";
                    else if (k == "adapter") r.Adapter = v;
                }
                return r;
            }
            catch (Exception ex)
            {
                Log("Could not read the Keep DHCP record: " + ex.Message, "WARN");
                return null;
            }
        }

        bool SaveRecord(string guid, KeepRecord r)
        {
            string path = RecordPath(guid);
            if (path == null) return false;
            try
            {
                EnsureAppDir();
                List<string> lines = new List<string>();
                lines.Add("# What IPbind added with Keep DHCP. Return to DHCP removes exactly these.");
                lines.Add("mode=keepdhcp");
                lines.Add("adapter=" + (r.Adapter ?? ""));
                foreach (string[] e in r.Addresses) lines.Add("address=" + e[0] + "/" + e[1]);
                if (r.Gateway != null) lines.Add("gateway=" + r.Gateway);
                lines.Add("dns=" + (r.DnsChanged ? "1" : "0"));
                File.WriteAllLines(path, lines.ToArray());
                return true;
            }
            catch (Exception ex)
            {
                Log("Could not save the Keep DHCP record: " + ex.Message, "WARN");
                return false;
            }
        }

        void DeleteRecord(string guid)
        {
            string path = RecordPath(guid);
            if (path == null) return;
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }

        // Targeted undo. Route first, by next hop only: never "gateway=all".
        List<string> RevertCommands(string alias, KeepRecord r)
        {
            List<string> c = new List<string>();
            if (r.Gateway != null)
                c.Add("interface ipv4 delete route prefix=0.0.0.0/0 interface=\"" + alias + "\" nexthop=" + r.Gateway);
            foreach (string[] e in r.Addresses)
                c.Add("interface ipv4 delete address name=\"" + alias + "\" address=" + e[0]);
            if (r.DnsChanged)
                c.Add("interface ipv4 set dnsservers name=\"" + alias + "\" source=dhcp");
            return c;
        }

        static bool InSubnet(uint addr, uint ip, int prefix)
        {
            uint mask = prefix <= 0 ? 0u : (0xFFFFFFFFu << (32 - Math.Min(prefix, 32)));
            return (addr & mask) == (ip & mask);
        }

        // BEGIN ApplySafety (self-contained; exercised by a unit test)
        internal enum DhcpOverlapKind { None, SameAsLease, InsideSubnet }

        internal static int ClampPrefix(int prefix)
        {
            if (prefix < 0) return 0;
            if (prefix > 32) return 32;
            return prefix;
        }

        internal static DhcpOverlapKind ClassifyDhcpOverlap(uint bindAddr, uint leaseAddr, int leasePrefix)
        {
            leasePrefix = ClampPrefix(leasePrefix);
            if (bindAddr == leaseAddr) return DhcpOverlapKind.SameAsLease;
            if (InSubnet(bindAddr, leaseAddr, leasePrefix)) return DhcpOverlapKind.InsideSubnet;
            return DhcpOverlapKind.None;
        }

        internal static string DhcpNetworkText(uint leaseAddr, int leasePrefix)
        {
            leasePrefix = ClampPrefix(leasePrefix);
            uint mask = leasePrefix == 0 ? 0u : (0xFFFFFFFFu << (32 - leasePrefix));
            return IPv4Text(leaseAddr & mask) + "/" + leasePrefix;
        }

        internal static string EqualDhcpAddressLog(string ip)
        {
            return ip + " is this adapter's DHCP address. Nothing was applied.";
        }

        internal static string InsideDhcpNetworkLog(string ip, string network)
        {
            return ip + " is inside the DHCP network " + network + ". Another device could be given this address.";
        }

        internal static string AddressInUseLog(string ip, string mac)
        {
            return ip + " is already in use on this network (MAC " + mac + "), skipped.";
        }

        internal static string AllAddressesInUseLog()
        {
            return "Every address was already in use on this network. Nothing was applied.";
        }

        internal static string FormatMac(byte[] mac, int length)
        {
            int n = length < 0 ? 0 : Math.Min(mac == null ? 0 : mac.Length, length);
            if (n < 1) return "";
            string[] parts = new string[n];
            for (int i = 0; i < n; i++) parts[i] = mac[i].ToString("x2");
            return string.Join("-", parts);
        }
        // END ApplySafety

        [DllImport("iphlpapi.dll", ExactSpelling = true)]
        static extern int SendARP(uint destIP, uint srcIP, byte[] pMacAddr, ref int phyAddrLen);

        static uint ToIpAddr(uint hostOrder)
        {
            byte[] b = new byte[]
            {
                (byte)((hostOrder >> 24) & 0xFF),
                (byte)((hostOrder >> 16) & 0xFF),
                (byte)((hostOrder >> 8) & 0xFF),
                (byte)(hostOrder & 0xFF)
            };
            return BitConverter.ToUInt32(b, 0);
        }

        sealed class ArpHit
        {
            public string Ip;
            public string Mac;
        }

        // Probes only the given addresses, in parallel. SendARP is bounded by a
        // 1s per-address join and a 1.5s overall deadline so Apply stays responsive.
        // A timeout or error is treated as "not in use" (the address is not skipped).
        List<ArpHit> ProbeInUse(List<string> ips, Dictionary<string, uint> srcByIp)
        {
            List<ArpHit> hits = new List<ArpHit>();
            if (ips == null || ips.Count == 0) return hits;
            ArpHit[] found = new ArpHit[ips.Count];
            Thread[] threads = new Thread[ips.Count];
            for (int i = 0; i < ips.Count; i++)
            {
                int idx = i;
                string ip = ips[i];
                uint dest, src = 0;
                if (!TryIPv4(ip, out dest)) continue;
                if (srcByIp != null) srcByIp.TryGetValue(ip, out src);
                threads[idx] = new Thread(delegate()
                {
                    try
                    {
                        byte[] mac = new byte[8];
                        int len = 6;
                        int r = SendARP(ToIpAddr(dest), src == 0 ? 0u : ToIpAddr(src), mac, ref len);
                        if (r == 0 && len >= 6)
                        {
                            ArpHit hit = new ArpHit();
                            hit.Ip = ip;
                            hit.Mac = FormatMac(mac, 6);
                            found[idx] = hit;
                        }
                    }
                    catch { }
                });
                threads[idx].IsBackground = true;
                threads[idx].Start();
            }
            DateTime deadline = DateTime.UtcNow.AddMilliseconds(1500);
            for (int i = 0; i < threads.Length; i++)
            {
                if (threads[i] == null) continue;
                int remain = (int)(deadline - DateTime.UtcNow).TotalMilliseconds;
                if (remain < 1) remain = 1;
                if (remain > 1000) remain = 1000;
                threads[i].Join(remain);
            }
            for (int i = 0; i < found.Length; i++)
                if (found[i] != null) hits.Add(found[i]);
            return hits;
        }

        // All netsh commands run in ONE netsh process via a script file. Spawning a
        // separate netsh.exe per address is what made this slow: on a managed machine,
        // endpoint security inspects every child process as it launches.
        string RunNetshBatch(List<string> cmds)
        {
            string outp = "";
            string script = Path.Combine(Path.GetTempPath(), "ipbind-" + Guid.NewGuid().ToString("N") + ".netsh");
            try
            {
                PrivateFile.WriteAllText(
                    script, string.Join("\r\n", cmds.ToArray()) + "\r\n");
                RunProc("netsh", "-f \"" + script + "\"", out outp);
            }
            catch (Exception ex) { outp = ex.Message; }
            finally { try { File.Delete(script); } catch { } }
            return outp;
        }

        // netsh prints harmless notes (e.g. "DHCP is already enabled") that are not
        // failures, so don't judge success by its output. Log real-looking errors as
        // warnings, everything else as info, then verify against the actual interface.
        void LogNetshOutput(string outp)
        {
            foreach (string raw in (outp ?? "").Replace("\r", "").Split('\n'))
            {
                string t = raw.Trim();
                if (t.Length == 0) continue;
                string low = t.ToLowerInvariant();
                bool looksBad = low.Contains("error") || low.Contains("failed")
                    || low.Contains("cannot") || low.Contains("incorrect") || low.Contains("not found");
                Log("  netsh: " + t, looksBad ? "WARN" : "INFO");
            }
        }

        // Return to DHCP while Keep DHCP is in effect: DHCP is already on, so remove just
        // what IPbind added (from the record, or, without one, the listed addresses that
        // are on the interface as manual addresses).
        bool RevertKeepDhcp(string alias, AdapterInfo info, KeepRecord rec)
        {
            KeepRecord undo = rec;
            if (undo != null)
            {
                Log("Removing the addresses IPbind added to '" + alias + "'. DHCP stays on...");
            }
            else
            {
                Log("No record of what IPbind added to '" + alias +
                    "'. Removing listed addresses found on it.", "WARN");
                undo = new KeepRecord();
                HashSet<string> listed = new HashSet<string>();
                foreach (string k in CanonicalList())
                {
                    string ip; int pfx;
                    if (TryCidr(k, out ip, out pfx)) listed.Add(ip);
                }
                foreach (UnicastIPAddressInformation ua in info.V4)
                {
                    string ip = ua.Address.ToString();
                    if (ua.PrefixOrigin == PrefixOrigin.Manual && listed.Contains(ip))
                        undo.Addresses.Add(new string[] { ip, ua.PrefixLength.ToString() });
                }
                // The saved Gateway support gateway, only if it serves one of those
                // addresses and is not on the DHCP address's subnet.
                uint gw;
                if (TryIPv4(txtGateway.Text.Trim(), out gw) && IsUsableHost(gw) &&
                    info.Gateways.Contains(IPv4Text(gw)))
                {
                    bool mine = false, dhcpSide = false;
                    foreach (string[] e in undo.Addresses)
                    {
                        uint v; if (TryIPv4(e[0], out v) && InSubnet(gw, v, int.Parse(e[1]))) mine = true;
                    }
                    foreach (UnicastIPAddressInformation ua in info.V4)
                    {
                        uint v;
                        if (ua.PrefixOrigin == PrefixOrigin.Dhcp && TryIPv4(ua.Address.ToString(), out v) &&
                            InSubnet(gw, v, ua.PrefixLength)) dhcpSide = true;
                    }
                    if (mine && !dhcpSide) undo.Gateway = IPv4Text(gw);
                }
                if (undo.Addresses.Count == 0) Log("  None of the listed addresses are on the interface.");
            }

            HashSet<string> removing = new HashSet<string>();
            foreach (string[] e in undo.Addresses) removing.Add(e[0]);
            int others = 0;
            foreach (UnicastIPAddressInformation ua in info.V4)
                if (ua.PrefixOrigin == PrefixOrigin.Manual && !removing.Contains(ua.Address.ToString())) others++;

            List<string> cmds = RevertCommands(alias, undo);
            if (others == 0)
                cmds.Add("interface ipv4 set interface \"" + alias + "\" dhcpstaticipcoexistence=disabled");
            else
                Log("  Left " + others + " other static address(es) on '" + alias +
                    "' alone, so Keep DHCP stays on for it.", "WARN");

            LogNetshOutput(RunNetshBatch(cmds));
            foreach (string[] e in undo.Addresses) Log("  - " + e[0] + "/" + e[1], "OK");
            if (undo.Gateway != null) Log("  - gateway " + undo.Gateway, "OK");
            if (undo.DnsChanged) Log("  DNS back to automatic (DHCP).", "OK");
            Log("Back to DHCP - verifying:", "OK");
            ShowCurrentConfig(alias);

            AdapterInfo after = GetAdapterInfo(alias);
            bool ok = after != null && after.Dhcp;
            if (after != null && !after.Dhcp) Log("DHCP does not appear to be enabled on the interface.", "WARN");
            if (after != null)
                foreach (UnicastIPAddressInformation ua in after.V4)
                    if (removing.Contains(ua.Address.ToString()))
                    {
                        ok = false;
                        Log("  " + ua.Address + " is still on the interface.", "WARN");
                    }
            if (ok)
            {
                if (rec != null) DeleteRecord(info.Guid);
                Log("  DHCP stayed on the whole time.", "OK");
            }
            return ok;
        }

        // ---------------- actions ----------------
        bool InvokeBind()
        {
            string alias = GetSelectedAlias();
            if (alias == null) { Log("No interface selected.", "WARN"); return false; }
            if (!ValidateAdapterAlias(alias)) return false;

            string status = AdapterStatus(alias);
            if (status != "Up")
            {
                Log("Interface '" + alias + "' is '" + status + "', not connected (Up).", "WARN");
                DialogResult ans = MessageBox.Show(
                    "The interface '" + alias + "' is currently '" + status + "', not connected.\r\n\r\n" +
                    "Windows will not bind static addresses to a disconnected interface, so this likely " +
                    "won't take effect until it is connected.\r\n\r\nApply anyway?",
                    "Interface not connected", MessageBoxButtons.YesNo, MessageBoxIcon.Warning);
                if (ans != DialogResult.Yes) { Log("Apply cancelled - interface not connected.", "WARN"); return false; }
                Log("Proceeding on a disconnected interface at user request.", "WARN");
            }

            SaveList(true);

            GatewayPlan gwPlan;
            if (!TryBuildGatewayPlan(out gwPlan))
            {
                Log("Nothing was applied. Fix the gateway settings and try again.", "ERR");
                return false;
            }

            List<string[]> entries = new List<string[]>();
            foreach (string line in txtIPs.Lines)
            {
                if (string.IsNullOrEmpty((line ?? "").Trim())) continue;
                string ip; int pfx;
                if (TryCidr(line, out ip, out pfx)) entries.Add(new string[] { ip, pfx.ToString() });
                else Log("Skipping invalid entry: '" + line.Trim() + "'", "WARN");
            }
            if (entries.Count == 0) { Log("No valid addresses to apply.", "WARN"); return false; }

            // With a gateway, the chosen address goes first: it becomes the primary
            // ("set address") entry, which is where netsh attaches the gateway.
            if (gwPlan != null)
            {
                int gi = entries.FindIndex(delegate (string[] e)
                {
                    return e[0] == gwPlan.Ip && e[1] == gwPlan.Prefix.ToString();
                });
                if (gi > 0) { string[] g = entries[gi]; entries.RemoveAt(gi); entries.Insert(0, g); }
            }

            AdapterInfo info = GetAdapterInfo(alias);
            CoexistState coexist = DetectCoexist(info);
            bool keep = chkKeepDhcp.Checked;
            if (keep && coexist == CoexistState.Unsupported)
            {
                Log("Keep DHCP isn't available on '" + alias +
                    "' (needs Windows 10 version 1703 or later). Applying static addresses only.", "WARN");
                keep = false;
            }
            KeepRecord old = info != null ? LoadRecord(info.Guid) : null;

            // Apply-time safety: never bind the adapter's own DHCP address, warn
            // when a bind address sits in a DHCP lease subnet, and skip addresses
            // that already answer ARP on that subnet. Isolated (non-DHCP-subnet)
            // entries are not probed, so air-gapped use stays fast. Lease info is
            // read here, before Keep DHCP or static mode changes the adapter.
            List<string[]> leases = new List<string[]>();
            if (info != null)
            {
                foreach (UnicastIPAddressInformation ua in info.V4)
                {
                    if (ua.PrefixOrigin != PrefixOrigin.Dhcp) continue;
                    uint leaseAddr;
                    if (!TryIPv4(ua.Address.ToString(), out leaseAddr)) continue;
                    leases.Add(new string[] { ua.Address.ToString(), ua.PrefixLength.ToString(), DhcpNetworkText(leaseAddr, ua.PrefixLength) });
                }
            }
            foreach (string[] e in entries)
            {
                uint bindAddr;
                if (!TryIPv4(e[0], out bindAddr)) continue;
                foreach (string[] lease in leases)
                {
                    uint leaseAddr;
                    if (!TryIPv4(lease[0], out leaseAddr)) continue;
                    if (ClassifyDhcpOverlap(bindAddr, leaseAddr, int.Parse(lease[1])) == DhcpOverlapKind.SameAsLease)
                    {
                        Log(EqualDhcpAddressLog(e[0]), "ERR");
                        return false;
                    }
                }
            }
            Dictionary<string, uint> probeSrc = new Dictionary<string, uint>();
            List<string> inside = new List<string>();
            foreach (string[] e in entries)
            {
                uint bindAddr;
                if (!TryIPv4(e[0], out bindAddr)) continue;
                foreach (string[] lease in leases)
                {
                    uint leaseAddr;
                    if (!TryIPv4(lease[0], out leaseAddr)) continue;
                    if (ClassifyDhcpOverlap(bindAddr, leaseAddr, int.Parse(lease[1])) != DhcpOverlapKind.InsideSubnet)
                        continue;
                    Log(InsideDhcpNetworkLog(e[0], lease[2]), "WARN");
                    if (!probeSrc.ContainsKey(e[0]))
                    {
                        probeSrc[e[0]] = leaseAddr;
                        inside.Add(e[0]);
                    }
                    break;
                }
            }
            HashSet<string> alreadyHeld = new HashSet<string>(StringComparer.Ordinal);
            if (info != null)
                foreach (UnicastIPAddressInformation ua in info.V4)
                    alreadyHeld.Add(ua.Address.ToString());
            if (old != null)
                foreach (string[] e in old.Addresses)
                    alreadyHeld.Add(e[0]);
            List<string> toProbe = new List<string>();
            foreach (string ip in inside)
                if (!alreadyHeld.Contains(ip)) toProbe.Add(ip);
            if (toProbe.Count > 0)
            {
                List<ArpHit> hits = ProbeInUse(toProbe, probeSrc);
                if (hits.Count > 0)
                {
                    HashSet<string> used = new HashSet<string>(StringComparer.Ordinal);
                    foreach (ArpHit hit in hits)
                    {
                        used.Add(hit.Ip);
                        Log(AddressInUseLog(hit.Ip, hit.Mac), "WARN");
                    }
                    List<string[]> kept = new List<string[]>();
                    foreach (string[] e in entries)
                        if (!used.Contains(e[0])) kept.Add(e);
                    if (kept.Count == 0)
                    {
                        Log(AllAddressesInUseLog(), "ERR");
                        return false;
                    }
                    if (gwPlan != null)
                    {
                        bool gwKept = false;
                        foreach (string[] e in kept)
                            if (e[0] == gwPlan.Ip && e[1] == gwPlan.Prefix.ToString()) gwKept = true;
                        if (!gwKept)
                        {
                            Log(gwPlan.Ip + " was the Gateway support address, so the gateway was not applied.", "WARN");
                            gwPlan = null;
                        }
                    }
                    entries = kept;
                }
            }

            List<string> cmds = new List<string>();
            cmds.Add("interface ipv4 set interface \"" + alias + "\" dadtransmits=0");
            // Undo an earlier Keep DHCP Apply first, so its items never linger.
            if (old != null) cmds.AddRange(RevertCommands(alias, old));
            KeepRecord rec = null;
            if (keep)
            {
                Log("Applying " + entries.Count + " static IP(s) to '" + alias + "' alongside DHCP (single batch)...");
                if (!info.Dhcp)
                {
                    // Left static (e.g. by Static only): back to DHCP before adding.
                    Log("  The interface is static; switching it to DHCP first.");
                    cmds.Add("interface ipv4 set address name=\"" + alias + "\" source=dhcp");
                    cmds.Add("interface ipv4 set dnsservers name=\"" + alias + "\" source=dhcp");
                }
                cmds.Add("interface ipv4 set interface \"" + alias + "\" dhcpstaticipcoexistence=enabled");
                foreach (string[] e in entries)
                {
                    string ip = e[0];
                    string mask = MaskFromPrefix(int.Parse(e[1]));
                    string c = "interface ipv4 add address name=\"" + alias + "\" address=" + ip + " mask=" + mask;
                    if (gwPlan != null && ip == gwPlan.Ip && e[1] == gwPlan.Prefix.ToString())
                        c += " gateway=" + gwPlan.Gateway + " gwmetric=" + GatewayMetric;
                    cmds.Add(c);
                }
                rec = new KeepRecord();
                rec.Adapter = alias;
                rec.Addresses.AddRange(entries);
                rec.Gateway = gwPlan != null ? gwPlan.Gateway : null;
                rec.DnsChanged = gwPlan != null && gwPlan.Dns1 != null;
            }
            else
            {
                Log("Applying " + entries.Count + " static IP(s) to '" + alias + "' (single batch)...");
                if (coexist == CoexistState.Enabled)
                    cmds.Add("interface ipv4 set interface \"" + alias + "\" dhcpstaticipcoexistence=disabled");
                cmds.Add("interface ipv4 set address name=\"" + alias + "\" source=dhcp");
                bool first = true;
                foreach (string[] e in entries)
                {
                    string ip = e[0];
                    string mask = MaskFromPrefix(int.Parse(e[1]));
                    if (first && gwPlan != null)
                        cmds.Add("interface ipv4 set address name=\"" + alias + "\" source=static address=" + ip +
                                 " mask=" + mask + " gateway=" + gwPlan.Gateway + " gwmetric=" + GatewayMetric);
                    else
                        cmds.Add(first
                            ? "interface ipv4 set address name=\"" + alias + "\" static " + ip + " " + mask
                            : "interface ipv4 add address name=\"" + alias + "\" " + ip + " " + mask);
                    first = false;
                }
            }
            if (gwPlan != null && gwPlan.Dns1 != null)
            {
                // register=none: never publish these bench addresses to corporate DNS.
                cmds.Add("interface ipv4 set dnsservers name=\"" + alias + "\" source=static address=" +
                         gwPlan.Dns1 + " register=none validate=no");
                if (gwPlan.Dns2 != null)
                    cmds.Add("interface ipv4 add dnsservers name=\"" + alias + "\" address=" +
                             gwPlan.Dns2 + " index=2 validate=no");
            }

            // Record before running, so a crash mid-batch still leaves a way back.
            if (rec != null) SaveRecord(info.Guid, rec);
            else if (old != null) DeleteRecord(info.Guid);

            string outp = RunNetshBatch(cmds);

            foreach (string[] e in entries) Log("  + " + e[0] + "/" + e[1], "OK");
            if (gwPlan != null)
            {
                Log("  gateway " + gwPlan.Gateway + " (metric " + GatewayMetric + ") on " +
                    gwPlan.Ip + "/" + gwPlan.Prefix, "OK");
                if (gwPlan.Dns1 != null)
                    Log("  DNS " + gwPlan.Dns1 + (gwPlan.Dns2 != null ? ", " + gwPlan.Dns2 : ""), "OK");
                else if (!keep)
                    Log("No DNS set. Website names may not work through this adapter. " +
                        "Try your router's address (often the same as the gateway).", "WARN");
            }
            LogNetshOutput(outp);

            Log("Bind complete - verifying:", "OK");
            ShowCurrentConfig(alias);

            // Success = every intended address is actually present on the interface
            // (and, with Keep DHCP, DHCP is still on).
            List<string> want = new List<string>();
            foreach (string[] e in entries) want.Add(e[0]);
            int found = 0;
            bool gwFound = false;
            AdapterInfo after = GetAdapterInfo(alias);
            if (after != null)
            {
                foreach (UnicastIPAddressInformation ua in after.V4)
                    if (want.Contains(ua.Address.ToString())) found++;
                if (gwPlan != null && after.Gateways.Contains(gwPlan.Gateway)) gwFound = true;
            }
            if (gwPlan != null && !gwFound)
                Log("Gateway " + gwPlan.Gateway + " is not showing on the interface yet.", "WARN");
            bool ok = found >= want.Count;
            if (!ok) Log("Only " + found + " of " + want.Count + " addresses are present on the interface.", "WARN");
            if (keep && after != null && !after.Dhcp)
            {
                ok = false;
                Log("DHCP turned off on the interface; Keep DHCP did not take effect.", "WARN");
            }
            else if (keep && ok)
                Log("  DHCP is still on.", "OK");
            if (status != "Up") Log("Interface is not connected - addresses will activate once it comes up.", "WARN");
            return ok;
        }

        bool InvokeDhcp()
        {
            string alias = GetSelectedAlias();
            if (alias == null) { Log("No interface selected.", "WARN"); return false; }
            if (!ValidateAdapterAlias(alias)) return false;
            Log("Returning '" + alias + "' to DHCP...");

            AdapterInfo info = GetAdapterInfo(alias);
            KeepRecord rec = info != null ? LoadRecord(info.Guid) : null;
            CoexistState coexist = DetectCoexist(info);
            if (info != null && info.Dhcp && (rec != null || coexist == CoexistState.Enabled))
                return RevertKeepDhcp(alias, info, rec);

            // Same one-process batching as the bind path.
            List<string> cmds = new List<string>();
            if (rec != null) cmds.AddRange(RevertCommands(alias, rec));
            cmds.Add("interface ipv4 set address name=\"" + alias + "\" source=dhcp");
            cmds.Add("interface ipv4 set dnsservers name=\"" + alias + "\" source=dhcp");
            if (coexist == CoexistState.Enabled)
                cmds.Add("interface ipv4 set interface \"" + alias + "\" dhcpstaticipcoexistence=disabled");
            string outp = RunNetshBatch(cmds);
            if (rec != null && info != null) DeleteRecord(info.Guid);

            // Kick off a lease renewal but DON'T wait for it. The interface is already
            // configured for DHCP at this point; blocking on ipconfig /renew waits for the
            // full discover/request handshake (often many seconds). Windows acquires the
            // lease on its own - we just nudge it in the background.
            try
            {
                ProcessStartInfo psi = new ProcessStartInfo("ipconfig", "/renew \"" + alias + "\"");
                psi.UseShellExecute = false;
                psi.CreateNoWindow = true;
                Process.Start(psi);
            }
            catch { }
            Log("  DHCP re-enabled, DNS reset. Lease will refresh in the background.");
            Log("Back to DHCP - verifying:", "OK");
            ShowCurrentConfig(alias);

            // Success = the interface is configured for DHCP. This is a config flag, set the
            // instant source=dhcp ran - it does not wait for a lease to be acquired.
            bool dhcpOk = false;
            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.Name != alias) continue;
                    IPv4InterfaceProperties p4 = ni.GetIPProperties().GetIPv4Properties();
                    if (p4 != null && p4.IsDhcpEnabled) dhcpOk = true;
                }
            }
            catch { }
            if (!dhcpOk) Log("DHCP does not appear to be enabled on the interface.", "WARN");
            else StartLeaseWatch(alias);
            return dhcpOk;
        }

        // Watches in the background for the DHCP server to actually hand the interface an
        // address, then logs it. Runs off the UI thread so it never holds up the button.
        void StartLeaseWatch(string alias)
        {
            System.Threading.Thread t = new System.Threading.Thread(delegate()
            {
                for (int i = 0; i < 30; i++)
                {
                    System.Threading.Thread.Sleep(1000);
                    try
                    {
                        foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                        {
                            if (ni.Name != alias) continue;
                            foreach (UnicastIPAddressInformation ua in ni.GetIPProperties().UnicastAddresses)
                            {
                                string ip = ua.Address.ToString();
                                if (ip.Contains(":")) continue;            // skip IPv6
                                if (ua.PrefixOrigin != PrefixOrigin.Dhcp) continue;  // only a real DHCP lease
                                Log("DHCP lease acquired: " + ip + " on '" + alias + "'.", "OK");
                                return;
                            }
                        }
                    }
                    catch { }
                }
                Log("No DHCP address seen yet on '" + alias + "' (lease may still be pending).", "INFO");
            });
            t.IsBackground = true;
            t.Start();
        }

        // ---------------- UI ----------------
        void SetBusy(string text)
        {
            btnBind.Enabled = false; btnDhcp.Enabled = false;
            lblStatus.ForeColor = dark ? Color.FromArgb(200, 200, 200) : Color.FromArgb(60, 60, 60);
            lblStatus.Text = text;
            lblStatus.Update();
        }
        void EndBusy(bool ok, string okMsg, string failMsg)
        {
            btnBind.Enabled = true; btnDhcp.Enabled = true;
            if (ok)
            {
                lblStatus.ForeColor = dark ? Color.FromArgb(90, 200, 110) : Color.FromArgb(30, 120, 40);   // green
                lblStatus.Text = "OK - " + okMsg;
            }
            else
            {
                lblStatus.ForeColor = dark ? Color.FromArgb(240, 100, 100) : Color.FromArgb(180, 40, 40);   // red
                lblStatus.Text = failMsg;
            }
        }

        void CheckForUpdatesOnLaunch()
        {
            if (launchUpdateCheckStarted) return;
            launchUpdateCheckStarted = true;
            if (!LaunchUpdateCheckEnabled()) return;   // user chose manual-only updates
            Thread thread = new Thread(delegate()
            {
                UpdateInfo info;
                try { info = UpdateChecker.Check(); }
                catch { return; }
                if (!info.IsNewer) return;

                try
                {
                    BeginInvoke((MethodInvoker)delegate
                    {
                        if (IsDisposed || updateDismissed) return;
                        lblUpdateBanner.Text =
                            "Version " + VersionText.ForDisplay(info.RemoteVersion) + " is available.";
                        updateBanner.Visible = true;
                        ApplyLeftColumnStatusLayout();
                        updateBanner.BringToFront();
                    });
                }
                catch { }
            });
            thread.IsBackground = true;
            thread.Start();
        }

        void InstallUpdateFromBanner(object sender, EventArgs e)
        {
            btnUpdateNow.Enabled = false;
            btnUpdateLater.Enabled = false;
            btnUpdateNow.Text = "Updating...";
            lblUpdateBanner.Text = "Downloading update; IPbind will restart.";
            Thread thread = new Thread(delegate()
            {
                try
                {
                    UpdateChecker.DownloadAndRestart();
                    try { BeginInvoke((MethodInvoker)delegate { Application.Exit(); }); }
                    catch { }
                }
                catch (Exception ex)
                {
                    try
                    {
                        BeginInvoke((MethodInvoker)delegate
                        {
                            if (IsDisposed) return;
                            lblUpdateBanner.Text = UpdateChecker.DescribeError(ex);
                            btnUpdateNow.Text = "Update";
                            btnUpdateNow.Enabled = true;
                            btnUpdateLater.Enabled = true;
                        });
                    }
                    catch { }
                }
            });
            thread.IsBackground = true;
            thread.Start();
        }

        void DismissUpdateBanner(object sender, EventArgs e)
        {
            updateDismissed = true;
            updateBanner.Visible = false;
            ApplyLeftColumnStatusLayout();
        }

        Button MakeButton(string text, int x, int y, int w, int h)
        {
            Button b = new Button();
            b.Text = text;
            b.Location = P(x, y);
            b.Size = Z(w, h);
            b.FlatStyle = FlatStyle.Standard;
            return b;
        }

        Font UiFont(float pt, FontStyle style) { return new Font("Segoe UI", pt, style); }

        // Width of a left-box option checkbox: glyph, gap and caption at this scale.
        int OptionWidth(CheckBox box)
        {
            Size text = TextRenderer.MeasureText(box.Text, box.Font);
            return text.Width + U(24);
        }

        // Distance from a check box's left edge to the right edge of its drawn text
        // (glyph, gap and text as Windows actually renders them at this DPI). Drawn
        // on a stand-alone copy so MainForm's handle is not created early.
        int TextInkRight(CheckBox box)
        {
            try
            {
                using (CheckBox probe = new CheckBox())
                {
                    probe.Text = box.Text;
                    probe.Font = box.Font;
                    probe.Size = box.Size;
                    probe.CheckAlign = box.CheckAlign;
                    probe.TextAlign = box.TextAlign;
                    probe.FlatStyle = box.FlatStyle;
                    probe.BackColor = Color.White;
                    probe.ForeColor = Color.Black;
                    using (Bitmap bmp = new Bitmap(probe.Width, probe.Height))
                    {
                        probe.DrawToBitmap(bmp, new Rectangle(Point.Empty, probe.Size));
                        for (int x = bmp.Width - 1; x >= 0; x--)
                            for (int y = 0; y < bmp.Height; y++)
                            {
                                Color c = bmp.GetPixel(x, y);
                                // Any pixel clearly darker than the white background
                                // counts, so the anti-aliased edge of the last letter is
                                // included, as it is on screen.
                                if (c.R + c.G + c.B < 765 - 300) return x + 1;
                            }
                    }
                }
            }
            catch { }
            return box.Width - U(6);
        }

        Label MakeRowLabel(string text, int x, int y)
        {
            Label l = new Label();
            l.Text = text;
            l.Font = UiFont(8F, FontStyle.Regular);
            l.Location = P(x, y);
            l.Size = Z(x == 20 ? 98 : 64, 23);
            l.TextAlign = ContentAlignment.MiddleLeft;
            l.Visible = false;
            Controls.Add(l);
            return l;
        }

        TextBox MakeIpBox(int x, int y)
        {
            TextBox t = new TextBox();
            t.Location = P(x, y);
            t.Width = U(188);
            t.MaxLength = 15;
            t.Visible = false;
            t.TextChanged += delegate { SaveGatewaySettings(); };
            Controls.Add(t);
            return t;
        }

        // ---------------- dark mode ----------------
        static bool IsDarkMode()
        {
            return WindowTheme.IsAppDark();
        }

        const int WM_DPICHANGED = 0x02E0;

        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int Left, Top, Right, Bottom; }

        bool dpiChanging;

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            WindowTheme.ApplyTitleBar(this, dark);
        }

        const int WM_DISPLAYCHANGE = 0x007E;

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_DPICHANGED)
            {
                int oldDpi = DpiWindow.WindowDpi(Handle);
                Size oldSize = Size;
                if (!dpiChanging)
                {
                    dpiChanging = true;
                    try
                    {
                        // The process is system-DPI aware, so this message is
                        // unusual (tests post it; a future per-monitor switch
                        // would too). Do not dispose fonts or relayout here.
                        SuspendLayout();
                        if (m.LParam != IntPtr.Zero)
                        {
                            RECT suggested = (RECT)Marshal.PtrToStructure(m.LParam, typeof(RECT));
                            Bounds = new Rectangle(suggested.Left, suggested.Top, Width, Height);
                        }
                        ResumeLayout(true);
                    }
                    finally { dpiChanging = false; }
                }
                int newDpi = (int)(m.WParam.ToInt64() & 0xFFFF);
                DebugLog.Info("WM_DPICHANGED main oldDpi=" + oldDpi + " newDpi=" + newDpi
                    + " oldSize=" + oldSize + " newSize=" + Size + " bounds=" + Bounds);
                return;
            }
            if (m.Msg == WM_DISPLAYCHANGE)
                DebugLog.Info("WM_DISPLAYCHANGE wParam=" + m.WParam.ToInt64());
            base.WndProc(ref m);
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            shown = true;
            FitToWorkingArea(true);
            if (dark)
            {
                WindowTheme.ApplyPreferredAppMode();
                DarkenScrollbars(this.Controls);
            }
            CheckForUpdatesOnLaunch();
            // After the main window has painted: the Return-to-DHCP reminder.
            BeginInvoke((MethodInvoker)ShowDhcpReminderIfNeeded);
        }

        // Shown at every launch until the user clicks Continue with "Don’t show this
        // again" ticked. Anything other than Continue closes IPbind. The flag file is new in 2.3.125, so fresh installs and the first
        // launch after updating from an older version both see it.
        void ShowDhcpReminderIfNeeded()
        {
            if (IsDisposed) return;
            try { if (File.Exists(DhcpReminderFile)) return; } catch { }
            bool proceed, remember;
            using (DhcpReminderForm reminder = new DhcpReminderForm(this, dark, sc))
            {
                proceed = reminder.ShowDialog(this) == DialogResult.OK;
                remember = proceed && reminder.DontShowAgain;
            }
            if (!proceed)
            {
                // Exit, Esc, X or Alt+F4 on the reminder: quit without touching any
                // adapter and without saving the opt-out.
                Close();
                return;
            }
            if (!remember) return;
            try
            {
                EnsureAppDir();
                File.WriteAllText(DhcpReminderFile, "Return to DHCP reminder dismissed in v" + BuildInfo.DisplayVersion + "\r\n"); DebugLog.LogSettingsFile(DhcpReminderFile, "save-reminder");
            }
            catch (Exception ex)
            {
                Log("Could not save the reminder setting: " + ex.Message, "WARN");
            }
        }

        protected override void OnLocationChanged(EventArgs e)
        {
            base.OnLocationChanged(e);
            if (!shown || fittingWorkingArea) return;

            Rectangle workingArea = Screen.FromControl(this).WorkingArea;
            if (workingArea != fittedWorkingArea)
                FitToWorkingArea(true);
        }

        // The optional update banner shares reserved space with the left-column status.
        // No captioned controls are resized, so text retains its designed DPI-scaled bounds.
        void ApplyLeftColumnStatusLayout()
        {
            lblStatus.Location = P(20, updateBanner.Visible ? 512 : 472);
        }

        // If an unusually large DPI makes the full design wider or taller than the
        // working area, give up console viewport space first. The console has its own
        // scrollbars; action buttons and their captions always retain their design size.
        void ApplyRightColumnLayout(int availableWidth, int availableHeight)
        {
            int consoleWidth = Math.Min(U(520), Math.Max(U(270), availableWidth - U(580)));
            int consoleHeight = Math.Min(U(466), Math.Max(U(140), availableHeight - U(94)));
            console.Size = new Size(consoleWidth, consoleHeight);
            btnClear.Location = new Point(console.Right - U(80), U(14));
            btnAbout.Location = new Point(console.Right - btnAbout.Width, console.Bottom + U(10));
        }

        void FitToWorkingArea(bool center)
        {
            if (fittingWorkingArea || !IsHandleCreated) return;
            fittingWorkingArea = true;
            try
            {
                Rectangle workingArea = Screen.FromControl(this).WorkingArea;
                fittedWorkingArea = workingArea;

                // WorkingArea excludes the taskbar. Leave a small physical-pixel margin
                // on every side so borders and taskbar auto-hide affordances remain clear.
                int margin = Math.Max(8, U(8));
                int maxOuterWidth = Math.Max(1, workingArea.Width - margin * 2);
                int maxOuterHeight = Math.Max(1, workingArea.Height - margin * 2);
                int chromeWidth = Math.Max(0, Width - ClientSize.Width);
                int chromeHeight = Math.Max(0, Height - ClientSize.Height);
                int maxClientWidth = Math.Max(1, maxOuterWidth - chromeWidth);
                int maxClientHeight = Math.Max(1, maxOuterHeight - chromeHeight);

                int designedWidth = U(1100);
                int designedHeight = U(560);
                int protectedWidth = U(850);
                int protectedHeight = U(540);
                bool needsWholeFormScrolling =
                    maxClientWidth < protectedWidth || maxClientHeight < protectedHeight;
                AutoScroll = needsWholeFormScrolling;
                AutoScrollMinSize = needsWholeFormScrolling
                    ? new Size(designedWidth, designedHeight)
                    : Size.Empty;

                MaximumSize = new Size(maxOuterWidth, maxOuterHeight);
                ClientSize = new Size(
                    Math.Min(designedWidth, maxClientWidth),
                    Math.Min(designedHeight, maxClientHeight));
                ApplyLeftColumnStatusLayout();
                ApplyRightColumnLayout(
                    needsWholeFormScrolling ? designedWidth : ClientSize.Width,
                    needsWholeFormScrolling ? designedHeight : ClientSize.Height);

                if (center)
                {
                    int x = workingArea.Left + (workingArea.Width - Width) / 2;
                    int y = workingArea.Top + (workingArea.Height - Height) / 2;
                    Location = new Point(
                        Math.Max(workingArea.Left + margin, x),
                        Math.Max(workingArea.Top + margin, y));
                }
            }
            finally
            {
                fittingWorkingArea = false;
            }
        }

        // Give scrollable controls dark scrollbars. WinForms paints scrollbars with the OS
        // theme and ignores BackColor, so this is the only way to darken them.
        // ComboBoxes use DarkMode_CFD (and ThemedComboBox chrome); Explorer does not
        // style combo fields, arrows, or the open list.
        void DarkenScrollbars(Control.ControlCollection cc)
        {
            foreach (Control c in cc)
            {
                if (c is ComboBox)
                {
                    ThemedComboBox themed = c as ThemedComboBox;
                    if (themed != null) themed.ApplyDark(true);
                    else WindowTheme.ApplyCombo((ComboBox)c, true);
                }
                else if (c is TextBox || c is ListBox)
                {
                    try { WindowTheme.SetWindowTheme(c.Handle, "DarkMode_Explorer", null); } catch { }
                }
                if (c.Controls.Count > 0) DarkenScrollbars(c.Controls);
            }
        }

        // Draw the closed field ourselves so Windows does not paint the selected
        // adapter with the system highlight merely because the ComboBox has focus.
        // The open list still highlights its active row so keyboard/mouse selection
        // remains obvious.
        void DrawAdapterItem(object sender, DrawItemEventArgs e)
        {
            if (e.Bounds.Width <= 0 || e.Bounds.Height <= 0) return;
            ComboBox combo = (ComboBox)sender;

            bool editField = (e.State & DrawItemState.ComboBoxEdit) != 0;
            bool selectedListItem =
                combo.DroppedDown && !editField && (e.State & DrawItemState.Selected) != 0;

            Color back = combo.BackColor;
            Color fore = combo.ForeColor;
            if (selectedListItem)
            {
                back = dark ? Color.FromArgb(70, 70, 70) : SystemColors.Highlight;
                fore = dark ? FgCol : SystemColors.HighlightText;
            }
            else if (!combo.Enabled || (e.State & DrawItemState.Disabled) != 0)
            {
                fore = dark ? SubCol : SystemColors.GrayText;
            }

            using (SolidBrush brush = new SolidBrush(back))
                e.Graphics.FillRectangle(brush, e.Bounds);

            string text =
                e.Index >= 0 && e.Index < combo.Items.Count
                    ? combo.Items[e.Index].ToString()
                    : combo.Text;
            Rectangle textBounds = new Rectangle(
                e.Bounds.X + U(3), e.Bounds.Y,
                Math.Max(0, e.Bounds.Width - U(6)), e.Bounds.Height);
            TextRenderer.DrawText(
                e.Graphics, text, combo.Font, textBounds, fore,
                TextFormatFlags.Left | TextFormatFlags.VerticalCenter |
                TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix |
                TextFormatFlags.NoPadding);
        }

        // Detect the OS theme and, if dark, recolor the controls. Light mode is left
        // exactly as designed. Called at the end of BuildUi.
        void ApplyTheme()
        {
            dark = IsDarkMode();
            if (!dark) return;

            BgCol     = Color.FromArgb(32, 32, 32);
            PanelCol  = Color.FromArgb(50, 50, 50);
            InputCol  = Color.FromArgb(24, 24, 24);
            FgCol     = Color.FromArgb(230, 230, 230);
            SubCol    = Color.FromArgb(150, 150, 150);
            BorderCol = Color.FromArgb(80, 80, 80);

            this.BackColor = BgCol;
            StyleTree(this.Controls);
        }

        void StyleTree(Control.ControlCollection cc)
        {
            foreach (Control c in cc)
            {
                if (c == console) { /* already dark and readable - leave it */ }
                else if (c == btnBind || c == btnDhcp) { /* accent buttons keep their color */ }
                else if (c == lblStatus) { c.ForeColor = FgCol; } // live color set in SetBusy/EndBusy
                else if (c is Panel)
                {
                    c.BackColor = PanelCol;
                    c.ForeColor = FgCol;
                }
                else if (c is Button)
                {
                    Button b = (Button)c;
                    b.FlatStyle = FlatStyle.Flat;
                    b.BackColor = PanelCol;
                    b.ForeColor = FgCol;
                    b.FlatAppearance.BorderColor = BorderCol;
                }
                else if (c is TextBox)
                {
                    c.BackColor = InputCol;
                    c.ForeColor = FgCol;
                    ((TextBox)c).BorderStyle = BorderStyle.FixedSingle;
                }
                else if (c is ComboBox)
                {
                    c.BackColor = InputCol;
                    c.ForeColor = FgCol;
                    ThemedComboBox themed = c as ThemedComboBox;
                    if (themed != null) themed.ApplyDark(true);
                    else ((ComboBox)c).FlatStyle = FlatStyle.Standard;
                }
                else if (c is LinkLabel)
                {
                    LinkLabel ll = (LinkLabel)c;
                    ll.ForeColor = SubCol;
                    ll.LinkColor = Color.FromArgb(90, 160, 240);
                }
                else if (c is CheckBox)
                {
                    c.ForeColor = FgCol;
                    ThemedCheckBox themedBox = c as ThemedCheckBox;
                    if (themedBox != null) themedBox.ApplyDark(true);
                }
                else if (c is Label)
                {
                    // italic = the subtitle; keep it dimmer for hierarchy. Others full strength.
                    c.ForeColor = (c.Font != null && c.Font.Italic) ? SubCol : FgCol;
                }

                if (c.Controls.Count > 0) StyleTree(c.Controls);
            }
        }

        void BuildUi()
        {
            this.Text = BuildInfo.AppName + " v" + BuildInfo.DisplayVersion;
            this.Font = new Font("Segoe UI", 9F);
            this.AutoScaleMode = AutoScaleMode.None;
            this.FormBorderStyle = FormBorderStyle.FixedSingle;
            this.MaximizeBox = false;
            this.StartPosition = FormStartPosition.CenterScreen;
            this.BackColor = Color.White;
            try
            {
                System.IO.Stream ico = typeof(MainForm).Assembly.GetManifestResourceStream("IPbind.app.ico");
                if (ico != null) this.Icon = new Icon(ico);
                else this.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath);
            }
            catch { try { this.Icon = Icon.ExtractAssociatedIcon(Application.ExecutablePath); } catch { } }

            // read the real device DPI WITHOUT creating this form's handle early
            // (creating it early breaks CenterScreen - the window centers at its default
            //  size, then grows, ending up too low). The desktop DC gives system DPI.
            try { using (Graphics g = Graphics.FromHwnd(IntPtr.Zero)) { sc = g.DpiX / 96f; } }
            catch { sc = 1f; }

            this.ClientSize = Z(1100, 560);
            this.SuspendLayout();

            Label lblTitle = new Label();
            lblTitle.Text = BuildInfo.AppName;
            lblTitle.Font = UiFont(16F, FontStyle.Bold);
            // 8 to 38, ending exactly where the subtitle starts, so the 16 pt title
            // is not clipped at 150% and never overlaps the subtitle after rounding.
            lblTitle.Location = P(20, 8);
            lblTitle.Size = new Size(U(520), U(38) - U(8));
            lblTitle.TextAlign = ContentAlignment.MiddleCenter;
            Controls.Add(lblTitle);

            Label lblSub = new Label();
            lblSub.Text = "Static IP Binder for easily accessing air-gapped equipment spread across multiple IP ranges";
            lblSub.Font = UiFont(9F, FontStyle.Italic);
            lblSub.Location = P(20, 38);
            lblSub.Size = Z(520, 34);
            lblSub.TextAlign = ContentAlignment.MiddleCenter;
            lblSub.ForeColor = Color.FromArgb(90, 90, 90);
            Controls.Add(lblSub);

            Label lblAdapter = new Label();
            lblAdapter.Text = "Select your LAN interface below:";
            lblAdapter.Location = P(20, 74);
            lblAdapter.AutoSize = true;
            Controls.Add(lblAdapter);

            combo = new ThemedComboBox();
            combo.Location = P(20, 94);
            combo.Size = Z(420, 24);
            combo.DropDownStyle = ComboBoxStyle.DropDownList;
            combo.DrawMode = DrawMode.OwnerDrawFixed;
            combo.DrawItem += DrawAdapterItem;
            combo.DropDown += delegate { combo.Invalidate(); };
            combo.DropDownClosed += delegate { combo.Invalidate(); };
            combo.SelectedIndexChanged += delegate
            {
                string a = GetSelectedAlias();
                if (a != null) { SaveAdapter(a); Log("Interface set to '" + a + "' (remembered)."); }
                RefreshKeepDhcp(a);
            };
            Controls.Add(combo);

            // Keep DHCP and Gateway support share one row above Refresh, 28 px apart
            // as before. The pair is placed below, once Refresh exists, so the last
            // letter of "Gateway support" ends at Refresh's right edge.
            chkKeepDhcp = new ThemedCheckBox();
            chkKeepDhcp.Text = "Keep DHCP";
            chkKeepDhcp.Font = UiFont(8F, FontStyle.Regular);
            chkKeepDhcp.Size = new Size(OptionWidth(chkKeepDhcp), U(20));
            chkKeepDhcp.CheckAlign = ContentAlignment.MiddleLeft;
            chkKeepDhcp.TextAlign = ContentAlignment.MiddleLeft;
            chkKeepDhcp.Checked = true;
            chkKeepDhcp.CheckedChanged += delegate
            {
                if (settingKeepDhcp) return;
                keepDhcpPref = chkKeepDhcp.Checked;
                SaveKeepDhcpPref();
                Log(keepDhcpPref
                    ? "Keep DHCP on (remembered). Apply adds the addresses alongside DHCP."
                    : "Keep DHCP off (remembered). Apply sets static addresses only.");
            };
            Controls.Add(chkKeepDhcp);
            keepDhcpTip = new ToolTip();
            keepDhcpTip.AutoPopDelay = 10000;
            keepDhcpTip.InitialDelay = 400;
            keepDhcpTip.ReshowDelay = 200;
            keepDhcpTip.ShowAlways = true;
            keepDhcpTip.SetToolTip(chkKeepDhcp, KeepDhcpTipOn);

            Button btnRefresh = MakeButton("Refresh", 448, 93, 92, 26);
            btnRefresh.Click += delegate { using (DebugLog.Time("Refresh")) LoadAdapters(); };
            Controls.Add(btnRefresh);

            chkAll = new ThemedCheckBox();
            chkAll.Text = "Show all adapters (include virtual / cellular / VPN)";
            chkAll.Font = UiFont(8F, FontStyle.Regular);
            chkAll.Location = P(20, 122);
            chkAll.Size = Z(370, 20);
            chkAll.CheckedChanged += delegate { LoadAdapters(); };
            Controls.Add(chkAll);

            chkGateway = new ThemedCheckBox();
            chkGateway.Text = "Gateway support";
            chkGateway.Font = UiFont(8F, FontStyle.Regular);
            chkGateway.Size = new Size(OptionWidth(chkGateway), U(20));
            chkGateway.CheckAlign = ContentAlignment.MiddleLeft;
            chkGateway.TextAlign = ContentAlignment.MiddleLeft;
            chkGateway.CheckedChanged += delegate
            {
                ApplyGatewayLayout();
                SaveGatewaySettings();
                if (!loadingGateway)
                    Log(chkGateway.Checked
                        ? "Gateway support on (remembered). Choose one address and enter its gateway and optional DNS."
                        : "Gateway support off (remembered). Static IPs only, no gateway or DNS.");
            };
            Controls.Add(chkGateway);
            // Row y 72-92: under the subtitle (which ends at 72), above Refresh (93),
            // level with "Select your LAN interface below:".
            chkGateway.Location = new Point(btnRefresh.Right - TextInkRight(chkGateway), U(72));
            chkKeepDhcp.Location = new Point(chkGateway.Left - U(28) - chkKeepDhcp.Width, U(72));

            // Gateway support is off by default; warn before someone turns it on.
            gatewayTip = new ToolTip();
            gatewayTip.AutoPopDelay = 10000;
            gatewayTip.InitialDelay = 400;
            gatewayTip.ReshowDelay = 200;
            gatewayTip.ShowAlways = true;
            gatewayTip.SetToolTip(chkGateway, "Only enable if you know what you're doing!");

            lblIPs = new Label();
            lblIPs.Text = IpLabelPlain;
            lblIPs.Font = UiFont(8F, FontStyle.Regular);
            lblIPs.Location = P(20, 146);
            lblIPs.Size = Z(530, 22);
            lblIPs.TextAlign = ContentAlignment.MiddleLeft;
            Controls.Add(lblIPs);

            txtIPs = new AutoHideScrollBox();
            txtIPs.Multiline = true;
            txtIPs.ScrollBars = ScrollBars.None;
            txtIPs.Location = P(20, 170);
            txtIPs.Size = Z(520, 196);
            txtIPs.Font = new Font("Consolas", 10F);
            ((AutoHideScrollBox)txtIPs).SetAreaName("ips");
            txtIPs.TextChanged += delegate
            {
                RefreshGatewayChoices();
                AutoHideScrollBox ips = txtIPs as AutoHideScrollBox;
                if (ips != null) ips.SyncThumbOnly();
            };
            Controls.Add(txtIPs);

            // Gateway rows (hidden until Gateway support is on). They sit in the bottom
            // 62px of the IP list's normal area, which the list gives up when shown.
            lblGwOn = MakeRowLabel("Use gateway on:", 20, 310);
            cboGwIp = new ThemedComboBox();
            cboGwIp.Location = P(122, 310);
            cboGwIp.Size = Z(150, 24);
            cboGwIp.DropDownStyle = ComboBoxStyle.DropDownList;
            cboGwIp.DrawMode = DrawMode.OwnerDrawFixed;
            cboGwIp.DrawItem += DrawAdapterItem;
            cboGwIp.DropDown += delegate { cboGwIp.Invalidate(); };
            cboGwIp.DropDownClosed += delegate { cboGwIp.Invalidate(); };
            cboGwIp.SelectionChangeCommitted += delegate
            {
                gwChoice = cboGwIp.SelectedIndex >= 0 ? (string)cboGwIp.SelectedItem : "";
                SaveGatewaySettings();
            };
            Controls.Add(cboGwIp);
            lblGateway = MakeRowLabel("Gateway:", 284, 310);
            txtGateway = MakeIpBox(352, 310);
            lblDns1 = MakeRowLabel("DNS 1:", 20, 339);
            txtDns1 = MakeIpBox(122, 339);
            txtDns1.Width = U(150);
            lblDns2 = MakeRowLabel("DNS 2:", 284, 339);
            txtDns2 = MakeIpBox(352, 339);

            Button btnSaveList = MakeButton("Save List", 20, 372, 166, 28);
            btnSaveList.Click += delegate { SaveList(false); };
            Controls.Add(btnSaveList);

            Button btnRestore = MakeButton("Restore Defaults", 197, 372, 166, 28);
            btnRestore.Click += delegate { RestoreDefaults(); };
            Controls.Add(btnRestore);

            Button btnShow = MakeButton("Show Current", 374, 372, 166, 28);
            btnShow.Click += delegate
            {
                using (DebugLog.Time("Show Current"))
                {
                    string a = GetSelectedAlias();
                    if (a == null) { Log("No interface selected.", "WARN"); return; }
                    ShowCurrentConfig(a);
                }
            };
            Controls.Add(btnShow);

            btnBind = MakeButton(UiCopy.ApplyButton, 20, 406, 255, 60);
            btnBind.Font = UiFont(12F, FontStyle.Bold);
            btnBind.BackColor = UiCopy.ApplyButtonBack;
            btnBind.ForeColor = Color.White;
            btnBind.FlatStyle = FlatStyle.Flat;
            btnBind.FlatAppearance.BorderSize = 0;
            btnBind.Click += delegate
            {
                using (DebugLog.Time("Apply"))
                {
                    string alias = GetSelectedAlias();
                    string before = CaptureAdapterState(alias);
                    SetBusy("Applying static IPs...");
                    bool ok = InvokeBind();
                    EndBusy(ok, "Static IPs applied",
                                "Failed to apply static IPs");
                    LogAdapterDiff("Apply", before, CaptureAdapterState(alias));
                }
            };
            Controls.Add(btnBind);

            btnDhcp = MakeButton(UiCopy.DhcpButton, 285, 406, 255, 60);
            btnDhcp.Font = UiFont(12F, FontStyle.Bold);
            btnDhcp.BackColor = UiCopy.DhcpButtonBack;
            btnDhcp.ForeColor = Color.White;
            btnDhcp.FlatStyle = FlatStyle.Flat;
            btnDhcp.FlatAppearance.BorderSize = 0;
            btnDhcp.Click += delegate
            {
                using (DebugLog.Time("Return to DHCP"))
                {
                    string alias = GetSelectedAlias();
                    string before = CaptureAdapterState(alias);
                    SetBusy("Returning to DHCP...");
                    bool ok = InvokeDhcp();
                    EndBusy(ok, "Returned to DHCP",
                                "Failed to return to DHCP");
                    LogAdapterDiff("Return", before, CaptureAdapterState(alias));
                }
            };
            Controls.Add(btnDhcp);

            lblStatus = new Label();
            lblStatus.Text = "Ready.";
            lblStatus.Font = UiFont(10F, FontStyle.Bold);
            lblStatus.Location = P(20, 472);
            lblStatus.Size = Z(520, 38);
            lblStatus.TextAlign = ContentAlignment.MiddleCenter;
            lblStatus.ForeColor = Color.FromArgb(60, 60, 60);
            Controls.Add(lblStatus);

            Label lblConsole = new Label();
            lblConsole.Text = "Console output:";
            lblConsole.Location = P(560, 18);
            lblConsole.AutoSize = true;
            Controls.Add(lblConsole);

            btnClear = MakeButton("Clear", 1000, 14, 80, 24);
            btnClear.Click += delegate
            {
                console.Clear();
                AutoHideScrollBox box = console as AutoHideScrollBox;
                if (box != null) box.SyncThumbOnly();
            };
            Controls.Add(btnClear);

            console = new AutoHideScrollBox();
            console.Multiline = true;
            console.ScrollBars = ScrollBars.None;
            console.ReadOnly = true;
            console.Location = P(560, 44);
            console.Size = Z(520, 466);
            console.Font = new Font("Consolas", 9F);
            console.BackColor = Color.FromArgb(18, 18, 18);
            console.ForeColor = Color.FromArgb(210, 210, 210);
            ((AutoHideScrollBox)console).SetAreaName("console");
            Controls.Add(console);

            updateBanner = new Panel();
            updateBanner.Location = P(20, 472);
            updateBanner.Size = Z(520, 38);
            updateBanner.BorderStyle = BorderStyle.FixedSingle;
            updateBanner.BackColor = Color.FromArgb(245, 247, 250);
            updateBanner.Visible = false;

            lblUpdateBanner = new Label();
            lblUpdateBanner.Location = P(8, 5);
            lblUpdateBanner.Size = Z(294, 26);
            lblUpdateBanner.TextAlign = ContentAlignment.MiddleLeft;
            lblUpdateBanner.AutoEllipsis = true;
            updateBanner.Controls.Add(lblUpdateBanner);

            btnUpdateNow = MakeButton("Update", 308, 5, 96, 26);
            btnUpdateNow.Click += InstallUpdateFromBanner;
            updateBanner.Controls.Add(btnUpdateNow);

            btnUpdateLater = MakeButton("Later", 410, 5, 96, 26);
            btnUpdateLater.Click += DismissUpdateBanner;
            updateBanner.Controls.Add(btnUpdateLater);
            Controls.Add(updateBanner);

            // 984 = console left (560) + console width (520) - About width (96),
            // so the button is right-aligned with the console from the first paint.
            btnAbout = MakeButton("About", 984, 520, 96, 26);
            btnAbout.Font = UiFont(8F, FontStyle.Regular);
            btnAbout.Click += delegate
            {
                using (DebugLog.Time("About"))
                using (AboutForm about = new AboutForm(this, dark, sc, UpdateCheckFile))
                    about.ShowDialog(this);
            };
            Controls.Add(btnAbout);

            ApplyTheme();
            this.ResumeLayout(true);
        }
    }
}
