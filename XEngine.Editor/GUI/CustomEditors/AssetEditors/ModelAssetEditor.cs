using System;
using System.Collections.Generic;
using System.IO;

using XEngine.Echo;
using XEngine.Editor.AnimationEditor;
using XEngine.Editor.GUI;
using XEngine.Editor.GUI.PropertyEditors;
using XEngine.Editor.Importers;
using XEngine.Editor.Projects;
using XEngine.Editor.Theming;
using XEngine.OrigamiUI;
using XEngine.PaperUI;
using XEngine.PaperUI.LayoutEngine;
using XEngine.Runtime;
using XEngine.Runtime.MeshFeatures.Generation;
using XEngine.Runtime.Resources;

using static XEngine.Editor.GUI.EditorGUI;

namespace XEngine.Editor.Inspector;

/// <summary>
/// Unity-shaped Model Import Settings: Model / Rig / Animation / Materials tabs.
/// The Inspector preview bar plays the selected Animation-tab clip on this model.
/// </summary>
// Targets the importer, not the asset type: a model imports into a PrefabAsset like any prefab,
// so keying on the asset would hand these files the generic prefab editor and drop the import settings.
[CustomAssetEditor(typeof(Importers.EditorModelImporter))]
public class ModelAssetEditor : AssetImporterEditor
{
    public ModelImportTab ActiveTab { get; private set; } = ModelImportTab.Model;

    private bool _generateNormals = true;
    private bool _generateSmoothNormals;
    private bool _calculateTangents = true;
    private bool _flipUVs;
    private bool _globalScale;
    private bool _convertFileUnits = true;
    private float _unitScale = 1f;
    private bool _generateLightmapUVs;
    private bool _generateSDF;
    private int _sdfResolution = 64;
    private float _sdfPadding = 0.1f;
    private float _sdfMaxDistance = 0.25f;

    private bool _importAnimation = true;
    private bool _resampleCurves = true;
    private ModelAnimationType _animationType = ModelAnimationType.Generic;
    private ModelAvatarDefinition _avatarDefinition = ModelAvatarDefinition.NoAvatar;
    private int _skinBoneCount = 4;
    private static readonly int[] SkinInfluenceOptions = [1, 2, 4];
    private bool _stripBones;
    // Read-only capability captions are rebuilt on settings load, not on every draw.
    private string _unitsCapability = "";
    private string _skinCapability = "";
    private string _stripCapability = "";
    private string _resampleCapability = "";
    private string _avatarCapability = "";
    private bool _flipUvSupported;

    private bool _settingsLoaded;
    private bool _settingsDirty;
    private Guid _currentGuid;
    private ModelImportSettingsSession? _settingsSession;
    private Project? _settingsProject;
    private string? _settingsError;
    private bool _lastSettingsWrite;
    private int _selectedClipIndex;
    private readonly List<AnimationClip> _clips = new();
    private readonly List<Material> _materials = new();
    private EchoObject _materialRemaps = EchoObject.NewCompound();
    private static readonly AssetRefPropertyEditor s_materialField = new();

    /// <summary>Startup-only tab override (editor arg --select-asset-tab), consumed on the first
    /// OnGUI so acceptance captures can open e.g. the Animation tab without input automation.</summary>
    public static ModelImportTab? StartupTab;

    public bool TryGetPreviewClip(out AnimationClip clip)
    {
        clip = null!;
        if (!ModelImportEditorModel.ShouldPreviewClip(ActiveTab, _importAnimation, _clips.Count))
            return false;
        int i = ModelImportEditorModel.ClampClipIndex(_selectedClipIndex, _clips.Count);
        if (i < 0) return false;
        clip = _clips[i];
        return clip != null;
    }

    /// <summary>Guarded variant for the Inspector preview bar: only serves clips while this
    /// editor instance is tracking <paramref name="assetGuid"/>, so a stale clip list from a
    /// previously inspected model can never animate another prefab's preview.</summary>
    public bool TryGetPreviewClip(Guid assetGuid, out AnimationClip clip)
    {
        clip = null!;
        if (assetGuid == Guid.Empty || _currentGuid != assetGuid) return false;
        return TryGetPreviewClip(out clip);
    }

