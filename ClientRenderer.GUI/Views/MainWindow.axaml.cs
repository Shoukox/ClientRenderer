using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input;
using ClientRenderer.GUI.Services;
using ClientRenderer.GUI.ViewModels;

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
                    e.Cancel = RendererService.Instance.IsRenderingRightNow;
                    return;
                }

                if (App.SettingsProvider.Current.MinimizeInsteadOfClosing)
                {
                    Hide();
                    e.Cancel = true;
                    return;
                }

                e.Cancel = !App.ShowWarningMessageBoxBeforeClosing(App.Current!.ApplicationLifetime).Result;
            };
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

        private void Logo_OnPointerPressed(object? sender, PointerPressedEventArgs e)
        {
            ulong msDelta = e.Timestamp - timestampOfLastClick;
            timestampOfLastClick = e.Timestamp;

            if (msDelta > doubleClickWithinMs) return;

            (DataContext as MainWindowViewModel)?.SideMenuResizeCommand.Execute(null);
        }
    }
}
