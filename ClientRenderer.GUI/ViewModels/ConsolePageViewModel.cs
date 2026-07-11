using Avalonia.Media;
using Avalonia.Styling;
using Avalonia.Threading;
using ClientRenderer.GUI.Services.Localization;
using ClientRenderer.Logging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Serilog.Events;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Avalonia.Input.Platform;

namespace ClientRenderer.GUI.ViewModels
{
    public sealed record ConsoleLine(string Timestamp, string LevelText, string Message, IBrush? LevelBrush);

    public partial class ConsolePageViewModel : ViewModelBase
    {
        public static ConsolePageViewModel Instance { get; } = new();

        private const int maxRetainedLines = 2000;
        private static readonly TimeSpan flushInterval = TimeSpan.FromMilliseconds(150);

        private readonly Queue<ConsoleLine> _lines = new();
        private readonly Queue<ConsoleLine> _pendingLines = new();
        private readonly StringBuilder _consoleBuffer = new();
        private readonly object _sync = new();
        private readonly LocalizationService _localizer = App.Localizer;
        private readonly IBrush _warningBrush;
        private readonly IBrush _errorBrush;

        private DispatcherTimer? _flushTimer;
        private CancellationTokenSource? _copyFeedbackCts;

        [ObservableProperty]
        private string _title = string.Empty;

        [ObservableProperty]
        private string _consoleText = string.Empty;

