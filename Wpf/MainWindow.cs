using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Shapes;
using IoPath = System.IO.Path;

namespace IPbind
{
    sealed class MainWindow : IBindHost
    {
        readonly Window window;
        readonly bool dark;
        readonly bool selftest;
        BindEngine engine;
        OverlayScrollHost ipsOverlay;
        OverlayScrollHost consoleOverlay;

        ComboBox combo;
        CheckBox chkAll, chkKeepDhcp, chkGateway;
        TextBox txtIPs, txtGateway, txtDns1, txtDns2;
        TextBlock lblIPs, lblStatus, lblUpdateBanner, bannerHeadline, bannerDetail;
        ComboBox cboGwIp;
        FrameworkElement gatewayRow1, gatewayRow2;
        Button btnBind, btnDhcp, btnSaveList, btnRestore, btnShow, btnRefresh, btnAbout, btnClear;
        Button btnUpdateNow, btnUpdateLater;
        Border updateBanner, statusBanner;
        Ellipse bannerIcon;
        Shape bannerGlyphCheck, bannerGlyphBang, bannerGlyphX, bannerGlyphDash;
        RichTextBox console;
        Paragraph consolePara;
        bool suppressingGateway;
        int bannerSerial;

        const string IpHelp = "One per line, CIDR (e.g. 192.168.1.98/24)";
        const string KeepDhcpTipOn =
            "On: DHCP stays on and these addresses are added alongside it.\r\n" +
            "Off: static addresses only, no DHCP.";
        const string KeepDhcpTipOff =
            "Needs Windows 10 version 1703 or later.\r\n" +
            "Apply uses static addresses only.";

        public Window Window { get { return window; } }
        public bool BannerReady { get; private set; }

        public MainWindow() : this(false, false) { }

        public MainWindow(bool selftest) : this(selftest, false) { }

        // quiet: load the real window (adapters, list) but skip the DHCP popup and the update check.
        public MainWindow(bool selftest, bool quiet)
        {
            this.selftest = selftest;
            dark = WindowTheme.IsAppDark();
            window = WpfXaml.LoadWindow(WpfXaml.Main);
            window.Title = BuildInfo.AppName + " v" + BuildInfo.DisplayVersion;
            Wire();
            FitWorkArea();
            ApplyChrome();
            LoadIcon();
            engine = new BindEngine(this);
            engine.LeaseWatchFinished = delegate { RefreshBanner(); };
            if (selftest) return;
            engine.Startup();
            RefreshBanner();
            if (!quiet)
                window.ContentRendered += OnFirstShown;
        }

