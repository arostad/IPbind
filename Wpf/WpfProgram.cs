using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;

namespace IPbind
{
    static class WpfProgram
    {
        [STAThread]
        static void Main(string[] args)
        {
            DebugLog.Start();
            try
            {
                ApplyThemeArg(args);
                if (HasArg(args, "--selftest"))
                {
                    RunSelfTest(ArgValue(args, "--selftest") ?? "selftest.txt");
                    return;
                }
                string uiDir = ArgValue(args, "--uicheck");
                if (uiDir != null)
                {
                    RunUiCheck(uiDir);
                    return;
                }

                if (UpdateChecker.TryRunApplyUpdate(args)) return;
                UpdateChecker.BeginHelperCleanup(args);
                InstalledVersionSync.Run();
                if (UpdateChecker.TryStartUninstallerRefresh()) return;

                // Software rendering: on carbon (Nov 13 2026) hardware rendering left
                // every window blank white. IPbind is a small form; the CPU path is fast
                // and behaves the same on every GPU, driver and remote session.
                RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
                WindowTheme.ApplyPreferredAppMode();
                Application app = new Application();
                app.ShutdownMode = ShutdownMode.OnMainWindowClose;
                WpfXaml.ApplyTheme(app, WindowTheme.IsAppDark());
                MainWindow main = new MainWindow();
                app.Run(main.Window);
            }
            finally
            {
                DebugLog.Stop();
            }
        }

        // Beta-only hidden override. Does not change the Windows theme.
        static void ApplyThemeArg(string[] args)
        {
            if (!BuildInfo.IsBeta) return;
            string theme = ArgValue(args, "--theme");
            if (theme == null) return;
            if (string.Equals(theme, "dark", StringComparison.OrdinalIgnoreCase))
                WindowTheme.SetForcedDark(true);
            else if (string.Equals(theme, "light", StringComparison.OrdinalIgnoreCase))
                WindowTheme.SetForcedDark(false);
        }