    public override void OnGUI(Paper paper, string id, AssetEntry entry, EngineObject? asset)
    {
        if (_currentGuid != entry.Guid || _settingsProject != Project.Current
            || (_settingsSession != null && !_settingsSession.Matches(Project.Current?.AssetsPath ?? "", entry)))
        {
            _currentGuid = entry.Guid;
            _settingsLoaded = false;
            _settingsDirty = false;
            _settingsSession = null;
            _settingsError = null;
            _settingsProject = Project.Current;
            ResetSettings();
            _selectedClipIndex = 0;
            ActiveTab = ModelImportTab.Model;
        }
        if (StartupTab != null)
        {
            ActiveTab = StartupTab.Value;
            StartupTab = null;
        }

        id = $"{id}_{entry.Guid:N}";

        var font = EditorTheme.DefaultFont;
        if (font == null) return;
        var m = Origami.Current.Metrics;
        var model = asset as PrefabAsset;

        if (!_settingsLoaded)
        {
            LoadSettingsFromMeta(entry);
            _settingsLoaded = true;
        }

        var session = _settingsSession;
        if (session == null)
        {
            var targetProject = Project.Current;
            DrawSettingsError(paper, id);
            Origami.Button(paper, $"{id}_reload", "Reload Settings", () =>
            {
                if (_currentGuid == entry.Guid && ReferenceEquals(Project.Current, targetProject)) LoadSettingsFromMeta(entry);
            }).Show();
            return;
        }

        RefreshSubAssets(entry);

        Origami.Header(paper, $"{id}_h", $"{EditorIcons.Cube}  {entry.Path} Import Settings").Show();

        Origami.Tabs(paper, $"{id}_tabs", (int)ActiveTab, v => { if (Owns(session)) ActiveTab = (ModelImportTab)v; })
            .Underline()
            .Height(22)
            .Tab("Model")
            .Tab("Rig")
            .Tab("Animation")
            .Tab("Materials")
            .Show();

        switch (ActiveTab)
        {
            case ModelImportTab.Model:
                DrawModelTab(paper, id, model, entry, m, session);
                break;
            case ModelImportTab.Rig:
                DrawRigTab(paper, id, session);
                break;
            case ModelImportTab.Animation:
                DrawAnimationTab(paper, id, m, session);
                break;
            case ModelImportTab.Materials:
                DrawMaterialsTab(paper, id, m, session);
                break;
        }

        DrawSettingsError(paper, id);
        DrawApplyRevert(paper, id, entry, font, m, session);
    }

    private void RefreshSubAssets(AssetEntry entry)
    {
        _clips.Clear();
        _materials.Clear();
        SubAssetEntry[] subs = entry.SubAssets;
        for (int i = 0; i < subs.Length; i++)
        {
            Type? t = subs[i].Type;
            if (t == null) continue;
            EngineObject? loaded = AssetDatabase.Get(subs[i].Guid);
            if (loaded is AnimationClip clip)
                _clips.Add(clip);
            else if (loaded is Material mat)
                _materials.Add(mat);
        }
        _selectedClipIndex = ModelImportEditorModel.ClampClipIndex(_selectedClipIndex, _clips.Count);
        if (_selectedClipIndex < 0) _selectedClipIndex = 0;
    }

