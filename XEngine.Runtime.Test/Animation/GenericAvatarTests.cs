using global::XEngine.Animation;
using global::XEngine.Animation.Humanoid;
using XEngine.Echo;
using XEngine.Runtime.Resources;
using XEngine.Vector;
using Xunit;

namespace XEngine.Runtime.Test.Animation;

public sealed class GenericAvatarTests : RuntimeTestBase
{
    [Fact]
    public void GenericDescriptorCanBeBuiltWithoutHumanoidMappingsOrAnimation()
    {
        var root = CreateGameObject("Model");
        var bone = CreateGameObject("Bip001");
        bone.SetParent(root);
        using var avatar = Avatar.BuildGeneric(root.Transform, "Bip001", "ModelAvatar");
        Assert.Equal(AvatarRigKind.Generic, avatar.RigKind);
        Assert.False(avatar.IsHumanoid);
        Assert.Empty(avatar.Mappings);
        Assert.Null(avatar.Validate());
        Assert.Equal("Bip001", avatar.RootMotionBonePath);
    }

    private GameObject Node(GameObject parent, string name)
    {
        var node = CreateGameObject(name);
        node.SetParent(parent);
        return node;
    }

    [Fact]
    public void SnapshotKeepsModelRootSocketsDuplicateLeavesParentOrderAndIndependentTRS()
    {
        var root = CreateGameObject("Model");
        root.Transform.LocalScale = new Float3(2,3,4);
        var a = Node(root, "A");
        var first = Node(a, "Bip001");
        first.Transform.LocalPosition = new Float3(1,2,3);
        first.Transform.LocalRotation = Quaternion.FromEuler(20,30,40);
        Node(first, "Socket");
        var second = Node(Node(root, "B"), "Bip001");
        using var avatar = Avatar.BuildGeneric(root.Transform, "A/Bip001");
        Assert.Equal([".","A","A/Bip001","A/Bip001/Socket","B","B/Bip001"], avatar.GenericSkeleton.Select(n => n.Path));
        Assert.Equal([-1,0,1,2,0,4], avatar.GenericSkeleton.Select(n => n.ParentIndex));
        Assert.Equal(root.Transform.LocalScale, avatar.GenericSkeleton[0].LocalScale);
        Assert.Equal(first.Transform.LocalRotation, avatar.GenericSkeleton[2].LocalRotation);
        first.Transform.LocalPosition = Float3.Zero;
        second.Transform.LocalScale = Float3.Zero;
        Assert.Equal(new Float3(1,2,3), avatar.GenericSkeleton[2].LocalPosition);
        Assert.Equal(Float3.One, avatar.GenericSkeleton[5].LocalScale);
        Assert.Null(avatar.Validate());
    }

    [Theory]
    [InlineData("")] [InlineData(".")] [InlineData("Bip001")]
    public void DescriptorDistinguishesNoRootModelRootAndChildRoot(string path)
    {
        var root = CreateGameObject("Model"); Node(root,"Bip001");
        using var avatar = Avatar.BuildGeneric(root.Transform, path);
        Assert.Equal(path, avatar.RootMotionBonePath);
        Assert.Null(avatar.Validate());
    }

    [Theory]
    [InlineData("Missing")] [InlineData("/Bip001")] [InlineData("Bip001/")] [InlineData("./Bip001")]
    public void InvalidRootDoesNotResolveByLeafOrFallback(string path)
    {
        var root = CreateGameObject("Model"); Node(Node(root,"A"),"Bip001");
        Assert.Throws<ArgumentException>(() => Avatar.BuildGeneric(root.Transform,path));
    }

    [Fact]
    public void DuplicateSiblingNamesFailInsteadOfChoosingFirst()
    {
        var root = CreateGameObject("Model"); Node(root,"Bip001"); Node(root,"Bip001");
        Assert.Contains("duplicate", Assert.Throws<ArgumentException>(() => Avatar.BuildGeneric(root.Transform)).Message);
    }

    [Theory]
    [InlineData("A/B")] [InlineData("A\\B")] [InlineData(".")] [InlineData("..")]
    public void UnrepresentableNodeNamesFail(string name)
    {
        var root = CreateGameObject("Model"); Node(root,name);
        Assert.Throws<ArgumentException>(() => Avatar.BuildGeneric(root.Transform));
    }

    [Fact]
    public void GenericEchoRoundTripPreservesHierarchyAndType()
    {
        var root = CreateGameObject("Model"); var bone = Node(root,"Bip001");
        bone.Transform.LocalPosition = new Float3(1,2,3);
        bone.Transform.LocalRotation = Quaternion.FromEuler(12,23,34);
        using var avatar = Avatar.BuildGeneric(root.Transform,"Bip001","ModelAvatar");
        using var back = Serializer.Deserialize<Avatar>(Serializer.Serialize(typeof(object),avatar));
        Assert.Equal(AvatarRigKind.Generic, back.RigKind);
        Assert.Null(back.Validate());
        Assert.Empty(back.Mappings);
        Assert.Equal("Bip001",back.RootMotionBonePath);
        Assert.Equal(2,back.GenericSkeleton.Count);
        Assert.Equal(0,back.GenericSkeleton[1].ParentIndex);
        Assert.Equal(bone.Transform.LocalPosition,back.GenericSkeleton[1].LocalPosition);
        Assert.Equal(bone.Transform.LocalRotation,back.GenericSkeleton[1].LocalRotation);
    }

