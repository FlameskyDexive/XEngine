// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

using XEngine.Runtime;
using XEngine.Runtime.Rendering;
using XEngine.Runtime.Resources;
using XEngine.Vector;

using Rendering = XEngine.Runtime.Rendering;

namespace XEngine.Terrain;

internal class TerrainTreeRenderer
{
    private readonly List<Float4x4> _transforms = [];
    private readonly List<Float4> _colors = [];
    private readonly List<Rendering.InstanceData> _instanceDataList = [];
    private readonly List<Float4x4> _billboardTransforms = [];
    private readonly List<Rendering.InstanceData> _billboardInstances = [];
    private static Material? s_defaultStandardMat;
    private static readonly PropertyState s_emptyProps = new();
    private Mesh? _billboardMesh;

    public void Dispose()
    {
        _billboardMesh?.Dispose();
        _billboardMesh = null;
    }

    public void CollectRenderables(
        TerrainData data, TerrainComponent terrain, Camera camera,
        float maxDistance, List<IRenderable> renderables)
    {
        if (data.Trees.Count == 0 || data.TreePrototypes.Count == 0) return;

        Float3 camLocal = terrain.WorldToTerrain(camera.Transform.Position);
        camLocal.Y = 0;
        Float3 camWorld = camera.Transform.Position;
        float terrainSize = data.Size;
        float maxDistSq = maxDistance * maxDistance;
        float billboardStart = MathF.Max(0f, terrain.TreeBillboardDistance);

        for (int protoIdx = 0; protoIdx < data.TreePrototypes.Count; protoIdx++)
        {
            var proto = data.TreePrototypes[protoIdx];
            var mesh = proto.Mesh.Res;
            if (mesh == null) continue;

            s_defaultStandardMat ??= Material.LoadDefault(DefaultMaterial.Standard);

            _transforms.Clear();
            _colors.Clear();
            _instanceDataList.Clear();
            _billboardTransforms.Clear();
            _billboardInstances.Clear();

            float meshExtent = mesh.bounds.Size.X > 0 ? MathF.Max(mesh.bounds.Size.X, mesh.bounds.Size.Z) * 0.5f : 5f;
            float meshHeight = mesh.bounds.Size.Y > 0 ? mesh.bounds.Size.Y : 20f;

            foreach (var tree in data.Trees)
            {
                if (tree.PrototypeIndex != protoIdx) continue;

                float lx = tree.Position.X * terrainSize;
                float lz = tree.Position.Y * terrainSize;
                float ly = data.GetInterpolatedHeight(tree.Position.X, tree.Position.Y);

                float dx = lx - (float)camLocal.X;
                float dz = lz - (float)camLocal.Z;
                float distSq = dx * dx + dz * dz;
                if (distSq > maxDistSq) continue;

                Float4x4 localTransform = Float4x4.CreateTranslation(new Float3(lx, ly, lz))
                    * Float4x4.FromAxisAngle(new Float3(0, 1, 0), tree.Rotation)
                    * Float4x4.CreateScale(new Float3(tree.WidthScale, tree.HeightScale, tree.WidthScale));
                var worldTransform = terrain.Transform.LocalToWorldMatrix * localTransform;
                Float3 worldPos = new(worldTransform.c3.X, worldTransform.c3.Y, worldTransform.c3.Z);
                var color = new Float4(tree.Tint.R, tree.Tint.G, tree.Tint.B, tree.Tint.A);
                float dist = MathF.Sqrt(distSq);

                if (IsBillboardDistance(dist, billboardStart, maxDistance))
                {
                    float width = MathF.Max(0.1f, tree.WidthScale * meshExtent * 2f);
                    float height = MathF.Max(0.1f, tree.HeightScale * meshHeight);
                    Float4x4 billboard = CreateBillboardMatrix(worldPos, camWorld, width, height);
                    billboard = WindZone.ApplySway(billboard, worldPos, proto.BendFactor);
                    _billboardTransforms.Add(billboard);
                    _billboardInstances.Add(new Rendering.InstanceData(billboard, color));
                    continue;
                }

                worldTransform = WindZone.ApplySway(worldTransform, worldPos, proto.BendFactor);
                _transforms.Add(worldTransform);
                _colors.Add(color);
                _instanceDataList.Add(new Rendering.InstanceData(worldTransform, color));
            }

            if (_instanceDataList.Count > 0)
            {
                SubmitMeshInstances(renderables, terrain, mesh, proto, meshExtent, meshHeight);
            }

            if (_billboardInstances.Count > 0)
            {
                _billboardMesh ??= CreateBillboardQuad();
                Material? billboardMat = null;
                if (proto.Materials.Count > 0)
                    billboardMat = CollectionsMarshal.AsSpan(proto.Materials)[0].Res;
                billboardMat ??= s_defaultStandardMat;
                if (billboardMat != null)
                {
                    AABB bbBounds = BoundsFromTransforms(_billboardTransforms, meshExtent, meshHeight);
                    renderables.Add(new InstancedMeshRenderable(
                        _billboardMesh, billboardMat, _billboardInstances.ToArray(),
                        (bbBounds.Min + bbBounds.Max) * 0.5f,
                        terrain.GameObject.LayerIndex,
                        s_emptyProps,
                        bbBounds,
                        renderingLayerMask: terrain.RenderingLayers.Mask));
                }
            }
        }
    }

