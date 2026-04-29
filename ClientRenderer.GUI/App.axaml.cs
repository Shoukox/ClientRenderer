using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Data.Core.Plugins;
using Avalonia.Markup.Xaml;
using Avalonia.Platform;
using ClientRenderer.GUI.Configuration;
using ClientRenderer.GUI.Services;
using ClientRenderer.GUI.Services.Localization;
using ClientRenderer.GUI.ViewModels;
using ClientRenderer.GUI.Views;
using System;
using System.IO;
using System.Linq;

namespace ClientRenderer.GUI
{
    public partial class App : Application
    {
        private TrayIcon? _trayIcon;
        private NativeMenuItem? _trayShowMenuItem;
        private NativeMenuItem? _trayExitMenuItem;

        internal static SingleInstanceManager? SingleInstance { get; set; }

        public static AppSettingsProvider SettingsProvider { get; } = new(
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "ClientRenderer"));

        public static LocalizationService Localizer { get; } = new();

        public override void Initialize()
        {
            SettingsProvider.Load();
            Localizer.SetLanguage(SettingsProvider.Current.Language);
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
                desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;

                bool hidden = false;
                if (desktop.Args?.Contains("--startup") == true)
                {
                    desktop.MainWindow.Opened += (_, _) =>
                    {
                        if (!hidden)
                        {
                            desktop.MainWindow.Hide();
                            hidden = true;
                        }
                    };
                }

                SingleInstance?.RegisterActivationHandler(() => ShowAndActivateMainWindow(desktop));

                CreateTrayIcon();
                UpdateLocalizedShellText();
                Localizer.LanguageChanged += (_, _) => UpdateLocalizedShellText();

                var settings = SettingsProvider.Current;
                RendererService.Instance.RunTask(settings.DefaultEncoder, settings.ServerUrl);
                _ = UpdateService.Instance.CheckForUpdatesAsync(silentIfUpToDate: true, restartArgs: desktop.Args);
            }

            base.OnFrameworkInitializationCompleted();
        }

        private static void ShowAndActivateMainWindow(IClassicDesktopStyleApplicationLifetime desktop)
        {
            var window = desktop.MainWindow;
            if (window == null)
                return;

            if (window.WindowState == WindowState.Minimized)
                window.WindowState = WindowState.Normal;

            window.Show();
            window.Activate();
            window.Topmost = true;
            window.Topmost = false;
            window.Focus();
        }

        private void CreateTrayIcon()
        {
            _trayShowMenuItem = new NativeMenuItem();
            _trayShowMenuItem.Click += Tray_Show_OnClick;

            _trayExitMenuItem = new NativeMenuItem();
            _trayExitMenuItem.Click += Tray_Exit_OnClick;

            var menu = new NativeMenu();
            menu.Add(_trayShowMenuItem);
            menu.Add(new NativeMenuItemSeparator());
            menu.Add(_trayExitMenuItem);

            Stream iconStream = AssetLoader.Open(new Uri("avares://ClientRenderer.GUI/Assets/icon.ico"));
            _trayIcon = new TrayIcon
            {
                Icon = new WindowIcon(iconStream),
                IsVisible = true,
                Menu = menu,
                ToolTipText = Localizer["Tray.Tooltip"]
            };

            _trayIcon.Clicked += Tray_OnClick;
            TrayIcon.SetIcons(this, [_trayIcon]);
        }

        private void UpdateLocalizedShellText()
        {
            if (_trayIcon is not null)
                _trayIcon.ToolTipText = Localizer["Tray.Tooltip"];

            if (_trayShowMenuItem is not null)
                _trayShowMenuItem.Header = Localizer["Tray.Show"];

            if (_trayExitMenuItem is not null)
                _trayExitMenuItem.Header = Localizer["Tray.Exit"];

            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
            {
                desktop.MainWindow!.Title = Localizer["App.Title"];
            }
        }

        private void DisableAvaloniaDataAnnotationValidation()
        {
            var dataValidationPluginsToRemove =
                BindingPlugins.DataValidators.OfType<DataAnnotationsValidationPlugin>().ToArray();

            foreach (var plugin in dataValidationPluginsToRemove)
            {
                BindingPlugins.DataValidators.Remove(plugin);
            }
        }

        public void Tray_OnClick(object? sender, EventArgs e)
        {
            Tray_Show_OnClick(sender, e);
        }

        public void Tray_Show_OnClick(object? sender, EventArgs e)
        {
            if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
                ShowAndActivateMainWindow(desktop);
        }

        public void Tray_Exit_OnClick(object? sender, EventArgs e)
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