    [Fact]
    public void OldEchoWithoutNewFieldsKeepsHumanoidMappingsAndValidation()
    {
        var root = CreateGameObject("Model"); var hips = Node(root,"Hips");
        Node(Node(Node(hips,"LeftUpperLeg"),"LeftLowerLeg"),"LeftFoot");
        using var old = Avatar.BuildAuto(root.Transform);
        EchoObject echo = Serializer.Serialize(typeof(object),old);
        echo.Remove("RigKind"); echo.Remove("GenericSkeleton");
        using var back = Serializer.Deserialize<Avatar>(echo);
        Assert.True(back.IsHumanoid); Assert.Equal(AvatarRigKind.Humanoid,back.RigKind);
        Assert.Empty(back.GenericSkeleton); Assert.Null(back.Validate());
        Assert.True(back.TryGetPath(HumanoidBone.Hips,out string hipsPath));
        Assert.Equal("Hips",hipsPath); Assert.Equal(old.Mappings.Count,back.Mappings.Count);
    }

    [Fact]
    public void ValidationRejectsBrokenTopologyTRSAndUnknownType()
    {
        var root = CreateGameObject("Model"); Node(Node(root,"A"),"Bone");
        using var avatar = Avatar.BuildGeneric(root.Transform);
        avatar.GenericSkeleton[2].ParentIndex = 0;
        Assert.Contains("parent path mismatch",avatar.Validate());
        avatar.GenericSkeleton[2].ParentIndex = 2;
        Assert.Contains("parent index",avatar.Validate());
        avatar.GenericSkeleton[2].ParentIndex = 1;
        avatar.GenericSkeleton[2].LocalPosition.X = float.NaN;
        Assert.Contains("TRS",avatar.Validate());
        avatar.RigKind = (AvatarRigKind)42;
        Assert.Contains("unknown",avatar.Validate());
    }

    [Fact]
    public void HumanoidAPIsRejectGenericWithoutMutatingClipEvenWithPoisonedMappings()
    {
        var root = CreateGameObject("Model"); Node(root,"Hips");
        using var avatar = Avatar.BuildGeneric(root.Transform);
        avatar.Mappings.Add(new Avatar.BoneMapping { Bone=HumanoidBone.Hips,Path="Hips" });
        Assert.False(avatar.TryGetPath(HumanoidBone.Hips,out _));
        Assert.False(avatar.TryGetMapping(HumanoidBone.Hips,out _));
        Assert.Equal(-1,avatar.IndexOf(HumanoidBone.Hips));
        using var clip = AnimationTestHelpers.MakeSingleBonePosXClip("Move","Hips",endX:1);
        string before = Serializer.Serialize(typeof(object),clip).WriteToString();
        Assert.Throws<ArgumentException>(() => HumanoidClipBaker.Bake(clip,avatar));
        Assert.Throws<ArgumentException>(() => new HumanoidClipPlayable(clip,avatar));
        Assert.Equal(before,Serializer.Serialize(typeof(object),clip).WriteToString());
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void AnimatorKeepsPathCurvesAndDoesNotApplyHumanoidIK(bool muscleClip)
    {
        var scene = CreateScene(enable:true);
        var root = CreateGameObject("Model"); var hips = Node(root,"Hips"); scene.Add(root);
        using var avatar = Avatar.BuildGeneric(root.Transform);
        avatar.Mappings.Add(new Avatar.BoneMapping { Bone=HumanoidBone.Hips,Path="Hips" });
        var animator = new Animator { PlayAutomatically=false };
        root.AddComponent(animator); animator.Avatar = new AssetRef<Avatar>(avatar);
        using var clip = AnimationTestHelpers.MakeSingleBonePosXClip("Move","Hips",endX:1);
        clip.IsHumanoid = muscleClip;
        clip.MuscleCurves.Add(new MuscleCurve { Bone=HumanoidBone.Hips,Channel=3,
            Curve=AnimationTestHelpers.ConstantCurve(100) });
        animator.Play(clip);
        Assert.IsType<AnimationClipPlayable>(animator.GetState(clip));
        int callbacks = 0;
        animator.OnAnimatorIK += _ => { callbacks++; animator.SetBodyPosition(new Float3(200,0,0)); animator.SetBodyPositionWeight(1); };
        Update(scene,30);
        Assert.Equal(30,callbacks);
        Assert.Equal(.5f,hips.Transform.LocalPosition.X,precision:3);
        Assert.Equal(Float3.Zero,root.Transform.LocalPosition);
    }
}
