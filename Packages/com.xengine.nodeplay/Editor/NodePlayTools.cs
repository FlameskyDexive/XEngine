using System;
using System.ComponentModel;
using System.Threading.Tasks;
using System.Linq;

using XEngine.Ai.Server;

using ModelContextProtocol.Server;

using XEngine.NodePlay;
using XEngine.Editor;
using XEngine.Runtime;
using XEngine.Runtime.Resources;
using XEngine.Editor.Core;
using XEngine.NodePlay.BT;
using XEngine.NodePlay.Dialogue;
using XEngine.NodePlay.FSM;

namespace XEngine.NodePlay.Editor;

/// <summary>
/// Live-editor NodePlay tools (M21): open graphs and focus nodes in the graph editor window via
/// the in-process MCP server (--serve). Tools run on the main thread through the dispatcher.
/// </summary>
[McpServerToolType]
public static class NodePlayTools
{
    [McpServerTool(Name = "nodeplay_open", UseStructuredContent = true)]
    [Description("Open a .nodeplay graph in the NodePlay graph editor window (live editor). Returns the window state.")]
    public static Task<object> OpenGraph(
        MainThreadDispatcher dispatcher,
        [Description("Project-relative .nodeplay path (e.g. Assets/NodePlay/Soldier.nodeplay).")] string graphPath)
    {
        return dispatcher.InvokeOnMainThreadAsync<object>(() =>
        {
            var backend = EditorAssetBackend.Instance;
            var entry = backend?.GetEntry(graphPath);
            if (entry == null)
                return Task.FromResult<object>(new NodePlayResult(false, "entry not found: " + graphPath));

            object? asset = AssetDatabase.Get(entry.Guid);
            if (asset == null)
                return Task.FromResult<object>(new NodePlayResult(false, "asset not loaded: " + graphPath));

            NodePlayWindow.OpenGraph(asset as NPGraphAsset);
            return Task.FromResult<object>(new NodePlayResult(true, "opened " + graphPath));
        });
    }

    [McpServerTool(Name = "nodeplay_focus", UseStructuredContent = true)]
    [Description("Focus a node (by node Id or display name) inside the already-open NodePlay editor: selects it for the Inspector and pans the canvas to it. Live status colors show if a runner is playing.")]
    public static Task<object> FocusNode(
        MainThreadDispatcher dispatcher,
        [Description("Node Id (guid-like) or display name, e.g. 'Log Chase'.")] string node,
        [Description("Project-relative .nodeplay path; optional when exactly one editor window has a graph.")] string? graphPath = null)
    {
        var asm = AppDomain.CurrentDomain.GetAssemblies().First(
            a => a.GetName().Name == "XEngine.NodePlay.Editor");
        var winType = asm.GetType("XEngine.NodePlay.Editor.NodePlayWindow")!;
        var window = EditorApplication.Instance?.FindOpenPanel(winType);
        if (window == null)
            return Task.FromResult<object>(new NodePlayResult(false, "NodePlay editor window is not open (call nodeplay_open first)."));

        var bf = System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance;
        var graphField = winType.GetField("_graph", bf)!;
        var graph = graphField.GetValue(window) as NPGraphAsset;
        if (graph == null)
            return Task.FromResult<object>(new NodePlayResult(false, "window has no graph open"));

        NPNode? target = null;
        foreach (NPNode candidate in graph.Nodes)
        {
            if (candidate.Id == node
                || string.Equals(candidate.DisplayName, node, StringComparison.OrdinalIgnoreCase)
                || string.Equals(candidate.Name, node, StringComparison.OrdinalIgnoreCase)
                || string.Equals(candidate.Comment, node, StringComparison.OrdinalIgnoreCase))
            {
                target = candidate;
                break;
            }
        }

        if (target == null)
            return Task.FromResult<object>(new NodePlayResult(false, "node not found: " + node));

        return dispatcher.InvokeOnMainThreadAsync<object>(() =>
        {
            winType.GetField("_inspectId", bf)!.SetValue(window, target.Id);
            var controller = winType.GetField("_controller", bf)!.GetValue(window);
            controller!.GetType().GetMethod("FocusNode")!.Invoke(controller, new object?[] { target.Id });
            return Task.FromResult<object>(new NodePlayResult(true, "focused " + target.DisplayName + " (" + target.Id + ")"));
        });
    }

    public sealed record NodePlayResult(bool Succeeded, string Message);
}
