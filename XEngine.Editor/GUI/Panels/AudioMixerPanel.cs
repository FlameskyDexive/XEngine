// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.IO;

using XEngine.Editor.Core;
using XEngine.Editor.Projects;
using XEngine.Editor.Theming;
using XEngine.Echo;
using XEngine.OrigamiUI;
using XEngine.PaperUI;
using XEngine.Runtime;
using XEngine.Runtime.Audio;
using XEngine.Scribe;

using TextAlignment = XEngine.PaperUI.TextAlignment;

namespace XEngine.Editor.GUI.Panels;

/// <summary>
/// N1 Audio Mixer editor window: group tree with faders, Mute/Solo, routing, and snapshot
/// management for `.audiomixer` assets in the open project. All edits go through the
/// <see cref="AudioMixerInstance"/>, so sources that are playing right now pick the changes up
/// immediately (see the plan node N1). Saving persists the asset file (and migrates legacy
/// assets by writing the minted group ids).
/// </summary>
public sealed class AudioMixerPanel : DockPanel
{
    private const float RowHeight = 30f;

    private readonly List<string> _mixerPaths = new();
    private DateTime _lastScan = DateTime.MinValue;
    private int _selectedMixer = -1;
    private string _loadedPath = "";
    private AudioMixer? _asset;
    private AudioMixerInstance? _instance;
    private string _status = "";
    private string _newGroupName = "";
    private string _newSnapshotName = "";

    [MenuItem("Window/Audio/Mixer", priority: 0)]
    private static void Open() => EditorApplication.Instance?.OpenPanel(typeof(AudioMixerPanel));

    public override string Title => "Audio Mixer";
    public override string Icon => EditorIcons.VolumeHigh;
    public override float HeaderWidth => 34f;

    public override void OnHeaderContent(Paper paper, float width, float height)
        => Origami.Button(paper, "amx_header_save", EditorIcons.FloppyDisk, SaveAsset)
            .Width(MathF.Min(28f, width)).Height(MathF.Min(26f, height))
            .Tooltip("Save mixer asset").Show();

    public override void OnGUI(Paper paper, float width, float height)
    {
        FontFile? font = EditorTheme.DefaultFont;
        if (font == null) return;
        if (Project.Current == null)
        {
            EditorGUI.EmptyState(paper, "amx_no_project", "Open a project to edit audio mixers.", font);
            return;
        }

        ScanMixers();
        if (_selectedMixer >= _mixerPaths.Count) _selectedMixer = _mixerPaths.Count - 1;

        DrawMixerSelector(paper, font, width);

        if (_asset == null)
        {
            EditorGUI.EmptyState(paper, "amx_no_mixer",
                _mixerPaths.Count == 0
                    ? "No .audiomixer assets in this project. Create one via Assets/Create."
                    : "Select a mixer above.", font);
            return;
        }

        using (paper.Row("amx_scroll").Width(width).Height(height - RowHeight).Clip().Enter())
        using (paper.Column("amx_content").Width(width).Padding(10, 10, 8, 8).Enter())
        {
            DrawGroups(paper, font);
            DrawAddGroup(paper, font);
            DrawSnapshots(paper, font);

            paper.Box("amx_status").Width(width - 20).Height(RowHeight).Padding(8, 4, 0, 0)
                .Text(_status, font).TextColor(EditorTheme.InkDim).FontSize(EditorTheme.FontSizeSmall)
                .Alignment(TextAlignment.MiddleLeft).IsNotInteractable();
        }
    }

    // -----------------------------------------------------------------------------------------
    // Asset discovery & persistence
    // -----------------------------------------------------------------------------------------

