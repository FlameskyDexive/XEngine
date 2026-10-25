// Sample controller for Settings.xui. Copy both next to each other under Assets/, run
// xui_scaffold to generate SettingsView.g.cs, and this compiles as written.

using XEngine.Async;
using XEngine.XUI;
using XEngine.XUI.Core;

/// <summary>
/// The half of a screen that is not visual: what its controls do, and what it says when they do it.
/// </summary>
/// <remarks>
/// Nothing here touches a GameObject. Every control is reached through <c>View</c>, which is generated
/// from the document's ids — so renaming an id in the XML is a compile error rather than a null at run
/// time.
/// </remarks>
[XuiScreen("Settings")]
public partial class Settings : XuiController<SettingsView>
{
    private float _volume = 70f;
    private bool _muted;
    private string _name = string.Empty;

    /// <summary>Runs once. Subscribe here, not in OnShow — OnShow runs on every re-show.</summary>
    protected override void OnRegisterEvents()
    {
        View.SliderVolume.OnValueChanged += value => _volume = value;
        View.TogMute.OnValueChanged += isOn => _muted = isOn;
        View.InputName.OnSubmitted += text => { _name = text; ShowSavedState(); };

        View.BtnCancel.OnClick += () => Xui.Hide<Settings>();
        View.BtnSave.OnClick += () => _ = SaveAsync();

        // Text this controller writes is not re-translated by a locale switch — the catalog owns only
        // what the document authored — so re-render it when the language changes.
        Xui.LocaleChanged += _ => ShowSavedState();
    }

    /// <summary>Runs every time the screen becomes visible.</summary>
    protected override void OnShow(object? context)
    {
        View.TxtTitle.Text = Xui.Tr("Settings");
        View.SliderVolume.Value = _volume;
        View.TogMute.IsOn = _muted;
        View.InputName.Text = _name;
        ShowSavedState();
    }

    private async XTask SaveAsync()
    {
        // The modal returns the answer where the question was asked, rather than in a callback that has
        // lost the context it needed.
        if (!await Xui.Confirm(Xui.Tr("Save settings"), Xui.Tr("Apply these changes now?"))) return;

        Xui.ShowLoading(Xui.Tr("Saving…"));
        try
        {
            await Persist();
        }
        finally
        {
            // Reference-counted, and released in a finally: a save that throws must not leave the
            // screen behind an overlay nothing will ever remove.
            Xui.HideLoading();
        }

        Xui.Toast(Xui.Tr("Settings saved"));
        ShowSavedState();
    }

    private void ShowSavedState()
        => View.TxtStatus.Text = _name.Length == 0
            ? Xui.Tr("No display name set")
            : string.Format(Xui.Tr("Signed in as {0}"), _name);

    /// <summary>Stands in for whatever really persists this — a file, a service, a save game.</summary>
    private static XTask Persist() => XTask.CompletedTask;
}
