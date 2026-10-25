// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using XEngine.Echo;
using XEngine.Runtime.Resources;

namespace XEngine.Runtime.VirtualGeometry.Generation;

/// <summary>
/// Registers the virtual geometry mesh feature: reads settings, invokes
/// <see cref="VirtualGeometryBaker"/>. Auto-discovered by <see cref="MeshFeatures.MeshFeatureRegistry"/>
/// via reflection; sub-asset name is <c>{meshName}_virtualGeometry</c>.
/// </summary>
public sealed class VirtualGeometryFeatureSpec : MeshFeatures.MeshFeatureSpec
{
    public const string KeyRoot = "virtualGeometry";
    public const string Key_Enabled = "enabled";
    public const string Key_ClusterTriangles = "clusterTriangles";
    public const string Key_SimplifyRatio = "simplifyRatio";
    public const string Key_MaxLodLevels = "maxLodLevels";

    public override string Key => KeyRoot;
    public override string DisplayName => "Virtual Geometry";
    public override Type FeatureType => typeof(VirtualGeometryData);
    public override int Version => 1;

    public override void PopulateDefaults(EchoObject settings)
    {
        var vg = EchoObject.NewCompound();
        vg[Key_Enabled] = new EchoObject(false);
        vg[Key_ClusterTriangles] = new EchoObject(128);
        vg[Key_SimplifyRatio] = new EchoObject(0.5f);
        vg[Key_MaxLodLevels] = new EchoObject(16);
        settings[Key] = vg;
    }

    public override EngineObject? TryGenerate(Mesh mesh, EchoObject? settings)
    {
        var options = ReadOptions(settings, out bool enabled);
        if (!enabled) return null;

        if (!mesh.HasNormals)
        {
            // Virtual geometry renders through the standard lighting pipeline; meshes without
            // normals still bake, the warning surfaces the likely shading artifact instead.
            Debug.LogWarning($"[VirtualGeometry] Mesh '{mesh.Name ?? "unnamed"}' has no normals; simplified levels inherit source attributes verbatim.");
        }

        return VirtualGeometryBaker.Generate(mesh, options);
    }

    private static VirtualGeometryBakeOptions ReadOptions(EchoObject? settings, out bool enabled)
    {
        var opts = VirtualGeometryBakeOptions.Default;
        enabled = false;
        if (settings == null) return opts;
        if (!settings.TryGet(KeyRoot, out var vg) || vg == null) return opts;

        if (vg.TryGet(Key_Enabled, out var e)) enabled = e.BoolValue;
        if (vg.TryGet(Key_ClusterTriangles, out var ct))
            opts.ClusterTriangles = Math.Clamp(ct.IntValue, 8, 256);
        if (vg.TryGet(Key_SimplifyRatio, out var sr))
            opts.SimplifyRatio = Math.Clamp(sr.FloatValue, 0.05f, 0.95f);
        if (vg.TryGet(Key_MaxLodLevels, out var ml))
            opts.MaxLodLevels = Math.Clamp(ml.IntValue, 1, 32);
        return opts;
    }
}
