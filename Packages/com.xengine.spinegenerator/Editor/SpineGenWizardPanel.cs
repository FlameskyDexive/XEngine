// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using FontFile = XEngine.Scribe.FontFile;

using XEngine.Editor;
using XEngine.Editor.Core;
using XEngine.Editor.GUI;
using XEngine.Editor.Projects;
using XEngine.Editor.Theming;
using XEngine.OrigamiUI;
using XEngine.PaperUI;
using XEngine.PaperUI.LayoutEngine;
using XEngine.Runtime;
using XEngine.Runtime.Resources;

namespace XEngine.SpineGenerator.Editor;

/// <summary>
/// Four-step wizard mirroring the AI Spine2D production video: ① source image → slice,
/// ② fill gallery (inpaint candidates), ③ rig + skin, ④ animate (built-in or LLM). Every action
/// shells out to `xengine-cli spinegen-*` so the editor, CLI and MCP surfaces share one pipeline.
/// </summary>
public sealed class SpineGenWizardPanel : DockPanel
{
    [MenuItem("Window/AI/Spine Generator", priority: 25, Icon = EditorIcons.WandMagicSparkles)]
    static void Open() => EditorApplication.Instance?.OpenPanel(typeof(SpineGenWizardPanel));

    public override string Title => "Spine Generator";
    public override string Icon => EditorIcons.WandMagicSparkles;

    private static readonly string[] Motions = ["idle_breath", "sway", "bob", "wave_arm"];
    private static readonly string[] Providers = ["mock", "openai"];
    private static readonly string[] FillProviders = ["mock", "openai"];

    private readonly SpineGenCliRunner _cli = new();

    private int _step;
    private string _imagePath = "";
    private string? _sliceDir;
    private string _sliceSummary = "";
    private string _fillProvider = "mock";
    private string _fillSummary = "";
    private string _rigSummary = "";
    private float _skinEpsilon = 1.5f;
    private string _skinSummary = "";
    private string _motion = "idle_breath";
    private string _prompt = "";
    private string _animateProvider = "mock";
    private string _animateSummary = "";

    public override void OnGUI(Paper paper, float width, float height)
    {
        var font = EditorTheme.DefaultFont;
        if (font == null)
            return;
        _cli.Poll();
        if (!_cli.IsRunning && _cli.LastJson != null)
            RefreshSummaries();

        Project? project = Project.Current;
        using (paper.Column("sgw_root").Width(width).Height(height).Padding(10, 10, 10, 10).Enter())
        {
            using (paper.Row("sgw_header").Width(UnitValue.Auto).Height(26).Enter())
            {
                paper.Box("sgw_title").Width(UnitValue.StretchOne).Height(26)
                    .Text($"{EditorIcons.WandMagicSparkles}  Spine Generator", font)
                    .FontSize(EditorTheme.FontSize).TextColor(EditorTheme.Ink500)
                    .Alignment(TextAlignment.MiddleLeft).IsNotInteractable();
            }

            // Step rail.
            using (paper.Row("sgw_steps").Width(UnitValue.Auto).Height(24).Margin(0, 6, 0, 0).Enter())
            {
                string[] stepLabels = ["1 Image", "2 Fill", "3 Rig+Skin", "4 Animate"];
                for (int i = 0; i < stepLabels.Length; i++)
                {
                    string id = "sgw_step_" + i;
                    bool active = _step == i;
                    bool reachable = StepReachable(i);
                    using (paper.Box(id).Width(110).Height(24).Margin(0, i > 0 ? 6 : 0, 0, 0)
                        .Rounded(4)
                        .BackgroundColor(active ? EditorTheme.Accent
                            : reachable ? EditorTheme.Hover : EditorTheme.BorderSoft)
                        .Enter())
                    {
                        paper.Box(id + "_t").Width(110).Height(24).Text(stepLabels[i], font)
                            .FontSize(EditorTheme.FontSizeSmall)
                            .TextColor(active ? EditorTheme.AccentText : EditorTheme.Ink300)
                            .Alignment(TextAlignment.MiddleCenter).IsNotInteractable();
                    }
                }
            }

            if (project == null)
            {
                paper.Box("sgw_noproject").Width(UnitValue.Auto).Height(40)
                    .Text("Open a project first.", font).TextColor(EditorTheme.Ink300)
                    .FontSize(EditorTheme.FontSize).Alignment(TextAlignment.MiddleCenter);
                return;
            }

            if (_cli.IsRunning || _cli.LastJson != null || _cli.LastError != null)
            {
                DrawStatusStrip(paper, font, project.RootPath);
            }

            Origami.ScrollView(paper, "sgw_scroll", width - 20, height - 60).Body(() =>
            {
                using (paper.Column("sgw_content").Height(UnitValue.Auto).Padding(0, 0, 4, 8).Enter())
                {
                    switch (_step)
                    {
                        case 0: DrawStepImage(paper, font, project); break;
                        case 1: DrawStepFill(paper, font, project); break;
                        case 2: DrawStepRigSkin(paper, font, project); break;
                        case 3: DrawStepAnimate(paper, font, project); break;
                    }
                }
            });
        }
    }

