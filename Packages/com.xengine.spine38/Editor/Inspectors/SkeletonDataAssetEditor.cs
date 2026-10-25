using System;
using System.Collections.Generic;
using System.IO;

using XEngine.Echo;
using XEngine.Editor;
using XEngine.Editor.Core;
using XEngine.Editor.GUI;
using XEngine.Editor.Inspector;
using XEngine.Editor.Projects;
using XEngine.Editor.Theming;
using XEngine.OrigamiUI;
using XEngine.PaperUI;
using XEngine.PaperUI.LayoutEngine;
using XEngine.Runtime;
using XEngine.Spine38;

using Color = System.Drawing.Color;

namespace XEngine.Spine38.Editor;

/// <summary>
/// SkeletonDataAsset inspector mirroring spine-unity's (Unity 6.7) layout: SkeletonData
/// (source + scale), Atlas assets, Mix Settings (default + per-pair custom durations),
/// an Animations table with per-row play buttons, and a live animated preview in the
/// inspector's docked preview bar (skin dropdown + transport).
/// </summary>
[CustomAssetEditor(typeof(SkeletonDataAsset))]
public sealed class SkeletonDataAssetEditor : AssetImporterEditor
{
    private const float PreviewTransportHeight = 28f;

    // ── Preview state (applied lazily by OnPreviewGUI) ──
    private PreviewRenderer? _preview;
    private Guid _previewedGuid;
    private SkeletonDataAsset? _previewedAsset;
    private string _previewAnim = "";   // "" = setup pose
    private string _previewSkin = "";
    private string _appliedAnim = "";
    private string _appliedSkin = "";
    private bool _playing;
    private float _speed = 1f;
    private bool _stateDirty;           // mix settings edited -> rebuild AnimationState
    private int _selectedAnimRow = -1;

    // ── Importer settings (scale lives in the .meta; mixes mirror asset fields) ──
    private EchoObject? _cachedSettings;
    private Guid _settingsGuid;
    private bool _scaleDirty;
    private AssetEntry? _entry;

    // ── Cached per-asset animation durations (row table) ──
    private Guid _durationsGuid;
    private readonly List<float> _durations = new();

    public override void OnGUI(Paper paper, string id, AssetEntry entry, EngineObject? asset)
    {
        id = $"{id}_{entry.Guid:N}";
        _entry = entry;
        var data = asset as SkeletonDataAsset;
        if (data == null)
        {
            Origami.Label(paper, $"{id}_none", "SkeletonDataAsset failed to load.").Show();
            return;
        }

        EnsureSettings(entry);
        EnsureDurations(entry.Guid, data);
        if (_previewedGuid != entry.Guid)
            ResetPreviewSelection(data);

        DrawSkeletonDataSection(paper, id, entry, data);
        DrawAtlasSection(paper, id, data);
        DrawMixSettingsSection(paper, id, data);
        DrawAnimationsSection(paper, id, data);
    }

    // ─────────────────────────────────────────────────────────────
    //  SkeletonData
    // ─────────────────────────────────────────────────────────────

    private void DrawSkeletonDataSection(Paper paper, string id, AssetEntry entry, SkeletonDataAsset data)
    {
        var font = EditorTheme.DefaultFont;
        EditorGUI.SectionHeader(paper, $"{id}_sd_hdr", "SkeletonData", first: true);

        string sourceLabel = data.IsBinary ? "Skeleton Binary" : "Skeleton JSON";
        EditorGUI.Row(paper, $"{id}_src", sourceLabel, () =>
        {
            if (font == null) return;
            paper.Box($"{id}_src_pill").Width(UnitValue.Auto).Height(22)
                .Margin(0, 0, UnitValue.StretchOne, UnitValue.StretchOne).Rounded(6).Padding(9, 9, 0, 0)
                .BackgroundColor(EditorTheme.Glass).BorderColor(EditorTheme.BorderSoft).BorderWidth(1)
                .Text($"{EditorIcons.Bone}  {Path.GetFileName(entry.Path)}", font)
                .TextColor(EditorTheme.Ink400).FontSize(EditorTheme.FontSizeSmall)
                .Alignment(TextAlignment.MiddleLeft);
        });

        float scale = _cachedSettings != null && _cachedSettings.TryGet(Spine38SkeletonImporter.ScaleSetting, out EchoObject? scaleTag)
            ? scaleTag.FloatValue
            : data.Scale;
        EditorGUI.Row(paper, $"{id}_scale", "Scale", () =>
            Origami.NumericField(paper, $"{id}_scale_v", scale, v =>
            {
                if (_cachedSettings == null) return;
                _cachedSettings[Spine38SkeletonImporter.ScaleSetting] = new EchoObject(v);
                _scaleDirty = true;
            }).Width(90f).Show());

        if (_scaleDirty)
        {
            using (paper.Row($"{id}_scale_apply_row").Height(UnitValue.Auto)
                .Padding(Origami.Current.Metrics.PaddingLarge, Origami.Current.Metrics.PaddingLarge, 0, 6).Enter())
            {
                EditorGUI.CtaButton(paper, $"{id}_scale_apply", $"{EditorIcons.FloppyDisk}  Apply (Save & Reimport)",
                    EditorTheme.Accent, () =>
                    {
                        SaveMeta(reimport: true);
                        _scaleDirty = false;
                    });
            }
        }
    }

