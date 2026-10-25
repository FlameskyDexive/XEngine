namespace XEngine.Ai.Gateway;

/// <summary>
/// ★ M10 — <c>kind=texture</c> extras that turn fetch into a layout-preserving Spine page write.
/// Fail-fast providers must <see cref="Consume"/> these so <c>atlasPath</c> / <c>region</c> /
/// Host-injected <c>inputImagePath</c> are not rejected as typos.
/// </summary>
internal static class SpineAtlasGenExtras
{
    public const string AtlasPath = "atlasPath";
    public const string Region = "region";
    public const string SourcePagePath = "sourcePagePath";
    public const string PageName = "pageName";
    public const string Mode = "mode";
    public const string TintR = "tintR";
    public const string TintG = "tintG";
    public const string TintB = "tintB";
    public const string Pma = "pma";
    public const string Width = "width";
    public const string Height = "height";
    public const string InputImagePath = "inputImagePath";

    public static readonly string[] All =
    [
        AtlasPath, Region, SourcePagePath, PageName, Mode,
        TintR, TintG, TintB, Pma, Width, Height,
    ];

    public static void Consume(Func<string, string?> take)
    {
        ArgumentNullException.ThrowIfNull(take);
        for (int i = 0; i < All.Length; i++)
        {
            take(All[i]);
        }
    }
}
