using System.Text.Json;
using XEngine.Ai.Host;
using XEngine.Ai.Protocol;
using XEngine.Ai.SpineGen;
using Xunit;

namespace XEngine.Ai.Host.Tests;

public sealed class SpineGenAssemblyTests
{
    internal static AssemblyRecipe Fixture() => new()
    {
        Bones = [new() {Name="root"},new(){Name="hand",Parent="root",X=30,Y=40,Rotation=90}],
        Parts = [new(){Name="sword",Crop=new(){X=2,Y=3,Width=4,Height=6},Bone="hand",PivotX=2,PivotY=3,X=7,Y=8,Rotation=30,ScaleX=2,ScaleY=.5f}]
    };

    [Fact]
    public void ExplicitRigUsesLocalAssemblyInsteadOfSheetPosition()
    {
        var recipe=Fixture();
        var slice=new SliceManifest {ImageWidth=20,ImageHeight=20,Recipe=recipe,
            Parts=[new(){Name="sword",X=2,Y=3,Width=4,Height=6}]};
        var (doc,rig)=Rigger.Build(slice);
        var data=new global::Spine.SkeletonJson(new SpineGenRigTests.StubLoader()).ReadSkeletonData(new StringReader(SpineExporter.Write(doc)));
        var skeleton=new global::Spine.Skeleton(data);
        skeleton.UpdateWorldTransform(global::Spine.Physics.Update);
        var b=skeleton.FindBone("b_sword");
        Assert.Equal(22,b.AppliedPose.WorldX,3); Assert.Equal(47,b.AppliedPose.WorldY,3);
        Assert.Equal("hand",b.Parent.Data.Name);
        Assert.Equal(RigRole.Prop,Assert.Single(rig.Parts).Role);
        Assert.Equal("4.3.74",data.Version);
    }

    [Fact]
    public void SlicePreservesAlphaAndSeparatesPaddingFromContent()
    {
        string root=Path.Combine(Path.GetTempPath(),"spine-recipe-"+Guid.NewGuid());
        Directory.CreateDirectory(root);
        try
        {
            var rgba=new byte[20*20*4];
            for(int i=0;i<rgba.Length;i+=4){rgba[i]=120;rgba[i+1]=80;rgba[i+3]=73;}
            File.WriteAllBytes(Path.Combine(root,"input.png"),PngRgba.Encode(rgba,20,20));
            File.WriteAllText(Path.Combine(root,"recipe.json"),JsonSerializer.Serialize(Fixture()));
            var service=new SpineGenSliceService(new ProjectDescriptor("test",root,null,null,null,"fp"));
            var result=service.Slice("input.png",null,null,null,3,null,null,null,false,"recipe.json");
            var again=service.Slice("input.png",null,null,null,3,null,null,null,false,"recipe.json");
            Assert.Equal(result.InputHash,again.InputHash);
            var manifest=JsonSerializer.Deserialize<SliceManifest>(File.ReadAllText(Path.Combine(root,result.Manifest)))!;
            var p=Assert.Single(manifest.Parts);
            Assert.Equal(4,p.Atlas.Width);Assert.Equal(6,p.Atlas.Height);
            var page=PngRgba.Decode(File.ReadAllBytes(Path.Combine(root,result.PagePng)),out int w,out _);
            Assert.Equal(34,page[(p.Atlas.Y*w+p.Atlas.X)*4]);
            Assert.Contains("pma: true",File.ReadAllText(Path.Combine(root,result.Atlas)));
            Assert.Equal(73,page[(p.Atlas.Y*w+p.Atlas.X)*4+3]);
            Assert.Equal(73,page[(p.Atlas.Y*w+p.Atlas.X-1)*4+3]); // one pixel extrusion
            Assert.Equal(34,page[(p.Atlas.Y*w+p.Atlas.X-1)*4]); // PMA, not straight RGB
            Assert.Equal(0,page[(p.Atlas.Y*w+p.Atlas.X-2)*4+3]); // remaining padding
            var recipe=Fixture();recipe.Parts[0].Rotation=45;
            File.WriteAllText(Path.Combine(root,"recipe.json"),JsonSerializer.Serialize(recipe));
            var changed=service.Slice("input.png",null,null,null,3,null,null,null,true,"recipe.json");
            Assert.NotEqual(result.InputHash,changed.InputHash);
        }
        finally {Directory.Delete(root,true);}
    }

    [Theory]
    [InlineData("duplicate")][InlineData("parent")][InlineData("cycle")][InlineData("nan")][InlineData("crop")][InlineData("unknown")]
    public void InvalidRecipesFailBeforeWriting(string issue)
    {
        var r=Fixture();
        switch(issue)
        {
            case "duplicate":r.Parts.Add(r.Parts[0]);break;
            case "parent":r.Bones[1].Parent="absent";break;
            case "cycle":r.Bones[0].Parent="hand";break;
            case "nan":r.Parts[0].Rotation=float.NaN;break;
            case "crop":r.Parts[0].Crop.Width=30;break;
            case "unknown":r.Parts[0].Bone="absent";break;
        }
        Assert.ThrowsAny<Exception>(()=>r.Validate(20,20));
    }