        void Wire()
        {
            combo = WpfXaml.Find<ComboBox>(window, "combo");
            chkAll = WpfXaml.Find<CheckBox>(window, "chkAll");
            chkKeepDhcp = WpfXaml.Find<CheckBox>(window, "chkKeepDhcp");
            chkGateway = WpfXaml.Find<CheckBox>(window, "chkGateway");
            txtIPs = WpfXaml.Find<TextBox>(window, "txtIPs");
            lblIPs = WpfXaml.Find<TextBlock>(window, "lblIPs");
            cboGwIp = WpfXaml.Find<ComboBox>(window, "cboGwIp");
            txtGateway = WpfXaml.Find<TextBox>(window, "txtGateway");
            txtDns1 = WpfXaml.Find<TextBox>(window, "txtDns1");
            txtDns2 = WpfXaml.Find<TextBox>(window, "txtDns2");
            gatewayRow1 = WpfXaml.Find<FrameworkElement>(window, "gatewayRow1");
            gatewayRow2 = WpfXaml.Find<FrameworkElement>(window, "gatewayRow2");
            btnBind = WpfXaml.Find<Button>(window, "btnBind");
            btnDhcp = WpfXaml.Find<Button>(window, "btnDhcp");
            btnSaveList = WpfXaml.Find<Button>(window, "btnSaveList");
            btnRestore = WpfXaml.Find<Button>(window, "btnRestore");
            btnShow = WpfXaml.Find<Button>(window, "btnShow");
            btnRefresh = WpfXaml.Find<Button>(window, "btnRefresh");
            btnAbout = WpfXaml.Find<Button>(window, "btnAbout");
            btnClear = WpfXaml.Find<Button>(window, "btnClear");
            btnUpdateNow = WpfXaml.Find<Button>(window, "btnUpdateNow");
            btnUpdateLater = WpfXaml.Find<Button>(window, "btnUpdateLater");
            updateBanner = WpfXaml.Find<Border>(window, "updateBanner");
            lblUpdateBanner = WpfXaml.Find<TextBlock>(window, "lblUpdateBanner");
            lblStatus = WpfXaml.Find<TextBlock>(window, "lblStatus");
            statusBanner = WpfXaml.Find<Border>(window, "statusBanner");
            bannerHeadline = WpfXaml.Find<TextBlock>(window, "bannerHeadline");
            bannerDetail = WpfXaml.Find<TextBlock>(window, "bannerDetail");
            bannerIcon = WpfXaml.Find<Ellipse>(window, "bannerIcon");
            bannerGlyphCheck = WpfXaml.Find<Shape>(window, "bannerGlyphCheck");
            bannerGlyphBang = WpfXaml.Find<Shape>(window, "bannerGlyphBang");
            bannerGlyphX = WpfXaml.Find<Shape>(window, "bannerGlyphX");
            bannerGlyphDash = WpfXaml.Find<Shape>(window, "bannerGlyphDash");
            console = WpfXaml.Find<RichTextBox>(window, "console");

            btnBind.Content = UiCopy.ApplyButton;
            btnDhcp.Content = UiCopy.DhcpButton;

            console.Document = new FlowDocument();
            console.Document.PagePadding = new Thickness(8, 6, 16, 6);
            console.Document.FontFamily = new FontFamily("Consolas");
            console.Document.FontSize = 12;
            consolePara = new Paragraph();
            consolePara.Margin = new Thickness(0);
            console.Document.Blocks.Add(consolePara);
            console.SizeChanged += delegate { FitConsolePage(); };

            chkKeepDhcp.ToolTip = KeepDhcpTipOn;
            chkGateway.ToolTip = "Only enable if you know what you're doing!";

            combo.SelectionChanged += delegate
            {
                if (engine == null) return;
                engine.OnAdapterChosen();
                RefreshBanner();
            };
            chkAll.Checked += delegate { if (engine != null) { engine.LoadAdapters(); RefreshBanner(); } };
            chkAll.Unchecked += delegate { if (engine != null) { engine.LoadAdapters(); RefreshBanner(); } };
            chkKeepDhcp.Checked += delegate { if (engine != null) engine.OnKeepDhcpCheckedChanged(); };
            chkKeepDhcp.Unchecked += delegate { if (engine != null) engine.OnKeepDhcpCheckedChanged(); };
            chkGateway.Checked += delegate { if (engine != null) engine.OnGatewayCheckedChanged(); };
            chkGateway.Unchecked += delegate { if (engine != null) engine.OnGatewayCheckedChanged(); };
            cboGwIp.SelectionChanged += delegate
            {
                if (engine != null && !suppressingGateway) engine.OnGatewayIpChosen();
            };
            txtIPs.TextChanged += delegate
            {
                if (engine != null) engine.RefreshGatewayChoices();
                if (ipsOverlay != null) ipsOverlay.SyncThumbOnly();
            };
            txtGateway.TextChanged += delegate { if (engine != null) engine.SaveGatewaySettings(); };
            txtDns1.TextChanged += delegate { if (engine != null) engine.SaveGatewaySettings(); };
            txtDns2.TextChanged += delegate { if (engine != null) engine.SaveGatewaySettings(); };

            btnRefresh.Click += delegate
            {
                if (engine == null) return;
                using (DebugLog.Time("Refresh")) engine.LoadAdapters();
                RefreshBanner();
            };
            btnSaveList.Click += delegate { if (engine != null) engine.SaveList(false); };
            btnRestore.Click += delegate { if (engine != null) engine.RestoreDefaults(); };
            btnShow.Click += delegate
            {
                if (engine == null) return;
                using (DebugLog.Time("Show Current"))
                {
                    string a = engine.GetSelectedAlias();
                    if (a == null) { engine.Log("No interface selected.", "WARN"); return; }
                    engine.ShowCurrentConfig(a);
                }
            };
            btnBind.Click += delegate
            {
                if (engine == null) return;
                using (DebugLog.Time("Apply"))
                {
                    string alias = engine.GetSelectedAlias();
                    string before = engine.CaptureAdapterState(alias);
                    SetBusy("Applying static IPs...");
                    bool ok = engine.InvokeBind();
                    EndBusy(ok, "Static IPs applied", "Failed to apply static IPs");
                    engine.LogAdapterDiff("Apply", before, engine.CaptureAdapterState(alias));
                }
            };
            btnDhcp.Click += delegate
            {
                if (engine == null) return;
                using (DebugLog.Time("Return to DHCP"))
                {
                    string alias = engine.GetSelectedAlias();
                    string before = engine.CaptureAdapterState(alias);
                    SetBusy("Returning to DHCP...");
                    bool ok = engine.InvokeDhcp();
                    EndBusy(ok, "Returned to DHCP", "Failed to return to DHCP");
                    engine.LogAdapterDiff("Return", before, engine.CaptureAdapterState(alias));
                }
            };
            btnClear.Click += delegate
            {
                consolePara.Inlines.Clear();
                if (consoleOverlay != null) consoleOverlay.SyncThumbOnly();
            };
            btnAbout.Click += delegate
            {
                using (DebugLog.Time("About"))
                {
                    AboutWindow about = new AboutWindow(window, dark,
                        engine != null ? engine.UpdateCheckPath : UpdateCheckPath());
                    about.ShowDialog();
                }
            };
            btnUpdateNow.Click += delegate { if (engine != null) engine.InstallUpdateFromBanner(); };
            btnUpdateLater.Click += delegate { if (engine != null) engine.DismissUpdateBanner(); };

            ipsOverlay = new OverlayScrollHost(txtIPs,
                WpfXaml.Find<Border>(window, "ipsTrack"),
                WpfXaml.Find<Border>(window, "ipsThumb"), dark);
            consoleOverlay = new OverlayScrollHost(console,
                WpfXaml.Find<Border>(window, "consoleTrack"),
                WpfXaml.Find<Border>(window, "consoleThumb"), dark);
        }

