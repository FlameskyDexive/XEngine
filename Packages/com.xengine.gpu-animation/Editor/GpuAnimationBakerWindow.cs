using System;
using System.Collections.Generic;

using XEngine.Editor;
using XEngine.Editor.Core;
using XEngine.Editor.GUI;
using XEngine.Editor.Theming;
using XEngine.OrigamiUI;
using XEngine.PaperUI;
using XEngine.PaperUI.LayoutEngine;
using XEngine.Runtime;
using XEngine.Runtime.Resources;
using XEngine.Scribe;
using XEngine.Vector;

using TextAlignment = XEngine.PaperUI.TextAlignment;

namespace XEngine.GpuAnimation.Editor;

/// <summary>
/// Window/Animation/GPU Animation Baker — bakes the selected skinned character's clips into
/// vertex animation textures (.gpuanim) plus a UV2-encoded static mesh (.mesh), through the
/// same <see cref="GpuAnimationBaker"/> pipeline the tests assert against.
/// </summary>
public sealed class GpuAnimationBakerWindow : DockPanel
{
    private float _fps = 30f;
    private int _frameCount; // 0 = auto (nearest power of two of fps * duration per clip)
    private bool _bakeNormals = true;
    private bool _halfPrecision = true; // default half: half the GPU memory, same visual result
    private bool _pinRoot = true;
    private readonly HashSet<Guid> _selectedClips = new();
    private string _status = "Select a skinned character in the Hierarchy, pick clips, then Bake.";
    private string _lastAssetPath = "";
    private string _lastMeshPath = "";
    private string _lastMaterialPath = "";

    [MenuItem("Window/Animation/GPU Animation Baker", priority: 23)]
    private static void Open() => EditorApplication.Instance?.OpenPanel(typeof(GpuAnimationBakerWindow));

    public override string Title => "GPU Animation Baker";
    public override string Icon => EditorIcons.Film;

    public override void OnGUI(Paper paper, float width, float height)
    {
        FontFile? font = EditorTheme.DefaultFont;
        if (font == null) return;

        GameObject? target = Selection.ActiveObject as GameObject;
        var smr = FindRenderableSkinnedMesh(target);
        bool targetValid = smr != null;
        bool canBake = targetValid && _selectedClips.Count > 0;

        using (paper.Column("gab_root").Width(width).Height(height).Padding(10, 10, 10, 10).ColBetween(6).Enter())
        {
            // ---- Target ----
            string meshText = targetValid
                ? $"{smr!.SharedMesh.Res?.Name} ({smr.SharedMesh.Res?.Vertices?.Length ?? 0} verts)"
                : (target == null ? "no GameObject selected" : "no SkinnedMeshRenderer with a loaded mesh");
            paper.Box("gab_target").Height(46)
                .Text($"Target: {target?.Name ?? "—"}\nSkinnedMesh: {meshText}", font)
                .FontSize(EditorTheme.FontSizeSmall)
                .Alignment(TextAlignment.MiddleLeft).Padding(8, 0, 0, 0)
                .IsNotInteractable();

            // ---- Options ----
            EditorGUI.SettingsRow(paper, "gab_fps", "Bake FPS", () =>
                Origami.Slider(paper, "gab_fps_v", _fps, v => _fps = MathF.Round(v), 1f, 120f).Format("F0").Show());
            EditorGUI.SettingsRow(paper, "gab_frames", "Frames/clip (0=auto)", () =>
                Origami.IntSlider(paper, "gab_frames_v", _frameCount, v => _frameCount = v, 0, 4096).Show());
            EditorGUI.SettingsToggle(paper, "gab_normals", "Bake normals (lit crowds)", _bakeNormals, v => _bakeNormals = v);
            EditorGUI.SettingsToggle(paper, "gab_half", "Half precision (16F, half VRAM)", _halfPrecision, v => _halfPrecision = v);
            EditorGUI.SettingsToggle(paper, "gab_pin", "Pin root (in-place loop)", _pinRoot, v => _pinRoot = v);

            // ---- Clips ----
            paper.Box("gab_clips_header").Height(24)
                .Text($"Clips ({_selectedClips.Count} selected)", font)
                .FontSize(EditorTheme.FontSizeSmall)
                .Alignment(TextAlignment.MiddleLeft).IsNotInteractable();

            var entries = EditorAssetBackend.Instance.FindAssetsOfType<AnimationClip>();
            int row = 0;
            foreach (var entry in entries)
            {
                if (row++ >= 24) break; // keep the window usable; projects with more clips should filter
                bool selected = _selectedClips.Contains(entry.Guid);
                string clipName = System.IO.Path.GetFileNameWithoutExtension(entry.Path);
                EditorGUI.SettingsRow(paper, "gab_clip_" + row, clipName, () =>
                    Origami.Checkbox(paper, "gab_clip_c_" + row, selected, v =>
                    {
                        if (v) _selectedClips.Add(entry.Guid);
                        else _selectedClips.Remove(entry.Guid);
                    }).LabelRight(entry.Path).Show(), separator: false);
            }
            if (row == 0)
            {
                paper.Box("gab_clips_empty").Height(24)
                    .Text("No AnimationClip assets in project.", font)
                    .FontSize(EditorTheme.FontSizeSmall)
                    .Alignment(TextAlignment.MiddleLeft).IsNotInteractable();
            }

            paper.Box("gab_spacer").Height(UnitValue.StretchOne).IsNotInteractable();

            // ---- Bake + status ----
            Origami.Button(paper, "gab_bake", canBake ? "Bake" : "Bake (select target + clips)", BakeFromUi)
                .Height(30).Disabled(!canBake).Show();
            paper.Box("gab_status").Height(40)
                .Text(_status, font)
                .FontSize(EditorTheme.FontSizeSmall)
                .TextColor(EditorTheme.InkDim)
                .Alignment(TextAlignment.MiddleLeft).Padding(4, 0, 0, 0)
                .IsNotInteractable();
        }
    }

