// Console driver for IPbind's Apply-safety helpers (never shipped in the app).
using System;
using System.Reflection;

namespace IPbindTest
{
    static class ApplySafetyTest
    {
        static int fails;

        static uint Parse(string s)
        {
            MethodInfo m = typeof(IPbind.MainForm).GetMethod("TryIPv4",
                BindingFlags.Static | BindingFlags.NonPublic);
            object[] args = new object[] { s, 0u };
            bool ok = (bool)m.Invoke(null, args);
            if (!ok) throw new Exception("TryIPv4 failed for " + s);
            return (uint)args[1];
        }

        static void Expect(bool cond, string name)
        {
            Console.WriteLine((cond ? "PASS " : "FAIL ") + name);
            if (!cond) fails++;
        }

        static int Main()
        {
            Type t = typeof(IPbind.MainForm);
            MethodInfo classify = t.GetMethod("ClassifyDhcpOverlap", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            MethodInfo netText = t.GetMethod("DhcpNetworkText", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            MethodInfo eqLog = t.GetMethod("EqualDhcpAddressLog", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            MethodInfo inLog = t.GetMethod("InsideDhcpNetworkLog", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            MethodInfo useLog = t.GetMethod("AddressInUseLog", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            MethodInfo allLog = t.GetMethod("AllAddressesInUseLog", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            MethodInfo mac = t.GetMethod("FormatMac", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);
            MethodInfo clamp = t.GetMethod("ClampPrefix", BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public);

            uint l24 = Parse("192.168.1.50");
            uint bindIn = Parse("192.168.1.98");
            uint bindOut = Parse("192.168.2.1");
            uint bindEq = Parse("192.168.1.50");
            uint l8 = Parse("10.0.0.1");
            uint in8 = Parse("10.1.2.3");
            uint l32 = Parse("10.0.0.1");

            Expect(classify.Invoke(null, new object[] { bindEq, l24, 24 }).ToString() == "SameAsLease",
                "equal lease /24 is SameAsLease");
            Expect(classify.Invoke(null, new object[] { bindIn, l24, 24 }).ToString() == "InsideSubnet",
                "192.168.1.98 is inside 192.168.1.50/24");
            Expect(classify.Invoke(null, new object[] { bindOut, l24, 24 }).ToString() == "None",
                "192.168.2.1 is outside 192.168.1.50/24");
            Expect(classify.Invoke(null, new object[] { in8, l8, 8 }).ToString() == "InsideSubnet",
                "10.1.2.3 is inside 10.0.0.1/8");
            Expect(classify.Invoke(null, new object[] { Parse("11.0.0.1"), l8, 8 }).ToString() == "None",
                "11.0.0.1 is outside 10.0.0.1/8");
            Expect(classify.Invoke(null, new object[] { l32, l32, 32 }).ToString() == "SameAsLease",
                "equal /32 is SameAsLease");
            Expect(classify.Invoke(null, new object[] { Parse("10.0.0.2"), l32, 32 }).ToString() == "None",
                "neighbor is outside /32");
            Expect(classify.Invoke(null, new object[] { bindIn, l24, 24 }).ToString() != "SameAsLease",
                "inside is not treated as equal");
            Expect((string)netText.Invoke(null, new object[] { l24, 24 }) == "192.168.1.0/24",
                "network text 192.168.1.50/24 -> 192.168.1.0/24");
            Expect((string)netText.Invoke(null, new object[] { l8, 8 }) == "10.0.0.0/8",
                "network text 10.0.0.1/8 -> 10.0.0.0/8");
            Expect((string)netText.Invoke(null, new object[] { Parse("192.168.1.50"), 32 }) == "192.168.1.50/32",
                "network text /32 is the host");
            Expect((string)netText.Invoke(null, new object[] { Parse("172.16.5.9"), 12 }) == "172.16.0.0/12",
                "network text 172.16.5.9/12 -> 172.16.0.0/12");
            Expect((int)clamp.Invoke(null, new object[] { -1 }) == 0, "clamp -1 -> 0");
            Expect((int)clamp.Invoke(null, new object[] { 33 }) == 32, "clamp 33 -> 32");
            Expect(classify.Invoke(null, new object[] { Parse("1.2.3.4"), Parse("8.8.8.8"), 0 }).ToString() == "InsideSubnet",
                "/0 contains every other address");
            Expect((string)eqLog.Invoke(null, new object[] { "192.168.1.50" })
                == "192.168.1.50 is this adapter's DHCP address. Nothing was applied.",
                "equal-lease error wording");
            Expect((string)inLog.Invoke(null, new object[] { "10.10.10.98", "192.168.1.0/24" })
                == "10.10.10.98 is inside the DHCP network 192.168.1.0/24. Another device could be given this address.",
                "inside-subnet warn wording");
            Expect((string)useLog.Invoke(null, new object[] { "10.10.10.98", "aa-bb-cc-dd-ee-ff" })
                == "10.10.10.98 is already in use on this network (MAC aa-bb-cc-dd-ee-ff), skipped.",
                "in-use warn wording");
            Expect((string)allLog.Invoke(null, new object[] { })
                == "Every address was already in use on this network. Nothing was applied.",
                "all-skipped error wording");
            Expect((string)mac.Invoke(null, new object[] { new byte[] { 0xAA, 0xBB, 0xCC, 0xDD, 0xEE, 0xFF }, 6 })
                == "aa-bb-cc-dd-ee-ff",
                "MAC format is lowercase hyphenated");
            Expect((string)mac.Invoke(null, new object[] { new byte[] { 0x01, 0x02 }, 2 }) == "01-02",
                "short MAC still formats");

            Console.WriteLine("RESULT=" + (fails == 0));
            return fails == 0 ? 0 : 1;
        }
    }
}
