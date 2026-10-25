using System.Text.Json;
using XEngine.Ai.SpineGen;
using Xunit;

namespace XEngine.Ai.Host.Tests;

public sealed class SpineGenActionTests
{
    [Fact]
    public void NormalizedEasingExportsAbsoluteControlsForEveryRuntimeChannel()
    {
        var recipe=SpineGenAssemblyTests.Fixture();
        var (doc,rig)=recipe.Build(new SliceManifest{ImageWidth=20,ImageHeight=20,Recipe=recipe,
            Parts=[new(){Name="sword",X=2,Y=3,Width=4,Height=6}]});
        var curve=JsonSerializer.Deserialize<JsonElement>("[0.25,0,0.75,1]");
        List<MotionKey> Keys(float a,float b)=>[new(){Time=1,Value=a,Curve=curve},new(){Time=3,Value=b}];
        var plan=new MotionPlan{Animation="test",Duration=3,Loop=false,Trusted=true,Bones=[new(){Bone="hand",
            Rotate=Keys(20,100),Translate=Keys(2,12),TranslateY=Keys(-4,4),Scale=Keys(1,2)}]};
        Assert.Empty(MotionValidator.Validate(plan,rig));
        doc.Animations!["test"]=MotionValidator.ToAnimation(plan);
        var data=new global::Spine.SkeletonJson(new SpineGenRigTests.StubLoader()).ReadSkeletonData(new StringReader(SpineExporter.Write(doc)));
        var sk=new global::Spine.Skeleton(data);var animation=data.FindAnimation("test");
        foreach(var (time,fraction) in new[]{(1f,0f),(2f,.5f),(3f,1f)})
        {
            sk.SetupPose();animation.Apply(sk,0,time,false,null,1,global::Spine.MixFrom.Setup,false,false,false);
            var b=sk.FindBone("hand");
            Assert.Equal(90+20+80*fraction,b.Pose.Rotation,2);
            Assert.Equal(30+2+10*fraction,b.Pose.X,2);
            Assert.Equal(40-4+8*fraction,b.Pose.Y,2);
            Assert.Equal(1+fraction,b.Pose.ScaleX,3);Assert.Equal(1+fraction,b.Pose.ScaleY,3);
        }
        // JSON cannot grant itself local-authoring amplitude privileges.
        var untrusted=JsonSerializer.Deserialize<MotionPlan>(JsonSerializer.Serialize(plan))!;
        Assert.False(untrusted.Trusted);
        Assert.Contains(MotionValidator.Validate(untrusted,rig),v=>v.Code=="value-range");
        plan.Bones[0].Rotate![0].Curve=JsonSerializer.Deserialize<JsonElement>("[0.8,0,0.2,1]");
        Assert.Contains(MotionValidator.Validate(plan,rig),v=>v.Code=="curve");
    }

    [Fact]
    public void HiddenAlternateAttachmentCanBeShownAndClearedAtExactFrames()
    {
        var recipe=SpineGenAssemblyTests.Fixture();recipe.Parts[0].Visible=false;
        var (doc,rig)=recipe.Build(new SliceManifest{ImageWidth=20,ImageHeight=20,Recipe=recipe,
            Parts=[new(){Name="sword",X=2,Y=3,Width=4,Height=6}]});
        var plan=new MotionPlan{Animation="switch",Duration=2,Loop=true,Slots=[new(){Slot="sword_slot",
            Attachment=[new(){Time=0,Name=null},new(){Time=.5f,Name="sword"},new(){Time=2,Name=null}]}]};
        Assert.Empty(MotionValidator.Validate(plan,rig,doc));doc.Animations!["switch"]=MotionValidator.ToAnimation(plan);
        var data=new global::Spine.SkeletonJson(new SpineGenRigTests.StubLoader()).ReadSkeletonData(new StringReader(SpineExporter.Write(doc)));
        var sk=new global::Spine.Skeleton(data);Assert.Null(sk.Slots.Items[0].Pose.Attachment);
        foreach(var time in new[]{0f,.5f,1.5f,2f})
        {
            sk.SetupPose();data.FindAnimation("switch").Apply(sk,0,time,false,null,1,global::Spine.MixFrom.Setup,false,false,false);
            Assert.Equal(time>=.5f&&time<2,sk.Slots.Items[0].Pose.Attachment!=null);
        }
        plan.Slots[0].Attachment[1].Name="missing";
        Assert.Contains(MotionValidator.Validate(plan,rig,doc),v=>v.Code=="attachment");
    }
}
