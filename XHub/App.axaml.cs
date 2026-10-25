using System;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Markup.Xaml;
using Avalonia.Threading;
using XHub.Platform;
using XHub.Resources;
using XHub.Services;
using XHub.ViewModels;
using XHub.Views;

namespace XHub;
public class App : Application
{
    public static SettingsService Settings { get; private set; } = new();
    public static MainWindowViewModel? Hub { get; private set; }

    public override void Initialize() => AvaloniaXamlLoader.Load(this);

    public override void OnFrameworkInitializationCompleted()
    {
        Settings.Load();
        // User-configured install locations must be in place before the download/install services
        // are constructed (they capture the roots), i.e. before Hub below.
        AppPaths.ConfigureLocationOverrides(
            Settings.Current.EditorsRootOverride, Settings.Current.DownloadsRootOverride);
        if (Program.Options?.Language is { } cliLanguage)
            Loc.Instance.Apply(cliLanguage);
        else
            Loc.Instance.Apply(Settings.Current.Language);

        if (ApplicationLifetime is IClassicDesktopStyleApplicationLifetime desktop)
        {
            desktop.ShutdownMode = ShutdownMode.OnMainWindowClose;

            Hub = new MainWindowViewModel(Settings, new ProjectLibrary(), new Views.DialogService(),
                feedOverride: Program.Options?.FeedOverride);
            desktop.MainWindow = new MainWindow { DataContext = Hub };

            var request = Program.Options;
            if (request != null && request.HasForwardableRequest)
            {
                // Apply on the first layout pass so deep links open on the requested page.
                Dispatcher.UIThread.Post(() =>
                {
                    if (request.Activate)
                        desktop.MainWindow?.Activate();
                    Hub.HandleRequest(request);
                }, DispatcherPriority.Loaded);
            }

            // Background path liveness probe for the projects list (§3.1) + install probing (§3.3).
            Dispatcher.UIThread.Post(() =>
            {
                Hub.Projects.StartLivenessProbe();
                Hub.Installations.StartProbe();
            }, DispatcherPriority.Background);
        }

        base.OnFrameworkInitializationCompleted();
    }

    /// <summary>Routes a forwarded IPC request onto the UI thread of the primary instance.</summary>
    public static void DispatchIpc(string[] args)
    {
        Dispatcher.UIThread.Post(() =>
        {
            var request = CliOptions.Parse(args);
            if (Hub == null) return;
            Hub.HandleRequest(request);
            (Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)
                ?.MainWindow?.Activate();
        });
    }
}
