using XEngine.Echo;

namespace XEngine.Ai.Host;

/// <summary>
/// ★ Layer 1 (P0-4) — Maps friendly component short-names (the names an AI agent naturally uses, e.g.
/// "Camera", "Light", "MeshRenderer") to the assembly-qualified <c>$type</c> string the Echo
/// serializer stamps on each component node. Also provides a minimal default field set per type, so
/// <c>scene_component_add</c> can construct a node the runtime will deserialize cleanly.
///
/// <para>The runtime falls back to <c>MissingMonobehaviour</c> for any unresolvable $type, so an
/// unknown short-name is NOT catastrophic — but the registry lets us validate the name up-front and
/// give the AI an actionable error + the list of known names instead of a silent fallback.</para>
///
/// <para>This is deliberately a hand-maintained list of the engine's built-in components. User
/// scripts (MonoBehaviour subclasses compiled into the project) are not enumerable offline (we'd need
/// to load the project assembly); for those, the caller can pass the full <c>$type</c> directly via
/// <c>scene_component_add</c>'s <c>componentType</c> and we trust them. The registry is the
/// friendly-name fast path.</para>
/// </summary>
internal static class ComponentRegistry
{
    /// <summary>Assembly-qualified name prefix used by all XEngine.Runtime components in saved scenes.</summary>
    private const string RuntimeAssembly =
        "XEngine.Runtime, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null";

    private const string TerrainRuntimeAssembly =
        "XEngine.Terrain.Runtime, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null";

    private const string NavigationRuntimeAssembly =
        "XEngine.AI.Navigation.Runtime, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null";

    /// <summary>Optional <c>com.xengine.spine</c> package runtime. The scene samples stamp 0.0.0.0
    /// (package asmdef assemblies carry no assembly version), so match that exactly.</summary>
    private const string SpineRuntimeAssembly =
        "XEngine.Spine.Runtime, Version=0.0.0.0, Culture=neutral, PublicKeyToken=null";

