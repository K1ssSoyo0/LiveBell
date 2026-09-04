using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using Forms = System.Windows.Forms;

namespace LiveBell
{
    public partial class MainWindow : Window, INotifyPropertyChanged
    {
        private readonly AppState state;
        private readonly Forms.NotifyIcon tray;
        private readonly DispatcherTimer monitorTimer;
        private bool checking;
        private bool exiting;
        private Streamer selectedStreamer;
        private Streamer latestWindowsNoticeStreamer;

        public ObservableCollection<Streamer> Streamers { get; private set; }
        public Streamer SelectedStreamer
        {
            get { return selectedStreamer; }
            set { selectedStreamer = value; Changed("SelectedStreamer"); }
        }

        public MainWindow()
        {
            InitializeComponent();
            DataContext = this;
            Directory.CreateDirectory(LocalData.AvatarFolder);
            state = LocalData.Load();
            Streamers = new ObservableCollection<Streamer>(state.Streamers);
            foreach (Streamer streamer in Streamers)
            {
                string avatar = LocalData.AvatarFile(streamer);
                streamer.AvatarPath = File.Exists(avatar) ? avatar : "";
            }

            tray = new Forms.NotifyIcon();
            tray.Icon = TrayIconFactory.GetAvatar();
            tray.Text = "开播铃";
            Forms.ContextMenuStrip menu = new Forms.ContextMenuStrip();
            menu.Items.Add("打开开播铃", null, delegate { ShowFromTray(); });
            menu.Items.Add("立即刷新", null, async delegate { ShowFromTray(); await RefreshAllAsync(true); });
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("退出", null, delegate { ExitApplication(); });
            tray.ContextMenuStrip = menu;
            tray.Visible = true;
            tray.DoubleClick += delegate { ShowFromTray(); };
            tray.BalloonTipClicked += delegate { OpenStreamer(latestWindowsNoticeStreamer); };

            monitorTimer = new DispatcherTimer();
            monitorTimer.Tick += async delegate { await RefreshAllAsync(false); };
            monitorTimer.Interval = TimeSpan.FromSeconds(Math.Max(6, state.Settings.IntervalSeconds));
            Closing += MainWindow_Closing;
            UpdateStatus();
        }

        public void StartMonitoring()
        {
            monitorTimer.Start();
            // Only the rooms that already exist when the program starts may send
            // the startup reminder.  A later-added room sends its own reminder,
            // but must not be treated as another "first pass" by the timer.
            RefreshAllAsync(false, true);
        }

        private async void Add_Click(object sender, RoutedEventArgs e)
        {
            AddStreamerWindow dialog = new AddStreamerWindow();
            dialog.Owner = this;
            if (dialog.ShowDialog() != true) return;
            if (Streamers.Any(x => x.Platform == dialog.Platform && x.RoomId == dialog.RoomId))
            {
                MessageBox.Show(this, "这个直播间已经在关注列表中。", "开播铃", MessageBoxButton.OK, MessageBoxImage.Information);
                return;
            }
            Streamer streamer = new Streamer();
            streamer.Platform = dialog.Platform;
            streamer.RoomId = dialog.RoomId;
            streamer.Name = dialog.Platform == "douyu" ? "斗鱼主播" : "B站主播";
            Streamers.Add(streamer);
            state.Streamers.Add(streamer);
            Save();
            UpdateStatus();
            await RefreshOneAsync(streamer, true, true);
        }

        private async void Refresh_Click(object sender, RoutedEventArgs e)
        {
            await RefreshAllAsync(true);
        }

        private async void RefreshOne_Click(object sender, RoutedEventArgs e)
        {
            Button button = sender as Button;
            Streamer streamer = button == null ? null : button.DataContext as Streamer;
            if (streamer != null) await RefreshOneAsync(streamer, false);
            UpdateStatus();
        }

        private async void ToggleChecking_Click(object sender, RoutedEventArgs e)
        {
            Button button = sender as Button;
            Streamer streamer = button == null ? null : button.DataContext as Streamer;
            if (streamer == null) return;
            if (streamer.IsCheckingEnabled)
            {
                streamer.IsCheckingEnabled = false;
                streamer.IsLive = false;
                streamer.HasChecked = false;
                streamer.State = "检查已暂停";
                streamer.LastError = "";
                Save();
                UpdateStatus();
                return;
            }
            streamer.IsCheckingEnabled = true;
            streamer.State = "读取中";
            Save();
            UpdateStatus();
            await RefreshOneAsync(streamer, true, true);
            UpdateStatus();
        }

