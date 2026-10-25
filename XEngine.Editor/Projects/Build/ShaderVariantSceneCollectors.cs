// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using XEngine.Runtime;
using XEngine.Runtime.Resources;

namespace XEngine.Editor.Build;

/// <summary>
/// Optional scene-scan hook for shader variant collection. Packages register collectors so
/// <see cref="VulkanShaderVariantCollector"/> does not take a compile-time dependency on Terrain.
/// </summary>
public interface IShaderVariantSceneCollector
{
    void Collect(Scene scene, string scenePath, ShaderVariantSceneSink sink);
}

/// <summary>Process-wide registry of <see cref="IShaderVariantSceneCollector"/> implementations.</summary>
public static class ShaderVariantSceneCollectors
{
    private static readonly List<IShaderVariantSceneCollector> Collectors = [];
    private static readonly object Gate = new();

    public static void Register(IShaderVariantSceneCollector collector)
    {
        ArgumentNullException.ThrowIfNull(collector);
        lock (Gate)
        {
            if (!Collectors.Contains(collector))
                Collectors.Add(collector);
        }
    }

    public static void Unregister(IShaderVariantSceneCollector collector)
    {
        if (collector == null)
            return;
        lock (Gate)
            Collectors.Remove(collector);
    }

    internal static void CollectAll(Scene scene, string scenePath, ShaderVariantSceneSink sink)
    {
        lock (Gate)
        {
            for (int i = 0; i < Collectors.Count; i++)
                Collectors[i].Collect(scene, scenePath, sink);
        }
    }
}

/// <summary>Package-facing sink that forwards into the Vulkan variant collector.</summary>
public sealed class ShaderVariantSceneSink
{
    private readonly Action<Material?, Shader?, Guid, string, IReadOnlyDictionary<string, bool>> _addMaterialOrBuiltIn;
    private readonly Action<Material?, Mesh, string> _addInstancedMesh;

    internal ShaderVariantSceneSink(
        Action<Material?, Shader?, Guid, string, IReadOnlyDictionary<string, bool>> addMaterialOrBuiltIn,
        Action<Material?, Mesh, string> addInstancedMesh)
    {
        _addMaterialOrBuiltIn = addMaterialOrBuiltIn;
        _addInstancedMesh = addInstancedMesh;
    }

    public void AddMaterialOrBuiltIn(
        Material? material,
        DefaultShader fallback,
        string source,
        IReadOnlyDictionary<string, bool> keywords) =>
        AddMaterialOrShader(
            material,
            Shader.LoadDefault(fallback),
            BuiltInAssets.GuidFor(fallback),
            source,
            keywords);

    public void AddMaterialOrShader(
        Material? material,
        Shader? fallbackShader,
        Guid fallbackGuid,
        string source,
        IReadOnlyDictionary<string, bool> keywords) =>
        _addMaterialOrBuiltIn(material, fallbackShader, fallbackGuid, source, keywords);

    public void AddInstancedMesh(Material? material, Mesh mesh, string source) =>
        _addInstancedMesh(material, mesh, source);
}
