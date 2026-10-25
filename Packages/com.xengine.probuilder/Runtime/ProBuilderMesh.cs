// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using XEngine.Echo;
using XEngine.Runtime;
using XEngine.Runtime.Resources;
using XEngine.Vector;

namespace XEngine.ProBuilder;

/// <summary>Parametric shape set (zonezero M5 — the shapes the ZZZ arena needs).</summary>
public enum ProBuilderShape
{
    /// <summary>Subdivided ground grid on the XZ plane.</summary>
    Plane,
    /// <summary>Axis-aligned box (six faces, hard edges).</summary>
    Cube,
    /// <summary>Ascending steps along +Z (step count derived from size/stepHeight).</summary>
    Stairs,
    /// <summary>Box sheared into a ramp along +Z.</summary>
    Ramp,
}

/// <summary>
/// Minimal Unity-ProBuilder equivalent (zonezero M5): a parametric shape component that
/// generates an engine <see cref="Mesh"/> and wires the sibling <see cref="MeshRenderer"/>
/// (+ a static <see cref="MeshCollider"/> when <see cref="AddCollider"/> is set — no
/// Rigidbody means world-static collision, exactly what arena geometry needs).
/// Shape edits go through <see cref="Rebuild"/>; the mesh itself stays a plain engine mesh.
/// </summary>
[AddComponentMenu("ProBuilder/ProBuilder Mesh")]
public sealed class ProBuilderMesh : MonoBehaviour
{
    [SerializeField] private ProBuilderShape _shape = ProBuilderShape.Cube;
    [SerializeField] private Float3 _size = Float3.One;
    [SerializeField] private int _segments = 1;
    [SerializeField] private float _stepHeight = 0.3f;
    [SerializeField] private bool _addCollider = true;
    [SerializeField] private AssetRef<Mesh>? _mesh;

    public ProBuilderShape Shape
    {
        get => _shape;
        set { _shape = value; Rebuild(); }
    }

    /// <summary>Full extents (not half-extents), matching Unity ProBuilder size semantics.</summary>
    public Float3 Size
    {
        get => _size;
        set { _size = value; Rebuild(); }
    }

    /// <summary>Grid subdivisions per axis for the Plane shape (1 = one quad).</summary>
    public int Segments
    {
        get => _segments;
        set { _segments = Math.Max(1, value); Rebuild(); }
    }

    /// <summary>Rise per step for the Stairs shape.</summary>
    public float StepHeight
    {
        get => _stepHeight;
        set { _stepHeight = MathF.Max(0.05f, value); Rebuild(); }
    }

    /// <summary>The generated mesh (asset reference once persisted, live instance before).</summary>
    public AssetRef<Mesh>? Mesh => _mesh;

    public override void Start() => Rebuild();

    /// <summary>Regenerates the mesh and refreshes renderer/collider wiring.</summary>
    public void Rebuild()
    {
        if (GameObject == null) return;

        Mesh mesh = BuildShape(_shape, _size, _segments, _stepHeight);
        if (_mesh?.Res is { IsDisposed: false } previous)
            previous.Dispose();
        _mesh = new AssetRef<Mesh>(mesh);

        MeshRenderer? renderer = GameObject.GetComponent<MeshRenderer>();
        if (renderer == null)
        {
            renderer = GameObject.AddComponent<MeshRenderer>();
            renderer.Material = new Material(Shader.LoadDefault(DefaultShader.Standard));
        }
        renderer.Mesh = mesh;

        if (_addCollider && GameObject.GetComponent<MeshCollider>() == null)
            GameObject.AddComponent<MeshCollider>();
    }

    /// <summary>Builds a parametric shape as a standalone engine mesh (unit-test friendly).</summary>
    public static Mesh BuildShape(ProBuilderShape shape, Float3 size, int segments = 1, float stepHeight = 0.3f)
    {
        return shape switch
        {
            ProBuilderShape.Plane => BuildPlane(size, Math.Max(1, segments)),
            ProBuilderShape.Stairs => BuildStairs(size, MathF.Max(0.05f, stepHeight)),
            ProBuilderShape.Ramp => BuildRamp(size),
            _ => BuildCube(size),
        };
    }

