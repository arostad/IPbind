// Console tests for WPF Beta feature helpers (never shipped in the app).
using System;
using System.IO;

namespace IPbindTest
{
    static class WpfFeatureTest
    {
        static int fails;

        static void Expect(bool cond, string name)
        {
            Console.WriteLine((cond ? "PASS " : "FAIL ") + name);
            if (!cond) fails++;
        }

        static void ExpectEq(string got, string want, string name)
        {
            bool ok = got == want;
            Console.WriteLine((ok ? "PASS " : "FAIL ") + name);
            if (!ok)
            {
                Console.WriteLine("  got  [" + got + "]");
                Console.WriteLine("  want [" + want + "]");
                fails++;
            }
        }

        static IPbind.IpLineResult Line(IPbind.IpLineResult[] r, int i)
        {
            return r[i];
        }

        static void TestValidator()
        {
            IPbind.IpLineResult[] r = IPbind.IpListValidator.Validate(new string[] {
                "10.10.10.98/24",
                "192.168.1.98/24"
            });
            Expect(r.Length == 2 && r[0].Valid && r[1].Valid && !r[0].Ignored,
                "valid CIDR lines pass");
            Expect(r[0].Ip == "10.10.10.98" && r[0].Prefix == 24,
                "valid line keeps ip and prefix");

            r = IPbind.IpListValidator.Validate(new string[] { "256.1.1.1/24" });
            Expect(r.Length == 1 && !r[0].Valid && !r[0].Ignored
                && r[0].Reason == "octet out of range",
                "bad octet 256 is octet out of range");

            r = IPbind.IpListValidator.Validate(new string[] { "192.168.1.1/33" });
            Expect(!r[0].Valid && r[0].Reason == "bad CIDR prefix",
                "prefix /33 is bad CIDR prefix");

            r = IPbind.IpListValidator.Validate(new string[] { "192.168.1.1" });
            Expect(r[0].Valid && r[0].Prefix == 24 && r[0].Ip == "192.168.1.1",
                "missing prefix is valid and defaults to /24 like Apply");

            r = IPbind.IpListValidator.Validate(new string[] {
                "10.10.10.98/24",
                "10.10.10.98/24"
            });
            Expect(r[0].Valid && !r[1].Valid && r[1].Reason == "duplicate of line 1",
                "duplicate of line 1");

            r = IPbind.IpListValidator.Validate(new string[] {
                "10.10.10.98/24",
                "10.10.10.98 / 24"
            });
            Expect(r[0].Valid && !r[1].Valid && r[1].Reason == "duplicate of line 1",
                "canonical duplicate with spaces around slash");

            r = IPbind.IpListValidator.Validate(new string[] { "not-an-ip" });
            Expect(!r[0].Valid && r[0].Reason == "not an IPv4 address",
                "garbage is not an IPv4 address");

            r = IPbind.IpListValidator.Validate(new string[] {
                "10.10.10.98/24",
                "",
                "   ",
                "192.168.1.98/24"
            });
            Expect(r[1].Ignored && r[2].Ignored && r[0].Valid && r[3].Valid,
                "blank and whitespace lines are ignored");
            Expect(IPbind.IpListValidator.InvalidCount(r) == 0,
                "ignored blanks do not count as invalid");

            r = IPbind.IpListValidator.Validate(new string[] {
                "10.10.10.98/24",
                "256.1.1.1/24",
                "10.10.10.98/24"
            });
            Expect(IPbind.IpListValidator.InvalidCount(r) == 2,
                "invalid count is bad octet plus duplicate");
            ExpectEq(IPbind.IpListValidator.InvalidStatus(1), "1 line is invalid.",
                "singular invalid status");
            ExpectEq(IPbind.IpListValidator.InvalidStatus(2), "2 lines are invalid.",
                "plural invalid status");
            ExpectEq(IPbind.IpListValidator.InvalidStatus(0), "",
                "zero invalid status is empty");
        }

        static void TestBound()
        {
            string[] lines = new string[] {
                "10.10.10.98/24",
                "",
                "192.168.1.98/24",
                "10.0.0.1/8"
            };
            bool[] b = IPbind.BoundState.Compute(lines, new string[] { "10.10.10.98", "10.0.0.1" });
            Expect(b.Length == 4, "bound flags match line count");
            Expect(b[0] && !b[1] && !b[2] && b[3],
                "bound true only when the address is present");

            b = IPbind.BoundState.Compute(lines, new string[] { "10.10.10.98/24" });
            Expect(!b[0], "present address with a prefix does not match the host");

            b = IPbind.BoundState.Compute(null, null);
            Expect(b.Length == 0, "null lines yield an empty bound array");
        }

