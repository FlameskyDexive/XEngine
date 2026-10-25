using System.Drawing;

using XEngine.OrigamiUI;

using Xunit;

namespace XEngine.Editor.Test;

public sealed class HierarchyZebraStripeTests
{
    private static readonly Color Alt = Color.FromArgb(255, 66, 66, 66);
    private static readonly Color Even = Color.FromArgb(255, 104, 104, 104);
    private static readonly Color Drop = Color.FromArgb(60, 168, 85, 247);

    [Fact]
    public void OddPaintedRowsUseAltWhenZebraEnabled()
    {
        Assert.Equal(Even, TreeBuilder.ResolveRowBackground(true, 0, false, Drop, Alt, Even));
        Assert.Equal(Alt, TreeBuilder.ResolveRowBackground(true, 1, false, Drop, Alt, Even));
        Assert.Equal(Even, TreeBuilder.ResolveRowBackground(true, 2, false, Drop, Alt, Even));
    }

    [Fact]
    public void ZebraDisabledKeepsRowsTransparent()
    {
        Assert.Equal(Color.Transparent, TreeBuilder.ResolveRowBackground(false, 1, false, Drop, Alt, Even));
    }

    [Fact]
    public void DropIntoTintWinsOverZebra()
    {
        Assert.Equal(Drop, TreeBuilder.ResolveRowBackground(true, 1, true, Drop, Alt, Even));
    }
}