    // Subdivided XZ grid, +Y normals, UVs in world units so textures tile at 1/unit.
    private static Mesh BuildPlane(Float3 size, int segments)
    {
        float halfX = size.X * 0.5f, halfZ = size.Z * 0.5f;
        int vertsPerSide = segments + 1;
        var vertices = new Float3[vertsPerSide * vertsPerSide];
        var uvs = new Float2[vertices.Length];
        var indices = new uint[segments * segments * 6];

        int v = 0;
        for (int z = 0; z < vertsPerSide; z++)
        {
            for (int x = 0; x < vertsPerSide; x++)
            {
                float fx = segments == 0 ? 0 : (float)x / segments;
                float fz = segments == 0 ? 0 : (float)z / segments;
                vertices[v] = new Float3(-halfX + fx * size.X, 0f, -halfZ + fz * size.Z);
                uvs[v] = new Float2(vertices[v].X, vertices[v].Z);
                v++;
            }
        }

        int i = 0;
        for (int z = 0; z < segments; z++)
        {
            for (int x = 0; x < segments; x++)
            {
                int a = z * vertsPerSide + x;
                int b = a + 1;
                int c = a + vertsPerSide;
                int d = c + 1;
                indices[i++] = (uint)a; indices[i++] = (uint)c; indices[i++] = (uint)b;
                indices[i++] = (uint)b; indices[i++] = (uint)c; indices[i++] = (uint)d;
            }
        }

        var mesh = new Mesh { Name = "PB_Plane" };
        mesh.Vertices = vertices;
        mesh.UV = uvs;
        mesh.Indices = indices;
        mesh.RecalculateBounds();
        mesh.RecalculateNormals();
        mesh.RecalculateTangents();
        return mesh;
    }

    private static Mesh BuildCube(Float3 size)
    {
        float hx = size.X * 0.5f, hy = size.Y * 0.5f, hz = size.Z * 0.5f;
        Float3[] corners =
        {
            new(-hx, -hy, -hz), new(hx, -hy, -hz), new(hx, -hy, hz), new(-hx, -hy, hz),
            new(-hx, hy, -hz), new(hx, hy, -hz), new(hx, hy, hz), new(-hx, hy, hz),
        };
        // 24 verts (4 per face) so normals/UVs stay per-face sharp.
        Float3[] vertices = new Float3[24];
        Float2[] uvs = new Float2[24];
        uint[] indices = new uint[36];
        int[][] faces =
        {
            new[] { 4, 5, 6, 7 }, // +Y top
            new[] { 1, 0, 3, 2 }, // -Y bottom
            new[] { 7, 6, 2, 3 }, // +Z front
            new[] { 0, 1, 5, 4 }, // -Z back
            new[] { 5, 1, 2, 6 }, // +X right
            new[] { 0, 4, 7, 3 }, // -X left
        };
        for (int f = 0; f < 6; f++)
        {
            for (int c = 0; c < 4; c++)
            {
                vertices[f * 4 + c] = corners[faces[f][c]];
                uvs[f * 4 + c] = new Float2((c == 1 || c == 2) ? size.X : 0f, (c >= 2) ? size.Z : 0f);
            }
            uint b = (uint)(f * 4);
            int t = f * 6;
            indices[t] = b; indices[t + 1] = (uint)(b + 1); indices[t + 2] = (uint)(b + 2);
            indices[t + 3] = b; indices[t + 4] = (uint)(b + 2); indices[t + 5] = (uint)(b + 3);
        }

        var mesh = new Mesh { Name = "PB_Cube" };
        mesh.Vertices = vertices;
        mesh.UV = uvs;
        mesh.Indices = indices;
        mesh.RecalculateBounds();
        mesh.RecalculateNormals();
        mesh.RecalculateTangents();
        return mesh;
    }