        void FitConsolePage()
        {
            if (console == null || console.Document == null) return;
            double w = console.ActualWidth - console.Document.PagePadding.Left - console.Document.PagePadding.Right - 4;
            if (w < 40) return;
            if (double.IsNaN(console.Document.PageWidth) || Math.Abs(console.Document.PageWidth - w) > 0.5)
                console.Document.PageWidth = w;
        }

        static string UpdateCheckPath()
        {
            return IoPath.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                BuildInfo.AppName, "update-check.txt");
        }

        void OnFirstShown(object sender, EventArgs e)
        {
            window.ContentRendered -= OnFirstShown;
            engine.CheckForUpdatesOnLaunch();
            window.Dispatcher.BeginInvoke(new Action(engine.ShowDhcpReminderIfNeeded));
        }

        void FitWorkArea()
        {
            Rect wa = SystemParameters.WorkArea;
            window.MinWidth = Math.Min(980, wa.Width);
            window.MinHeight = Math.Min(520, wa.Height);
            window.MaxWidth = Math.Max(window.MinWidth, wa.Width);
            window.MaxHeight = Math.Max(window.MinHeight, wa.Height);
            if (window.Width > wa.Width) window.Width = wa.Width;
            if (window.Height > wa.Height) window.Height = wa.Height;
            if (!window.IsLoaded) return;
            double w = window.ActualWidth > 1 ? window.ActualWidth : window.Width;
            double h = window.ActualHeight > 1 ? window.ActualHeight : window.Height;
            if (w > wa.Width) window.Width = wa.Width;
            if (h > wa.Height) window.Height = wa.Height;
            if (window.Left < wa.Left) window.Left = wa.Left;
            if (window.Top < wa.Top) window.Top = wa.Top;
            if (window.Left + window.Width > wa.Right)
                window.Left = Math.Max(wa.Left, wa.Right - window.Width);
            if (window.Top + window.Height > wa.Bottom)
                window.Top = Math.Max(wa.Top, wa.Bottom - window.Height);
        }

