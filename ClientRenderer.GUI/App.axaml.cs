using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core.Plugins;
using Avalonia.Markup.Xaml;
using ClientRenderer.GUI.ViewModels;
using ClientRenderer.GUI.Views;
using System;
using System.Linq;

namespace ClientRenderer.GUI
{
    public partial class App : Application
    {
        public override void Initialize()
        {
            AvaloniaXamlLoader.Load(this);
        }

        public override void OnFrameworkInitializationCompleted()
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                DisableAvaloniaDataAnnotationValidation();
                desktop.MainWindow = new MainWindow
                {
                    DataContext = new MainWindowViewModel(),
                };
                desktop.ShutdownMode = ShutdownMode.OnExplicitShutdown;
            }

            base.OnFrameworkInitializationCompleted();
        }

        private void DisableAvaloniaDataAnnotationValidation()
        {
            // Get an array of plugins to remove
            var dataValidationPluginsToRemove =
                BindingPlugins.DataValidators.OfType<DataAnnotationsValidationPlugin>().ToArray();

            // remove each entry found
            foreach (var plugin in dataValidationPluginsToRemove)
            {
                BindingPlugins.DataValidators.Remove(plugin);
            }
        }

        public void Tray_OnClick(object sender, EventArgs e)
        {
            Tray_Show_OnClick(sender, e);
        }
        
        public void Tray_Show_OnClick(object sender, EventArgs e)
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                if (desktop.MainWindow!.WindowState == WindowState.Minimized)
                {
                    desktop.MainWindow.WindowState = WindowState.Normal;
                }
                desktop.MainWindow.Show();
                desktop.MainWindow.Activate();
            }
        }

        public void Tray_Exit_OnClick(object sender, EventArgs e)
        {
            switch (ApplicationLifetime)
            {
                case IClassicDesktopStyleApplicationLifetime desktopLifetime:
                    desktopLifetime.TryShutdown();
                    break;
                case IControlledApplicationLifetime controlledLifetime:
                    controlledLifetime.Shutdown();
                    break;
                default:
                    Environment.Exit(0);
                    break;
            }
        }
    }
}