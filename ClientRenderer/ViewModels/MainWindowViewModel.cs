using Avalonia.Svg.Skia;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ClientRenderer.ViewModels
{
    public partial class MainWindowViewModel : ViewModelBase
    {
        [ObservableProperty]
        private bool _sideMenuExpanded = true;
    }
}