    private bool StepReachable(int step) => step switch
    {
        0 => true,
        1 => _sliceDir != null,
        2 => _sliceDir != null,
        _ => _sliceDir != null,
    };

    private void DrawStatusStrip(Paper paper, FontFile font, string projectRoot)
    {
        using (paper.Row("sgw_status").Width(UnitValue.Auto).Height(30).Margin(0, 4, 6, 0)
            .Rounded(4).BackgroundColor(EditorTheme.Hover).Enter())
        {
            string text = _cli.IsRunning ? _cli.Status
                : _cli.LastSucceeded ? "✓ " + _cli.Status
                : "✗ " + (_cli.LastError ?? "failed");
            paper.Box("sgw_status_t").Width(UnitValue.StretchOne).Height(30).Padding(8, 0, 0, 0)
                .Text(text, font).FontSize(EditorTheme.FontSizeSmall)
                .TextColor(_cli.LastSucceeded || _cli.IsRunning ? EditorTheme.Ink500 : EditorTheme.Red400)
                .Alignment(TextAlignment.MiddleLeft).IsNotInteractable();

            if (_cli.IsRunning)
                return;
            EditorGUI.CtaButton(paper, "sgw_status_next", "Next →", EditorTheme.Accent,
                () => { if (_step < 3 && StepReachable(_step + 1)) _step++; }, grow: false, height: 22);
        }
    }

    // ---- Step 1: source image → slice -----------------------------------------------

    private void DrawStepImage(Paper paper, FontFile font, Project project)
    {
        EditorGUI.SectionHeader(paper, "sgw_h0", "Character illustration", compact: true);
        EditorGUI.SettingsTextField(paper, "sgw_img", "Image (Assets-relative)",
            _imagePath, v => _imagePath = v);
        paper.Box("sgw_img_hint").Width(UnitValue.Auto).Height(18)
            .Text("RGBA PNG with clean silhouette, e.g. Art/bear.png", font)
            .FontSize(EditorTheme.FontSizeSmall).TextColor(EditorTheme.Ink300)
            .Alignment(TextAlignment.MiddleLeft).IsNotInteractable();

        EditorGUI.CtaButton(paper, "sgw_slice", $"{EditorIcons.Scissors}  Slice into parts",
            EditorTheme.Accent, () =>
            {
                _cli.ResetResult();
                _sliceSummary = "";
                _cli.Run(project.RootPath, ["spinegen-slice", "--image", _imagePath]);
            });

        if (_sliceSummary.Length > 0)
            DrawSummary(paper, font, "sgw_slice_sum", _sliceSummary);
    }

    // ---- Step 2: fill candidates ------------------------------------------------------

