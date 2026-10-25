using XEngine.Editor.Inspector;
using XEngine.Runtime.Resources;

using Xunit;

namespace XEngine.Editor.Test;

public sealed class MaterialEditorGuiTests
{
    [Fact]
    public void SaveIsDueAfterDelay()
    {
        Assert.False(MaterialEditorGUI.IsSaveDue(1f, 1.4f));
        Assert.True(MaterialEditorGUI.IsSaveDue(1f, 1.5f));
        Assert.True(MaterialEditorGUI.IsSaveDue(1f, 2f));
    }

    [Fact]
    public void MarkDirtyIgnoresRuntimeInstancesWithoutAssetId()
    {
        var material = new Material();
        Assert.Equal(Guid.Empty, material.AssetID);
        MaterialEditorGUI.MarkDirty(material);
        Assert.False(MaterialEditorGUI.HasPendingSaves);
    }

    [Fact]
    public void TrySaveReturnsFalseWithoutProjectOrAssetId()
    {
        Assert.False(MaterialEditorGUI.TrySave(new Material()));
        Assert.False(MaterialEditorGUI.TrySave(null!));
    }
}
