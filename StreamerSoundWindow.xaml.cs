using System;
using System.IO;
using System.Windows;
using System.Windows.Input;
using Microsoft.Win32;

namespace LiveBell
{
    public partial class StreamerSoundWindow : Window
    {
        private readonly string defaultSoundPath;
        private readonly string streamerId;
        private string selectedSoundPath;
        private readonly string originalSoundPath;

        public string CustomSoundPath { get { return selectedSoundPath; } }

        public StreamerSoundWindow(Streamer streamer, string globalSoundPath)
        {
            InitializeComponent();
            defaultSoundPath = globalSoundPath ?? "";
            streamerId = String.IsNullOrWhiteSpace(streamer.Id) ? Guid.NewGuid().ToString("N") : streamer.Id;
            selectedSoundPath = streamer.CustomSoundPath ?? "";
            originalSoundPath = selectedSoundPath;
            StreamerNameText.Text = streamer.Name + " 开播时使用的声音";
            UpdateSoundName();
        }

        private void ChooseSound_Click(object sender, RoutedEventArgs e)
        {
            OpenFileDialog picker = new OpenFileDialog();
            picker.Title = "选择这位主播的提醒声音";
            picker.Filter = "音频文件|*.wav;*.mp3;*.wma|所有文件|*.*";
            if (picker.ShowDialog(this) != true) return;
            try
            {
                selectedSoundPath = picker.FileName;
                ErrorText.Text = "";
                UpdateSoundName();
            }
            catch (Exception ex)
            {
                ErrorText.Text = "声音添加失败：" + ex.Message;
            }
        }

        private void PreviewSound_Click(object sender, RoutedEventArgs e)
        {
            SoundService.Play(String.IsNullOrWhiteSpace(selectedSoundPath) ? defaultSoundPath : selectedSoundPath);
        }

        private void UseDefault_Click(object sender, RoutedEventArgs e)
        {
            selectedSoundPath = "";
            ErrorText.Text = "";
            UpdateSoundName();
        }

        private void UpdateSoundName()
        {
            SoundNameText.Text = !String.IsNullOrWhiteSpace(selectedSoundPath) && File.Exists(selectedSoundPath)
                ? "当前使用：" + Path.GetFileName(selectedSoundPath)
                : "未单独设置，将使用默认提醒声音";
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (!String.IsNullOrWhiteSpace(selectedSoundPath) && selectedSoundPath != originalSoundPath)
                    selectedSoundPath = LocalData.ImportSound(selectedSoundPath, "主播提醒_" + streamerId);
            }
            catch (Exception ex) { ErrorText.Text = "声音保存失败：" + ex.Message; return; }
            DialogResult = true;
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
