// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using XEngine.Editor.Core;
using XEngine.Editor.GUI;
using XEngine.Editor.Theming;
using XEngine.OrigamiUI;
using XEngine.PaperUI;
using XEngine.PaperUI.LayoutEngine;
using XEngine.Runtime;
using XEngine.Runtime.Rendering;
using XEngine.Scribe;
using PaperTextAlignment = XEngine.PaperUI.TextAlignment;

namespace XEngine.Editor.GUI.Panels;

/// <summary>Universal pipeline debugger: passes, Features, lighting, Volume stack, 3D physics overlay. Window/Analysis/Rendering Debugger.</summary>
public sealed class RenderingDebuggerPanel : DockPanel
{
    [MenuItem("Window/Analysis/Rendering Debugger", priority: 21)]
    private static void Open() => EditorApplication.Instance?.OpenPanel(typeof(RenderingDebuggerPanel));

    public override string Title => "Rendering Debugger";
    public override string Icon => EditorIcons.Bug;

    public override void OnGUI(Paper paper, float width, float height)
    {
        var font = EditorTheme.DefaultFont;
        if (font == null) return;

        RenderingDebugSnapshot snapshot = RenderingDebugger.Capture(UniversalRenderPipeline.Default);
        using (paper.Column("rd_root").Size(width, height).Padding(10, 10, 10, 10).Clip().Enter())
        {
            using (paper.Row("rd_title").Height(28).Enter())
            {
                paper.Box("rd_title_text").Width(width).Text("Rendering Debugger", font)
                    .TextColor(EditorTheme.Ink500).FontSize(EditorTheme.FontSizeLarge)
                    .Alignment(PaperTextAlignment.MiddleLeft);
            }

            paper.Box("rd_divider").Height(1).BackgroundColor(EditorTheme.BorderSoft).IsNotInteractable();
            DrawSection(paper, font, "Lighting",
            [
                $"Lights {snapshot.Lights}  dir {snapshot.DirectionalLights}  point {snapshot.PointLights}  spot {snapshot.SpotLights}",
                $"Casters {snapshot.ShadowCasters}  cascades {snapshot.ShadowCascades}",
                $"Draws {snapshot.DrawCalls}  objects {snapshot.RenderablesDrawn}  overdraw proxy {snapshot.OverdrawProxy}"
            ]);
            DrawSection(paper, font, "Passes",
            [
                $"Queue {snapshot.EnqueuedPasses}  executed {snapshot.ExecutedPasses}  culled {snapshot.CulledGraphPasses}",
                $"CopyColor {snapshot.CopyColorPasses}  CopyDepth {snapshot.CopyDepthPasses}",
                JoinPassEvents(snapshot.PassEvents)
            ]);
            DrawSection(paper, font, "Virtual Geometry",
            [
                $"Clusters {snapshot.VgVisibleClusters}  tris {snapshot.VgTriangles}  draws {snapshot.VgDrawCalls}",
                $"Scratch upload {(snapshot.VgScratchUploadBytes / 1024)} KB  resident {(snapshot.VgResidentBytes / 1024)} KB"
            ]);
            DrawSection(paper, font, "RTHandle",
            [
                $"Reference {snapshot.RtWidth}x{snapshot.RtHeight}",
                $"Opaque {(snapshot.OpaqueAllocated ? "allocated" : "idle")}  Prepass {(snapshot.PrepassAllocated ? "allocated" : "idle")}"
            ]);
            DrawSection(paper, font, "Features", snapshot.Features.Length == 0
                ? ["(none)"]
                : snapshot.Features);
            DrawSection(paper, font, "Volume stack", snapshot.VolumeStack.Length == 0
                ? ["(empty)"]
                : snapshot.VolumeStack);
            DrawSection(paper, font, "Volumes", FormatVolumes(snapshot.Volumes));

            PhysicsDebugSnapshot physics = PhysicsDebugger.Capture();
            DrawSection(paper, font, "Physics 3D",
            [
                $"Bodies {physics.Bodies}  active {physics.ActiveBodies}  sleeping {physics.SleepingBodies}",
                $"Shapes {physics.Shapes}  triggers {physics.Triggers}  overlaps {physics.SensorOverlaps}",
                $"Contacts {physics.Contacts}"
            ]);
            EditorGUI.SettingsToggle(paper, "rd_phys_col", "Draw colliders",
                PhysicsDebugger.DrawColliders, v => PhysicsDebugger.DrawColliders = v,
                separator: false, compact: true);
            EditorGUI.SettingsToggle(paper, "rd_phys_con", "Draw contacts",
                PhysicsDebugger.DrawContacts, v => PhysicsDebugger.DrawContacts = v,
                separator: false, compact: true);
            EditorGUI.SettingsToggle(paper, "rd_phys_slp", "Draw sleeping",
                PhysicsDebugger.DrawSleeping, v => PhysicsDebugger.DrawSleeping = v,
                separator: false, compact: true);
        }
    }

    private static string JoinPassEvents(string[] events)
    {
        if (events.Length == 0)
            return "(none)";
        int count = events.Length < 8 ? events.Length : 8;
        string text = events[0];
        for (int i = 1; i < count; i++)
            text = text + " → " + events[i];
        if (events.Length > 8)
            text = text + " …";
        return text;
    }

    private static string[] FormatVolumes(VolumeDebugEntry[] volumes)
    {
        if (volumes.Length == 0)
            return ["(none)"];
        int count = volumes.Length < 8 ? volumes.Length : 8;
        var lines = new string[count];
        for (int i = 0; i < count; i++)
        {
            VolumeDebugEntry volume = volumes[i];
            string scope = volume.IsGlobal ? "global" : "local";
            string on = volume.Enabled ? "on" : "off";
            lines[i] = $"{volume.Name} {scope} w={volume.Weight:0.##} p={volume.Priority} {on}";
        }

        return lines;
    }

    private static void DrawSection(Paper paper, FontFile font, string title, string[] lines)
    {
        using (paper.Column("rd_" + title).Height(30 + lines.Length * 18).Padding(0, 8, 0, 0).Enter())
        {
            paper.Box("header").Height(22).Text(title, font).TextColor(EditorTheme.AccentText)
                .FontSize(EditorTheme.FontSize).Alignment(PaperTextAlignment.MiddleLeft);
            for (int i = 0; i < lines.Length; i++)
                paper.Box("line" + i).Height(18).Text(lines[i], font).TextColor(EditorTheme.Ink300)
                    .FontSize(EditorTheme.FontSizeSmall).Alignment(PaperTextAlignment.MiddleLeft);
        }
    }
}
