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
using System.Windows.Data;
using System.Threading;
using System.Threading.Tasks;
using Forms = System.Windows.Forms;

namespace LiveBell
{
    public partial class MainWindow : Window, INotifyPropertyChanged
    {
        private readonly AppState state;
        private readonly Forms.NotifyIcon tray;
        private readonly Forms.ToolStripItem trayOpenItem;
        private readonly DispatcherTimer monitorTimer;
        private bool checking;
        private bool exiting;
        private Streamer selectedStreamer;
        private Streamer latestWindowsNoticeStreamer;
        private readonly SemaphoreSlim querySlots = new SemaphoreSlim(3);
        private readonly SemaphoreSlim avatarSlots = new SemaphoreSlim(2);
        private readonly HashSet<string> pendingRooms = new HashSet<string>();
        private readonly HashSet<string> pendingAvatars = new HashSet<string>();
        private readonly Dictionary<string, DateTime> avatarRetryAfter = new Dictionary<string, DateTime>();
        private string filterMode = "all";
        private string saveError = "";

        public ObservableCollection<Streamer> Streamers { get; private set; }
        public ICollectionView StreamersView { get; private set; }
        public Streamer SelectedStreamer
        {
            get { return selectedStreamer; }
            set { selectedStreamer = value; Changed("SelectedStreamer"); }
        }

        public MainWindow()
        {
            InitializeComponent();
            Directory.CreateDirectory(LocalData.AvatarFolder);
            state = LocalData.Load();
            Streamers = new ObservableCollection<Streamer>(state.Streamers);
            foreach (Streamer streamer in Streamers)
            {
                streamer.IsLive = false;
                streamer.HasKnownState = false;
                streamer.State = streamer.IsCheckingEnabled ? "等待检查" : "检查已暂停";
                string avatar = LocalData.AvatarFile(streamer);
                streamer.AvatarPath = File.Exists(avatar) ? avatar : "";
            }
            StreamersView = CollectionViewSource.GetDefaultView(Streamers);
            StreamersView.Filter = MatchesFilter;
            DataContext = this;

            tray = new Forms.NotifyIcon();
            tray.Icon = TrayIconFactory.GetAvatar(state.Settings.CustomAvatarPath);
            Forms.ContextMenuStrip menu = new Forms.ContextMenuStrip();
            trayOpenItem = menu.Items.Add("打开开播铃", null, delegate { ShowFromTray(); });
            menu.Items.Add("立即刷新", null, async delegate { ShowFromTray(); await RefreshAllAsync(true); });
            menu.Items.Add(new Forms.ToolStripSeparator());
            menu.Items.Add("退出", null, delegate { ExitApplication(); });
            tray.ContextMenuStrip = menu;
            tray.Visible = true;
            tray.DoubleClick += delegate { ShowFromTray(); };
            tray.BalloonTipClicked += delegate { OpenStreamer(latestWindowsNoticeStreamer); };
            ApplyAppearance();

            monitorTimer = new DispatcherTimer();
            monitorTimer.Tick += async delegate { await RefreshAllAsync(false); };
            monitorTimer.Interval = TimeSpan.FromSeconds(Math.Max(6, state.Settings.IntervalSeconds));
            Closing += MainWindow_Closing;
            UpdateStatus();
        }

        public async void StartMonitoring()
        {
            monitorTimer.Start();
            // Only the rooms that already exist when the program starts may send
            // the startup reminder.  A later-added room sends its own reminder,
            // but must not be treated as another "first pass" by the timer.
            await RefreshAllAsync(false, true);
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
            FrameworkElement button = sender as FrameworkElement;
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
                streamer.HasKnownState = false;
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
            FrameworkElement button = sender as FrameworkElement;
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
            FrameworkElement button = sender as FrameworkElement;
            Streamer streamer = button == null ? null : button.DataContext as Streamer;
            OpenStreamer(streamer);
        }

        private void Delete_Click(object sender, RoutedEventArgs e)
        {
            FrameworkElement button = sender as FrameworkElement;
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

        private void Grid_DoubleClick(object sender, MouseButtonEventArgs e)
        {
            DependencyObject element = e.OriginalSource as DependencyObject;
            while (element != null && !(element is DataGridRow))
            {
                if (element is Button) return;
                element = System.Windows.Media.VisualTreeHelper.GetParent(element);
            }
            DataGridRow row = ItemsControl.ContainerFromElement(StreamersGrid, e.OriginalSource as DependencyObject) as DataGridRow;
            if (row != null) OpenStreamer(row.Item as Streamer);
        }

        private void More_Click(object sender, RoutedEventArgs e)
        {
            Button button = sender as Button;
            if (button == null || button.ContextMenu == null) return;
            button.ContextMenu.PlacementTarget = button;
            button.ContextMenu.IsOpen = true;
        }

        private bool MatchesFilter(object item)
        {
            Streamer streamer = item as Streamer;
            if (streamer == null) return false;
            if (filterMode == "live" && !streamer.IsLive) return false;
            if (filterMode == "paused" && streamer.IsCheckingEnabled) return false;
            return true;
        }

        private void Filter_Click(object sender, RoutedEventArgs e)
        {
            FrameworkElement button = sender as FrameworkElement;
            filterMode = button == null ? "all" : (string)button.Tag;
            UpdateStatus();
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
            ApplyAppearance();
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
                await Task.WhenAll(copy.Select(x => RefreshOneAsync(x, true, notifyAlreadyLive)));
            }
            finally
            {
                checking = false;
                UpdateStatus();
            }
        }

