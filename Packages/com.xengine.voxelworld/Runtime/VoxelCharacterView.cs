using System;
using System.Collections.Generic;

using XEngine.Runtime;
using XEngine.Runtime.Resources;
using XEngine.Vector;

namespace XEngine.VoxelWorld;

/// <summary>Owned cube-rig body. Not Dungeon Mason.</summary>
[AddComponentMenu("Voxel World/Character View")]
public sealed class VoxelCharacterView : MonoBehaviour
{
    public bool MobBody;

    private static readonly Dictionary<int, Material> _tintCache = new();

    /// <summary>
    /// Rig cubes carry no authored material; without one the renderer skips them. Parts are
    /// tinted through the Standard shader's <c>_MainColor</c>, shared per distinct color.
    /// </summary>
    public static Material TintedRigMaterial(Color color)
    {
        int key = System.HashCode.Combine(color.R, color.G, color.B, color.A);
        if (!_tintCache.TryGetValue(key, out Material? material))
        {
            material = new Material(Shader.LoadDefault(DefaultShader.Standard));
            material._properties.SetColor("_MainColor", color);
            material.SetTexture("_MainTex", Texture2D.LoadDefault(DefaultTexture.White));
            _tintCache[key] = material;
        }
        return material;
    }

    public override void Start()
    {
        // The controller creates its HUD before our deferred Start. A canvas child
        // is not a rig; only an existing authored head suppresses auto assembly.
        foreach (GameObject child in GameObject.Children)
            if (child.Name == "Head" && child.GetComponent<MeshRenderer>() != null)
                return;
        Attach(GameObject, MobBody ? VoxelCharacterRig.Mob() : VoxelCharacterRig.Player());
        ApplyVisualStyle(anime: false);
    }

    /// <summary>Applies the first dual-style character profile without changing gameplay bounds.</summary>
    public void ApplyVisualStyle(bool anime)
    {
        VoxelBodyPart[] parts = MobBody ? VoxelCharacterRig.Mob() : VoxelCharacterRig.Player();
        for (int i = 0; i < parts.Length; i++)
        {
            VoxelBodyPart part = parts[i];
            GameObject? child = null;
            foreach (GameObject candidate in GameObject.Children)
                if (candidate.Name == part.Name) { child = candidate; break; }
            if (child == null)
                continue;
            child.Transform.LocalScale = anime ? AnimeScale(part.Name) : Float3.One;
            if (child.GetComponent<MeshRenderer>() is MeshRenderer renderer)
                renderer.Material = new AssetRef<Material>(TintedRigMaterial(anime ? AnimeColor(part.Name) : part.Color));
        }
    }

    private Float3 AnimeScale(string name) => name switch
    {
        "Head" => new Float3(1.16f, 1.1f, 0.96f),
        "Torso" or "Body" => new Float3(0.9f, 1.08f, 0.92f),
        "ArmL" or "ArmR" => new Float3(0.76f, 1f, 0.76f),
        _ when name.StartsWith("Leg", StringComparison.Ordinal) => new Float3(0.78f, 1f, 0.78f),
        _ => new Float3(0.9f, 1f, 0.9f),
    };

    private Color AnimeColor(string name)
    {
        if (MobBody)
            return name.StartsWith("Eye", StringComparison.Ordinal)
                ? new Color((byte)132, (byte)255, (byte)180)
                : new Color((byte)142, (byte)192, (byte)132);
        return name switch
        {
            "Head" or "ArmL" or "ArmR" => new Color((byte)255, (byte)214, (byte)184),
            "Torso" => new Color((byte)92, (byte)170, (byte)238),
            _ => new Color((byte)82, (byte)96, (byte)184),
        };
    }

    public static void Attach(GameObject root, VoxelBodyPart[] parts)
    {
        for (int i = 0; i < parts.Length; i++)
        {
            VoxelBodyPart part = parts[i];
            var go = new GameObject(part.Name);
            go.SetParent(root, worldPositionStays: false);
            go.Transform.LocalPosition = part.LocalPosition;
            var renderer = go.AddComponent<MeshRenderer>();
            renderer.Mesh = new AssetRef<Mesh>(Mesh.CreateCube(part.Size));
            renderer.Material = new AssetRef<Material>(TintedRigMaterial(part.Color));
        }
    }
}
