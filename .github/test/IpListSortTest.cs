using System;
using System.IO;
using System.Reflection;
using System.Runtime.Serialization;
using System.Windows.Forms;

namespace IPbindTest
{
    // 2.3.144: default IP list is in numerical order; Save List always re-sorts;
    // LoadList does not sort a hand-edited file. Duplicates stay; invalid lines
    // keep their relative order at the end.
    internal static class IpListSortTest
    {
        const BindingFlags F = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
        static int fails;

        static void Expect(bool cond, string msg)
        {
            if (cond) return;
            fails++;
            Console.WriteLine("FAIL: " + msg);
        }

        static void ExpectLines(string[] got, string[] want, string msg)
        {
            if (got == null)
            {
                Expect(false, msg + " (got null)");
                return;
            }
            if (got.Length != want.Length)
            {
                Expect(false, msg + " length " + got.Length + " != " + want.Length + " got=[" + string.Join(" | ", got) + "]");
                return;
            }
            for (int i = 0; i < want.Length; i++)
            {
                if (got[i] != want[i])
                {
                    Expect(false, msg + " at " + i + ": got '" + got[i] + "' want '" + want[i] + "'");
                    return;
                }
            }
        }

        static string[] Sort(params string[] lines)
        {
            return IPbind.MainForm.SortIpList(lines);
        }

        static object NewForm(string storeFile, string[] lines)
        {
            object main = FormatterServices.GetUninitializedObject(typeof(IPbind.MainForm));
            string dir = Path.GetDirectoryName(storeFile);
            typeof(IPbind.MainForm).GetField("AppDir", F).SetValue(main, dir);
            typeof(IPbind.MainForm).GetField("StoreFile", F).SetValue(main, storeFile);
            TextBox box = new TextBox();
            box.Multiline = true;
            if (lines != null) box.Lines = lines;
            typeof(IPbind.MainForm).GetField("txtIPs", F).SetValue(main, box);
            return main;
        }

        static string[] BoxLines(object main)
        {
            TextBox box = (TextBox)typeof(IPbind.MainForm).GetField("txtIPs", F).GetValue(main);
            return box.Lines;
        }

        [STAThread]
        static int Main()
        {
            string[] nines = Sort("192.168.1.10/24", "192.168.1.9/24");
            ExpectLines(nines, new string[] { "192.168.1.9/24", "192.168.1.10/24" },
                "192.168.1.9 comes before 192.168.1.10");

            string[] bare = Sort("192.168.1.10", "192.168.1.9");
            ExpectLines(bare, new string[] { "192.168.1.9", "192.168.1.10" },
                "bare IPv4 addresses sort numerically");

            string[] mixedPfx = Sort("10.0.0.1/24", "10.0.0.1/16");
            ExpectLines(mixedPfx, new string[] { "10.0.0.1/16", "10.0.0.1/24" },
                "same address sorts by prefix");

            string[] dups = Sort("192.168.1.10/24", "10.0.0.1/24", "192.168.1.10/24");
            ExpectLines(dups, new string[] { "10.0.0.1/24", "192.168.1.10/24", "192.168.1.10/24" },
                "duplicates are kept and only reordered");

            string[] invalid = Sort("not-an-ip", "192.168.1.10/24", "zzz", "192.168.1.9/24", "also bad");
            ExpectLines(invalid, new string[] { "192.168.1.9/24", "192.168.1.10/24", "not-an-ip", "zzz", "also bad" },
                "invalid lines stay in relative order at the end");

            FieldInfo defField = typeof(IPbind.MainForm).GetField("DefaultIPs", F);
            Expect(defField != null, "MainForm.DefaultIPs is missing");
            if (defField != null)
            {
                string[] defaults = (string[])defField.GetValue(null);
                string[] sortedDefaults = IPbind.MainForm.SortIpList(defaults);
                ExpectLines(sortedDefaults, defaults, "shipped DefaultIPs must already be in numerical order");
                int i128 = Array.IndexOf(defaults, "10.255.128.98/24");
                int i255 = Array.IndexOf(defaults, "10.255.255.98/24");
                Expect(i128 >= 0 && i255 >= 0 && i128 < i255,
                    "DefaultIPs must list 10.255.128.98/24 before 10.255.255.98/24");
            }

            string tmp = Path.Combine(Path.GetTempPath(), "ipbind-sort-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tmp);
            try
            {
                MethodInfo save = typeof(IPbind.MainForm).GetMethod("SaveList", F, null, new Type[] { typeof(bool) }, null);
                MethodInfo load = typeof(IPbind.MainForm).GetMethod("LoadList", F, null, Type.EmptyTypes, null);
                Expect(save != null, "SaveList(bool) is missing");
                Expect(load != null, "LoadList() is missing");
                if (save == null || load == null) return Report();

                string store = Path.Combine(tmp, "ip-list.txt");
                string[] unsorted = new string[] { "192.168.1.10/24", "10.10.10.98/24", "192.168.1.9/24" };
                object main = NewForm(store, unsorted);
                save.Invoke(main, new object[] { true });
                string[] afterSave = BoxLines(main);
                ExpectLines(afterSave, new string[] { "10.10.10.98/24", "192.168.1.9/24", "192.168.1.10/24" },
                    "SaveList must sort the list numerically");
                ExpectLines(File.ReadAllLines(store), afterSave, "SaveList must write the sorted list to disk");

                // Clicking Save List with no edits still re-sorts (file already sorted:
                // put the box back out of order, which is what a hand-edited load looks like).
                string[] alreadyOnDisk = File.ReadAllLines(store);
                TextBox box = (TextBox)typeof(IPbind.MainForm).GetField("txtIPs", F).GetValue(main);
                box.Lines = new string[] { "192.168.1.10/24", "192.168.1.9/24", "10.10.10.98/24" };
                save.Invoke(main, new object[] { true });
                ExpectLines(BoxLines(main), alreadyOnDisk,
                    "Save List with no additions still re-sorts the whole list");

                // Hand-edited file is not sorted on load.
                string hand = Path.Combine(tmp, "hand.txt");
                File.WriteAllLines(hand, new string[] { "192.168.1.10/24", "192.168.1.9/24" });
                object loaded = NewForm(hand, null);
                load.Invoke(loaded, null);
                ExpectLines(BoxLines(loaded), new string[] { "192.168.1.10/24", "192.168.1.9/24" },
                    "LoadList must not auto-sort a hand-edited file");
                ExpectLines(File.ReadAllLines(hand), new string[] { "192.168.1.10/24", "192.168.1.9/24" },
                    "LoadList must not rewrite a hand-edited file");
            }
            catch (Exception ex)
            {
                Expect(false, "SaveList/LoadList test threw: " + ex);
            }
            finally
            {
                try { Directory.Delete(tmp, true); } catch { }
            }

            return Report();
        }

        static int Report()
        {
            Console.WriteLine(fails == 0 ? "IP_LIST_SORT_FAILS=0" : ("IP_LIST_SORT_FAILS=" + fails));
            return fails == 0 ? 0 : 1;
        }
    }
}
