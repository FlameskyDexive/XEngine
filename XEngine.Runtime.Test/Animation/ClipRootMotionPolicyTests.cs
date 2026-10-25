using XEngine.Echo;
using XEngine.Animation;
using XEngine.Runtime.Resources;
using XEngine.Vector;
using Xunit;

namespace XEngine.Runtime.Test.Animation;

public sealed class ClipRootMotionPolicyTests : RuntimeTestBase
{
    [Fact]
    public void ClipHasExplicitSerializableRootMotionPolicy()
    {
        using var clip = new AnimationClip();
        var policy = typeof(AnimationClip).GetProperty("RootMotionPolicy");
        Assert.NotNull(policy);
        Assert.Equal("LegacyAutomatic", policy.GetValue(clip)!.ToString());
        policy.SetValue(clip, Enum.Parse(policy.PropertyType, "Disabled"));
        using var back = Serializer.Deserialize<AnimationClip>(Serializer.Serialize(typeof(object), clip));
        Assert.Equal("Disabled", policy.GetValue(back)!.ToString());
    }

    [Theory]
    [InlineData(0)] [InlineData(1)] [InlineData(2)] [InlineData(77)]
    public void EchoPreservesPolicyAndPathWithoutChangingLegacyOutput(int value)
    {
        using var clip = Motion("Move", "Carrier", 2);
        clip.RootMotionPolicy = (ClipRootMotionPolicy)value;
        clip.RootBonePath = "Carrier";
        EchoObject echo = Serializer.Serialize(typeof(object), clip);
        Assert.Equal(value != 0, echo.TryGet("RootMotionPolicy", out _));
        using var back = Serializer.Deserialize<AnimationClip>(echo);
        Assert.Equal(clip.RootMotionPolicy, back.RootMotionPolicy);
        Assert.Equal(clip.RootBonePath, back.RootBonePath);
        Assert.Equal(echo.WriteToString(), Serializer.Serialize(typeof(object), back).WriteToString());
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void DisabledDoesNotExtractOrPinAnyAnimatedBone(bool extract)
    {
        var (animator, carrier, marker) = Rig(extract);
        using var clip = Motion("Move", "Carrier", 2);
        clip.AddBone(Bone("Root", 4));
        clip.RootBonePath = "Root"; // Poisoned legacy hint must be ignored.
        clip.RootMotionPolicy = ClipRootMotionPolicy.Disabled;
        animator.Play(clip);
        Tick(animator, .5f);
        Near(Float3.Zero, animator.Transform.LocalPosition);
        Near(new Float3(1,0,0), carrier.LocalPosition);
        Near(new Float3(2,0,0), marker.LocalPosition);
    }

    [Theory]
    [InlineData(false, null)] [InlineData(true, null)]
    [InlineData(false, "")] [InlineData(true, "")]
    [InlineData(false, "Missing")] [InlineData(true, "Missing")]
    [InlineData(false, "Still")] [InlineData(true, "Still")]
    [InlineData(false, ".")] [InlineData(true, ".")]
    public void ExplicitUnanimatedOrUnboundRootNeverFallsBack(bool extract, string? root)
    {
        var (animator, carrier, marker) = Rig(extract);
        Child(animator.GameObject, "Still");
        animator.RebuildGraph();
        using var clip = Motion("Move", "Carrier", 2);
        clip.AddBone(Bone("Root", 4));
        clip.RootMotionPolicy = ClipRootMotionPolicy.ExplicitBone;
        clip.RootBonePath = root;
        animator.Play(clip);
        Tick(animator, .5f);
        Near(Float3.Zero, animator.Transform.LocalPosition);
        Near(new Float3(1,0,0), carrier.LocalPosition);
        Near(new Float3(2,0,0), marker.LocalPosition);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void ExactExplicitRootDoesNotSelectSiblingMarkerOrOtherBones(bool extract)
    {
        var (animator, carrier, marker) = Rig(extract);
        using var clip = Motion("Step", "Carrier", .2f);
        clip.AddBone(Bone("Root", 8));
        clip.RootMotionPolicy = ClipRootMotionPolicy.ExplicitBone;
        clip.RootBonePath = "Carrier";
        animator.Play(clip);
        Tick(animator, .5f);
        Near(new Float3(extract ? .1f : 0,0,0), animator.Transform.LocalPosition);
        Near(Float3.Zero, carrier.LocalPosition);
        Near(new Float3(4,0,0), marker.LocalPosition);
    }

    [Fact]
    public void ExactSiblingMarkerDoesNotPinVisualCarrierForNewPolicy()
    {
        var (animator, carrier, marker) = Rig(true);
        using var clip = Motion("Move", "Carrier", 2);
        clip.AddBone(Bone("Root", 4));
        clip.RootMotionPolicy = ClipRootMotionPolicy.ExplicitBone;
        clip.RootBonePath = "Root";
        animator.Play(clip);
        Tick(animator, .5f);
        Near(new Float3(2,0,0), animator.Transform.LocalPosition);
        Near(new Float3(1,0,0), carrier.LocalPosition);
        Near(Float3.Zero, marker.LocalPosition);
    }

    [Theory]
    [InlineData("Dup", 0, 1, 2)] [InlineData("B/Dup", 2, 1, 0)]
    public void DuplicateLeavesRequireFullPath(string path, float bodyX, float firstX, float secondX)
    {
        var (animator, _, _) = Rig(true);
        var first = Child(Child(animator.GameObject, "A"), "Dup");
        var second = Child(Child(animator.GameObject, "B"), "Dup");
        animator.RebuildGraph();
        using var clip = Motion("Move", "A/Dup", 2);
        clip.AddBone(Bone("B/Dup", 4));
        clip.RootMotionPolicy = ClipRootMotionPolicy.ExplicitBone;
        clip.RootBonePath = path;
        animator.Play(clip);
        Tick(animator, .5f);
        Near(new Float3(bodyX,0,0), animator.Transform.LocalPosition);
        Near(new Float3(firstX,0,0), first.Transform.LocalPosition);
        Near(new Float3(secondX,0,0), second.Transform.LocalPosition);
    }

    [Theory]
    [InlineData(false, false)] [InlineData(true, false)]
    [InlineData(false, true)] [InlineData(true, true)]
    public void MixedStatesOnlyPinTheirOwnMotionContribution(bool extract, bool legacyOutgoing)
    {
        var (animator, carrier, _) = Rig(extract);
        using var outgoing = Motion("Outgoing", "Carrier", 2);
        outgoing.RootBonePath = "Carrier";
        outgoing.RootMotionPolicy = legacyOutgoing ? ClipRootMotionPolicy.LegacyAutomatic : ClipRootMotionPolicy.ExplicitBone;
        using var incoming = Motion("Incoming", "Carrier", 4);
        incoming.RootMotionPolicy = ClipRootMotionPolicy.Disabled;
        var old = animator.Play(outgoing);
        var next = animator.Play(incoming);
        old.Weight = .5f; next.Weight = .5f;
        Tick(animator, .5f);
        Near(new Float3(extract ? .5f : 0,0,0), animator.Transform.LocalPosition);
        Near(new Float3(1,0,0), carrier.LocalPosition); // incoming's 2m sample * its .5 weight
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void NestedBlendTreePreservesDisabledContribution(bool extract)
    {
        var (animator, carrier, _) = Rig(extract);
        using var explicitClip = Motion("Step", "Carrier", 2);
        explicitClip.RootMotionPolicy = ClipRootMotionPolicy.ExplicitBone;
        explicitClip.RootBonePath = "Carrier";
        using var disabledClip = Motion("Move", "Carrier", 4);
        disabledClip.RootMotionPolicy = ClipRootMotionPolicy.Disabled;
        var tree = new BlendTreePlayable { ParameterValue = .75f };
        tree.Bind(animator.Skeleton!);
        var a = new AnimationClipPlayable(explicitClip); a.Bind(animator.Skeleton!); a.Play();
        var b = new AnimationClipPlayable(disabledClip); b.Bind(animator.Skeleton!); b.Play();
        tree.AddChild(a, 0); tree.AddChild(b, 1);
        animator.PlayPlayable(tree);
        Tick(animator, .5f);
        Near(new Float3(extract ? .25f : 0,0,0), animator.Transform.LocalPosition);
        Near(new Float3(1.5f,0,0), carrier.LocalPosition);
    }

    [Fact]
    public void RotationIsPinnedBeforeMixingRatherThanErasingDisabledState()
    {
        var (animator, carrier, _) = Rig(true);
        animator.ApplyRootMotionRotation = true;
        using var a = AnimationTestHelpers.MakeSingleBoneRotClip("TurnA","Carrier",Quaternion.Identity,Quaternion.FromEuler(0,90,0),1);
        using var b = AnimationTestHelpers.MakeSingleBoneRotClip("TurnB","Carrier",Quaternion.Identity,Quaternion.FromEuler(0,90,0),1);
        a.RootMotionPolicy = ClipRootMotionPolicy.ExplicitBone; a.RootBonePath = "Carrier";
        b.RootMotionPolicy = ClipRootMotionPolicy.Disabled;
        var sa = animator.Play(a); var sb = animator.Play(b);
        sa.Weight = .5f; sb.Weight = .5f;
        Tick(animator, 1);
        Assert.True(MathF.Abs(Quaternion.Dot(Quaternion.FromEuler(0,45,0), carrier.LocalRotation)) > .9999f);
        Assert.True(MathF.Abs(Quaternion.Dot(Quaternion.FromEuler(0,45,0), animator.Transform.LocalRotation)) > .9999f);
    }

    [Fact]
    public void ExplicitRebindClearsPreviousRootPolicyAndPinDescriptors()
    {
        var (animator, carrier, _) = Rig(true);
        using var clip = Motion("Move", "Carrier", 2);
        clip.RootMotionPolicy = ClipRootMotionPolicy.ExplicitBone; clip.RootBonePath = "Carrier";
        var state = animator.Play(clip);
        Tick(animator,.25f);
        Near(new Float3(.5f,0,0),animator.Transform.LocalPosition);
        clip.RootMotionPolicy = ClipRootMotionPolicy.Disabled;
        state.Bind(animator.Skeleton!);
        Tick(animator,.25f);
        Near(new Float3(.5f,0,0),animator.Transform.LocalPosition);
        Near(new Float3(1,0,0),carrier.LocalPosition);
    }

    [Fact]
    public void UnknownPolicyIsRetainedButNeverEnablesHeuristics()
    {
        var (animator, carrier, _) = Rig(true);
        using var clip=Motion("Unknown","Carrier",2);
        clip.RootMotionPolicy=(ClipRootMotionPolicy)77; animator.Play(clip); Tick(animator,.5f);
        Near(Float3.Zero,animator.Transform.LocalPosition); Near(new Float3(1,0,0),carrier.LocalPosition);
    }

    [Fact]
    public void DisabledOverrideLayerKeepsItsOwnTranslationAfterBaseExtraction()
    {
        var (animator,carrier,_)=Rig(true);
        using var a=Motion("Base","Carrier",2);
        a.RootMotionPolicy=ClipRootMotionPolicy.ExplicitBone; a.RootBonePath="Carrier";
        using var b=Motion("Overlay","Carrier",4); b.RootMotionPolicy=ClipRootMotionPolicy.Disabled;
        animator.Play(a);
        var overlay=animator.AddLayer(null,AnimatorLayerBlendingMode.Override,.5f);
        animator.PlayClipOnMixer(b,0,overlay);
        Tick(animator,.5f);
        Near(new Float3(1,0,0),animator.Transform.LocalPosition);
        Near(new Float3(1,0,0),carrier.LocalPosition);
    }

    [Fact]
    public void ExplicitInPlacePinDoesNotFreezeUnskinnedProps()
    {
        Scene scene=CreateScene(enable:true);
        var actor=CreateGameObject("Prop"); var bone=Child(actor,"Carrier"); scene.Add(actor);
        var animator=new Animator {PlayAutomatically=false,ApplyRootMotion=false}; actor.AddComponent(animator);
        using var clip=Motion("Slide","Carrier",2);
        clip.RootMotionPolicy=ClipRootMotionPolicy.ExplicitBone; clip.RootBonePath="Carrier";
        animator.Play(clip); Tick(animator,.5f);
        Near(Float3.Zero,actor.Transform.LocalPosition); Near(new Float3(1,0,0),bone.Transform.LocalPosition);
    }

    [Theory]
    [InlineData(15)] [InlineData(30)] [InlineData(60)] [InlineData(120)]
    public void ExplicitPlanarRootRetainsVerticalPoseUnderRotatedScaledActor(int fps)
    {
        var (animator,carrier,_)=Rig(true);
        animator.RootMotionPlanar=true;
        animator.Transform.LocalRotation=Quaternion.FromEuler(0,90,0);
        animator.Transform.LocalScale=new Float3(2,2,2);
        using var clip=Motion("Planar","Carrier",2);
        clip.Bones[0].PosY=AnimationTestHelpers.LinearCurve(0,2,1);
        clip.RootMotionPolicy=ClipRootMotionPolicy.ExplicitBone;clip.RootBonePath="Carrier";
        animator.Play(clip);
        for(int i=0;i<fps;i++)Tick(animator,1f/fps);
        Near(new Float3(0,2,0),carrier.LocalPosition);
        Near(animator.Transform.TransformVector(new Float3(2,0,0)),animator.Transform.Position);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void StableConfiguredPlaybackAllocatesZeroBytes(bool extract)
    {
        var (animator, _, _) = Rig(extract);
        using var clip=Motion("Loop","Carrier",2);
        clip.RootMotionPolicy=ClipRootMotionPolicy.ExplicitBone; clip.RootBonePath="Carrier";
        animator.Play(clip).Wrap=AnimationWrapMode.Loop;
        Time.TimeStack.Push(new TimeData {DeltaTime=1f/60});
        try
        {
            for(int i=0;i<2048;i++)animator.LateUpdate();
            long before=GC.GetAllocatedBytesForCurrentThread();
            for(int i=0;i<10000;i++)animator.LateUpdate();
            Assert.Equal(0,GC.GetAllocatedBytesForCurrentThread()-before);
        }
        finally {Time.TimeStack.Pop();}
    }

    private (Animator animator, Transform carrier, Transform marker) Rig(bool extract)
    {
        Scene scene=CreateScene(enable:true);
        GameObject actor=CreateGameObject("Actor");
        GameObject carrier=Child(actor,"Carrier"); GameObject marker=Child(actor,"Root");
        carrier.AddComponent<SkinnedMeshRenderer>().RootBonePath="Carrier";
        scene.Add(actor);
        var animator=new Animator {PlayAutomatically=false,ApplyRootMotion=extract,RootMotionPlanar=false,
            ApplyRootMotionRotation=false,Wrap=AnimationWrapMode.Once};
        actor.AddComponent(animator);
        return (animator,carrier.Transform,marker.Transform);
    }

    private GameObject Child(GameObject parent,string name)
    { var child=CreateGameObject(name); child.SetParent(parent,worldPositionStays:false); return child; }
    private static AnimationClip Motion(string name,string path,float end)
    {var clip=new AnimationClip {Name=name,Duration=1};clip.AddBone(Bone(path,end));return clip;}
    private static AnimationClip.AnimBone Bone(string path,float end)=>new()
    {BoneName=path,PosX=AnimationTestHelpers.LinearCurve(0,end,1),PosY=AnimationTestHelpers.ConstantCurve(0),PosZ=AnimationTestHelpers.ConstantCurve(0)};
    private static void Tick(Animator animator,float dt)
    {Time.TimeStack.Push(new TimeData {DeltaTime=dt});try {animator.LateUpdate();} finally {Time.TimeStack.Pop();}}
    private static void Near(Float3 expected,Float3 actual)=>Assert.True(Float3.Length(expected-actual)<2e-4f,$"expected {expected}; actual {actual}");
}
