// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using XEngine.OrigamiUI;
using XEngine.Editor.Inspector;
using XEngine.PaperUI;
using XEngine.Rosetta;
using XEngine.Runtime;
using XEngine.Editor.Core;
using XEngine.Editor.Theming;
using XEngine.Editor.GUI.Panels;

namespace XEngine.Editor.GUI.Panels;

/// <summary>
/// A focused, floating Inspector window pinned to one object (Unity 6 "Properties" / focused
/// inspector). Opened from the Window menu (Alt+P) or the Hierarchy / Project context menus;
/// keeps editing its target regardless of the current Selection, like an always-locked inspector.
/// GameObjects are tracked by identifier so the panel survives scene switches.
/// </summary>
public class PropertiesPanel : DockPanel
{
    private Guid _goIdentifier;          // when the target was a GameObject
    private object? _pinnedTarget;       // ContentItem / EngineObject / any other object
    private string _title;

    public override string Title => _title;
    public override string Icon => EditorIcons.Sliders;

    public PropertiesPanel(object? target)
    {
        _title = Loc.Get("panel.properties");
        if (target is GameObject go)
        {
            _goIdentifier = go.Identifier;
            _title = go.Name;
        }
        else
        {
            _pinnedTarget = target;
            _title = target switch
            {
                ContentItem ci => ci.Name,
                EngineObject eo => eo.Name,
                _ => target?.ToString() ?? _title,
            };
        }
    }

    public PropertiesPanel() : this(null) { }

    public override void OnGUI(Paper paper, float width, float height)
    {
        var font = EditorTheme.DefaultFont;
        if (font == null) return;

        GameObject? go = null;
        if (_goIdentifier != Guid.Empty)
        {
            go = Runtime.Resources.Scene.Current?.FindObjectByIdentifier<GameObject>(_goIdentifier);
            if (go == null)
            {
                DrawMissing(paper, font, width, height);
                return;
            }
            _title = go.Name;
        }
        else if (_pinnedTarget == null)
        {
            DrawMissing(paper, font, width, height);
            return;
        }

        Origami.ScrollView(paper, "props_scroll", width, height).Body(() =>
        {
            var target = (object?)go ?? _pinnedTarget;
            try
            {
                if (target is GameObject targetGo)
                {
                    GameObjectInspector.Draw(paper, font, targetGo);
                }
                else if (target is ContentItem ci)
                {
                    InspectorPanel.DrawSelectionHeader(paper, font, ci);
                    Origami.Separator(paper, "props_sep_header").Show();
                    InspectorPanel.DrawAssetInspector(paper, font, ci);
                }
                else if (target is EngineObject eo)
                {
                    InspectorPanel.DrawSelectionHeader(paper, font, eo);
                    Origami.Separator(paper, "props_sep_header").Show();
                    InspectorPanel.DrawEngineObjectInspector(paper, font, eo);
                }
                else if (target != null)
                {
                    InspectorPanel.DrawSelectionHeader(paper, font, target);
                    Origami.Separator(paper, "props_sep_header").Show();
                    InspectorPanel.DrawGenericInspector(paper, font, target);
                }
            }
            catch (Exception ex)
            {
                Runtime.Debug.LogError($"[Properties] Drawing threw and was skipped: {ex.Message}");
                _pinnedTarget = null;
                _goIdentifier = Guid.Empty;
            }
        });
    }

    private void DrawMissing(Paper paper, Scribe.FontFile font, float width, float height)
    {
        using (paper.Column("props_missing").Size(width, height).Enter())
        {
            paper.Box("props_missing_spacer");
            paper.Box("props_missing_text")
                .Height(40)
                .Text(Loc.Get("properties.no_target"), font)
                .TextColor(EditorTheme.Ink300)
                .FontSize(EditorTheme.FontSize)
                .Alignment(TextAlignment.MiddleCenter);
            paper.Box("props_missing_spacer2");
        }
    }

    /// <summary>Open a focused Properties window for the given object.</summary>
    public static void Open(object? target)
    {
        if (target == null) return;
        var panel = new PropertiesPanel(target);
        EditorApplication.Instance?.OpenPanelInstance(panel, 360, 620);
    }

    [MenuItem("Window/General/Properties", priority: 3)]
    static void OpenMenu() => Open(Selection.ActiveObject);
}
