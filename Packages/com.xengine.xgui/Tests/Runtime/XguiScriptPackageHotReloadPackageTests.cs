namespace XEngine.XGUI.Runtime.Tests;

/// <summary>Compile-time marker so the package test assembly stays bound after hot-reload work.</summary>
internal static class XguiScriptPackageHotReloadPackageMarker
{
    public static readonly System.Type GraphicRegistry = typeof(XEngine.Runtime.UI.GraphicRegistry);
}
