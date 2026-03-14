using System;
using System.Text;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace ClientRenderer.GUI.ViewModels
{
    public partial class ConsolePageViewModel : ViewModelBase
    {
        private readonly StringBuilder _consoleBuffer = new();

        [ObservableProperty]
        private string _title = "client renderer console";

        [ObservableProperty]
        private string _consoleText = string.Empty;

        private string _initialText = $"There is nothing in there. You would probably start the application firstly.";

        public ConsolePageViewModel()
        {
            AddInitialLine();
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