    // ─────────────────────────────────────────────────────────────
    //  Atlas
    // ─────────────────────────────────────────────────────────────

    private void DrawAtlasSection(Paper paper, string id, SkeletonDataAsset data)
    {
        var font = EditorTheme.DefaultFont;
        EditorGUI.SectionHeader(paper, $"{id}_atlas_hdr", "Atlas");

        EditorGUI.Row(paper, $"{id}_atlas_n", "Atlas Assets", () =>
            Origami.Label(paper, $"{id}_atlas_nv", data.AtlasAssets.Count.ToString()).Show());

        for (int i = 0; i < data.AtlasAssets.Count; i++)
        {
            int index = i;
            EngineObject? atlas = AssetDatabase.Get(data.AtlasAssets[i].AssetID);
            string label = atlas != null ? $"{atlas.Name} (Spine Atlas Asset)" : "(missing atlas)";
            EditorGUI.Row(paper, $"{id}_atlas_{i}", $"Element {i}", () =>
            {
                if (font == null) return;
                paper.Box($"{id}_atlas_{index}_pill").Width(UnitValue.Auto).Height(22)
                    .Margin(0, 0, UnitValue.StretchOne, UnitValue.StretchOne).Rounded(6).Padding(9, 9, 0, 0)
                    .BackgroundColor(EditorTheme.Glass).BorderColor(EditorTheme.BorderSoft).BorderWidth(1)
                    .Hovered.BorderColor(EditorTheme.BorderStrong).End()
                    .Text($"{EditorIcons.Image}  {label}", font)
                    .TextColor(EditorTheme.Ink400).FontSize(EditorTheme.FontSizeSmall)
                    .Alignment(TextAlignment.MiddleLeft)
                    .OnClick(0, (_, _) =>
                    {
                        Guid guid = data.AtlasAssets[index].AssetID;
                        if (guid != Guid.Empty)
                            Selection.Ping(guid);
                    });
            });
        }
    }

    // ─────────────────────────────────────────────────────────────
    //  Mix Settings
    // ─────────────────────────────────────────────────────────────

