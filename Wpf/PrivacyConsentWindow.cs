using System;
using System.IO;
using System.Reflection;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Interop;

namespace IPbind
{
    sealed class PrivacyConsentWindow
    {
        readonly Window window;

        public Window Window { get { return window; } }

        public PrivacyConsentWindow(Window owner, bool dark)
        {
            window = WpfXaml.LoadWindow(WpfXaml.PrivacyConsent);
            window.Title = BuildInfo.AppName + " privacy";
            window.Owner = owner;
            window.SourceInitialized += delegate
            {
                IntPtr hwnd = new WindowInteropHelper(window).Handle;
                WindowTheme.ApplyTitleBar(hwnd, dark);
            };

            TextBlock intro = WpfXaml.Find<TextBlock>(window, "intro");
            intro.Text = "Please read how " + BuildInfo.AppName + " uses the network before turning on update checks at launch.";
            TextBox text = WpfXaml.Find<TextBox>(window, "privacyText");
            text.Text = ReadPrivacyText();
            Button agree = WpfXaml.Find<Button>(window, "agreeButton");
            Button cancel = WpfXaml.Find<Button>(window, "cancelButton");
            agree.Click += delegate { window.DialogResult = true; };
            cancel.Click += delegate { window.DialogResult = false; };
            window.ContentRendered += delegate
            {
                cancel.Focus();
                text.CaretIndex = 0;
            };
        }

        public bool? ShowDialog()
        {
            return window.ShowDialog();
        }

        static string ReadPrivacyText()
        {
            try
            {
                using (Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream("IPbind.privacy.txt"))
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
}