    /// <summary>
    /// The SMR to bake: characters often carry several renderers (preview slots, weapon meshes)
    /// and some may not have a resolved mesh — pick the loaded one with the most vertices.
    /// </summary>
    private static SkinnedMeshRenderer? FindRenderableSkinnedMesh(GameObject? target)
    {
        if (target == null) return null;
        SkinnedMeshRenderer? best = null;
        int bestVerts = -1;
        foreach (var candidate in target.GetComponentsInChildren(typeof(SkinnedMeshRenderer), includeSelf: true, includeInactive: true))
        {
            if (candidate is not SkinnedMeshRenderer smr) continue;
            var mesh = smr.SharedMesh.Res;
            if (mesh == null) continue;
            int verts = mesh.Vertices?.Length ?? 0;
            if (verts > bestVerts)
            {
                bestVerts = verts;
                best = smr;
            }
        }
        return best;
    }

    /// <summary>
    /// Runs the bake and saves .gpuanim + .mesh under Assets/GpuAnim/. Public so acceptance
    /// harnesses can drive the exact button code path without synthetic clicks.
    /// </summary>
    public void BakeFromUi()
    {
        try
        {
            GameObject? target = Selection.ActiveObject as GameObject;
            var smr = FindRenderableSkinnedMesh(target);
            if (target == null || smr == null)
            {
                _status = "Bake failed: select a GameObject with a SkinnedMeshRenderer.";
                return;
            }

            var clips = new List<AnimationClip>();
            foreach (Guid guid in _selectedClips)
                if (AssetDatabase.Get(guid) is AnimationClip clip)
                    clips.Add(clip);
            if (clips.Count == 0)
            {
                _status = "Bake failed: no clips selected.";
                return;
            }

            var request = new GpuAnimationBakeRequest
            {
                Target = target,
                Avatar = target.GetComponentInChildren<Animator>(includeSelf: true, includeInactive: true)?.Avatar.Res,
                Fps = _fps,
                FrameCountOverride = _frameCount > 0 ? _frameCount : null,
                BakeNormals = _bakeNormals,
                HalfPrecision = _halfPrecision,
                PinRootToReferencePose = _pinRoot,
            };
            foreach (AnimationClip clip in clips)
                request.Clips.Add(clip);

            GpuAnimationBakeResult result = GpuAnimationBaker.Bake(request);

            string baseName = $"{target.Name}_{clips.Count}clip{(clips.Count > 1 ? "s" : "")}";
            _lastAssetPath = $"GpuAnim/{baseName}.gpuanim";
            _lastMeshPath = $"GpuAnim/{baseName}_VAT.mesh";
            _lastMaterialPath = $"GpuAnim/{baseName}.mat";
            EditorAssetBackend.Instance.CreateAsset(result.Asset, _lastAssetPath);
            EditorAssetBackend.Instance.CreateAsset(result.VatMesh, _lastMeshPath);

            // Ready-to-use crowd material referencing the packaged unlit shader (deterministic
            // BuiltInAssets GUID, so the .mat resolves in any project with the package). The
            // crowd binds the VAT textures per-instance at runtime — the .mat carries the shader.
            var crowdMaterial = new Material(GpuAnimationShaderLibrary.LoadUnlitShader()) { Name = baseName };
            EditorAssetBackend.Instance.CreateAsset(crowdMaterial, _lastMaterialPath);

            _status = $"Baked {clips.Count} clip(s) @ {_fps:0} fps — {_lastAssetPath} " +
                      $"({result.Asset.TextureWidth}x{result.Asset.TextureHeight} texels) + {_lastMeshPath} + {_lastMaterialPath}";
        }
        catch (Exception ex)
        {
            _status = "Bake failed: " + ex.Message;
            Debug.LogError($"[GpuAnimation] bake failed: {ex}");
        }
    }
}
