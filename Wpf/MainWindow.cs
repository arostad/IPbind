using System;
using System.Collections.Generic;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;

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
        TextBlock lblIPs, lblStatus, lblUpdateBanner, lblTitle;
        ComboBox cboGwIp;
        FrameworkElement gatewayRow1, gatewayRow2;
        Button btnBind, btnDhcp, btnSaveList, btnRestore, btnShow, btnRefresh, btnAbout, btnClear;
        Button btnUpdateNow, btnUpdateLater;
        Border updateBanner;
        RichTextBox console;
        Paragraph consolePara;
        bool suppressingGateway;

        const string IpLabelPlain =
            "IP addresses to bind - one per line, CIDR (e.g. 192.168.1.98/24). No gateway or DNS:";
        const string IpLabelGateway =
            "IP addresses to bind - one per line, CIDR (e.g. 192.168.1.98/24):";
        const string KeepDhcpTipOn =
            "On: DHCP stays on and these addresses are added alongside it.\r\n" +
            "Off: static addresses only, no DHCP.";
        const string KeepDhcpTipOff =
            "Needs Windows 10 version 1703 or later.\r\n" +
            "Apply uses static addresses only.";

        public Window Window { get { return window; } }

        public MainWindow() : this(false) { }

        public MainWindow(bool selftest)
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
            if (!selftest)
            {
                engine.Startup();
                window.ContentRendered += OnFirstShown;
            }
        }

        void Wire()
        {
            lblTitle = WpfXaml.Find<TextBlock>(window, "lblTitle");
            lblTitle.Text = BuildInfo.AppName;
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
            console = WpfXaml.Find<RichTextBox>(window, "console");

            btnBind.Content = UiCopy.ApplyButton;
            btnDhcp.Content = UiCopy.DhcpButton;

            console.Document = new FlowDocument();
            console.Document.PagePadding = new Thickness(6, 4, 14, 4);
            console.Document.FontFamily = new FontFamily("Consolas");
            console.Document.FontSize = 12;
            consolePara = new Paragraph();
            consolePara.Margin = new Thickness(0);
            console.Document.Blocks.Add(consolePara);

            chkKeepDhcp.ToolTip = KeepDhcpTipOn;
            chkGateway.ToolTip = "Only enable if you know what you're doing!";

            combo.SelectionChanged += delegate { if (engine != null) engine.OnAdapterChosen(); };
            chkAll.Checked += delegate { if (engine != null) engine.LoadAdapters(); };
            chkAll.Unchecked += delegate { if (engine != null) engine.LoadAdapters(); };
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

            btnRefresh.Click += delegate { if (engine != null) { using (DebugLog.Time("Refresh")) engine.LoadAdapters(); } };
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

        static string UpdateCheckPath()
        {
            return Path.Combine(
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
            window.MaxWidth = wa.Width;
            window.MaxHeight = wa.Height;
            window.MinWidth = Math.Min(720, wa.Width);
            window.MinHeight = Math.Min(480, wa.Height);
            if (window.Width > wa.Width) window.Width = wa.Width;
            if (window.Height > wa.Height) window.Height = wa.Height;
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
            }
            else
            {
                lblStatus.Foreground = BrushOf("ErrBrush", dark ? Color.FromRgb(240, 100, 100) : Color.FromRgb(180, 40, 40));
                lblStatus.Text = failMsg;
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
            return b ?? new SolidColorBrush(fallback);
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
            string line = "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + level + "  " + msg + "\r\n";
            Brush brush = console.Foreground;
            if (level == "ERR") brush = BrushOf("ErrBrush", Color.FromRgb(240, 100, 100));
            else if (level == "WARN") brush = BrushOf("WarnBrush", Color.FromRgb(255, 200, 61));
            else if (level == "OK") brush = BrushOf("OkBrush", Color.FromRgb(90, 200, 110));
            Run run = new Run(line);
            run.Foreground = brush;
            consolePara.Inlines.Add(run);
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
            lblIPs.Text = on ? IpLabelGateway : IpLabelPlain;
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
