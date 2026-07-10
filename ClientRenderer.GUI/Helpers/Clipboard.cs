using System;
using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Input.Platform;
using Avalonia.VisualTree;

namespace ClientRenderer.GUI.Helpers;

public static class Clipboard
{
    public static IClipboard Get()
    {
        //Desktop
        if (Application.Current?.ApplicationLifetime is IClassicDesktopStyleApplicationLifetime { MainWindow: { } window })
        {
            return window.Clipboard!;

        }
        //Android (and iOS?)
        else if (Application.Current?.ApplicationLifetime is ISingleViewApplicationLifetime { MainView: { } mainView })
        {
            return TopLevel.GetTopLevel(mainView)?.Clipboard ?? throw new InvalidOperationException("Control is not attached to a TopLevel.");
        }

        return null!;
    }
}
