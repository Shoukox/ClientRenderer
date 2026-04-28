using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClientRenderer.GUI.ViewModels
{
    public partial class MainWindowViewModel : ViewModelBase
    {
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(LogoWidth))]
        private bool _sideMenuExpanded = true;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(HomePageIsActive))]
        [NotifyPropertyChangedFor(nameof(StatusPageIsActive))]
        [NotifyPropertyChangedFor(nameof(ConsolePageIsActive))]
        [NotifyPropertyChangedFor(nameof(SettingsPageIsActive))]
        private ViewModelBase _currentPage;

        private readonly HomePageViewModel _homePage = new();
        private readonly StatusPageViewModel _statusPage = new();
        private readonly ConsolePageViewModel _consolePage = ConsolePageViewModel.Instance;
        private readonly SettingsPageViewModel _settingsPage = new();

        public bool HomePageIsActive => CurrentPage == _homePage;
        public bool StatusPageIsActive => CurrentPage == _statusPage;
        public bool ConsolePageIsActive => CurrentPage == _consolePage;
        public bool SettingsPageIsActive => CurrentPage == _settingsPage;

        public int LogoWidth => SideMenuExpanded ? 75 : 35;

        public MainWindowViewModel()
        {
            CurrentPage = _homePage;
        }


        [RelayCommand]
        private void SideMenuResize()
        {
            SideMenuExpanded = !SideMenuExpanded;
        }

        [RelayCommand]
        private void GoToHome()
        {
            CurrentPage = _homePage;
        }

        [RelayCommand]
        private void GoToStatus()
        {
            CurrentPage = _statusPage;
        }

        [RelayCommand]
        private void GoToConsole()
        {
            CurrentPage = _consolePage;
        }

        [RelayCommand]
        private void GoToSettings()
        {
            CurrentPage = _settingsPage;
        }
    }
}
