// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

namespace XEngine.PostProcessing;

/// <summary>
/// Associates a <see cref="PostProcessEffectSettings"/> type with a renderer and an Add-effect menu path.
/// </summary>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class PostProcessAttribute : Attribute
{
    public Type Renderer { get; }
    public PostProcessEvent EventType { get; }
    public string MenuItem { get; }
    public bool AllowInSceneView { get; }
    public bool BuiltinEffect { get; }

    public PostProcessAttribute(Type renderer, PostProcessEvent eventType, string menuItem, bool allowInSceneView = true)
    {
        Renderer = renderer;
        EventType = eventType;
        MenuItem = menuItem;
        AllowInSceneView = allowInSceneView;
        BuiltinEffect = false;
    }

    public PostProcessAttribute(Type renderer, string menuItem, bool allowInSceneView = true)
        : this(renderer, PostProcessEvent.BeforeStack, menuItem, allowInSceneView)
    {
        BuiltinEffect = true;
    }
}
