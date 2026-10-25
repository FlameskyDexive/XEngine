using System;
using XHub.Infrastructure;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Controls.ApplicationLifetimes;
using Avalonia.Layout;
using Avalonia.Media;
using Avalonia.Platform.Storage;
using Avalonia;
using XHub.Models;
using XHub.Resources;
using XHub.Services;
using XHub.ViewModels;
using XHub.Views.Dialogs;

namespace XHub.Views;

/// <summary>View-layer implementation of the dialogs/pickers the view models use.</summary>
public sealed class DialogService : IDialogService
{
    public async Task<string?> PickProjectFolderAsync()
    {
        IStorageProvider? storage = TopLevelStorage();
        if (storage == null) return null;

        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = Loc.Instance["dialog.pickFolder.title"],
            AllowMultiple = false,
        });
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickXEngineFileAsync()
    {
        IStorageProvider? storage = TopLevelStorage();
        if (storage == null) return null;

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Loc.Instance["dialog.pickFile.title"],
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("XEngine Project") { Patterns = ["*.xengine"] },
            ],
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public async Task<string?> PickAnyFolderAsync()
    {
        IStorageProvider? storage = TopLevelStorage();
        if (storage == null) return null;

        var folders = await storage.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = Loc.Instance["wizard.parent.label"],
            AllowMultiple = false,
        });
        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }

    public async Task<Models.ProjectRecord?> ShowNewProjectWizardAsync(
        ProjectCreator creator, ProjectLibrary library, InstallationLibrary installations,
        TemplateCatalog.Template template)
    {
        Window? owner = OwnerWindow();
        if (owner == null) return null;

        var vm = new NewProjectViewModel(creator, library, installations, template, this);
        var wizard = new NewProjectWindow { DataContext = vm };
        // Cancel closes natively (IsCancel); the create path only resolves Completion — close the
        // window here so the modal returns and the post-creation launch/nav flow can proceed.
        _ = vm.Completion.ContinueWith(
            (_, w) => Avalonia.Threading.Dispatcher.UIThread.Post(() => ((Window)w!).Close()),
            wizard);
        await wizard.ShowDialog(owner);
        return await vm.Completion;
    }

    public async Task ShowModulesAsync(InstallationRecord installation)
    {
        Window? owner = OwnerWindow();
        if (owner == null) return;

        var vm = new ModulesViewModel(installation, this);
        var dialog = new ModulesWindow { DataContext = vm };

        // Automation seam (--modules --screenshot): capture the dialog itself, then quit.
        if (Program.Options?.Modules == true && !string.IsNullOrWhiteSpace(Program.Options.ScreenshotPath))
        {
            string shot = Program.Options.ScreenshotPath;
            int delay = Math.Max(1500, Program.Options.ScreenshotDelayMs);
            dialog.Opened += (_, _) => Avalonia.Threading.DispatcherTimer.RunOnce(() =>
            {
                try
                {
                    WindowCapture.Save(dialog, shot);
                    HubLog.Info($"Screenshot saved: {shot}");
                }
                catch (Exception ex)
                {
                    HubLog.Error("Modules dialog screenshot failed.", ex);
                }
                finally
                {
                    dialog.Close();
                    owner.Close();
                }
            }, TimeSpan.FromMilliseconds(delay));
        }

        await dialog.ShowDialog(owner);
    }

    public async Task<string?> PickEditorInstallAsync()
    {
        IStorageProvider? storage = TopLevelStorage();
        if (storage == null) return null;

        var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = Loc.Instance["dialog.pickEditor.title"],
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("XEngine Editor") { Patterns = ["XEngine.Editor.exe", "XEngine.Editor"] },
                new FilePickerFileType("*") { Patterns = ["*"] },
            ],
        });
        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    public async Task<string?> ShowXEngineChoiceAsync(IReadOnlyList<string> xengineFiles)
    {
        Window? owner = OwnerWindow();
        if (owner == null || xengineFiles.Count == 0) return null;

        var list = new ListBox { MinHeight = 140, MinWidth = 480 };
        foreach (string file in xengineFiles)
            list.Items.Add(new TextBlock { Text = file, Margin = new Thickness(4, 6) });
        list.SelectedIndex = 0;

        var message = new TextBlock
        {
            Text = Loc.Instance["dialog.xengine.message"],
            TextWrapping = TextWrapping.Wrap,
            MaxWidth = 480,
            Margin = new Thickness(0, 0, 0, 12),
        };

        var ok = new Button { Content = Loc.Instance["common.ok"], IsDefault = true, MinWidth = 90 };
        var cancel = new Button { Content = Loc.Instance["common.cancel"], IsCancel = true, MinWidth = 90 };
        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Spacing = 10,
            Margin = new Thickness(0, 16, 0, 0),
        };
        buttons.Children.Add(cancel);
        buttons.Children.Add(ok);

        var root = new StackPanel { Margin = new Thickness(20) };
        root.Children.Add(message);
        root.Children.Add(list);
        root.Children.Add(buttons);

        var dialog = new Window
        {
            Title = Loc.Instance["dialog.xengine.title"],
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            ShowInTaskbar = false,
            Content = root,
        };

        string? chosen = null;
        ok.Click += (_, _) =>
        {
            chosen = (list.SelectedItem as TextBlock)?.Text;
            dialog.Close();
        };
        list.DoubleTapped += (_, _) =>
        {
            chosen = (list.SelectedItem as TextBlock)?.Text;
            dialog.Close();
        };
        cancel.Click += (_, _) => dialog.Close();

        await dialog.ShowDialog(owner);
        return chosen;
    }

    public async Task ShowErrorAsync(string titleKey, string hintKey, string detail)
    {
        Window? owner = OwnerWindow();
        if (owner == null) return;

        var ok = new Button
        {
            Content = Loc.Instance["common.ok"],
            IsDefault = true,
            IsCancel = true,
            MinWidth = 90,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0),
        };

        var root = new StackPanel { Margin = new Thickness(20), MaxWidth = 460 };
        root.Children.Add(new TextBlock
        {
            Text = Loc.Instance[titleKey],
            FontWeight = FontWeight.SemiBold,
            FontSize = 15,
            Margin = new Thickness(0, 0, 0, 8),
        });
        root.Children.Add(new TextBlock
        {
            Text = Loc.Instance[hintKey],
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 6),
        });
        root.Children.Add(new TextBlock
        {
            Text = detail,
            TextWrapping = TextWrapping.Wrap,
            FontSize = 12,
            Opacity = 0.7,
            TextTrimming = TextTrimming.CharacterEllipsis,
        });
        root.Children.Add(ok);

        var dialog = new Window
        {
            Title = Loc.Instance["dialog.error.title"],
            SizeToContent = SizeToContent.WidthAndHeight,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            CanResize = false,
            ShowInTaskbar = false,
            Content = root,
        };
        ok.Click += (_, _) => dialog.Close();

        await dialog.ShowDialog(owner);
    }

    private static IStorageProvider? TopLevelStorage() => OwnerWindow()?.StorageProvider;

    private static Window? OwnerWindow() =>
        (App.Current?.ApplicationLifetime as IClassicDesktopStyleApplicationLifetime)?.MainWindow;
}
