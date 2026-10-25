using System.Text.Json;
using XEngine.Ai.Host;
using XEngine.Ai.Protocol;
using XEngine.Ai.SpineGen;
using Xunit;

namespace XEngine.Ai.Host.Tests;

public sealed class SpineGenJointTests
{
    [Fact]
    public void HorizontalWeightAxisPinsChestAndMovesArmInRuntime()
    {
        var r=SpineGenAssemblyTests.Fixture();var p=r.Parts[0];
        p.Rigid=false;p.Influences=["root","hand"];p.WeightAxis=[0,0,1,0];
        r.Validate(20,20);
        var (doc,_)=r.Build(new SliceManifest{ImageWidth=20,ImageHeight=20,Recipe=r,
            Parts=[new(){Name="sword",X=2,Y=3,Width=4,Height=6}]});
        var a=AssemblySkinner.Build(p,doc,out _);
        doc.Skins![0].Attachments["sword_slot"]["sword"]=a;
        var data=new global::Spine.SkeletonJson(new SpineGenRigTests.StubLoader()).ReadSkeletonData(new StringReader(SpineExporter.Write(doc)));
        var sk=new global::Spine.Skeleton(data);sk.UpdateWorldTransform(global::Spine.Physics.Update);
        var slot=sk.Slots.Items[0];var mesh=Assert.IsType<global::Spine.MeshAttachment>(slot.Pose.Attachment);
        var before=new float[mesh.WorldVerticesLength];mesh.ComputeWorldVertices(sk,slot,before);
        sk.FindBone("hand").Pose.X+=20;sk.UpdateWorldTransform(global::Spine.Physics.Update);
        var after=new float[mesh.WorldVerticesLength];mesh.ComputeWorldVertices(sk,slot,after);
        for(int i=0;i<a.Uvs!.Length;i+=2)
        {
            if(a.Uvs[i]==0)Assert.Equal(before[i],after[i],3);
            if(a.Uvs[i]==1)Assert.Equal(before[i]+20,after[i],3);
            Assert.Equal(before[i+1],after[i+1],3);
        }
        p.WeightAxis=[.5f,.5f,.5f,.5f];Assert.Throws<ArgumentException>(()=>r.Validate(20,20));
    }

    [Fact]
    public void SmallCropFromLargeMainSourceRemainsValid()
    {
        var recipe=SpineGenAssemblyTests.Fixture();
        recipe.Validate(8192,100);
        recipe.Parts[0].Image="repair.png";recipe.Parts[0].ImageWidth=8192;recipe.Parts[0].ImageHeight=100;
        Assert.Throws<ArgumentException>(()=>recipe.Validate(8192,100));
    }

    [Fact]
    public void InternalExclusionFeatherPreservesRgbAndOriginalAlpha()
    {
        var recipe=SpineGenAssemblyTests.Fixture();var p=recipe.Parts[0];
        p.ExcludePolygons=[[0,0,1,0,1,6,0,6]];p.ExclusionFeather=2;
        var rgba=new byte[20*20*4];
        for(int i=0;i<rgba.Length;i+=4){rgba[i]=90;rgba[i+3]=128;}
        var part=Assert.Single(recipe.Slice(rgba,20,20).Parts);
        Assert.Equal(3,part.Width);Assert.Equal(32,part.Rgba[3]);Assert.Equal(96,part.Rgba[7]);Assert.Equal(128,part.Rgba[11]);
        Assert.Equal(90,part.Rgba[0]);Assert.Equal(90,part.Rgba[4]);
    }

    [Fact]
    public void ExplicitRepairAlphaGainKeepsGradientRatherThanThresholding()
    {
        var recipe=SpineGenAssemblyTests.Fixture();var p=recipe.Parts[0];p.AlphaScale=1.02f;
        var rgba=new byte[20*20*4];
        for(int i=0;i<rgba.Length;i+=4){rgba[i]=90;rgba[i+3]=128;}
        var part=Assert.Single(recipe.Slice(rgba,20,20).Parts);
        Assert.Equal(131,part.Rgba[3]);Assert.Equal(90,part.Rgba[0]);
        p.AlphaScale=float.NaN;Assert.Throws<ArgumentException>(()=>recipe.Validate(20,20));
    }