    private void ScanMixers()
    {
        if ((DateTime.UtcNow - _lastScan).TotalSeconds < 2) return;
        _lastScan = DateTime.UtcNow;

        _mixerPaths.Clear();
        try
        {
            string assets = Project.Current.AssetsPath;
            if (!string.IsNullOrEmpty(assets) && Directory.Exists(assets))
            {
                foreach (var file in Directory.EnumerateFiles(assets, "*.audiomixer", SearchOption.AllDirectories))
                    _mixerPaths.Add(Path.GetRelativePath(assets, file));
            }
        }
        catch (Exception)
        {
            // enumeration races with project IO; retry on the next scan
        }

        if (_loadedPath.Length > 0 && (_selectedMixer < 0 || _selectedMixer >= _mixerPaths.Count
                || _mixerPaths[_selectedMixer] != _loadedPath))
            _selectedMixer = _mixerPaths.IndexOf(_loadedPath);

        // First open with no selection: auto-select the first mixer so the window is useful
        // immediately (and panel-screenshot captures land on real content).
        if (_selectedMixer < 0 && _mixerPaths.Count > 0)
            SelectMixer(0);
    }

    private void SelectMixer(int index)
    {
        if (index < 0 || index >= _mixerPaths.Count) return;
        _selectedMixer = index;
        LoadSelected();
    }

    private void LoadSelected()
    {
        _asset = null;
        _instance = null;
        _loadedPath = "";
        if (_selectedMixer < 0 || _selectedMixer >= _mixerPaths.Count) return;

        try
        {
            string absolute = Path.Combine(Project.Current.AssetsPath, _mixerPaths[_selectedMixer]);
            var echo = EchoObject.ReadFromString(File.ReadAllText(absolute));
            var mixer = new AudioMixer { Name = Path.GetFileNameWithoutExtension(_mixerPaths[_selectedMixer]) };
            mixer.Deserialize(echo, new SerializationContext());
            _asset = mixer;
            _instance = AudioMixerInstance.Get(mixer);
            _loadedPath = _mixerPaths[_selectedMixer];
            _status = $"{_loadedPath}  ·  {_asset.Groups.Count} groups  ·  {_asset.Snapshots.Count} snapshots";
        }
        catch (Exception e)
        {
            _status = "Failed to load: " + e.Message;
        }
    }

    private void SaveAsset()
    {
        if (_asset == null || _loadedPath.Length == 0) return;
        try
        {
            _asset.EnsureGroupIds();
            var echo = EchoObject.NewCompound();
            _asset.Serialize(ref echo, new SerializationContext());
            string absolute = Path.Combine(Project.Current.AssetsPath, _loadedPath);
            File.WriteAllText(absolute, echo.WriteToString());
            _status = $"Saved {_loadedPath} (group ids persisted)";
        }
        catch (Exception e)
        {
            _status = "Save failed: " + e.Message;
        }
    }

    // -----------------------------------------------------------------------------------------
    // Drawing
    // -----------------------------------------------------------------------------------------

    private void DrawMixerSelector(Paper paper, FontFile font, float width)
    {
        using (paper.Row("amx_selector").Width(width).Height(RowHeight + 6).Padding(10, 10, 0, 2).RowBetween(6).Enter())
        {
            Origami.IconButton(paper, "amx_prev", EditorIcons.ChevronLeft, () => SelectMixer(_selectedMixer - 1))
                .Width(26).Height(RowHeight).Disabled(_mixerPaths.Count == 0).Show();
            string label = _mixerPaths.Count == 0
                ? "no .audiomixer assets"
                : $"{_selectedMixer + 1}/{_mixerPaths.Count}: {_mixerPaths[Math.Max(0, _selectedMixer)]}";
            paper.Box("amx_sel_label").Width(width - 96).Height(RowHeight).Padding(8, 4, 0, 0)
                .Text(label, font).TextColor(EditorTheme.Ink500).FontSize(EditorTheme.FontSizeSmall)
                .Alignment(TextAlignment.MiddleLeft).IsNotInteractable();
            Origami.IconButton(paper, "amx_next", EditorIcons.ChevronRight, () => SelectMixer(_selectedMixer + 1))
                .Width(26).Height(RowHeight).Disabled(_selectedMixer + 1 >= _mixerPaths.Count).Show();
            Origami.IconButton(paper, "amx_reload", EditorIcons.ArrowsRotate, LoadSelected)
                .Width(26).Height(RowHeight).Disabled(_selectedMixer < 0).Show();
        }
    }

