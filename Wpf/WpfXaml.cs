using System;
using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Markup;

namespace IPbind
{
    static class WpfXaml
    {
        public const string Theme = "IPbind.Wpf.Theme.xaml";
        public const string Main = "IPbind.Wpf.MainWindow.xaml";
        public const string About = "IPbind.Wpf.AboutWindow.xaml";
        public const string DhcpReminder = "IPbind.Wpf.DhcpReminderWindow.xaml";
        public const string PrivacyConsent = "IPbind.Wpf.PrivacyConsentWindow.xaml";

        public static readonly string[] All = new string[]
        {
            Theme, Main, About, DhcpReminder, PrivacyConsent
        };

        public static Stream Open(string name)
        {
            Stream s = Assembly.GetExecutingAssembly().GetManifestResourceStream(name);
            if (s == null)
                throw new InvalidOperationException("Missing embedded XAML resource " + name);
            return s;
        }

        public static object Load(string name)
        {
            using (Stream s = Open(name))
                return XamlReader.Load(s);
        }

        public static Window LoadWindow(string name)
        {
            return (Window)Load(name);
        }

        public static ResourceDictionary LoadDictionary(string name)
        {
            return (ResourceDictionary)Load(name);
        }

        public static void ApplyTheme(Application app, bool dark)
        {
            if (app == null) throw new ArgumentNullException("app");
            ResourceDictionary theme = LoadDictionary(Theme);
            app.Resources.MergedDictionaries.Add(theme);
            ResourceDictionary pal = (ResourceDictionary)theme[dark ? "DarkPalette" : "LightPalette"];
            if (pal == null)
                throw new InvalidOperationException("Theme.xaml is missing the " + (dark ? "Dark" : "Light") + " palette.");
            app.Resources.MergedDictionaries.Add(pal);
        }

        public static T Find<T>(FrameworkElement root, string name) where T : class
        {
            T c = root.FindName(name) as T;
            if (c == null)
                throw new InvalidOperationException("XAML is missing " + typeof(T).Name + " '" + name + "'.");
            return c;
        }
    }
}