        private async System.Threading.Tasks.Task RefreshOneAsync(Streamer streamer, bool sendNotice, bool notifyAlreadyLive = false)
        {
            if (!Streamers.Contains(streamer) || !streamer.IsCheckingEnabled || !pendingRooms.Add(streamer.Id)) return;
            if (!streamer.HasKnownState) streamer.State = "读取中";
            try
            {
                RoomResult result;
                await querySlots.WaitAsync();
                try
                {
                    if (exiting || !Streamers.Contains(streamer) || !streamer.IsCheckingEnabled) return;
                    result = await RoomClient.QueryAsync(streamer);
                }
                finally { querySlots.Release(); }
                if (exiting || !Streamers.Contains(streamer)) return;
                await ApplyResultAsync(streamer, result, sendNotice, notifyAlreadyLive);
                Save();
            }
            catch (Exception ex)
            {
                if (Streamers.Contains(streamer)) SetConnectionError(streamer, ex);
            }
            finally { pendingRooms.Remove(streamer.Id); if (!exiting) UpdateStatus(); }
        }

        private System.Threading.Tasks.Task ApplyResultAsync(Streamer streamer, RoomResult result, bool sendNotice, bool notifyAlreadyLive)
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
            if (!streamer.IsCheckingEnabled)
            {
                streamer.IsLive = false;
                streamer.HasChecked = false;
                streamer.State = "检查已暂停";
                return Task.FromResult(0);
            }

            bool shouldNotify = streamer.AcceptLiveState(result.isLive, result.liveConfirmed, result.videoLoop, notifyAlreadyLive);
            if (sendNotice && shouldNotify) SendNotification(streamer);
            UpdateAvatarInBackground(streamer, oldAvatarUrl != streamer.AvatarUrl);
            return Task.FromResult(0);
        }

        private async void UpdateAvatarInBackground(Streamer streamer, bool changedUrl)
        {
            string avatar = LocalData.AvatarFile(streamer);
            if (File.Exists(avatar) && !changedUrl) { streamer.AvatarPath = avatar; return; }
            DateTime retry;
            if (String.IsNullOrWhiteSpace(streamer.AvatarUrl) || (!changedUrl && avatarRetryAfter.TryGetValue(streamer.Id, out retry) && retry > DateTime.UtcNow) || !pendingAvatars.Add(streamer.Id)) return;
            try
            {
                await avatarSlots.WaitAsync();
                try
                {
                    if (exiting || !Streamers.Contains(streamer)) return;
                    await RoomClient.DownloadAvatarAsync(streamer.AvatarUrl, avatar);
                }
                finally { avatarSlots.Release(); }
                if (!exiting && Streamers.Contains(streamer))
                {
                    bool samePath = streamer.AvatarPath == avatar;
                    streamer.AvatarPath = avatar;
                    if (samePath) streamer.LoadAvatar();
                }
                avatarRetryAfter.Remove(streamer.Id);
            }
            catch { avatarRetryAfter[streamer.Id] = DateTime.UtcNow.AddMinutes(5); }
            finally { pendingAvatars.Remove(streamer.Id); }
        }

        private static void SetConnectionError(Streamer streamer, Exception ex)
        {
            if (!streamer.IsCheckingEnabled)
            {
                streamer.State = "检查已暂停";
                return;
            }
            streamer.LastError = ex == null ? "" : ex.Message;
            streamer.IsLive = false;
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
            if (Streamers == null || StreamersView == null) return;
            StreamersView.Refresh();
            EmptyState.Visibility = StreamersView.IsEmpty ? Visibility.Visible : Visibility.Collapsed;
            EmptyTitle.Text = Streamers.Count == 0 ? "从第一位主播开始" : "这个分类暂无主播";
            EmptyHint.Text = Streamers.Count == 0 ? "添加房间号或直播间网址，开播时就会提醒你" : "试试切换到全部关注";
            int checkingCount = Streamers.Count(x => x.IsCheckingEnabled);
            int live = Streamers.Count(x => x.IsCheckingEnabled && x.IsLive);
            SummaryText.Text = Streamers.Count + " 位关注  ·  " + live + " 位直播中";
            StatusText.Text = saveError.Length > 0 ? saveError : (checking ? "正在更新直播间状态…" : "正在监听 " + checkingCount + " 位主播  ·  每 " + state.Settings.IntervalSeconds + " 秒检查");
        }

        private void Save()
        {
            try { LocalData.Save(state); saveError = ""; }
            catch { saveError = "设置未能保存，请检查文件夹是否可写"; }
        }

        private void ApplyAppearance()
        {
            string taskbarLabel = String.IsNullOrWhiteSpace(state.Settings.TaskbarLabel)
                ? "开播铃"
                : state.Settings.TaskbarLabel.Trim();
            string avatarPath = !String.IsNullOrWhiteSpace(state.Settings.CustomAvatarPath) && File.Exists(state.Settings.CustomAvatarPath)
                ? state.Settings.CustomAvatarPath
                : LocalData.DefaultAppAvatar;
            System.Windows.Media.ImageSource avatarImage = ImageTools.Load(avatarPath);

            Title = taskbarLabel;
            HeaderSubtitleText.Text = state.Settings.HeaderSubtitle ?? "";
            if (avatarImage != null)
            {
                HeaderAvatarBrush.ImageSource = avatarImage;
                Icon = avatarImage;
            }
            tray.Icon = TrayIconFactory.GetAvatar(avatarPath);
            tray.Text = taskbarLabel.Length > 63 ? taskbarLabel.Substring(0, 63) : taskbarLabel;
            trayOpenItem.Text = "打开" + taskbarLabel;
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

        internal void ShowFromTray()
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
            exiting = true;
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