    private void DrawModelTab(Paper paper, string id, PrefabAsset? model, AssetEntry entry, OrigamiMetrics m, ModelImportSettingsSession session)
    {
        if (model != null)
        {
            int meshCount = 0;
            SubAssetEntry[] subs = entry.SubAssets;
            for (int i = 0; i < subs.Length; i++)
            {
                Type? t = subs[i].Type;
                if (t != null && typeof(Mesh).IsAssignableFrom(t))
                    meshCount++;
            }

            using (paper.Row($"{id}_stats").Height(UnitValue.Auto)
                .Margin(m.PaddingLarge, m.PaddingLarge, 0, m.SpacingLarge).RowBetween(m.SpacingMedium).Enter())
            {
                EditorGUI.StatChip(paper, $"{id}_st_meshes", $"{meshCount} {(meshCount == 1 ? "Mesh" : "Meshes")}", EditorTheme.DefaultFont!);
                EditorGUI.StatChip(paper, $"{id}_st_mats", $"{_materials.Count} {(_materials.Count == 1 ? "Material" : "Materials")}", EditorTheme.DefaultFont!);
                if (_clips.Count > 0)
                    EditorGUI.StatChip(paper, $"{id}_st_anims", $"{_clips.Count} {(_clips.Count == 1 ? "Animation" : "Animations")}", EditorTheme.DefaultFont!);
                paper.Box($"{id}_st_pad").Height(1).IsNotInteractable();
            }
        }

        EditorGUI.SectionHeader(paper, $"{id}_h_scene", "Scene", first: true);

        EditorGUI.Row(paper, $"{id}_unitScale", "Scale Factor", () =>
            Origami.NumericField<float>(paper, $"{id}_unitScale_v", _unitScale,
                v => { if (!Owns(session)) return; _unitScale = v; SettingsChanged(session); }).Show());

        EditorGUI.SettingsToggle(paper, $"{id}_convertFileUnits", "Convert File Units", _convertFileUnits,
            v => { if (!Owns(session)) return; _convertFileUnits = v; SettingsChanged(session); }, separator: false);
        DrawImportNote(paper, $"{id}_globalScale", _unitsCapability);

        EditorGUI.SectionHeader(paper, $"{id}_h_meshes", "Meshes");

        EditorGUI.SettingsToggle(paper, $"{id}_genNormals", "Generate Normals", _generateNormals,
            v => { if (!Owns(session)) return; _generateNormals = v; SettingsChanged(session); }, separator: false);

        EditorGUI.SettingsToggle(paper, $"{id}_smoothNormals", "Smooth Normals", _generateSmoothNormals,
            v => { if (!Owns(session)) return; _generateSmoothNormals = v; SettingsChanged(session); }, separator: false);

        EditorGUI.SettingsToggle(paper, $"{id}_tangents", "Calculate Tangents", _calculateTangents,
            v => { if (!Owns(session)) return; _calculateTangents = v; SettingsChanged(session); }, separator: false);

        if (_flipUvSupported)
            EditorGUI.SettingsToggle(paper, $"{id}_flipUV", "Flip UVs", _flipUVs,
                v => { if (!Owns(session)) return; _flipUVs = v; SettingsChanged(session); }, separator: false);
        else
            DrawImportNote(paper, $"{id}_flipUV", "Flip UVs: Source orientation is retained. This preference applies to glTF/GLB only.");

        EditorGUI.SettingsToggle(paper, $"{id}_lightmapUVs", "Generate Lightmap UVs (slow)", _generateLightmapUVs,
            v => { if (!Owns(session)) return; _generateLightmapUVs = v; SettingsChanged(session); }, separator: false);

        EditorGUI.SectionHeader(paper, $"{id}_h_features", "Mesh Features");

        EditorGUI.SettingsToggle(paper, $"{id}_genSDF", "Generate SDF (all meshes)", _generateSDF,
            v => { if (!Owns(session)) return; _generateSDF = v; SettingsChanged(session); }, separator: false);

        if (_generateSDF)
        {
            EditorGUI.Row(paper, $"{id}_sdfRes", "SDF Resolution", () =>
                Origami.NumericField<int>(paper, $"{id}_sdfRes_v", _sdfResolution,
                    v => { if (!Owns(session)) return; _sdfResolution = System.Math.Clamp(v, 8, 256); SettingsChanged(session); })
                    .Min(8).Max(256).Show());

            EditorGUI.Row(paper, $"{id}_sdfPad", "SDF Padding", () =>
                Origami.NumericField<float>(paper, $"{id}_sdfPad_v", _sdfPadding,
                    v => { if (!Owns(session)) return; _sdfPadding = v; SettingsChanged(session); }).Show());

            EditorGUI.Row(paper, $"{id}_sdfMax", "SDF Max Distance", () =>
                Origami.NumericField<float>(paper, $"{id}_sdfMax_v", _sdfMaxDistance,
                    v => { if (!Owns(session)) return; _sdfMaxDistance = v; SettingsChanged(session); }).Show());
        }
    }