    private void DrawGroups(Paper paper, FontFile font)
    {
        if (_asset == null || _instance == null) return;

        paper.Box("amx_groups_header").Width(_asset.Groups.Count * 0 + 380).Height(24).Padding(0, 2, 0, 0)
            .Text("GROUPS   (edits apply to playing audio immediately)", font)
            .TextColor(EditorTheme.InkDim).FontSize(EditorTheme.FontSizeSmall)
            .Alignment(TextAlignment.MiddleLeft).IsNotInteractable();

        for (int i = 0; i < _asset.Groups.Count; i++)
        {
            var group = _asset.Groups[i];
            int depth = DepthOf(i);
            string indent = new(' ', depth * 2);
            string label = DescribeGroup(i, indent);

            // name + routing + duck/eff volume + mute/solo
            using (paper.Row($"amx_g{i}_head").Width(380).Height(RowHeight).RowBetween(6).Enter())
            {
                paper.Box($"amx_g{i}_name").Width(230).Height(RowHeight).Padding(8, 4, 0, 0)
                    .Text(label, font)
                    .TextColor(EditorTheme.Ink500).FontSize(EditorTheme.FontSizeSmall)
                    .Alignment(TextAlignment.MiddleLeft).IsNotInteractable();

                // Active mute/solo get a filled primary variant; inactive stay default.
                var muteBtn = Origami.Button(paper, $"amx_g{i}_mute", group.Mute ? "MUTE" : "Mute",
                        () => _instance!.SetMute(i, !group.Mute))
                    .Width(56).Height(RowHeight - 6);
                if (group.Mute) muteBtn.Primary();
                muteBtn.Show();

                var soloBtn = Origami.Button(paper, $"amx_g{i}_solo", group.Solo ? "SOLO" : "Solo",
                        () => _instance!.SetSolo(i, !group.Solo))
                    .Width(56).Height(RowHeight - 6);
                if (group.Solo) soloBtn.Primary();
                soloBtn.Show();
            }

            // fader writes through the instance: playing sources hear it this frame
            EditorGUI.SliderRow(paper, $"amx_g{i}_vol", $"{indent}volume dB",
                _instance.GetEffectiveVolumeDB(i), AudioMixerMath.MinVolumeDB, AudioMixerMath.MaxVolumeDB,
                v => _instance.SetGroupVolumeDB(i, v), format: "F1");

            // live post-fader meter (peak fills the bar; rms dims underneath) — N2
            _instance.GetMetering(i, out float meterPeak, out float meterRms);
            float meterWidth = 380f;
            paper.Box($"amx_g{i}_meterbg").Width(meterWidth).Height(6)
                .BackgroundColor(EditorTheme.BorderSoft).IsNotInteractable();
            float peakFill = Math.Clamp(meterPeak, 0f, 1f);
            if (peakFill > 0.002f)
                paper.Box($"amx_g{i}_meterfill").Width(meterWidth * peakFill).Height(6)
                    .BackgroundColor(peakFill > 0.99f ? EditorTheme.Red400 : EditorTheme.Purple400)
                    .IsNotInteractable();
            float rmsFill = Math.Clamp(meterRms, 0f, 1f);
            if (rmsFill > 0.002f)
                paper.Box($"amx_g{i}_meterrms").Width(meterWidth * rmsFill).Height(2)
                    .BackgroundColor(EditorTheme.Ink500).IsNotInteractable();
        }
    }

    private void DrawAddGroup(Paper paper, FontFile font)
    {
        if (_asset == null || _instance == null) return;

        using (paper.Row("amx_addgroup").Width(380).Height(RowHeight).RowBetween(6).Enter())
        {
            Origami.TextField(paper, "amx_newgroup_name", _newGroupName, v => _newGroupName = v)
                .Width(230).Height(RowHeight - 6).Show();
            Origami.Button(paper, "amx_addgroup_btn", "+ Group", () =>
                {
                    if (string.IsNullOrWhiteSpace(_newGroupName)) return;
                    _asset.AddGroup(_newGroupName.Trim(), AudioMixer.MasterGroupName);
                    _instance!.ForceRecompile();
                    _newGroupName = "";
                })
                .Width(110).Height(RowHeight - 6).Show();
        }
    }

