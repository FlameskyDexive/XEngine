// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Xunit;

using global::XEngine.Animation;
using global::XEngine.Animation.Humanoid;
using XEngine.Echo;
using XEngine.Runtime.Resources;

namespace XEngine.Runtime.Test.Animation;

/// <summary>
/// M53 — humanoid Avatar asset: hierarchy-aware auto-mapping across rig naming conventions
/// (canonical / Mixamo / Bip001 / root-trap), T-pose capture, validation, and Echo round-trip.
/// </summary>
public class AvatarTests : RuntimeTestBase
{
    private GameObject Node(GameObject parent, string name, float x = 0f)
    {
        var go = CreateGameObject(name);
        go.SetParent(parent);
        go.Transform.LocalPosition = new XEngine.Vector.Float3(x, 0f, 0f);
        return go;
    }

    /// <summary>Creates a chain of children under <paramref name="parent"/>; returns the deepest.</summary>
    private GameObject Chain(GameObject parent, params string[] names)
    {
        GameObject current = parent;
        for (int i = 0; i < names.Length; i++)
            current = Node(current, names[i]);
        return current;
    }

    /// <summary>Canonical Unity-style rig with an extra scene Root above the armature.</summary>
    private GameObject CanonicalRig()
    {
        var root = CreateGameObject("Root");
        var hips = Node(root, "Hips");
        var spine = Node(hips, "Spine");
        var chest = Node(spine, "Chest");
        var neck = Node(chest, "Neck");
        Node(neck, "Head");
        Chain(chest, "LeftShoulder", "LeftUpperArm", "LeftLowerArm", "LeftHand");
        Chain(chest, "RightShoulder", "RightUpperArm", "RightLowerArm", "RightHand");
        Chain(hips, "LeftUpperLeg", "LeftLowerLeg", "LeftFoot");
        Chain(hips, "RightUpperLeg", "RightLowerLeg", "RightFoot");
        return root;
    }

    [Fact]
    public void AutoMap_CanonicalRig_ResolvesAllRoles()
    {
        var root = CanonicalRig();
        var avatar = Avatar.BuildAuto(root.Transform);

        Assert.Null(avatar.Validate());
        Assert.True(avatar.TryGetPath(HumanoidBone.Hips, out string hips));
        Assert.Equal("Hips", hips);
        Assert.True(avatar.TryGetPath(HumanoidBone.Spine, out string spine));
        Assert.Equal("Hips/Spine", spine);
        Assert.True(avatar.TryGetPath(HumanoidBone.Head, out string head));
        Assert.Equal("Hips/Spine/Chest/Neck/Head", head);
        Assert.True(avatar.TryGetPath(HumanoidBone.LeftUpperArm, out string lua));
        Assert.Equal("Hips/Spine/Chest/LeftShoulder/LeftUpperArm", lua);
        Assert.True(avatar.TryGetPath(HumanoidBone.LeftHand, out string lh));
        Assert.Equal("Hips/Spine/Chest/LeftShoulder/LeftUpperArm/LeftLowerArm/LeftHand", lh);
        Assert.True(avatar.TryGetPath(HumanoidBone.RightFoot, out string rf));
        Assert.Equal("Hips/RightUpperLeg/RightLowerLeg/RightFoot", rf);
        Assert.Equal("Hips", avatar.RootMotionBonePath);
    }

    [Fact]
    public void AutoMap_MixamoPrefixNames_Resolve()
    {
        var wrapper = CreateGameObject("Armature");
        var root = Node(wrapper, "mixamorig:Hips");
        var spine = Node(root, "mixamorig:Spine");
        var spine1 = Node(spine, "mixamorig:Spine1");
        var spine2 = Node(spine1, "mixamorig:Spine2");
        Node(Node(spine2, "mixamorig:Neck"), "mixamorig:Head");
        Node(Node(Node(Node(spine2, "mixamorig:LeftShoulder"), "mixamorig:LeftArm"), "mixamorig:LeftForeArm"), "mixamorig:LeftHand");
        Node(Node(Node(Node(spine2, "mixamorig:RightShoulder"), "mixamorig:RightArm"), "mixamorig:RightForeArm"), "mixamorig:RightHand");
        Node(Node(Node(root, "mixamorig:LeftUpLeg"), "mixamorig:LeftLeg"), "mixamorig:LeftFoot");
        Node(Node(Node(root, "mixamorig:RightUpLeg"), "mixamorig:RightLeg"), "mixamorig:RightFoot");

        var avatar = Avatar.BuildAuto(wrapper.Transform);

        Assert.Null(avatar.Validate());
        Assert.True(avatar.TryGetPath(HumanoidBone.Hips, out string hips));
        Assert.Equal("mixamorig:Hips", hips);
        Assert.True(avatar.TryGetPath(HumanoidBone.Chest, out string chest));
        Assert.EndsWith("mixamorig:Spine2", chest); // spine2 alias = Chest slot.
        Assert.True(avatar.TryGetPath(HumanoidBone.LeftLowerArm, out string fore));
        Assert.EndsWith("mixamorig:LeftForeArm", fore);
        Assert.True(avatar.TryGetPath(HumanoidBone.LeftUpperLeg, out string upleg));
        Assert.EndsWith("mixamorig:LeftUpLeg", upleg);
    }

