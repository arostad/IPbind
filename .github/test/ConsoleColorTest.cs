using System;
using System.Drawing;
using System.Reflection;
using System.Runtime.Serialization;
using System.Windows.Forms;

namespace IPbindTest
{
    // 2.3.144: the console colors only the level tag (OK green, WARN amber,
    // ERR red, INFO blue). The stamp and the message stay the normal color.
    // Drives MainForm.LogConsole on the real console control and reads the
    // character colors back. The console stays dark in light mode too, so every
    // tag color must read on its background.
    internal static class ConsoleColorTest
    {
        const BindingFlags F = BindingFlags.Instance | BindingFlags.Static | BindingFlags.NonPublic | BindingFlags.Public;
        static int fails;

        static void Expect(bool cond, string msg)
        {
            if (cond) return;
            fails++;
            Console.WriteLine("FAIL: " + msg);
        }

        static double Lum(Color c)
        {
            double[] v = new double[] { c.R / 255.0, c.G / 255.0, c.B / 255.0 };
            for (int i = 0; i < 3; i++)
                v[i] = v[i] <= 0.03928 ? v[i] / 12.92 : Math.Pow((v[i] + 0.055) / 1.055, 2.4);
            return 0.2126 * v[0] + 0.7152 * v[1] + 0.0722 * v[2];
        }

        static double Contrast(Color a, Color b)
        {
            double la = Lum(a), lb = Lum(b);
            return (Math.Max(la, lb) + 0.05) / (Math.Min(la, lb) + 0.05);
        }

        static bool Same(Color a, Color b) { return a.R == b.R && a.G == b.G && a.B == b.B; }

        static Color ColorAt(RichTextBox box, string needle)
        {
            int at = box.Text.IndexOf(needle, StringComparison.Ordinal);
            if (at < 0) { Expect(false, "console text is missing '" + needle + "'"); return Color.Empty; }
            box.Select(at, needle.Length);
            return box.SelectionColor;
        }

        [STAThread]
        static int Main()
        {
            FieldInfo field = typeof(IPbind.MainForm).GetField("console", F);
            Expect(field != null, "MainForm has no console field");
            if (field == null) return Report();
            Expect(typeof(RichTextBox).IsAssignableFrom(field.FieldType),
                "console is a " + field.FieldType.Name + ", which cannot color lines (needs a RichTextBox)");
            if (!typeof(Control).IsAssignableFrom(field.FieldType) || field.FieldType.IsAbstract) return Report();

            Control console = (Control)Activator.CreateInstance(field.FieldType);
            console.Size = new Size(700, 300);
            console.Font = new Font("Consolas", 9F);
            console.BackColor = Color.FromArgb(18, 18, 18);
            console.ForeColor = Color.FromArgb(210, 210, 210);
            TextBoxBase tb = console as TextBoxBase;
            if (tb != null) { tb.Multiline = true; tb.ReadOnly = true; }

            Form host = new Form();
            host.ClientSize = new Size(720, 320);
            host.Controls.Add(console);
            IntPtr h = host.Handle; h = console.Handle;

            object main = FormatterServices.GetUninitializedObject(typeof(IPbind.MainForm));
            field.SetValue(main, console);
            MethodInfo logConsole = typeof(IPbind.MainForm).GetMethod("LogConsole", F, null, new Type[] { typeof(string), typeof(string) }, null);
            Expect(logConsole != null, "MainForm.LogConsole(string, string) is missing");
            if (logConsole == null) return Report();
            logConsole.Invoke(main, new object[] { "plain info line", "INFO" });
            logConsole.Invoke(main, new object[] { "it worked line", "OK" });
            logConsole.Invoke(main, new object[] { "careful line", "WARN" });
            logConsole.Invoke(main, new object[] { "broken line", "ERR" });
            logConsole.Invoke(main, new object[] { "plain again", "INFO" });

            RichTextBox rich = console as RichTextBox;
            if (rich == null) return Report();

            Color fg = console.ForeColor, bg = console.BackColor;
            Color okWant = Color.FromArgb(90, 200, 110);
            Color warnWant = Color.FromArgb(232, 180, 70);
            Color errWant = Color.FromArgb(240, 100, 100);
            Color infoWant = Color.FromArgb(64, 150, 230);

            Color ok = ColorAt(rich, "OK");
            Color warn = ColorAt(rich, "WARN");
            Color err = ColorAt(rich, "ERR");
            Color info = ColorAt(rich, "INFO");
            Color okMsg = ColorAt(rich, "it worked line");
            Color warnMsg = ColorAt(rich, "careful line");
            Color errMsg = ColorAt(rich, "broken line");
            Color infoMsg = ColorAt(rich, "plain info line");
            Color info2Msg = ColorAt(rich, "plain again");
            Color stamp = Color.Empty;
            int warnAt = rich.Text.IndexOf("WARN  careful line", StringComparison.Ordinal);
            int lineStart = rich.Text.LastIndexOf('[', Math.Max(0, warnAt));
            if (lineStart >= 0) { rich.Select(lineStart, 10); stamp = rich.SelectionColor; }

            Console.WriteLine("fg=" + fg + " ok=" + ok + " warn=" + warn + " err=" + err + " info=" + info + " stamp=" + stamp);
            Expect(Same(ok, okWant), "OK tag must be green (90,200,110), got " + ok);
            Expect(Same(warn, warnWant), "WARN tag must be yellow (232,180,70), got " + warn);
            Expect(Same(err, errWant), "ERR tag must be red (240,100,100), got " + err);
            Expect(Same(info, infoWant), "INFO tag must be blue (64,150,230), got " + info);
            Expect(Same(okMsg, fg) && Same(warnMsg, fg) && Same(errMsg, fg) && Same(infoMsg, fg) && Same(info2Msg, fg),
                "the message after each tag must stay the normal console color");
            Expect(Same(stamp, fg), "the [hh:mm:ss] stamp on a WARN line must stay the normal console color");
            Expect(!Same(ok, warn) && !Same(ok, err) && !Same(ok, info) && !Same(warn, err) && !Same(warn, info) && !Same(err, info),
                "OK, WARN, ERR and INFO tag colors must differ");
            Color[] all = new Color[] { ok, warn, err, info };
            string[] names = new string[] { "OK", "WARN", "ERR", "INFO" };
            for (int i = 0; i < all.Length; i++)
            {
                double c = Contrast(all[i], bg);
                Console.WriteLine(names[i] + " contrast " + c.ToString("0.0") + ":1");
                Expect(c >= 4.5, names[i] + " contrast on the console background is " + c.ToString("0.0") + ":1 (needs 4.5:1)");
            }
            Expect(Same(bg, Color.FromArgb(18, 18, 18)), "console background changed");

            host.Dispose();
            return Report();
        }

        static int Report()
        {
            Console.WriteLine(fails == 0 ? "CONSOLE_COLOR_FAILS=0" : ("CONSOLE_COLOR_FAILS=" + fails));
            return fails == 0 ? 0 : 1;
        }
    }
}
