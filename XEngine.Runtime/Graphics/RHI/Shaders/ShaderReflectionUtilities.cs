// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

namespace XEngine.Runtime.RHI.Shaders;

internal static class ShaderReflectionUtilities
{
    public static bool TryMapVertexSemantic(string name, uint index, out uint semantic)
    {
        switch (name.ToUpperInvariant())
        {
            case "POSITION" when index == 0:
                semantic = (uint)VertexFormat.VertexSemantic.Position;
                return true;
            case "TEXCOORD" when index == 0:
                semantic = (uint)VertexFormat.VertexSemantic.TexCoord0;
                return true;
            case "TEXCOORD" when index == 1:
                semantic = (uint)VertexFormat.VertexSemantic.TexCoord1;
                return true;
            case "TEXCOORD" when index >= 8:
                semantic = index;
                return true;
            case "NORMAL" when index == 0:
                semantic = (uint)VertexFormat.VertexSemantic.Normal;
                return true;
            case "COLOR" when index == 0:
                semantic = (uint)VertexFormat.VertexSemantic.Color;
                return true;
            case "TANGENT" when index == 0:
                semantic = (uint)VertexFormat.VertexSemantic.Tangent;
                return true;
            case "BLENDINDICES" when index == 0:
                semantic = (uint)VertexFormat.VertexSemantic.BoneIndex;
                return true;
            case "BLENDWEIGHT" when index == 0:
                semantic = (uint)VertexFormat.VertexSemantic.BoneWeight;
                return true;
            default:
                semantic = 0;
                return false;
        }
    }

    public static bool TryParseHlslVertexFormat(string typeName, out ShaderVertexInputFormat format)
    {
        string normalized = typeName.Trim().ToLowerInvariant();
        int suffixIndex = normalized.Length;
        while (suffixIndex > 0 && char.IsAsciiDigit(normalized[suffixIndex - 1]))
            suffixIndex--;

        byte componentCount = 1;
        if (suffixIndex < normalized.Length
            && (!byte.TryParse(normalized.AsSpan(suffixIndex), out componentCount)
                || componentCount is < 1 or > 4))
        {
            format = default;
            return false;
        }

        ShaderScalarType scalarType = normalized[..suffixIndex] switch
        {
            "float" => ShaderScalarType.Float32,
            "half" => ShaderScalarType.Float16,
            "int" => ShaderScalarType.Int32,
            "uint" => ShaderScalarType.UInt32,
            "bool" => ShaderScalarType.Bool,
            _ => ShaderScalarType.Unknown,
        };

        format = new ShaderVertexInputFormat(scalarType, componentCount);
        return scalarType != ShaderScalarType.Unknown;
    }
}