        static void TestBanner()
        {
            ExpectEq(IPbind.BannerHeadline.FormatStatic("Onboard Ethernet", 8, true),
                "8 static addresses on Onboard Ethernet, DHCP kept",
                "8 addresses DHCP kept");
            ExpectEq(IPbind.BannerHeadline.FormatStatic("Onboard Ethernet", 8, false),
                "8 static addresses on Onboard Ethernet, DHCP off",
                "8 addresses DHCP off");
            ExpectEq(IPbind.BannerHeadline.FormatStatic("Ethernet", 1, true),
                "1 static address on Ethernet, DHCP kept",
                "singular 1 static address");
            ExpectEq(IPbind.BannerHeadline.FormatStatic("Ethernet", 0, false),
                "0 static addresses on Ethernet, DHCP off",
                "0 addresses DHCP off");
            ExpectEq(IPbind.BannerHeadline.FormatSafe("Onboard Ethernet"),
                "No static addresses on Onboard Ethernet",
                "green 0 static headline");
        }

        static void TestSiteLists()
        {
            string dir = Path.Combine(Path.GetTempPath(), "ipbind-sitelist-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            try
            {
                File.WriteAllLines(Path.Combine(dir, "ip-list.txt"), new string[] { "10.0.0.1/24", "10.0.0.2/24" });
                IPbind.SiteListStore s = new IPbind.SiteListStore(dir);
                s.Initialize();
                Expect(s.CurrentName == "Default", "migration names the list Default");
                string[] loaded = s.LoadCurrent();
                Expect(loaded.Length == 2 && loaded[0] == "10.0.0.1/24",
                    "migration copies ip-list.txt into Default");
                Expect(File.Exists(Path.Combine(dir, "ip-list.txt")),
                    "ip-list.txt is left in place");

                string err;
                Expect(s.SaveAs("Lab", new string[] { "192.168.1.1/24" }, out err) && err == null,
                    "SaveAs Lab succeeds");
                Expect(s.CurrentName == "Lab", "SaveAs switches current to the new name");

                string[] lab;
                Expect(s.SwitchTo("Default", out lab, out err) && lab[0] == "10.0.0.1/24",
                    "SwitchTo Default reloads migrated lines");
                Expect(s.CurrentName == "Default", "current is Default after switch");

                Expect(s.Rename("Default", "Home", out err) && s.CurrentName == "Home",
                    "Rename Default -> Home");
                bool hasHome = false;
                foreach (string n in s.Names) if (n == "Home") hasHome = true;
                Expect(hasHome, "Home is in the index after rename");

                Expect(s.Delete("Lab", out err), "Delete Lab");
                bool hasLab = false;
                foreach (string n in s.Names) if (n == "Lab") hasLab = true;
                Expect(!hasLab, "Lab is gone after delete");

                Expect(!s.SaveAs("", new string[] { "1.1.1.1/24" }, out err)
                    && err != null && err.IndexOf("empty", StringComparison.OrdinalIgnoreCase) >= 0,
                    "empty name is rejected");
                Expect(!s.SaveAs("bad/name", new string[] { "1.1.1.1/24" }, out err)
                    && err != null && err.IndexOf("path", StringComparison.OrdinalIgnoreCase) >= 0,
                    "path character is rejected");
                Expect(!s.SaveAs("home", new string[] { "1.1.1.1/24" }, out err)
                    && err != null && err.IndexOf("already", StringComparison.OrdinalIgnoreCase) >= 0,
                    "duplicate name ignoring case is rejected");

                // Second Initialize reads the index rather than migrating again.
                IPbind.SiteListStore s2 = new IPbind.SiteListStore(dir);
                s2.Initialize();
                Expect(s2.CurrentName == "Home", "index restores current selection");
            }
            finally
            {
                try { Directory.Delete(dir, true); } catch { }
            }
        }

        static int Main()
        {
            TestValidator();
            TestBound();
            TestBanner();
            TestSiteLists();
            Console.WriteLine("WPF_FEATURE_FAILS=" + fails);
            return fails == 0 ? 0 : 1;
        }
    }
}