    private void DrawRigTab(Paper paper, string id, ModelImportSettingsSession session)
    {
        EditorGUI.SectionHeader(paper, $"{id}_h_rig", "Rig", first: true);

        EditorGUI.Row(paper, $"{id}_animType", "Animation Type", () =>
            Origami.EnumDropdown(paper, $"{id}_animType_v", _animationType, v =>
            {
                if (!Owns(session)) return;
                _animationType = v;
                SettingsChanged(session);
            }).Show());

        DrawImportNote(paper, $"{id}_avatar", _avatarCapability);
        DrawImportNote(paper, $"{id}_avatar_support", _animationType == ModelAnimationType.Humanoid
            ? "Humanoid import uses the stored Avatar definition when an Animator is created. Avatar editing is unavailable here."
            : _animationType == ModelAnimationType.Generic
                ? "Generic Avatar Create uses the stored full root path; an empty path disables root motion. Copy and Rig editing are unavailable here."
                : "Avatar definition is not applied for Legacy imports.");
        EditorGUI.Row(paper, $"{id}_skinCount", "Skin Weights", () =>
            Origami.Dropdown<int>(paper, $"{id}_skinCount_v", _skinBoneCount, v =>
            {
                if (!Owns(session) || v is not (1 or 2 or 4)) return;
                _skinBoneCount = v;
                SettingsChanged(session);
            }, SkinInfluenceOptions).Display(static v => v == 1 ? "1 Bone" : v == 2 ? "2 Bones" : "4 Bones").Show());
        DrawImportNote(paper, $"{id}_skin", _skinCapability);
        EditorGUI.SettingsToggle(paper, $"{id}_stripBones", "Strip Bones", _stripBones,
            v => { if (!Owns(session)) return; _stripBones = v; SettingsChanged(session); }, separator: false);
        DrawImportNote(paper, $"{id}_strip", _stripCapability);
    }

    private void DrawAnimationTab(Paper paper, string id, OrigamiMetrics m, ModelImportSettingsSession session)
    {
        EditorGUI.SectionHeader(paper, $"{id}_h_anim", "Animations", first: true);

        EditorGUI.SettingsToggle(paper, $"{id}_importAnim", "Import Animation", _importAnimation,
            v => { if (!Owns(session)) return; _importAnimation = v; SettingsChanged(session); }, separator: false);

        if (!_importAnimation)
        {
            DrawImportNote(paper, $"{id}_noimp",
                "Animations will not be imported. Apply to remove imported clip subassets.");
            return;
        }

        DrawImportNote(paper, $"{id}_resample", _resampleCapability);

        EditorGUI.SectionHeader(paper, $"{id}_h_clips", "Clips");
        DrawImportNote(paper, $"{id}_clip_support", "Imported clips are read-only. Select a clip to preview. Range and loop editing are unavailable here.");

        using (paper.Row($"{id}_cliphdr").Height(22)
            .Margin(m.PaddingLarge, m.PaddingLarge, 0, 0)
            .BackgroundColor(AnimationEditorChrome.Raised).Enter())
        {
            Origami.Label(paper, $"{id}_ch_n", "Clips").Show();
            paper.Box($"{id}_ch_sp").Width(UnitValue.Stretch()).Height(1).IsNotInteractable();
            Origami.Label(paper, $"{id}_ch_s", "Start").Show();
            Origami.Label(paper, $"{id}_ch_e", "End").Show();
        }

        if (_clips.Count == 0)
        {
            Origami.Label(paper, $"{id}_noclip", "No animation clips in this model.").Show();
            return;
        }

        for (int i = 0; i < _clips.Count; i++)
        {
            int idx = i;
            AnimationClip clip = _clips[i];
            bool selected = idx == _selectedClipIndex;
            float start = ModelImportEditorModel.ClipStartFrame(clip);
            float end = ModelImportEditorModel.ClipEndFrame(clip);
            using (paper.Row($"{id}_clip{idx}").Height(22)
                .Margin(m.PaddingLarge, m.PaddingLarge, 0, 0)
                .BackgroundColor(selected ? AnimationEditorChrome.Selection : AnimationEditorChrome.Panel)
                .Padding(8, 4, 0, 0)
                .OnClick(0, (_, _) => { if (Owns(session)) _selectedClipIndex = idx; })
                .Enter())
            {
                Origami.Label(paper, $"{id}_cn{idx}", clip.Name).Show();
                paper.Box($"{id}_csp{idx}").Width(UnitValue.Stretch()).Height(1).IsNotInteractable();
                Origami.Label(paper, $"{id}_cs{idx}", start.ToString("0.0")).Show();
                Origami.Label(paper, $"{id}_ce{idx}", end.ToString("0.0")).Show();
            }
        }
    }

