using System;

using XEngine.PaperUI;
using XEngine.Runtime;

namespace XEngine.Editor.Inspector;

/// <summary>
/// Attribute to register a custom asset editor for a specific EngineObject type.
/// </summary>
[AttributeUsage(AttributeTargets.Class)]
public class CustomAssetEditorAttribute : Attribute
{
    public Type TargetType { get; }
    public CustomAssetEditorAttribute(Type targetType) => TargetType = targetType;
}

/// <summary>
/// Base class for custom asset editors shown in the inspector when an asset is selected.
/// </summary>
public abstract class AssetImporterEditor
{
    /// <summary>Draw the asset editor UI.</summary>
    public abstract void OnGUI(Paper paper, string id, AssetEntry entry, EngineObject? asset);

    /// <summary>
    /// Whether this editor draws its own content in the inspector's docked bottom preview bar.
    /// Lets optional packages (Spine, VFX) join the universal preview without the engine
    /// switch in <c>InspectorPanel.SetupAndDrawPreview</c> knowing their types.
    /// </summary>
    public virtual bool HasPreview(EngineObject? asset) => false;

    /// <summary>Draw the docked preview content for <paramref name="asset"/> into a
    /// <paramref name="width"/> x <paramref name="height"/> area.</summary>
    public virtual void OnPreviewGUI(Paper paper, string id, EngineObject asset, float width, float height) { }

    /// <summary>Release preview resources (render targets, preview scenes). Called on script
    /// reload and when the inspector drops the preview.</summary>
    public virtual void OnPreviewCleanup() { }
}

