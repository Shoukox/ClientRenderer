using Avalonia.Controls;
using Avalonia.Input;
using MsBox.Avalonia;

namespace ClientRenderer.Views
{
    public partial class MainWindow : Window
    {
        public MainWindow()
        {
            InitializeComponent();

            Closing += (s, e) =>
            {
                if (e.CloseReason is
                    WindowCloseReason.ApplicationShutdown 
                    or WindowCloseReason.OSShutdown 
                    or WindowCloseReason.Undefined)
                {
                    e.Cancel = false;
                }
                else
                {
                    Hide();
                    e.Cancel = true;
                }
            };
        }

        public async void HomeButton_OnClick(object sender, Avalonia.Interactivity.RoutedEventArgs e)
        {
            await MessageBoxManager.GetMessageBoxStandard("Info", "Home button clicked!").ShowAsPopupAsync(this);
        }

        private void TitleBar_OnPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            // Only start move on left-button press
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                // BeginMoveDrag is provided by Window in Avalonia
                BeginMoveDrag(e);

                // Optional: double-click to toggle maximize
                if (e.ClickCount == 2)
                {
                    WindowState = (WindowState == WindowState.Maximized) ? WindowState.Normal : WindowState.Maximized;
                }
            }
        }
    }
}