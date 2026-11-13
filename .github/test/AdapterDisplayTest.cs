// Console tests for display-only adapter label tidy (never shipped in the app).
using System;

namespace IPbindTest
{
    static class AdapterDisplayTest
    {
        static int fails;

        static void Expect(string got, string want, string name)
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

        static void ExpectTrue(bool cond, string name)
        {
            Console.WriteLine((cond ? "PASS " : "FAIL ") + name);
            if (!cond) fails++;
        }

        static int Main()
        {
            // Andy's example: Intel (R) + generation (16), spaces collapsed, name first.
            Expect(
                IPbind.AdapterDisplay.Format(
                    "Onboard Ethernet",
                    "Intel(R) Ethernet Connection (16) I219-LM",
                    "Up"),
                "Onboard Ethernet | Intel Ethernet Connection I219-LM [Up]",
                "Intel I219-LM (R) and (16) tidy to the short label");

            Expect(
                IPbind.AdapterDisplay.CleanDescription("Intel(R) Ethernet Connection (16) I219-LM"),
                "Intel Ethernet Connection I219-LM",
                "CleanDescription drops (R) and (16)");

            Expect(
                IPbind.AdapterDisplay.CleanDescription("Intel(r) Ethernet Connection ( 16 ) I219-LM"),
                "Intel Ethernet Connection I219-LM",
                "lowercase (r) and spaced ( 16 ) are also dropped");

            Expect(
                IPbind.AdapterDisplay.CleanDescription("Intel(TM) Ethernet Controller"),
                "Intel Ethernet Controller",
                "CleanDescription drops (TM)");

            Expect(
                IPbind.AdapterDisplay.CleanDescription("Intel(tm) Ethernet Controller"),
                "Intel Ethernet Controller",
                "lowercase (tm) is dropped");

            Expect(
                IPbind.AdapterDisplay.CleanDescription("Intel\u00ae Ethernet Connection I219-LM"),
                "Intel Ethernet Connection I219-LM",
                "registered-sign symbol is dropped");

            Expect(
                IPbind.AdapterDisplay.CleanDescription("Intel Ethernet\u2122 Connection"),
                "Intel Ethernet Connection",
                "trademark symbol is dropped");

            // Windows duplicate suffixes must stay, including on the connection name.
            Expect(
                IPbind.AdapterDisplay.Format(
                    "Ethernet #2",
                    "Intel(R) Ethernet Connection (16) I219-LM",
                    "Up"),
                "Ethernet #2 | Intel Ethernet Connection I219-LM [Up]",
                "connection name keeps #2");

            Expect(
                IPbind.AdapterDisplay.Format(
                    "Local Area Connection #2",
                    "Realtek PCIe GbE Family Controller",
                    "Down"),
                "Local Area Connection #2 | Realtek PCIe GbE Family Controller [Down]",
                "Local Area Connection #2 is unchanged");

            Expect(
                IPbind.AdapterDisplay.CleanDescription("Some Vendor Adapter #2"),
                "Some Vendor Adapter #2",
                "description #2 is not stripped");

            // Parentheses that are part of a real model name stay.
            Expect(
                IPbind.AdapterDisplay.CleanDescription("Realtek PCIe GbE Family Controller (NDIS 6.40)"),
                "Realtek PCIe GbE Family Controller (NDIS 6.40)",
                "keeps (NDIS 6.40)");

            Expect(
                IPbind.AdapterDisplay.Format(
                    "Ethernet",
                    "Broadcom NetXtreme (BCM57766)",
                    "Up"),
                "Ethernet | Broadcom NetXtreme (BCM57766) [Up]",
                "keeps model parentheses with letters");

            Expect(
                IPbind.AdapterDisplay.CleanDescription("Widget Adapter (Pro)"),
                "Widget Adapter (Pro)",
                "keeps (Pro)");

            Expect(
                IPbind.AdapterDisplay.Format(
                    "Port (16)",
                    "Intel(R) Ethernet Connection (16) I219-LM",
                    "Up"),
                "Port (16) | Intel Ethernet Connection I219-LM [Up]",
                "connection name parentheses are not tidied");

            // Empty / whitespace descriptions omit the "| description" span.
            Expect(
                IPbind.AdapterDisplay.Format("Onboard Ethernet", "", "Up"),
                "Onboard Ethernet [Up]",
                "empty description");

            Expect(
                IPbind.AdapterDisplay.Format("Onboard Ethernet", null, "Down"),
                "Onboard Ethernet [Down]",
                "null description");

            Expect(
                IPbind.AdapterDisplay.Format("Onboard Ethernet", "   ", "Up"),
                "Onboard Ethernet [Up]",
                "whitespace-only description");

            Expect(
                IPbind.AdapterDisplay.CleanDescription(""),
                "",
                "CleanDescription empty string");

            Expect(
                IPbind.AdapterDisplay.CleanDescription(null),
                "",
                "CleanDescription null");

            // Extra spaces in the hardware description collapse; the name stays first.
            Expect(
                IPbind.AdapterDisplay.Format(
                    "Onboard Ethernet",
                    "Intel(R)   Ethernet    Connection   (16)   I219-LM",
                    "Up"),
                "Onboard Ethernet | Intel Ethernet Connection I219-LM [Up]",
                "collapsed spaces in the description");

            Console.WriteLine(fails == 0 ? "ALL PASSED" : "FAILURES=" + fails);
            return fails == 0 ? 0 : 1;
        }
    }
}
