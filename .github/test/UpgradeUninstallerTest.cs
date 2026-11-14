using System;
using System.IO;
using IPbind;

namespace IPbindTest
{
    internal static class UpgradeUninstallerTest
    {
        static int fails;

        static void Expect(bool cond, string msg)
        {
            if (cond) return;
            fails++;
            Console.WriteLine("FAIL: " + msg);
        }

        static void ExpectEq(string got, string want, string msg)
        {
            if (got == want) return;
            fails++;
            Console.WriteLine("FAIL: " + msg + " got '" + got + "' want '" + want + "'");
        }

        static void ExpectThrows(string msg, Action action)
        {
            try
            {
                action();
                fails++;
                Console.WriteLine("FAIL: " + msg + " did not throw");
            }
            catch (ArgumentException)
            {
            }
            catch (Exception ex)
            {
                fails++;
                Console.WriteLine("FAIL: " + msg + " threw " + ex.GetType().Name);
            }
        }

        static int Main()
        {
            ExpectEq(
                UpdateChecker.InstallerFileName("2.3.142", false),
                "IPbind-Installer-v2.3.142.exe",
                "prod installer filename");
            ExpectEq(
                UpdateChecker.InstallerFileName("2.3.142", true),
                "IPbind-Installer-v2.3.142-beta.exe",
                "beta installer filename");
            ExpectThrows("empty version", delegate
            {
                UpdateChecker.InstallerFileName("", false);
            });
            ExpectThrows("invalid version", delegate
            {
                UpdateChecker.InstallerFileName("not-a-version", false);
            });

            string hash;
            Expect(
                UpdateChecker.TryParseChecksum(
                    "c239b0ea9ac0cf8d78d11a60e3657250fcfb09414ccc11edd06e0d211ab824de  IPbind.exe",
                    out hash)
                && hash == "C239B0EA9AC0CF8D78D11A60E3657250FCFB09414CCC11EDD06E0D211AB824DE",
                "parse exe checksum");
            Expect(
                UpdateChecker.TryParseChecksum(
                    "3009ab6cb4f571765d3dc24e53bde218c5724151007c60169c9caa8f8b02ef5b  IPbind-Installer-v2.3.128.exe\n",
                    out hash)
                && hash == "3009AB6CB4F571765D3DC24E53BDE218C5724151007C60169C9CAA8F8B02EF5B",
                "parse installer checksum with newline");
            Expect(
                !UpdateChecker.TryParseChecksum("not a checksum", out hash),
                "reject garbage checksum");
            Expect(
                !UpdateChecker.TryParseChecksum(
                    "c239b0ea9ac0cf8d78d11a60e3657250fcfb09414ccc11edd06e0d211ab824d  IPbind.exe",
                    out hash),
                "reject short hash");

            Expect(
                UpdateChecker.UninstallStringIsCurrent(
                    "\"C:\\Users\\a\\IPbind\\unins000.exe\" /SILENT /IPBINDPROMPT"),
                "current UninstallString");
            Expect(
                UpdateChecker.UninstallStringIsCurrent(
                    "\"C:\\Users\\a\\IPbind\\unins000.exe\" /silent /ipbindprompt"),
                "current UninstallString is case-insensitive");
            Expect(
                !UpdateChecker.UninstallStringIsCurrent(
                    "\"C:\\Users\\a\\IPbind\\unins000.exe\""),
                "old UninstallString is not current");
            Expect(
                !UpdateChecker.UninstallStringIsCurrent(""),
                "empty UninstallString is not current");
            Expect(
                !UpdateChecker.UninstallStringIsCurrent(null),
                "null UninstallString is not current");

            string temp = Path.Combine(Path.GetTempPath(), "ipbind-unins-unit-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(temp);
            try
            {
                Expect(!UpdateChecker.IsInstallerManaged(temp), "dir without unins000 is portable");
                Expect(!UpdateChecker.IsInstallerManaged(""), "empty dir is not managed");
                Expect(!UpdateChecker.IsInstallerManaged(null), "null dir is not managed");
                File.WriteAllText(Path.Combine(temp, "unins000.exe"), "placeholder");
                Expect(UpdateChecker.IsInstallerManaged(temp), "dir with unins000 is installer-managed");
                Expect(
                    UpdateChecker.UninstallerNeedsRefresh(temp),
                    "managed dir with no matching uninstall key needs refresh");
            }
            finally
            {
                try { Directory.Delete(temp, true); } catch { }
            }

            if (fails > 0)
            {
                Console.WriteLine(fails + " upgrade-uninstaller unit test(s) failed");
                return 1;
            }
            Console.WriteLine("Upgrade-uninstaller unit tests passed");
            return 0;
        }
    }
}