    [Fact]
    public void JointOnPinnedMeshAreaFollowsAnimatedParentExactly()
    {
        var r=SpineGenAssemblyTests.Fixture();var p=r.Parts[0];
        p.Rigid=false;p.Influences=["hand","root"];p.WeightStart=.5f;
        r.Bones.Add(new(){Name="child",Parent="hand"});
        r.Joints.Add(new(){Bone="child",ParentPart="sword",SourceX=1,SourceY=1});
        var s=new SliceManifest{ImageWidth=20,ImageHeight=20,Recipe=r,Parts=[new(){Name="sword",X=2,Y=3,Width=4,Height=6}]};
        var (doc,_)=r.Build(s);
        var attachment=AssemblySkinner.Build(p,doc,out _);
        doc.Skins![0].Attachments["sword_slot"]["sword"]=attachment;
        var data=new global::Spine.SkeletonJson(new SpineGenRigTests.StubLoader()).ReadSkeletonData(new StringReader(SpineExporter.Write(doc)));
        var sk=new global::Spine.Skeleton(data);
        sk.FindBone("hand").Pose.X+=20;sk.FindBone("hand").Pose.Rotation+=23;
        sk.UpdateWorldTransform(global::Spine.Physics.Update);
        var slot=sk.Slots.Items[0];var mesh=Assert.IsType<global::Spine.MeshAttachment>(slot.Pose.Attachment);
        var vertices=new float[mesh.WorldVerticesLength];mesh.ComputeWorldVertices(sk,slot,vertices);
        int at=-1;
        for(int i=0;i<attachment.Uvs!.Length;i+=2)
            if(attachment.Uvs[i]==.25f && MathF.Abs(attachment.Uvs[i+1]-1f/6)<.00001f){at=i;break;}
        Assert.True(at>=0);var joint=sk.FindBone("child").AppliedPose;
        Assert.Equal(joint.WorldX,vertices[at],3);Assert.Equal(joint.WorldY,vertices[at+1],3);
    }

    [Fact]
    public void SourceLandmarkDefinesJointThroughRuntimeTransformWithoutMutatingRecipe()
    {
        var r=SpineGenAssemblyTests.Fixture();var p=r.Parts[0];
        p.ShearX=15;p.ShearY=-8;
        r.Bones.Add(new(){Name="child",Parent="hand",X=-900,Y=-900});
        r.Joints.Add(new(){Bone="child",ParentPart="sword",SourceX=1,SourceY=4});
        var s=new SliceManifest{ImageWidth=20,ImageHeight=20,Recipe=r,Parts=[new(){Name="sword",X=2,Y=3,Width=4,Height=6}]};
        string before=JsonSerializer.Serialize(r);
        var (doc,_)=r.Build(s);
        Assert.Equal(before,JsonSerializer.Serialize(r));
        var data=new global::Spine.SkeletonJson(new SpineGenRigTests.StubLoader()).ReadSkeletonData(new StringReader(SpineExporter.Write(doc)));
        var sk=new global::Spine.Skeleton(data);sk.UpdateWorldTransform(global::Spine.Physics.Update);
        var part=sk.FindBone("b_sword").AppliedPose;var joint=sk.FindBone("child").AppliedPose;
        float x=1-p.PivotX,y=p.PivotY-4;
        Assert.Equal(part.WorldX+part.A*x+part.B*y,joint.WorldX,3);
        Assert.Equal(part.WorldY+part.C*x+part.D*y,joint.WorldY,3);
        r.Joints.Add(r.Joints[0]);Assert.Throws<ArgumentException>(()=>r.Validate(20,20));
    }

    [Fact]
    public void RasterResizeDoesNotBlendTransparentRgbIntoOpaqueColor()
    {
        byte[] source=[255,0,0,0, 0,100,200,128];
        Assert.Equal(new byte[]{0,100,200,64},SpineGenRaster.Resize(source,2,1,1,1));
    }

    [Fact]
    public void ExternalRepairSourceIsHashedAndUsedBySlice()
    {
        string root=Path.Combine(Path.GetTempPath(),"spine-sources-"+Guid.NewGuid());Directory.CreateDirectory(root);
        try
        {
            File.WriteAllBytes(Path.Combine(root,"sheet.png"),PngRgba.Encode(new byte[20*20*4],20,20));
            var pixels=new byte[8*8*4];for(int i=0;i<pixels.Length;i+=4){pixels[i+1]=123;pixels[i+3]=255;}
            File.WriteAllBytes(Path.Combine(root,"repair.png"),PngRgba.Encode(pixels,8,8));
            var r=SpineGenAssemblyTests.Fixture();r.Parts[0].Image="repair.png";r.Parts[0].ImageWidth=8;r.Parts[0].ImageHeight=8;r.Parts[0].Crop=new(){Width=8,Height=8};
            File.WriteAllText(Path.Combine(root,"recipe.json"),JsonSerializer.Serialize(r));
            var service=new SpineGenSliceService(new ProjectDescriptor("test",root,null,null,null,"fp"));
            var first=service.Slice("sheet.png",null,null,null,null,null,null,null,false,"recipe.json");
            var m=JsonSerializer.Deserialize<SliceManifest>(File.ReadAllText(Path.Combine(root,first.Manifest!)))!;
            Assert.Single(m.SourceHashes);Assert.Equal(64,m.Parts[0].PixelCount);
            byte[] page=PngRgba.Decode(File.ReadAllBytes(Path.Combine(root,first.PagePng!)),out int w,out _);
            var at=m.Parts[0].Atlas;Assert.Equal(123,page[(at.Y*w+at.X)*4+1]);
            pixels[1]=124;File.WriteAllBytes(Path.Combine(root,"repair.png"),PngRgba.Encode(pixels,8,8));
            var changed=service.Slice("sheet.png",null,null,null,null,null,null,null,true,"recipe.json");
            Assert.NotEqual(first.InputHash,changed.InputHash);
            Assert.Throws<ArgumentOutOfRangeException>(()=>service.Slice("sheet.png",null,null,null,int.MaxValue,null,null,null,false,"recipe.json"));
        }
        finally{Directory.Delete(root,true);}
    }
}
