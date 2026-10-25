using System;
using System.Collections.Generic;

using XEngine.NodePlay;
using XEngine.OrigamiUI;
using XEngine.PaperUI;
using XEngine.Vector;

using Color = System.Drawing.Color;

namespace XEngine.NodePlay.Editor;

/// <summary>
/// Canvas minimap strip: one chip per node in row order (accent-coloured, click = FocusNode),
/// plus a live read-out of the viewport pan/zoom. Flow-layout by design — the whole-graph
/// "where am I / jump to" answer without absolute positioning.
/// </summary>
internal static class NodePlayMinimap
{
    private const float StripHeight = 64f;

    public static void Draw(Paper paper, string idPrefix, float width,
        NodeGraphController controller, IReadOnlyList<GraphNode> nodes)
    {
        using (paper.Column(idPrefix + "_col").Width(width).Height(StripHeight).Enter())
        {
            Float2 view = controller.Pan;
            Origami.Label(paper, idPrefix + "_view",
                    $"minimap  view ({view.X:0},{view.Y:0})  zoom {controller.Zoom:0.00}  nodes {nodes.Count}")
                .Show();

            int shown = Math.Min(nodes.Count, 48);
            for (int i = 0; i < shown; i++)
            {
                GraphNode node = nodes[i];
                Color fill = node.Accent ?? Color.FromArgb(0xE4, 0xB9, 0x54);
                string label = node.Title.Length > 14 ? node.Title[..14] : node.Title;
                GraphNode captured = node;
                Origami.Button(paper, $"{idPrefix}_n{i}", label, () => controller.FocusNode(captured.Id))
                    .Show();
            }

            if (nodes.Count > shown)
                Origami.Label(paper, idPrefix + "_more", $"+{nodes.Count - shown} more").Show();
        }
    }
}
