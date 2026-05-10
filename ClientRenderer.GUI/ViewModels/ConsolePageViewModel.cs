using Avalonia;
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
using System.ComponentModel;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ClientRenderer.GUI.ViewModels
{
    public partial class ConsolePageViewModel : ViewModelBase
    {
        public static ConsolePageViewModel Instance { get; } = new();

        private const int maxRetainedLines = 2000;
        private static readonly TimeSpan flushInterval = TimeSpan.FromMilliseconds(150);

        private readonly Queue<string> _lines = new();
        private readonly Queue<string> _pendingLines = new();
        private readonly StringBuilder _consoleBuffer = new();
        private readonly object _sync = new();
        private readonly LocalizationService _localizer = App.Localizer;

        private DispatcherTimer? _flushTimer;
        private CancellationTokenSource? _copyFeedbackCts;

        [ObservableProperty]
        private string _title = string.Empty;

        [ObservableProperty]
        private string _consoleText = string.Empty;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CopyIcon))]
        [NotifyPropertyChangedFor(nameof(CopyIconBrush))]
        private bool _isCopied;
        public string CopyIcon => IsCopied ? "\u2713" : "\u2398";
        public IBrush CopyIconBrush => IsCopied ? Brushes.LimeGreen : Brushes.White;

        public IBrush ConsoleBackgroundNormal;
        public IBrush ConsoleBackgroundError;
        public IBrush ConsoleBackground => StatusPageViewModel.Instance.IsFailed ? ConsoleBackgroundError : ConsoleBackgroundNormal;

        private ConsolePageViewModel()
        {
            if (!App.Current!.TryGetResource("Console.Background", ThemeVariant.Default, out object? backgroundNormalResource)
             || !App.Current!.TryGetResource("Console.BackgroundError", ThemeVariant.Default, out object? backgroundErrorResource))
            {
                throw new KeyNotFoundException("Console background resources were not found.");
            }
            ConsoleBackgroundNormal = (IBrush)backgroundNormalResource!;
            ConsoleBackgroundError = (IBrush)backgroundErrorResource!;

            UpdateLocalizedText();
            _localizer.LanguageChanged += (_, _) => UpdateLocalizedText();
            StatusPageViewModel.Instance.PropertyChanged += OnStatusPagePropertyChanged;
            Logger.MessageLogged += OnMessageLogged;
        }

        public void AddLine(string text)
        {
            enqueueLine($"[{DateTime.Now:HH:mm:ss}] {text}");
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
                AddLine(text);
                return;
            }

            Dispatcher.UIThread.Post(() => AddLine(text));
        }

        private void OnStatusPagePropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName != nameof(StatusPageViewModel.IsFailed))
                return;

            if (Dispatcher.UIThread.CheckAccess())
            {
                OnPropertyChanged(nameof(ConsoleBackground));
                return;
            }

            Dispatcher.UIThread.Post(() => OnPropertyChanged(nameof(ConsoleBackground)));
        }

        private void enqueueLine(string line)
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
                    _lines.Enqueue(_pendingLines.Dequeue());
                    hasChanges = true;
                }

                while (_lines.Count > maxRetainedLines)
                    _lines.Dequeue();

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

                _consoleBuffer.Append(line);
                first = false;
            }
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
