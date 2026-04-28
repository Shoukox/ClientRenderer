using Avalonia.Media;
using Avalonia.Threading;
using ClientRenderer.GUI.Services.Localization;
using ClientRenderer.Logging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using System;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace ClientRenderer.GUI.ViewModels
{
    public partial class ConsolePageViewModel : ViewModelBase
    {
        public static ConsolePageViewModel Instance { get; } = new();

        private readonly StringBuilder _consoleBuffer = new();
        private readonly LocalizationService _localizer = App.Localizer;

        [ObservableProperty]
        private string _title = string.Empty;

        [ObservableProperty]
        private string _consoleText = string.Empty;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CopyIcon))]
        [NotifyPropertyChangedFor(nameof(CopyIconBrush))]
        private bool _isCopied;

        private CancellationTokenSource? _copyFeedbackCts;
        public string CopyIcon => IsCopied ? "\u2713" : "\u2398";
        public IBrush CopyIconBrush => IsCopied ? Brushes.LimeGreen : Brushes.White;

        private ConsolePageViewModel()
        {
            UpdateLocalizedText();
            _localizer.LanguageChanged += (_, _) => UpdateLocalizedText();
            Logger.MessageLogged += OnMessageLogged;
        }

        public void AddLine(string text)
        {
            AppendRawLine($"[{DateTime.Now:HH:mm:ss}] {text}");
        }

        public void Clear()
        {
            _consoleBuffer.Clear();
            ConsoleText = string.Empty;
        }

        private void UpdateLocalizedText()
        {
            Title = _localizer["Page.Console.Title"];
        }

        private void OnMessageLogged(string text)
        {
            if (Dispatcher.UIThread.CheckAccess())
            {
                AddLine(text);
                return;
            }

            Dispatcher.UIThread.Post(() => AddLine(text));
        }

        private void AppendRawLine(string line)
        {
            if (_consoleBuffer.Length > 0)
            {
                _consoleBuffer.AppendLine();
            }

            _consoleBuffer.Append(line);
            ConsoleText = _consoleBuffer.ToString();
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
