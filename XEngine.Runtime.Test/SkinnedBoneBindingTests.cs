using XEngine.Echo;
using XEngine.Runtime.Resources;
using XEngine.Vector;
using Xunit;

namespace XEngine.Runtime.Test;

public sealed class SkinnedBoneBindingTests
{
    [Fact]
    public void ValidationClearsMissingAndNullRootsAndRebindsBoneOrder()
    {
        using var root = new GameObject("Model");
        var hips = Child("Hips", root); var spine = Child("Spine", hips);
        var body = Child("Body", root); var renderer = body.AddComponent<SkinnedMeshRenderer>();
        renderer.SetBones([hips.Transform, spine.Transform], hips.Transform);
        Assert.Same(hips.Transform, renderer.RootBone);
        renderer.RootBonePath = "missing"; renderer.OnValidate();
        Assert.Null(renderer.RootBone);
        Assert.Same(hips.Transform, renderer.Bones![0]); Assert.Same(spine.Transform, renderer.Bones[1]);
        renderer.RootBonePath = null; renderer.OnValidate(); Assert.Null(renderer.RootBone);
        renderer.BonePaths = ["Hips/Spine", "missing"]; renderer.OnValidate();
        Assert.Same(spine.Transform, renderer.Bones![0]); Assert.Null(renderer.Bones[1]);
    }

    [Fact]
    public void ReparentedPaletteRootDoesNotDependOnOptionalRootBone()
    {
        using var host = new GameObject("Host");
        var model = Child("Model", host); var hips = Child("Hips", model);
        var body = Child("Body", model); var renderer = body.AddComponent<SkinnedMeshRenderer>();
        renderer.BonePaths = ["MissingFirstBone", "Hips"]; renderer.RootBonePath = "missing";
        renderer.OnValidate();
        Assert.Null(renderer.RootBone); Assert.Same(hips.Transform, renderer.Bones![1]);
        Assert.Same(model.Transform, renderer.FindSearchRoot());
    }

    [Fact]
    public void ExplicitHierarchyRootAndNoneSurviveEchoRoundTrip()
    {
        using var root = new GameObject("Model"); var hips = Child("Hips", root);
        var body = Child("Body", root); var renderer = body.AddComponent<SkinnedMeshRenderer>();
        renderer.SetBones([hips.Transform], root.Transform);
        Assert.Equal(".", renderer.RootBonePath);
        using var copy = Serializer.Deserialize<GameObject>(Serializer.Serialize(root));
        var restored = copy.Transform.Find("Body")!.GameObject.GetComponent<SkinnedMeshRenderer>();
        Assert.Same(copy.Transform, restored.RootBone);
        Assert.Same(copy.Transform.Find("Hips"), restored.Bones![0]);
        restored.SetBones([copy.Transform.Find("Hips")!], null);
        Assert.Null(restored.RootBonePath); Assert.Null(restored.RootBone);
    }

    [Fact]
    public void MeshPaletteSubsetSupersetAndReorderRebindWithoutValidationAndKeepRootOverride()
    {
        using var root = new GameObject("Model");
        var hips = Child("Hips", root); var spine = Child("Spine", hips);
        var body = Child("Body", root); var r = body.AddComponent<SkinnedMeshRenderer>();
        using var full = Palette(["Hips", "Hips/Spine"]);
        using var small = Palette(["Hips/Spine"]);
        using var reversed = Palette(["Hips/Spine", "Hips"]);
        using var empty = Palette([]);
        r.SharedMesh = full; r.SetBones([hips.Transform, spine.Transform], root.Transform);
        Assert.Equal(2, r.Bones!.Length);
        r.SharedMesh = small;
        Assert.Single(r.Bones!); Assert.Same(spine.Transform, r.Bones![0]);
        Assert.Same(root.Transform, r.RootBone); Assert.Equal(".", r.RootBonePath);
        r.SharedMesh = reversed;
        Assert.Equal(2, r.Bones!.Length); Assert.Same(spine.Transform, r.Bones[0]); Assert.Same(hips.Transform, r.Bones[1]);
        r.SharedMesh = empty; Assert.Empty(r.Bones!); Assert.Same(root.Transform, r.RootBone);
        r.SharedMesh = full; Assert.Equal(2, r.Bones!.Length); Assert.Same(hips.Transform, r.Bones[0]);
        r.SetBones([spine.Transform], null); r.SharedMesh = small; Assert.Null(r.RootBone);
    }

    [Fact]
    public void DifferentRigManualPathsRemainAuthoritativeAndSteadyReadsAllocateNothing()
    {
        using var root = new GameObject("Model");
        var hips = Child("Hips", root); var other = Child("OtherRig", root);
        var body = Child("Body", root); var r = body.AddComponent<SkinnedMeshRenderer>();
        using var mesh = Palette(["OtherRig"]); r.SharedMesh = mesh;
        r.SetBones([hips.Transform], hips.Transform); r.OnValidate();
        Assert.Same(hips.Transform, r.Bones![0]); // an explicit remap is not a palette subset.
        for (int i = 0; i < 500; i++) { _ = r.Bones; _ = r.RootBone; }
        long start = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) { _ = r.Bones; _ = r.RootBone; }
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - start);
    }

    [Fact]
    public void FreshSceneStyleOldPathsUseCompactMeshAndSameCountReplacementInvalidatesMatrices()
    {
        using var root = new GameObject("Model");
        var hips = Child("Hips", root); var spine = Child("Spine", hips);
        var body = Child("Body", root); var r = body.AddComponent<SkinnedMeshRenderer>();
        using var compact = Palette(["Hips/Spine"]); using var replacement = Palette(["Hips/Spine"]);
        r.BonePaths = ["Hips", "Hips/Spine"]; r.RootBonePath = "Hips"; r.SharedMesh = compact;
        r.OnValidate(); Assert.Single(r.Bones!); Assert.Same(spine.Transform, r.Bones![0]);
        var version = typeof(SkinnedMeshRenderer).GetField("_lastSkeletonVersion",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!;
        version.SetValue(r, 1UL); r.SharedMesh = replacement; _ = r.Bones;
        Assert.Equal(ulong.MaxValue, version.GetValue(r));
        Assert.Same(hips.Transform, r.RootBone);
        Assert.Equal(new[] { "Hips", "Hips/Spine" }, r.BonePaths); // derived rebind does not rewrite the scene.
        using var restored = Serializer.Deserialize<GameObject>(Serializer.Serialize(root));
        var loaded = restored.Transform.Find("Body")!.GameObject.GetComponent<SkinnedMeshRenderer>();
        Assert.Single(loaded.Bones!); Assert.Same(restored.Transform.Find("Hips/Spine"), loaded.Bones![0]);
        Assert.Same(restored.Transform.Find("Hips"), loaded.RootBone);
    }

    private static Mesh Palette(string[] names) => new()
    { BoneNames = names, BindPoses = Enumerable.Repeat(Float4x4.Identity, names.Length).ToArray() };

    private static GameObject Child(string name, GameObject parent)
    { var go = new GameObject(name); go.SetParent(parent, false); return go; }
}
