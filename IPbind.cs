// IPbind - Static IP Binder for accessing air-gapped industrial automation systems
// A genuine compiled .NET WinForms application (no ps2exe / no embedded PowerShell).
// Binds multiple static IPv4 addresses (no gateway/DNS) to a chosen LAN interface for
// reaching air-gapped equipment web GUIs across different IP ranges, with one-click
// DHCP revert. Local network configuration is the app's purpose; update checks are the
// only intentional outbound internet traffic.
//
// DPI handling: the layout is scaled explicitly in code from the real device DPI read
// at startup, so it renders correctly at any Windows scaling (100/125/150/175%) without
// relying on WinForms auto-scaling. Version metadata is generated per-build in Version.cs.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Windows.Forms;

[assembly: AssemblyTitle("Static IP Binder for accessing air-gapped automation equipment spread across multiple IP ranges")]
[assembly: AssemblyDescription("Binds multiple static IPv4 addresses to a LAN interface (no gateway/DNS) so air-gapped automation equipment on different IP ranges is reachable, with one-click DHCP revert.")]
[assembly: AssemblyProduct("IPbind")]
[assembly: AssemblyCompany("Andy Rostad")]
[assembly: AssemblyCopyright("Copyright © 2026 Andy Rostad. MIT License.")]
// AssemblyVersion / AssemblyFileVersion / AssemblyInformationalVersion and the
// BuildInfo.Version string are generated fresh on every compile into Version.cs.

namespace IPbind
{
    static class Program
    {
        [STAThread]
        static void Main()
        {
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }

    class Adapter
    {
        public string Name;
        public string Description;
        public string Status;
    }

    sealed class UpdateInfo
    {
        public string RemoteVersion;
        public bool IsNewer;
        public string Error;
    }

    static class UpdateChecker
    {
        const string WorkerVersionUrl =
            "https://ipbind-update-pings.andy-s-account-376.workers.dev/version.txt";
        const string VersionUrl =
            "https://github.com/arostad/IPbind/releases/download/latest/version.txt";
        const string ExeUrl =
            "https://github.com/arostad/IPbind/releases/download/latest/IPbind.exe";
        const string ChecksumUrl =
            "https://github.com/arostad/IPbind/releases/download/latest/IPbind.exe.sha256";
        const string ExeFileName = "IPbind.exe";
        const long MinExeBytes = 20L * 1024L;
        const long MaxExeBytes = 500L * 1024L * 1024L;
        const int MaxRedirects = 5;

        static readonly HashSet<string> TrustedDownloadHosts =
            new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "ipbind-update-pings.andy-s-account-376.workers.dev",
                "github.com",
                "objects.githubusercontent.com",
                "release-assets.githubusercontent.com"
            };

        static readonly HttpClient Http = CreateClient();
        static readonly Regex ChecksumPattern = new Regex(
            @"\A(?<hash>[0-9a-fA-F]{64})  IPbind\.exe[ \t]*(?:\r?\n)?\z",
            RegexOptions.CultureInvariant | RegexOptions.Compiled);

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
            using (HttpResponseMessage response = GetTrusted(ChecksumUrl))
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