    private void DrawMaterialsTab(Paper paper, string id, OrigamiMetrics m, ModelImportSettingsSession session)
    {
        EditorGUI.SectionHeader(paper, $"{id}_h_mats", "Materials", first: true);
        DrawImportNote(paper, $"{id}_mat_note",
            "Remap source slots to project Materials. None uses the importer default.");

        if (_materials.Count == 0)
        {
            Origami.Label(paper, $"{id}_nomats", "No materials in this model.").Show();
            return;
        }

        for (int i = 0; i < _materials.Count; i++)
        {
            Material mat = _materials[i];
            Guid sourceGuid = mat.AssetID;
            Guid targetGuid = Guid.Empty;
            if (_materialRemaps.TryGet(sourceGuid.ToString(), out var stored) && stored.TagType == EchoType.String)
                Guid.TryParse(stored.StringValue, out targetGuid);
            s_materialField.Draw(paper, $"{id}_mat_{sourceGuid:N}", mat.Name ?? $"Material_{i}",
                new AssetRef<Material>(targetGuid), value =>
                {
                    if (value is AssetRef<Material> target) SetMaterialRemap(session, sourceGuid, target);
                }, compact: true);
        }
    }

    private bool SetMaterialRemap(ModelImportSettingsSession session, Guid sourceGuid, AssetRef<Material> target)
    {
        if (!Owns(session)) return false;
        bool sourceExists = false;
        for (int i = 0; i < _materials.Count; i++)
            if (_materials[i].AssetID == sourceGuid) { sourceExists = true; break; }
        if (!sourceExists) return false;
        if (target.IsRuntimeResource || (target.AssetID != Guid.Empty && !ModelMaterialRemapping.IsExternalMaterial(target.AssetID)))
        {
            _settingsError = "Choose a standalone Material asset from this project.";
            return false;
        }
        // Absent and explicit None are both the importer default; don't create a draft for that no-op.
        if (target.AssetID == Guid.Empty && !_materialRemaps.Contains(sourceGuid.ToString()))
        {
            _settingsError = null;
            return true;
        }
        _materialRemaps[sourceGuid.ToString()] = new EchoObject(target.AssetID.ToString());
        SettingsChanged(session);
        return true;
    }

    private void DrawApplyRevert(Paper paper, string id, AssetEntry entry, XEngine.Scribe.FontFile font, OrigamiMetrics m, ModelImportSettingsSession session)
    {
        using (paper.Row($"{id}_btns").Height(UnitValue.Auto)
            .Margin(m.PaddingLarge, m.PaddingLarge, m.SpacingLarge, m.SpacingLarge)
            .RowBetween(m.SpacingMedium).Enter())
        {
            paper.Box($"{id}_btn_spacer").Height(1).IsNotInteractable();

            var revert = paper.Box($"{id}_revert").Width(UnitValue.Auto).Height(30).Rounded(8).Padding(16, 16, 0, 0)
                .BackgroundColor(EditorTheme.Glass).BorderColor(EditorTheme.BorderSoft).BorderWidth(1)
                .Text("Revert", EditorTheme.FontSemiBold ?? font).TextColor(EditorTheme.Ink400)
                .FontSize(EditorTheme.FontSizeSmall).Alignment(TextAlignment.MiddleCenter);
            if (_settingsDirty || _settingsError != null)
            {
                revert.Hovered.BackgroundColor(EditorTheme.Neutral300).End()
                    .OnClick(0, (_, _) =>
                    {
                        if (Owns(session)) LoadSettingsFromMeta(entry);
                    });
            }

            if (_settingsDirty)
            {
                paper.Box($"{id}_save").Width(UnitValue.Auto).Height(30).Rounded(8).Padding(16, 16, 0, 0)
                    .BackgroundColor(EditorTheme.Accent)
                    .Hovered.BackgroundColor(EditorTheme.AccentBright).End()
                    .Text("Apply", EditorTheme.FontSemiBold ?? font)
                    .TextColor(System.Drawing.Color.White).FontSize(EditorTheme.FontSizeSmall)
                    .Alignment(TextAlignment.MiddleCenter)
                    .OnClick(0, (_, _) =>
                    {
                        if (!Owns(session) || !SaveSettingsToMeta(entry)) return;
                        _settingsDirty = false;
                        if (_lastSettingsWrite) Reimport(entry, session);
                    });
            }
            else
            {
                paper.Box($"{id}_reimport").Width(UnitValue.Auto).Height(30).Rounded(8).Padding(16, 16, 0, 0)
                    .BackgroundColor(EditorTheme.Accent)
                    .Hovered.BackgroundColor(EditorTheme.AccentBright).End()
                    .Text($"{EditorIcons.ArrowsRotate}  Reimport", EditorTheme.FontSemiBold ?? font)
                    .TextColor(System.Drawing.Color.White).FontSize(EditorTheme.FontSizeSmall)
                    .Alignment(TextAlignment.MiddleCenter)
                    .OnClick(0, (_, _) => { if (Owns(session)) Reimport(entry, session); });
            }
        }
    }

