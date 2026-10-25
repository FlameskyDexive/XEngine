// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Vector;

using Xunit;

namespace XEngine.Runtime.Test;

/// <summary>Headless Bone2D / SpriteSkin / SpriteLibrary (CPU deform via BatchMath, no GPU).</summary>
public sealed class SpriteSkinTests : RuntimeTestBase
{
    [Fact]
    public void SpriteSkin_Deform_FollowsTranslatedBone()
    {
        GameObject root = CreateGameObject("Skin");
        GameObject boneGo = CreateGameObject("Bone");
        boneGo.SetParent(root, worldPositionStays: false);
        var bone = boneGo.AddComponent<Bone2D>();
        var skin = root.AddComponent<SpriteSkin>();
        skin.Bones = [bone];
        skin.Vertices = [Float3.Zero];
        skin.Weights = [new Float4(1f, 0f, 0f, 0f)];
        skin.BoneIndices = [new UInt4(0, 0, 0, 0)];
        skin.CaptureBindPoses();

        boneGo.Transform.LocalPosition = new Float3(10f, 0f, 0f);
        var dest = new Float3[1];
        skin.Deform(dest);

        Assert.Equal(10f, dest[0].X, 2);
        Assert.Equal(0f, dest[0].Y, 2);
    }

    [Fact]
    public void SpriteLibrary_Get_ReturnsCategoryLabel()
    {
        var library = new SpriteLibrary();
        library.Add("Idle", "Default", "hero_idle");

        Assert.Equal("hero_idle", library.Get("Idle", "Default"));
        Assert.Null(library.Get("Idle", "Missing"));
    }

    [Fact]
    public void SpriteResolver_ResolvedId_ReadsLibrary()
    {
        var library = new SpriteLibrary();
        library.Add("Idle", "Walk", "hero_walk");
        GameObject go = CreateGameObject("Actor");
        var resolver = go.AddComponent<SpriteResolver>();
        resolver.Library = library;
        resolver.Category = "Idle";
        resolver.Label = "Walk";

        Assert.Equal("hero_walk", resolver.ResolvedId);
    }

    [Fact]
    public void IKSolver2D_CCD_ReachesTarget()
    {
        Bone2D root = CreateArm();
        Bone2D tip = root.GameObject.Children[0].GetComponent<Bone2D>()!;

        IKSolver2D.SolveCCD([root, tip], new Float2(0f, 2f), iterations: 24);

        Float3 worldTip = tip.Transform.TransformPoint(new Float3(tip.Length, 0f, 0f));
        Assert.True(Float2.Distance(worldTip.XY, new Float2(0f, 2f)) < 0.15f,
            $"CCD tip=({worldTip.X},{worldTip.Y})");
    }

    [Fact]
    public void IKSolver2D_FABRIK_ReachesTarget()
    {
        Bone2D root = CreateArm();
        Bone2D tip = root.GameObject.Children[0].GetComponent<Bone2D>()!;

        IKSolver2D.SolveFABRIK([root, tip], new Float2(0f, 2f), iterations: 24);

        Float3 worldTip = tip.Transform.TransformPoint(new Float3(tip.Length, 0f, 0f));
        Assert.True(Float2.Distance(worldTip.XY, new Float2(0f, 2f)) < 0.15f,
            $"FABRIK tip=({worldTip.X},{worldTip.Y})");
    }

    private Bone2D CreateArm()
    {
        GameObject rootGo = CreateGameObject("RootBone");
        rootGo.Transform.Position = Float3.Zero;
        var root = rootGo.AddComponent<Bone2D>();
        root.Length = 1f;

        GameObject tipGo = CreateGameObject("TipBone");
        tipGo.SetParent(rootGo, worldPositionStays: false);
        tipGo.Transform.LocalPosition = new Float3(1f, 0f, 0f);
        var tip = tipGo.AddComponent<Bone2D>();
        tip.Length = 1f;
        return root;
    }
}
