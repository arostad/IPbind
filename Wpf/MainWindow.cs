using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Animation;
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
        SiteListStore siteStore;

        ComboBox combo, cboSites, cboConsoleFilter;
        CheckBox chkAll, chkKeepDhcp, chkGateway;
        TextBox txtIPs, txtGateway, txtDns1, txtDns2;
        TextBlock lblIPs, lblStatus, lblUpdateBanner, bannerHeadline, bannerDetail;
        ComboBox cboGwIp;
        FrameworkElement gatewayRow1, gatewayRow2, rightColumn, leftColumn;
        ColumnDefinition colGap, colRight;
        Button btnBind, btnDhcp, btnSaveList, btnRestore, btnShow, btnRefresh, btnAbout, btnClear;
        Button btnUpdateNow, btnUpdateLater, btnCopy, btnCompact, btnSaveAs, btnRename, btnDeleteList;
        Border updateBanner, statusBanner, bindBusyOverlay, dhcpBusyOverlay, bindFocusRing;
        TranslateTransform bindShimmerX, dhcpShimmerX;
        Ellipse bannerIcon;
        Shape bannerGlyphCheck, bannerGlyphBang, bannerGlyphX, bannerGlyphDash;
        RichTextBox console;
        Paragraph consolePara;
        Canvas ipGutter;
        bool suppressingGateway;
        bool suppressingSites;
        bool busy;
        bool compact;
        bool boundActive;
        bool showingInvalidStatus;
        bool keepDhcpWantEnabled = true;
        bool[] boundFlags;
        string bindIdleContent, dhcpIdleContent;
        double restoredWidth;
        int invalidCount;
        int bannerSerial;
        readonly List<ConsoleEntry> consoleEntries = new List<ConsoleEntry>();

        const string IpHelp = "One per line, CIDR (e.g. 192.168.1.98/24)";
        const string KeepDhcpTipOn =
            "On: DHCP stays on and these addresses are added alongside it.\r\n" +
            "Off: static addresses only, no DHCP.";
        const string KeepDhcpTipOff =
            "Needs Windows 10 version 1703 or later.\r\n" +
            "Apply uses static addresses only.";

        // uicheck / tests: keep Beta UI files out of the user's settings folder.
        internal static string SettingsFolderOverride;

        public Window Window { get { return window; } }
        public bool BannerReady { get; private set; }
        public bool IsCompactMode { get { return compact; } }

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
            InitSiteLists();
            LoadCompactPref();
            UpdateGutter();
            RefreshBanner();
            if (!quiet)
                window.ContentRendered += OnFirstShown;
        }

        internal static string UiSettingsFolder()
        {
            if (!string.IsNullOrEmpty(SettingsFolderOverride))
                return SettingsFolderOverride;
            return IoPath.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                BuildInfo.AppName);
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
            rightColumn = WpfXaml.Find<FrameworkElement>(window, "rightColumn");
            leftColumn = WpfXaml.Find<FrameworkElement>(window, "leftColumn");
            Grid columnsGrid = WpfXaml.Find<Grid>(window, "columnsGrid");
            colGap = columnsGrid.ColumnDefinitions[1];
            colRight = columnsGrid.ColumnDefinitions[2];
            btnBind = WpfXaml.Find<Button>(window, "btnBind");
            btnDhcp = WpfXaml.Find<Button>(window, "btnDhcp");
            btnSaveList = WpfXaml.Find<Button>(window, "btnSaveList");
            btnRestore = WpfXaml.Find<Button>(window, "btnRestore");
            btnShow = WpfXaml.Find<Button>(window, "btnShow");
            btnRefresh = WpfXaml.Find<Button>(window, "btnRefresh");
            btnAbout = WpfXaml.Find<Button>(window, "btnAbout");
            btnClear = WpfXaml.Find<Button>(window, "btnClear");
            btnCopy = WpfXaml.Find<Button>(window, "btnCopy");
            btnCompact = WpfXaml.Find<Button>(window, "btnCompact");
            btnSaveAs = WpfXaml.Find<Button>(window, "btnSaveAs");
            btnRename = WpfXaml.Find<Button>(window, "btnRename");
            btnDeleteList = WpfXaml.Find<Button>(window, "btnDeleteList");
            cboSites = WpfXaml.Find<ComboBox>(window, "cboSites");
            cboConsoleFilter = WpfXaml.Find<ComboBox>(window, "cboConsoleFilter");
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
            ipGutter = WpfXaml.Find<Canvas>(window, "ipGutter");
            bindBusyOverlay = WpfXaml.Find<Border>(window, "bindBusyOverlay");
            dhcpBusyOverlay = WpfXaml.Find<Border>(window, "dhcpBusyOverlay");
            bindFocusRing = WpfXaml.Find<Border>(window, "bindFocusRing");
            bindShimmerX = window.FindName("bindShimmerX") as TranslateTransform;
            dhcpShimmerX = window.FindName("dhcpShimmerX") as TranslateTransform;

            btnBind.Content = UiCopy.ApplyButton;
            btnDhcp.Content = UiCopy.DhcpButton;
            bindIdleContent = UiCopy.ApplyButton;
            dhcpIdleContent = UiCopy.DhcpButton;

            console.Document = new FlowDocument();
            console.Document.PagePadding = new Thickness(8, 6, 16, 6);
            console.Document.FontFamily = new FontFamily("Consolas");
            console.Document.FontSize = 12;
            consolePara = new Paragraph();
            consolePara.Margin = new Thickness(0);
            console.Document.Blocks.Add(consolePara);
            console.SizeChanged += delegate { FitConsolePage(); };

            cboConsoleFilter.Items.Add("All");
            cboConsoleFilter.Items.Add("Warnings and errors");
            cboConsoleFilter.SelectedIndex = 0;

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
                boundActive = false;
                boundFlags = null;
                UpdateGutter();
            };
            txtIPs.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler(
                delegate { UpdateGutter(); }), true);
            txtIPs.SizeChanged += delegate { UpdateGutter(); };
            txtGateway.TextChanged += delegate { if (engine != null) engine.SaveGatewaySettings(); };
            txtDns1.TextChanged += delegate { if (engine != null) engine.SaveGatewaySettings(); };
            txtDns2.TextChanged += delegate { if (engine != null) engine.SaveGatewaySettings(); };

            btnRefresh.Click += delegate
            {
                if (engine == null) return;
                using (DebugLog.Time("Refresh")) engine.LoadAdapters();
                RefreshBanner();
                RefreshBoundDots();
            };
            btnSaveList.Click += delegate
            {
                if (engine != null) engine.SaveList(false);
                SaveCurrentSite();
            };
            btnRestore.Click += delegate
            {
                if (engine != null) engine.RestoreDefaults();
                SaveCurrentSite();
            };
            btnShow.Click += delegate
            {
                if (engine == null) return;
                using (DebugLog.Time("Show Current"))
                {
                    string a = engine.GetSelectedAlias();
                    if (a == null) { engine.Log("No interface selected.", "WARN"); return; }
                    engine.ShowCurrentConfig(a);
                }
                RefreshBoundDots();
            };
            btnBind.Click += delegate { StartEngineRun(true); };
            btnDhcp.Click += delegate { StartEngineRun(false); };
            btnClear.Click += delegate
            {
                consoleEntries.Clear();
                consolePara.Inlines.Clear();
                if (consoleOverlay != null) consoleOverlay.SyncThumbOnly();
            };
            btnCopy.Click += delegate { CopyConsole(); };
            cboConsoleFilter.SelectionChanged += delegate { RebuildConsole(); };
            btnCompact.Click += delegate { SetCompact(!compact, true); };
            btnSaveAs.Click += OnSaveAs;
            btnRename.Click += OnRenameSite;
            btnDeleteList.Click += OnDeleteSite;
            cboSites.SelectionChanged += OnSiteChosen;
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
            double minW = compact ? 520 : 980;
            window.MinWidth = Math.Min(minW, wa.Width);
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
            window.DpiChanged += delegate { FitWorkArea(); UpdateGutter(); };
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
            ThreadPool.QueueUserWorkItem(delegate
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

        void StartEngineRun(bool apply)
        {
            if (engine == null || busy) return;
            if (apply && invalidCount > 0) return;
            string alias = engine.GetSelectedAlias();
            string before = engine.CaptureAdapterState(alias);
            SetBusy(apply ? "Applying static IPs..." : "Returning to DHCP...", apply);
            Thread t = new Thread(delegate()
            {
                bool ok = false;
                try
                {
                    if (apply)
                    {
                        using (DebugLog.Time("Apply"))
                            ok = engine.InvokeBind();
                    }
                    else
                    {
                        using (DebugLog.Time("Return to DHCP"))
                            ok = engine.InvokeDhcp();
                    }
                }
                catch (Exception ex)
                {
                    try { engine.Log(ex.Message, "ERR"); } catch { }
                    ok = false;
                }
                string after = "";
                try { after = engine.CaptureAdapterState(alias); } catch { after = ""; }
                window.Dispatcher.BeginInvoke(new Action(delegate
                {
                    engine.LogAdapterDiff(apply ? "Apply" : "Return", before, after);
                    if (apply)
                    {
                        EndBusy(ok, "Static IPs applied", "Failed to apply static IPs");
                        if (ok) RefreshBoundDots();
                    }
                    else
                    {
                        boundActive = false;
                        boundFlags = null;
                        EndBusy(ok, "Returned to DHCP", "Failed to return to DHCP");
                        UpdateGutter();
                    }
                }));
            });
            t.IsBackground = true;
            t.Start();
        }

        void SetBusy(string text, bool apply)
        {
            busy = true;
            SetInputsEnabled(false);
            ApplyBindEnabled();
            lblStatus.Foreground = BrushOf("BusyBrush", dark ? Color.FromRgb(200, 200, 200) : Color.FromRgb(60, 60, 60));
            lblStatus.Text = text;
            if (apply)
            {
                btnBind.Content = "Applying...";
                StartShimmer(bindBusyOverlay, bindShimmerX);
            }
            else
            {
                btnDhcp.Content = "Returning...";
                StartShimmer(dhcpBusyOverlay, dhcpShimmerX);
            }
        }

        void EndBusy(bool ok, string okMsg, string failMsg)
        {
            StopShimmer(bindBusyOverlay, bindShimmerX);
            StopShimmer(dhcpBusyOverlay, dhcpShimmerX);
            btnBind.Content = bindIdleContent;
            btnDhcp.Content = dhcpIdleContent;
            busy = false;
            SetInputsEnabled(true);
            ApplyBindEnabled();
            if (ok)
            {
                lblStatus.Foreground = BrushOf("OkBrush", dark ? Color.FromRgb(90, 200, 110) : Color.FromRgb(30, 120, 40));
                lblStatus.Text = "OK - " + okMsg;
                showingInvalidStatus = false;
                RefreshBanner();
            }
            else
            {
                lblStatus.Foreground = BrushOf("ErrBrush", dark ? Color.FromRgb(240, 100, 100) : Color.FromRgb(180, 40, 40));
                lblStatus.Text = failMsg;
                showingInvalidStatus = false;
                if (combo.Items.Count == 0)
                    ShowBanner(3, "No adapter can be read", "Click Refresh, or check that a network adapter is present.");
                else if (engine == null || string.IsNullOrEmpty(engine.GetSelectedAlias()))
                    ShowBanner(0, "No interface selected", "Choose a LAN interface below.");
                else
                    ShowFailureBanner(failMsg);
            }
            if (invalidCount > 0 && !busy) ApplyInvalidStatus();
        }

        void SetInputsEnabled(bool on)
        {
            combo.IsEnabled = on;
            chkAll.IsEnabled = on;
            chkKeepDhcp.IsEnabled = on && keepDhcpWantEnabled;
            chkGateway.IsEnabled = on;
            txtIPs.IsEnabled = on;
            txtGateway.IsEnabled = on;
            txtDns1.IsEnabled = on;
            txtDns2.IsEnabled = on;
            cboGwIp.IsEnabled = on;
            btnDhcp.IsEnabled = on;
            btnSaveList.IsEnabled = on;
            btnRestore.IsEnabled = on;
            btnShow.IsEnabled = on;
            btnRefresh.IsEnabled = on;
            btnSaveAs.IsEnabled = on;
            btnRename.IsEnabled = on;
            btnDeleteList.IsEnabled = on;
            cboSites.IsEnabled = on;
            btnCompact.IsEnabled = on;
        }

        void ApplyBindEnabled()
        {
            btnBind.IsEnabled = !busy && invalidCount == 0;
        }

        static void StartShimmer(Border overlay, TranslateTransform shift)
        {
            if (overlay == null) return;
            overlay.Visibility = Visibility.Visible;
            if (shift == null) return;
            DoubleAnimation anim = new DoubleAnimation();
            anim.From = -1;
            anim.To = 1;
            anim.Duration = TimeSpan.FromSeconds(1.1);
            anim.RepeatBehavior = RepeatBehavior.Forever;
            shift.BeginAnimation(TranslateTransform.XProperty, anim);
        }

        static void StopShimmer(Border overlay, TranslateTransform shift)
        {
            if (shift != null) shift.BeginAnimation(TranslateTransform.XProperty, null);
            if (overlay != null) overlay.Visibility = Visibility.Collapsed;
        }

        static string[] SplitLines(string text)
        {
            if (text == null) return new string[0];
            return text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
        }

        void UpdateGutter()
        {
            if (ipGutter == null || txtIPs == null) return;
            string[] lines = SplitLines(txtIPs.Text);
            IpLineResult[] results = IpListValidator.Validate(lines);
            invalidCount = IpListValidator.InvalidCount(results);
            if (!busy)
            {
                ApplyBindEnabled();
                if (invalidCount > 0) ApplyInvalidStatus();
                else if (showingInvalidStatus)
                {
                    showingInvalidStatus = false;
                    lblStatus.Foreground = BrushOf("FgBrush", dark ? Color.FromRgb(230, 230, 230) : Color.FromRgb(26, 26, 26));
                    lblStatus.Text = "Ready.";
                }
            }

            ipGutter.Children.Clear();
            Brush ok = BrushOf("OkBrush", dark ? Color.FromRgb(90, 200, 110) : Color.FromRgb(30, 120, 40));
            Brush err = BrushOf("ErrBrush", dark ? Color.FromRgb(240, 100, 100) : Color.FromRgb(180, 40, 40));
            Brush gray = BrushOf("SubBrush", Color.FromRgb(0x96, 0x96, 0x96));
            string text = txtIPs.Text ?? "";
            for (int i = 0; i < results.Length; i++)
            {
                if (results[i].Ignored) continue;
                Rect rect = LineRect(text, i);
                if (rect == Rect.Empty) continue;
                double h = rect.Height;
                if (h < 1) h = txtIPs.FontSize * 1.3;
                if (rect.Top + h < 0 || rect.Top > ipGutter.ActualHeight + 2) continue;

                FrameworkElement mark = results[i].Valid ? MakeCheck(ok, h) : MakeCross(err, h);
                if (!results[i].Valid && !string.IsNullOrEmpty(results[i].Reason))
                    mark.ToolTip = results[i].Reason;
                Canvas.SetLeft(mark, 4);
                Canvas.SetTop(mark, rect.Top + Math.Max(0, (h - 12) / 2));
                ipGutter.Children.Add(mark);

                if (boundActive && boundFlags != null && i < boundFlags.Length)
                {
                    Ellipse dot = new Ellipse();
                    double d = Math.Max(6, Math.Min(9, h * 0.45));
                    dot.Width = d;
                    dot.Height = d;
                    bool on = boundFlags[i];
                    dot.Fill = on ? ok : gray;
                    dot.ToolTip = on ? "bound" : "not bound";
                    Canvas.SetLeft(dot, 22);
                    Canvas.SetTop(dot, rect.Top + Math.Max(0, (h - d) / 2));
                    ipGutter.Children.Add(dot);
                }
            }
        }

        void ApplyInvalidStatus()
        {
            showingInvalidStatus = true;
            lblStatus.Foreground = BrushOf("WarnBrush", dark ? Color.FromRgb(255, 200, 61) : Color.FromRgb(156, 92, 0));
            lblStatus.Text = IpListValidator.InvalidStatus(invalidCount);
        }

        Rect LineRect(string text, int line)
        {
            int at = 0;
            int n = 0;
            while (n < line && at < text.Length)
            {
                int nl = text.IndexOf('\n', at);
                if (nl < 0) { at = text.Length; break; }
                at = nl + 1;
                n++;
            }
            if (at > text.Length) at = text.Length;
            try { return txtIPs.GetRectFromCharacterIndex(at); }
            catch { return Rect.Empty; }
        }

        static FrameworkElement MakeCheck(Brush brush, double lineH)
        {
            double s = Math.Max(10, Math.Min(14, lineH * 0.7));
            System.Windows.Shapes.Path p = new System.Windows.Shapes.Path();
            p.Data = Geometry.Parse("M 1,5.5 L 4,9 L 10,2");
            p.Stroke = brush;
            p.StrokeThickness = 1.8;
            p.StrokeStartLineCap = PenLineCap.Round;
            p.StrokeEndLineCap = PenLineCap.Round;
            p.StrokeLineJoin = PenLineJoin.Round;
            p.Width = s;
            p.Height = s;
            p.Stretch = Stretch.Uniform;
            p.SnapsToDevicePixels = true;
            return p;
        }

        static FrameworkElement MakeCross(Brush brush, double lineH)
        {
            double s = Math.Max(10, Math.Min(14, lineH * 0.7));
            System.Windows.Shapes.Path p = new System.Windows.Shapes.Path();
            p.Data = Geometry.Parse("M 2,2 L 10,10 M 10,2 L 2,10");
            p.Stroke = brush;
            p.StrokeThickness = 1.8;
            p.StrokeStartLineCap = PenLineCap.Round;
            p.StrokeEndLineCap = PenLineCap.Round;
            p.Width = s;
            p.Height = s;
            p.Stretch = Stretch.Uniform;
            p.SnapsToDevicePixels = true;
            return p;
        }

        void RefreshBoundDots()
        {
            if (engine == null) return;
            string alias = engine.GetSelectedAlias();
            string[] present = engine.ReadPresentIPv4(alias);
            boundFlags = BoundState.Compute(SplitLines(txtIPs.Text), present);
            boundActive = true;
            UpdateGutter();
        }

        void InitSiteLists()
        {
            try
            {
                siteStore = new SiteListStore(UiSettingsFolder());
                siteStore.Initialize();
                FillSiteCombo();
                if (!string.IsNullOrEmpty(siteStore.CurrentName)
                    && !string.Equals(siteStore.CurrentName, "Default", StringComparison.OrdinalIgnoreCase))
                {
                    string[] lines = siteStore.LoadCurrent();
                    txtIPs.Text = string.Join("\r\n", lines);
                }
            }
            catch { }
        }

        void FillSiteCombo()
        {
            if (cboSites == null || siteStore == null) return;
            suppressingSites = true;
            try
            {
                cboSites.Items.Clear();
                string[] names = siteStore.Names;
                int sel = -1;
                for (int i = 0; i < names.Length; i++)
                {
                    cboSites.Items.Add(names[i]);
                    if (string.Equals(names[i], siteStore.CurrentName, StringComparison.OrdinalIgnoreCase))
                        sel = i;
                }
                if (sel >= 0) cboSites.SelectedIndex = sel;
                else if (cboSites.Items.Count > 0) cboSites.SelectedIndex = 0;
            }
            finally { suppressingSites = false; }
        }

        void SaveCurrentSite()
        {
            if (siteStore == null) return;
            try { siteStore.SaveCurrent(SplitLines(txtIPs.Text)); }
            catch { }
        }

        void OnSiteChosen(object sender, SelectionChangedEventArgs e)
        {
            if (suppressingSites || siteStore == null || cboSites.SelectedItem == null) return;
            string name = cboSites.SelectedItem as string;
            SaveCurrentSite();
            string[] lines;
            string err;
            if (!siteStore.SwitchTo(name, out lines, out err))
            {
                SetWarnStatus(err);
                return;
            }
            txtIPs.Text = string.Join("\r\n", lines);
        }

        void OnSaveAs(object sender, RoutedEventArgs e)
        {
            if (siteStore == null) return;
            string name = InputDialog.Prompt(window, dark, "Save list as", "Name for this list", "");
            if (name == null) return;
            string err;
            if (!siteStore.SaveAs(name, SplitLines(txtIPs.Text), out err))
            {
                SetWarnStatus(err);
                return;
            }
            FillSiteCombo();
            SetOkStatus("Saved list '" + siteStore.CurrentName + "'.");
        }

        void OnRenameSite(object sender, RoutedEventArgs e)
        {
            if (siteStore == null || string.IsNullOrEmpty(siteStore.CurrentName)) return;
            string name = InputDialog.Prompt(window, dark, "Rename list", "New name", siteStore.CurrentName);
            if (name == null) return;
            string err;
            if (!siteStore.Rename(siteStore.CurrentName, name, out err))
            {
                SetWarnStatus(err);
                return;
            }
            FillSiteCombo();
            SetOkStatus("Renamed list to '" + siteStore.CurrentName + "'.");
        }

        void OnDeleteSite(object sender, RoutedEventArgs e)
        {
            if (siteStore == null || string.IsNullOrEmpty(siteStore.CurrentName)) return;
            string gone = siteStore.CurrentName;
            string err;
            if (!siteStore.Delete(gone, out err))
            {
                SetWarnStatus(err);
                return;
            }
            string[] lines = siteStore.LoadCurrent();
            FillSiteCombo();
            txtIPs.Text = string.Join("\r\n", lines);
            SetOkStatus("Deleted list '" + gone + "'.");
        }

        void SetWarnStatus(string text)
        {
            lblStatus.Foreground = BrushOf("WarnBrush", dark ? Color.FromRgb(255, 200, 61) : Color.FromRgb(156, 92, 0));
            lblStatus.Text = text ?? "";
            showingInvalidStatus = false;
        }

        void SetOkStatus(string text)
        {
            lblStatus.Foreground = BrushOf("OkBrush", dark ? Color.FromRgb(90, 200, 110) : Color.FromRgb(30, 120, 40));
            lblStatus.Text = text ?? "";
            showingInvalidStatus = false;
        }

        void CopyConsole()
        {
            try
            {
                string text = ShownConsoleText();
                Clipboard.SetText(text ?? "");
                int n = 0;
                foreach (ConsoleEntry e in consoleEntries)
                    if (EntryVisible(e)) n++;
                SetOkStatus(n == 1 ? "Copied 1 console line." : "Copied " + n.ToString() + " console lines.");
            }
            catch (Exception ex)
            {
                SetWarnStatus("Could not copy: " + ex.Message);
            }
        }

        string ShownConsoleText()
        {
            System.Text.StringBuilder sb = new System.Text.StringBuilder();
            foreach (ConsoleEntry e in consoleEntries)
            {
                if (!EntryVisible(e)) continue;
                sb.Append(e.Stamp).Append(e.Level).Append("  ").Append(e.Message).Append("\r\n");
            }
            return sb.ToString();
        }

        bool EntryVisible(ConsoleEntry e)
        {
            if (cboConsoleFilter == null || cboConsoleFilter.SelectedIndex <= 0) return true;
            string lv = e.Level ?? "";
            return lv == "WARN" || lv == "ERR" || lv == "ERROR";
        }

        void RebuildConsole()
        {
            if (consolePara == null) return;
            consolePara.Inlines.Clear();
            foreach (ConsoleEntry e in consoleEntries)
            {
                if (EntryVisible(e)) AppendEntryVisual(e);
            }
            FitConsolePage();
            console.ScrollToEnd();
            if (consoleOverlay != null) consoleOverlay.SyncThumbOnly();
        }

        void AppendEntryVisual(ConsoleEntry e)
        {
            Brush body = console.Foreground;
            if (e.Level == "OK" || e.Level == "WARN" || e.Level == "ERR" || e.Level == "ERROR")
            {
                System.Drawing.Color c = ConsoleBox.LevelColor(e.Level, System.Drawing.Color.Empty);
                SolidColorBrush b = new SolidColorBrush(Color.FromRgb(c.R, c.G, c.B));
                b.Freeze();
                body = b;
            }
            Run stamp = new Run(e.Stamp);
            stamp.Foreground = BrushOf("TimestampBrush", Color.FromRgb(138, 138, 138));
            Run text = new Run((string.IsNullOrEmpty(e.Level) ? "INFO" : e.Level) + "  " + e.Message);
            text.Foreground = body;
            consolePara.Inlines.Add(stamp);
            consolePara.Inlines.Add(text);
            consolePara.Inlines.Add(new LineBreak());
        }

        void LoadCompactPref()
        {
            bool on = false;
            try
            {
                string path = IoPath.Combine(UiSettingsFolder(), "compact-mode.txt");
                if (File.Exists(path))
                    on = File.ReadAllText(path).Trim() == "1";
            }
            catch { on = false; }
            if (on) SetCompact(true, false);
        }

        void SaveCompactPref()
        {
            try
            {
                string dir = UiSettingsFolder();
                if (!Directory.Exists(dir)) Directory.CreateDirectory(dir);
                File.WriteAllText(IoPath.Combine(dir, "compact-mode.txt"), compact ? "1" : "0");
            }
            catch { }
        }

        void SetCompact(bool on, bool persist)
        {
            if (on == compact && rightColumn != null
                && rightColumn.Visibility == (on ? Visibility.Collapsed : Visibility.Visible))
            {
                if (persist) SaveCompactPref();
                return;
            }
            if (on)
            {
                double cur = window.ActualWidth > 1 ? window.ActualWidth : window.Width;
                if (cur > 1) restoredWidth = cur;
            }
            compact = on;
            if (on)
            {
                colGap.Width = new GridLength(0);
                colRight.MinWidth = 0;
                colRight.Width = new GridLength(0);
                rightColumn.Visibility = Visibility.Collapsed;
                btnCompact.Content = "_Show console";
                FitWorkArea();
                double left = leftColumn != null && leftColumn.ActualWidth > 80 ? leftColumn.ActualWidth : 520;
                window.Width = Math.Max(window.MinWidth, left + 32);
            }
            else
            {
                colGap.Width = new GridLength(18);
                colRight.MinWidth = 240;
                colRight.Width = new GridLength(1, GridUnitType.Star);
                rightColumn.Visibility = Visibility.Visible;
                btnCompact.Content = "_Hide console";
                FitWorkArea();
                if (restoredWidth > window.MinWidth) window.Width = restoredWidth;
            }
            window.UpdateLayout();
            if (persist) SaveCompactPref();
        }

        static Brush BrushOf(string key, Color fallback)
        {
            object v = Application.Current != null ? Application.Current.TryFindResource(key) : null;
            SolidColorBrush b = v as SolidColorBrush;
            if (b != null) return b;
            return new SolidColorBrush(fallback);
        }

        T UiGet<T>(Func<T> fn)
        {
            try
            {
                if (window.Dispatcher.CheckAccess()) return fn();
                return (T)window.Dispatcher.Invoke(fn);
            }
            catch
            {
                return default(T);
            }
        }

        void UiCall(Action fn)
        {
            if (window.Dispatcher.CheckAccess()) fn();
            else window.Dispatcher.Invoke(fn);
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
            if (!window.Dispatcher.CheckAccess())
            {
                window.Dispatcher.BeginInvoke(new Action<string, string>(LogConsoleCore), msg, level);
                return;
            }
            LogConsoleCore(msg, level);
        }

        void LogConsoleCore(string msg, string level)
        {
            ConsoleEntry e = new ConsoleEntry();
            e.Stamp = DateTime.Now.ToString("HH:mm:ss") + "  ";
            e.Level = string.IsNullOrEmpty(level) ? "INFO" : level;
            e.Message = msg ?? "";
            consoleEntries.Add(e);
            if (EntryVisible(e))
            {
                AppendEntryVisual(e);
                FitConsolePage();
                console.ScrollToEnd();
                if (consoleOverlay != null) consoleOverlay.SyncThumbOnly();
            }
        }

        string[] IBindHost.IpLines
        {
            get
            {
                string[] v = UiGet(delegate
                {
                    return txtIPs.Text.Replace("\r\n", "\n").Replace("\r", "\n").Split('\n');
                });
                return v ?? new string[0];
            }
            set
            {
                UiCall(delegate
                {
                    txtIPs.Text = value == null ? "" : string.Join("\r\n", value);
                });
            }
        }

        bool IBindHost.ShowAllChecked
        {
            get { return UiGet(delegate { return chkAll.IsChecked == true; }); }
        }

        bool IBindHost.GatewayChecked
        {
            get { return UiGet(delegate { return chkGateway.IsChecked == true; }); }
            set { UiCall(delegate { chkGateway.IsChecked = value; }); }
        }

        string IBindHost.GatewayText
        {
            get { return UiGet(delegate { return txtGateway.Text ?? ""; }); }
            set { UiCall(delegate { txtGateway.Text = value ?? ""; }); }
        }

        string IBindHost.Dns1Text
        {
            get { return UiGet(delegate { return txtDns1.Text ?? ""; }); }
            set { UiCall(delegate { txtDns1.Text = value ?? ""; }); }
        }

        string IBindHost.Dns2Text
        {
            get { return UiGet(delegate { return txtDns2.Text ?? ""; }); }
            set { UiCall(delegate { txtDns2.Text = value ?? ""; }); }
        }

        string IBindHost.SelectedGatewayItem
        {
            get
            {
                return UiGet(delegate
                {
                    return cboGwIp.SelectedIndex >= 0 ? cboGwIp.SelectedItem as string : null;
                });
            }
        }

        int IBindHost.GatewayComboCount
        {
            get { return UiGet(delegate { return cboGwIp.Items.Count; }); }
        }

        string IBindHost.GatewayComboItemAt(int index)
        {
            return UiGet(delegate { return cboGwIp.Items[index] as string; });
        }

        int IBindHost.GatewaySelectedIndex
        {
            get { return UiGet(delegate { return cboGwIp.SelectedIndex; }); }
            set
            {
                UiCall(delegate
                {
                    suppressingGateway = true;
                    try { cboGwIp.SelectedIndex = value; }
                    finally { suppressingGateway = false; }
                });
            }
        }

        void IBindHost.ReplaceGatewayItems(List<string> keys)
        {
            UiCall(delegate
            {
                suppressingGateway = true;
                try
                {
                    cboGwIp.Items.Clear();
                    foreach (string k in keys) cboGwIp.Items.Add(k);
                }
                finally { suppressingGateway = false; }
            });
        }

        void IBindHost.ApplyGatewayLayout()
        {
            UiCall(delegate
            {
                bool on = chkGateway.IsChecked == true;
                gatewayRow1.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
                gatewayRow2.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
                lblIPs.Text = IpHelp;
            });
        }

        bool IBindHost.KeepDhcpChecked
        {
            get { return UiGet(delegate { return chkKeepDhcp.IsChecked == true; }); }
            set { UiCall(delegate { chkKeepDhcp.IsChecked = value; }); }
        }

        bool IBindHost.KeepDhcpEnabled
        {
            get { return UiGet(delegate { return chkKeepDhcp.IsEnabled; }); }
            set
            {
                UiCall(delegate
                {
                    keepDhcpWantEnabled = value;
                    if (!busy) chkKeepDhcp.IsEnabled = value;
                });
            }
        }

        void IBindHost.SetKeepDhcpToolTip(string text)
        {
            UiCall(delegate { chkKeepDhcp.ToolTip = text; });
        }

        int IBindHost.SelectedAdapterIndex
        {
            get { return UiGet(delegate { return combo.SelectedIndex; }); }
        }

        void IBindHost.BindAdapterList(List<Adapter> list, int selectedIndex)
        {
            UiCall(delegate
            {
                combo.Items.Clear();
                foreach (Adapter a in list)
                    combo.Items.Add(AdapterDisplay.Format(a.Name, a.Description, a.Status));
                if (selectedIndex >= 0 && selectedIndex < combo.Items.Count)
                    combo.SelectedIndex = selectedIndex;
            });
        }

        bool IBindHost.ConfirmDisconnected(string alias, string status)
        {
            return UiGet(delegate
            {
                MessageBoxResult ans = MessageBox.Show(window,
                    "The interface '" + alias + "' is currently '" + status + "', not connected.\r\n\r\n" +
                    "Windows will not bind static addresses to a disconnected interface, so this likely " +
                    "won't take effect until it is connected.\r\n\r\nApply anyway?",
                    "Interface not connected", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                return ans == MessageBoxResult.Yes;
            });
        }

        void IBindHost.RunOnUi(Action action)
        {
            window.Dispatcher.BeginInvoke(action);
        }

        bool IBindHost.IsHostDisposed
        {
            get { return window == null || window.Dispatcher.HasShutdownStarted; }
        }

        void IBindHost.ShowUpdateAvailable(string remoteVersion)
        {
            UiCall(delegate
            {
                lblUpdateBanner.Text =
                    "Version " + VersionText.ForDisplay(remoteVersion) + " is available.";
                updateBanner.Visibility = Visibility.Visible;
                lblStatus.Visibility = Visibility.Collapsed;
            });
        }

        void IBindHost.HideUpdateBanner()
        {
            UiCall(delegate
            {
                updateBanner.Visibility = Visibility.Collapsed;
                lblStatus.Visibility = Visibility.Visible;
            });
        }

        void IBindHost.SetUpdatingBanner()
        {
            UiCall(delegate
            {
                btnUpdateNow.IsEnabled = false;
                btnUpdateLater.IsEnabled = false;
                btnUpdateNow.Content = "Updating...";
                lblUpdateBanner.Text = "Downloading update; IPbind will restart.";
            });
        }

        void IBindHost.ShowUpdateError(string message)
        {
            UiCall(delegate
            {
                lblUpdateBanner.Text = message;
                btnUpdateNow.Content = "Update";
                btnUpdateNow.IsEnabled = true;
                btnUpdateLater.IsEnabled = true;
            });
        }

        void IBindHost.ExitApp()
        {
            UiCall(delegate
            {
                if (Application.Current != null) Application.Current.Shutdown();
            });
        }

        void IBindHost.ShowDhcpReminder(out bool proceed, out bool remember)
        {
            bool p = false, r = false;
            UiCall(delegate
            {
                DhcpReminderWindow reminder = new DhcpReminderWindow(window, dark);
                bool? result = reminder.ShowDialog();
                p = result == true;
                r = p && reminder.DontShowAgain;
            });
            proceed = p;
            remember = r;
        }

        void IBindHost.CloseHost()
        {
            UiCall(delegate { window.Close(); });
        }

        // ---------------- Beta-only uicheck hooks (no netsh) ----------------
        public void UiCheckSimulateBanner(int kind, string headline, string detail)
        {
            ShowBanner(kind, headline, detail);
        }

        public void UiCheckSimulateValidation(string text)
        {
            txtIPs.Text = text ?? "";
            UpdateGutter();
        }

        public bool UiCheckApplyEnabled { get { return btnBind.IsEnabled; } }

        public void UiCheckSimulateBound(string[] present)
        {
            boundFlags = BoundState.Compute(SplitLines(txtIPs.Text), present);
            boundActive = true;
            UpdateGutter();
        }

        public void UiCheckSimulateSiteLists(string[] names)
        {
            suppressingSites = true;
            try
            {
                cboSites.Items.Clear();
                if (names == null) return;
                foreach (string n in names) cboSites.Items.Add(n);
                if (cboSites.Items.Count > 0) cboSites.SelectedIndex = 0;
            }
            finally { suppressingSites = false; }
        }

        public void UiCheckOpenSiteDropDown()
        {
            cboSites.IsDropDownOpen = true;
        }

        public void UiCheckSimulateConsoleFilter()
        {
            consoleEntries.Clear();
            consolePara.Inlines.Clear();
            AddSimulatedLog("10:00:01  ", "INFO", "Listed 2 interface(s) (wired only).");
            AddSimulatedLog("10:00:02  ", "OK", "Saved 8 address(es) to disk.");
            AddSimulatedLog("10:00:03  ", "WARN", "Skipping invalid entry: '256.1.1.1/24'");
            AddSimulatedLog("10:00:04  ", "ERR", "No valid addresses to apply.");
            AddSimulatedLog("10:00:05  ", "INFO", "Interface set to 'Ethernet' (remembered).");
            cboConsoleFilter.SelectedIndex = 1;
            RebuildConsole();
        }

        void AddSimulatedLog(string stamp, string level, string msg)
        {
            ConsoleEntry e = new ConsoleEntry();
            e.Stamp = stamp;
            e.Level = level;
            e.Message = msg;
            consoleEntries.Add(e);
        }

        public void UiCheckSetCompact(bool on)
        {
            SetCompact(on, false);
        }

        public bool UiCheckConsoleVisible
        {
            get { return rightColumn != null && rightColumn.Visibility == Visibility.Visible; }
        }

        public double UiCheckWidth
        {
            get { return window.ActualWidth > 1 ? window.ActualWidth : window.Width; }
        }

        public void UiCheckSimulateBusy()
        {
            SetBusy("Applying static IPs...", true);
            if (bindShimmerX == null) return;
            bindShimmerX.BeginAnimation(TranslateTransform.XProperty, null);
            bindShimmerX.X = 0.25;
        }

        public void UiCheckSimulateFocusApply()
        {
            try
            {
                System.Reflection.PropertyInfo p = typeof(KeyboardNavigation).GetProperty(
                    "AlwaysShowFocusVisual",
                    System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
                if (p != null) p.SetValue(null, true, null);
            }
            catch { }
            btnBind.Focus();
            Keyboard.Focus(btnBind);
            bindFocusRing.Visibility = Visibility.Visible;
        }

        public void UiCheckResetVisuals()
        {
            StopShimmer(bindBusyOverlay, bindShimmerX);
            StopShimmer(dhcpBusyOverlay, dhcpShimmerX);
            btnBind.Content = bindIdleContent;
            btnDhcp.Content = dhcpIdleContent;
            busy = false;
            SetInputsEnabled(true);
            bindFocusRing.Visibility = Visibility.Collapsed;
            cboSites.IsDropDownOpen = false;
            cboConsoleFilter.SelectedIndex = 0;
            SetCompact(false, false);
            boundActive = false;
            boundFlags = null;
            showingInvalidStatus = false;
            lblStatus.Foreground = BrushOf("FgBrush", dark ? Color.FromRgb(230, 230, 230) : Color.FromRgb(26, 26, 26));
            lblStatus.Text = "Ready.";
            ApplyBindEnabled();
            UpdateGutter();
        }
    }

    sealed class ConsoleEntry
    {
        public string Stamp;
        public string Level;
        public string Message;
    }
}