        public ObservableCollection<ConsoleLine> ConsoleLines { get; } = new();

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CopyIcon))]
        [NotifyPropertyChangedFor(nameof(CopyIconBrush))]
        private bool _isCopied;
        public string CopyIcon => IsCopied ? "\u2713" : "\u2398";
        public IBrush CopyIconBrush => IsCopied ? Brushes.LimeGreen : Brushes.White;

        public IBrush ConsoleBackgroundNormal;
        public IBrush ConsoleBackgroundError;
        public IBrush ConsoleBackground => StatusPageViewModel.Instance.IsServerOnline ? ConsoleBackgroundNormal : ConsoleBackgroundError;

        private ConsolePageViewModel()
        {
            if (!App.Current!.TryGetResource("Console.Background", ThemeVariant.Default, out object? backgroundNormalResource)
             || !App.Current!.TryGetResource("Console.BackgroundError", ThemeVariant.Default, out object? backgroundErrorResource))
            {
                throw new KeyNotFoundException("Console background resources were not found.");
            }
            ConsoleBackgroundNormal = (IBrush)backgroundNormalResource!;
            ConsoleBackgroundError = (IBrush)backgroundErrorResource!;
            _warningBrush = TryGetConsoleBrush("Console.Warning") ?? Brushes.Orange;
            _errorBrush = TryGetConsoleBrush("Console.Error") ?? Brushes.Red;

            UpdateLocalizedText();
            _localizer.LanguageChanged += (_, _) => UpdateLocalizedText();
            StatusPageViewModel.Instance.PropertyChanged += OnStatusPagePropertyChanged;
            Logger.MessageLogged += OnMessageLogged;
        }

        public void AddLine(string text)
        {
            AddLine(LogEventLevel.Information, text);
        }

        public void AddLine(LogEventLevel level, string text)
        {
            const string warningPrefix = "[WARNING] ";
            const string errorPrefix = "[ERROR] ";
            string levelText = string.Empty;
            IBrush? levelBrush = null;

            if (level == LogEventLevel.Warning)
            {
                levelText = warningPrefix;
                levelBrush = _warningBrush;

                if (text.StartsWith(warningPrefix, StringComparison.Ordinal))
                    text = text[warningPrefix.Length..];
            }
            else if (level == LogEventLevel.Error)
            {
                levelText = errorPrefix;
                levelBrush = _errorBrush;

                if (text.StartsWith(errorPrefix, StringComparison.Ordinal))
                    text = text[errorPrefix.Length..];
            }

            enqueueLine(new ConsoleLine($"[{DateTime.Now:HH:mm:ss}] ", levelText, text, levelBrush));
        }

        public void Clear()
        {
            lock (_sync)
            {
                _lines.Clear();
                _pendingLines.Clear();
                _consoleBuffer.Clear();
            }

            stopFlushTimer();
            ConsoleLines.Clear();
            ConsoleText = string.Empty;
        }

        private void UpdateLocalizedText()
        {
            Title = _localizer["Page.Console.Title"];
        }

        private void OnMessageLogged(LogEventLevel level, string text)
        {
            if (level is LogEventLevel.Verbose or LogEventLevel.Debug) return;

            if (Dispatcher.UIThread.CheckAccess())
            {
                AddLine(level, text);
                return;
            }

            Dispatcher.UIThread.Post(() => AddLine(level, text));
        }

        private void OnStatusPagePropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(StatusPageViewModel.IsServerOnline))
                return;

            if (Dispatcher.UIThread.CheckAccess())
            {
                OnPropertyChanged(nameof(ConsoleBackground));
                return;
            }

            Dispatcher.UIThread.Post(() => OnPropertyChanged(nameof(ConsoleBackground)));
        }

        private void enqueueLine(ConsoleLine line)
        {
            lock (_sync)
            {
                _pendingLines.Enqueue(line);
            }

            ensureFlushTimer();
        }

        private void ensureFlushTimer()
        {
            if (!Dispatcher.UIThread.CheckAccess())
            {
                Dispatcher.UIThread.Post(ensureFlushTimer);
                return;
            }

            _flushTimer ??= new DispatcherTimer
            {
                Interval = flushInterval
            };

            _flushTimer.Tick -= OnFlushTimerTick;
            _flushTimer.Tick += OnFlushTimerTick;

            if (!_flushTimer.IsEnabled)
                _flushTimer.Start();
        }

        private void stopFlushTimer()
        {
            if (!Dispatcher.UIThread.CheckAccess())
            {
                Dispatcher.UIThread.Post(stopFlushTimer);
                return;
            }

            _flushTimer?.Stop();
        }

        private void OnFlushTimerTick(object? sender, EventArgs e)
        {
            bool hasChanges = false;

            lock (_sync)
            {
                while (_pendingLines.Count > 0)
                {
                    var line = _pendingLines.Dequeue();
                    _lines.Enqueue(line);
                    ConsoleLines.Add(line);
                    hasChanges = true;
                }

                while (_lines.Count > maxRetainedLines)
                {
                    _lines.Dequeue();
                    ConsoleLines.RemoveAt(0);
                }

                if (!hasChanges)
                {
                    _flushTimer?.Stop();
                    return;
                }

                rebuildBufferUnsafe();
            }

            ConsoleText = _consoleBuffer.ToString();
        }

        private void rebuildBufferUnsafe()
        {
            _consoleBuffer.Clear();

            bool first = true;
            foreach (var line in _lines)
            {
                if (!first)
                    _consoleBuffer.AppendLine();

                _consoleBuffer.Append(line.Timestamp);
                _consoleBuffer.Append(line.LevelText);
                _consoleBuffer.Append(line.Message);
                first = false;
            }
        }

        private static IBrush? TryGetConsoleBrush(string resourceKey)
        {
            return App.Current!.TryGetResource(resourceKey, ThemeVariant.Default, out object? resource)
                ? resource as IBrush
                : null;
        }

        [RelayCommand]
        private Task CopyConsole()
        {
            _ = CopyConsoleCoreAsync();
            return Task.CompletedTask;
        }

        private async Task CopyConsoleCoreAsync()
        {
            await Helpers.Clipboard.Get()!.SetTextAsync(ConsoleText);

            _copyFeedbackCts?.Cancel();
            _copyFeedbackCts = new CancellationTokenSource();
            var token = _copyFeedbackCts.Token;
            IsCopied = true;

            try
            {
                await Task.Delay(1000, token);
                IsCopied = false;
            }
            catch (TaskCanceledException)
            {
            }
        }
    }
}
