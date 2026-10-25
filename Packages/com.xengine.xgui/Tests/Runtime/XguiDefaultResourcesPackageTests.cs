namespace XEngine.XGUI.Runtime.Tests;

/// <summary>Compile-time marker so the package test assembly references default resource keys.</summary>
internal static class XguiDefaultResourcesPackageMarker
{
    public static readonly string SpritePath = XEngine.Runtime.UI.XguiDefaultResources.DefaultSprite;
    public static readonly string FontPath = XEngine.Runtime.UI.XguiDefaultResources.DefaultFont;
}
