using XEngine.NodePlay;
using XEngine.NodePlay.BT;
using XEngine.NodePlay.Editor;

using Xunit;

namespace XEngine.Editor.Test;

/// <summary>M12: node type discovery (categories/names), favourites toggle, recent persistence.</summary>
public sealed class NodePlayBrowserTests
{
    [Fact]
    public void Discovery_FindsCompositesWithAttributes()
    {
        IReadOnlyList<NPNodeTypeInfo> btTypes = NodePlayBrowser.TypesFor(typeof(BTNode));
        Assert.Contains(btTypes, t => t.Type == typeof(BTSelector) && t.Category == "Composites" && t.Name == "Selector");
        Assert.Contains(btTypes, t => t.Type == typeof(BTConditionalEvaluator) && !string.IsNullOrEmpty(t.Description));

        // module filter excludes non-BT nodes
        Assert.DoesNotContain(btTypes, t => t.Type == typeof(NPRelayNode));
        Assert.Contains(NodePlayBrowser.TypesFor(null), t => t.Type == typeof(NPRelayNode));
    }

    [Fact]
    public void Favorites_ToggleRoundTrip()
    {
        string typeName = typeof(BTProbabilitySelector).FullName!;
        NodePlayBrowser.ToggleFavorite(typeName);
        Assert.True(NodePlayBrowser.IsFavorite(typeName));
        NodePlayBrowser.ToggleFavorite(typeName);
        Assert.False(NodePlayBrowser.IsFavorite(typeName));
    }
}
