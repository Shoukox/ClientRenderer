using Avalonia.Controls;
using Avalonia.Input;
using ClientRenderer.GUI.ViewModels;
using MsBox.Avalonia;

namespace ClientRenderer.GUI.Views
{
    public partial class MainWindow : Window
    {
        private ulong timestampOfLastClick = 0;
        private ulong doubleClickWithinMs = 200; //200ms for double click
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
            if (e.GetCurrentPoint(this).Properties.IsLeftButtonPressed)
            {
                BeginMoveDrag(e);

                if (e.ClickCount == 2)
                {
                    WindowState = (WindowState == WindowState.Maximized) ? WindowState.Normal : WindowState.Maximized;
                }
            }
        }

        private async void Logo_OnPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            ulong msDelta = e.Timestamp - timestampOfLastClick;
            timestampOfLastClick = e.Timestamp;

            if (msDelta > doubleClickWithinMs) return;

            (DataContext as MainWindowViewModel)?.SideMenuResizeCommand.Execute(null);
        }
    }
}
