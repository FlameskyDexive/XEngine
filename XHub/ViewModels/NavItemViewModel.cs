using System;
using Avalonia.Media;
using CommunityToolkit.Mvvm.ComponentModel;
using XHub.Resources;
using XHub.Services;

namespace XHub.ViewModels;

/// <summary>One sidebar navigation entry: display key (localized), glyph, and target page.</summary>
public sealed class NavItemViewModel : ViewModelBase
{
    private readonly string _iconPath;
    private Geometry? _icon;

    public NavItemViewModel(HubPageId page, string key, string iconPath)
    {
        Page = page;
        Key = key;
        _iconPath = iconPath;

        // Live language switching: re-resolve the label whenever the shared dictionary changes.
        Loc.Instance.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName is "" or "Item[]" or null)
                OnPropertyChanged(nameof(Label));
        };
    }

    public HubPageId Page { get; }

    /// <summary>Localization key for the item label.</summary>
    public string Key { get; }

    /// <summary>Resolved, current-language label text.</summary>
    public string Label => Loc.Instance[Key];

    /// <summary>Raw path markup for the glyph (tests assert on this; parsing needs a UI thread).</summary>
    public string IconPath => _iconPath;

    /// <summary>
    /// Parsed on first use on the UI thread — parallel unit tests have no UI-thread affinity,
    /// so only the view layer may touch <see cref="StreamGeometry.Parse"/>.
    /// </summary>
    public Geometry Icon => _icon ??= StreamGeometry.Parse(_iconPath);
}
