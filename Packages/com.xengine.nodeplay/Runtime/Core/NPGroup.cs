namespace XEngine.NodePlay;

/// <summary>A titled, tinted group box drawn behind a cluster of nodes (spatial membership only).
/// Bounds are plain floats so the text format stays flat and trivially patchable.</summary>
public class NPGroup
{
    public string Id = NPNode.NewId();
    public string Title = "";
    public float X;
    public float Y;
    public float Width = 320f;
    public float Height = 240f;
    /// <summary>Header/border tint as RRGGBB hex; empty = theme default.</summary>
    public string Style = "";

    public override string ToString() => string.IsNullOrEmpty(Title) ? Id : Title;
}

/// <summary>A free-floating sticky note with inline text.</summary>
public class NPSticky
{
    public string Id = NPNode.NewId();
    public string Text = "";
    public float X;
    public float Y;
    public float Width = 160f;
    public float Height = 90f;
    public string Style = "";
}
