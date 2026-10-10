using System.Windows;
using System.Windows.Input;

namespace GameOverlay
{
    public partial class SettingsWindow : Window
    {
        public SettingsWindow()
        {
            InitializeComponent();

            // Esc — закрыть
            KeyDown += (s, e) =>
            {
                if (e.Key == Key.Escape)
                    Close();
            };

            // Таскать окно за заголовок
            MouseLeftButtonDown += (s, e) =>
            {
                if (e.ButtonState == MouseButtonState.Pressed)
                    DragMove();
            };
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}