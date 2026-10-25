// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Text;

namespace XEngine.Runtime.RHI.Shaders;

internal static class ShaderReflectionContractComparer
{
    public static string CompareLogical(ShaderCompileResult expected, ShaderCompileResult actual) =>
        Compare(expected, actual, includePhysicalLayout: false);

    public static string CompareFull(ShaderCompileResult expected, ShaderCompileResult actual) =>
        Compare(expected, actual, includePhysicalLayout: true);

    private static string Compare(
        ShaderCompileResult expected,
        ShaderCompileResult actual,
        bool includePhysicalLayout)
    {
        ArgumentNullException.ThrowIfNull(expected);
        ArgumentNullException.ThrowIfNull(actual);

        var differences = new StringBuilder();
        CompareBindings(
            "buffer",
            expected.BindingLayout?.Buffers ?? [],
            actual.BindingLayout?.Buffers ?? [],
            includePhysicalLayout,
            differences);
        CompareBindings(
            "texture",
            expected.BindingLayout?.Textures ?? [],
            actual.BindingLayout?.Textures ?? [],
            includePhysicalLayout,
            differences);
        CompareBindings(
            "sampler",
            expected.BindingLayout?.Samplers ?? [],
            actual.BindingLayout?.Samplers ?? [],
            includePhysicalLayout,
            differences);
        CompareVertexInputs(
            expected.VertexInputLayout?.Inputs ?? [],
            actual.VertexInputLayout?.Inputs ?? [],
            differences);
        return differences.ToString();
    }

    private static void CompareBindings(
        string group,
        ShaderBindingSlot[] expected,
        ShaderBindingSlot[] actual,
        bool includePhysicalLayout,
        StringBuilder differences)
    {
        if (expected.Length != actual.Length)
            differences.AppendLine($"{group} count: expected {expected.Length}, actual {actual.Length}");

        int count = Math.Min(expected.Length, actual.Length);
        for (int i = 0; i < count; i++)
        {
            ShaderBindingSlot left = expected[i];
            ShaderBindingSlot right = actual[i];
            string path = $"{group}[{i}]";
            AppendDifference(path, "kind", left.Kind, right.Kind, differences);
            AppendDifference(path, "space", left.Space, right.Space, differences);
            AppendDifference(path, "slot", left.Slot, right.Slot, differences);
            AppendDifference(path, "name", left.Name, right.Name, differences);
            AppendDifference(path, "count", left.Count, right.Count, differences);
            AppendDifference(path, "stages", left.Stages, right.Stages, differences);

            if (!includePhysicalLayout)
                continue;

            ComparePhysicalBindings(path, left.PhysicalBindings, right.PhysicalBindings, differences);
            AppendDifference(path, "shape", left.ResourceShape, right.ResourceShape, differences);
            AppendDifference(path, "byteSize", left.ByteSize, right.ByteSize, differences);
            CompareFields(path, left.UniformFields, right.UniformFields, differences);
        }
    }

    private static void ComparePhysicalBindings(
        string bindingPath,
        ShaderPhysicalBinding[] expected,
        ShaderPhysicalBinding[] actual,
        StringBuilder differences)
    {
        if (expected.Length != actual.Length)
        {
            differences.AppendLine(
                $"{bindingPath}.physical binding count: expected {expected.Length}, actual {actual.Length}");
        }

        int count = Math.Min(expected.Length, actual.Length);
        for (int i = 0; i < count; i++)
        {
            ShaderPhysicalBinding left = expected[i];
            ShaderPhysicalBinding right = actual[i];
            string path = $"{bindingPath}.physicalBinding[{i}]";
            AppendDifference(path, "space", left.Space, right.Space, differences);
            AppendDifference(path, "binding", left.Binding, right.Binding, differences);
            AppendDifference(path, "stages", left.Stages, right.Stages, differences);
        }
    }

    private static void CompareFields(
        string bindingPath,
        ShaderUniformField[] expected,
        ShaderUniformField[] actual,
        StringBuilder differences)
    {
        if (expected.Length != actual.Length)
            differences.AppendLine($"{bindingPath}.field count: expected {expected.Length}, actual {actual.Length}");

        int count = Math.Min(expected.Length, actual.Length);
        for (int i = 0; i < count; i++)
        {
            ShaderUniformField left = expected[i];
            ShaderUniformField right = actual[i];
            string path = $"{bindingPath}.field[{i}]";
            AppendDifference(path, "name", left.Name, right.Name, differences);
            AppendDifference(path, "offset", left.Offset, right.Offset, differences);
            AppendDifference(path, "size", left.Size, right.Size, differences);
            AppendDifference(path, "scalar", left.ScalarType, right.ScalarType, differences);
            AppendDifference(path, "rows", left.Rows, right.Rows, differences);
            AppendDifference(path, "columns", left.Columns, right.Columns, differences);
            AppendDifference(path, "elements", left.ElementCount, right.ElementCount, differences);
            AppendDifference(path, "arrayStride", left.ArrayStride, right.ArrayStride, differences);
            AppendDifference(path, "matrixStride", left.MatrixStride, right.MatrixStride, differences);
        }
    }

    private static void CompareVertexInputs(
        ShaderVertexInputSlot[] expected,
        ShaderVertexInputSlot[] actual,
        StringBuilder differences)
    {
        if (expected.Length != actual.Length)
            differences.AppendLine($"vertex input count: expected {expected.Length}, actual {actual.Length}");

        int count = Math.Min(expected.Length, actual.Length);
        for (int i = 0; i < count; i++)
        {
            ShaderVertexInputSlot left = expected[i];
            ShaderVertexInputSlot right = actual[i];
            string path = $"vertex[{i}]";
            AppendDifference(path, "semantic", left.Semantic, right.Semantic, differences);
            AppendDifference(path, "semanticName", left.SemanticName, right.SemanticName, differences);
            AppendDifference(path, "semanticIndex", left.SemanticIndex, right.SemanticIndex, differences);
            AppendDifference(path, "location", left.Location, right.Location, differences);
            AppendDifference(path, "format", left.Format, right.Format, differences);
        }
    }

    private static void AppendDifference<T>(
        string path,
        string property,
        T expected,
        T actual,
        StringBuilder differences)
    {
        if (!Equals(expected, actual))
            differences.AppendLine($"{path}.{property}: expected {expected}, actual {actual}");
    }
}
