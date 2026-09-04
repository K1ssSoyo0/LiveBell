using System;
using System.Linq;
using System.Windows;

namespace LiveBell
{
    public partial class App : Application
    {
        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            MainWindow window = new MainWindow();
            MainWindow = window;
            window.StartMonitoring();
            if (!e.Args.Contains("--background", StringComparer.OrdinalIgnoreCase))
                window.Show();
        }
    }
}