    private void DrawMixSettingsSection(Paper paper, string id, SkeletonDataAsset data)
    {
        EditorGUI.SectionHeader(paper, $"{id}_mix_hdr", "Mix Settings");
        var m = Origami.Current.Metrics;

        using (paper.Column($"{id}_mix_wrap").Height(UnitValue.Auto)
            .Margin(m.PaddingLarge, m.PaddingLarge, 0, m.SpacingLarge).Enter())
        {
            Origami.Foldout(paper, $"{id}_mix_fold", "Animation State Data").DefaultExpanded().Body(() =>
            {
                EditorGUI.Row(paper, $"{id}_mix_def", "Default Mix Duration", () =>
                    Origami.NumericField(paper, $"{id}_mix_def_v", data.DefaultMix, v =>
                    {
                        data.DefaultMix = MathF.Max(0f, v);
                        OnMixSettingsChanged(data);
                    }).Width(90f).Show(), compact: true);

                Origami.Label(paper, $"{id}_mix_custom_lbl", "Custom Mix Durations").Show();

                IReadOnlyList<string> anims = data.AnimationNames;
                for (int i = 0; i < data.CustomMixes.Count; i++)
                {
                    int index = i;
                    SpineMixEntry mix = data.CustomMixes[i];
                    using (paper.Row($"{id}_mix_row_{i}").Height(UnitValue.Auto).RowBetween(6)
                        .Margin(0, 0, 2, 2).Enter())
                    {
                        Origami.Dropdown(paper, $"{id}_mix_from_{i}", IndexOf(anims, mix.From), sel =>
                        {
                            mix.From = anims[sel];
                            OnMixSettingsChanged(data);
                        }, anims).Show();
                        Origami.Dropdown(paper, $"{id}_mix_to_{i}", IndexOf(anims, mix.To), sel =>
                        {
                            mix.To = anims[sel];
                            OnMixSettingsChanged(data);
                        }, anims).Show();
                        Origami.NumericField(paper, $"{id}_mix_dur_{i}", mix.Duration, v =>
                        {
                            mix.Duration = MathF.Max(0f, v);
                            OnMixSettingsChanged(data);
                        }).Width(64f).Show();
                        Origami.Button(paper, $"{id}_mix_del_{i}", "Delete", () =>
                        {
                            data.CustomMixes.RemoveAt(index);
                            OnMixSettingsChanged(data);
                        }).Show();
                    }
                }

                if (anims.Count > 0)
                {
                    Origami.Button(paper, $"{id}_mix_add", "Add Custom Mix", () =>
                    {
                        data.CustomMixes.Add(new SpineMixEntry
                        {
                            From = anims[0],
                            To = anims[anims.Count > 1 ? 1 : 0],
                            Duration = data.DefaultMix,
                        });
                        OnMixSettingsChanged(data);
                    }).Show();
                }
            });
        }
    }

    private void OnMixSettingsChanged(SkeletonDataAsset data)
    {
        data.ClearStateCache();
        _stateDirty = true;
        SaveMeta(reimport: false);
    }

    // ─────────────────────────────────────────────────────────────
    //  Animations table (Preview section body)
    // ─────────────────────────────────────────────────────────────

    private void DrawAnimationsSection(Paper paper, string id, SkeletonDataAsset data)
    {
        var font = EditorTheme.DefaultFont;
        if (font == null) return;
        var m = Origami.Current.Metrics;

        EditorGUI.SectionHeader(paper, $"{id}_pv_hdr", "Preview");

        using (paper.Column($"{id}_anims_wrap").Height(UnitValue.Auto)
            .Margin(m.PaddingLarge, m.PaddingLarge, 0, m.SpacingLarge).Enter())
        {
            Origami.Foldout(paper, $"{id}_anims_fold", $"Animations [{data.AnimationNames.Count}]")
                .DefaultExpanded().Body(() =>
            {
                Origami.Button(paper, $"{id}_setup_pose", "Setup Pose", () =>
                {
                    _previewAnim = "";
                    _playing = false;
                    _selectedAnimRow = -1;
                }).Show();

                if (data.AnimationNames.Count == 0)
                {
                    Origami.Label(paper, $"{id}_anims_none", "No animations.").Show();
                    return;
                }

                float rowH = EditorTheme.RowHeight + 4f;
                float tableH = 26f + Math.Clamp(data.AnimationNames.Count, 1, 10) * rowH;
                Origami.Table(paper, $"{id}_anims_tbl", _selectedAnimRow, i => _selectedAnimRow = i)
                    .Bordered(true)
                    .Scroll(380f, tableH).RowHeight(rowH)
                    .Virtualize()
                    .Column("Name", 2.4f)
                    .Column("Duration", 1f, align: TextAlignment.MiddleRight)
                    .OnRowActivate(i => PlayAnimationRow(data, i))
                    .RowCount(data.AnimationNames.Count)
                    .CellContent((row, col) => DrawAnimationCell(paper, font, id, data, row, col, rowH))
                    .Show();
            });

            if (data.EventNames.Count > 0)
            {
                Origami.Foldout(paper, $"{id}_events_fold", $"Events [{data.EventNames.Count}]").Body(() =>
                {
                    for (int i = 0; i < data.EventNames.Count; i++)
                        Origami.Label(paper, $"{id}_event_{i}", data.EventNames[i]).Show();
                });
            }
        }
    }

