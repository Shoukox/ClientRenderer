using ClientRenderer.GUI.Services;
using CommunityToolkit.Mvvm.ComponentModel;

namespace ClientRenderer.GUI.ViewModels
{
    public partial class SettingsPageViewModel : ViewModelBase
    {
        private readonly StartupLaunchService _startupLaunchService = new();
        private bool _isApplyingStartupSetting;

        [ObservableProperty]
        private bool _runOnSystemStartup;

        [ObservableProperty]
        private string _startupSettingStatus = string.Empty;

        [ObservableProperty]
        private bool _startupSettingSupported;

        public SettingsPageViewModel()
        {
            StartupSettingSupported = _startupLaunchService.IsSupported;

            if (!StartupSettingSupported)
            {
                StartupSettingStatus = "This option is only available on Windows.";
                return;
            }

            RunOnSystemStartup = _startupLaunchService.IsEnabled();
            StartupSettingStatus = RunOnSystemStartup
                ? "ClientRenderer will start automatically after you sign in to Windows."
                : "ClientRenderer will not start automatically with Windows.";
        }

        partial void OnRunOnSystemStartupChanged(bool value)
        {
            if (_isApplyingStartupSetting || !StartupSettingSupported)
                return;

            try
            {
                _isApplyingStartupSetting = true;
                _startupLaunchService.SetEnabled(value);
                StartupSettingStatus = value
                    ? "ClientRenderer will start automatically after you sign in to Windows."
                    : "ClientRenderer will not start automatically with Windows.";
            }
            catch (System.Exception ex)
            {
                RunOnSystemStartup = !value;
                StartupSettingStatus = $"Failed to update startup setting: {ex.Message}";
            }
            finally
            {
                _isApplyingStartupSetting = false;
            }
        }
    }
}
