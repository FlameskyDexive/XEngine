using System.Reflection;

using XEngine.Editor.Core;
using XEngine.VFX.Editor;

using Xunit;

namespace XEngine.Editor.Test;

public sealed class VfxMcpScanTests
{
    [Fact]
    public void PackageEditor_DeclaresMcpServerToolType()
    {
        Assert.True(McpToolAssemblyScan.ContainsMcpServerTools(typeof(VfxRuntimeTools).Assembly));
    }

    [Fact]
    public void Collect_AlwaysIncludesEditorAssembly()
    {
        Assembly[] asms = McpToolAssemblyScan.Collect();
        Assert.Contains(asms, a => a == typeof(EditorApplication).Assembly);
    }

    [Fact]
    public void CollectState_WithoutScene_ReturnsEmpty()
    {
        VfxRuntimeTools.VfxLiveStateResult state = VfxRuntimeTools.CollectState();
        Assert.True(state.EffectCount >= 0);
        Assert.NotNull(state.Effects);
    }
}