        static bool HasArg(string[] args, string name)
        {
            if (args == null) return false;
            foreach (string a in args)
                if (string.Equals(a, name, StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        static string ArgValue(string[] args, string name)
        {
            if (args == null) return null;
            for (int i = 0; i < args.Length; i++)
            {
                if (!string.Equals(args[i], name, StringComparison.OrdinalIgnoreCase)) continue;
                if (i + 1 >= args.Length) return "";
                string v = args[i + 1];
                if (v.StartsWith("--")) return "";
                return v;
            }
            return null;
        }

        static void RunSelfTest(string outPath)
        {
            try
            {
                Application app = Application.Current ?? new Application();
                WpfXaml.ApplyTheme(app, WindowTheme.IsAppDark());
                foreach (string name in WpfXaml.All)
                    WpfXaml.Load(name);

                string updateFile = Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                    BuildInfo.AppName, "update-check.txt");
                MainWindow main = new MainWindow(true);
                AboutWindow about = new AboutWindow(null, WindowTheme.IsAppDark(), updateFile);
                DhcpReminderWindow reminder = new DhcpReminderWindow(null, WindowTheme.IsAppDark());
                PrivacyConsentWindow privacy = new PrivacyConsentWindow(null, WindowTheme.IsAppDark());
                if (main.Window == null || about.Window == null || reminder.Window == null || privacy.Window == null)
                    throw new InvalidOperationException("A selftest window failed to construct.");
                File.WriteAllText(outPath, "SELFTEST OK");
            }
            catch (Exception ex)
            {
                try { File.WriteAllText(outPath, ex.ToString()); }
                catch { }
                Environment.ExitCode = 1;
            }
        }

        static void RunUiCheck(string outDir)
        {
            List<string> lines = new List<string>();
            List<string> fails = new List<string>();
            try
            {
                Directory.CreateDirectory(outDir);
                RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
                WindowTheme.ApplyPreferredAppMode();
                Application app = new Application();
                app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
                WpfXaml.ApplyTheme(app, WindowTheme.IsAppDark());
                MainWindow main = new MainWindow(false, true);
                bool finished = false;
                Action finish = delegate
                {
                    if (finished) return;
                    finished = true;
                    try
                    {
                        main.Window.UpdateLayout();
                        Inspect(main.Window, lines, fails);
                        RenderPng(main.Window, Path.Combine(outDir, "main.png"));
                        AboutWindow about = new AboutWindow(main.Window, WindowTheme.IsAppDark(),
                            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                                BuildInfo.AppName, "update-check.txt"), false);
                        ShowRenderClose(about.Window, Path.Combine(outDir, "about.png"));
                        DhcpReminderWindow popup = new DhcpReminderWindow(main.Window, WindowTheme.IsAppDark());
                        ShowRenderClose(popup.Window, Path.Combine(outDir, "popup.png"));
                    }
                    catch (Exception ex)
                    {
                        fails.Add("FAIL " + ex.Message);
                    }
                    WriteReport(outDir, lines, fails);
                    Environment.ExitCode = fails.Count == 0 ? 0 : 1;
                    app.Shutdown();
                };

                DispatcherTimer wait = new DispatcherTimer();
                wait.Interval = TimeSpan.FromMilliseconds(100);
                int ticks = 0;
                main.Window.ContentRendered += delegate
                {
                    wait.Tick += delegate
                    {
                        ticks++;
                        main.Window.UpdateLayout();
                        if (main.BannerReady || ticks >= 50)
                        {
                            wait.Stop();
                            finish();
                        }
                    };
                    wait.Start();
                };
                DispatcherTimer watchdog = new DispatcherTimer();
                watchdog.Interval = TimeSpan.FromSeconds(30);
                watchdog.Tick += delegate
                {
                    watchdog.Stop();
                    fails.Add("FAIL window did not finish loading");
                    finish();
                };
                watchdog.Start();
                app.Run(main.Window);
            }
            catch (Exception ex)
            {
                fails.Add("FAIL " + ex.GetType().Name + ": " + ex.Message);
                try { Directory.CreateDirectory(outDir); WriteReport(outDir, lines, fails); }
                catch { }
                Environment.ExitCode = 1;
            }
        }

        static void ShowRenderClose(Window w, string path)
        {
            w.Show();
            w.UpdateLayout();
            if (w.ActualWidth < 2 || w.ActualHeight < 2)
            {
                double width = double.IsNaN(w.Width) ? 640 : w.Width;
                w.Measure(new Size(width, 4000));
                w.Arrange(new Rect(0, 0, w.DesiredSize.Width, w.DesiredSize.Height));
                w.UpdateLayout();
            }
            RenderPng(w, path);
            w.Close();
        }

        static void Inspect(Window window, List<string> lines, List<string> fails)
        {
            Rect client = new Rect(0, 0, window.ActualWidth, window.ActualHeight);
            const double tol = 2.0;
            List<FrameworkElement> named = new List<FrameworkElement>();
            Walk(window, delegate(DependencyObject d)
            {
                FrameworkElement el = d as FrameworkElement;
                if (el == null || string.IsNullOrEmpty(el.Name)) return;
                if (!el.IsVisible || el.ActualWidth < 1 || el.ActualHeight < 1) return;
                if (InsidePopup(el)) return;
                named.Add(el);
            });
            named.Sort(delegate(FrameworkElement a, FrameworkElement b) { return string.CompareOrdinal(a.Name, b.Name); });
            foreach (FrameworkElement el in named)
            {
                Rect b;
                try { b = ToWindow(el, window); }
                catch (Exception ex)
                {
                    fails.Add("FAIL " + el.Name + " bounds: " + ex.Message);
                    continue;
                }
                lines.Add(el.Name + " " + Fmt(b.Left) + "," + Fmt(b.Top) + " " + Fmt(b.Width) + "x" + Fmt(b.Height));
                if (b.Left < client.Left - tol || b.Top < client.Top - tol
                    || b.Right > client.Right + tol || b.Bottom > client.Bottom + tol)
                {
                    fails.Add("FAIL " + el.Name + " outside client "
                        + Fmt(b.Left) + "," + Fmt(b.Top) + " " + Fmt(b.Width) + "x" + Fmt(b.Height)
                        + " client " + Fmt(client.Width) + "x" + Fmt(client.Height));
                }
            }

            Walk(window, delegate(DependencyObject d)
            {
                FrameworkElement el = d as FrameworkElement;
                if (el == null || !el.IsVisible) return;
                if (InsidePopup(el)) return;
                if (!(el is TextBlock) && !(el is Button) && !(el is CheckBox)) return;
                if (el.RenderSize.Width < 1 || el.RenderSize.Height < 1) return;
                if (el.DesiredSize.Width <= 0 && el.DesiredSize.Height <= 0) return;
                // DesiredSize includes Margin; RenderSize does not.
                Thickness m = el.Margin;
                double wantW = el.DesiredSize.Width - m.Left - m.Right;
                double wantH = el.DesiredSize.Height - m.Top - m.Bottom;
                if (wantW > el.RenderSize.Width + tol
                    || wantH > el.RenderSize.Height + tol)
                {
                    string who = string.IsNullOrEmpty(el.Name) ? el.GetType().Name : el.Name;
                    string text = "";
                    TextBlock tb = el as TextBlock;
                    if (tb != null) text = tb.Text;
                    else
                    {
                        ContentControl cc = el as ContentControl;
                        if (cc != null && cc.Content != null) text = cc.Content.ToString();
                    }
                    if (text.Length > 60) text = text.Substring(0, 60);
                    fails.Add("FAIL " + who + " text trimmed desired "
                        + Fmt(wantW) + "x" + Fmt(wantH)
                        + " render " + Fmt(el.RenderSize.Width) + "x" + Fmt(el.RenderSize.Height)
                        + " \"" + text + "\"");
                }
            });

            FrameworkElement left = window.FindName("leftColumn") as FrameworkElement;
            FrameworkElement console = window.FindName("consoleHost") as FrameworkElement;
            if (left == null || console == null)
            {
                fails.Add("FAIL console or left column is missing");
            }
            else
            {
                Rect lb = ToWindow(left, window);
                Rect cb = ToWindow(console, window);
                lines.Add("leftColumn.bottom " + Fmt(lb.Bottom));
                lines.Add("consoleHost.bottom " + Fmt(cb.Bottom));
                if (cb.Bottom + 4 < lb.Bottom)
                    fails.Add("FAIL console shorter than the left column: console bottom "
                        + Fmt(cb.Bottom) + " left bottom " + Fmt(lb.Bottom));
            }
        }

        static void Walk(DependencyObject d, Action<DependencyObject> visit)
        {
            if (d == null) return;
            visit(d);
            int n = VisualTreeHelper.GetChildrenCount(d);
            for (int i = 0; i < n; i++)
                Walk(VisualTreeHelper.GetChild(d, i), visit);
        }

        static bool InsidePopup(DependencyObject d)
        {
            DependencyObject p = d;
            while (p != null)
            {
                if (p is Popup) return true;
                p = VisualTreeHelper.GetParent(p);
            }
            return false;
        }

        static Rect ToWindow(FrameworkElement el, Visual window)
        {
            GeneralTransform t = el.TransformToAncestor(window);
            return t.TransformBounds(new Rect(0, 0, el.RenderSize.Width, el.RenderSize.Height));
        }

        static string Fmt(double v)
        {
            return v.ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);
        }