    private void DrawSnapshots(Paper paper, FontFile font)
    {
        if (_asset == null || _instance == null) return;

        paper.Box("amx_snap_header").Width(380).Height(24).Padding(0, 6, 0, 0)
            .Text("SNAPSHOTS", font).TextColor(EditorTheme.InkDim)
            .FontSize(EditorTheme.FontSizeSmall).Alignment(TextAlignment.MiddleLeft).IsNotInteractable();

        for (int i = 0; i < _asset.Snapshots.Count; i++)
        {
            var snapshot = _asset.Snapshots[i];
            bool active = _instance.ActiveSnapshot == snapshot.Name;
            int index = i;
            using (paper.Row($"amx_snap{i}").Width(380).Height(RowHeight).RowBetween(6).Enter())
            {
                paper.Box($"amx_snap{i}_name").Width(230).Height(RowHeight).Padding(8, 4, 0, 0)
                    .Text($"{snapshot.Name} ({snapshot.Volumes.Count} overrides)", font)
                    .TextColor(active ? EditorTheme.Purple400 : EditorTheme.Ink500).FontSize(EditorTheme.FontSizeSmall)
                    .Alignment(TextAlignment.MiddleLeft).IsNotInteractable();
                var activateBtn = Origami.Button(paper, $"amx_snap{i}_go", active ? "ACTIVE" : "Activate",
                        () => _instance!.ApplySnapshot(_asset!.Snapshots[index].Name))
                    .Width(110).Height(RowHeight - 6);
                if (active) activateBtn.Primary();
                activateBtn.Show();
            }
        }

        using (paper.Row("amx_snapadd").Width(380).Height(RowHeight).RowBetween(6).Enter())
        {
            Origami.TextField(paper, "amx_newsnap_name", _newSnapshotName, v => _newSnapshotName = v)
                .Width(230).Height(RowHeight - 6).Show();
            Origami.Button(paper, "amx_snap_capture", "+ Capture", CaptureSnapshot)
                .Width(110).Height(RowHeight - 6).Show();
            Origami.Button(paper, "amx_snap_clear", EditorIcons.Xmark, () => _instance!.ClearSnapshot())
                .Width(30).Height(RowHeight - 6).Disabled(_instance.ActiveSnapshot.Length == 0).Show();
        }
    }

    /// <summary>Captures the current group volumes as a snapshot (authoring action on the asset).</summary>
    private void CaptureSnapshot()
    {
        if (_asset == null || string.IsNullOrWhiteSpace(_newSnapshotName)) return;
        var snapshot = new AudioMixerSnapshotData { Name = _newSnapshotName.Trim() };
        foreach (var group in _asset.Groups)
            snapshot.Volumes.Add(new AudioMixerSnapshotVolume { Group = group.Name, VolumeDB = group.VolumeDB });
        _asset.Snapshots.RemoveAll(s => s.Name == snapshot.Name);
        _asset.Snapshots.Add(snapshot);
        _newSnapshotName = "";
        _status = $"Snapshot '{snapshot.Name}' captured";
    }

    /// <summary>Group label: name, routing, ducking source and effective volume (N3 UI).</summary>
    private string DescribeGroup(int index, string indent)
    {
        if (_asset == null || _instance == null) return "";
        var group = _asset.Groups[index];
        string text = $"{indent}{group.Name}  → {group.Parent}";
        var (reductionDB, holders) = _instance.Automation.GetDuckInfo(index);
        if (reductionDB > 0.01f)
            text += $"  [duck -{reductionDB:0.#}dB ×{holders}]";
        text += $"  ·  eff {_instance.GetEffectiveVolumeDB(index):0.#}dB";
        return text;
    }

    private int DepthOf(int index)
    {
        if (_asset == null) return 0;
        int depth = 0;
        int guard = 0;
        string parent = _asset.Groups[index].Parent;
        while (!string.IsNullOrEmpty(parent) && guard++ < 16)
        {
            depth++;
            var found = _asset.Groups.Find(g => g.Name == parent);
            if (found == null) break;
            parent = found.Parent;
        }
        return depth;
    }
}
