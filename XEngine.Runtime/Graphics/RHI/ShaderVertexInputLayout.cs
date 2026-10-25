// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

namespace XEngine.Runtime.RHI;

public readonly record struct ShaderVertexInputFormat(
    ShaderScalarType ScalarType,
    byte ComponentCount);

public sealed class ShaderVertexInputLayout
{
    public ShaderVertexInputSlot[] Inputs { get; init; } = [];

    public bool TryGetLocation(uint semantic, out uint location)
    {
        for (int i = 0; i < Inputs.Length; i++)
        {
            ShaderVertexInputSlot input = Inputs[i];
            if (input.Semantic == semantic)
            {
                location = input.Location;
                return true;
            }
        }

        location = 0;
        return false;
    }
}

public readonly record struct ShaderVertexInputSlot(
    uint Semantic,
    uint Location,
    string SemanticName = "",
    uint SemanticIndex = 0,
    ShaderVertexInputFormat Format = default);