                    Match match = ChecksumPattern.Match(new string(buffer, 0, count));
                    if (!match.Success)
                        throw new InvalidOperationException(
                            "The update checksum was not readable.");
                    return match.Groups["hash"].Value.ToUpperInvariant();
                }
            }
        }

        static void DownloadExe(string destination)
        {
            using (HttpResponseMessage response = GetTrusted(ExeUrl))
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
                    int read;
                    while ((read = input.Read(buffer, 0, buffer.Length)) > 0)
                    {
                        total += read;
                        if (total > MaxExeBytes)
                            throw new InvalidOperationException(
                                "The update download was unexpectedly large.");
                        output.Write(buffer, 0, read);
                    }
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
                "IPbind");
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
                    if (string.Equals(
                        Sha256(pending), expectedHash, StringComparison.OrdinalIgnoreCase))
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

        public static UpdateInfo Check()
        {
            string remoteText;
            Version remote;
            string error;
            if (TryReadVersion(WorkerVersionUrl, out remoteText, out remote, out error)
                || TryReadVersion(VersionUrl, out remoteText, out remote, out error))
            {
                Version current;
                if (!Version.TryParse(BuildInfo.Version, out current))
                    current = new Version(0, 0);
                return new UpdateInfo
                {
                    RemoteVersion = remoteText,
                    IsNewer = remote.CompareTo(current) > 0,
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
            return "\"" + value.Replace("\"", "\\\"") + "\"";
        }

        public static void DownloadAndRestart()
        {
            string exe = ResolveInstalledExe();
            string directory = Path.GetDirectoryName(exe);
            if (string.IsNullOrEmpty(directory))
                throw new InvalidOperationException("Could not resolve the install directory.");
            Directory.CreateDirectory(directory);

            string expectedHash;
            string pending = DownloadVerifiedExe(directory, out expectedHash);
            string errorFile = Path.Combine(directory, "update-error.txt");
            string script = Path.Combine(
                ChooseStagingDirectory(directory),
                ".IPbind-update-" + Guid.NewGuid().ToString("N") + ".ps1");
            string scriptText = @"
$ErrorActionPreference = ""Stop""
$pending = $env:IPBIND_UPDATE_PENDING
$exe = $env:IPBIND_UPDATE_EXE
$expectedHash = $env:IPBIND_UPDATE_SHA256
$errorFile = $env:IPBIND_UPDATE_ERROR
try {
    $process = Get-Process -Id ([int]$env:IPBIND_UPDATE_PID) -ErrorAction SilentlyContinue
    if ($process) { $process.WaitForExit() }
    if ((Get-FileHash -LiteralPath $pending -Algorithm SHA256).Hash -ne $expectedHash) {
        throw ""The downloaded update failed its integrity check.""
    }
    $length = (Get-Item -LiteralPath $pending).Length
    if ($length -lt 20480 -or $length -gt 524288000) {
        throw ""The downloaded update has an unexpected size.""
    }
    for ($attempt = 0; $attempt -lt 8; $attempt++) {
        try {
            Move-Item -LiteralPath $pending -Destination $exe -Force
            break
        } catch {
            if ($attempt -eq 7) { throw }
            Start-Sleep -Seconds 1
        }
    }
    if (-not (Test-Path -LiteralPath $exe)) {
        throw ""The updated executable is missing.""
    }
    Remove-Item -LiteralPath $errorFile -Force -ErrorAction SilentlyContinue
    Start-Process -FilePath $exe
} catch {
    ""Update failed: $($_.Exception.Message)"" | Set-Content -LiteralPath $errorFile
    if (Test-Path -LiteralPath $exe) { Start-Process -FilePath $exe }
} finally {
    Remove-Item -LiteralPath $PSCommandPath -Force -ErrorAction SilentlyContinue
}
";
            using (FileStream stream = new FileStream(
                script, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            using (StreamWriter writer = new StreamWriter(stream, new UTF8Encoding(true)))
                writer.Write(scriptText);

            ProcessStartInfo start = new ProcessStartInfo();
            start.FileName = "powershell.exe";
            start.Arguments =
                "-NoProfile -NonInteractive -ExecutionPolicy Bypass -File " + QuoteArgument(script);
            start.CreateNoWindow = true;
            start.UseShellExecute = false;
            start.EnvironmentVariables["IPBIND_UPDATE_PENDING"] = pending;
            start.EnvironmentVariables["IPBIND_UPDATE_EXE"] = exe;
            start.EnvironmentVariables["IPBIND_UPDATE_SHA256"] = expectedHash;
            start.EnvironmentVariables["IPBIND_UPDATE_ERROR"] = errorFile;
            start.EnvironmentVariables["IPBIND_UPDATE_PID"] =
                Process.GetCurrentProcess().Id.ToString(
                    System.Globalization.CultureInfo.InvariantCulture);
            try
            {
                if (Process.Start(start) == null)
                    throw new InvalidOperationException("Could not start the update helper.");
            }
            catch
            {
                try { File.Delete(script); } catch { }
                try { File.Delete(pending); } catch { }
                throw;
            }
        }
    }

    sealed class AboutForm : Form
    {
        readonly Button checkButton;
        readonly Label statusLabel;
        readonly float sc;
        UpdateInfo availableUpdate;

        public AboutForm(Form owner, bool dark, float scale)
        {
            sc = scale > 0f ? scale : 1f;
            Text = "About IPbind";
            Font = new Font("Segoe UI", 9F);
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            ClientSize = Z(430, 330);
            try { Icon = owner.Icon; } catch { }

            Label title = MakeLabel("IPbind", 18F, FontStyle.Bold, 24, 20, 382, 34);
            Label version = MakeLabel(
                "Version " + BuildInfo.Version, 9F, FontStyle.Regular, 24, 58, 382, 22);
            Label tagline = MakeLabel(
                "One-click Static IP Binder for easily accessing air-gapped equipment " +
                "spread across multiple IP ranges",
                9F, FontStyle.Regular, 24, 82, 382, 58);

            LinkLabel directed = MakeLink(
                "App carefully directed by Andy Rostad",
                "Andy Rostad", "https://github.com/arostad", 24, 142, 382, 22);
            LinkLabel source = MakeLink(
                "Source", "Source", "https://github.com/arostad/IPbind", 24, 170, 382, 22);
            LinkLabel license = MakeLink(
                "Released under the MIT License", "MIT License",
                "https://github.com/arostad/IPbind/blob/main/LICENSE", 24, 198, 382, 22);

            checkButton = new Button();
            checkButton.Text = "Check for updates";
            checkButton.Location = P(120, 230);
            checkButton.Size = Z(190, 34);
            checkButton.Click += CheckUpdates;

            statusLabel = MakeLabel("", 8.5F, FontStyle.Regular, 24, 268, 382, 48);

            Controls.Add(title);
            Controls.Add(version);
            Controls.Add(tagline);
            Controls.Add(directed);
            Controls.Add(source);
            Controls.Add(license);
            Controls.Add(checkButton);
            Controls.Add(statusLabel);

            if (dark)
            {
                BackColor = Color.FromArgb(32, 32, 32);
                ForeColor = Color.FromArgb(230, 230, 230);
                foreach (Control control in Controls)
                {
                    control.ForeColor = ForeColor;
                    LinkLabel link = control as LinkLabel;
                    if (link != null)
                    {
                        link.LinkColor = Color.FromArgb(90, 160, 240);
                        link.ActiveLinkColor = Color.FromArgb(130, 185, 255);
                    }
                }
                checkButton.FlatStyle = FlatStyle.Flat;
                checkButton.BackColor = Color.FromArgb(50, 50, 50);
                checkButton.FlatAppearance.BorderColor = Color.FromArgb(80, 80, 80);
                statusLabel.ForeColor = Color.FromArgb(170, 170, 170);
            }
        }

        int U(int value) { return (int)Math.Round(value * sc); }
        Point P(int x, int y) { return new Point(U(x), U(y)); }
        Size Z(int width, int height) { return new Size(U(width), U(height)); }

        Label MakeLabel(
            string text, float size, FontStyle style, int x, int y, int width, int height)
        {
            Label label = new Label();
            label.Text = text;
            label.Font = new Font("Segoe UI", size, style);
            label.Location = P(x, y);
            label.Size = Z(width, height);
            label.TextAlign = ContentAlignment.MiddleCenter;
            return label;
        }

        LinkLabel MakeLink(
            string text, string linkedText, string url, int x, int y, int width, int height)
        {
            LinkLabel label = new LinkLabel();
            label.Text = text;
            label.Location = P(x, y);
            label.Size = Z(width, height);
            label.TextAlign = ContentAlignment.MiddleCenter;
            label.LinkColor = Color.FromArgb(25, 90, 160);
            int start = text.IndexOf(linkedText, StringComparison.Ordinal);
            label.LinkArea = new LinkArea(start, linkedText.Length);
            label.Links[0].LinkData = url;
            label.LinkClicked += delegate(object sender, LinkLabelLinkClickedEventArgs e)
            {
                try
                {
                    ProcessStartInfo info = new ProcessStartInfo((string)e.Link.LinkData);
                    info.UseShellExecute = true;
                    Process.Start(info);
                }
                catch { }
            };
            return label;
        }

        void CheckUpdates(object sender, EventArgs e)
        {
            checkButton.Enabled = false;
            statusLabel.Text = "Checking...";
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
                            statusLabel.Text = string.IsNullOrEmpty(info.Error)
                                ? "You're on the latest version (" + BuildInfo.Version + ")."
                                : info.Error;
                            return;
                        }

                        availableUpdate = info;
                        statusLabel.Text =
                            "Version " + info.RemoteVersion + " is available.";
                        checkButton.Text = "Update now";
                        checkButton.Click -= CheckUpdates;
                        checkButton.Click += UpdateNow;
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
            statusLabel.Text = "Downloading. The app will restart when it is ready.";
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
                            statusLabel.Text = UpdateChecker.DescribeError(ex);
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

    class MainForm : Form
    {
        static readonly string AppVersion = BuildInfo.Version;

        static readonly string[] DefaultIPs = new string[] {
            "10.255.255.98/24", "10.255.128.98/24", "192.168.0.98/24",
            "192.168.1.98/24",  "192.168.4.98/24",  "192.168.255.98/24"
        };

        static readonly Regex CidrRe =
            new Regex(@"^\s*(\d{1,3})\.(\d{1,3})\.(\d{1,3})\.(\d{1,3})\s*(/\s*(\d{1,2}))?\s*$");
        static readonly Regex VirtualRe =
            new Regex(@"virtual|hyper-?v|vmware|vbox|virtualbox|tap-|tunnel|vpn|loopback|wan miniport|bluetooth|pseudo|wi-?fi|wireless|cellular|wwan",
                      RegexOptions.IgnoreCase);

        readonly string AppDir =
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "IPbind");
        readonly string StoreFile;
        readonly string AdapterFile;

        float sc = 1f;  // DPI scale factor (deviceDpi / 96), applied to all pixel coordinates

        ComboBox combo;
        CheckBox chkAll;
        TextBox txtIPs;
        TextBox console;
        Label lblStatus;
        Button btnBind, btnDhcp;
        Panel updateBanner;
        Label lblUpdateBanner;
        Button btnUpdateNow, btnUpdateLater;
        Button btnClear;
        Button btnAbout;
        Label lblVersion;
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
            BuildUi();
            LoadAdapters();
            LoadList();
            Log("IPbind v" + AppVersion + " started. Select an interface, edit the list if needed, then Apply or revert.");
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
            if (console.InvokeRequired)
            {
                console.BeginInvoke((Action<string, string>)Log, msg, level);
                return;
            }
            string line = "[" + DateTime.Now.ToString("HH:mm:ss") + "] " + level + "  " + msg + "\r\n";
            console.AppendText(line);
            console.SelectionStart = console.TextLength;
            console.ScrollToCaret();
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
            File.WriteAllLines(StoreFile, lines);
            txtIPs.Lines = lines;
            if (!quiet) Log("Saved " + lines.Length + " address(es) to disk.", "OK");
        }

        void LoadList()
        {
            if (File.Exists(StoreFile))
            {
                List<string> lines = new List<string>();
                foreach (string l in File.ReadAllLines(StoreFile))
                {
                    string t = (l ?? "").Trim();
                    if (t.Length > 0) lines.Add(t);
                }
                if (lines.Count > 0) { txtIPs.Lines = lines.ToArray(); return; }
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

        void SaveAdapter(string name) { EnsureAppDir(); File.WriteAllText(AdapterFile, name); }
        string GetSavedAdapter() { return File.Exists(AdapterFile) ? File.ReadAllText(AdapterFile).Trim() : null; }

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
                combo.Items.Add(a.Name + "   |   " + a.Description + "   [" + a.Status + "]");

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
            try
            {
                using (Process p = Process.Start(psi))
                {
                    string o = p.StandardOutput.ReadToEnd();
                    string e = p.StandardError.ReadToEnd();
                    p.WaitForExit();
                    output = (o + e).Trim();
                    return p.ExitCode;
                }
            }
            catch (Exception ex) { output = ex.Message; return -1; }
        }

        int Netsh(string args, out string output) { return RunProc("netsh", args, out output); }

        void ShowCurrentConfig(string alias)
        {
            Log("Current IPv4 config for '" + alias + "':");
            string outp;
            Netsh("interface ipv4 show addresses name=\"" + alias + "\"", out outp);
            foreach (string raw in outp.Replace("\r", "").Split('\n'))
            {
                string t = raw.Trim();
                if (t.Length > 0) Log("  " + t);
            }
        }

        // ---------------- actions ----------------
        bool InvokeBind()
        {
            string alias = GetSelectedAlias();
            if (alias == null) { Log("No interface selected.", "WARN"); return false; }

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

            List<string[]> entries = new List<string[]>();
            foreach (string line in txtIPs.Lines)
            {
                if (string.IsNullOrEmpty((line ?? "").Trim())) continue;
                string ip; int pfx;
                if (TryCidr(line, out ip, out pfx)) entries.Add(new string[] { ip, pfx.ToString() });
                else Log("Skipping invalid entry: '" + line.Trim() + "'", "WARN");
            }
            if (entries.Count == 0) { Log("No valid addresses to apply.", "WARN"); return false; }

            Log("Applying " + entries.Count + " static IP(s) to '" + alias + "' (single batch)...");

            // All netsh commands run in ONE netsh process via a script file. Spawning a
            // separate netsh.exe per address is what made this slow: on a managed machine,
            // endpoint security inspects every child process as it launches. One process
            // = one inspection instead of eight.
            List<string> cmds = new List<string>();
            cmds.Add("interface ipv4 set interface \"" + alias + "\" dadtransmits=0");
            cmds.Add("interface ipv4 set address name=\"" + alias + "\" source=dhcp");
            bool first = true;
            foreach (string[] e in entries)
            {
                string ip = e[0];
                int pfx = int.Parse(e[1]);
                string mask = MaskFromPrefix(pfx);
                cmds.Add(first
                    ? "interface ipv4 set address name=\"" + alias + "\" static " + ip + " " + mask
                    : "interface ipv4 add address name=\"" + alias + "\" " + ip + " " + mask);
                first = false;
            }

            string outp = "";
            string script = Path.Combine(Path.GetTempPath(), "ipbind-" + Guid.NewGuid().ToString("N") + ".netsh");
            try
            {
                File.WriteAllText(script, string.Join("\r\n", cmds.ToArray()) + "\r\n");
                RunProc("netsh", "-f \"" + script + "\"", out outp);
            }
            catch (Exception ex) { outp = ex.Message; }
            finally { try { File.Delete(script); } catch { } }

            foreach (string[] e in entries) Log("  + " + e[0] + "/" + e[1], "OK");
            // netsh prints harmless notes (e.g. "DHCP is already enabled") that are not
            // failures, so don't judge success by its output. Log real-looking errors as
            // warnings, everything else as info, then verify against the actual interface.
            foreach (string raw in (outp ?? "").Replace("\r", "").Split('\n'))
            {
                string t = raw.Trim();
                if (t.Length == 0) continue;
                string low = t.ToLowerInvariant();
                bool looksBad = low.Contains("error") || low.Contains("failed")
                    || low.Contains("cannot") || low.Contains("incorrect") || low.Contains("not found");
                Log("  netsh: " + t, looksBad ? "WARN" : "INFO");
            }

            Log("Bind complete - verifying:", "OK");
            ShowCurrentConfig(alias);

            // Success = every intended address is actually present on the interface.
            List<string> want = new List<string>();
            foreach (string[] e in entries) want.Add(e[0]);
            int found = 0;
            try
            {
                foreach (NetworkInterface ni in NetworkInterface.GetAllNetworkInterfaces())
                {
                    if (ni.Name != alias) continue;
                    foreach (UnicastIPAddressInformation ua in ni.GetIPProperties().UnicastAddresses)
                        if (want.Contains(ua.Address.ToString())) found++;
                }
            }
            catch { }
            bool ok = found >= want.Count;
            if (!ok) Log("Only " + found + " of " + want.Count + " addresses are present on the interface.", "WARN");
            if (status != "Up") Log("Interface is not connected - addresses will activate once it comes up.", "WARN");
            return ok;
        }

        bool InvokeDhcp()
        {
            string alias = GetSelectedAlias();
            if (alias == null) { Log("No interface selected.", "WARN"); return false; }
            Log("Returning '" + alias + "' to DHCP...");
            string outp = "";

            // Same one-process batching as the bind path.
            List<string> cmds = new List<string>();
            cmds.Add("interface ipv4 set address name=\"" + alias + "\" source=dhcp");
            cmds.Add("interface ipv4 set dnsservers name=\"" + alias + "\" source=dhcp");
            string script = Path.Combine(Path.GetTempPath(), "ipbind-" + Guid.NewGuid().ToString("N") + ".netsh");
            try
            {
                File.WriteAllText(script, string.Join("\r\n", cmds.ToArray()) + "\r\n");
                RunProc("netsh", "-f \"" + script + "\"", out outp);
            }
            catch (Exception ex) { outp = ex.Message; }
            finally { try { File.Delete(script); } catch { } }

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
                            "Version " + info.RemoteVersion + " is available.";
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

        // ---------------- dark mode ----------------
        [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
        static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int val, int size);

        // Themes a control's scrollbars/borders. "DarkMode_Explorer" gives dark scrollbars.
        [System.Runtime.InteropServices.DllImport("uxtheme.dll", CharSet = System.Runtime.InteropServices.CharSet.Unicode)]
        static extern int SetWindowTheme(IntPtr hWnd, string subApp, string subId);

        // uxtheme ordinal 135 (Win10 1903+): 0=Default,1=AllowDark,2=ForceDark. Best-effort.
        [System.Runtime.InteropServices.DllImport("uxtheme.dll", EntryPoint = "#135")]
        static extern int SetPreferredAppMode(int mode);

        static bool IsDarkMode()
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

        // Tint the title bar. Must run after the handle exists, so it's called from
        // OnHandleCreated (not BuildUi - forcing the handle early breaks CenterScreen).
        void ApplyTitleBar()
        {
            try
            {
                int on = dark ? 1 : 0;
                // attribute 20 = DWMWA_USE_IMMERSIVE_DARK_MODE (Win10 2004+/Win11);
                // older builds used 19, so fall back to it.
                if (DwmSetWindowAttribute(this.Handle, 20, ref on, 4) != 0)
                    DwmSetWindowAttribute(this.Handle, 19, ref on, 4);
            }
            catch { }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            ApplyTitleBar();
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            shown = true;
            FitToWorkingArea(true);
            if (dark)
            {
                try { SetPreferredAppMode(1); } catch { }   // AllowDark (best-effort, Win10 1903+)
                DarkenScrollbars(this.Controls);
            }
            CheckForUpdatesOnLaunch();
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
            btnAbout.Location = new Point(console.Left, console.Bottom + U(10));
            lblVersion.Location =
                new Point(console.Right - U(160), console.Bottom + U(14));
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
        void DarkenScrollbars(Control.ControlCollection cc)
        {
            foreach (Control c in cc)
            {
                if (c is TextBox || c is ComboBox || c is ListBox)
                {
                    try { SetWindowTheme(c.Handle, "DarkMode_Explorer", null); } catch { }
                }
                if (c.Controls.Count > 0) DarkenScrollbars(c.Controls);
            }
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
                    c.BackColor = PanelCol;
                    c.ForeColor = FgCol;
                    ((ComboBox)c).FlatStyle = FlatStyle.Flat;
                }
                else if (c is LinkLabel)
                {
                    LinkLabel ll = (LinkLabel)c;
                    ll.ForeColor = SubCol;
                    ll.LinkColor = Color.FromArgb(90, 160, 240);
                }
                else if (c is CheckBox) { c.ForeColor = FgCol; }
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
            this.Text = "IPbind v" + AppVersion;
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
            lblTitle.Text = "IPbind";
            lblTitle.Font = UiFont(16F, FontStyle.Bold);
            lblTitle.Location = P(20, 10);
            lblTitle.Size = Z(520, 28);
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

            combo = new ComboBox();
            combo.Location = P(20, 94);
            combo.Size = Z(420, 24);
            combo.DropDownStyle = ComboBoxStyle.DropDownList;
            combo.SelectedIndexChanged += delegate
            {
                string a = GetSelectedAlias();
                if (a != null) { SaveAdapter(a); Log("Interface set to '" + a + "' (remembered)."); }
            };
            Controls.Add(combo);

            Button btnRefresh = MakeButton("Refresh", 448, 93, 92, 26);
            btnRefresh.Click += delegate { LoadAdapters(); };
            Controls.Add(btnRefresh);

            chkAll = new CheckBox();
            chkAll.Text = "Show all adapters (include virtual / cellular / VPN)";
            chkAll.Font = UiFont(8F, FontStyle.Regular);
            chkAll.Location = P(20, 122);
            chkAll.Size = Z(520, 20);
            chkAll.CheckedChanged += delegate { LoadAdapters(); };
            Controls.Add(chkAll);

            Label lblIPs = new Label();
            lblIPs.Text =
                "IP addresses to bind - one per line, CIDR (e.g. 192.168.1.98/24). No gateway or DNS:";
            lblIPs.Font = UiFont(8F, FontStyle.Regular);
            lblIPs.Location = P(20, 146);
            lblIPs.Size = Z(530, 22);
            lblIPs.TextAlign = ContentAlignment.MiddleLeft;
            Controls.Add(lblIPs);

            txtIPs = new TextBox();
            txtIPs.Multiline = true;
            txtIPs.ScrollBars = ScrollBars.Vertical;
            txtIPs.Location = P(20, 170);
            txtIPs.Size = Z(520, 196);
            txtIPs.Font = new Font("Consolas", 10F);
            Controls.Add(txtIPs);

            Button btnSaveList = MakeButton("Save List", 20, 372, 166, 28);
            btnSaveList.Click += delegate { SaveList(false); };
            Controls.Add(btnSaveList);

            Button btnRestore = MakeButton("Restore Defaults", 197, 372, 166, 28);
            btnRestore.Click += delegate { RestoreDefaults(); };
            Controls.Add(btnRestore);

            Button btnShow = MakeButton("Show Current", 374, 372, 166, 28);
            btnShow.Click += delegate
            {
                string a = GetSelectedAlias();
                if (a == null) { Log("No interface selected.", "WARN"); return; }
                ShowCurrentConfig(a);
            };
            Controls.Add(btnShow);

            btnBind = MakeButton("Apply Static IPs to\r\nLAN Interface", 20, 406, 255, 60);
            btnBind.Font = UiFont(12F, FontStyle.Bold);
            btnBind.BackColor = Color.FromArgb(46, 125, 50);
            btnBind.ForeColor = Color.White;
            btnBind.FlatStyle = FlatStyle.Flat;
            btnBind.FlatAppearance.BorderSize = 0;
            btnBind.Click += delegate
            {
                SetBusy("Applying static IPs to selected LAN interface...");
                bool ok = InvokeBind();
                EndBusy(ok, "Static IPs applied to selected LAN interface",
                            "Failed to apply static IPs to selected LAN interface");
            };
            Controls.Add(btnBind);

            btnDhcp = MakeButton("Return LAN Interface\r\nto DHCP", 285, 406, 255, 60);
            btnDhcp.Font = UiFont(12F, FontStyle.Bold);
            btnDhcp.BackColor = Color.FromArgb(25, 90, 160);
            btnDhcp.ForeColor = Color.White;
            btnDhcp.FlatStyle = FlatStyle.Flat;
            btnDhcp.FlatAppearance.BorderSize = 0;
            btnDhcp.Click += delegate
            {
                SetBusy("Returning selected LAN interface to DHCP...");
                bool ok = InvokeDhcp();
                EndBusy(ok, "Selected LAN interface returned to DHCP",
                            "Failed to return selected LAN interface to DHCP");
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
            btnClear.Click += delegate { console.Clear(); };
            Controls.Add(btnClear);

            console = new TextBox();
            console.Multiline = true;
            console.ScrollBars = ScrollBars.Vertical;
            console.ReadOnly = true;
            console.Location = P(560, 44);
            console.Size = Z(520, 466);
            console.Font = new Font("Consolas", 9F);
            console.BackColor = Color.FromArgb(18, 18, 18);
            console.ForeColor = Color.FromArgb(210, 210, 210);
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

            btnAbout = MakeButton("About", 560, 520, 96, 26);
            btnAbout.Font = UiFont(8F, FontStyle.Regular);
            btnAbout.Click += delegate
            {
                using (AboutForm about = new AboutForm(this, dark, sc))
                    about.ShowDialog(this);
            };
            Controls.Add(btnAbout);

            lblVersion = new Label();
            lblVersion.Text = "v" + AppVersion;
            lblVersion.Font = UiFont(8F, FontStyle.Regular);
            lblVersion.AutoSize = false;
            lblVersion.TextAlign = ContentAlignment.MiddleRight;
            lblVersion.Location = P(920, 524);
            lblVersion.Size = Z(160, 20);
            lblVersion.ForeColor = Color.FromArgb(120, 120, 120);
            Controls.Add(lblVersion);

            ApplyTheme();
            this.ResumeLayout(true);
        }
    }
}
