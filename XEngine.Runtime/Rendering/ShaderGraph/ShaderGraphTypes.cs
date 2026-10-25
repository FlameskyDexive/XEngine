// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Collections.Generic;

namespace XEngine.Runtime.Rendering;

public enum ShaderGraphTargetKind
{
    Unlit = 0,
    Lit = 1,
    Decal = 2,
    Fullscreen = 3,
}

public enum ShaderGraphValueType
{
    Float = 0,
    Float2 = 1,
    Float3 = 2,
    Float4 = 3,
    Color = 4,
    Texture2D = 5,
}

public sealed class ShaderGraphProperty
{
    public string Id = "";
    public string Name = "";
    public string DisplayName = "";
    public ShaderGraphValueType Type;
    public string DefaultValue = "";
}

public sealed class ShaderGraphNodeField
{
    public string Key = "";
    public string Value = "";
}

public sealed class ShaderGraphNode
{
    public string Id = "";
    public string Type = "";
    public float PosX;
    public float PosY;
    public List<ShaderGraphNodeField> Fields = new();
}

public sealed class ShaderGraphEdge
{
    public string Id = "";
    public string FromNode = "";
    public string FromPort = "";
    public string ToNode = "";
    public string ToPort = "";
}