    private void DrawStepFill(Paper paper, FontFile font, Project project)
    {
        EditorGUI.SectionHeader(paper, "sgw_h1", "Occlusion fill", compact: true);
        DrawChips(paper, font, "sgw_fillprov", FillProviders, _fillProvider, v => _fillProvider = v);

        EditorGUI.CtaButton(paper, "sgw_fill", $"{EditorIcons.WandMagicSparkles}  Fill seeds",
            EditorTheme.Accent, () =>
            {
                _cli.ResetResult();
                _fillSummary = "";
                _cli.Run(project.RootPath, ["spinegen-fill", "--slice", Rel(_sliceDir!), "--provider", _fillProvider]);
            });

        if (_fillSummary.Length > 0)
            DrawSummary(paper, font, "sgw_fill_sum", _fillSummary);
    }

    // ---- Step 3: rig + skin ------------------------------------------------------------

    private void DrawStepRigSkin(Paper paper, FontFile font, Project project)
    {
        EditorGUI.SectionHeader(paper, "sgw_h2", "Rig + skin", compact: true);
        EditorGUI.SettingsSlider(paper, "sgw_eps", "Contour simplify (px)", _skinEpsilon, 0.5f, 6f,
            v => _skinEpsilon = v);

        EditorGUI.CtaButton(paper, "sgw_rig", $"{EditorIcons.Bone}  One-click rig + skin",
            EditorTheme.Accent, () =>
            {
                _cli.ResetResult();
                _rigSummary = "";
                _skinSummary = "";
                _cli.Run(project.RootPath, ["spinegen-rig", "--slice", Rel(_sliceDir!)]);
            });
        EditorGUI.CtaButton(paper, "sgw_skin", $"{EditorIcons.Scissors}  Skin meshes",
            EditorTheme.Accent, () =>
            {
                _cli.ResetResult();
                _skinSummary = "";
                _cli.Run(project.RootPath, ["spinegen-skin", "--slice", Rel(_sliceDir!), "--simplify-epsilon-x100", ((int)(_skinEpsilon * 100)).ToString()]);
            });

        if (_rigSummary.Length > 0)
            DrawSummary(paper, font, "sgw_rig_sum", _rigSummary);
        if (_skinSummary.Length > 0)
            DrawSummary(paper, font, "sgw_skin_sum", _skinSummary);
    }

    // ---- Step 4: animate ----------------------------------------------------------------

    private void DrawStepAnimate(Paper paper, FontFile font, Project project)
    {
        EditorGUI.SectionHeader(paper, "sgw_h3", "Animate", compact: true);
        DrawChips(paper, font, "sgw_motion", Motions, _motion, v => _motion = v);
        EditorGUI.SettingsTextField(paper, "sgw_prompt", "…or describe it (LLM)", _prompt,
            v => _prompt = v);
        DrawChips(paper, font, "sgw_animprov", Providers, _animateProvider, v => _animateProvider = v);

        bool usePrompt = _prompt.Trim().Length > 0;
        EditorGUI.CtaButton(paper, "sgw_anim",
            usePrompt ? $"{EditorIcons.PersonRunning}  Generate (LLM)" : $"{EditorIcons.PersonRunning}  Generate (built-in)",
            EditorTheme.Accent, () =>
            {
                _cli.ResetResult();
                _animateSummary = "";
                var args = new List<string> { "spinegen-animate", "--slice", Rel(_sliceDir!) };
                if (usePrompt)
                {
                    args.AddRange(["--prompt", _prompt, "--provider", _animateProvider]);
                }
                else
                {
                    args.AddRange(["--motion", _motion]);
                }
                _cli.Run(project.RootPath, args);
            });

        if (_animateSummary.Length > 0)
        {
            DrawSummary(paper, font, "sgw_anim_sum", _animateSummary);
            string absDir = Path.Combine(project.RootPath, Rel(_sliceDir!), "rig");
            paper.Box("sgw_open_hint").Width(UnitValue.Auto).Height(18)
                .Text($"Output: {absDir}", font).FontSize(EditorTheme.FontSizeSmall)
                .TextColor(EditorTheme.Ink300).Alignment(TextAlignment.MiddleLeft).IsNotInteractable();
        }
    }

