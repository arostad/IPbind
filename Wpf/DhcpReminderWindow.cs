using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Interop;
using System.Windows.Media;

namespace IPbind
{
    sealed class DhcpReminderWindow
    {
        readonly Window window;
        readonly bool dark;
        readonly CheckBox understandBox;
        readonly CheckBox dontShowBox;
        readonly Button continueButton;

        public Window Window { get { return window; } }
        public bool DontShowAgain { get { return dontShowBox.IsChecked == true; } }

        public DhcpReminderWindow(Window owner, bool dark)
        {
            this.dark = dark;
            window = WpfXaml.LoadWindow(WpfXaml.DhcpReminder);
            window.Title = BuildInfo.AppName;
            window.Owner = owner;
            window.SourceInitialized += delegate
            {
                IntPtr hwnd = new WindowInteropHelper(window).Handle;
                WindowTheme.ApplyTitleBar(hwnd, dark);
            };

            understandBox = WpfXaml.Find<CheckBox>(window, "understandBox");
            dontShowBox = WpfXaml.Find<CheckBox>(window, "dontShowBox");
            continueButton = WpfXaml.Find<Button>(window, "continueButton");
            Button exitButton = WpfXaml.Find<Button>(window, "exitButton");
            TextBlock intro = WpfXaml.Find<TextBlock>(window, "intro");
            TextBlock replicaText = WpfXaml.Find<TextBlock>(window, "replicaText");

            replicaText.Text = UiCopy.DhcpButton;
            understandBox.Content = "I\u2019ll click " + UiCopy.DhcpButton + " before I plug into a live network";
            dontShowBox.Content = "Don\u2019t show this again";

            intro.Inlines.Clear();
            intro.Inlines.Add(new Run("The addresses you bind using this app stay on your selected interface until you click the "));
            Run bold = new Run(UiCopy.DhcpButton);
            bold.FontWeight = FontWeights.SemiBold;
            bold.Foreground = Brushes.White;
            if (!dark) bold.Foreground = new SolidColorBrush(Color.FromRgb(0x1A, 0x1A, 0x1A));
            intro.Inlines.Add(bold);
            intro.Inlines.Add(new Run(" button."));

            understandBox.Checked += delegate { ApplyContinueState(); };
            understandBox.Unchecked += delegate { ApplyContinueState(); };
            continueButton.Click += delegate { window.DialogResult = true; };
            exitButton.Click += delegate { window.DialogResult = false; };
            window.Closing += delegate(object sender, System.ComponentModel.CancelEventArgs e)
            {
                if (window.DialogResult == null) window.DialogResult = false;
            };
            ApplyContinueState();
        }

        public bool? ShowDialog()
        {
            return window.ShowDialog();
        }

        void ApplyContinueState()
        {
            bool on = understandBox.IsChecked == true;
            continueButton.IsEnabled = on;
            if (on)
            {
                continueButton.Opacity = 1;
                continueButton.Cursor = System.Windows.Input.Cursors.Hand;
            }
            else
            {
                continueButton.Opacity = 0.55;
                continueButton.Cursor = System.Windows.Input.Cursors.Arrow;
            }
        }
    }
}