    private void LoadSettingsFromMeta(AssetEntry entry)
    {
        var previous = BuildDisplayedSettings();
        try
        {
            var project = Project.Current ?? throw new InvalidOperationException("No project is open.");
            var session = ModelImportSettingsSession.Load(project.AssetsPath, entry);
            ReadDisplayedSettings(session.Settings);
            RefreshCapabilityCaptions(session.Settings);
            string extension = Path.GetExtension(entry.Path);
            _flipUvSupported = extension.Equals(".gltf", StringComparison.OrdinalIgnoreCase)
                || extension.Equals(".glb", StringComparison.OrdinalIgnoreCase);
            session.CaptureDisplay(BuildDisplayedSettings());
            _settingsSession = session;
            _settingsProject = project;
            _settingsDirty = false;
            _settingsError = null;
        }
        catch (Exception ex)
        {
            ReadDisplayedSettings(previous);
            _settingsError = "Could not load import settings: " + ex.Message;
        }
    }

    private bool SaveSettingsToMeta(AssetEntry entry)
    {
        _lastSettingsWrite = false;
        try
        {
            var session = _settingsSession;
            if (session == null || !Owns(session) || !session.Matches(Project.Current!.AssetsPath, entry))
                throw new InvalidOperationException("The selected model has changed. Reload its settings.");
            if (EditorAssetBackend.Instance == null)
                throw new InvalidOperationException("The project's asset database is unavailable.");
            _lastSettingsWrite = session.Apply(BuildDisplayedSettings());
            _settingsError = null;
            return true;
        }
        catch (Exception ex)
        {
            _settingsError = "Could not apply import settings: " + ex.Message;
            return false;
        }
    }

    private EchoObject BuildDisplayedSettings()
    {
        var s = EchoObject.NewCompound();
        s["generateNormals"] = new EchoObject(_generateNormals);
        s["generateSmoothNormals"] = new EchoObject(_generateSmoothNormals);
        s["calculateTangents"] = new EchoObject(_calculateTangents);
        s["flipUVs"] = new EchoObject(_flipUVs);
        s["globalScale"] = new EchoObject(_globalScale);
        s["convertFileUnits"] = new EchoObject(_convertFileUnits);
        s["unitScale"] = new EchoObject(_unitScale);
        s["generateLightmapUVs"] = new EchoObject(_generateLightmapUVs);
        s["importAnimation"] = new EchoObject(_importAnimation);
        s["resampleCurves"] = new EchoObject(_resampleCurves);
        s["animationType"] = new EchoObject((int)_animationType);
        s["avatarDefinition"] = new EchoObject((int)_avatarDefinition);
        s["skinBoneCount"] = new EchoObject(_skinBoneCount);
        s["stripBones"] = new EchoObject(_stripBones);

        var sdf = EchoObject.NewCompound();
        sdf[SDFFeatureSpec.Key_Enabled] = new EchoObject(_generateSDF);
        sdf[SDFFeatureSpec.Key_Resolution] = new EchoObject(_sdfResolution);
        sdf[SDFFeatureSpec.Key_Padding] = new EchoObject(_sdfPadding);
        sdf[SDFFeatureSpec.Key_MaxDistance] = new EchoObject(_sdfMaxDistance);
        s[SDFFeatureSpec.KeyRoot] = sdf;
        s[ModelMaterialRemapping.SettingsKey] = _materialRemaps.Clone();

        return s;
    }