        void ApplyChrome()
        {
            window.SourceInitialized += delegate
            {
                IntPtr hwnd = new WindowInteropHelper(window).Handle;
                WindowTheme.ApplyTitleBar(hwnd, dark);
                WindowTheme.ApplyMaximizedBorder(hwnd, window.WindowState == WindowState.Maximized);
            };
            window.StateChanged += delegate
            {
                IntPtr hwnd = new WindowInteropHelper(window).Handle;
                WindowTheme.ApplyMaximizedBorder(hwnd, window.WindowState == WindowState.Maximized);
            };
            window.DpiChanged += delegate { FitWorkArea(); };
        }

        void LoadIcon()
        {
            try
            {
                Stream ico = typeof(MainWindow).Assembly.GetManifestResourceStream("IPbind.app.ico");
                if (ico == null) return;
                using (ico)
                {
                    IconBitmapDecoder dec = new IconBitmapDecoder(ico, BitmapCreateOptions.None, BitmapCacheOption.OnLoad);
                    if (dec.Frames.Count > 0) window.Icon = dec.Frames[0];
                }
            }
            catch { }
        }

        void RefreshBanner()
        {
            if (engine == null) return;
            int serial = ++bannerSerial;
            int count = combo.Items.Count;
            string alias = engine.GetSelectedAlias();
            if (count == 0)
            {
                ShowBanner(3, "No adapter can be read", "Click Refresh, or check that a network adapter is present.");
                return;
            }
            if (string.IsNullOrEmpty(alias))
            {
                ShowBanner(0, "No interface selected", "Choose a LAN interface below.");
                return;
            }
            string aliasCopy = alias;
            System.Threading.ThreadPool.QueueUserWorkItem(delegate
            {
                BindEngine.SafetyBanner snap;
                try { snap = engine.ReadSafetyBanner(aliasCopy); }
                catch
                {
                    snap = new BindEngine.SafetyBanner();
                    snap.Kind = 3;
                    snap.Headline = "Can't read " + aliasCopy;
                    snap.Detail = "Click Refresh, or pick another interface.";
                }
                window.Dispatcher.BeginInvoke(new Action(delegate
                {
                    if (serial != bannerSerial || snap == null) return;
                    ShowBanner(snap.Kind, snap.Headline, snap.Detail);
                }));
            });
        }

        void ShowFailureBanner(string failMsg)
        {
            bannerSerial++;
            string detail = "See the console, then try again.";
            string headline = failMsg ?? "Something went wrong";
            if (headline.IndexOf("DHCP", StringComparison.OrdinalIgnoreCase) >= 0)
                headline = "Couldn't return to DHCP";
            else if (headline.IndexOf("apply", StringComparison.OrdinalIgnoreCase) >= 0)
                headline = "Couldn't apply static IPs";
            ShowBanner(3, headline, detail);
        }

        // kind: 0 none, 1 safe, 2 static, 3 error
        void ShowBanner(int kind, string headline, string detail)
        {
            string bg = "BannerNoneBg", border = "BannerNoneBorder", accent = "BannerNoneAccent";
            if (kind == 1) { bg = "BannerSafeBg"; border = "BannerSafeBorder"; accent = "BannerSafeAccent"; }
            else if (kind == 2) { bg = "BannerStaticBg"; border = "BannerStaticBorder"; accent = "BannerStaticAccent"; }
            else if (kind == 3) { bg = "BannerErrorBg"; border = "BannerErrorBorder"; accent = "BannerErrorAccent"; }
            statusBanner.Background = BrushOf(bg, Colors.DimGray);
            statusBanner.BorderBrush = BrushOf(border, Colors.Gray);
            bannerIcon.Fill = BrushOf(accent, Colors.Gray);
            bannerHeadline.Text = headline ?? "";
            bannerDetail.Text = detail ?? "";
            bannerGlyphCheck.Visibility = kind == 1 ? Visibility.Visible : Visibility.Collapsed;
            bannerGlyphBang.Visibility = kind == 2 ? Visibility.Visible : Visibility.Collapsed;
            bannerGlyphX.Visibility = kind == 3 ? Visibility.Visible : Visibility.Collapsed;
            bannerGlyphDash.Visibility = kind == 0 ? Visibility.Visible : Visibility.Collapsed;
            BannerReady = true;
        }

