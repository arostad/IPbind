using System;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace IPbind
{
    // Small themed name prompt for site lists. Not a MessageBox.
    sealed class InputDialog
    {
        public static string Prompt(Window owner, bool dark, string title, string label, string initial)
        {
            Window w = new Window();
            w.Owner = owner;
            w.Title = title ?? "";
            w.WindowStartupLocation = WindowStartupLocation.CenterOwner;
            w.ResizeMode = ResizeMode.NoResize;
            w.SizeToContent = SizeToContent.WidthAndHeight;
            w.ShowInTaskbar = false;
            w.MinWidth = 380;
            w.Background = BrushOf("DialogBgBrush", dark ? Color.FromRgb(0x2B, 0x2B, 0x2B) : Colors.White);
            w.Foreground = BrushOf("FgBrush", dark ? Color.FromRgb(0xE6, 0xE6, 0xE6) : Color.FromRgb(0x1A, 0x1A, 0x1A));
            w.FontFamily = new FontFamily("Segoe UI");
            w.FontSize = 14;
            w.SourceInitialized += delegate
            {
                IntPtr hwnd = new WindowInteropHelper(w).Handle;
                WindowTheme.ApplyTitleBar(hwnd, dark);
            };

            DockPanel root = new DockPanel();
            Border footer = new Border();
            footer.Background = BrushOf("FooterBrush", dark ? Color.FromRgb(0x20, 0x20, 0x20) : Color.FromRgb(0xF3, 0xF3, 0xF3));
            footer.Padding = new Thickness(28, 14, 28, 16);
            DockPanel.SetDock(footer, Dock.Bottom);

            StackPanel buttons = new StackPanel();
            buttons.Orientation = Orientation.Horizontal;
            buttons.HorizontalAlignment = HorizontalAlignment.Right;
            Button ok = new Button();
            ok.Content = "OK";
            ok.MinWidth = 88;
            ok.Padding = new Thickness(16, 6, 16, 6);
            ok.IsDefault = true;
            ok.Background = BrushOf("PrimaryBack", Color.FromRgb(0x00, 0x5F, 0xB8));
            ok.Foreground = BrushOf("PrimaryFore", Colors.White);
            ok.BorderBrush = ok.Background;
            ok.FontWeight = FontWeights.SemiBold;
            ok.Margin = new Thickness(0, 0, 8, 0);
            Button cancel = new Button();
            cancel.Content = "Cancel";
            cancel.MinWidth = 88;
            cancel.Padding = new Thickness(16, 6, 16, 6);
            cancel.IsCancel = true;
            cancel.Background = BrushOf("SecondaryBack", dark ? Color.FromRgb(0x33, 0x33, 0x33) : Colors.White);
            cancel.Foreground = BrushOf("FgBrush", dark ? Color.FromRgb(0xE6, 0xE6, 0xE6) : Color.FromRgb(0x1A, 0x1A, 0x1A));
            cancel.BorderBrush = BrushOf("SecondaryBorder", Color.FromRgb(0xD0, 0xD0, 0xD0));
            buttons.Children.Add(ok);
            buttons.Children.Add(cancel);
            footer.Child = buttons;

            StackPanel body = new StackPanel();
            body.Margin = new Thickness(28, 22, 28, 12);
            TextBlock lbl = new TextBlock();
            lbl.Text = label ?? "";
            lbl.TextWrapping = TextWrapping.Wrap;
            lbl.Margin = new Thickness(0, 0, 0, 8);
            TextBox box = new TextBox();
            box.MinHeight = 28;
            box.Text = initial ?? "";
            box.MaxLength = 64;
            body.Children.Add(lbl);
            body.Children.Add(box);

            root.Children.Add(footer);
            root.Children.Add(body);
            w.Content = root;

            string result = null;
            ok.Click += delegate
            {
                result = box.Text;
                w.DialogResult = true;
            };
            cancel.Click += delegate { w.DialogResult = false; };
            w.ContentRendered += delegate
            {
                box.Focus();
                box.SelectAll();
                Keyboard.Focus(box);
            };
            bool? okResult = w.ShowDialog();
            if (okResult == true) return result;
            return null;
        }

        static Brush BrushOf(string key, Color fallback)
        {
            object v = Application.Current != null ? Application.Current.TryFindResource(key) : null;
            SolidColorBrush b = v as SolidColorBrush;
            if (b != null) return b;
            return new SolidColorBrush(fallback);
        }
    }
}
