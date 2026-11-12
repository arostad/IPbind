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
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
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
// AssemblyInformationalVersion and BuildInfo (Version, Channel, AppName,
// ReleaseTag) are generated fresh on every compile into Version.cs by
// Build-Exe.ps1. -Channel beta builds the separate "IPbind Beta" app.

namespace IPbind
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            if (UpdateChecker.TryRunApplyUpdate(args)) return;
            UpdateChecker.BeginHelperCleanup(args);

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
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

            try
            {
                using (Process parent = Process.GetProcessById(parentPid))
                    parent.WaitForExit();
            }
            catch (ArgumentException)
            {
                // The parent already exited before the helper queried it.
            }

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

        public static bool TryRunApplyUpdate(string[] args)
        {
            if (args == null || args.Length == 0
                || !string.Equals(args[0], "--apply-update", StringComparison.Ordinal))
                return false;

            string target = args.Length > 2 ? args[2] : "";
            string pending = args.Length > 1 ? args[1] : "";
            string helperPath = Application.ExecutablePath;
            string errorFile = "";
            try
            {
                if (!string.IsNullOrEmpty(target))
                {
                    target = Path.GetFullPath(target);
                    string directory = Path.GetDirectoryName(target);
                    if (!string.IsNullOrEmpty(directory))
                        errorFile = Path.Combine(directory, "update-error.txt");
                }

                ApplyUpdate(args);
                if (!string.IsNullOrEmpty(errorFile))
                    try { File.Delete(errorFile); } catch { }
            }
            catch (Exception ex)
            {
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

        public static void DownloadAndRestart()
        {
            string exe = ResolveInstalledExe();
            string directory = Path.GetDirectoryName(exe);
            if (string.IsNullOrEmpty(directory))
                throw new InvalidOperationException("Could not resolve the install directory.");
            Directory.CreateDirectory(directory);

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
    }

    sealed class AboutForm : Form
    {
        readonly Button checkButton;
        readonly Label statusLabel;
        readonly Label matchLabel;
        readonly bool checkMatchOnShow;
        readonly float sc;
        readonly bool dark;
        UpdateInfo availableUpdate;
        readonly string updateCheckFile;
        readonly LinkLabel enableLaunchLink;
        readonly CheckBox launchCheckBox;
        readonly bool portableCopy;
        bool settingLaunchCheck;

        public AboutForm(Form owner, bool dark, float scale, string updateCheckFile)
        {
            this.updateCheckFile = updateCheckFile;
            this.dark = dark;
            sc = scale > 0f ? scale : 1f;
            Text = "About " + BuildInfo.AppName;
            Font = new Font("Segoe UI", 9F);
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            // Installed copies: a "turn on" link only while update-check.txt = manual.
            // Portable copies: a "Check for updates at launch" checkbox, always shown.
            // The form is taller only while one of the two is shown.
            portableCopy = !IsInstalledCopy();
            bool launchEnabled = ReadLaunchCheckEnabled();
            bool manualOnly = !portableCopy && !launchEnabled;
            bool showRow = portableCopy || manualOnly;
            ClientSize = Z(430, showRow ? 388 : 360);
            try { Icon = owner.Icon; } catch { }

            Label title = MakeLabel(BuildInfo.AppName, 18F, FontStyle.Bold, 24, 20, 382, 34);
            Label version = MakeLabel(
                "Version " + BuildInfo.Version + (BuildInfo.IsBeta ? " (beta)" : ""), 9F, FontStyle.Regular, 24, 58, 382, 22);
            // Beta only: small note under the version when this beta equals prod Latest.
            // Hidden until the background check confirms it; prod builds never show it.
            matchLabel = MakeLabel(
                "Matches the current release", 8F, FontStyle.Regular, 24, 79, 382, 15);
            matchLabel.Visible = false;
            Label tagline = MakeLabel(
                "One-click Static IP Binder for easily accessing\r\n" +
                "air-gapped equipment spread across multiple IP ranges",
                9F, FontStyle.Regular, 24, 94, 382, 58);

            LinkLabel directed = MakeLink(
                "App created by Andy Rostad",
                "Andy Rostad", "https://github.com/arostad", 24, 166, 382, 22);
            LinkLabel source = MakeLink(
                "Source", "Source", "https://github.com/arostad/IPbind", 24, 194, 382, 22);
            LinkLabel license = MakeLink(
                "Released under the MIT License", "MIT License",
                "https://github.com/arostad/IPbind/blob/main/LICENSE", 24, 222, 382, 22);

            checkButton = new Button();
            checkButton.Text = "Check for updates";
            checkButton.Location = P(120, 254);
            checkButton.Size = Z(190, 34);
            checkButton.Click += CheckUpdates;

            // Installed copies: only offered when the user chose manual updates
            // (installer task unticked); there is no way to turn checks off here.
            enableLaunchLink = new LinkLabel();
            enableLaunchLink.Text = "Turn on automatic update checks…";
            enableLaunchLink.Location = P(24, 296);
            enableLaunchLink.Size = Z(382, 22);
            enableLaunchLink.TextAlign = ContentAlignment.MiddleCenter;
            enableLaunchLink.LinkColor = Color.FromArgb(25, 90, 160);
            enableLaunchLink.Visible = manualOnly;
            enableLaunchLink.LinkClicked += EnableLaunchChecks;

            launchCheckBox = new CheckBox();
            launchCheckBox.Text = "Check for updates at launch";
            launchCheckBox.AutoSize = true;
            launchCheckBox.Checked = launchEnabled;
            launchCheckBox.Visible = portableCopy;
            launchCheckBox.CheckedChanged += LaunchCheckChanged;

            statusLabel = MakeLabel(
                "", 8.5F, FontStyle.Regular, 24, showRow ? 324 : 292, 382, 48);

            Controls.Add(title);
            Controls.Add(version);
            Controls.Add(matchLabel);
            Controls.Add(tagline);
            Controls.Add(directed);
            Controls.Add(source);
            Controls.Add(license);
            Controls.Add(checkButton);
            Controls.Add(enableLaunchLink);
            Controls.Add(launchCheckBox);
            Controls.Add(statusLabel);
            // Centre the auto-sized checkbox under the button (device pixels).
            launchCheckBox.Location = new Point(
                Math.Max(U(24), (ClientSize.Width - launchCheckBox.PreferredSize.Width) / 2), U(296));

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
                statusLabel.BackColor = BackColor;
                statusLabel.ForeColor = Color.FromArgb(170, 170, 170);
            }
            matchLabel.ForeColor = dark ? Color.FromArgb(150, 150, 150) : Color.FromArgb(110, 110, 110);

            // Respect "manual": with launch checks off, only the Check button goes online.
            checkMatchOnShow = BuildInfo.IsBeta && launchEnabled;
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            if (checkMatchOnShow) StartReleaseMatchCheck();
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
                        matchLabel.Visible = match;
                    });
                }
                catch { }
            });
            thread.IsBackground = true;
            thread.Start();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            WindowTheme.ApplyTitleBar(this, dark);
        }

        int U(int value) { return (int)Math.Round(value * sc); }
        Point P(int x, int y) { return new Point(U(x), U(y)); }
        Size Z(int width, int height) { return new Size(U(width), U(height)); }

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
                statusLabel.Text = "Could not save the update setting: " + ex.Message;
                return false;
            }
        }

        // Installed = Inno Setup's uninstaller (unins*.exe + unins*.dat) sits next to
        // the running exe. Every installed copy (prod or beta, any version) has it,
        // the in-app updater never touches it, and portable copies never do.
        static bool IsInstalledCopy()
        {
            try
            {
                string dir = Path.GetDirectoryName(Path.GetFullPath(Application.ExecutablePath));
                if (string.IsNullOrEmpty(dir)) return false;
                foreach (string dat in Directory.GetFiles(dir, "unins*.dat"))
                    if (File.Exists(Path.ChangeExtension(dat, ".exe"))) return true;
            }
            catch { }
            return false;
        }

        void SetLaunchCheckSilently(bool value)
        {
            settingLaunchCheck = true;
            try { launchCheckBox.Checked = value; }
            finally { settingLaunchCheck = false; }
        }

        // Portable checkbox: turning checks ON needs consent to the installer's
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
                statusLabel.Text = "Updates will now be checked at launch.";
            }
            else
            {
                if (!WriteLaunchCheckMode("manual"))
                {
                    SetLaunchCheckSilently(true);
                    return;
                }
                statusLabel.Text = "Automatic update checks are off. Use Check for updates any time.";
            }
        }

        // Installed link. Consent first: the same privacy text the installer shows. "I Agree" saves
        // "auto" and removes the link immediately; Cancel changes nothing.
        void EnableLaunchChecks(object sender, LinkLabelLinkClickedEventArgs e)
        {
            bool agreed;
            using (PrivacyConsentForm consent = new PrivacyConsentForm(dark, sc))
                agreed = consent.ShowDialog(this) == DialogResult.OK;
            if (!agreed || !WriteLaunchCheckMode("auto")) return;

            enableLaunchLink.Visible = false;
            statusLabel.Location = P(24, 292);
            ClientSize = Z(430, 360);
            statusLabel.Text = "Updates will now be checked at launch.";
        }

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
            Uri safeUrl;
            label.Links[0].LinkData = TryGetHttpsUrl(url, out safeUrl)
                ? safeUrl.AbsoluteUri
                : null;
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
            return label;
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

    // Shows the installer's privacy text (installer/privacy.txt, embedded at build
    // time as IPbind.privacy.txt so both are identical). "I Agree" returns OK.
    sealed class PrivacyConsentForm : Form
    {
        readonly float sc;
        readonly bool dark;

        public PrivacyConsentForm(bool dark, float scale)
        {
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
            return "The privacy notice could not be loaded. See the Privacy section at https://github.com/arostad/IPbind";
        }
    }

    // Startup reminder: static addresses stay on the adapter until Return to DHCP.
    // Shown at launch until the user clicks Continue with "Don't show this again"
    // ticked (MainForm then writes dhcp-reminder-dismissed.txt in the settings folder).
    // Continue (and Enter) stay inactive until "I understand" is ticked. Exit is
    // always available: Exit, Esc, the title-bar X and Alt+F4 all return Cancel,
    // and MainForm then closes IPbind without touching any adapter or saving the
    // opt-out. Laid out in code from the DPI scale like AboutForm.
    sealed class DhcpReminderForm : Form
    {
        [System.Runtime.InteropServices.DllImport("comctl32.dll")]
        static extern int LoadIconWithScaleDown(IntPtr hinst, IntPtr pszName, int cx, int cy, out IntPtr phico);
        [System.Runtime.InteropServices.DllImport("user32.dll")]
        static extern bool DestroyIcon(IntPtr hIcon);

        readonly float sc;
        readonly bool dark;
        readonly CheckBox understandBox;
        readonly CheckBox dontShowBox;
        readonly Button continueButton;
        readonly Button exitButton;
        readonly Bitmap warningBitmap;

        public bool DontShowAgain { get { return dontShowBox.Checked; } }

        public DhcpReminderForm(Form owner, bool dark, float scale)
        {
            this.dark = dark;
            sc = scale > 0f ? scale : 1f;
            Text = BuildInfo.AppName;
            Font = new Font("Segoe UI", 9F);
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            try { Icon = owner.Icon; } catch { }

            Color fore = dark ? Color.FromArgb(230, 230, 230) : SystemColors.ControlText;
            Color amber = dark ? Color.FromArgb(242, 179, 61) : Color.FromArgb(156, 92, 0);
            Font headingBold = new Font("Segoe UI", 12F, FontStyle.Bold);
            Font headingRegular = new Font("Segoe UI", 12F, FontStyle.Regular);
            Font bodyBold = new Font("Segoe UI", 9F, FontStyle.Bold);

            int width = U(600);
            int left = U(24);
            int textWidth = width - 2 * left;
            int y = U(20);

            // Heading row: warning icon, then bold amber "Important:" and the regular rest.
            int iconSize = U(24);
            warningBitmap = LoadWarningBitmap(iconSize);
            PictureBox icon = new PictureBox();
            icon.SizeMode = PictureBoxSizeMode.Zoom;
            icon.Size = new Size(iconSize, iconSize);
            icon.Image = warningBitmap;
            icon.TabStop = false;

            int headingLeft = left + iconSize + U(10);
            RunsLabel heading = new RunsLabel();
            heading.AddRun("Important:", headingBold, amber);
            heading.AddRun(" Return to DHCP when you\u2019re done", headingRegular, fore);
            heading.Location = new Point(headingLeft, 0);
            heading.FitWidth(width - headingLeft - left);
            int rowHeight = Math.Max(iconSize, heading.Height);
            icon.Location = new Point(left, y + (rowHeight - iconSize) / 2);
            heading.Top = y + (rowHeight - heading.Height) / 2;
            Controls.Add(icon);
            Controls.Add(heading);
            y += rowHeight + U(14);

            Label intro = WrapLabel(
                "IPbind gives your Ethernet adapter static IP addresses so you can reach field equipment. " +
                "They stay in place until you click Return to DHCP.", left, y, textWidth);
            y += intro.Height + U(12);

            Label leadIn = WrapLabel(
                "If you plug this adapter into an office, public, or home network while those addresses are still set:",
                left, y, textWidth);
            y += leadIn.Height + U(6);

            string[] bullets = new string[]
            {
                "One of your addresses may already belong to a device on that network, and you could knock it offline",
                "Your addresses may fall inside that network\u2019s DHCP pool and clash with addresses it hands out",
                "You likely won\u2019t have internet or network access",
                "You could trip network security alarms, which may get your laptop blocked by network admins"
            };
            int bulletIndent = U(20);
            foreach (string item in bullets)
            {
                Label dot = new Label();
                dot.Text = "\u2022";
                dot.UseMnemonic = false;
                dot.AutoSize = false;
                dot.Location = new Point(left + U(4), y);
                dot.Size = new Size(bulletIndent - U(4), U(20));
                Controls.Add(dot);
                Label text = WrapLabel(item, left + bulletIndent, y, textWidth - bulletIndent);
                y += text.Height + U(4);
            }
            y += U(10);

            RunsLabel closing = new RunsLabel();
            closing.AddRun("Click ", Font, fore);
            closing.AddRun("Return to DHCP", bodyBold, fore);
            closing.AddRun(" before plugging this adapter into a non-air-gapped network.", Font, fore);
            closing.Location = new Point(left, y);
            closing.FitWidth(textWidth);
            Controls.Add(closing);
            y += closing.Height + U(18);

            understandBox = new CheckBox();
            understandBox.Text = "I understand";
            understandBox.AutoSize = true;
            understandBox.Location = new Point(left, y);
            understandBox.CheckedChanged += delegate { continueButton.Enabled = understandBox.Checked; };
            Controls.Add(understandBox);
            y += understandBox.PreferredSize.Height + U(6);

            dontShowBox = new CheckBox();
            dontShowBox.Text = "Don\u2019t show this again";
            dontShowBox.AutoSize = true;
            dontShowBox.Location = new Point(left, y);
            Controls.Add(dontShowBox);
            int checkBottom = y + dontShowBox.PreferredSize.Height;

            continueButton = new Button();
            continueButton.Text = "Continue";
            continueButton.Size = Z(110, 32);
            continueButton.Location = new Point(width - left - continueButton.Width,
                checkBottom - continueButton.Height);
            continueButton.Enabled = false;
            continueButton.DialogResult = DialogResult.OK;
            Controls.Add(continueButton);
            AcceptButton = continueButton;

            // Way out for anyone who won't tick "I understand": closes IPbind.
            exitButton = new Button();
            exitButton.Text = "Exit";
            exitButton.Size = continueButton.Size;
            exitButton.Location = new Point(continueButton.Left - U(8) - exitButton.Width, continueButton.Top);
            exitButton.DialogResult = DialogResult.Cancel;
            Controls.Add(exitButton);
            CancelButton = exitButton;

            ClientSize = new Size(width, Math.Max(checkBottom, continueButton.Bottom) + U(20));

            if (dark)
            {
                BackColor = Color.FromArgb(32, 32, 32);
                ForeColor = fore;
                foreach (Control control in Controls)
                    if (!(control is RunsLabel)) control.ForeColor = fore;
                continueButton.FlatStyle = FlatStyle.Flat;
                continueButton.BackColor = Color.FromArgb(50, 50, 50);
                continueButton.FlatAppearance.BorderColor = Color.FromArgb(80, 80, 80);
                exitButton.FlatStyle = FlatStyle.Flat;
                exitButton.BackColor = Color.FromArgb(50, 50, 50);
                exitButton.FlatAppearance.BorderColor = Color.FromArgb(80, 80, 80);
            }

            Shown += delegate { understandBox.Focus(); };
        }

        Label WrapLabel(string text, int x, int y, int width)
        {
            Label label = new Label();
            label.Text = text;
            label.UseMnemonic = false;
            label.AutoSize = false;
            label.Font = Font;
            label.Padding = Padding.Empty;
            int height = label.GetPreferredSize(new Size(width, 0)).Height;
            label.Location = new Point(x, y);
            label.Size = new Size(width, height + U(2));
            Controls.Add(label);
            return label;
        }

        static Bitmap LoadWarningBitmap(int size)
        {
            // Crisp, DPI-sized stock warning icon (IDI_WARNING); falls back to SystemIcons.Warning.
            try
            {
                IntPtr handle;
                if (LoadIconWithScaleDown(IntPtr.Zero, new IntPtr(32515), size, size, out handle) == 0 &&
                    handle != IntPtr.Zero)
                {
                    try
                    {
                        using (Icon stock = Icon.FromHandle(handle))
                            return stock.ToBitmap();
                    }
                    finally { DestroyIcon(handle); }
                }
            }
            catch { }
            try
            {
                using (Icon sized = new Icon(SystemIcons.Warning, size, size))
                    return sized.ToBitmap();
            }
            catch { return SystemIcons.Warning.ToBitmap(); }
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            WindowTheme.ApplyTitleBar(this, dark);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing && warningBitmap != null) warningBitmap.Dispose();
            base.Dispose(disposing);
        }

        int U(int value) { return (int)Math.Round(value * sc); }
        Size Z(int width, int height) { return new Size(U(width), U(height)); }

        // A wrapped line made of differently styled runs (e.g. a bold phrase inside
        // a sentence). Words are measured and drawn with GDI TextRenderer, the same
        // renderer Labels use, so sizes follow the system DPI.
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
            public void FitWidth(int maxWidth)
            {
                int x = 0, y = 0, lineHeight = 0, widest = 0;
                // Screen DC: same system DPI the dialog paints at.
                using (Graphics g = Graphics.FromHwnd(IntPtr.Zero))
                {
                    foreach (Word word in words)
                    {
                        int fullWidth = Measure(g, word);
                        if (x > 0 && x + fullWidth > maxWidth)
                        {
                            y += lineHeight;
                            x = 0;
                            lineHeight = 0;
                            word.Text = word.Text.TrimStart(' ');
                            fullWidth = Measure(g, word);
                        }
                        word.Location = new Point(x, y);
                        x += fullWidth;
                        widest = Math.Max(widest, x);
                        lineHeight = Math.Max(lineHeight, word.Font.Height);
                    }
                }
                Size = new Size(Math.Min(maxWidth, widest + 2), y + lineHeight + 2);
            }

            static int Measure(Graphics g, Word word)
            {
                return TextRenderer.MeasureText(g, word.Text, word.Font, new Size(int.MaxValue, int.MaxValue), Flags).Width;
            }

            protected override void OnPaint(PaintEventArgs e)
            {
                base.OnPaint(e);
                foreach (Word word in words)
                    TextRenderer.DrawText(e.Graphics, word.Text, word.Font, word.Location, word.Color, Flags);
            }
        }
    }

    class MainForm : Form
    {
        static readonly string AppVersion = BuildInfo.Version;

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
        Label lblVersion;
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
            Log(BuildInfo.AppName + " v" + AppVersion + " started. Select an interface, edit the list if needed, then Apply or revert.");
            if (defaultsUpgraded)
                Log("Default address list updated to the latest defaults.", "INFO");
            if (BuildInfo.IsBeta)
                Log("Beta build: separate settings (%APPDATA%\\" + BuildInfo.AppName + "), updates only from the beta pre-release.");
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

        void SaveAdapter(string name) { EnsureAppDir(); File.WriteAllText(AdapterFile, name); }
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
                });
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
                    keepDhcpPref = File.ReadAllText(KeepDhcpFile).Trim() != "0";
            }
            catch { keepDhcpPref = true; }
            settingKeepDhcp = true;
            try { chkKeepDhcp.Checked = keepDhcpPref; }
            finally { settingKeepDhcp = false; }
        }

        void SaveKeepDhcpPref()
        {
            try { EnsureAppDir(); File.WriteAllText(KeepDhcpFile, keepDhcpPref ? "1" : "0"); }
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

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            WindowTheme.ApplyTitleBar(this, dark);
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
            // After the main window has painted: the Return-to-DHCP reminder.
            BeginInvoke((MethodInvoker)ShowDhcpReminderIfNeeded);
        }

        // Shown at every launch until the user clicks Continue with "Don't show this
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
                File.WriteAllText(DhcpReminderFile, "Return to DHCP reminder dismissed in v" + AppVersion + "\r\n");
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
            else if ((e.State & DrawItemState.Disabled) != 0)
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
            this.Text = BuildInfo.AppName + " v" + AppVersion;
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

            // Keep DHCP sits right-aligned above Refresh, mirroring Gateway support below it.
            chkKeepDhcp = new CheckBox();
            chkKeepDhcp.Text = "Keep DHCP";
            chkKeepDhcp.Font = UiFont(8F, FontStyle.Regular);
            chkKeepDhcp.Location = P(410, 74);
            chkKeepDhcp.Size = Z(130, 20);
            chkKeepDhcp.CheckAlign = ContentAlignment.MiddleRight;
            chkKeepDhcp.TextAlign = ContentAlignment.MiddleRight;
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
            btnRefresh.Click += delegate { LoadAdapters(); };
            Controls.Add(btnRefresh);

            chkAll = new CheckBox();
            chkAll.Text = "Show all adapters (include virtual / cellular / VPN)";
            chkAll.Font = UiFont(8F, FontStyle.Regular);
            chkAll.Location = P(20, 122);
            chkAll.Size = Z(370, 20);
            chkAll.CheckedChanged += delegate { LoadAdapters(); };
            Controls.Add(chkAll);

            chkGateway = new CheckBox();
            chkGateway.Text = "Gateway support";
            chkGateway.Font = UiFont(8F, FontStyle.Regular);
            chkGateway.Location = P(410, 122);
            chkGateway.Size = Z(130, 20);
            chkGateway.CheckAlign = ContentAlignment.MiddleRight;
            chkGateway.TextAlign = ContentAlignment.MiddleRight;
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

            // Gateway support is off by default; warn before someone turns it on.
            gatewayTip = new ToolTip();
            gatewayTip.AutoPopDelay = 10000;
            gatewayTip.InitialDelay = 400;
            gatewayTip.ReshowDelay = 200;
            gatewayTip.ShowAlways = true;
            gatewayTip.SetToolTip(chkGateway,
                "Only enable if you know what you're doing!\r\n" +
                "A gateway or DNS here can change how this PC reaches the internet.");

            lblIPs = new Label();
            lblIPs.Text = IpLabelPlain;
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
            txtIPs.TextChanged += delegate { RefreshGatewayChoices(); };
            Controls.Add(txtIPs);

            // Gateway rows (hidden until Gateway support is on). They sit in the bottom
            // 62px of the IP list's normal area, which the list gives up when shown.
            lblGwOn = MakeRowLabel("Use gateway on:", 20, 310);
            cboGwIp = new ComboBox();
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
                using (AboutForm about = new AboutForm(this, dark, sc, UpdateCheckFile))
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
