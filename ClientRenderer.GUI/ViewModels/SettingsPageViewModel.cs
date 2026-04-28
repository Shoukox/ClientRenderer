using Avalonia.Media;
using ClientRenderer.GUI.Configuration;
using ClientRenderer.GUI.Services;
using ClientRenderer.GUI.Services.Localization;
using CommunityToolkit.Mvvm.ComponentModel;
using System.Collections.Generic;
using System.Linq;

namespace ClientRenderer.GUI.ViewModels
{
    public partial class SettingsPageViewModel : ViewModelBase
    {
        private static readonly IBrush ErrorColor = new SolidColorBrush(Color.FromRgb(220, 20, 60));
        private static readonly IBrush SuccessColor = new SolidColorBrush(Color.FromRgb(76, 187, 23));
        private readonly StartupLaunchService _startupLaunchService = new();
        private readonly AppSettingsProvider _settingsProvider = App.SettingsProvider;
        private readonly LocalizationService _localizer = App.Localizer;
        private bool _isApplyingStartupSetting;
        private bool _isLoadingSettings;

        public SettingsPageViewModel()
        {
            SupportedLanguages = _localizer.SupportedLanguages;
            LoadSettings();
            InitializeRunOnSystemStartupStatus();
            _localizer.LanguageChanged += (_, _) => RefreshLocalizedState();
        }

        public IReadOnlyList<SupportedLanguage> SupportedLanguages { get; }

        [ObservableProperty]
        private SupportedLanguage? _selectedLanguage;

        [ObservableProperty]
        private bool _runOnSystemStartup;

        [ObservableProperty]
        private string _startupSettingStatus = string.Empty;

        [ObservableProperty]
        private IBrush _startupSettingStatusColor = ErrorColor;

        [ObservableProperty]
        private bool _startupSettingSupported;

        [ObservableProperty]
        private bool _minimizeInsteadOfClosing;

        private void LoadSettings()
        {
            _isLoadingSettings = true;

            var settings = _settingsProvider.Current;
            RunOnSystemStartup = settings.RunOnSystemStartup;
            MinimizeInsteadOfClosing = settings.MinimizeInsteadOfClosing;
            SelectedLanguage = SupportedLanguages.FirstOrDefault(x => x.Code == settings.Language) ?? SupportedLanguages.First();

            _isLoadingSettings = false;
        }

        private void InitializeRunOnSystemStartupStatus()
        {
            StartupSettingSupported = _startupLaunchService.IsSupported;

            if (!StartupSettingSupported)
            {
                StartupSettingStatus = _localizer["Settings.RunOnStartup.Unsupported"];
                StartupSettingStatusColor = ErrorColor;
                return;
            }

            var actualState = _startupLaunchService.IsEnabled();
            if (actualState != RunOnSystemStartup)
            {
                _isApplyingStartupSetting = true;
                _startupLaunchService.SetEnabled(RunOnSystemStartup);
                _isApplyingStartupSetting = false;
            }

            UpdateStartupStatus(RunOnSystemStartup);
        }

        partial void OnSelectedLanguageChanged(SupportedLanguage? value)
        {
            if (_isLoadingSettings || value is null)
                return;

            _settingsProvider.Update(settings => settings.Language = value.Code);
            _localizer.SetLanguage(value.Code);
        }

        partial void OnRunOnSystemStartupChanged(bool value)
        {
            if (_isLoadingSettings)
                return;

            _settingsProvider.Update(settings => settings.RunOnSystemStartup = value);

            if (_isApplyingStartupSetting || !StartupSettingSupported)
                return;

            try
            {
                _isApplyingStartupSetting = true;
                _startupLaunchService.SetEnabled(value);
                UpdateStartupStatus(value);
            }
            catch (System.Exception ex)
            {
                _settingsProvider.Update(settings => settings.RunOnSystemStartup = !value);
                RunOnSystemStartup = !value;
                StartupSettingStatus = string.Format(_localizer["Settings.RunOnStartup.Failed"], ex.Message);
                StartupSettingStatusColor = ErrorColor;
            }
            finally
            {
                _isApplyingStartupSetting = false;
            }
        }

        partial void OnMinimizeInsteadOfClosingChanged(bool value)
        {
            if (_isLoadingSettings)
                return;

            _settingsProvider.Update(settings => settings.MinimizeInsteadOfClosing = value);
        }

        private void RefreshLocalizedState()
        {
            if (!StartupSettingSupported)
            {
                StartupSettingStatus = _localizer["Settings.RunOnStartup.Unsupported"];
                StartupSettingStatusColor = ErrorColor;
                return;
            }

            UpdateStartupStatus(RunOnSystemStartup);
        }

        private void UpdateStartupStatus(bool isEnabled)
        {
            StartupSettingStatus = isEnabled
                ? _localizer["Settings.RunOnStartup.Enabled"]
                : _localizer["Settings.RunOnStartup.Disabled"];
            StartupSettingStatusColor = isEnabled ? SuccessColor : ErrorColor;
        }
    }
}