    // Steps ascending along +Z: step i spans z in [i*s, (i+1)*s), top at (i+1)*stepHeight.
    private static Mesh BuildStairs(Float3 size, float stepHeight)
    {
        int steps = Math.Max(1, (int)MathF.Floor(size.Y / stepHeight + 0.5f));
        float stepDepth = size.Z / steps;
        float stepWidth = size.X;

        // Per step: tread + riser + two side quads; plus one tall back face = 4*steps + 1 quads.
        int quadCount = 4 * steps + 1;
        var vertices = new Float3[quadCount * 4];
        var indices = new uint[quadCount * 6];
        var uvs = new Float2[vertices.Length];
        int v = 0;
        uint quad = 0;

        void Quad(Float3 a, Float3 b, Float3 c, Float3 d)
        {
            int start = v;
            vertices[v++] = a; vertices[v++] = b; vertices[v++] = c; vertices[v++] = d;
            uvs[start] = new Float2(a.X, a.Z); uvs[start + 1] = new Float2(b.X, b.Z);
            uvs[start + 2] = new Float2(c.X, c.Z); uvs[start + 3] = new Float2(d.X, d.Z);
            uint b0 = (uint)start;
            indices[(int)quad * 6] = b0; indices[(int)quad * 6 + 1] = (uint)(start + 1); indices[(int)quad * 6 + 2] = (uint)(start + 2);
            indices[(int)quad * 6 + 3] = b0; indices[(int)quad * 6 + 4] = (uint)(start + 2); indices[(int)quad * 6 + 5] = (uint)(start + 3);
            quad++;
        }

        float hx = stepWidth * 0.5f;
        for (int i = 0; i < steps; i++)
        {
            float z0 = -size.Z * 0.5f + i * stepDepth;
            float z1 = z0 + stepDepth;
            float y = (i + 1) * stepHeight;

            // Tread (top) of step i.
            Quad(new Float3(-hx, y, z1), new Float3(hx, y, z1), new Float3(hx, y, z0), new Float3(-hx, y, z0));
            // Riser (front face) down to the previous step.
            Quad(new Float3(-hx, y, z0), new Float3(hx, y, z0), new Float3(hx, y - stepHeight, z0), new Float3(-hx, y - stepHeight, z0));
        }
        // Sides: one big quad per side (staircase silhouette).
        float topY = steps * stepHeight;
        for (int s = 0; s < steps; s++)
        {
            float z0 = -size.Z * 0.5f + s * stepDepth;
            float z1 = z0 + stepDepth;
            float y = (s + 1) * stepHeight;
            Quad(new Float3(hx, y, z1), new Float3(hx, y, z0), new Float3(hx, y - stepHeight, z0), new Float3(hx, y - stepHeight, z1));
            Quad(new Float3(-hx, y - stepHeight, z0), new Float3(-hx, y, z0), new Float3(-hx, y, z1), new Float3(-hx, y - stepHeight, z1));
        }
        Quad(new Float3(-hx, topY, size.Z * 0.5f), new Float3(hx, topY, size.Z * 0.5f), new Float3(hx, 0, size.Z * 0.5f), new Float3(-hx, 0, size.Z * 0.5f));

        var mesh = new Mesh { Name = "PB_Stairs" };
        var trimmedVertices = new Float3[v];
        var trimmedUvs = new Float2[v];
        Array.Copy(vertices, trimmedVertices, v);
        Array.Copy(uvs, trimmedUvs, v);
        mesh.Vertices = trimmedVertices;
        mesh.UV = trimmedUvs;
        mesh.Indices = indices;
        mesh.RecalculateBounds();
        mesh.RecalculateNormals();
        mesh.RecalculateTangents();
        return mesh;
    }

    private static Mesh BuildRamp(Float3 size)
    {
        var cube = BuildCube(size);
        var vertices = cube.Vertices;
        // Shear the top face toward -Z by the full depth: top edge z=+hz maps to z=-hz+0…
        // simple ramp: lower the +Z top edge to the floor.
        float hz = size.Z * 0.5f;
        for (int i = 0; i < vertices.Length; i++)
        {
            if (vertices[i].Y > 0 && vertices[i].Z > 0)
                vertices[i] = new Float3(vertices[i].X, -size.Y * 0.5f, hz);
        }
        cube.Vertices = vertices;
        cube.RecalculateBounds();
        cube.RecalculateNormals();
        cube.RecalculateTangents();
        return cube;
    }
}
