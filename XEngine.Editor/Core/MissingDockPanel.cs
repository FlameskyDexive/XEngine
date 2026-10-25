using XEngine.OrigamiUI;
using XEngine.PaperUI;

namespace XEngine.Editor.Core;

/// <summary>Retains an unavailable panel entry so layout recovery does not silently discard it.</summary>
public sealed class MissingDockPanel : DockPanel
{
    public MissingDockPanel(string missingTypeName) => MissingTypeName = missingTypeName;

    public string MissingTypeName { get; }
    public override string Title => $"Missing: {MissingTypeName}";

    public override void OnGUI(Paper paper, float width, float height) { }
}
