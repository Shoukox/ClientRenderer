using Avalonia.Media;
using Avalonia.Threading;
using ClientRenderer.GUI.Helpers;
using ClientRenderer.GUI.Services;
using ClientRenderer.GUI.Services.Localization;
using ClientRenderer.Logging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

namespace ClientRenderer.GUI.ViewModels
{
    public partial class StatusPageViewModel : ViewModelBase
    {
        private readonly LocalizationService _localizer = App.Localizer;
        private readonly RendererService _rendererService = RendererService.Instance;
        private readonly UpdateService _updateService = UpdateService.Instance;
        private CancellationTokenSource? _restartFeedbackCts;
        private CancellationTokenSource? _updateFeedbackCts;

        [ObservableProperty]
        private string _title = string.Empty;

        [ObservableProperty]
        private string _serverStatusLabel = string.Empty;

        [ObservableProperty]
        private string _heartbeatFailuresLabel = string.Empty;

        [ObservableProperty]
        private bool _isServerOnline;

        [ObservableProperty]
        private bool _isStarting;

        [ObservableProperty]
        private bool _isRestarting;

        [ObservableProperty]
        private bool _isCheckingForUpdates;

        [ObservableProperty]
        private string _restartFeedbackIcon = string.Empty;

        [ObservableProperty]
        private IBrush _restartFeedbackBrush = Brushes.Transparent;

        [ObservableProperty]
        private string _updateFeedbackIcon = string.Empty;

        [ObservableProperty]
        private IBrush _updateFeedbackBrush = Brushes.Transparent;

        [ObservableProperty]
        private string _appVersion = AppBuildInfo.DisplayText;

        public IBrush ServerStatusBrush => IsStarting
            ? Brushes.Goldenrod
            : IsServerOnline ? Brushes.LimeGreen : Brushes.IndianRed;

        public bool CanRunStatusActions => !IsRestarting && !IsCheckingForUpdates;

        public StatusPageViewModel()
        {
            UpdateLocalizedText();
            applyStatus(_rendererService.Status);
            IsCheckingForUpdates = _updateService.IsCheckingForUpdates;

            _localizer.LanguageChanged += (_, _) => UpdateLocalizedText();
            _rendererService.StatusChanged += OnRendererStatusChanged;
            _updateService.CheckingStateChanged += OnUpdateCheckingStateChanged;
        }

        partial void OnIsRestartingChanged(bool value) => OnPropertyChanged(nameof(CanRunStatusActions));
        partial void OnIsCheckingForUpdatesChanged(bool value) => OnPropertyChanged(nameof(CanRunStatusActions));

        private void UpdateLocalizedText()
        {
            Title = _localizer["Page.Status.Title"];
            updateStatusLabels(_rendererService.Status);
        }

        private void OnRendererStatusChanged(RendererStatusSnapshot status)
        {
            if (Dispatcher.UIThread.CheckAccess())
            {
                applyStatus(status);
                return;
            }

            Dispatcher.UIThread.Post(() => applyStatus(status));
        }

        private void OnUpdateCheckingStateChanged(bool isChecking)
        {
            if (Dispatcher.UIThread.CheckAccess())
            {
                IsCheckingForUpdates = isChecking;
                return;
            }

            Dispatcher.UIThread.Post(() => IsCheckingForUpdates = isChecking);
        }

        private void applyStatus(RendererStatusSnapshot status)
        {
            IsStarting = status.State == RendererServiceState.Starting;
            IsServerOnline = status.State == RendererServiceState.Online;
            updateStatusLabels(status);
            OnPropertyChanged(nameof(ServerStatusBrush));
        }

        private void updateStatusLabels(RendererStatusSnapshot status)
        {
            ServerStatusLabel = status.State switch
            {
                RendererServiceState.Starting => _localizer["Page.Status.State.Starting"],
                RendererServiceState.Online => _localizer["Page.Status.State.Online"],
                RendererServiceState.Failed => _localizer["Page.Status.State.Failed"],
                _ => _localizer["Page.Status.State.Offline"]
            };

            HeartbeatFailuresLabel = string.Format(
                CultureInfo.CurrentCulture,
                _localizer["Page.Status.HeartbeatFailures"],
                status.State == RendererServiceState.Offline ? status.ConsecutiveHeartbeatFailures : 0);
        }

