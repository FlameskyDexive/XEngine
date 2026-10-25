using System;

using XEngine.Runtime;
using XEngine.Runtime.Resources;
using XEngine.Vector;

using AnimationCurve = XEngine.Runtime.AnimationCurve;

namespace XEngine.GpuAnimation.Samples;

/// <summary>
/// Self-contained crowd sample: builds a small skinned "flag puppet" procedurally, bakes it to
/// a vertex animation texture at runtime through the package baker, and spawns a GPU-instanced
/// animated crowd — no pre-baked assets needed. Drop this component on any empty GameObject
/// and press Play.
/// </summary>
[AddComponentMenu("Rendering/GPU Animated Crowd (Sample)")]
public class GpuAnimationCrowdSample : MonoBehaviour
{
    /// <summary>Crowd grid width/depth (GridSize x GridSize instances).</summary>
    public int GridSize = 100;

    /// <summary>Instance spacing in world units.</summary>
    public float Spacing = 1.2f;

    /// <summary>Use the lit (directional-lambert) crowd shader.</summary>
    public bool Lit;

    /// <summary>Randomize each instance's loop phase so the crowd desynchronizes.</summary>
    public bool RandomizePhase = true;

    private GpuAnimatedCrowd? _crowd;
    private BakedGpuAnimation? _asset;
    private Mesh? _vatMesh;
    private Mesh? _rigMesh;
    private GameObject? _rig;

    private void Start()
    {
        (_asset, _vatMesh, _rigMesh, _rig) = BuildFlagBake();
        var crowdObject = GameObject;
        _crowd = crowdObject.AddComponent<GpuAnimatedCrowd>();
        _crowd.BakedAsset = new AssetRef<BakedGpuAnimation>(_asset);
        _crowd.CrowdMesh = new AssetRef<Mesh>(_vatMesh);
        _crowd.Lit = Lit;

        var random = new Random(12345);
        for (int x = 0; x < GridSize; x++)
        for (int z = 0; z < GridSize; z++)
        {
            GpuAnimatedInstance inst = GpuAnimatedInstance.Place(
                new Float3(x * Spacing, 0f, z * Spacing),
                phase: RandomizePhase ? (float)random.NextDouble() : 0f,
                headingDegrees: (float)(random.NextDouble() * 360.0));
            inst.Tint = new Float4(
                0.7f + 0.3f * (x & 1), 0.7f + 0.3f * (z & 1), 0.75f, 1f);
            _crowd.Instances.Add(inst);
        }
    }

    private void OnDestroy()
    {
        _crowd?.ReleaseGpuResources();
        _asset?.Dispose();
        _vatMesh?.Dispose();
        _rigMesh?.Dispose();
    }

    /// <summary>
    /// Procedural two-bone skinned "flag" (8 vertices, top half skinned to the swinging tip
    /// bone), baked into a 1-clip VAT atlas. Static so tests can drive the exact same bake.
    /// </summary>
    public static (BakedGpuAnimation Asset, Mesh VatMesh, Mesh RigMesh, GameObject Rig) BuildFlagBake()
    {
        var rig = new GameObject("FlagRig");
        var pole = new GameObject("Pole");
        pole.SetParent(rig, worldPositionStays: false);
        var tip = new GameObject("Tip");
        tip.SetParent(pole, worldPositionStays: false);

        var mesh = new Mesh { Name = "SampleFlag" };
        mesh.Vertices = new[]
        {
            new Float3(-0.5f, 0f, 0f), new Float3(0.5f, 0f, 0f), new Float3(-0.5f, 1f, 0f), new Float3(0.5f, 1f, 0f),
            new Float3(-0.5f, 2f, 0f), new Float3(0.5f, 2f, 0f), new Float3(-0.5f, 3f, 0f), new Float3(0.5f, 3f, 0f),
        };
        mesh.Normals = new[]
        {
            new Float3(0f, 0f, 1f), new Float3(0f, 0f, 1f), new Float3(0f, 0f, 1f), new Float3(0f, 0f, 1f),
            new Float3(0f, 0f, 1f), new Float3(0f, 0f, 1f), new Float3(0f, 0f, 1f), new Float3(0f, 0f, 1f),
        };
        mesh.Indices = new uint[] { 0, 1, 2, 2, 1, 3, 2, 3, 4, 4, 3, 5, 4, 5, 6, 6, 5, 7 };
        mesh.BoneIndices = new[]
        {
            new Float4(1, 0, 0, 0), new Float4(1, 0, 0, 0), new Float4(1, 0, 0, 0), new Float4(1, 0, 0, 0),
            new Float4(2, 0, 0, 0), new Float4(2, 0, 0, 0), new Float4(2, 0, 0, 0), new Float4(2, 0, 0, 0),
        };
        mesh.BoneWeights = new[]
        {
            new Float4(1, 0, 0, 0), new Float4(1, 0, 0, 0), new Float4(1, 0, 0, 0), new Float4(1, 0, 0, 0),
            new Float4(1, 0, 0, 0), new Float4(1, 0, 0, 0), new Float4(1, 0, 0, 0), new Float4(1, 0, 0, 0),
        };
        mesh.BindPoses = new[] { Float4x4.Identity, Float4x4.Identity };

        var smr = rig.AddComponent<SkinnedMeshRenderer>();
        smr.SharedMesh = new AssetRef<Mesh>(mesh);
        smr.SetBones(new[] { pole.Transform, tip.Transform }, rig.Transform);

        Quaternion from = Quaternion.Identity;
        Quaternion to = Quaternion.FromEuler(0f, 0f, 70f);
        var clip = new AnimationClip { Name = "Swing", Duration = 1f };
        var tipBone = new AnimationClip.AnimBone { BoneName = "Pole/Tip" };
        tipBone.PosX = Curve(0f);
        tipBone.PosY = Curve(0f);
        tipBone.PosZ = Curve(0f);
        tipBone.RotX = Ramp(from.X, to.X);
        tipBone.RotY = Ramp(from.Y, to.Y);
        tipBone.RotZ = Ramp(from.Z, to.Z);
        tipBone.RotW = Ramp(from.W, to.W);
        clip.AddBone(tipBone);

        var request = new GpuAnimationBakeRequest { Target = rig, Fps = 8f, BakeNormals = true };
        request.Clips.Add(clip);
        GpuAnimationBakeResult result = GpuAnimationBaker.Bake(request);
        return (result.Asset, result.VatMesh, mesh, rig);
    }

    private static AnimationCurve Curve(float v) => new(new[] { new KeyFrame(0, v) });

    private static AnimationCurve Ramp(float v0, float v1)
    {
        var c = new AnimationCurve(new[] { new KeyFrame(0, v0), new KeyFrame(1f, v1) });
        c.SmoothTangents(CurveTangent.Linear);
        return c;
    }
}
