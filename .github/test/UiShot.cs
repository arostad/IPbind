// Throwaway 2.3.136 UI harness: reminder and About shots and layout checks, font
// weights, simulated and real WM_DPICHANGED (live monitor-scale changes through
// DisplayConfigSetDeviceInfo), checkbox close-ups after a high-to-low DPI move,
// and icon checks. Not part of the app.
using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Windows.Forms;

namespace IPbindTest
{
    static class UiShot
    {
        const BindingFlags F = BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public;
        const int WM_DPICHANGED = 0x02E0;

        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int Left, Top, Right, Bottom; }

        [DllImport("user32.dll")] static extern IntPtr SendMessage(IntPtr hWnd, int msg, IntPtr wParam, IntPtr lParam);
        [DllImport("user32.dll")] static extern IntPtr SetThreadDpiAwarenessContext(IntPtr value);
        [DllImport("user32.dll")] static extern int GetDpiForWindow(IntPtr hwnd);
        [DllImport("user32.dll")] static extern int GetDpiForSystem();
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] static extern bool GetClientRect(IntPtr h, out RECT r);
        [DllImport("user32.dll")] static extern bool ClientToScreen(IntPtr h, ref Point p);
        [DllImport("user32.dll")] static extern IntPtr MonitorFromWindow(IntPtr h, int flags);
        [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr m, ref MONITORINFO mi);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern int PrivateExtractIcons(string file, int index, int cx, int cy, IntPtr[] icons, int[] ids, int n, int flags);
        [DllImport("user32.dll")] static extern bool DestroyIcon(IntPtr h);
        [DllImport("user32.dll")] static extern bool DrawIconEx(IntPtr hdc, int x, int y, IntPtr icon, int cx, int cy, int step, IntPtr brush, int flags);

        // Icon.ToBitmap throws on PNG-compressed frames; draw the HICON natively.
        static Bitmap IconBitmap(IntPtr hicon, int px)
        {
            Bitmap b = new Bitmap(px, px, PixelFormat.Format32bppArgb);
            using (Graphics g = Graphics.FromImage(b))
            {
                g.Clear(Color.Transparent);
                IntPtr hdc = g.GetHdc();
                try { DrawIconEx(hdc, 0, 0, hicon, px, px, 0, IntPtr.Zero, 3); }
                finally { g.ReleaseHdc(hdc); }
            }
            return b;
        }

        [StructLayout(LayoutKind.Sequential)]
        struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public int dwFlags; }

        static readonly IntPtr PMv2 = new IntPtr(-4);

        [STAThread]
        static int Main(string[] args)
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
            Application.ThreadException += delegate(object s, System.Threading.ThreadExceptionEventArgs e)
            { File.AppendAllText("shots/exceptions.txt", e.Exception + "\r\n"); };
            AppDomain.CurrentDomain.UnhandledException += delegate(object s, UnhandledExceptionEventArgs e)
            { File.AppendAllText("shots/exceptions.txt", e.ExceptionObject + "\r\n"); };

            string kind = args[0];
            if (kind == "popup") return ShotPopup(args[1] == "dark", Flt(args[2]), args[3], args[4]);
            if (kind == "about") return ShotAbout(args[1] == "dark", Flt(args[2]), args[3], args[4]);
            if (kind == "dpi") return SimDpi(args[1], args[2]);
            if (kind == "realdpi") return RealDpi(args[1], args[2]);
            if (kind == "setscale") { string r = ScaleControl.Set(int.Parse(args[1])); Console.WriteLine(r); File.WriteAllText(args[2], r); return r.StartsWith("OK") ? 0 : 1; }
            if (kind == "icons") return Icons(args[1], args[2], args[3]);
            if (kind == "main") return ShotMain(args[1], args[2]);
            if (kind == "tallwidth") return TallWidth(args[1]);
            if (kind == "tallshot") return TallShot(args[1], args[2]);
            if (kind == "aboutcheck") return AboutCheck(args[1], args[2]);
            return 2;
        }

        static float Flt(string s) { return float.Parse(s, System.Globalization.CultureInfo.InvariantCulture); }

        static void Pump(int ms)
        {
            DateTime end = DateTime.Now.AddMilliseconds(ms);
            while (DateTime.Now < end) { Application.DoEvents(); System.Threading.Thread.Sleep(15); }
        }

        // ---------- native geometry in PMv2 context (no virtualization) ----------
        static Rectangle WinRect(IntPtr h)
        {
            IntPtr prev = SetThreadDpiAwarenessContext(PMv2);
            try { RECT r; GetWindowRect(h, out r); return Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom); }
            finally { SetThreadDpiAwarenessContext(prev); }
        }
        static Rectangle ClientRectScreen(IntPtr h)
        {
            IntPtr prev = SetThreadDpiAwarenessContext(PMv2);
            try { RECT r; GetClientRect(h, out r); Point p = Point.Empty; ClientToScreen(h, ref p); return new Rectangle(p.X, p.Y, r.Right, r.Bottom); }
            finally { SetThreadDpiAwarenessContext(prev); }
        }
        static Rectangle WorkArea(IntPtr h)
        {
            IntPtr prev = SetThreadDpiAwarenessContext(PMv2);
            try
            {
                MONITORINFO mi = new MONITORINFO(); mi.cbSize = Marshal.SizeOf(typeof(MONITORINFO));
                GetMonitorInfo(MonitorFromWindow(h, 2), ref mi);
                return Rectangle.FromLTRB(mi.rcWork.Left, mi.rcWork.Top, mi.rcWork.Right, mi.rcWork.Bottom);
            }
            finally { SetThreadDpiAwarenessContext(prev); }
        }
        static void Capture(IntPtr h, string png)
        {
            IntPtr prev = SetThreadDpiAwarenessContext(PMv2);
            try
            {
                Rectangle b = WinRect(h);
                using (Bitmap bmp = new Bitmap(Math.Max(1, b.Width), Math.Max(1, b.Height)))
                {
                    using (Graphics g = Graphics.FromImage(bmp)) g.CopyFromScreen(b.Location, Point.Empty, b.Size);
                    bmp.Save(png, ImageFormat.Png);
                }
            }
            catch (Exception ex) { File.AppendAllText("shots/capture-errors.txt", png + " " + ex.Message + "\r\n"); }
            finally { SetThreadDpiAwarenessContext(prev); }
        }

        // ---------- geometry assertions shared by the DPI tests ----------
        static int CheckGeometry(Form f, string tag, List<string> lines, bool requireFit)
        {
            int faults = 0;
            IPbind.PerMonitorDialog d = f as IPbind.PerMonitorDialog;
            Rectangle win = WinRect(f.Handle);
            Rectangle client = ClientRectScreen(f.Handle);
            Rectangle work = WorkArea(f.Handle);
            int wdpi = GetDpiForWindow(f.Handle);
            Size layout = d.LayoutClientSize;
            string line = tag + " dpi=" + wdpi + " scale=" + d.LayoutScale + " window=" + win + " client=" + client.Size
                + " layout=" + layout + " work=" + work;
            if (client.Size != layout) { line += "  FAIL client!=layout"; faults++; }
            if (Math.Abs(d.LayoutScale - wdpi / 96f) > 0.001f && wdpi > 0 && requireFit) { line += "  FAIL scale!=dpi"; faults++; }
            foreach (string name in new string[] { "continueButton", "exitButton", "closeButton", "checkButton" })
            {
                FieldInfo fi = f.GetType().GetField(name, F);
                if (fi == null) continue;
                Control b = (Control)fi.GetValue(f);
                if (b == null || !b.IsHandleCreated) continue;
                Rectangle br = WinRect(b.Handle);
                bool inside = client.Contains(br);
                line += " " + name + "=" + br + (inside ? " in" : " OUT");
                if (!inside) { faults++; line += "  FAIL button outside client"; }
            }
            bool titleOn = win.Top >= work.Top && win.Left >= work.Left - 1 && win.Right <= work.Right + 1;
            bool fits = win.Bottom <= work.Bottom && titleOn;
            line += " titleOnScreen=" + titleOn + " fitsWork=" + fits;
            if (!titleOn) { faults++; line += "  FAIL title bar off screen"; }
            if (requireFit && !fits) { faults++; line += "  FAIL window outside work area"; }
            lines.Add((faults == 0 ? "OK " : "FAIL ") + line);
            return faults;
        }

        static string SendDpi(Form f, int toDpi)
        {
            Rectangle cur = WinRect(f.Handle);
            int fromDpi = (int)Math.Round(((IPbind.PerMonitorDialog)f).LayoutScale * 96);
            RECT r = new RECT();
            r.Left = cur.Left; r.Top = cur.Top;
            r.Right = r.Left + (int)Math.Round(cur.Width * (toDpi / (double)fromDpi));
            r.Bottom = r.Top + (int)Math.Round(cur.Height * (toDpi / (double)fromDpi));
            IntPtr mem = Marshal.AllocHGlobal(Marshal.SizeOf(typeof(RECT)));
            try
            {
                Marshal.StructureToPtr(r, mem, false);
                SendMessage(f.Handle, WM_DPICHANGED, (IntPtr)(toDpi | (toDpi << 16)), mem);
                return null;
            }
            catch (Exception ex) { return ex.GetType().Name + ": " + ex.Message; }
            finally { Marshal.FreeHGlobal(mem); }
        }

        // Simulated WM_DPICHANGED (window DPI really stays at the runner's DPI).
        static int SimDpi(string outDir, string report)
        {
            Directory.CreateDirectory(outDir);
            List<string> lines = new List<string>();
            int faults = 0;
            Form owner = new Form(); owner.ShowInTaskbar = false; owner.StartPosition = FormStartPosition.Manual;
            owner.Location = new Point(40, 40); owner.Size = new Size(400, 200); owner.Show();
            lines.Add("system dpi " + GetDpiForSystem());
            foreach (string which in new string[] { "popup", "about" })
            {
                string updateFile = Path.Combine(Path.GetTempPath(), "ipb-sim-update-check.txt");
                File.WriteAllText(updateFile, "auto");
                Form f = which == "popup" ? (Form)new IPbind.DhcpReminderForm(owner, true, 1.5f)
                    : new IPbind.AboutForm(owner, true, 1.5f, updateFile);
                f.StartPosition = FormStartPosition.Manual; f.Location = new Point(10, 10);
                f.Show(owner); Pump(600);
                PrepareChecked(f);
                faults += CheckGeometry(f, which + "-start-144sim", lines, false);
                int[] steps = new int[] { 96, 144, 96, 192, 96, 144, 192 };
                int prevDpi = 144;
                for (int i = 0; i < steps.Length; i++)
                {
                    string err = SendDpi(f, steps[i]);
                    Pump(500);
                    string name = which + "-sim-" + i + "-" + steps[i];
                    if (steps[i] == 96 && prevDpi > 96) name += "-after-" + prevDpi;
                    string png = Path.Combine(outDir, name + ".png");
                    Capture(f.Handle, png);
                    if (err != null) { lines.Add("FAIL " + name + " " + err); faults++; }
                    faults += CheckGeometry(f, name, lines, false);
                    faults += CheckCheckBoxPaint(f, png, Path.Combine(outDir, name + "-check"), lines, ((IPbind.PerMonitorDialog)f).LayoutScale);
                    prevDpi = steps[i];
                }
                foreach (string l in ((IPbind.PerMonitorDialog)f).DpiLog) lines.Add("  log " + l);
                f.Close(); f.Dispose();
            }
            owner.Close();
            if (File.Exists("shots/exceptions.txt")) { lines.Add("UNHANDLED:"); lines.Add(File.ReadAllText("shots/exceptions.txt")); faults++; }
            lines.Add("FAULTS=" + faults);
            File.WriteAllLines(report, lines.ToArray());
            return faults == 0 ? 0 : 1;
        }

        // Real per-monitor DPI changes: change the monitor's scale live while the
        // dialog is open. Windows then sends a genuine WM_DPICHANGED and changes
        // the window's frame metrics, as when dragging to another monitor.
        static int RealDpi(string outDir, string report)
        {
            Directory.CreateDirectory(outDir);
            List<string> lines = new List<string>();
            int faults = 0;
            int sys = GetDpiForSystem();
            lines.Add("system dpi " + sys + " scale state " + ScaleControl.Describe());
            Form owner = new Form(); owner.ShowInTaskbar = false; owner.StartPosition = FormStartPosition.Manual;
            owner.Location = new Point(40, 40); owner.Size = new Size(400, 200); owner.Show();
            foreach (string which in new string[] { "popup", "about" })
            {
                // Start on the system scale, exactly as MainForm does.
                string start = ScaleControl.Set(sys * 100 / 96);
                Pump(1500);
                lines.Add("reset to system scale: " + start);
                float sc = sys / 96f;
                string updateFile = Path.Combine(Path.GetTempPath(), "ipb-real-update-check.txt");
                File.WriteAllText(updateFile, "auto");
                Form f = which == "popup" ? (Form)new IPbind.DhcpReminderForm(owner, true, sc)
                    : new IPbind.AboutForm(owner, true, sc, updateFile);
                f.StartPosition = FormStartPosition.CenterScreen;
                f.Show(owner); Pump(1200);
                PrepareChecked(f);
                string startPng = Path.Combine(outDir, which + "-real-start-" + GetDpiForWindow(f.Handle) + ".png");
                Capture(f.Handle, startPng);
                faults += CheckGeometry(f, which + "-real-start", lines, true);
                faults += CheckCheckBoxPaint(f, startPng, Path.Combine(outDir, which + "-real-start-check"), lines, ((IPbind.PerMonitorDialog)f).LayoutScale);
                int[] pcts = new int[] { 100, 150, 100, 200, 100, 150, 200 };
                int prevPct = sys * 100 / 96;
                for (int i = 0; i < pcts.Length; i++)
                {
                    string r = ScaleControl.Set(pcts[i]);
                    int want = pcts[i] * 96 / 100;
                    DateTime end = DateTime.Now.AddSeconds(6);
                    while (DateTime.Now < end && GetDpiForWindow(f.Handle) != want) Pump(100);
                    Pump(1200);
                    int got = GetDpiForWindow(f.Handle);
                    string name = which + "-real-" + i + "-" + pcts[i];
                    if (pcts[i] == 100 && prevPct > 100) name += "-after-" + prevPct;
                    lines.Add("set " + pcts[i] + "%: " + r + " -> window dpi " + got);
                    if (!r.StartsWith("OK")) { lines.Add("SKIP " + name + " (scale not available on this display)"); prevPct = pcts[i]; continue; }
                    string png = Path.Combine(outDir, name + ".png");
                    Capture(f.Handle, png);
                    faults += CheckGeometry(f, name, lines, pcts[i] <= 150);
                    faults += CheckCheckBoxPaint(f, png, Path.Combine(outDir, name + "-check"), lines, ((IPbind.PerMonitorDialog)f).LayoutScale);
                    prevPct = pcts[i];
                }
                foreach (string l in ((IPbind.PerMonitorDialog)f).DpiLog) lines.Add("  log " + l);
                f.Close(); f.Dispose();
                Pump(300);
            }
            owner.Close();
            lines.Add("restore: " + ScaleControl.Set(sys * 100 / 96));
            if (File.Exists("shots/exceptions.txt")) { lines.Add("UNHANDLED:"); lines.Add(File.ReadAllText("shots/exceptions.txt")); faults++; }
            lines.Add("FAULTS=" + faults);
            File.WriteAllLines(report, lines.ToArray());
            return faults == 0 ? 0 : 1;
        }

        // ---------- static shots ----------
        static int ShotPopup(bool dark, float scale, string png, string report)
        {
            Form owner = new Form(); owner.ShowInTaskbar = false; owner.StartPosition = FormStartPosition.Manual;
            owner.Location = new Point(40, 40); owner.Size = new Size(800, 700); owner.Show();
            IPbind.DhcpReminderForm f = new IPbind.DhcpReminderForm(owner, dark, scale);
            f.StartPosition = FormStartPosition.Manual; f.Location = new Point(0, 0);
            List<string> lines = new List<string>();
            int rc = 0;
            Timer t = new Timer(); t.Interval = 2000;
            t.Tick += delegate
            {
                t.Stop();
                Capture(f.Handle, png);
                CheckLayout(f, lines, "popup sc=" + scale + " dark=" + dark);
                CheckGlyphAlignment(f, lines, scale);
                DumpFonts(f, lines);
                Size layout = f.LayoutClientSize;
                Rectangle client = ClientRectScreen(f.Handle);
                Rectangle win = WinRect(f.Handle);
                lines.Add("window " + win.Size + " client " + client.Size + " layout " + layout);
                lines.Add("FITS_1080P_WORK=" + (win.Width <= 1920 && win.Height <= 1040) + " (vs 1920x1040)");
                if (client.Size != layout) lines.Add("CLIENT MISMATCH");
                // checked state shot
                CheckCheckBoxPaint(f, png, png.Replace(".png", "-check"), lines, scale);
                CheckBox box = (CheckBox)typeof(IPbind.DhcpReminderForm).GetField("understandBox", F).GetValue(f);
                box.Checked = true; Pump(300);
                string ack = png.Replace(".png", "-ack.png");
                Capture(f.Handle, ack);
                CheckCheckBoxPaint(f, ack, png.Replace(".png", "-ack-check"), lines, scale);
                File.WriteAllLines(report, lines.ToArray());
                foreach (string line in lines)
                    if ((line.StartsWith("PROBLEMS=") && line != "PROBLEMS=0") || line.IndexOf("GLYPH NOT CENTERED") >= 0
                        || line.IndexOf("CLIENT MISMATCH") >= 0 || line.IndexOf("UNEXPECTED BOLD") >= 0) rc = 1;
                f.Close(); owner.Close();
            };
            f.Shown += delegate { f.Activate(); t.Start(); };
            f.ShowDialog(owner);
            return rc;
        }

        static int ShotAbout(bool dark, float scale, string png, string report)
        {
            Form owner = new Form(); owner.ShowInTaskbar = false; owner.StartPosition = FormStartPosition.Manual;
            owner.Location = new Point(40, 40); owner.Size = new Size(800, 700); owner.Show();
            IPbind.AboutForm f = new IPbind.AboutForm(owner, dark, scale, Path.Combine(Path.GetTempPath(), "ipb-about-update-check.txt"));
            f.StartPosition = FormStartPosition.Manual; f.Location = new Point(0, 0);
            List<string> lines = new List<string>();
            int rc = 0;
            Timer t = new Timer(); t.Interval = 2000;
            t.Tick += delegate
            {
                t.Stop();
                Capture(f.Handle, png);
                CheckLayout(f, lines, "about sc=" + scale + " dark=" + dark + " app=" + IPbind.BuildInfo.AppName);
                DumpFonts(f, lines);
                Control site = (Control)typeof(IPbind.AboutForm).GetField("website", F).GetValue(f);
                Control sep = (Control)typeof(IPbind.AboutForm).GetField("siteSeparator", F).GetValue(f);
                Control src = (Control)typeof(IPbind.AboutForm).GetField("sourceLink", F).GetValue(f);
                Control credit = (Control)typeof(IPbind.AboutForm).GetField("creditLink", F).GetValue(f);
                Control lic = (Control)typeof(IPbind.AboutForm).GetField("licenseLink", F).GetValue(f);
                bool oneLine = sep.Visible && src.Top == site.Top && sep.Left > site.Left && src.Left > sep.Left;
                lines.Add("header line: site=" + site.Bounds + " sep=" + sep.Bounds + " github='" + src.Text + "' " + src.Bounds + " oneLine=" + oneLine);
                if (!oneLine) lines.Add("HEADER NOT ONE LINE");
                LinkLabel sl = (LinkLabel)src, wl = (LinkLabel)site;
                if (sl.LinkColor != wl.LinkColor || !sl.Font.Equals(wl.Font)) lines.Add("LINK STYLE MISMATCH");
                bool stacked = credit.Left == lic.Left && lic.Top > credit.Bottom;
                int rightOf = 0;
                foreach (Control c in Walk(f))
                    if (c != credit && c != lic && c.Visible && c.Parent == f && c.Left > credit.Left
                        && c.Bottom > credit.Top && c.Top < lic.Bottom) rightOf++;
                lines.Add("credit=" + credit.Bounds + " license=" + lic.Bounds + " stacked=" + stacked + " controlsToRight=" + rightOf);
                if (!stacked || rightOf > 0) lines.Add("CREDIT NOT STACKED ALONE");
                CheckBox lc = (CheckBox)typeof(IPbind.AboutForm).GetField("launchCheckBox", F).GetValue(f);
                Label cap = (Label)typeof(IPbind.AboutForm).GetField("launchCheckLabel", F).GetValue(f);
                Size lt = TextRenderer.MeasureText(cap.Text, cap.Font, new Size(int.MaxValue, int.MaxValue), TextFormatFlags.NoPrefix | TextFormatFlags.SingleLine);
                bool lcFits = lc.Visible && cap.Visible
                    && cap.Text == "Check for updates at launch (recommended)"
                    && lt.Height <= cap.Height + 2 && cap.Right <= f.ClientSize.Width + 2
                    && lc.Right <= cap.Left + 2;
                lines.Add("launchCheckBox visible=" + lc.Visible + " checked=" + lc.Checked + " bounds=" + lc.Bounds
                    + " label='" + cap.Text + "' " + cap.Bounds + " text=" + lt + " fits=" + lcFits);
                if (!lcFits) lines.Add("LAUNCH CHECKBOX CLIPPED OR HIDDEN");
                Label priv = (Label)typeof(IPbind.AboutForm).GetField("privacyLabel", F).GetValue(f);
                string[] privLines = (priv.Text ?? "").Replace("\r\n", "\n").Split('\n');
                lines.Add("privacy lines=" + privLines.Length + " text=" + (priv.Text ?? "").Replace("\r\n", " | ") + " bounds=" + priv.Bounds);
                if (privLines.Length != 2
                    || privLines[0] != "Each update check sends one anonymous request to the IPbind update server."
                    || privLines[1] != "It counts only the country the request came from, for a rough guesstimate of the app\u2019s popularity (or lack thereof) for my own curiosity. No identifiers, cookies, or telemetry are used.")
                    lines.Add("PRIVACY WORDING");
                Size privNeed = TextRenderer.MeasureText(priv.Text ?? "", priv.Font, new Size(priv.Width, int.MaxValue),
                    TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding);
                lines.Add("privacy need=" + privNeed + " labelH=" + priv.Height);
                if (privNeed.Height > priv.Height + 2) lines.Add("PRIVACY CLIPPED");
                CheckCheckBoxPaint(f, png, png.Replace(".png", "-check"), lines, scale);
                Rectangle client = ClientRectScreen(f.Handle);
                lines.Add("window " + WinRect(f.Handle).Size + " client " + client.Size + " layout " + f.LayoutClientSize);
                if (client.Size != f.LayoutClientSize) lines.Add("CLIENT MISMATCH");
                File.WriteAllLines(report, lines.ToArray());
                foreach (string line in lines)
                    if ((line.StartsWith("PROBLEMS=") && line != "PROBLEMS=0") || line.IndexOf("HEADER NOT ONE LINE") >= 0
                        || line.IndexOf("CREDIT NOT STACKED") >= 0 || line.IndexOf("CLIENT MISMATCH") >= 0
                        || line.IndexOf("LINK STYLE MISMATCH") >= 0 || line.IndexOf("LAUNCH CHECKBOX") >= 0
                        || line.IndexOf("PRIVACY WORDING") >= 0 || line.IndexOf("PRIVACY CLIPPED") >= 0) rc = 1;
                f.Close(); owner.Close();
            };
            f.Shown += delegate { f.Activate(); t.Start(); };
            f.ShowDialog(owner);
            return rc;
        }

        static int ShotMain(string outPrefix, string report)
        {
            List<string> lines = new List<string>();
            IPbind.MainForm main = new IPbind.MainForm();
            main.Show(); Pump(2500);
            lines.Add("system dpi " + GetDpiForSystem() + " window " + WinRect(main.Handle) + " work " + WorkArea(main.Handle));
            int problems = 0;
            foreach (bool gw in new bool[] { false, true })
            {
                CheckBox g = (CheckBox)typeof(IPbind.MainForm).GetField("chkGateway", F).GetValue(main);
                g.Checked = gw; Pump(600);
                string png = outPrefix + (gw ? "-gateway" : "") + ".png";
                Capture(main.Handle, png);
                CheckLayout(main, lines, "main gateway=" + gw);
                problems += CheckAlign(main, png, lines);
                // overlaps between visible siblings (ignore labels inside the update banner)
                List<Control> vis = new List<Control>();
                foreach (Control c in main.Controls) if (c.Visible) vis.Add(c);
                for (int i = 0; i < vis.Count; i++)
                    for (int j = i + 1; j < vis.Count; j++)
                        if (vis[i].Bounds.IntersectsWith(vis[j].Bounds))
                        {
                            lines.Add("OVERLAP " + vis[i].GetType().Name + " '" + Short(vis[i].Text) + "' " + vis[i].Bounds
                                + " x " + vis[j].GetType().Name + " '" + Short(vis[j].Text) + "' " + vis[j].Bounds);
                            problems++;
                        }
                Rectangle w = WinRect(main.Handle), work = WorkArea(main.Handle);
                bool fits = work.Contains(w);
                lines.Add("window " + w + " fitsWork=" + fits);
                if (!fits) problems++;
                foreach (string l in lines) if (l.StartsWith("PROBLEMS=") && l != "PROBLEMS=0") problems++;
            }
            CheckBox g2 = (CheckBox)typeof(IPbind.MainForm).GetField("chkGateway", F).GetValue(main);
            g2.Checked = false; Pump(300);
            lines.Add("MAIN_PROBLEMS=" + problems);
            File.WriteAllLines(report, lines.ToArray());
            main.Close();
            return problems == 0 ? 0 : 1;
        }

        // Right edge of "Gateway support" text vs Refresh's right edge, from the screen
        // capture. Also writes a 6x zoom crop of the area.
        static int CheckAlign(IPbind.MainForm main, string png, List<string> lines)
        {
            CheckBox gw = (CheckBox)typeof(IPbind.MainForm).GetField("chkGateway", F).GetValue(main);
            CheckBox keep = (CheckBox)typeof(IPbind.MainForm).GetField("chkKeepDhcp", F).GetValue(main);
            Button refresh = null;
            foreach (Control c in main.Controls) if (c is Button && c.Text == "Refresh") refresh = (Button)c;
            if (refresh == null) { lines.Add("ALIGN no Refresh button"); return 1; }
            Rectangle win = WinRect(main.Handle);
            Rectangle gwS = gw.RectangleToScreen(gw.ClientRectangle);
            Rectangle rfS = refresh.RectangleToScreen(refresh.ClientRectangle);
            Rectangle kpS = keep.RectangleToScreen(keep.ClientRectangle);
            int ink = -1;
            using (Bitmap b = new Bitmap(png))
            {
                int ox = win.X, oy = win.Y;
                Color bg = b.GetPixel(gwS.Right - 1 - ox, gwS.Top + 1 - oy);
                for (int x = Math.Min(b.Width - 1, gwS.Right - 1 - ox); x >= gwS.Left - ox && ink < 0; x--)
                    for (int y = gwS.Top - oy; y < gwS.Bottom - oy; y++)
                    {
                        Color c = b.GetPixel(x, y);
                        if (Math.Abs(c.R - bg.R) + Math.Abs(c.G - bg.G) + Math.Abs(c.B - bg.B) > 3 * 100) { ink = x + ox + 1; break; }
                    }
                Rectangle all = Rectangle.Union(Rectangle.Union(kpS, gwS), rfS);
                Rectangle crop = Rectangle.FromLTRB(all.Left - 8 - ox, all.Top - 6 - oy, all.Right + 10 - ox, all.Bottom + 6 - oy);
                crop.Intersect(new Rectangle(0, 0, b.Width, b.Height));
                int z = 6;
                using (Bitmap zb = new Bitmap(crop.Width * z, crop.Height * z))
                {
                    using (Graphics g = Graphics.FromImage(zb))
                    {
                        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.NearestNeighbor;
                        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.Half;
                        g.DrawImage(b, new Rectangle(0, 0, zb.Width, zb.Height), crop, GraphicsUnit.Pixel);
                        using (Pen pen = new Pen(Color.Red, 1))
                        {
                            int lx = (rfS.Right - ox - crop.X) * z;
                            g.DrawLine(pen, lx, 0, lx, zb.Height);
                        }
                    }
                    zb.Save(png.Replace(".png", "-align-zoom.png"), ImageFormat.Png);
                }
            }
            int delta = ink - rfS.Right;
            int gap = gwS.Left - kpS.Right;
            lines.Add("ALIGN gatewayTextRight=" + ink + " refreshRight=" + rfS.Right + " delta=" + delta + " keep=" + kpS + " gateway=" + gwS + " refresh=" + rfS + " controlGap=" + gap
                + " sameRow=" + (kpS.Top == gwS.Top) + " aboveRefresh=" + (gwS.Bottom <= rfS.Top));
            int bad = 0;
            if (Math.Abs(delta) > 1) { lines.Add("ALIGN OFF"); bad++; }
            if (kpS.Top != gwS.Top || gwS.Bottom > rfS.Top) { lines.Add("ROW NOT ABOVE REFRESH"); bad++; }
            return bad;
        }

        static void DumpFonts(Form f, List<string> lines)
        {
            Dictionary<string, int> seen = new Dictionary<string, int>();
            foreach (Control c in Walk(f))
            {
                if (!c.Visible && c != f) continue;
                Font font = c.Font;
                string key = c.GetType().Name + " '" + Short(c.Text) + "' -> " + font.Name + " " + font.Style + " " + font.Size.ToString("0.##") + font.Unit;
                lines.Add("font " + key);
            }
            FieldInfo intro = f.GetType().GetField("intro", F);
            if (intro != null)
            {
                object runs = intro.GetValue(f);
                FieldInfo wordsF = runs.GetType().GetField("words", F);
                foreach (object w in (System.Collections.IEnumerable)wordsF.GetValue(runs))
                {
                    Font wf = (Font)w.GetType().GetField("Font", F).GetValue(w);
                    string txt = (string)w.GetType().GetField("Text", F).GetValue(w);
                    if (wf.Name.IndexOf("Semibold") >= 0 || wf.Style != FontStyle.Regular) lines.Add("font intro-run '" + txt.Trim() + "' -> " + wf.Name + " " + wf.Style);
                }
            }
            // Only the replica (700 in the mockup) and About's version line may be Bold.
            foreach (Control c in Walk(f))
            {
                if (!c.Visible || c == f) continue;
                if ((c.Font.Style & FontStyle.Bold) != 0 && c.GetType().Name != "ButtonReplica" && c.Text != ("Version " + IPbind.BuildInfo.DisplayVersion))
                    lines.Add("UNEXPECTED BOLD " + c.GetType().Name + " '" + Short(c.Text) + "'");
                if (!c.Font.Name.StartsWith("Segoe UI"))
                    lines.Add("UNEXPECTED FAMILY " + c.Font.Name);
                ButtonBase bb = c as ButtonBase;
                Label lb = c as Label;
                if ((bb != null && bb.UseCompatibleTextRendering) || (lb != null && lb.UseCompatibleTextRendering))
                    lines.Add("GDI+ TEXT " + c.GetType().Name + " '" + Short(c.Text) + "'");
            }
        }

        static string Short(string s) { s = (s ?? "").Replace("\r\n", " / "); return s.Length > 40 ? s.Substring(0, 40) + "..." : s; }

        // ---------- icons ----------
        static string Classify(Bitmap b)
        {
            int amber = 0, orange = 0, gray = 0, teal = 0, opaque = 0;
            for (int y = 0; y < b.Height; y++)
                for (int x = 0; x < b.Width; x++)
                {
                    Color c = b.GetPixel(x, y);
                    if (c.A < 128) continue;
                    opaque++;
                    if (c.R > 200 && c.G > 150 && c.G < 225 && c.B < 120) amber++;
                    else if (c.R > 200 && c.G > 70 && c.G < 150 && c.B < 90) orange++;
                    else if (Math.Abs(c.R - 46) < 14 && Math.Abs(c.G - 46) < 14 && Math.Abs(c.B - 48) < 14) gray++;
                    else if (c.G > 140 && c.B > 120 && c.R < 100) teal++;
                }
            string verdict = (orange > amber || orange * 20 > opaque) ? "ORANGE" : (teal > amber ? "TEAL" : (gray > opaque / 3 && amber > 0 ? "GRAY+AMBER" : "UNKNOWN"));
            return verdict + " (px " + b.Width + " opaque=" + opaque + " gray=" + gray + " amber=" + amber + " orange=" + orange + " teal=" + teal + ")";
        }

        static void SaveIcon(Bitmap b, string dir, string name) { try { b.Save(Path.Combine(dir, name + ".png"), ImageFormat.Png); } catch { } }

        static int Icons(string exe, string outDir, string report)
        {
            Directory.CreateDirectory(outDir);
            List<string> lines = new List<string>();
            int bad = 0;
            int[] sizes = new int[] { 16, 20, 24, 32, 48, 64, 256 };
            foreach (int s in sizes)
            {
                IntPtr[] h = new IntPtr[1]; int[] id = new int[1];
                int n = PrivateExtractIcons(exe, 0, s, s, h, id, 1, 0);
                if (n < 1 || h[0] == IntPtr.Zero) { lines.Add("exe " + s + ": none"); bad++; continue; }
                using (Bitmap b = IconBitmap(h[0], s))
                {
                    string v = Classify(b); lines.Add("exe-embedded " + s + ": " + v); SaveIcon(b, outDir, "exe-" + s);
                    if (!v.StartsWith("GRAY+AMBER")) bad++;
                }
                DestroyIcon(h[0]);
            }
            File.WriteAllText(Path.Combine(Path.GetTempPath(), "ipb-icons-update-check.txt"), "manual");
            IPbind.MainForm main = new IPbind.MainForm();
            main.StartPosition = FormStartPosition.Manual; main.Location = new Point(20, 20);
            main.Show(); Pump(1500);
            Form popup = new IPbind.DhcpReminderForm(main, true, 1f);
            popup.StartPosition = FormStartPosition.Manual; popup.Location = new Point(60, 60);
            popup.Show(main); Pump(600);
            Form about = new IPbind.AboutForm(main, true, 1f, Path.Combine(Path.GetTempPath(), "ipb-icons-update-check.txt"));
            about.StartPosition = FormStartPosition.Manual; about.Location = new Point(100, 100);
            about.Show(main); Pump(600);
            foreach (Form f in new Form[] { main, popup, about })
            {
                string tag = f.GetType().Name;
                foreach (int s in sizes)
                {
                    using (Icon sized = new Icon(f.Icon, s, s)) using (Bitmap b = IconBitmap(sized.Handle, s))
                    {
                        string v = Classify(b); lines.Add(tag + " Form.Icon " + s + ": " + v); SaveIcon(b, outDir, tag + "-formicon-" + s);
                        if (!v.StartsWith("GRAY+AMBER")) bad++;
                    }
                }
                foreach (int which in new int[] { 1, 0, 2 })
                {
                    IntPtr hi = SendMessage(f.Handle, 0x007F, (IntPtr)which, IntPtr.Zero); // WM_GETICON
                    string name = which == 1 ? "ICON_BIG" : which == 0 ? "ICON_SMALL" : "ICON_SMALL2";
                    if (hi == IntPtr.Zero) { lines.Add(tag + " WM_GETICON " + name + ": none"); if (which != 2) bad++; continue; }
                    using (Bitmap b = IconBitmap(hi, which == 1 ? 32 : 16))
                    {
                        string v = Classify(b); lines.Add(tag + " WM_GETICON " + name + ": " + v); SaveIcon(b, outDir, tag + "-wmgeticon-" + name);
                        if (!v.StartsWith("GRAY+AMBER")) bad++;
                    }
                }
            }
            about.Close(); popup.Close(); main.Close();
            lines.Add("BAD=" + bad);
            File.WriteAllLines(report, lines.ToArray());
            return bad == 0 ? 0 : 1;
        }

        // ---------- 2.3.134: width on tall high-resolution screens ----------
        // Lays the reminder out against a given work area (as the dialog does for
        // the monitor it opens on) and reports the logical width. On a screen that
        // has room, the width must be the design 640 at every scale.
        static int TallWidth(string report)
        {
            List<string> lines = new List<string>();
            int bad = 0;
            Form owner = new Form(); owner.ShowInTaskbar = false;
            FieldInfo workF = typeof(IPbind.PerMonitorDialog).GetField("layoutWork", F);
            MethodInfo layout = typeof(IPbind.DhcpReminderForm).GetMethod("LayoutContent", F);
            Rectangle[] works = new Rectangle[] {
                new Rectangle(0, 0, 1920, 1032), new Rectangle(0, 0, 2560, 1392),
                new Rectangle(0, 0, 3440, 1392), new Rectangle(0, 0, 3840, 2088) };
            foreach (Rectangle work in works)
                foreach (float sc in new float[] { 1f, 1.25f, 1.5f, 1.75f, 2f })
                {
                    IPbind.DhcpReminderForm f = new IPbind.DhcpReminderForm(owner, true, sc);
                    workF.SetValue(f, work);
                    layout.Invoke(f, null);
                    workF.SetValue(f, Rectangle.Empty);
                    Size c = f.LayoutClientSize;
                    float logicalW = c.Width / sc, logicalH = c.Height / sc;
                    // Content needs about 630 logical px plus a frame; if that fits the
                    // work area, nothing should be widened.
                    bool room = 680 * sc <= work.Height;
                    bool ok = !room || (Math.Abs(logicalW - 640) < 1.5f && logicalH >= 620);
                    lines.Add((ok ? "OK " : "FAIL ") + "work " + work.Width + "x" + work.Height + " scale " + sc
                        + " client " + c + " logical " + logicalW.ToString("0") + "x" + logicalH.ToString("0")
                        + (room ? " (room: expect 640 wide)" : " (short screen: may widen)"));
                    if (!ok) bad++;
                    f.Dispose();
                }
            owner.Dispose();
            lines.Add("BAD=" + bad);
            File.WriteAllLines(report, lines.ToArray());
            return bad == 0 ? 0 : 1;
        }

        // Picture of the reminder as laid out for a 3840x2088 work area (4K at
        // 175/200%). The runner display is 1080p, so the dialog is hosted as a
        // borderless child of a small form and painted with DrawToBitmap.
        static int TallShot(string outDir, string report)
        {
            Directory.CreateDirectory(outDir);
            List<string> lines = new List<string>();
            FieldInfo workF = typeof(IPbind.PerMonitorDialog).GetField("layoutWork", F);
            MethodInfo layout = typeof(IPbind.DhcpReminderForm).GetMethod("LayoutContent", F);
            Form owner = new Form(); owner.ShowInTaskbar = false;
            foreach (float sc in new float[] { 1f, 1.5f, 1.75f, 2f })
            {
                try
                {
                    IPbind.DhcpReminderForm f = new IPbind.DhcpReminderForm(owner, true, sc);
                    f.TopLevel = false;
                    f.FormBorderStyle = FormBorderStyle.None;
                    Form host = new Form(); host.ShowInTaskbar = false; host.StartPosition = FormStartPosition.Manual;
                    host.Location = new Point(0, 0); host.Size = new Size(300, 200);
                    host.Controls.Add(f);
                    host.Show(); f.Show(); Pump(300);
                    workF.SetValue(f, new Rectangle(0, 0, 3840, 2088));
                    layout.Invoke(f, null);
                    workF.SetValue(f, Rectangle.Empty);
                    Size c = f.LayoutClientSize;
                    f.Size = c; Pump(300);
                    using (Bitmap b = new Bitmap(c.Width, c.Height))
                    {
                        f.DrawToBitmap(b, new Rectangle(Point.Empty, c));
                        b.Save(Path.Combine(outDir, "popup-4k-" + ((int)Math.Round(sc * 100)) + ".png"), ImageFormat.Png);
                    }
                    lines.Add("scale " + sc + " client " + c + " logical " + (c.Width / sc).ToString("0") + "x" + (c.Height / sc).ToString("0") + " drawn " + f.Size);
                    host.Close(); f.Dispose();
                }
                catch (Exception ex) { lines.Add("scale " + sc + " ERROR " + ex.GetType().Name + ": " + ex.Message); }
            }
            File.WriteAllLines(report, lines.ToArray());
            return 0;
        }

        // About's update checkbox: always shown; turning it on goes through the
        // privacy consent (auto-accepted here), turning it off saves "manual".
        static int AboutCheck(string outDir, string report)
        {
            Directory.CreateDirectory(outDir);
            List<string> lines = new List<string>();
            int bad = 0;
            string file = Path.Combine(Path.GetTempPath(), "ipb-aboutcheck-update-check.txt");
            Form owner = new Form(); owner.ShowInTaskbar = false; owner.StartPosition = FormStartPosition.Manual;
            owner.Location = new Point(40, 40); owner.Size = new Size(400, 200); owner.Show();
            foreach (string start in new string[] { "manual", "auto" })
            {
                File.WriteAllText(file, start);
                IPbind.AboutForm f = new IPbind.AboutForm(owner, true, 1f, file);
                f.StartPosition = FormStartPosition.Manual; f.Location = new Point(60, 60);
                f.Show(owner); Pump(800);
                CheckBox lc = (CheckBox)typeof(IPbind.AboutForm).GetField("launchCheckBox", F).GetValue(f);
                Label cap = (Label)typeof(IPbind.AboutForm).GetField("launchCheckLabel", F).GetValue(f);
                bool expect = start == "auto";
                lines.Add("start=" + start + " visible=" + lc.Visible + " checked=" + lc.Checked + " label='" + cap.Text + "'");
                if (!lc.Visible || !cap.Visible || lc.Checked != expect) { lines.Add("FAIL initial state"); bad++; }
                if (cap.Text != "Check for updates at launch (recommended)") { lines.Add("FAIL label"); bad++; }
                Capture(f.Handle, Path.Combine(outDir, "about-start-" + start + ".png"));
                // Cancel the consent first: stays off.
                if (!expect)
                {
                    bool consentSeen = false;
                    Timer cancel = new Timer(); cancel.Interval = 700;
                    cancel.Tick += delegate
                    {
                        foreach (Form o in Application.OpenForms)
                            if (o is IPbind.PrivacyConsentForm) { consentSeen = true; cancel.Stop(); o.DialogResult = DialogResult.Cancel; break; }
                    };
                    cancel.Start();
                    lc.Checked = true; Pump(300); cancel.Stop();
                    string after = File.ReadAllText(file).Trim();
                    lines.Add("check+cancel: consentShown=" + consentSeen + " checked=" + lc.Checked + " file=" + after);
                    if (!consentSeen || lc.Checked || after != "manual") { lines.Add("FAIL cancel"); bad++; }
                    bool seen2 = false;
                    Timer agree = new Timer(); agree.Interval = 700;
                    agree.Tick += delegate
                    {
                        foreach (Form o in Application.OpenForms)
                            if (o is IPbind.PrivacyConsentForm) { seen2 = true; agree.Stop(); o.DialogResult = DialogResult.OK; break; }
                    };
                    agree.Start();
                    lc.Checked = true; Pump(600); agree.Stop();
                    after = File.ReadAllText(file).Trim();
                    lines.Add("check+agree: consentShown=" + seen2 + " checked=" + lc.Checked + " file=" + after);
                    if (!seen2 || !lc.Checked || after != "auto") { lines.Add("FAIL agree"); bad++; }
                    Capture(f.Handle, Path.Combine(outDir, "about-after-agree.png"));
                }
                lc.Checked = false; Pump(600);
                string off = File.ReadAllText(file).Trim();
                lines.Add("uncheck: checked=" + lc.Checked + " file=" + off);
                if (lc.Checked || off != "manual") { lines.Add("FAIL uncheck"); bad++; }
                Capture(f.Handle, Path.Combine(outDir, "about-after-uncheck-" + start + ".png"));
                f.Close(); f.Dispose(); Pump(300);
            }
            owner.Close();
            lines.Add("BAD=" + bad);
            File.WriteAllLines(report, lines.ToArray());
            return bad == 0 ? 0 : 1;
        }

        static void PrepareChecked(Form f)
        {
            IPbind.DhcpReminderForm popup = f as IPbind.DhcpReminderForm;
            if (popup != null)
            {
                CheckBox box = (CheckBox)typeof(IPbind.DhcpReminderForm).GetField("understandBox", F).GetValue(popup);
                box.Checked = true;
            }
            IPbind.AboutForm about = f as IPbind.AboutForm;
            if (about != null)
            {
                // Already opened with "auto" in the update-check file; keep it ticked
                // without going through the privacy consent dialog.
                CheckBox box = (CheckBox)typeof(IPbind.AboutForm).GetField("launchCheckBox", F).GetValue(about);
                FieldInfo silent = typeof(IPbind.AboutForm).GetField("settingLaunchCheck", F);
                silent.SetValue(about, true);
                try { box.Checked = true; }
                finally { silent.SetValue(about, false); }
            }
            Pump(300);
        }

        // Crop each RoundCheckBox from the window shot and fail if the check or
        // square overflows the control (the 2.3.135 mixed-DPI blow-out).
        static int CheckCheckBoxPaint(Form f, string windowPng, string cropPrefix, List<string> lines, float scale)
        {
            int faults = 0;
            if (!File.Exists(windowPng))
            {
                lines.Add("CHECKBOX no window shot " + windowPng);
                return 1;
            }
            Rectangle win = WinRect(f.Handle);
            using (Bitmap src = new Bitmap(windowPng))
            {
                int n = 0;
                foreach (Control c in Walk(f))
                {
                    IPbind.RoundCheckBox box = c as IPbind.RoundCheckBox;
                    if (box == null || !box.Visible || !box.IsHandleCreated) continue;
                    Rectangle screen = ControlScreenRect(box);
                    int x = screen.X - win.X, y = screen.Y - win.Y;
                    if (screen.Width < 8 || screen.Height < 8) continue;
                    Rectangle cropRc = new Rectangle(x, y, screen.Width, screen.Height);
                    cropRc.Intersect(new Rectangle(0, 0, src.Width, src.Height));
                    if (cropRc.Width < 8 || cropRc.Height < 8)
                    {
                        lines.Add("CHECKBOX crop missed " + box.AccessibleName + " screen=" + screen + " win=" + win);
                        faults++;
                        continue;
                    }
                    using (Bitmap crop = src.Clone(cropRc, src.PixelFormat))
                    {
                        string png = cropPrefix + "-" + n + ".png";
                        crop.Save(png, ImageFormat.Png);
                        int expectSide = IPbind.RoundCheckBox.SideFor(scale);
                        Color surface = box.Surface;
                        int minX = crop.Width, minY = crop.Height, maxX = -1, maxY = -1, ink = 0, edgeInk = 0;
                        for (int py = 0; py < crop.Height; py++)
                            for (int px = 0; px < crop.Width; px++)
                            {
                                Color p = crop.GetPixel(px, py);
                                int d = Math.Abs(p.R - surface.R) + Math.Abs(p.G - surface.G) + Math.Abs(p.B - surface.B);
                                if (d < 36) continue;
                                ink++;
                                if (px < minX) minX = px;
                                if (py < minY) minY = py;
                                if (px > maxX) maxX = px;
                                if (py > maxY) maxY = py;
                                if (px <= 1 || py <= 1 || px >= crop.Width - 2 || py >= crop.Height - 2) edgeInk++;
                            }
                        int inkW = maxX >= 0 ? maxX - minX + 1 : 0;
                        int inkH = maxY >= 0 ? maxY - minY + 1 : 0;
                        int edges = 0;
                        if (minX <= 1) edges++;
                        if (minY <= 1) edges++;
                        if (maxX >= crop.Width - 2) edges++;
                        if (maxY >= crop.Height - 2) edges++;
                        // A correct 20px box in a 26px control leaves a ring. A blown-out
                        // glyph fills the control and touches most edges.
                        bool blown = (edges >= 3 && edgeInk > Math.Max(8, crop.Width))
                            || inkW > expectSide + Math.Max(3, (int)Math.Round(4 * scale))
                            || inkH > expectSide + Math.Max(3, (int)Math.Round(4 * scale))
                            || crop.Width > expectSide + Math.Max(6, (int)Math.Round(8 * scale))
                            || crop.Height > expectSide + Math.Max(6, (int)Math.Round(8 * scale));
                        string row = "CHECKBOX '" + (box.AccessibleName ?? "") + "' checked=" + box.Checked
                            + " crop=" + crop.Width + "x" + crop.Height + " expectSide=" + expectSide
                            + " ink=" + inkW + "x" + inkH + " at " + minX + "," + minY
                            + " edges=" + edges + " edgeInk=" + edgeInk + " scale=" + scale
                            + " file=" + Path.GetFileName(png);
                        if (blown)
                        {
                            row += " BLOWN OUT";
                            faults++;
                        }
                        lines.Add(row);
                        n++;
                    }
                }
                if (n == 0)
                {
                    lines.Add("CHECKBOX none found on " + f.GetType().Name);
                    faults++;
                }
            }
            return faults;
        }

        static Rectangle ControlScreenRect(Control c)
        {
            IntPtr prev = SetThreadDpiAwarenessContext(PMv2);
            try
            {
                RECT r;
                if (GetWindowRect(c.Handle, out r)) return Rectangle.FromLTRB(r.Left, r.Top, r.Right, r.Bottom);
            }
            finally { SetThreadDpiAwarenessContext(prev); }
            return Rectangle.Empty;
        }

        // ---------- layout checks (from 2.3.132) ----------
        static void CheckGlyphAlignment(Form f, List<string> lines, float scale)
        {
            int slop = Math.Max(2, (int)Math.Round(2 * scale));
            foreach (Control c in Walk(f))
            {
                CheckBox box = c as CheckBox;
                if (box == null || !box.Visible) continue;
                Label caption = null; int best = int.MaxValue;
                foreach (Control sib in box.Parent.Controls)
                {
                    Label lab = sib as Label;
                    if (lab == null || !lab.Visible || string.IsNullOrEmpty(lab.Text)) continue;
                    int d = Math.Abs(lab.Left - box.Right) + Math.Abs(lab.Top - box.Top);
                    if (d < best) { best = d; caption = lab; }
                }
                if (caption == null) continue;
                int firstH = TextRenderer.MeasureText(caption.Text, caption.Font, new Size(int.MaxValue, int.MaxValue),
                    TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Height;
                if (firstH < caption.Font.Height) firstH = caption.Font.Height;
                int boxMid = box.Top + box.Height / 2, lineMid = caption.Top + firstH / 2;
                int delta = Math.Abs(boxMid - lineMid);
                lines.Add("glyph '" + caption.Text + "' box=" + box.Bounds + " boxMid=" + boxMid + " lineMid=" + lineMid + " delta=" + delta);
                if (delta > slop) lines.Add("GLYPH NOT CENTERED");
            }
        }

        static void CheckLayout(Form f, List<string> lines, string tag)
        {
            lines.Add("== " + tag);
            Rectangle client = f.ClientRectangle;
            int problems = 0;
            foreach (Control c in Walk(f))
            {
                if (!c.Visible || c == f) continue;
                Rectangle abs = f.RectangleToClient(c.Parent.RectangleToScreen(c.Bounds));
                string line = c.GetType().Name + " '" + Short(c.Text) + "' " + abs;
                if (abs.Right > client.Width + 2 || abs.Bottom > client.Height + 2 || abs.X < -2 || abs.Y < -2)
                { line += "  OUTSIDE CLIENT"; problems++; }
                if (!string.IsNullOrEmpty(c.Text) && !(c is CheckBox) && !(c is Button) && !(c is TextBox) && c.Width > 0)
                {
                    Size wrapped = TextRenderer.MeasureText(c.Text, c.Font, new Size(c.Width, int.MaxValue),
                        TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix | TextFormatFlags.TextBoxControl);
                    if (wrapped.Height > c.Height + 2) { line += "  TEXT CLIPPED"; problems++; }
                }
                lines.Add(line);
            }
            lines.Add("PROBLEMS=" + problems);
        }

        static IEnumerable<Control> Walk(Control root)
        {
            yield return root;
            foreach (Control child in root.Controls)
                foreach (Control x in Walk(child)) yield return x;
        }
    }
}