        [RelayCommand]
        private async Task RestartRendererService()
        {
            if (IsRestarting || IsCheckingForUpdates)
                return;

            IsRestarting = true;
            clearRestartFeedback();

            try
            {
                ConsolePageViewModel.Instance.Clear();
                await _rendererService.RestartAsync(waitForOnline: true);

                var currentStatus = _rendererService.Status.State;
                if (currentStatus == RendererServiceState.Online)
                    await showRestartFeedbackAsync("✓", Brushes.LimeGreen);
                else
                    await showRestartFeedbackAsync("✕", Brushes.IndianRed);
            }
            catch
            {
                await showRestartFeedbackAsync("✕", Brushes.IndianRed);
                throw;
            }
            finally
            {
                IsRestarting = false;
            }
        }

        [RelayCommand]
        private async Task CheckForUpdates()
        {
            if (IsRestarting || IsCheckingForUpdates)
                return;

            clearUpdateFeedback();
            var result = await _updateService.CheckForUpdatesAsync(silentIfUpToDate: false);

            switch (result)
            {
                case UpdateCheckResult.NoUpdates:
                case UpdateCheckResult.SkippedNotInstalled:
                    await showUpdateFeedbackAsync("i", Brushes.Goldenrod);
                    break;
                case UpdateCheckResult.Failed:
                    await showUpdateFeedbackAsync("✕", Brushes.IndianRed);
                    break;
                case UpdateCheckResult.Busy:
                    await showUpdateFeedbackAsync("…", Brushes.Goldenrod);
                    break;
            }
        }

        [RelayCommand]
        private void OpenSettingsFolder()
        {
            Directory.CreateDirectory(App.SettingsProvider.RendererSettingsDirectory);
            Process.Start(new ProcessStartInfo
            {
                FileName = App.SettingsProvider.RendererSettingsDirectory,
                UseShellExecute = true
            });
        }

        [RelayCommand]
        private void OpenClientRendererSettings()
        {
            try
            {
                App.SettingsProvider.Save();
            }
            catch (Exception ex)
            {
                Logger.LogError(ex.ToString());
            }
            Process.Start(new ProcessStartInfo
            {
                FileName = App.SettingsProvider.FilePath,
                UseShellExecute = true
            });
        }

        [RelayCommand]
        private void OpenLogsFolder()
        {
            Directory.CreateDirectory(Logger.LogsDirectory);
            Process.Start(new ProcessStartInfo
            {
                FileName = Logger.LogsDirectory,
                UseShellExecute = true
            });
        }

        private void clearRestartFeedback()
        {
            _restartFeedbackCts?.Cancel();
            RestartFeedbackIcon = string.Empty;
            RestartFeedbackBrush = Brushes.Transparent;
        }

        private void clearUpdateFeedback()
        {
            _updateFeedbackCts?.Cancel();
            UpdateFeedbackIcon = string.Empty;
            UpdateFeedbackBrush = Brushes.Transparent;
        }

        private async Task showRestartFeedbackAsync(string icon, IBrush brush)
        {
            _restartFeedbackCts?.Cancel();
            _restartFeedbackCts = new CancellationTokenSource();
            var token = _restartFeedbackCts.Token;

            RestartFeedbackIcon = icon;
            RestartFeedbackBrush = brush;

            try
            {
                await Task.Delay(2500, token);
                if (!token.IsCancellationRequested)
                    clearRestartFeedback();
            }
            catch (TaskCanceledException)
            {
            }
        }

        private async Task showUpdateFeedbackAsync(string icon, IBrush brush)
        {
            _updateFeedbackCts?.Cancel();
            _updateFeedbackCts = new CancellationTokenSource();
            var token = _updateFeedbackCts.Token;

            UpdateFeedbackIcon = icon;
            UpdateFeedbackBrush = brush;

            try
            {
                await Task.Delay(2500, token);
                if (!token.IsCancellationRequested)
                    clearUpdateFeedback();
            }
            catch (TaskCanceledException)
            {
            }
        }
    }
}