    private void ResetSettings()
    {
        ReadDisplayedSettings(null);
        RefreshCapabilityCaptions(null);
    }

    private void ReadDisplayedSettings(EchoObject? settings)
    {
        var s = settings ?? EchoObject.NewCompound();
        _materialRemaps = EchoObject.NewCompound();
        if (s.TryGet(ModelMaterialRemapping.SettingsKey, out var remaps))
        {
            if (remaps.TagType != EchoType.Compound) throw new InvalidDataException("Material remaps must be a compound document.");
            _materialRemaps = remaps.Clone();
        }
        _generateNormals = !s.TryGet("generateNormals", out var gn) || gn.BoolValue;
        _generateSmoothNormals = !s.TryGet("generateSmoothNormals", out var gsn) || gsn.BoolValue;
        _calculateTangents = !s.TryGet("calculateTangents", out var ct) || ct.BoolValue;
        _flipUVs = !s.TryGet("flipUVs", out var fu) || fu.BoolValue;
        _globalScale = s.TryGet("globalScale", out var gs) && gs.BoolValue;
        _convertFileUnits = !s.TryGet("convertFileUnits", out var cfu) || cfu.BoolValue;
        _unitScale = s.TryGet("unitScale", out var us) ? us.FloatValue : 1f;
        _generateLightmapUVs = s.TryGet("generateLightmapUVs", out var glu) && glu.BoolValue;
        _importAnimation = !s.TryGet("importAnimation", out var ia) || ia.BoolValue;
        _resampleCurves = !s.TryGet("resampleCurves", out var rc) || rc.BoolValue;
        _animationType = s.TryGet("animationType", out var at) ? (ModelAnimationType)at.IntValue : ModelAnimationType.Generic;
        _avatarDefinition = s.TryGet("avatarDefinition", out var ad) ? (ModelAvatarDefinition)ad.IntValue : ModelAvatarDefinition.NoAvatar;
        _skinBoneCount = s.TryGet("skinBoneCount", out var sb) && sb.IntValue is 1 or 2 or 4 ? sb.IntValue : 4;
        _stripBones = s.TryGet("stripBones", out var st) && st.BoolValue;
        _generateSDF = false;
        _sdfResolution = 64;
        _sdfPadding = 0.1f;
        _sdfMaxDistance = 0.25f;
        if (s.TryGet(SDFFeatureSpec.KeyRoot, out var sdf))
        {
            if (sdf.TagType != EchoType.Compound) throw new InvalidDataException("SDF settings must be a compound document.");
            _generateSDF = sdf.TryGet(SDFFeatureSpec.Key_Enabled, out var en) && en.BoolValue;
            _sdfResolution = sdf.TryGet(SDFFeatureSpec.Key_Resolution, out var sr) ? sr.IntValue : 64;
            _sdfPadding = sdf.TryGet(SDFFeatureSpec.Key_Padding, out var sp) ? sp.FloatValue : 0.1f;
            _sdfMaxDistance = sdf.TryGet(SDFFeatureSpec.Key_MaxDistance, out var sm) ? sm.FloatValue : 0.25f;
        }
    }

