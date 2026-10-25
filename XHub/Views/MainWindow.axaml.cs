using System;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Threading;
using XHub.Infrastructure;
using XHub.ViewModels;

namespace XHub.Views;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        Opened += OnWindowOpened;
    }

    private void OnWindowOpened(object? sender, EventArgs e)
    {
        HubLog.Info("main window opened"); // performance baseline marker (§8 startup P95)
        // Acceptance/automation seam: --screenshot captures the settled UI and quits so every
        // node can record real interface evidence without a human at the keyboard.
        string? screenshotPath = Program.Options?.ScreenshotPath;
        if (string.IsNullOrWhiteSpace(screenshotPath)) return;
        if (Program.Options?.Modules == true)
            return; // the modules dialog captures itself (DialogService.ShowModulesAsync)

        int delayMs = Program.Options?.ScreenshotDelayMs ?? 500;
        DispatcherTimer.RunOnce(() =>
        {
            try
            {
                WindowCapture.Save(this, screenshotPath);
                HubLog.Info($"Screenshot saved: {screenshotPath}");
            }
            catch (Exception ex)
            {
                HubLog.Error($"Screenshot failed: {screenshotPath}", ex);
            }
            finally
            {
                Close();
            }
        }, TimeSpan.FromMilliseconds(delayMs));
    }
}