    internal static bool IsBillboardDistance(float distance, float billboardStart, float maxDistance)
        => distance <= maxDistance && distance >= billboardStart;

    internal static bool UsesBillboard(float distanceSquared, float billboardStartSquared, float maxDistanceSquared)
        => distanceSquared <= maxDistanceSquared && distanceSquared >= billboardStartSquared;

    internal static Float4x4 CreateBillboardMatrix(Float3 worldPosition, Float3 cameraPosition, float width, float height)
    {
        Float3 toCamera = cameraPosition - worldPosition;
        toCamera.Y = 0f;
        float lenSq = Float3.LengthSquared(toCamera);
        Float3 forward = lenSq > 1e-8f ? toCamera / MathF.Sqrt(lenSq) : Float3.UnitZ;
        Float3 up = Float3.UnitY;
        Float3 right = Float3.Cross(up, forward);
        float rightLenSq = Float3.LengthSquared(right);
        right = rightLenSq > 1e-8f ? right / MathF.Sqrt(rightLenSq) : Float3.UnitX;
        forward = Float3.Normalize(Float3.Cross(right, up));
        return new Float4x4(
            new Float4(right.X * width, right.Y * width, right.Z * width, 0f),
            new Float4(up.X * height, up.Y * height, up.Z * height, 0f),
            new Float4(forward.X, forward.Y, forward.Z, 0f),
            new Float4(worldPosition.X, worldPosition.Y, worldPosition.Z, 1f));
    }

    private void SubmitMeshInstances(
        List<IRenderable> renderables, TerrainComponent terrain, Mesh mesh, TreePrototype proto,
        float meshExtent, float meshHeight)
    {
        Float3 bmin = new(float.MaxValue), bmax = new(float.MinValue);
        foreach (var tr in _transforms)
        {
            float tx = tr[0, 3], ty = tr[1, 3], tz = tr[2, 3];
            bmin = new Float3(MathF.Min(bmin.X, tx - meshExtent), MathF.Min(bmin.Y, ty), MathF.Min(bmin.Z, tz - meshExtent));
            bmax = new Float3(MathF.Max(bmax.X, tx + meshExtent), MathF.Max(bmax.Y, ty + meshHeight), MathF.Max(bmax.Z, tz + meshExtent));
        }

        var instanceArr = _instanceDataList.ToArray();
        Float3 sortPos = (bmin + bmax) * 0.5f;
        AABB worldBounds = new AABB(bmin, bmax);
        int subMeshCount = mesh.SubMeshCount;

        for (int sub = 0; sub < subMeshCount; sub++)
        {
            Material? mat = null;
            if (sub < proto.Materials.Count) mat = CollectionsMarshal.AsSpan(proto.Materials)[sub].Res;
            mat ??= s_defaultStandardMat;
            if (mat == null) continue;

            renderables.Add(new InstancedMeshRenderable(
                mesh, mat, instanceArr,
                sortPos,
                terrain.GameObject.LayerIndex,
                s_emptyProps,
                worldBounds,
                subMeshIndex: subMeshCount > 1 ? sub : -1,
                renderingLayerMask: terrain.RenderingLayers.Mask));
        }
    }

    private static AABB BoundsFromTransforms(List<Float4x4> transforms, float meshExtent, float meshHeight)
    {
        Float3 bmin = new(float.MaxValue), bmax = new(float.MinValue);
        foreach (var tr in transforms)
        {
            float tx = tr.c3.X, ty = tr.c3.Y, tz = tr.c3.Z;
            bmin = new Float3(MathF.Min(bmin.X, tx - meshExtent), MathF.Min(bmin.Y, ty), MathF.Min(bmin.Z, tz - meshExtent));
            bmax = new Float3(MathF.Max(bmax.X, tx + meshExtent), MathF.Max(bmax.Y, ty + meshHeight), MathF.Max(bmax.Z, tz + meshExtent));
        }

        return new AABB(bmin, bmax);
    }

    private static Mesh CreateBillboardQuad()
    {
        var mesh = new Mesh();
        mesh.Vertices = [new(-0.5f, 0, 0), new(0.5f, 0, 0), new(0.5f, 1, 0), new(-0.5f, 1, 0)];
        mesh.UV = [new(0, 0), new(1, 0), new(1, 1), new(0, 1)];
        mesh.Normals = [Float3.UnitZ, Float3.UnitZ, Float3.UnitZ, Float3.UnitZ];
        mesh.Indices = [0, 2, 1, 0, 3, 2];
        mesh.RecalculateBounds();
        mesh.Upload();
        return mesh;
    }
}
