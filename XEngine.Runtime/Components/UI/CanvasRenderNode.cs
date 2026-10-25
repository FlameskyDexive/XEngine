// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Runtime.Rendering;
using XEngine.Runtime.Resources;
using XEngine.Vector;
using XEngine.Vector.Geometry;

namespace XEngine.Runtime.UI;

/// <summary>
/// Runtime-only render submission for a Canvas primitive. It intentionally exposes
/// only mesh/material/model/culling data and never references a concrete XGUI type.
/// The compatibility adapter delegates the payload to the existing UI render item.
/// </summary>
public sealed class CanvasRenderNode : IRenderable
{
    private IRenderable? _payload;
    private readonly PropertyState _properties = new();
    private bool _standalone;

    public long SortKey { get; private set; }
    public UISurface Surface { get; private set; }
    public CanvasClipState Clip { get; private set; }
    public Mesh? Mesh { get; private set; }
    public Material? Material { get; private set; }
    public Float4x4 Model { get; private set; }
    public bool IsAttached => _payload != null || _standalone;

    public CanvasRenderNode() { }

    internal void Attach(
        IRenderable payload,
        Mesh mesh,
        Material material,
        in Float4x4 model,
        long sortKey,
        UISurface surface,
        CanvasClipState clip)
    {
        _payload = payload;
        _standalone = false;
        Mesh = mesh;
        Material = material;
        Model = model;
        SortKey = sortKey;
        Surface = surface;
        Clip = clip;
    }

    internal void ConfigureStandalone(
        Mesh? mesh,
        Material? material,
        in Float4x4 model,
        long sortKey,
        UISurface surface,
        CanvasClipState clip,
        PropertyState? properties = null)
    {
        _payload = null;
        _standalone = true;
        Mesh = mesh;
        Material = material;
        Model = model;
        SortKey = sortKey;
        Surface = surface;
        Clip = clip;
        _properties.Clear();
        if (properties != null)
            _properties.ApplyOverride(properties);
    }

    internal void SetProperties(PropertyState properties)
    {
        _properties.Clear();
        _properties.ApplyOverride(properties);
    }

    internal void UpdateModel(in Float4x4 model) => Model = model;

    internal void RefreshSubmission(
        Mesh mesh,
        Material material,
        in Float4x4 model,
        long sortKey,
        UISurface surface,
        CanvasClipState clip)
    {
        Mesh = mesh;
        Material = material;
        Model = model;
        SortKey = sortKey;
        Surface = surface;
        Clip = clip;
    }

    internal void Detach()
    {
        _payload = null;
        _standalone = false;
        Mesh = null;
        Material = null;
        Model = Float4x4.Identity;
        SortKey = 0;
        Surface = default;
        Clip = CanvasClipState.None;
    }

    public Material GetMaterial() => _payload?.GetMaterial() ?? Material!;
    public int GetLayer() => _payload?.GetLayer() ?? 0;
    public Float3 GetPosition() => _payload?.GetPosition() ?? Float4x4.TransformPoint(Float3.Zero, Model);
    public int GetSubMeshIndex() => _payload?.GetSubMeshIndex() ?? -1;

    public void GetRenderingData(
        ViewerData viewer,
        out PropertyState properties,
        out Mesh mesh,
        out Float4x4 model,
        out InstanceData[]? instanceData)
    {
        if (_payload == null)
        {
            properties = _properties;
            ApplyClipProperties(properties);
            mesh = Mesh!;
            model = Model;
            instanceData = null;
            return;
        }

        _payload.GetRenderingData(viewer, out properties, out mesh, out model, out instanceData);
    }

    private void ApplyClipProperties(PropertyState properties)
    {
        if (Clip.HasRect)
        {
            properties.SetFloat("_ClipEnable", 1f);
            properties.SetMatrix("_ClipToLocal", Clip.ClipToLocal);
            properties.SetVector("_ClipRect", Clip.ClipRect);
            properties.SetFloat("_ClipRadius", Clip.CornerRadius);
            properties.SetFloat("_ClipSoftness", Clip.Softness);
        }
        else
        {
            properties.SetFloat("_ClipEnable", 0f);
        }
    }

    public Float4x4 GetWorldToObjectMatrix(in Float4x4 model)
        => _payload?.GetWorldToObjectMatrix(model) ?? model.Invert();

    public void GetCullingData(out bool isRenderable, out AABB bounds)
    {
        if (_payload == null)
        {
            // Standalone CanvasRenderer submissions do not have a legacy IRenderable
            // payload. They still participate in world-space culling using their mesh
            // bounds transformed by the published model matrix.
            Mesh? mesh = Mesh;
            isRenderable = mesh != null && mesh.IsValid();
            bounds = isRenderable ? mesh!.bounds.TransformBy(Model) : default;
            return;
        }

        _payload.GetCullingData(out isRenderable, out bounds);
    }

    public bool IsStaticForShadows => false;
    public ulong ShadowCasterVersion => 0;
}
