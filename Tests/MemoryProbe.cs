using System;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using LiveBell;

// Runs in its own disposable folder, without App's single-instance activation
// or StartMonitoring's startup notifications. Never modifies the user's Data.
internal static class MemoryProbe
{
    private static Application app;
    private static MainWindow main;
    private static Stopwatch elapsed = Stopwatch.StartNew();
    private static TimeSpan previousCpu;
    private static long previousTime;
    private static int result;

    [STAThread]
    private static int Main(string[] args)
    {
        if (args[0] == "software") RenderOptions.ProcessRenderMode = System.Windows.Interop.RenderMode.SoftwareOnly;
        if (args[0] == "policy") typeof(MainWindow).Assembly.GetType("LiveBell.RenderingPolicy").GetMethod("Configure").Invoke(null, new object[] { new AppSettings() });
        app = new Application();
        app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
        System.Xml.XmlDocument xml = new System.Xml.XmlDocument();
        xml.Load(args[1]);
        app.Resources = (ResourceDictionary)System.Windows.Markup.XamlReader.Parse("<ResourceDictionary xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>" + xml.DocumentElement.FirstChild.InnerXml + "</ResourceDictionary>");
        app.Dispatcher.BeginInvoke(new Action(delegate { Run(args); }));
        app.Run();
        return result;
    }

    private static async void Run(string[] args)
    {
        try
        {
            Snapshot("resources");
            main = new MainWindow();
            main.ShowActivated = false; main.ShowInTaskbar = false;
            main.WindowStartupLocation = WindowStartupLocation.Manual;
            main.Left = -10000; main.Top = -10000;
            main.Show();
            await Task.Delay(3000);
            Snapshot("visible");
            for (int i = 0; i < 3; i++)
            {
                SettingsWindow settings = new SettingsWindow(new AppSettings());
                settings.ShowActivated = false; settings.ShowInTaskbar = false;
                settings.WindowStartupLocation = WindowStartupLocation.Manual;
                settings.Left = -10000; settings.Top = -10000;
                settings.Show(); await Task.Delay(200); settings.Close();
                if (main.Streamers.Count > 0)
                {
                    LiveToast toast = new LiveToast(main.Streamers[0], 8, true);
                    toast.Left = -10000; toast.Top = -10000;
                    toast.Show(); toast.Left = -10000; toast.Top = -10000;
                    await Task.Delay(100); toast.Close();
                }
            }
            await Task.Delay(2000);
            Snapshot("after-dialogs");
            MethodInfo refresh = typeof(MainWindow).GetMethod("RefreshOneAsync", BindingFlags.Instance | BindingFlags.NonPublic);
            int polls = args.Length > 2 ? Int32.Parse(args[2]) : 6;
            for (int i = 0; i < polls; i++)
            {
                Task[] tasks = new Task[main.Streamers.Count];
                for (int j = 0; j < tasks.Length; j++) tasks[j] = (Task)refresh.Invoke(main, new object[] { main.Streamers[j], false, false });
                await Task.WhenAll(tasks);
                await Task.Delay(6000);
                if (i == 0 || i == polls - 1) Snapshot("poll-" + (i + 1));
            }
            main.Hide();
            await Task.Delay(3000);
            Snapshot("hidden");
        }
        catch (Exception ex) { Console.WriteLine(ex); result = 1; }
        finally
        {
            if (main != null)
            {
                typeof(MainWindow).GetField("exiting", BindingFlags.Instance | BindingFlags.NonPublic).SetValue(main, true);
                main.Close();
            }
            app.Shutdown();
        }
    }

    private static void Snapshot(string phase)
    {
        using (Process p = Process.GetCurrentProcess())
        {
            long now = elapsed.ElapsedMilliseconds;
            double cpu = now > previousTime ? (p.TotalProcessorTime - previousCpu).TotalMilliseconds / (now - previousTime) / Environment.ProcessorCount * 100 : 0;
            Console.WriteLine("{0}: WS={1:F1} MiB; Private={2:F1} MiB; Managed={3:F1} MiB; CPU={4:F3}%; Tier={5}; Mode={6}", phase, p.WorkingSet64 / 1048576.0, p.PrivateMemorySize64 / 1048576.0, GC.GetTotalMemory(false) / 1048576.0, cpu, RenderCapability.Tier >> 16, RenderOptions.ProcessRenderMode);
            previousCpu = p.TotalProcessorTime; previousTime = now;
        }
    }
}