    private void RefreshCapabilityCaptions(EchoObject? settings)
    {
        var s = settings ?? EchoObject.NewCompound();
        _unitsCapability = "File units: FBX uses its declared unit conversion; glTF/GLB uses meters and OBJ retains its assumed units. Scale Factor is applied separately."
            + $" Legacy globalScale preference: {StoredFlag(s, "globalScale")} (unused).";
        string storedWeights = s.TryGet("skinBoneCount", out var storedSkin) ? storedSkin.IntValue.ToString() : "Not set";
        string fallback = s.TryGet("skinBoneCount", out var skin) && skin.IntValue is not (1 or 2 or 4)
            ? " (unsupported; using 4)" : "";
        _skinCapability = $"Skin Weights: Keeps the strongest 1, 2 or 4 influences and normalizes their weights. Unweighted vertices stay unweighted. Stored limit: {storedWeights}{fallback}.";
        _stripCapability = $"Strip Bones: Removes unused renderer bone entries. All transforms, animation paths, attachments and Root Bone references remain. Default: Off. Stored preference: {StoredFlag(s, "stripBones")}.";
        _resampleCapability = $"Resample Curves: Unavailable. Sampling follows the source importer. Stored preference: {StoredFlag(s, "resampleCurves")}.";
        _avatarCapability = $"Avatar Definition: {_avatarDefinition} (read-only).";
        if (_avatarDefinition == ModelAvatarDefinition.CopyFromOtherAvatar)
            _avatarCapability += s.TryGet("avatarSourceGuid", out var sourceAvatar)
                ? $" Stored source: {sourceAvatar.StringValue}." : " Stored source: Not set.";
        if (_animationType == ModelAnimationType.Generic && _avatarDefinition == ModelAvatarDefinition.CreateFromThisModel)
            _avatarCapability += s.TryGet("genericRootPath", out var root)
                ? root.TagType == EchoType.String
                    ? $" Stored Root: {(string.IsNullOrEmpty(root.StringValue) ? "None" : root.StringValue)}."
                    : $" Stored Root: Invalid ({root.TagType})."
                : " Stored Root: None.";
    }

    private static string StoredFlag(EchoObject settings, string key)
        => settings.TryGet(key, out var flag) ? flag.BoolValue ? "On" : "Off" : "Not set";

    private static void DrawImportNote(Paper paper, string id, string text)
        => paper.Box(id).Height(UnitValue.Auto).Margin(8, 8, 2, 6)
            .Text(text, EditorTheme.DefaultFont!).FontSize(EditorTheme.FontSizeSmall)
            .TextColor(EditorTheme.Ink400).Wrap(XEngine.Scribe.TextWrapMode.Wrap).IsNotInteractable();

    private bool Owns(ModelImportSettingsSession session)
        => ReferenceEquals(_settingsSession, session) && ReferenceEquals(_settingsProject, Project.Current)
            && _currentGuid == session.AssetGuid;

    private void SettingsChanged(ModelImportSettingsSession session)
    {
        _settingsDirty = session.IsDirty(BuildDisplayedSettings());
        _settingsError = null;
    }

    private void Reimport(AssetEntry entry, ModelImportSettingsSession session)
    {
        if (!Owns(session) || !session.Matches(Project.Current!.AssetsPath, entry)) return;
        try
        {
            var backend = EditorAssetBackend.Instance ?? throw new InvalidOperationException("The asset database is unavailable.");
            // Detect external meta changes before the explicit clean-state Reimport too.
            session.RequireUnchanged();
            var result = backend.ReimportWithResult(entry.Guid);
            if (!result.Succeeded || result.Status != AssetReimportStatus.Imported)
            {
                string reason = result.Error ?? (result.DependentFailures.Count > 0
                    ? $"Dependent '{result.DependentFailures[0].Path}' failed: {result.DependentFailures[0].Error}"
                    : "The importer produced no runtime model.");
                _settingsError = "Settings are saved; reimport could not finish: " + reason;
                return;
            }
            _settingsError = null;
            _settingsLoaded = false;
        }
        catch (Exception ex)
        {
            _settingsError = "Settings are saved; reimport could not finish: " + ex.Message;
        }
    }

    private void DrawSettingsError(Paper paper, string id)
    {
        if (_settingsError == null) return;
        paper.Box($"{id}_settings_error").Height(UnitValue.Auto).Margin(8, 8, 6, 6).Padding(6)
            .Text(_settingsError, EditorTheme.DefaultFont!).FontSize(EditorTheme.FontSizeSmall)
            .TextColor(EditorTheme.Ink400).Wrap(XEngine.Scribe.TextWrapMode.Wrap).IsNotInteractable();
    }
}