    /// <summary>The friendly short-name → full type-name map. Keep this in sync with the engine's
    /// built-in components (the same names the editor's Add Component menu surfaces).</summary>
    private static readonly Dictionary<string, string> ShortNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Camera"] = "XEngine.Runtime.Camera",
        ["MeshRenderer"] = "XEngine.Runtime.MeshRenderer",
        ["SkinnedMeshRenderer"] = "XEngine.Runtime.SkinnedMeshRenderer",
        ["VirtualGeometryRenderer"] = "XEngine.Runtime.VirtualGeometryRenderer",
        ["SpriteRenderer"] = "XEngine.Runtime.SpriteRenderer",
        ["SpriteFlipbook"] = "XEngine.Runtime.SpriteFlipbook",
        ["LineRenderer"] = "XEngine.Runtime.LineRenderer",
        ["TextComponent"] = "XEngine.Runtime.TextComponent",
        ["TextMeshComponent"] = "XEngine.Runtime.TextMeshComponent",
        ["GameCanvas"] = "XEngine.Runtime.GameCanvas",
        ["Animation"] = "XEngine.Runtime.AnimationComponent",
        ["AnimationComponent"] = "XEngine.Runtime.AnimationComponent",
        ["Animator"] = "XEngine.Runtime.Animator",
        ["AudioListener"] = "XEngine.Runtime.AudioListener",
        ["AudioSource"] = "XEngine.Runtime.AudioSource",
        ["DirectionalLight"] = "XEngine.Runtime.DirectionalLight",
        ["PointLight"] = "XEngine.Runtime.PointLight",
        ["SpotLight"] = "XEngine.Runtime.SpotLight",
        ["Light"] = "XEngine.Runtime.DirectionalLight", // alias — "Light" alone is abstract; default to Directional
        ["LightProbeGroup"] = "XEngine.Runtime.LightProbeGroup",
        ["ReflectionProbe"] = "XEngine.Runtime.ReflectionProbe",
        ["DecalProjector"] = "XEngine.Runtime.DecalProjector",
        ["Light2D"] = "XEngine.Runtime.Light2D",
        ["ShadowCaster2D"] = "XEngine.Runtime.ShadowCaster2D",
        ["PixelPerfectCamera"] = "XEngine.Runtime.PixelPerfectCamera",
        ["Rigidbody3D"] = "XEngine.Runtime.Rigidbody3D",
        ["BoxCollider"] = "XEngine.Runtime.BoxCollider",
        ["MeshCollider"] = "XEngine.Runtime.MeshCollider",
        ["SphereCollider"] = "XEngine.Runtime.SphereCollider",
        ["CapsuleCollider"] = "XEngine.Runtime.CapsuleCollider",
        ["CylinderCollider"] = "XEngine.Runtime.CylinderCollider",
        ["ConeCollider"] = "XEngine.Runtime.ConeCollider",
        ["CharacterController"] = "XEngine.Runtime.CharacterController",
        ["TriggerVolume"] = "XEngine.Runtime.TriggerVolume",
        ["Volume"] = "XEngine.Runtime.Volume",
        ["FogVolume"] = "XEngine.Runtime.FogVolume",
        ["TerrainCollider"] = "XEngine.Terrain.TerrainCollider, " + TerrainRuntimeAssembly,
        ["TerrainComponent"] = "XEngine.Terrain.TerrainComponent, " + TerrainRuntimeAssembly,
        ["Terrain"] = "XEngine.Terrain.TerrainComponent, " + TerrainRuntimeAssembly,
        ["WindZone"] = "XEngine.Terrain.WindZone, " + TerrainRuntimeAssembly,
        ["NavMeshAgent"] = "XEngine.Runtime.Navigation.NavMeshAgent, " + NavigationRuntimeAssembly,
        ["NavMeshObstacle"] = "XEngine.Runtime.Navigation.NavMeshObstacle, " + NavigationRuntimeAssembly,
        ["OffMeshLink"] = "XEngine.Runtime.Navigation.OffMeshLink, " + NavigationRuntimeAssembly,
        ["NavMeshSurface"] = "XEngine.AI.Navigation.NavMeshSurface, " + NavigationRuntimeAssembly,
        ["NavMeshModifier"] = "XEngine.AI.Navigation.NavMeshModifier, " + NavigationRuntimeAssembly,
        ["NavMeshModifierVolume"] = "XEngine.AI.Navigation.NavMeshModifierVolume, " + NavigationRuntimeAssembly,
        ["NavMeshLink"] = "XEngine.AI.Navigation.NavMeshLink, " + NavigationRuntimeAssembly,
        ["EventSystem"] = "XEngine.Runtime.EventSystem",
        ["RectTransform"] = "XEngine.Vector.RectTransform",
        ["ParticleSystem"] = "XEngine.Runtime.ParticleSystemComponent",
        // Optional com.xengine.spine package (requires the package enabled in the project's
        // Packages/manifest.json — otherwise the runtime falls back to MissingMonolikeaviour).
        ["SkeletonAnimation"] = "XEngine.Spine.SkeletonAnimation, " + SpineRuntimeAssembly,
        ["SkeletonRenderer"] = "XEngine.Spine.SkeletonRenderer, " + SpineRuntimeAssembly,
    };

    /// <summary>Resolve a short-name (or accept a full $type) to a full assembly-qualified type string.
    /// Returns null when the name is neither a known short-name nor already a full type name. The
    /// caller decides whether to reject or trust-through.</summary>
    public static string? ResolveTypeString(string componentType)
    {
        if (string.IsNullOrWhiteSpace(componentType)) return null;

        // Exact short-name hit → stamp the runtime assembly qualifier.
        if (ShortNames.TryGetValue(componentType, out string? full))
        {
            return full.Contains(',', StringComparison.Ordinal) ? full : $"{full}, {RuntimeAssembly}";
        }

        // Caller passed a full assembly-qualified name already (contains a comma) — trust through.
        // This is how user MonoBehaviour scripts get added (we can't enumerate them offline).
        if (componentType.Contains(','))
        {
            return componentType;
        }

        return null;
    }

    /// <summary>The list of friendly short-names an AI can use directly (for error messages /
    /// discoverability).</summary>
    public static IReadOnlyCollection<string> KnownShortNames => ShortNames.Keys;

    /// <summary>Build a minimal component node: $type + a fresh _identifier + the base MonoBehaviour
    /// fields the runtime expects. Component-type-specific defaults are left to the runtime's
    /// parameterless constructor (Echo constructs the instance then overlays serialized fields, so a
    /// sparse node is fine).</summary>
    public static EchoObject MakeMinimalNode(string fullTypeString, string? componentIdentifier = null)
    {
        EchoObject comp = EchoObject.NewCompound();
        comp["$type"] = new EchoObject(fullTypeString);
        // ★ Gen-P0.2 — _identifier/AssetID are Guid-typed fields; the serializer stores them in the
        // compact form {$t:"g",$v:"…"}. A plain string tag here would not deserialize into the Guid
        // field, so the engine would mint a fresh identity and orphan journal references.
        comp["_identifier"] = EchoFacade.MakeGuidTag(componentIdentifier ?? Guid.NewGuid().ToString());
        // Base MonoBehaviour fields the runtime reads on every component. Defaults match the
        // post-constructor state of a fresh MonoBehaviour.
        comp["_enabled"] = new EchoObject(true);
        comp["_enabledInHierarchy"] = new EchoObject(true);
        comp["HideFlags"] = new EchoObject(0L);
        comp["AssetPath"] = new EchoObject("");
        comp["AssetID"] = EchoFacade.MakeGuidTag("00000000-0000-0000-0000-000000000000");
        comp["Name"] = new EchoObject("");
        return comp;
    }
}