        void SetBusy(string text)
        {
            SetActionEnabled(false);
            lblStatus.Foreground = BrushOf("BusyBrush", dark ? Color.FromRgb(200, 200, 200) : Color.FromRgb(60, 60, 60));
            lblStatus.Text = text;
        }

        void EndBusy(bool ok, string okMsg, string failMsg)
        {
            SetActionEnabled(true);
            if (ok)
            {
                lblStatus.Foreground = BrushOf("OkBrush", dark ? Color.FromRgb(90, 200, 110) : Color.FromRgb(30, 120, 40));
                lblStatus.Text = "OK - " + okMsg;
                RefreshBanner();
            }
            else
            {
                lblStatus.Foreground = BrushOf("ErrBrush", dark ? Color.FromRgb(240, 100, 100) : Color.FromRgb(180, 40, 40));
                lblStatus.Text = failMsg;
                if (combo.Items.Count == 0)
                    ShowBanner(3, "No adapter can be read", "Click Refresh, or check that a network adapter is present.");
                else if (engine == null || string.IsNullOrEmpty(engine.GetSelectedAlias()))
                    ShowBanner(0, "No interface selected", "Choose a LAN interface below.");
                else
                    ShowFailureBanner(failMsg);
            }
        }

        void SetActionEnabled(bool on)
        {
            btnBind.IsEnabled = on;
            btnDhcp.IsEnabled = on;
            btnSaveList.IsEnabled = on;
            btnRestore.IsEnabled = on;
            btnShow.IsEnabled = on;
            btnRefresh.IsEnabled = on;
            btnAbout.IsEnabled = on;
        }

        static Brush BrushOf(string key, Color fallback)
        {
            object v = Application.Current != null ? Application.Current.TryFindResource(key) : null;
            SolidColorBrush b = v as SolidColorBrush;
            if (b != null) return b;
            return new SolidColorBrush(fallback);
        }

        // ---------------- IBindHost ----------------
        bool IBindHost.InvokeRequired
        {
            get { return window.Dispatcher != null && !window.Dispatcher.CheckAccess(); }
        }

        void IBindHost.BeginLogInvoke(Action<string, string> action, string msg, string level)
        {
            window.Dispatcher.BeginInvoke(action, msg, level);
        }

        void IBindHost.LogConsole(string msg, string level)
        {
            Brush body = console.Foreground;
            if (level == "ERR") body = BrushOf("ErrBrush", Color.FromRgb(240, 100, 100));
            else if (level == "WARN") body = BrushOf("WarnBrush", Color.FromRgb(255, 200, 61));
            else if (level == "OK") body = BrushOf("OkBrush", Color.FromRgb(90, 200, 110));
            Run stamp = new Run(DateTime.Now.ToString("HH:mm:ss") + "  ");
            stamp.Foreground = BrushOf("TimestampBrush", Color.FromRgb(138, 138, 138));
            Run text = new Run((string.IsNullOrEmpty(level) ? "INFO" : level) + "  " + msg);
            text.Foreground = body;
            consolePara.Inlines.Add(stamp);
            consolePara.Inlines.Add(text);
            consolePara.Inlines.Add(new LineBreak());
            FitConsolePage();
            console.ScrollToEnd();
            if (consoleOverlay != null) consoleOverlay.SyncThumbOnly();
        }