    private void DrawAnimationCell(Paper paper, XEngine.Scribe.FontFile font, string id,
        SkeletonDataAsset data, int row, int col, float rowH)
    {
        if ((uint)row >= (uint)data.AnimationNames.Count) return;
        string name = data.AnimationNames[row];

        if (col == 0)
        {
            using (paper.Row($"{id}_anim_c0_{row}").Height(rowH).RowBetween(4).Enter())
            {
                bool isCurrent = _playing && string.Equals(_previewAnim, name, StringComparison.Ordinal);
                string glyph = isCurrent ? EditorIcons.Pause : EditorIcons.CirclePlay;
                paper.Box($"{id}_anim_play_{row}").Width(20).Height(rowH)
                    .Text(glyph, font)
                    .TextColor(isCurrent ? EditorTheme.Accent : EditorTheme.Ink300)
                    .FontSize(12f).Alignment(TextAlignment.MiddleCenter)
                    .Hovered.TextColor(EditorTheme.Accent).End()
                    .OnClick(0, (_, e) =>
                    {
                        e.StopPropagation();
                        if (isCurrent)
                            _playing = false;
                        else
                            PlayAnimationRow(data, row);
                    });

                paper.Box($"{id}_anim_name_{row}").Width(UnitValue.Stretch()).Height(rowH)
                    .IsNotInteractable()
                    .Text(name, font).TextColor(EditorTheme.Ink500)
                    .FontSize(EditorTheme.FontSizeSmall).Alignment(TextAlignment.MiddleLeft).TextTruncate();
            }
        }
        else
        {
            float duration = row < _durations.Count ? _durations[row] : 0f;
            paper.Box($"{id}_anim_dur_{row}").Width(UnitValue.Stretch()).Height(rowH)
                .IsNotInteractable()
                .Text($"{duration:0.###}s", font).TextColor(EditorTheme.Ink300)
                .FontSize(EditorTheme.FontSizeSmall).Alignment(TextAlignment.MiddleRight);
        }
    }

    private void PlayAnimationRow(SkeletonDataAsset data, int row)
    {
        if ((uint)row >= (uint)data.AnimationNames.Count) return;
        _previewAnim = data.AnimationNames[row];
        _playing = true;
        _selectedAnimRow = row;
    }

    // ─────────────────────────────────────────────────────────────
    //  Docked preview bar (render + skin overlay + transport)
    // ─────────────────────────────────────────────────────────────

    public override bool HasPreview(EngineObject? asset)
        => asset is SkeletonDataAsset && !Graphics.IsHeadless;

    public override void OnPreviewGUI(Paper paper, string id, EngineObject asset, float width, float height)
    {
        if (asset is not SkeletonDataAsset data || Graphics.IsHeadless)
            return;

        _preview ??= new PreviewRenderer((int)width, (int)(height - PreviewTransportHeight));
        SyncPreviewSubject(data);

        if (_playing)
        {
            SpinePreview.Tick(_preview, (float)Time.UnscaledDeltaTime * _speed);
            // The preview scene has no update loop; keep editor frames coming while playing.
            EditorApplication.Instance?.FrameScheduler.RegisterAnimation(Time.UnscaledTotalTime + 0.25);
        }

        float viewH = height - PreviewTransportHeight;
        using (paper.Column($"{id}_col").Width(width).Height(height).Enter())
        {
            _preview.DrawPreview(paper, $"{id}_rt", width, viewH);
            DrawSkinOverlay(paper, id, data);
            DrawTransport(paper, id, data, width);
        }
    }

    private void SyncPreviewSubject(SkeletonDataAsset data)
    {
        if (_preview == null) return;

        Guid guid = data.AssetID;
        if (_previewedGuid != guid)
            ResetPreviewSelection(data);

        // Rebuild on first show, on selection change, and on reimport (new asset instance).
        if (!ReferenceEquals(_previewedAsset, data))
        {
            SpinePreview.Apply(_preview, data, string.IsNullOrEmpty(_previewAnim) ? null : _previewAnim,
                string.IsNullOrEmpty(_previewSkin) ? null : _previewSkin);
            _previewedAsset = data;
            _previewedGuid = guid;
            _appliedAnim = _previewAnim;
            _appliedSkin = _previewSkin;
            _stateDirty = false;
            return;
        }

        if (_stateDirty)
        {
            SpinePreview.ReinitializeState(_preview);
            _stateDirty = false;
        }

        if (!string.Equals(_appliedSkin, _previewSkin, StringComparison.Ordinal))
        {
            SpinePreview.SetSkin(_preview, _previewSkin);
            _appliedSkin = _previewSkin;
        }

        if (!string.Equals(_appliedAnim, _previewAnim, StringComparison.Ordinal))
        {
            if (string.IsNullOrEmpty(_previewAnim))
                SpinePreview.SetupPose(_preview);
            else
                SpinePreview.SetAnimation(_preview, _previewAnim);
            _appliedAnim = _previewAnim;
        }
    }

