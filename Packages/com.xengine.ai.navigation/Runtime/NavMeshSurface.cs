using System;

using XEngine.Runtime;
using XEngine.Runtime.Navigation;
using XEngine.Runtime.Resources;
using XEngine.Vector;

namespace XEngine.AI.Navigation;

/// <summary>
/// Collects scene geometry and bakes a <see cref="NavMeshData"/> asset. Query APIs live in
/// <see cref="XEngine.Runtime.Navigation.NavMesh"/> in this package; this component is the
/// authorization / bake shell.
/// </summary>
[AddComponentMenu("AI/NavMesh Surface")]
[ExecuteAlways]
[Preserve]
public sealed class NavMeshSurface : MonoBehaviour
{
    public NavMeshBuildSettings Settings = new();
    public LayerMask LayerMask = LayerMask.Everything;
    public AssetRef<NavMeshData> NavMeshData;
    public NavMeshDrawMode DrawMode = NavMeshDrawMode.Areas;
    public bool ShowNavMesh = true;
    public int TriangleBudget = 20000;

    [NonSerialized] private NavMeshData? _runtime;
    [NonSerialized] private NavMeshTriangulation? _viz;
    [NonSerialized] private Float3[]? _previewPath;

    public NavMeshData? BakedData => NavMeshData.Res ?? _runtime;

    public Float3[]? PreviewPath
    {
        get => _previewPath;
        set => _previewPath = value;
    }

    public override void OnEnable()
    {
        NavMeshData? data = BakedData;
        if (data != null)
            XEngine.Runtime.Navigation.NavMesh.AddNavMeshData(data);
    }

    public override void OnDisable()
    {
        NavMeshData? data = BakedData;
        if (data != null)
            XEngine.Runtime.Navigation.NavMesh.RemoveNavMeshData(data);
    }

    public NavMeshData BuildNavMesh()
    {
        Scene scene = Scene ?? throw new InvalidOperationException("NavMeshSurface is not in a scene.");
        NavMeshInputGeometry geom = CollectGeometry(scene);
        NavMeshData data = NavMeshBuilder.Build(geom, Settings);
        ApplyBaked(data, register: true);
        return data;
    }

    public NavMeshInputGeometry CollectGeometry(Scene scene)
    {
        NavMeshInputGeometry geom = NavMeshBuilder.Collect(scene, Settings, LayerMask, IncludeObject);
        CollectLinksAndVolumes(scene, geom);
        return geom;
    }

    public void ApplyBaked(NavMeshData data, bool register)
    {
        _runtime = data;
        _viz = NavMeshGeometry.Extract(data.RuntimeMesh, Math.Max(1, TriangleBudget));
        if (register)
        {
            XEngine.Runtime.Navigation.NavMesh.RemoveNavMeshData(data);
            XEngine.Runtime.Navigation.NavMesh.AddNavMeshData(data);
        }
    }

    public void ClearBaked()
    {
        NavMeshData? data = BakedData;
        if (data != null)
            XEngine.Runtime.Navigation.NavMesh.RemoveNavMeshData(data);
        _runtime = null;
        _viz = null;
        _previewPath = null;
        NavMeshData = default;
    }

    public override void DrawGizmos()
    {
        if (!ShowNavMesh) return;
        NavMeshData? data = BakedData;
        if (data == null) return;
        _viz ??= NavMeshGeometry.Extract(data.RuntimeMesh, Math.Max(1, TriangleBudget));
        NavMeshVisualizer.Draw(_viz, DrawMode);
        if (_previewPath is { Length: > 1 })
            NavMeshVisualizer.DrawPath(_previewPath, new Color(1f, 0.85f, 0.15f, 1f));
    }

    public override void DrawGizmosSelected()
    {
        Debug.DrawWireCube(Transform.Position, new Float3(0.35f, 0.35f, 0.35f), new Color(0.3f, 0.9f, 1f, 1f));
    }

    private bool IncludeObject(GameObject go) => !NavMeshModifier.ShouldIgnore(go);

    private static void CollectLinksAndVolumes(Scene scene, NavMeshInputGeometry geom)
    {
        NavMeshLink?[] links = scene.FindObjectsOfType<NavMeshLink>();
        for (int i = 0; i < links.Length; i++)
        {
            NavMeshLink? link = links[i];
            if (link == null || !link.EnabledInHierarchy) continue;
            Float3 start = link.StartWorld;
            Float3 end = link.EndWorld;
            geom.AddOffMeshConnection(
                NavMeshConvert.ToRecast(start),
                NavMeshConvert.ToRecast(end),
                MathF.Max(0.05f, link.Width * 0.5f),
                link.Bidirectional,
                link.Area,
                NavMeshAreas.FlagWalk | NavMeshAreas.FlagJump);
        }

        NavMeshModifierVolume?[] volumes = scene.FindObjectsOfType<NavMeshModifierVolume>();
        for (int i = 0; i < volumes.Length; i++)
        {
            NavMeshModifierVolume? volume = volumes[i];
            if (volume == null || !volume.EnabledInHierarchy) continue;
            geom.AddConvexVolume(volume.ToConvexVolume());
        }
    }
}
