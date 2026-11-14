using System;
using System.IO;
using System.Windows;

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
                if (args != null && args.Length > 0
                    && string.Equals(args[0], "--selftest", StringComparison.OrdinalIgnoreCase))
                {
                    RunSelfTest(args.Length > 1 ? args[1] : "selftest.txt");
                    return;
                }

                if (UpdateChecker.TryRunApplyUpdate(args)) return;
                UpdateChecker.BeginHelperCleanup(args);
                InstalledVersionSync.Run();
                if (UpdateChecker.TryStartUninstallerRefresh()) return;

                // Software rendering: on carbon (Nov 13 2026) hardware rendering left
                // every window blank white. IPbind is a small form; the CPU path is fast
                // and behaves the same on every GPU, driver and remote session.
                System.Windows.Media.RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
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
    }
}