        private void Sound_Click(object sender, RoutedEventArgs e)
        {
            Button button = sender as Button;
            Streamer streamer = button == null ? null : button.DataContext as Streamer;
            if (streamer == null) return;
            StreamerSoundWindow dialog = new StreamerSoundWindow(streamer, state.Settings.CustomSoundPath);
            dialog.Owner = this;
            if (dialog.ShowDialog() != true) return;
            streamer.CustomSoundPath = dialog.CustomSoundPath;
            Save();
        }

        private void Open_Click(object sender, RoutedEventArgs e)
        {
            Button button = sender as Button;
            Streamer streamer = button == null ? null : button.DataContext as Streamer;
            OpenStreamer(streamer);
        }

        private void Delete_Click(object sender, RoutedEventArgs e)
        {
            Button button = sender as Button;
            Streamer streamer = button == null ? null : button.DataContext as Streamer;
            if (streamer == null) return;
            ConfirmDeleteWindow confirm = new ConfirmDeleteWindow(streamer.Name);
            confirm.Owner = this;
            if (confirm.ShowDialog() != true) return;
            Streamers.Remove(streamer);
            state.Streamers.Remove(streamer);
            Save();
            UpdateStatus();
        }

        private async void Grid_DoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (SelectedStreamer != null) await RefreshOneAsync(SelectedStreamer, false);
        }

