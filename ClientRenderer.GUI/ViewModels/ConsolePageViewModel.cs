using System;
using System.Text;
using Avalonia.Threading;
using ClientRenderer.Logging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClientRenderer.GUI.ViewModels
{
    public partial class ConsolePageViewModel : ViewModelBase
    {
        public static ConsolePageViewModel Instance { get; } = new();

        private readonly StringBuilder _consoleBuffer = new();

        [ObservableProperty]
        private string _title = "client renderer console";

        [ObservableProperty]
        private string _consoleText = string.Empty;

        private readonly string _initialText = "There is nothing in there. You would probably start the application firstly.";

        private ConsolePageViewModel()
        {
            AddInitialLine();
            Logger.MessageLogged += OnMessageLogged;
        }

        public void AddInitialLine()
        {
            AppendRawLine(_initialText);
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
        private void CopyConsole()
        {
            Helpers.Clipboard.Get()?.SetTextAsync(ConsoleText);
        }
    }
}