        static void RenderPng(Window w, string path)
        {
            PresentationSource src = PresentationSource.FromVisual(w);
            double sx = 1, sy = 1;
            if (src != null && src.CompositionTarget != null)
            {
                sx = src.CompositionTarget.TransformToDevice.M11;
                sy = src.CompositionTarget.TransformToDevice.M22;
                if (sx < 0.1) sx = 1;
                if (sy < 0.1) sy = 1;
            }
            int px = Math.Max(1, (int)Math.Ceiling(w.ActualWidth * sx));
            int py = Math.Max(1, (int)Math.Ceiling(w.ActualHeight * sy));
            RenderTargetBitmap bmp = new RenderTargetBitmap(px, py, 96.0 * sx, 96.0 * sy, PixelFormats.Pbgra32);
            bmp.Render(w);
            PngBitmapEncoder enc = new PngBitmapEncoder();
            enc.Frames.Add(BitmapFrame.Create(bmp));
            using (FileStream fs = File.Create(path))
                enc.Save(fs);
        }

        static void WriteReport(string outDir, List<string> lines, List<string> fails)
        {
            StringBuilder sb = new StringBuilder();
            foreach (string line in lines) sb.AppendLine(line);
            if (fails.Count == 0) sb.AppendLine("UICHECK OK");
            else foreach (string f in fails) sb.AppendLine(f);
            File.WriteAllText(Path.Combine(outDir, "uicheck.txt"), sb.ToString());
        }
    }
}
