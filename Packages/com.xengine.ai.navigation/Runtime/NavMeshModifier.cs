using XEngine.Runtime;
using XEngine.Runtime.Navigation;

namespace XEngine.AI.Navigation;

/// <summary>Marks a GameObject (and optionally its children) as ignored or area-overridden during bake.</summary>
[AddComponentMenu("AI/NavMesh Modifier")]
[Preserve]
public sealed class NavMeshModifier : MonoBehaviour
{
    public bool IgnoreFromBuild;
    public bool OverrideArea;
    public int Area = NavMeshAreas.Walkable;
    public bool ApplyToChildren = true;

    public static bool ShouldIgnore(GameObject go)
    {
        NavMeshModifier? modifier = go.GetComponentInParent<NavMeshModifier>();
        if (modifier == null || !modifier.IgnoreFromBuild) return false;
        if (modifier.GameObject == go) return true;
        return modifier.ApplyToChildren;
    }
}