        private void Settings_Click(object sender, RoutedEventArgs e)
        {
            SettingsWindow dialog = new SettingsWindow(state.Settings);
            dialog.Owner = this;
            if (dialog.ShowDialog() != true) return;
            try { AutoStartService.Set(dialog.Value.AutoStart); }
            catch (Exception ex)
            {
                MessageBox.Show(this, ex.Message, "开播铃", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            state.Settings = dialog.Value;
            monitorTimer.Interval = TimeSpan.FromSeconds(Math.Max(6, state.Settings.IntervalSeconds));
            Save();
            UpdateStatus();
        }

        private async System.Threading.Tasks.Task RefreshAllAsync(bool manual, bool notifyAlreadyLive = false)
        {
            if (checking) return;
            checking = true;
            StatusText.Text = "正在刷新直播间…";
            Streamer[] copy = Streamers.Where(x => x.IsCheckingEnabled).ToArray();
            try
            {
                if (copy.Length == 0) return;
                List<RoomResult> results = await RoomClient.QueryManyAsync(copy);
                for (int i = 0; i < copy.Length; i++)
                {
                    try
                    {
                        RoomResult result = i < results.Count ? results[i] : null;
                        await ApplyResultAsync(copy[i], result, true, notifyAlreadyLive);
                    }
                    catch (Exception ex)
                    {
                        SetConnectionError(copy[i], ex);
                    }
                }
                if (copy.Length > 0) Save();
            }
            catch (Exception ex)
            {
                foreach (Streamer streamer in copy) SetConnectionError(streamer, ex);
                if (copy.Length > 0) Save();
            }
            finally
            {
                checking = false;
                UpdateStatus();
            }
        }

        private async System.Threading.Tasks.Task RefreshOneAsync(Streamer streamer, bool sendNotice, bool notifyAlreadyLive = false)
        {
            if (!Streamers.Contains(streamer)) return;
            streamer.State = "读取中";
            try
            {
                RoomResult result = await RoomClient.QueryAsync(streamer);
                await ApplyResultAsync(streamer, result, sendNotice, notifyAlreadyLive);
                Save();
            }
            catch (Exception ex)
            {
                SetConnectionError(streamer, ex);
            }
        }

        private async System.Threading.Tasks.Task ApplyResultAsync(Streamer streamer, RoomResult result, bool sendNotice, bool notifyAlreadyLive)
        {
            if (result == null || !result.ok)
                throw new InvalidOperationException(result == null ? "平台没有返回直播间数据。" : Value(result.message, "平台暂时无法读取直播间。"));
            string oldAvatarUrl = streamer.AvatarUrl ?? "";
            streamer.RoomId = Value(result.roomId, streamer.RoomId);
            streamer.Name = Value(result.name, streamer.Name);
            streamer.Title = Value(result.title, "主播暂未填写直播标题");
            streamer.Url = Value(result.url, streamer.Url);
            streamer.AvatarUrl = Value(result.avatarUrl, streamer.AvatarUrl);
            streamer.LastError = "";
            string avatar = LocalData.AvatarFile(streamer);
            if (!String.IsNullOrWhiteSpace(streamer.AvatarUrl) && (oldAvatarUrl != streamer.AvatarUrl || !File.Exists(avatar)))
                    await RoomClient.DownloadAvatarAsync(streamer.AvatarUrl, avatar);
            if (File.Exists(avatar)) streamer.AvatarPath = avatar;

            if (!streamer.IsCheckingEnabled)
            {
                streamer.IsLive = false;
                streamer.HasChecked = false;
                streamer.State = "检查已暂停";
                return;
            }

            bool isActualLive = result.liveConfirmed && !result.videoLoop && result.isLive;
            bool shouldNotify = sendNotice && isActualLive && (notifyAlreadyLive || !streamer.HasChecked || !streamer.IsLive);
            streamer.IsLive = isActualLive;
            streamer.HasChecked = result.liveConfirmed;
            if (result.videoLoop) streamer.State = "视频轮播";
            else if (!result.liveConfirmed) streamer.State = "状态待确认";
            else streamer.State = isActualLive ? "直播中" : "未开播";
            if (shouldNotify) SendNotification(streamer);
        }

        private static void SetConnectionError(Streamer streamer, Exception ex)
        {
            if (!streamer.IsCheckingEnabled)
            {
                streamer.State = "检查已暂停";
                return;
            }
            streamer.LastError = ex == null ? "" : ex.Message;
            streamer.State = "连接异常";
            streamer.Title = "暂时无法读取直播间";
        }

        private void SendNotification(Streamer streamer)
        {
            string soundPath = String.IsNullOrWhiteSpace(streamer.CustomSoundPath) ? state.Settings.CustomSoundPath : streamer.CustomSoundPath;
            SoundService.Play(soundPath);
            if (state.Settings.WindowsNotification)
            {
                latestWindowsNoticeStreamer = streamer;
                tray.BalloonTipTitle = streamer.Name + " 开播了";
                tray.BalloonTipText = streamer.Title;
                tray.ShowBalloonTip(8000);
            }
            else
            {
                LiveToast toast = new LiveToast(streamer, state.Settings.ToastSeconds);
                toast.Show();
            }
        }

        private static void OpenStreamer(Streamer streamer)
        {
            if (streamer == null) return;
            string url = String.IsNullOrWhiteSpace(streamer.Url)
                ? (streamer.Platform == "douyu" ? "https://www.douyu.com/" : "https://live.bilibili.com/") + streamer.RoomId
                : streamer.Url;
            try { Process.Start(new ProcessStartInfo(url) { UseShellExecute = true }); } catch { }
        }

        private void UpdateStatus()
        {
            EmptyState.Visibility = Streamers.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            int checkingCount = Streamers.Count(x => x.IsCheckingEnabled);
            int live = Streamers.Count(x => x.IsCheckingEnabled && x.IsLive);
            StatusText.Text = "正在检查 " + checkingCount + " / " + Streamers.Count + " 位主播 · " + live + " 位直播中";
        }

        private void Save()
        {
            LocalData.Save(state);
        }

        private static string Value(string value, string fallback)
        {
            return String.IsNullOrWhiteSpace(value) ? fallback : value.Trim();
        }

        private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed) DragMove();
        }

        private void Minimize_Click(object sender, RoutedEventArgs e)
        {
            WindowState = WindowState.Minimized;
        }

        private void Close_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }

        private void ShowFromTray()
        {
            Show();
            WindowState = WindowState.Normal;
            Activate();
            Topmost = true;
            Topmost = false;
        }

        private void MainWindow_Closing(object sender, CancelEventArgs e)
        {
            if (!exiting && state.Settings.ResidentInTray)
            {
                e.Cancel = true;
                Hide();
                return;
            }
            monitorTimer.Stop();
            tray.Visible = false;
            tray.Dispose();
        }

        private void ExitApplication()
        {
            exiting = true;
            Close();
            Application.Current.Shutdown();
        }

        private void Changed(string property)
        {
            PropertyChangedEventHandler changed = PropertyChanged;
            if (changed != null) changed(this, new PropertyChangedEventArgs(property));
        }

        public event PropertyChangedEventHandler PropertyChanged;
    }
}
