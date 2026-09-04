using System.Windows;
using System.Windows.Input;

namespace LiveBell
{
    public partial class ConfirmDeleteWindow : Window
    {
        public ConfirmDeleteWindow(string streamerName)
        {
            InitializeComponent();
            MessageText.Text = "“" + streamerName + "”将从关注列表中移除。";
        }

        private void Delete_Click(object sender, RoutedEventArgs e)
        {
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
