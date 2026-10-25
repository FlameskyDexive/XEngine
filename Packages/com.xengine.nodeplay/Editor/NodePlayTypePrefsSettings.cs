using System;

using XEngine.Editor.Projects.Settings;
using XEngine.NodePlay;
using XEngine.OrigamiUI;
using XEngine.PaperUI;

namespace XEngine.NodePlay.Editor;

/// <summary>
/// Preferred Types configurer (M19): project-settings page listing the node types pinned to the
/// top of the add-node browser. Persisted in Library/nodeplay-prefs.txt via NodePlayBrowser.
/// </summary>
[ProjectSettings("NodePlay", icon: "\xf522", order: 220, exportToBuild: false)]
public sealed class NodePlayTypePrefsSettings : ProjectSettingsBase
{
    private string _input = "";

    public override void OnGUI(Paper paper, float width)
    {
        Origami.Label(paper, "nptp_h",
            "Preferred node types — pinned to the top of the Add Node browser (◆).").Show();

        var types = NodePlayBrowser.TypesFor(null);
        for (int i = 0; i < types.Count; i++)
        {
            NPNodeTypeInfo info = types[i];
            if (!NodePlayBrowser.IsPreferred(info.Type.FullName!))
                continue;
            int captured = i;
            Origami.Label(paper, $"nptp_r{captured}", "◆ " + info.Name + "   (" + info.Category + ")").Show();
            Origami.Button(paper, $"nptp_x{captured}", "unpin", () =>
                NodePlayBrowser.TogglePreferred(types[captured].Type.FullName!)).Show();
        }

        Origami.TextField(paper, "nptp_in", _input, v => _input = v ?? "").Show();
        Origami.Button(paper, "nptp_add", "pin by name", () =>
        {
            string query = _input.Trim();
            foreach (NPNodeTypeInfo info in types)
            {
                if (string.Equals(info.Name, query, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(info.Type.Name, query, StringComparison.OrdinalIgnoreCase)
                    || string.Equals(info.Type.FullName, query, StringComparison.Ordinal))
                {
                    NodePlayBrowser.TogglePreferred(info.Type.FullName!);
                    _input = "";
                    return;
                }
            }
        }).Show();
    }
}
