using System;
using System.Linq;
using System.Threading;
using System.Windows;

namespace LiveBell
{
    public partial class App : Application
    {
        private const string InstanceMutexName = "Local\\LiveBell_SingleInstance_8E472742";
        private const string ActivateEventName = "Local\\LiveBell_Activate_8E472742";
        private static Mutex instanceMutex;
        private static EventWaitHandle activateEvent;
        private static RegisteredWaitHandle activateWait;

        protected override void OnStartup(StartupEventArgs e)
        {
            base.OnStartup(e);
            bool isFirstInstance;
            instanceMutex = new Mutex(true, InstanceMutexName, out isFirstInstance);
            if (!isFirstInstance)
            {
                if (!e.Args.Contains("--background", StringComparer.OrdinalIgnoreCase)) SignalRunningInstance();
                instanceMutex.Dispose();
                instanceMutex = null;
                Shutdown();
                return;
            }

            activateEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivateEventName);
            activateWait = ThreadPool.RegisterWaitForSingleObject(activateEvent, OnActivateRequested, null, -1, false);
            MainWindow window;
            try { window = new MainWindow(); }
            catch (Exception ex)
            {
                MessageBox.Show(ex.Message, "开播铃无法启动", MessageBoxButton.OK, MessageBoxImage.Warning);
                Shutdown();
                return;
            }
            MainWindow = window;
            window.StartMonitoring();
            if (!e.Args.Contains("--background", StringComparer.OrdinalIgnoreCase))
                window.Show();
        }

        private static void SignalRunningInstance()
        {
            try
            {
                using (EventWaitHandle signal = EventWaitHandle.OpenExisting(ActivateEventName)) signal.Set();
            }
            catch (WaitHandleCannotBeOpenedException) { }
        }

        private static void OnActivateRequested(object state, bool timedOut)
        {
            if (timedOut || Current == null) return;
            Current.Dispatcher.BeginInvoke(new Action(ShowRunningInstance));
        }

        private static void ShowRunningInstance()
        {
            MainWindow window = Current.MainWindow as MainWindow;
            if (window != null) window.ShowFromTray();
        }

        protected override void OnExit(ExitEventArgs e)
        {
            if (activateWait != null) activateWait.Unregister(null);
            if (activateEvent != null) activateEvent.Dispose();
            if (instanceMutex != null)
            {
                try { instanceMutex.ReleaseMutex(); }
                catch (ApplicationException) { }
                instanceMutex.Dispose();
            }
            base.OnExit(e);
        }
    }
}
