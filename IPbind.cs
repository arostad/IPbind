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
using System.Drawing.Drawing2D;
using System.Runtime.InteropServices;
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
// AssemblyInformationalVersion and BuildInfo (Version, Channel, DisplayVersion,
// AppName, ReleaseTag) are generated fresh on every compile into Version.cs by
// Build-Exe.ps1. -Channel beta builds the separate "IPbind Beta" app.
// DisplayVersion is Version plus a trailing "b" in Beta (title bar, About,
// footer, log). Parsed versions, version.txt, and installer DisplayVersion
// stay the numeric Version. Prod display is unchanged.

namespace IPbind
{
    static class Program
    {
        [STAThread]
        static void Main(string[] args)
        {
            if (UpdateChecker.TryRunApplyUpdate(args)) return;
            UpdateChecker.BeginHelperCleanup(args);
            InstalledVersionSync.Run();

            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }

    // The in-app updater swaps only the exe, so the installer's Apps & Features entry
    // (which winget reads) would keep the old version. On launch, an installed copy
    // raises DisplayVersion to the running version. Portable copies, other users'
    // installs and any error are skipped silently.
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
                    System.Version running, listed;
                    if (!System.Version.TryParse(BuildInfo.Version, out running)) return;
                    string current = key.GetValue("DisplayVersion") as string;
                    if (current != null && System.Version.TryParse(current, out listed)
                        && listed >= running)
                        return;
                    key.SetValue("DisplayVersion", BuildInfo.Version,
                        Microsoft.Win32.RegistryValueKind.String);
                }
            }
            catch { }
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
        const int WM_DPICHANGED = 0x02E0;
        static readonly IntPtr DpiAwarenessPerMonitorV2 = new IntPtr(-4);

        [DllImport("user32.dll")]
        static extern IntPtr SetThreadDpiAwarenessContext(IntPtr value);

        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int Left, Top, Right, Bottom; }

        float sc;
        readonly bool dark;
        readonly string updateCheckFile;
        readonly bool portableCopy;
        bool showEnableLaunchLink;
        readonly bool checkMatchOnShow;
        readonly Color bodyBack, footerBack, footerRule, fore, muted, quiet, linkColor, linkActive;
        readonly Color secondaryBack, secondaryBorder, secondaryFore, primaryBack, primaryFore;

        readonly PictureBox iconBox;
        readonly Label nameLabel;
        readonly Label versionLabel;
        readonly LinkLabel website;
        readonly Label matchLabel;
        readonly Label tagline;
        readonly LinkLabel creditLink;
        readonly LinkLabel sourceLink;
        readonly LinkLabel licenseLink;
        readonly CheckBox launchCheckBox;
        readonly LinkLabel enableLaunchLink;
        readonly Label statusLabel;
        readonly Label privacyLabel;
        readonly Panel footer;
        readonly Button checkButton;
        readonly Button closeButton;
        Bitmap iconBitmap;
        readonly List<Font> fonts = new List<Font>();
        Font fontName, fontVersion, fontBody, fontPrivacy, fontStatus, fontButton, fontButtonBold;
        bool layingOut;
        bool settingLaunchCheck;
        UpdateInfo availableUpdate;

        public AboutForm(Form owner, bool dark, float scale, string updateCheckFile)
        {
            this.updateCheckFile = updateCheckFile;
            this.dark = dark;
            sc = scale > 0f ? scale : 1f;
            Text = "About " + BuildInfo.AppName;
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            MinimizeBox = false;
            ShowInTaskbar = false;
            StartPosition = FormStartPosition.CenterParent;
            try { Icon = owner.Icon; } catch { }

            portableCopy = !IsInstalledCopy();
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
            sourceLink = MakeLink("GitHub Project Page", "GitHub Project Page", "https://github.com/arostad/IPbind");
            licenseLink = MakeLink("Released under the MIT License", "MIT License",
                "https://github.com/arostad/IPbind/blob/main/LICENSE");

            launchCheckBox = new CheckBox();
            launchCheckBox.Text = "Check for updates at launch";
            launchCheckBox.UseMnemonic = false;
            launchCheckBox.AutoSize = false;
            launchCheckBox.Checked = launchEnabled;
            launchCheckBox.Visible = portableCopy;
            launchCheckBox.CheckedChanged += LaunchCheckChanged;

            enableLaunchLink = MakeLink("Turn on automatic update checks\u2026",
                "Turn on automatic update checks\u2026", null);
            // The installed "turn on" link runs the privacy-consent flow, not a URL.
            enableLaunchLink.Links[0].LinkData = "launch-checks";
            showEnableLaunchLink = !portableCopy && !launchEnabled;
            enableLaunchLink.Visible = showEnableLaunchLink;
            enableLaunchLink.LinkClicked += EnableLaunchChecks;

            statusLabel = MakeLabel();
            statusLabel.Text = "";
            privacyLabel = MakeLabel();
            privacyLabel.Text = "Checking for updates sends one anonymous request.\r\n"
                + "Only the request\u2019s country code is counted; no identifiers or cookies.";

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
            Controls.Add(matchLabel);
            Controls.Add(tagline);
            Controls.Add(creditLink);
            Controls.Add(sourceLink);
            Controls.Add(licenseLink);
            Controls.Add(launchCheckBox);
            Controls.Add(enableLaunchLink);
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
            Font font = new Font("Segoe UI", px * sc, style, GraphicsUnit.Pixel);
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

        int U(int value) { return (int)Math.Round(value * sc); }

        int LinkWidth(string text, Font font)
        {
            // NoPadding is tight; LinkLabel wraps (and our single-line height
            // then clips the second line) if the width is even 1 CSS px short.
            return MeasureLine(text, font).Width + U(16);
        }

        void LayoutContent()
        {
            if (layingOut) return;
            layingOut = true;
            try
            {
                List<Font> previous = new List<Font>(fonts);
                fonts.Clear();
                fontName = MakeFont(22f, FontStyle.Bold);
                fontVersion = MakeFont(14f, FontStyle.Bold);
                fontBody = MakeFont(14f, FontStyle.Regular);
                fontPrivacy = MakeFont(12f, FontStyle.Regular);
                fontStatus = MakeFont(13f, FontStyle.Regular);
                fontButton = MakeFont(14f, FontStyle.Regular);
                fontButtonBold = MakeFont(14f, FontStyle.Bold);
                Font = MakeFont(9f, FontStyle.Regular);

                nameLabel.Font = fontName; nameLabel.ForeColor = fore;
                versionLabel.Font = fontVersion; versionLabel.ForeColor = fore;
                website.Font = fontBody;
                StyleLink(website);
                matchLabel.Font = MakeFont(12f, FontStyle.Regular);
                matchLabel.ForeColor = quiet;
                tagline.Font = fontBody; tagline.ForeColor = muted;
                StyleLink(creditLink); creditLink.Font = fontBody; creditLink.ForeColor = fore;
                StyleLink(sourceLink); sourceLink.Font = fontBody; sourceLink.ForeColor = fore;
                StyleLink(licenseLink); licenseLink.Font = fontBody; licenseLink.ForeColor = fore;
                launchCheckBox.Font = fontBody;
                launchCheckBox.ForeColor = fore;
                launchCheckBox.BackColor = bodyBack;
                StyleLink(enableLaunchLink); enableLaunchLink.Font = fontBody;
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
                int padBot = U(20);
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
                if (matchLabel.Visible)
                    stackH += U(2) + MeasureLine(matchLabel.Text, matchLabel.Font).Height;
                int heroH = Math.Max(iconPx, stackH);
                iconBox.Top = y + (heroH - iconPx) / 2;
                int stackY = y + (heroH - stackH) / 2;
                nameLabel.Location = new Point(stackX, stackY);
                nameLabel.Size = new Size(stackW, nameSize.Height);
                versionLabel.Location = new Point(stackX, nameLabel.Bottom + U(2));
                versionLabel.Size = new Size(stackW, verSize.Height);
                website.Location = new Point(stackX, versionLabel.Bottom + U(2));
                website.Size = new Size(Math.Min(stackW, LinkWidth(website.Text, fontBody)), siteSize.Height);
                int afterHero = Math.Max(iconBox.Bottom, website.Bottom);
                if (matchLabel.Visible)
                {
                    Size matchSize = MeasureLine(matchLabel.Text, matchLabel.Font);
                    matchLabel.Location = new Point(stackX, website.Bottom + U(2));
                    matchLabel.Size = new Size(stackW, matchSize.Height);
                    afterHero = Math.Max(iconBox.Bottom, matchLabel.Bottom);
                }
                y = afterHero + U(16);

                Size tagSize = MeasureWrap(tagline.Text, fontBody, inner);
                tagline.Location = new Point(padX, y);
                tagline.Size = new Size(inner, tagSize.Height);
                y = tagline.Bottom + U(18);

                // Credit and GitHub share a wrapping row; the license always starts the next line.
                int gap = U(28);
                int x = padX;
                int rowH = 0;
                int rowTop = y;
                LinkLabel[] pair = new LinkLabel[] { creditLink, sourceLink };
                foreach (LinkLabel link in pair)
                {
                    Size need = MeasureLine(link.Text, fontBody);
                    int w = LinkWidth(link.Text, fontBody);
                    if (x > padX && x + w > padX + inner)
                    {
                        y = rowTop + rowH + U(8);
                        x = padX;
                        rowTop = y;
                        rowH = 0;
                    }
                    link.Location = new Point(x, rowTop);
                    link.Size = new Size(w, need.Height);
                    x += w + gap;
                    rowH = Math.Max(rowH, need.Height);
                }
                y = rowTop + rowH + U(8);
                Size licNeed = MeasureLine(licenseLink.Text, fontBody);
                licenseLink.Location = new Point(padX, y);
                licenseLink.Size = new Size(Math.Min(inner, LinkWidth(licenseLink.Text, fontBody)), licNeed.Height);
                y = licenseLink.Bottom;

                // Control.Visible is false while the form itself is hidden, so layout
                // from our own flags rather than the getter.
                if (portableCopy)
                {
                    y += U(16);
                    Size boxNeed = MeasureWrap(launchCheckBox.Text, fontBody, inner - U(24));
                    int boxH = Math.Max(U(20), boxNeed.Height);
                    launchCheckBox.Location = new Point(padX, y);
                    launchCheckBox.Size = new Size(inner, boxH);
                    launchCheckBox.CheckAlign = ContentAlignment.MiddleLeft;
                    launchCheckBox.TextAlign = ContentAlignment.MiddleLeft;
                    launchCheckBox.Visible = true;
                    y = launchCheckBox.Bottom;
                }
                else
                    launchCheckBox.Visible = false;
                if (showEnableLaunchLink)
                {
                    y += U(16);
                    Size linkNeed = MeasureLine(enableLaunchLink.Text, fontBody);
                    enableLaunchLink.Location = new Point(padX, y);
                    enableLaunchLink.Size = new Size(Math.Min(inner, LinkWidth(enableLaunchLink.Text, fontBody)), linkNeed.Height);
                    enableLaunchLink.Visible = true;
                    y = enableLaunchLink.Bottom;
                }
                else
                    enableLaunchLink.Visible = false;
                if (!string.IsNullOrEmpty(statusLabel.Text))
                {
                    y += U(16);
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

                y += U(18);
                Size priv = MeasureWrap(privacyLabel.Text, fontPrivacy, inner);
                privacyLabel.Location = new Point(padX, y);
                privacyLabel.Size = new Size(inner, priv.Height);
                y = privacyLabel.Bottom + padBot;

                int footerPadX = U(28);
                int footerPadTop = U(16);
                int footerPadBot = U(18);
                int btnH = U(32);
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

                ClientSize = new Size(width, y + footerH);

                foreach (Font font in previous) font.Dispose();
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

        void FitOnScreen()
        {
            if (!IsHandleCreated) return;
            Rectangle work = Screen.FromControl(this).WorkingArea;
            int x = Left, y = Top, w = Width, h = Height;
            if (x + w > work.Right) x = work.Right - w;
            if (y + h > work.Bottom) y = work.Bottom - h;
            if (x < work.Left) x = work.Left;
            if (y < work.Top) y = work.Top;
            Location = new Point(x, y);
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

        protected override void CreateHandle()
        {
            IntPtr previous = IntPtr.Zero;
            bool changed = false;
            try
            {
                previous = SetThreadDpiAwarenessContext(DpiAwarenessPerMonitorV2);
                changed = previous != IntPtr.Zero && previous != DpiAwarenessPerMonitorV2;
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

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            WindowTheme.ApplyTitleBar(this, dark);
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_DPICHANGED)
            {
                int dpi = (int)(m.WParam.ToInt64() & 0xFFFF);
                if (dpi > 0)
                {
                    float newSc = dpi / 96f;
                    if (Math.Abs(newSc - sc) > 0.001f)
                    {
                        sc = newSc;
                        LayoutContent();
                    }
                }
                RECT suggested = (RECT)Marshal.PtrToStructure(m.LParam, typeof(RECT));
                int w = Width, h = Height;
                int x = suggested.Left, y = suggested.Top;
                Rectangle work = Screen.FromPoint(new Point(x, y)).WorkingArea;
                if (x + w > work.Right) x = work.Right - w;
                if (y + h > work.Bottom) y = work.Bottom - h;
                if (x < work.Left) x = work.Left;
                if (y < work.Top) y = work.Top;
                Bounds = new Rectangle(x, y, w, h);
                return;
            }
            base.WndProc(ref m);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                foreach (Font font in fonts) font.Dispose();
                fonts.Clear();
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

        // Installed link. Consent first: the same privacy text the installer shows. "I Agree" saves
        // "auto" and removes the link immediately; Cancel changes nothing.
        void EnableLaunchChecks(object sender, LinkLabelLinkClickedEventArgs e)
        {
            bool agreed;
            using (PrivacyConsentForm consent = new PrivacyConsentForm(dark, sc))
                agreed = consent.ShowDialog(this) == DialogResult.OK;
            if (!agreed || !WriteLaunchCheckMode("auto")) return;

            showEnableLaunchLink = false;
            enableLaunchLink.Visible = false;
            SetStatus("Updates will now be checked at launch.");
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
                        SetStatus("Version " + info.RemoteVersion + " is available.");
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
    sealed class DhcpReminderForm : Form
    {
        const int WM_DPICHANGED = 0x02E0;
        static readonly IntPtr DpiAwarenessPerMonitorV2 = new IntPtr(-4);

        [DllImport("user32.dll")]
        static extern IntPtr SetThreadDpiAwarenessContext(IntPtr value);
        [DllImport("user32.dll")]
        static extern int GetDpiForWindow(IntPtr hwnd);

        [StructLayout(LayoutKind.Sequential)]
        struct RECT { public int Left, Top, Right, Bottom; }

        float sc;
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
        readonly Button continueButton;
        readonly Button exitButton;

        readonly List<Font> fonts = new List<Font>();
        Font fontEyebrow, fontTitle, fontIntro, fontIntroBold, fontBody, fontBodyBold, fontSmall, fontButton, fontReplica;
        bool layingOut;

        public bool DontShowAgain { get { return dontShowBox.Checked; } }

        public DhcpReminderForm(Form owner, bool dark, float scale)
        {
            this.dark = dark;
            sc = scale > 0f ? scale : 1f;
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
                "Your laptop will likely not have internet",
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
            understandBox = new CheckBox();
            understandBox.UseMnemonic = false;
            understandBox.AutoSize = false;
            understandBox.CheckedChanged += delegate { ApplyContinueState(); };
            dontShowBox = new CheckBox();
            dontShowBox.UseMnemonic = false;
            dontShowBox.AutoSize = false;
            dontShowBox.Text = "Don\u2019t show this again";
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
            footer.Controls.Add(dontShowBox);
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
            Font font = new Font("Segoe UI", px * sc, style, GraphicsUnit.Pixel);
            fonts.Add(font);
            return font;
        }

        void DisposeFonts()
        {
            foreach (Font font in fonts) font.Dispose();
            fonts.Clear();
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

        // TextRenderer without NoPadding includes GDI padding and descent.
        // WinForms CheckBox then insets that text, so MeasureCheckHeight adds
        // 6 CSS pixels of inset (scaled) or the descenders are still clipped.
        Size MeasureCheck(string text, Font font, int maxWidth)
        {
            using (Graphics g = Graphics.FromHwnd(IntPtr.Zero))
            {
                Size size = TextRenderer.MeasureText(g, text, font, new Size(maxWidth, int.MaxValue),
                    TextFormatFlags.WordBreak | TextFormatFlags.TextBoxControl | TextFormatFlags.NoPrefix);
                if (size.Width < 1) size.Width = maxWidth;
                if (size.Height < font.Height) size.Height = font.Height;
                return size;
            }
        }

        int MeasureCheckHeight(string text, Font font, int maxWidth, int minHeight)
        {
            Size size = MeasureCheck(text, font, maxWidth);
            return Math.Max(minHeight, size.Height + U(6));
        }

        int MaxClientHeight()
        {
            Rectangle work;
            int chromeW, chromeH;
            if (IsHandleCreated)
            {
                work = Screen.FromControl(this).WorkingArea;
                chromeW = Math.Max(0, Width - ClientSize.Width);
                chromeH = Math.Max(0, Height - ClientSize.Height);
            }
            else
            {
                work = Screen.PrimaryScreen.WorkingArea;
                chromeW = SystemInformation.FixedFrameBorderSize.Width * 2;
                chromeH = SystemInformation.CaptionHeight + SystemInformation.FixedFrameBorderSize.Height * 2;
            }
            // Also cap to a 1080p working area (1920x1040) so 200% still fits
            // when the current screen is taller (CI runners often have no taskbar).
            int workH = Math.Min(work.Height, 1040);
            return Math.Max(U(360), workH - chromeH - 16);
        }

        int U(int value) { return (int)Math.Round(value * sc); }

        void LayoutContent()
        {
            if (layingOut) return;
            layingOut = true;
            try
            {
                List<Font> previousFonts = new List<Font>(fonts);
                fonts.Clear();
                fontEyebrow = MakeFont(12f, FontStyle.Bold);
                fontTitle = MakeFont(22f, FontStyle.Bold);
                fontIntro = MakeFont(15f, FontStyle.Regular);
                fontIntroBold = MakeFont(15f, FontStyle.Bold);
                fontBody = MakeFont(15f, FontStyle.Bold);
                fontBodyBold = MakeFont(15f, FontStyle.Bold);
                fontSmall = MakeFont(13f, FontStyle.Regular);
                fontButton = MakeFont(14f, FontStyle.Bold);
                fontReplica = MakeFont(12f, FontStyle.Bold);
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
                understandBox.Font = MakeFont(14f, FontStyle.Regular);
                understandBox.ForeColor = fore;
                understandBox.BackColor = footerBack;
                understandBox.Text = "I\u2019ll click " + UiCopy.DhcpButton + " before I plug into a live network";
                dontShowBox.Font = MakeFont(14f, FontStyle.Regular);
                dontShowBox.ForeColor = muted;
                dontShowBox.BackColor = footerBack;
                continueButton.Font = fontButton;
                exitButton.Font = fontButton;
                exitButton.BackColor = secondaryBack;
                exitButton.ForeColor = secondaryFore;
                exitButton.FlatAppearance.BorderColor = secondaryBorder;
                ApplyContinueState();

                intro.Clear();
                intro.AddRun("The addresses you bind using this app stay on your selected interface until you click the ", fontIntro, introColor);
                intro.AddRun(UiCopy.DhcpButton, fontIntroBold, fore);
                intro.AddRun(" button.", fontIntro, introColor);

                replica.SetCaption(UiCopy.DhcpButton, fontReplica, UiCopy.DhcpButtonBack, Color.White, sc);

                float tighten = 1f;
                for (int attempt = 0; attempt < 8; attempt++)
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
                    int width = U(640);
                    int maxW = width;
                    if (IsHandleCreated)
                    {
                        Rectangle work = Screen.FromControl(this).WorkingArea;
                        int chromeW = Math.Max(0, Width - ClientSize.Width);
                        maxW = Math.Max(U(420), work.Width - chromeW - 4);
                    }
                    else
                    {
                        int chromeW = SystemInformation.FixedFrameBorderSize.Width * 2;
                        maxW = Math.Max(U(420), Screen.PrimaryScreen.WorkingArea.Width - chromeW - 4);
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
                    int ackH = MeasureCheckHeight(understandBox.Text, understandBox.Font, ackTextW, chkBox);
                    int againH = MeasureCheckHeight(dontShowBox.Text, dontShowBox.Font, ackTextW, chkBox);
                    int footerH = footerPadTop + ackH + U(8) + againH + footerGap + btnH + footerPadBot;
                    footer.Location = new Point(0, bodyBottom);
                    footer.Size = new Size(width, footerH);
                    understandBox.Location = new Point(footerPadX, footerPadTop);
                    understandBox.Size = new Size(width - 2 * footerPadX, ackH);
                    understandBox.CheckAlign = ContentAlignment.TopLeft;
                    understandBox.TextAlign = ContentAlignment.MiddleLeft;
                    dontShowBox.Location = new Point(footerPadX, understandBox.Bottom + U(8));
                    dontShowBox.Size = new Size(width - 2 * footerPadX, againH);
                    dontShowBox.CheckAlign = ContentAlignment.TopLeft;
                    dontShowBox.TextAlign = ContentAlignment.MiddleLeft;
                    int btnY = dontShowBox.Bottom + footerGap;
                    exitButton.Size = new Size(btnW, btnH);
                    continueButton.Size = new Size(btnW, btnH);
                    exitButton.Location = new Point(width - footerPadX - btnW, btnY);
                    continueButton.Location = new Point(exitButton.Left - U(10) - btnW, btnY);

                    int clientH = bodyBottom + footerH;
                    int maxH = MaxClientHeight();
                    if (clientH <= maxH || tighten <= 0.01f)
                    {
                        ClientSize = new Size(width, clientH);
                        break;
                    }
                    tighten = Math.Max(0f, tighten - 0.2f);
                }
                foreach (Font previous in previousFonts) previous.Dispose();
            }
            finally { layingOut = false; }
        }

        int LerpPad(int full, int min, float tighten)
        {
            return U((int)Math.Round(min + (full - min) * tighten));
        }

        void FitOnScreen()
        {
            // Reposition only. Relaying out here would dispose fonts while the
            // dialog is visible and can throw GDI+ "Parameter is not valid".
            if (!IsHandleCreated) return;
            Rectangle work = Screen.FromControl(this).WorkingArea;
            int x = Left, y = Top, w = Width, h = Height;
            if (x + w > work.Right) x = work.Right - w;
            if (y + h > work.Bottom) y = work.Bottom - h;
            if (x < work.Left) x = work.Left;
            if (y < work.Top) y = work.Top;
            Location = new Point(x, y);
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

        protected override void CreateHandle()
        {
            IntPtr previous = IntPtr.Zero;
            bool changed = false;
            try
            {
                previous = SetThreadDpiAwarenessContext(DpiAwarenessPerMonitorV2);
                changed = previous != IntPtr.Zero && previous != DpiAwarenessPerMonitorV2;
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

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            WindowTheme.ApplyTitleBar(this, dark);
            // Scale stays at the value from the constructor (MainForm's system
            // DPI, or a test scale). A later move to another monitor is handled
            // by WM_DPICHANGED; do not overwrite a simulated scale here.
        }

        protected override void WndProc(ref Message m)
        {
            if (m.Msg == WM_DPICHANGED)
            {
                int dpi = (int)(m.WParam.ToInt64() & 0xFFFF);
                if (dpi > 0)
                {
                    float newSc = dpi / 96f;
                    if (Math.Abs(newSc - sc) > 0.001f)
                    {
                        sc = newSc;
                        LayoutContent();
                    }
                }
                RECT suggested = (RECT)Marshal.PtrToStructure(m.LParam, typeof(RECT));
                int w = Width, h = Height;
                int x = suggested.Left, y = suggested.Top;
                Rectangle work = Screen.FromPoint(new Point(x, y)).WorkingArea;
                if (x + w > work.Right) x = work.Right - w;
                if (y + h > work.Bottom) y = work.Bottom - h;
                if (x < work.Left) x = work.Left;
                if (y < work.Top) y = work.Top;
                Bounds = new Rectangle(x, y, w, h);
                return;
            }
            base.WndProc(ref m);
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) DisposeFonts();
            base.Dispose(disposing);
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
                int padX = (int)Math.Round(18 * sc);
                int padY = (int)Math.Round(10 * sc);
                Size = new Size(need.Width + 2 * padX, Math.Max(need.Height + 2 * padY, (int)Math.Round(36 * sc)));
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
            public void FitWidth(int maxWidth)
            {
                int x = 0, y = 0, lineHeight = 0, widest = 0;
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
                Size = new Size(Math.Min(maxWidth, Math.Max(widest + 2, 1)), y + lineHeight + 2);
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
            Log(BuildInfo.AppName + " v" + BuildInfo.DisplayVersion + " started. Select an interface, edit the list if needed, then Apply or revert.");
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
            gatewayTip.SetToolTip(chkGateway, "Only enable if you know what you're doing!");

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

            btnBind = MakeButton(UiCopy.ApplyButton, 20, 406, 255, 60);
            btnBind.Font = UiFont(12F, FontStyle.Bold);
            btnBind.BackColor = UiCopy.ApplyButtonBack;
            btnBind.ForeColor = Color.White;
            btnBind.FlatStyle = FlatStyle.Flat;
            btnBind.FlatAppearance.BorderSize = 0;
            btnBind.Click += delegate
            {
                SetBusy("Applying static IPs...");
                bool ok = InvokeBind();
                EndBusy(ok, "Static IPs applied",
                            "Failed to apply static IPs");
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
                SetBusy("Returning to DHCP...");
                bool ok = InvokeDhcp();
                EndBusy(ok, "Returned to DHCP",
                            "Failed to return to DHCP");
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
            lblVersion.Text = "v" + BuildInfo.DisplayVersion;
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
