using System;
using System.IO;

using Spine;

namespace XEngine.Spine;

/// <summary>Parse a Spine <c>.atlas</c> and read packed page rectangles (y from the top of the page).</summary>
public static class SpineAtlasLayout
{
    public static Atlas Parse(string atlasText)
    {
        using var reader = new StringReader(atlasText ?? "");
        return new Atlas(reader, "", SpineNoopTextureLoader.Instance);
    }

    /// <summary>
    /// Packed rectangle on the page after <see cref="Atlas"/> parse.
    /// 90° regions have width/height swapped to the on-page size.
    /// </summary>
    public static void GetPackedRect(AtlasRegion region, out int x, out int y, out int width, out int height)
    {
        ArgumentNullException.ThrowIfNull(region);
        x = region.x;
        y = region.y;
        width = region.width;
        height = region.height;
    }
}
