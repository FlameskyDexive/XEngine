namespace XEngine.XGUI.Runtime.Tests;

/// <summary>Compile-time marker so the package test assembly references DPI/safe-area types.</summary>
internal static class XguiDpiSafeAreaPackageMarker
{
    public static readonly System.Type Scaler = typeof(XEngine.Runtime.UI.CanvasScaler);
    public static readonly System.Type Fitter = typeof(XEngine.Runtime.UI.SafeAreaFitter);
}
