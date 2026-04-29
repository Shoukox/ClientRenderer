using Avalonia.Controls;

namespace ClientRenderer.GUI.Views;

public partial class ConsolePageView : UserControl
{
    public ConsolePageView()
    {
        InitializeComponent();
    }

    private bool _autoScroll = true;
    private void ScrollViewer_ScrollChanged(object? sender, ScrollChangedEventArgs e)
    {
        ScrollViewer? scrollViewer = sender as ScrollViewer;
        if (scrollViewer == null) return;

        bool isAtBottom = scrollViewer.Offset.Y >= scrollViewer.Extent.Height - scrollViewer.Viewport.Height - 1;
        if(!isAtBottom) {
            _autoScroll = false;
        }
        else {
            _autoScroll = true;
        }

        if (_autoScroll)
        {
            scrollViewer.ScrollToEnd();
        }
    }
}