    private void ResetPreviewSelection(SkeletonDataAsset data)
    {
        int walk = -1;
        for (int i = 0; i < data.AnimationNames.Count; i++)
        {
            if (string.Equals(data.AnimationNames[i], "walk", StringComparison.Ordinal))
            {
                walk = i;
                break;
            }
        }

        _previewAnim = walk >= 0 ? "walk" : (data.AnimationNames.Count > 0 ? data.AnimationNames[0] : "");
        _appliedAnim = "";
        _playing = !string.IsNullOrEmpty(_previewAnim);
        _speed = 1f;
        _selectedAnimRow = walk >= 0 ? walk : (_previewAnim.Length > 0 ? 0 : -1);
        _stateDirty = false;
        _previewSkin = data.SkinNames.Count > 0 ? data.SkinNames[0] : "";
        _appliedSkin = "";
        _previewedGuid = data.AssetID;
        _previewedAsset = null;
    }

    private void DrawSkinOverlay(Paper paper, string id, SkeletonDataAsset data)
    {
        if (data.SkinNames.Count == 0) return;
        var font = EditorTheme.DefaultFont;
        if (font == null) return;

        using (paper.Row($"{id}_skin_ov")
            .PositionType(PositionType.SelfDirected).Position(8, 8)
            .Width(UnitValue.Auto).Height(24).RowBetween(6)
            .Layer(Layer.Overlay)
            .Enter())
        {
            paper.Box($"{id}_skin_lbl").Width(UnitValue.Auto).Height(24).IsNotInteractable()
                .Text("Skin", font).TextColor(EditorTheme.Ink400)
                .FontSize(EditorTheme.FontSizeSmall).Alignment(TextAlignment.MiddleLeft);
            Origami.Dropdown(paper, $"{id}_skin_dd", IndexOf(data.SkinNames, _previewSkin), sel =>
            {
                _previewSkin = data.SkinNames[sel];
            }, data.SkinNames).Show();
        }
    }

    private void DrawTransport(Paper paper, string id, SkeletonDataAsset data, float width)
    {
        var font = EditorTheme.DefaultFont;
        if (font == null) return;

        using (paper.Row($"{id}_tr").Width(width).Height(PreviewTransportHeight)
            .RowBetween(6).Padding(6, 6, 2, 2).Enter())
        {
            bool hasAnim = !string.IsNullOrEmpty(_previewAnim);
            string playGlyph = _playing ? EditorIcons.Pause : EditorIcons.CirclePlay;
            paper.Box($"{id}_tr_play").Width(24).Height(24).Rounded(6)
                .Margin(0, 0, UnitValue.StretchOne, UnitValue.StretchOne)
                .Hovered.BackgroundColor(EditorTheme.Hover).End()
                .Text(playGlyph, font)
                .TextColor(_playing ? EditorTheme.Accent : EditorTheme.Ink300)
                .FontSize(13f).Alignment(TextAlignment.MiddleCenter)
                .OnClick(0, (_, _) =>
                {
                    if (!hasAnim && data.AnimationNames.Count > 0)
                        PlayAnimationRow(data, 0);
                    else
                        _playing = !_playing;
                });

            paper.Box($"{id}_tr_name").Width(UnitValue.Stretch()).Height(24).IsNotInteractable()
                .Text(hasAnim ? _previewAnim : "(setup pose)", font)
                .TextColor(EditorTheme.Ink400).FontSize(EditorTheme.FontSizeSmall)
                .Alignment(TextAlignment.MiddleLeft).TextTruncate();

            Origami.Slider(paper, $"{id}_tr_spd", _speed, v => _speed = v, 0.1f, 2f).Width(80f).Show();
            paper.Box($"{id}_tr_spdl").Width(38).Height(24).IsNotInteractable()
                .Text($"{_speed:0.00}x", font).TextColor(EditorTheme.Ink300)
                .FontSize(EditorTheme.FontSizeSmall).Alignment(TextAlignment.MiddleRight);
        }
    }

