// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using XEngine.Echo;
using XEngine.Runtime;
using XEngine.Runtime.Resources;
using XEngine.Runtime.Rendering;
using XEngine.Vector;

namespace XEngine.Runtime;

/// <summary>
/// Unity TrailRenderer port (zonezero M9): a camera-facing ribbon dragged behind the
/// GameObject, built on the same per-frame billboarded-ribbon mesh approach as
/// <see cref="LineRenderer"/>. Points are recorded when the transform moves more than
/// <see cref="MinVertexDistance"/>; the ribbon fades tail→head over
/// <see cref="Time"/> seconds with the material's vertex-color alpha.
/// </summary>
[AddComponentMenu("Effects/Trail Renderer")]
public sealed class TrailRenderer : MonoBehaviour
{
    [SerializeField] private AssetRef<Material>? _material;
    [SerializeField] private float _time = 1f;
    [SerializeField] private float _startWidth = 0.1f;
    [SerializeField] private float _endWidth = 0f;
    [SerializeField] private float _minVertexDistance = 0.1f;
    [SerializeField] private int _maxPoints = 128;
    [SerializeField] private Color _startColor = Color.White;

    private readonly List<TrailPoint> _points = new();
    private Mesh? _mesh;
    private bool _meshDirty = true;

    private struct TrailPoint
    {
        public Float3 Position;
        public float Age;
    }

    public AssetRef<Material>? Material
    {
        get => _material;
        set => _material = value;
    }

    /// <summary>Lifetime of a trail point in seconds (Unity trail time).</summary>
    public float Time
    {
        get => _time;
        set => _time = MathF.Max(0.01f, value);
    }

    public float StartWidth
    {
        get => _startWidth;
        set => _startWidth = value;
    }

    public float EndWidth
    {
        get => _endWidth;
        set => _endWidth = value;
    }

    /// <summary>Minimum transform movement before a new point is recorded.</summary>
    public float MinVertexDistance
    {
        get => _minVertexDistance;
        set => _minVertexDistance = value;
    }

    public int PointCount => _points.Count;

    public override void OnEnable()
    {
        _points.Clear();
        _meshDirty = true;
    }

    public override void OnRemovedFromScene()
    {
        _mesh?.Dispose();
        _mesh = null;
    }

    public override void OnDispose()
    {
        _mesh?.Dispose();
        _mesh = null;
        base.OnDispose();
    }

    public override void Update()
    {
        float dt = XEngine.Runtime.Time.DeltaTime;
        Float3 position = Transform.Position;

        // Age out old points.
        for (int i = _points.Count - 1; i >= 0; i--)
        {
            TrailPoint point = _points[i];
            point.Age += dt;
            _points[i] = point;
            if (point.Age > _time)
            {
                _points.RemoveAt(i);
                _meshDirty = true;
            }
        }

        // Record new points on movement.
        bool headMoved = _points.Count == 0
            || Float3.Length(position - _points[0].Position) >= _minVertexDistance;
        if (headMoved)
        {
            _points.Insert(0, new TrailPoint { Position = position, Age = 0f });
            if (_points.Count > _maxPoints)
                _points.RemoveAt(_points.Count - 1);
            _meshDirty = true;
        }
    }

    public override void OnRenderCollect(Camera camera, List<IRenderable> renderables, List<IRenderableLight>? lights = null)
    {
        Material? material = _material?.Res;
        if (_points.Count < 2 || material == null) return;

        _mesh ??= CreateStripMesh();
        if (_meshDirty)
        {
            RebuildMesh(camera);
            _meshDirty = false;
        }
        if (_mesh.Vertices.Length == 0) return;

        renderables.Add(new MeshRenderable(_mesh, material, Float4x4.Identity,
            GameObject?.LayerIndex ?? 0));
    }

    /// <summary>Rebuilds the camera-facing ribbon: per point, right = normalize(cross(toCamera,
    /// tangent)); width lerps start→end across the trail; alpha fades with point age.</summary>
    private void RebuildMesh(Camera camera)
    {
        int segments = _points.Count - 1;
        var vertices = new Float3[(segments + 1) * 2];
        var colors = new Color[vertices.Length];
        var uvs = new Float2[vertices.Length];
        var indices = new uint[segments * 6];

        Float3 cameraPosition = camera.Transform.Position;
        for (int i = 0; i <= segments; i++)
        {
            Float3 position = _points[i].Position;
            Float3 tangent = i == 0
                ? _points[1].Position - _points[0].Position
                : i == segments
                    ? _points[segments].Position - _points[segments - 1].Position
                    : _points[i + 1].Position - _points[i - 1].Position;
            Float3 toCamera = cameraPosition - position;
            Float3 right = Float3.Normalize(Float3.Cross(toCamera, tangent));
            if (Float3.Length(right) < 0.001f)
                right = Float3.Normalize(Float3.Cross(Float3.Cross(tangent, toCamera), tangent));

            float t = segments > 0 ? (float)i / segments : 0f;
            float width = Maths.Lerp(_startWidth, _endWidth, t) * 0.5f;
            float age01 = Maths.Clamp(_points[i].Age / _time, 0f, 1f);
            float alpha = 1f - age01;

            vertices[i * 2] = position - right * width;
            vertices[i * 2 + 1] = position + right * width;
            colors[i * 2] = new Color(_startColor.R, _startColor.G, _startColor.B, _startColor.A * alpha);
            colors[i * 2 + 1] = colors[i * 2];
            uvs[i * 2] = new Float2(t, 0f);
            uvs[i * 2 + 1] = new Float2(t, 1f);
        }

        for (int s = 0; s < segments; s++)
        {
            uint b = (uint)(s * 2);
            int o = s * 6;
            indices[o] = b; indices[o + 1] = (uint)(b + 1); indices[o + 2] = (uint)(b + 2);
            indices[o + 3] = (uint)(b + 1); indices[o + 4] = (uint)(b + 3); indices[o + 5] = (uint)(b + 2);
        }

        _mesh!.Vertices = vertices;
        _mesh.UV = uvs;
        _mesh.Colors = colors;
        _mesh.Indices = indices;
        _mesh.RecalculateBounds();
    }

    private static Mesh CreateStripMesh() => new() { Name = "TrailStrip" };
}
