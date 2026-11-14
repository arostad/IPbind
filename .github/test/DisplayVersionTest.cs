using System;
using IPbind;

namespace IPbindTest
{
    internal static class DisplayVersionTest
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

        static void ExpectWrite(string current, string numeric, string display, bool want, string msg)
        {
            string writeValue;
            bool got = InstalledVersionSync.ShouldWrite(current, numeric, display, out writeValue);
            Expect(got == want, msg + " ShouldWrite=" + got + " want " + want);
            if (want)
                ExpectEq(writeValue, display, msg + " writeValue");
        }

        static int Main()
        {
            // Numeric stays numeric; trailing b is display-only and is stripped for compares.
            ExpectEq(VersionText.Numeric("2.3.139"), "2.3.139", "Numeric leaves plain versions");
            ExpectEq(VersionText.Numeric("2.3.139b"), "2.3.139", "Numeric strips trailing b");
            ExpectEq(VersionText.Numeric("2.3.139B"), "2.3.139", "Numeric strips trailing B");
            ExpectEq(VersionText.Numeric("beta"), "beta", "Numeric does not strip a lone word ending in a");

            System.Version parsed;
            Expect(VersionText.TryParse("2.3.139b", out parsed) && parsed.ToString() == "2.3.139",
                "TryParse must accept a Beta display string");
            Expect(VersionText.TryParse("2.3.139", out parsed) && parsed.ToString() == "2.3.139",
                "TryParse still accepts a numeric version");

            if (BuildInfo.IsBeta)
            {
                ExpectEq(BuildInfo.DisplayVersion, BuildInfo.Version + "b", "Beta DisplayVersion appends b");
                ExpectEq(VersionText.ForDisplay("2.3.140"), "2.3.140b", "ForDisplay appends b in Beta");
                ExpectEq(VersionText.ForDisplay("2.3.140b"), "2.3.140b", "ForDisplay is idempotent");
                Expect(BuildInfo.DisplayVersion.EndsWith("b"), "running DisplayVersion ends with b");

                // Andy's catch: 2.3.139 already in Apps & Features (numeric from Inno) must be rewritten as 2.3.139b.
                ExpectWrite("2.3.139", "2.3.139", "2.3.139b", true, "numeric same version, missing b");
                ExpectWrite("2.3.139b", "2.3.139", "2.3.139b", false, "already has b");
                ExpectWrite("2.3.138", "2.3.139", "2.3.139b", true, "raise 138 -> 139b after in-app update");
                ExpectWrite("2.3.138b", "2.3.139", "2.3.139b", true, "raise 138b -> 139b");
                ExpectWrite("2.3.140", "2.3.139", "2.3.139b", false, "never lower numeric version");
                ExpectWrite("2.3.140b", "2.3.139", "2.3.139b", false, "never lower a b display of a newer version");
                ExpectWrite(null, "2.3.139", "2.3.139b", true, "missing value");
                ExpectWrite("", "2.3.139", "2.3.139b", true, "empty value");
            }
            else
            {
                ExpectEq(BuildInfo.DisplayVersion, BuildInfo.Version, "prod DisplayVersion has no b");
                ExpectEq(VersionText.ForDisplay("2.3.128"), "2.3.128", "prod ForDisplay stays numeric");
                ExpectWrite("2.3.127", "2.3.128", "2.3.128", true, "prod raise");
                ExpectWrite("2.3.128", "2.3.128", "2.3.128", false, "prod already current");
                ExpectWrite("2.3.129", "2.3.128", "2.3.128", false, "prod never lower");
            }

            if (fails > 0)
            {
                Console.WriteLine("DISPLAY_VERSION_UNIT_FAILS=" + fails);
                return 1;
            }
            Console.WriteLine("DISPLAY_VERSION_UNIT_FAILS=0");
            return 0;
        }
    }
}
