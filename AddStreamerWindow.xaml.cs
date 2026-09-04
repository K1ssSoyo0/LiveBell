using System;
using System.Windows;
using System.Windows.Input;

namespace LiveBell
{
    public partial class AddStreamerWindow : Window
    {
        public string Platform { get; private set; }
        public string RoomId { get; private set; }

        public AddStreamerWindow()
        {
            InitializeComponent();
            Platform = "bilibili";
            RoomId = "";
        }

        private void Save_Click(object sender, RoutedEventArgs e)
        {
            string selected = DouyuBox.IsChecked == true ? "douyu" : "bilibili";
            string platform;
            string room;
            if (!AddressParser.TryParse(AddressBox.Text, selected, out platform, out room))
            {
                MessageBox.Show(this, "请输入有效的直播间号或 B站、斗鱼直播间网址。", "开播铃", MessageBoxButton.OK, MessageBoxImage.Warning);
                return;
            }
            Platform = platform;
            RoomId = room;
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
