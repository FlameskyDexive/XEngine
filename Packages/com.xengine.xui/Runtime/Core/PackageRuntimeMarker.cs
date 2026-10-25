namespace XEngine.XUI;

/// <summary>Marker used by package/build audits to prove XUI Runtime is present.</summary>
public static class XUIRuntime
{
    public const string PackageName = "com.xengine.xui";
    public const string Version = "0.1.0";

    /// <summary>
    /// The <c>version</c> attribute value the <c>.xui</c> document root must carry. Bumped only on a
    /// breaking IR change; <c>Tools/XEngine.Ai.Host</c> pins the same constant so the offline tools and
    /// the runtime assembler can never disagree about which documents they understand.
    /// </summary>
    public const int DocumentVersion = 1;
}
