using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using System.Web.Script.Serialization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using LiveBell;

internal static class PreviewValidation
{
    private static int assertions;
    private static void Check(bool ok, string name)
    { if (!ok) throw new Exception("FAILED: " + name); assertions++; Console.WriteLine("PASS: " + name); }

    [STAThread]
    private static int Main(string[] args)
    {
        try
        {
            Application app = new Application();
            System.Xml.XmlDocument resourceXml = new System.Xml.XmlDocument();
            resourceXml.Load(args[2]);
            app.Resources = (ResourceDictionary)System.Windows.Markup.XamlReader.Parse("<ResourceDictionary xmlns='http://schemas.microsoft.com/winfx/2006/xaml/presentation' xmlns:x='http://schemas.microsoft.com/winfx/2006/xaml'>" + resourceXml.DocumentElement.FirstChild.InnerXml + "</ResourceDictionary>");
            app.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            Assembly asm = typeof(MainWindow).Assembly;
            Check(new AppSettings().LowMemoryRendering, "low-memory rendering defaults on");
            Check(new JavaScriptSerializer().Deserialize<AppSettings>("{\"IntervalSeconds\":6}").LowMemoryRendering, "legacy settings enable memory optimization without migration");
            AppSettings renderSettings = new AppSettings { LowMemoryRendering = false };
            Check(!renderSettings.Copy().LowMemoryRendering, "copy preserves graphics opt-out");
            MethodInfo configure = asm.GetType("LiveBell.RenderingPolicy").GetMethod("Configure");
            configure.Invoke(null, new object[] { renderSettings });
            Check(RenderOptions.ProcessRenderMode == System.Windows.Interop.RenderMode.Default, "graphics opt-out restores default rendering before window creation");
            configure.Invoke(null, new object[] { new AppSettings() });
            Check(RenderOptions.ProcessRenderMode == System.Windows.Interop.RenderMode.SoftwareOnly, "optimized rendering configured before first window");
            Type data = asm.GetType("LiveBell.LocalData");
            MethodInfo save = data.GetMethod("Save");
            MethodInfo load = data.GetMethod("Load");
            string stateFile = (string)data.GetField("StateFile").GetValue(null);
            AppState first = new AppState(); first.Settings.TaskbarLabel = "backup-original";
            save.Invoke(null, new object[] { first });
            first.Settings.TaskbarLabel = "new-value";
            save.Invoke(null, new object[] { first });
            Check(File.Exists(stateFile + ".bak"), "atomic save creates backup");
            File.WriteAllText(stateFile, "{ invalid json");
            AppState recovered = (AppState)load.Invoke(null, null);
            Check(recovered.Settings.TaskbarLabel == "backup-original", "corrupt primary recovers backup");
            File.WriteAllText(stateFile + ".bak", "{ invalid backup");
            bool refused = false;
            try { load.Invoke(null, null); } catch (TargetInvocationException ex) { refused = ex.InnerException is InvalidDataException; }
            Check(refused, "both corrupt files do not silently replace follows with empty state");
            save.Invoke(null, new object[] { new AppState() });

            Type sound = asm.GetType("LiveBell.SoundService");
            FieldInfo media = sound.GetField("player", BindingFlags.Static | BindingFlags.NonPublic);
            media.SetValue(null, new MediaPlayer());
            sound.GetMethod("PlayNext", BindingFlags.Static | BindingFlags.NonPublic).Invoke(null, null);
            Check(media.GetValue(null) == null, "idle sound queue closes and releases media player");
            Check(sound.GetField("systemSoundTimer", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null) == null, "idle sound queue does not retain a timer");
            ValidateSoundLifecycle(sound, args[0]);

            Streamer state = new Streamer();
            Check(state.AcceptLiveState(true, true, false, true), "startup already live alerts once");
            Check(!state.AcceptLiveState(true, true, false, false), "next poll has no duplicate");
            Check(!state.AcceptLiveState(false, false, false, false), "unknown does not alert");
            Check(!state.AcceptLiveState(true, true, false, false), "recovery from unknown has no duplicate");
            typeof(MainWindow).GetMethod("SetConnectionError", BindingFlags.NonPublic | BindingFlags.Static).Invoke(null, new object[] { state, new Exception("test") });
            Check(!state.IsLive && state.LastKnownLive, "network error display preserves confirmed baseline");
            Check(!state.AcceptLiveState(true, true, false, false), "network recovery has no duplicate");
            Check(!state.AcceptLiveState(true, true, true, false) && !state.IsLive, "video loop never counts as live");
            Check(state.AcceptLiveState(true, true, false, false), "genuine restart after loop alerts");
            Check(!state.AcceptLiveState(false, true, false, false), "offline resets baseline without alert");
            Check(state.AcceptLiveState(true, true, false, false), "offline to live alerts");
            string serialized = new JavaScriptSerializer().Serialize(state);
            Check(!serialized.Contains("LastKnownLive") && !serialized.Contains("HasKnownState"), "session state is not persisted across launches");

            MainWindow main = new MainWindow();
            main.Streamers.Clear();
            System.Threading.SemaphoreSlim slots = (System.Threading.SemaphoreSlim)typeof(MainWindow).GetField("querySlots", BindingFlags.NonPublic | BindingFlags.Instance).GetValue(main);
            Check(slots.CurrentCount == 3, "room requests are limited to three workers");
            slots.Wait(); slots.Wait(); slots.Wait();
            Streamer waitingRoom = new Streamer { Platform = "invalid", RoomId = "guard-test" };
            main.Streamers.Add(waitingRoom);
            MethodInfo refresh = typeof(MainWindow).GetMethod("RefreshOneAsync", BindingFlags.NonPublic | BindingFlags.Instance);
            Task firstRequest = (Task)refresh.Invoke(main, new object[] { waitingRoom, false, false });
            Task secondRequest = (Task)refresh.Invoke(main, new object[] { waitingRoom, false, false });
            Check(!firstRequest.IsCompleted && secondRequest.IsCompleted, "overlapping requests for one room are skipped");
            main.Streamers.Remove(waitingRoom); slots.Release(); slots.Release(); slots.Release(); Pump();
            Check(firstRequest.IsCompleted && slots.CurrentCount == 3, "removed room is ignored and query slot is released");
            settingsAssetsTest(asm, data, stateFile, save, load);
            string demoData = args[1];
            AppState demo = new JavaScriptSerializer().Deserialize<AppState>(File.ReadAllText(Path.Combine(demoData, "开播铃数据.json")));
            foreach (Streamer streamer in demo.Streamers)
            {
                streamer.AvatarPath = Path.Combine(demoData, "avatars", streamer.Id + ".image");
                main.Streamers.Add(streamer);
                BitmapSource image = streamer.AvatarImage as BitmapSource;
                Check(image != null && image.PixelWidth <= 128, "avatar decoded at display size: " + streamer.Name);
            }
            Streamer[] samples = main.Streamers.ToArray();
            samples[0].AcceptLiveState(true, true, false, false); samples[0].Title = "今晚一起看看新游戏";
            samples[1].IsCheckingEnabled = false; samples[1].IsLive = false; samples[1].State = "检查已暂停"; samples[1].Title = "下次开播见";
            samples[2].AcceptLiveState(true, true, true, false); samples[2].Title = "精彩片段回顾";
            MethodInfo filter = typeof(MainWindow).GetMethod("Filter_Click", BindingFlags.NonPublic | BindingFlags.Instance);
            filter.Invoke(main, new object[] { new RadioButton { Tag = "live" }, new RoutedEventArgs() });
            Check(main.StreamersView.Cast<object>().Count() == 1, "live filter excludes loops and paused rooms");
            filter.Invoke(main, new object[] { new RadioButton { Tag = "paused" }, new RoutedEventArgs() });
            Check(main.StreamersView.Cast<object>().Count() == 1, "paused filter works");
            filter.Invoke(main, new object[] { new RadioButton { Tag = "all" }, new RoutedEventArgs() });
            Render(main, Path.Combine(args[0], "main.png"));
            Check(!Descendants(main).OfType<TextBlock>().Any(x => x.Text == "关注列表"), "large list heading is removed");
            Check(!Descendants(main).OfType<TextBox>().Any(), "main-window search box is removed");
            ValidateCompactToolbar(main);

            Button more = Descendants(main).OfType<Button>().First(x => (x.Content as string) == "···");
            more.RaiseEvent(new RoutedEventArgs(Button.ClickEvent));
            Pump();
            Check(more.ContextMenu.DataContext == more.DataContext && ((MenuItem)more.ContextMenu.Items[0]).DataContext == more.DataContext, "more menu keeps correct streamer context");
            more.ContextMenu.IsOpen = false;
            main.Width = 870;
            Render(main, Path.Combine(args[0], "main-small.png"));
            ValidateCompactToolbar(main);

            Type room = asm.GetType("LiveBell.RoomResult");
            object result = Activator.CreateInstance(room, true);
            room.GetField("ok").SetValue(result, true); room.GetField("isLive").SetValue(result, false); room.GetField("liveConfirmed").SetValue(result, true);
            room.GetField("avatarUrl").SetValue(result, "http://127.0.0.1:1/missing-avatar");
            Stopwatch watch = Stopwatch.StartNew();
            Task apply = (Task)typeof(MainWindow).GetMethod("ApplyResultAsync", BindingFlags.NonPublic | BindingFlags.Instance).Invoke(main, new object[] { samples[0], result, false, false });
            Check(apply.IsCompleted && watch.ElapsedMilliseconds < 1000 && samples[0].State == "未开播", "status updates without waiting for avatar download");
            Pump();
            Check(samples[0].State == "未开播", "avatar failure does not become connection error");

            AppSettings settings = new AppSettings();
            SettingsWindow dialog = new SettingsWindow(settings);
            Check(((CheckBox)dialog.FindName("LowMemoryBox")).IsChecked == true, "settings show default low-memory option");
            Render(dialog, Path.Combine(args[0], "settings.png"));
            TabControl tabs = (TabControl)dialog.FindName("SettingsTabs");
            for (int i = 1; i < tabs.Items.Count; i++) { tabs.SelectedIndex = i; Render(dialog, Path.Combine(args[0], "settings-" + i + ".png")); }
            dialog.Height = 550; tabs.SelectedIndex = 0;
            Render(dialog, Path.Combine(args[0], "settings-small.png"));
            ValidateScrollBars(dialog, args[0]);
            dialog.Close();
            AddStreamerWindow add = new AddStreamerWindow(); Render(add, Path.Combine(args[0], "add.png")); add.Close();
            LiveToast toast = new LiveToast(samples[0], 8, true); Render(toast, Path.Combine(args[0], "toast.png"));
            Check(!toast.ShowActivated && !toast.ShowInTaskbar && !toast.Focusable, "software toast retains non-activating flags");
            toast.Close();
            Console.WriteLine("TOTAL PASS: " + assertions);
            typeof(MainWindow).GetField("exiting", BindingFlags.NonPublic | BindingFlags.Instance).SetValue(main, true);
            main.Close(); app.Shutdown();
            return 0;
        }
        catch (Exception ex) { Console.WriteLine(ex); return 1; }
    }
    private static void ValidateCompactToolbar(MainWindow main)
    {
        TextBlock summary = (TextBlock)main.FindName("SummaryText");
        Button refresh = Descendants(main).OfType<Button>().First(x => (x.Content as string) == "刷新");
        RadioButton filter = Descendants(main).OfType<RadioButton>().First(x => (string)x.Tag == "all");
        Point summaryTop = summary.TranslatePoint(new Point(0, 0), main);
        Point summaryBottom = summary.TranslatePoint(new Point(0, summary.ActualHeight), main);
        Point filterBottom = filter.TranslatePoint(new Point(0, filter.ActualHeight), main);
        Point refreshBottom = refresh.TranslatePoint(new Point(0, refresh.ActualHeight), main);
        DataGrid list = (DataGrid)main.FindName("StreamersGrid");
        Border card = (Border)((FrameworkElement)list.Parent).Parent;
        Point cardTop = card.TranslatePoint(new Point(0, 0), main);
        TextBlock filterLabel = Descendants(filter).OfType<TextBlock>().First();
        Point labelStart = filterLabel.TranslatePoint(new Point(0, 0), main);
        Check(summaryTop.Y >= Math.Max(filterBottom.Y, refreshBottom.Y) + 6 && summaryBottom.Y <= cardTop.Y - 4 && Math.Abs(summaryTop.X - labelStart.X) < 1, "summary is below toolbar and aligned with filter text at width " + main.Width);
    }
    private static void ValidateScrollBars(SettingsWindow dialog, string output)
    {
        ScrollViewer scroll = Descendants(dialog).OfType<ScrollViewer>().First(x => x.ScrollableHeight > 0);
        System.Windows.Controls.Primitives.ScrollBar bar = Descendants(scroll).OfType<System.Windows.Controls.Primitives.ScrollBar>().First(x => x.Orientation == Orientation.Vertical && x.IsVisible);
        Check(bar.ActualWidth <= 18, "vertical scrollbar keeps a usable narrow hit area");
        System.Windows.Controls.Primitives.Track track = (System.Windows.Controls.Primitives.Track)bar.Template.FindName("PART_Track", bar);
        Check(track != null && track.Thumb != null, "rounded scrollbar retains real draggable track");
        Check(Math.Abs(track.Thumb.ActualWidth - 6) < .1, "vertical thumb is a slim 6px capsule");
        scroll.ScrollToTop(); Pump();
        scroll.RaiseEvent(new System.Windows.Input.MouseWheelEventArgs(System.Windows.Input.Mouse.PrimaryDevice, Environment.TickCount, -120) { RoutedEvent = System.Windows.Input.Mouse.MouseWheelEvent });
        Pump(); Check(scroll.VerticalOffset > 0, "mouse wheel scrolls settings content");
        scroll.ScrollToEnd(); Pump(); double end = scroll.VerticalOffset;
        System.Windows.Controls.Primitives.ScrollBar.PageUpCommand.Execute(null, bar); Pump();
        Check(scroll.VerticalOffset < end, "track page-up command scrolls content");
        scroll.ScrollToVerticalOffset(scroll.ScrollableHeight / 2); Pump(); double before = scroll.VerticalOffset;
        track.Thumb.RaiseEvent(new System.Windows.Controls.Primitives.DragStartedEventArgs(0, 0) { RoutedEvent = System.Windows.Controls.Primitives.Thumb.DragStartedEvent });
        track.Thumb.RaiseEvent(new System.Windows.Controls.Primitives.DragDeltaEventArgs(0, 20) { RoutedEvent = System.Windows.Controls.Primitives.Thumb.DragDeltaEvent });
        track.Thumb.RaiseEvent(new System.Windows.Controls.Primitives.DragCompletedEventArgs(0, 20, false) { RoutedEvent = System.Windows.Controls.Primitives.Thumb.DragCompletedEvent });
        Pump(); Check(Math.Abs(scroll.VerticalOffset - before) > .1, "thumb drag changes content offset");
        scroll.ScrollToTop(); Pump(); Render(dialog, Path.Combine(output, "settings-small.png"));

        ScrollViewer horizontal = new ScrollViewer { HorizontalScrollBarVisibility = ScrollBarVisibility.Auto, VerticalScrollBarVisibility = ScrollBarVisibility.Disabled, Content = new Border { Width = 600, Height = 40, Background = Brushes.White } };
        Window host = new Window { Width = 260, Height = 160, Content = horizontal };
        Render(host, Path.Combine(output, "scroll-horizontal.png"));
        System.Windows.Controls.Primitives.ScrollBar horizontalBar = Descendants(horizontal).OfType<System.Windows.Controls.Primitives.ScrollBar>().First(x => x.Orientation == Orientation.Horizontal && x.IsVisible);
        System.Windows.Controls.Primitives.Track horizontalTrack = (System.Windows.Controls.Primitives.Track)horizontalBar.Template.FindName("PART_Track", horizontalBar);
        Check(Math.Abs(horizontalTrack.Thumb.ActualHeight - 6) < .1, "horizontal scrollbar has matching 6px capsule");
        horizontal.ScrollToHorizontalOffset(100); Pump();
        Check(horizontal.HorizontalOffset > 0, "horizontal scrolling remains functional");
        host.Close();
    }
    private static void settingsAssetsTest(Assembly asm, Type data, string stateFile, MethodInfo save, MethodInfo load)
    {
        string appearance = (string)data.GetField("AppearanceFolder").GetValue(null);
        Directory.CreateDirectory(appearance);
        string local = Path.Combine(appearance, "portable-test.png");
        File.Copy(Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "Assets", "avatar-icon.png"), local, true);
        AppState portable = new AppState(); portable.Settings.CustomAvatarPath = "D:\\old-folder\\Data\\appearance\\portable-test.png";
        save.Invoke(null, new object[] { portable });
        AppState moved = (AppState)load.Invoke(null, null);
        Check(moved.Settings.CustomAvatarPath == local, "moved Data uses local avatar rather than old absolute path");
        BitmapSource decoded = (BitmapSource)asm.GetType("LiveBell.ImageTools").GetMethod("Load").Invoke(null, new object[] { local });
        Check(decoded.IsFrozen && decoded.PixelWidth <= 128, "avatar loaded from stream is frozen and bounded");
        using (FileStream unlocked = new FileStream(local, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Check(unlocked.Length > 0, "decoded avatar does not retain a file handle");
        portable.Settings.LowMemoryRendering = false;
        save.Invoke(null, new object[] { portable });
        Check(!((AppState)load.Invoke(null, null)).Settings.LowMemoryRendering, "graphics opt-out persists across launches");
        save.Invoke(null, new object[] { new AppState() });
    }

    private static void ValidateSoundLifecycle(Type sound, string output)
    {
        // Short silent WAVs exercise the real playback/queue callbacks without
        // audible notifications or changes to the user's sound selection.
        string wav = Path.Combine(output, "silent-test.wav");
        const int samples = 4410;
        using (BinaryWriter writer = new BinaryWriter(File.Create(wav)))
        {
            writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + samples * 2);
            writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16);
            writer.Write((short)1); writer.Write((short)1); writer.Write(44100); writer.Write(88200);
            writer.Write((short)2); writer.Write((short)16); writer.Write(System.Text.Encoding.ASCII.GetBytes("data"));
            writer.Write(samples * 2); for (int i = 0; i < samples; i++) writer.Write((short)0);
        }
        FieldInfo media = sound.GetField("player", BindingFlags.Static | BindingFlags.NonPublic);
        FieldInfo playing = sound.GetField("playing", BindingFlags.Static | BindingFlags.NonPublic);
        MethodInfo play = sound.GetMethod("Play");
        HashSet<MediaPlayer> observed = new HashSet<MediaPlayer>();
        int opened = 0, ended = 0, failed = 0;
        play.Invoke(null, new object[] { wav }); play.Invoke(null, new object[] { wav });
        Stopwatch wait = Stopwatch.StartNew();
        while (wait.ElapsedMilliseconds < 10000)
        {
            MediaPlayer current = media.GetValue(null) as MediaPlayer;
            if (current != null && observed.Add(current))
            {
                current.MediaOpened += delegate { opened++; };
                current.MediaEnded += delegate { ended++; };
                current.MediaFailed += delegate { failed++; };
            }
            Pump();
            if (!(bool)playing.GetValue(null)) break;
            System.Threading.Thread.Sleep(10);
        }
        Check(opened == 2 && ended == 2 && failed == 0, "two real custom sounds finish in queue order");
        Check(media.GetValue(null) == null && !(bool)playing.GetValue(null), "player released after real playback completes");
    }
    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (int i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        { DependencyObject child = VisualTreeHelper.GetChild(root, i); yield return child; foreach (DependencyObject inner in Descendants(child)) yield return inner; }
    }
    private static void Pump()
    { System.Windows.Threading.Dispatcher.CurrentDispatcher.Invoke(DispatcherPriority.ApplicationIdle, new Action(delegate { })); }
    private static void Render(Window window, string path)
    {
        window.WindowStartupLocation = WindowStartupLocation.Manual;
        window.Left = -10000; window.Top = -10000; window.ShowActivated = false; window.ShowInTaskbar = false;
        if (!window.IsVisible) window.Show();
        // LiveToast deliberately positions itself on Loaded. Move it back offscreen.
        window.Left = -10000; window.Top = -10000;
        Pump(); window.UpdateLayout();
        RenderTargetBitmap bitmap = new RenderTargetBitmap((int)window.ActualWidth, (int)window.ActualHeight, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(window);
        PngBitmapEncoder encoder = new PngBitmapEncoder(); encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using (FileStream stream = File.Create(path)) encoder.Save(stream);
        Console.WriteLine("RENDER: " + Path.GetFileName(path));
    }
}
