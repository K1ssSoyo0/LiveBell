using System;
using System.IO;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;

namespace LiveBell
{
    public partial class SettingsWindow : Window
    {
        public AppSettings Value { get; private set; }

        public SettingsWindow(AppSettings current)
        {
            InitializeComponent();
            Value = current.Copy();
            SoftwareNoticeBox.IsChecked = !Value.WindowsNotification;
            WindowsNoticeBox.IsChecked = Value.WindowsNotification;
            AutoStartBox.IsChecked = Value.AutoStart;
            ResidentBox.IsChecked = Value.ResidentInTray;
            IntervalBox.Text = Value.IntervalSeconds.ToString();
            ToastSecondsBox.Text = Value.ToastSeconds.ToString();
            HeaderSubtitleBox.Text = Value.HeaderSubtitle ?? "";
            TaskbarLabelBox.Text = String.IsNullOrWhiteSpace(Value.TaskbarLabel) ? "开播铃" : Value.TaskbarLabel;
            UpdateSoundName();
            UpdateAvatarPreview();
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            int interval;
            if (!Int32.TryParse(IntervalBox.Text, out interval) || interval < 6 || interval > 3600)
            {
                ErrorText.Text = "刷新间隔请输入 6 到 3600 之间的数字。";
                return;
            }
            int toastSeconds;
            if (!Int32.TryParse(ToastSecondsBox.Text, out toastSeconds) || toastSeconds < 3 || toastSeconds > 3600)
            {
                ErrorText.Text = "提醒停留时间请输入 3 到 3600 之间的数字。";
                return;
            }
            string taskbarLabel = (TaskbarLabelBox.Text ?? "").Trim();
            if (taskbarLabel.Length == 0)
            {
                ErrorText.Text = "任务栏与托盘标签不能为空。";
                return;
            }
            Value.WindowsNotification = WindowsNoticeBox.IsChecked == true;
            Value.AutoStart = AutoStartBox.IsChecked == true;
            Value.ResidentInTray = ResidentBox.IsChecked == true;
            Value.IntervalSeconds = interval;
            Value.ToastSeconds = toastSeconds;
            Value.HeaderSubtitle = (HeaderSubtitleBox.Text ?? "").Trim();
            Value.TaskbarLabel = taskbarLabel;
            DialogResult = true;
        }

        private void ChooseAvatar_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog picker = new OpenFileDialog();
            picker.Title = "选择软件头像";
            picker.Filter = "图片文件|*.png;*.jpg;*.jpeg;*.bmp;*.gif|所有文件|*.*";
            if (picker.ShowDialog(this) != true) return;
            try
            {
                if (ImageTools.Load(picker.FileName) == null) throw new InvalidOperationException("无法读取这张图片。");
                Value.CustomAvatarPath = LocalData.ImportAppearanceAvatar(picker.FileName);
                UpdateAvatarPreview();
                ErrorText.Text = "";
            }
            catch (Exception ex)
            {
                ErrorText.Text = "头像添加失败：" + ex.Message;
            }
        }

        private void ResetAvatar_Click(object sender, RoutedEventArgs e)
        {
            Value.CustomAvatarPath = "";
            UpdateAvatarPreview();
            ErrorText.Text = "";
        }

        private void ChooseSound_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog picker = new OpenFileDialog();
            picker.Title = "选择提醒声音";
            picker.Filter = "音频文件|*.wav;*.mp3;*.wma|所有文件|*.*";
            if (picker.ShowDialog(this) != true) return;
            try
            {
                Value.CustomSoundPath = LocalData.ImportSound(picker.FileName);
                UpdateSoundName();
                ErrorText.Text = "";
            }
            catch (Exception ex)
            {
                ErrorText.Text = "声音添加失败：" + ex.Message;
            }
        }

        private void PreviewSound_Click(object sender, RoutedEventArgs e)
        {
            SoundService.Play(Value.CustomSoundPath);
        }

        private void UpdateSoundName()
        {
            SoundNameText.Text = !String.IsNullOrWhiteSpace(Value.CustomSoundPath) && File.Exists(Value.CustomSoundPath)
                ? Path.GetFileName(Value.CustomSoundPath)
                : "未选择，使用系统提示音";
        }

        private void UpdateAvatarPreview()
        {
            string avatarPath = !String.IsNullOrWhiteSpace(Value.CustomAvatarPath) && File.Exists(Value.CustomAvatarPath)
                ? Value.CustomAvatarPath
                : LocalData.DefaultAppAvatar;
            AvatarPreviewBrush.ImageSource = ImageTools.Load(avatarPath);
        }

        private void Cancel_Click(object sender, RoutedEventArgs e)
        {
            DialogResult = false;
        }

        private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
        {
            if (e.LeftButton == MouseButtonState.Pressed) DragMove();
        }
    }
}