        string[] IBindHost.IpLines
        {
            get { return txtIPs.Text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n'); }
            set { txtIPs.Text = value == null ? "" : string.Join("\r\n", value); }
        }

        bool IBindHost.ShowAllChecked { get { return chkAll.IsChecked == true; } }

        bool IBindHost.GatewayChecked
        {
            get { return chkGateway.IsChecked == true; }
            set { chkGateway.IsChecked = value; }
        }

        string IBindHost.GatewayText
        {
            get { return txtGateway.Text ?? ""; }
            set { txtGateway.Text = value ?? ""; }
        }

        string IBindHost.Dns1Text
        {
            get { return txtDns1.Text ?? ""; }
            set { txtDns1.Text = value ?? ""; }
        }

        string IBindHost.Dns2Text
        {
            get { return txtDns2.Text ?? ""; }
            set { txtDns2.Text = value ?? ""; }
        }

        string IBindHost.SelectedGatewayItem
        {
            get { return cboGwIp.SelectedIndex >= 0 ? cboGwIp.SelectedItem as string : null; }
        }

        int IBindHost.GatewayComboCount { get { return cboGwIp.Items.Count; } }

        string IBindHost.GatewayComboItemAt(int index) { return cboGwIp.Items[index] as string; }

        int IBindHost.GatewaySelectedIndex
        {
            get { return cboGwIp.SelectedIndex; }
            set
            {
                suppressingGateway = true;
                try { cboGwIp.SelectedIndex = value; }
                finally { suppressingGateway = false; }
            }
        }

        void IBindHost.ReplaceGatewayItems(List<string> keys)
        {
            suppressingGateway = true;
            try
            {
                cboGwIp.Items.Clear();
                foreach (string k in keys) cboGwIp.Items.Add(k);
            }
            finally { suppressingGateway = false; }
        }

        void IBindHost.ApplyGatewayLayout()
        {
            bool on = chkGateway.IsChecked == true;
            gatewayRow1.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            gatewayRow2.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
            lblIPs.Text = IpHelp;
        }

        bool IBindHost.KeepDhcpChecked
        {
            get { return chkKeepDhcp.IsChecked == true; }
            set { chkKeepDhcp.IsChecked = value; }
        }

        bool IBindHost.KeepDhcpEnabled
        {
            get { return chkKeepDhcp.IsEnabled; }
            set { chkKeepDhcp.IsEnabled = value; }
        }

        void IBindHost.SetKeepDhcpToolTip(string text)
        {
            chkKeepDhcp.ToolTip = text;
        }

        int IBindHost.SelectedAdapterIndex { get { return combo.SelectedIndex; } }

        void IBindHost.BindAdapterList(List<Adapter> list, int selectedIndex)
        {
            combo.Items.Clear();
            foreach (Adapter a in list)
                combo.Items.Add(AdapterDisplay.Format(a.Name, a.Description, a.Status));
            if (selectedIndex >= 0 && selectedIndex < combo.Items.Count)
                combo.SelectedIndex = selectedIndex;
        }

        bool IBindHost.ConfirmDisconnected(string alias, string status)
        {
            MessageBoxResult ans = MessageBox.Show(window,
                "The interface '" + alias + "' is currently '" + status + "', not connected.\r\n\r\n" +
                "Windows will not bind static addresses to a disconnected interface, so this likely " +
                "won't take effect until it is connected.\r\n\r\nApply anyway?",
                "Interface not connected", MessageBoxButton.YesNo, MessageBoxImage.Warning);
            return ans == MessageBoxResult.Yes;
        }

        void IBindHost.RunOnUi(Action action)
        {
            window.Dispatcher.BeginInvoke(action);
        }

        bool IBindHost.IsHostDisposed
        {
            get { return window == null; }
        }

        void IBindHost.ShowUpdateAvailable(string remoteVersion)
        {
            lblUpdateBanner.Text =
                "Version " + VersionText.ForDisplay(remoteVersion) + " is available.";
            updateBanner.Visibility = Visibility.Visible;
            lblStatus.Visibility = Visibility.Collapsed;
        }

        void IBindHost.HideUpdateBanner()
        {
            updateBanner.Visibility = Visibility.Collapsed;
            lblStatus.Visibility = Visibility.Visible;
        }

        void IBindHost.SetUpdatingBanner()
        {
            btnUpdateNow.IsEnabled = false;
            btnUpdateLater.IsEnabled = false;
            btnUpdateNow.Content = "Updating...";
            lblUpdateBanner.Text = "Downloading update; IPbind will restart.";
        }

        void IBindHost.ShowUpdateError(string message)
        {
            lblUpdateBanner.Text = message;
            btnUpdateNow.Content = "Update";
            btnUpdateNow.IsEnabled = true;
            btnUpdateLater.IsEnabled = true;
        }

        void IBindHost.ExitApp()
        {
            if (Application.Current != null) Application.Current.Shutdown();
        }

        void IBindHost.ShowDhcpReminder(out bool proceed, out bool remember)
        {
            DhcpReminderWindow reminder = new DhcpReminderWindow(window, dark);
            bool? result = reminder.ShowDialog();
            proceed = result == true;
            remember = proceed && reminder.DontShowAgain;
        }

        void IBindHost.CloseHost()
        {
            window.Close();
        }
    }
}