    private void RefreshSummaries()
    {
        try
        {
            if (_cli.LastJson == null)
                return;
            using JsonDocument doc = JsonDocument.Parse(_cli.LastJson);
            JsonElement root = doc.RootElement;

            string? GetStr(string name) =>
                root.TryGetProperty(name, out JsonElement e) && e.ValueKind == JsonValueKind.String
                    ? e.GetString()
                    : null;
            int GetInt(string name) =>
                root.TryGetProperty(name, out JsonElement e) && e.TryGetInt32(out int i) ? i : 0;

            if (LastCommandWas("spinegen-slice"))
            {
                _sliceDir = GetStr("outputDirectory");
                _sliceSummary = $"parts={GetInt("partCount")} page={GetInt("pageWidth")}x{GetInt("pageHeight")} " +
                                $"seeds={GetInt("totalInpaintSeedPixels")} dir={_sliceDir}";
            }
            else if (LastCommandWas("spinegen-fill"))
            {
                _fillSummary = $"filled={GetInt("partsFilled")} parts ({GetInt("pixelsFilled")} px) gallery={GetStr("gallery")}";
            }
            else if (LastCommandWas("spinegen-rig"))
            {
                _rigSummary = $"bones={GetInt("boneCount")} slots={GetInt("slotCount")} skeleton={GetStr("skeletonJson")}";
            }
            else if (LastCommandWas("spinegen-skin"))
            {
                _skinSummary = $"meshed={GetInt("meshedParts")}/{GetInt("totalParts")} " +
                               $"verts={GetInt("vertices")} tris={GetInt("triangles")}";
            }
            else if (LastCommandWas("spinegen-animate"))
            {
                _animateSummary = $"animation='{GetStr("animation")}' duration={GetInt("durationSeconds")}s " +
                                  $"repairs={GetInt("repairRounds")} src={GetStr("source")}";
            }
        }
        catch (JsonException)
        {
            // CLI prints non-JSON on hard failures; the status strip already shows the error.
        }
    }

    private bool LastCommandWas(string command) =>
        _cli.LastCommand != null && _cli.LastCommand.Contains(command, StringComparison.Ordinal);

    private static void DrawChips(Paper paper, FontFile font, string id, string[] options,
        string selected, Action<string> setter)
    {
        using (paper.Row(id).Width(UnitValue.Auto).Height(26).Margin(0, 0, 0, 4).Enter())
        {
            for (int i = 0; i < options.Length; i++)
            {
                string option = options[i];
                bool active = option == selected;
                using (paper.Box($"{id}_{i}").Width(96).Height(24).Margin(0, i > 0 ? 6 : 0, 0, 0)
                    .Rounded(4)
                    .BackgroundColor(active ? EditorTheme.Accent : EditorTheme.BorderSoft)
                    .Hovered.BackgroundColor(active ? EditorTheme.Accent : EditorTheme.Hover).End()
                    .OnClick(option, (value, _) => setter(value))
                    .Enter())
                {
                    paper.Box($"{id}_{i}_t").Width(96).Height(24).Text(option, font)
                        .FontSize(EditorTheme.FontSizeSmall)
                        .TextColor(active ? EditorTheme.AccentText : EditorTheme.Ink300)
                        .Alignment(TextAlignment.MiddleCenter).IsNotInteractable();
                }
            }
        }
    }

    private static void DrawSummary(Paper paper, FontFile font, string id, string text)
    {
        paper.Box(id).Width(UnitValue.Auto).Height(20).Margin(0, 4, 0, 0)
            .Text(text, font).FontSize(EditorTheme.FontSizeSmall)
            .TextColor(EditorTheme.Ink500).Alignment(TextAlignment.MiddleLeft).IsNotInteractable();
    }

    private static string Rel(string absoluteOrRelative)
    {
        if (Path.IsPathRooted(absoluteOrRelative))
            return absoluteOrRelative;
        return absoluteOrRelative.Replace('\\', '/');
    }
}
