using System;

namespace XEngine.Editor.GUI.SceneView;

[AttributeUsage(AttributeTargets.Class)]
public class SceneViewEditorForAttribute : Attribute
{
    public Type ComponentType { get; }
    public SceneViewEditorForAttribute(Type componentType) => ComponentType = componentType;
}