    public override void OnPreviewCleanup()
    {
        _preview?.Dispose();
        _preview = null;
        _previewedAsset = null;
        _previewedGuid = Guid.Empty;
        _appliedAnim = "";
        _appliedSkin = "";
    }

    // ─────────────────────────────────────────────────────────────
    //  Importer settings persistence (.meta)
    // ─────────────────────────────────────────────────────────────

    private void EnsureSettings(AssetEntry entry)
    {
        if (_cachedSettings != null && _settingsGuid == entry.Guid)
            return;

        _cachedSettings = null;
        _scaleDirty = false;
        _settingsGuid = entry.Guid;

        string? metaPath = GetMetaPath(entry);
        if (metaPath == null) return;

        MetaFileData meta = MetaFile.Read(metaPath);
        EchoObject settings = meta.Settings ?? EchoObject.NewCompound();
        EchoObject? defaults = new Spine38SkeletonImporter().DefaultSettings();
        if (defaults != null)
        {
            foreach (var kvp in defaults.Tags)
                if (!settings.TryGet(kvp.Key, out _))
                    settings[kvp.Key] = kvp.Value.Clone();
        }

        _cachedSettings = settings;
    }

    /// <summary>Write scale + mix settings to the .meta so they survive reimports.</summary>
    private void SaveMeta(bool reimport)
    {
        if (_entry == null || _cachedSettings == null) return;
        string? metaPath = GetMetaPath(_entry);
        if (metaPath == null) return;

        // Mirror the asset's mix state into the settings compound (single writer for the meta).
        if (AssetDatabase.Get(_entry.Guid) is SkeletonDataAsset data)
        {
            _cachedSettings[Spine38SkeletonImporter.DefaultMixSetting] = new EchoObject(data.DefaultMix);
            EchoObject list = EchoObject.NewList();
            for (int i = 0; i < data.CustomMixes.Count; i++)
            {
                SpineMixEntry mix = data.CustomMixes[i];
                EchoObject item = EchoObject.NewCompound();
                item[Spine38SkeletonImporter.MixFromKey] = new EchoObject(mix.From);
                item[Spine38SkeletonImporter.MixToKey] = new EchoObject(mix.To);
                item[Spine38SkeletonImporter.MixDurationKey] = new EchoObject(mix.Duration);
                list.ListAdd(item);
            }
            _cachedSettings[Spine38SkeletonImporter.CustomMixesSetting] = list;
        }

        MetaFileData meta = MetaFile.Read(metaPath);
        meta.Settings = _cachedSettings;
        MetaFile.Write(metaPath, meta);

        if (reimport)
        {
            _cachedSettings = null;
            EditorAssetBackend.Instance?.Reimport(_entry.Guid);
        }
    }

    private static string? GetMetaPath(AssetEntry entry)
    {
        if (Project.Current == null) return null;
        string absPath = Path.Combine(Project.Current.AssetsPath, entry.Path);
        string metaPath = MetaFile.GetMetaPath(absPath);
        return File.Exists(metaPath) ? metaPath : null;
    }

    // ─────────────────────────────────────────────────────────────
    //  Helpers
    // ─────────────────────────────────────────────────────────────

    private void EnsureDurations(Guid guid, SkeletonDataAsset data)
    {
        if (_durationsGuid == guid && _durations.Count == data.AnimationNames.Count)
            return;

        _durationsGuid = guid;
        _durations.Clear();
        try
        {
            global::Spine.SkeletonData skeletonData = data.GetSkeletonData();
            for (int i = 0; i < data.AnimationNames.Count; i++)
            {
                global::Spine.Animation? animation = skeletonData.FindAnimation(data.AnimationNames[i]);
                _durations.Add(animation?.Duration ?? 0f);
            }
        }
        catch (Exception)
        {
            // Parse failure (e.g. atlas mid-import): durations stay empty; table shows 0s.
            for (int i = _durations.Count; i < data.AnimationNames.Count; i++)
                _durations.Add(0f);
        }
    }

    private static int IndexOf(IReadOnlyList<string> items, string value)
    {
        for (int i = 0; i < items.Count; i++)
        {
            if (string.Equals(items[i], value, StringComparison.Ordinal))
                return i;
        }

        return 0;
    }
}