    [Fact]
    public void WeightedGridMatchesRegionThroughRotatedScaledParentChain()
    {
        var recipe=Fixture(); var p=recipe.Parts[0]; p.Rigid=false;p.Influences=["root","hand"];
        var slice=new SliceManifest {ImageWidth=20,ImageHeight=20,Recipe=recipe,
            Parts=[new(){Name="sword",X=2,Y=3,Width=4,Height=6}]};
        var (doc,_)=Rigger.Build(slice);
        var reader=new global::Spine.SkeletonJson(new SpineGenRigTests.StubLoader());
        var original=new global::Spine.Skeleton(reader.ReadSkeletonData(new StringReader(SpineExporter.Write(doc))));
        original.UpdateWorldTransform(global::Spine.Physics.Update);
        var slot=original.Slots.Items[0];var region=Assert.IsType<global::Spine.RegionAttachment>(slot.Pose.Attachment);
        var corners=new float[8];region.ComputeWorldVertices(slot,region.GetOffsets(slot.Pose),corners,0);
        var attachment=AssemblySkinner.Build(p,doc,out var report);
        doc.Skins![0].Attachments["sword_slot"]["sword"]=attachment;
        var meshed=new global::Spine.Skeleton(reader.ReadSkeletonData(new StringReader(SpineExporter.Write(doc))));
        meshed.UpdateWorldTransform(global::Spine.Physics.Update);
        var meshSlot=meshed.Slots.Items[0];var mesh=Assert.IsType<global::Spine.MeshAttachment>(meshSlot.Pose.Attachment);
        var vertices=new float[mesh.WorldVerticesLength];mesh.ComputeWorldVertices(meshed,meshSlot,vertices);
        foreach(var uv in new[]{(0f,0f),(1f,0f),(1f,1f),(0f,1f)})
        {
            int index=-1;
            for(int i=0;i<attachment.Uvs!.Length;i+=2)if(attachment.Uvs[i]==uv.Item1&&attachment.Uvs[i+1]==uv.Item2){index=i;break;}
            Assert.True(index>=0);
            Assert.Contains(Enumerable.Range(0,4),i=>MathF.Abs(corners[i*2]-vertices[index])<.001f&&MathF.Abs(corners[i*2+1]-vertices[index+1])<.001f);
        }
        Assert.Equal(48,report.TriangleCount);
        var values=attachment.Vertices!.Values!;
        for(int i=0;i<values.Length;i+=6)Assert.Equal(1f,values[i+2]+values[i+5],5);
        Assert.All(attachment.Uvs!,uv=>Assert.InRange(uv,0,1));
    }

    [Fact]
    public async Task FileMotionPlanParsesInRuntimeAndRejectsConflictingInputs()
    {
        string root=Path.Combine(Path.GetTempPath(),"spine-plan-"+Guid.NewGuid());Directory.CreateDirectory(root);
        try
        {
            var slice=new SliceManifest {ImageWidth=20,ImageHeight=20,Recipe=Fixture(),
                Parts=[new(){Name="sword",X=2,Y=3,Width=4,Height=6}]};
            File.WriteAllText(Path.Combine(root,"slice_manifest.json"),JsonSerializer.Serialize(slice));
            var project=new ProjectDescriptor("test",root,null,null,null,"fp");
            new SpineGenRigService(project).Rig(".");
            var plan=new MotionPlan {Animation="idle",Duration=3,Bones=[new(){Bone="hand",Rotate=[new(){Time=0,Value=0},new(){Time=1.5f,Value=105},new(){Time=3,Value=0}]}]};
            File.WriteAllText(Path.Combine(root,"idle.json"),JsonSerializer.Serialize(plan));
            var service=new SpineGenAnimateService(project);
            var result=await service.AnimateAsync(".",null,null,null,null,default,"idle.json");
            Assert.Equal("idle",result.Animation);
            var reader=new global::Spine.SkeletonJson(new SpineGenRigTests.StubLoader());
            var data=reader.ReadSkeletonData(new StringReader(File.ReadAllText(Path.Combine(root,"rig/skeleton.json"))));
            Assert.Equal(3,data.FindAnimation("idle").Duration);
            await Assert.ThrowsAsync<ArgumentException>(()=>service.AnimateAsync(".","idle_breath",null,null,null,default,"idle.json"));
            await Assert.ThrowsAsync<ArgumentException>(()=>service.AnimateAsync(".",null,"idle",null,null,default,"idle.json"));
        }
        finally{Directory.Delete(root,true);}
    }

    [Fact]
    public void MotionRejectsInfinityAndUnpairedTranslation()
    {
        var (_,rig)=Rigger.Build(new SliceManifest {ImageWidth=20,ImageHeight=20,Recipe=Fixture(),Parts=[new(){Name="sword",Width=4,Height=6}]});
        var plan=new MotionPlan {Animation="idle",Duration=3,Bones=[new(){Bone="hand",TranslateY=[new(){Time=0,Value=float.PositiveInfinity}]}]};
        var violations=MotionValidator.Validate(plan,rig);
        Assert.Contains(violations,v=>v.Code=="translate-pair");
        Assert.Contains(violations,v=>v.Code=="nan");
    }
}
