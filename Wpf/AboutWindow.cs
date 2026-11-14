using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media.Imaging;

namespace IPbind
{
    sealed class AboutWindow
    {
        readonly Window window;
        readonly bool dark;
        readonly string updateCheckFile;
        readonly bool checkMatchOnShow;
        TextBlock matchLabel, statusLabel, privacyLabel, nameLabel, versionLabel;
        CheckBox launchCheckBox;
        Button checkButton, closeButton;
        UpdateInfo availableUpdate;
        bool settingLaunchCheck;

        const string PrivacyLine1 = "Each update check sends one anonymous request to the IPbind update server.";
        const string PrivacyLine2 = "It counts only the country the request came from, for a rough guesstimate of the app\u2019s popularity (or lack thereof) for my own curiosity. Your IP address is never stored, and no identifiers, cookies, or telemetry are used.";

        public Window Window { get { return window; } }

        public AboutWindow(Window owner, bool dark, string updateCheckFile)
        {
            this.dark = dark;
            this.updateCheckFile = updateCheckFile;
            window = WpfXaml.LoadWindow(WpfXaml.About);
            window.Title = "About " + BuildInfo.AppName;
            window.Owner = owner;
            ApplyChrome();

            nameLabel = WpfXaml.Find<TextBlock>(window, "nameLabel");
            versionLabel = WpfXaml.Find<TextBlock>(window, "versionLabel");
            matchLabel = WpfXaml.Find<TextBlock>(window, "matchLabel");
            statusLabel = WpfXaml.Find<TextBlock>(window, "statusLabel");
            privacyLabel = WpfXaml.Find<TextBlock>(window, "privacyLabel");
            launchCheckBox = WpfXaml.Find<CheckBox>(window, "launchCheckBox");
            checkButton = WpfXaml.Find<Button>(window, "checkButton");
            closeButton = WpfXaml.Find<Button>(window, "closeButton");

            nameLabel.Text = BuildInfo.AppName;
            versionLabel.Text = "Version " + BuildInfo.DisplayVersion;
            privacyLabel.Text = PrivacyLine1 + "\r\n" + PrivacyLine2;

            bool launchEnabled = ReadLaunchCheckEnabled();
            checkMatchOnShow = BuildInfo.IsBeta && launchEnabled;
            settingLaunchCheck = true;
            try { launchCheckBox.IsChecked = launchEnabled; }
            finally { settingLaunchCheck = false; }

            WireLink(WpfXaml.Find<Hyperlink>(window, "websiteLink"));
            WireLink(WpfXaml.Find<Hyperlink>(window, "sourceLink"));
            WireLink(WpfXaml.Find<Hyperlink>(window, "creditLink"));
            WireLink(WpfXaml.Find<Hyperlink>(window, "licenseLink"));

            launchCheckBox.Checked += LaunchCheckChanged;
            launchCheckBox.Unchecked += LaunchCheckChanged;
            checkButton.Click += CheckUpdates;
            closeButton.Click += delegate { window.DialogResult = true; };
            window.ContentRendered += delegate
            {
                if (checkMatchOnShow) StartReleaseMatchCheck();
            };

            LoadIcon();
        }

        public bool? ShowDialog()
        {
            return window.ShowDialog();
        }

        void ApplyChrome()
        {
            window.SourceInitialized += delegate
            {
                IntPtr hwnd = new WindowInteropHelper(window).Handle;
                WindowTheme.ApplyTitleBar(hwnd, dark);
            };
        }

        void LoadIcon()
        {
            try
            {
                Stream ico = typeof(AboutWindow).Assembly.GetManifestResourceStream("IPbind.app.ico");
                if (ico == null) return;
                using (ico)
                {
                    IconBitmapDecoder dec = new IconBitmapDecoder(ico, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                    if (dec.Frames.Count > 0)
                    {
                        window.Icon = dec.Frames[0];
                        Image iconBox = window.FindName("iconBox") as Image;
                        if (iconBox != null) iconBox.Source = dec.Frames[0];
                    }
                }
            }
            catch { }
        }

        static void WireLink(Hyperlink link)
        {
            link.RequestNavigate += delegate(object sender, System.Windows.Navigation.RequestNavigateEventArgs e)
            {
                try
                {
                    Uri destination;
                    if (!TryGetHttpsUrl(e.Uri == null ? null : e.Uri.AbsoluteUri, out destination))
                        return;
                    ProcessStartInfo info = new ProcessStartInfo(destination.AbsoluteUri);
                    info.UseShellExecute = true;
                    Process.Start(info);
                }
                catch { }
                e.Handled = true;
            };
        }

        static bool TryGetHttpsUrl(string value, out Uri uri)
        {
            uri = null;
            Uri parsed;
            if (string.IsNullOrWhiteSpace(value)
                || !Uri.TryCreate(value, UriKind.Absolute, out parsed)
                || !string.Equals(parsed.Scheme, Uri.UriSchemeHttps, StringComparison.OrdinalIgnoreCase)
                || string.IsNullOrEmpty(parsed.Host))
                return false;
            uri = parsed;
            return true;
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
            try { launchCheckBox.IsChecked = value; }
            finally { settingLaunchCheck = false; }
        }

        void LaunchCheckChanged(object sender, RoutedEventArgs e)
        {
            if (settingLaunchCheck) return;
            if (launchCheckBox.IsChecked == true)
            {
                PrivacyConsentWindow consent = new PrivacyConsentWindow(window, dark);
                bool agreed = consent.ShowDialog() == true;
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

        void SetStatus(string text)
        {
            statusLabel.Text = text ?? "";
            statusLabel.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
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
                    window.Dispatcher.BeginInvoke(new Action(delegate
                    {
                        matchLabel.Visibility = match ? Visibility.Visible : Visibility.Collapsed;
                    }));
                }
                catch { }
            });
            thread.IsBackground = true;
            thread.Start();
        }

        void CheckUpdates(object sender, RoutedEventArgs e)
        {
            StartReleaseMatchCheck();
            checkButton.IsEnabled = false;
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
                    window.Dispatcher.BeginInvoke(new Action(delegate
                    {
                        checkButton.IsEnabled = true;
                        if (!info.IsNewer)
                        {
                            SetStatus(string.IsNullOrEmpty(info.Error)
                                ? "You're on the latest version (" + BuildInfo.DisplayVersion + ")."
                                : info.Error);
                            return;
                        }
                        availableUpdate = info;
                        SetStatus("Version " + VersionText.ForDisplay(info.RemoteVersion) + " is available.");
                        checkButton.Content = "Update now";
                        checkButton.Click -= CheckUpdates;
                        checkButton.Click += UpdateNow;
                    }));
                }
                catch { }
            });
            thread.IsBackground = true;
            thread.Start();
        }

        void UpdateNow(object sender, RoutedEventArgs e)
        {
            if (availableUpdate == null) return;
            checkButton.IsEnabled = false;
            SetStatus("Downloading. The app will restart when it is ready.");
            Thread thread = new Thread(delegate()
            {
                try
                {
                    UpdateChecker.DownloadAndRestart();
                    try
                    {
                        window.Dispatcher.BeginInvoke(new Action(delegate
                        {
                            if (Application.Current != null) Application.Current.Shutdown();
                        }));
                    }
                    catch { }
                }
                catch (Exception ex)
                {
                    try
                    {
                        window.Dispatcher.BeginInvoke(new Action(delegate
                        {
                            SetStatus(UpdateChecker.DescribeError(ex));
                            checkButton.IsEnabled = true;
                        }));
                    }
                    catch { }
                }
            });
            thread.IsBackground = true;
            thread.Start();
        }
    }
}