    [Fact]
    public void AutoMap_Bip001Style_Resolve()
    {
        var wrapper = CreateGameObject("Armature");
        var root = Node(wrapper, "Bip001");
        var hips = Node(root, "Bip001 Pelvis");
        var spine = Node(hips, "Bip001 Spine");
        var spine1 = Node(spine, "Bip001 Spine1");
        Node(Node(spine1, "Bip001 Neck"), "Bip001 Head");
        Node(Node(Node(Node(spine1, "Bip001 L Clavicle"), "Bip001 L UpperArm"), "Bip001 L Forearm"), "Bip001 L Hand");
        Node(Node(Node(hips, "Bip001 L Thigh"), "Bip001 L Calf"), "Bip001 L Foot");

        var avatar = Avatar.BuildAuto(wrapper.Transform);

        Assert.True(avatar.TryGetPath(HumanoidBone.Hips, out string hipsPath));
        Assert.Equal("Bip001/Bip001 Pelvis", hipsPath);
        Assert.True(avatar.TryGetPath(HumanoidBone.Chest, out string chest));
        Assert.EndsWith("Bip001 Spine1", chest);
        Assert.True(avatar.TryGetPath(HumanoidBone.LeftUpperArm, out _));
        Assert.True(avatar.TryGetPath(HumanoidBone.LeftLowerLeg, out _));
    }

    [Fact]
    public void AutoMap_SceneRootTrap_DoesNotMapRootAsHips()
    {
        // A scene wrapper named "root" with no spine child must not steal the Hips role.
        var wrapper = CreateGameObject("root");
        var realHips = Node(wrapper, "pelvis");
        Node(Node(Node(realHips, "spine"), "neck"), "head");
        Node(Node(Node(realHips, "leftupleg"), "leftleg"), "leftfoot");
        Node(Node(Node(realHips, "rightupleg"), "rightleg"), "rightfoot");

        var avatar = Avatar.BuildAuto(wrapper.Transform);

        Assert.True(avatar.TryGetPath(HumanoidBone.Hips, out string hips));
        Assert.Equal("pelvis", hips);
    }

    [Fact]
    public void Validate_NoLimbChains_ReportsProblems()
    {
        var root = CreateGameObject("Armature");
        Node(Node(root, "spine"), "head"); // Hips never resolves.

        var avatar = Avatar.BuildAuto(root.Transform);

        Assert.NotNull(avatar.Validate());
    }

    [Fact]
    public void AutoMap_CapturesTPoseSnapshot()
    {
        var root = CanonicalRig();
        var avatar = Avatar.BuildAuto(root.Transform);

        int hips = avatar.IndexOf(HumanoidBone.Hips);
        Assert.True(hips >= 0);
        Assert.Equal(1f, avatar.Mappings[hips].LocalScale.X, precision: 3);
    }

    [Fact]
    public void Echo_RoundTrip_KeepsMappingsAndTPose()
    {
        var root = CanonicalRig();
        var avatar = Avatar.BuildAuto(root.Transform);
        avatar.LegLength = 1.75f;

        EchoObject echo = Serializer.Serialize(typeof(object), avatar);
        var back = Serializer.Deserialize<Avatar>(echo);

        Assert.NotNull(back);
        Assert.Equal(avatar.Mappings.Count, back!.Mappings.Count);
        Assert.True(back.TryGetPath(HumanoidBone.Hips, out string hips));
        Assert.Equal("Hips", hips);
        Assert.Equal(1.75f, back.LegLength, precision: 3);
        int idx = back.IndexOf(HumanoidBone.Spine);
        Assert.Equal(avatar.Mappings[idx].LocalPosition, back.Mappings[idx].LocalPosition);
    }
}
