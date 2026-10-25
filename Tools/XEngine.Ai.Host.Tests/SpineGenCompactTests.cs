using System.Numerics;
using XEngine.Ai.SpineGen;
using Xunit;

namespace XEngine.Ai.Host.Tests;

public sealed class SpineGenCompactTests
{
    [Theory]
    [InlineData(0)] [InlineData(2)]
    public void TallPartDoesNotReserveAnEntireShelf(int pad)
    {
        PackRequest[] items = [new(){Index=7,Width=64-pad*2,Height=512-pad*2},
            new(){Index=42,Width=64-pad*2,Height=256-pad*2},new(){Index=81,Width=64-pad*2,Height=256-pad*2}];
        var result=AtlasPacker.Pack(items,pad);
        Assert.Equal(128*512,result.PageWidth*result.PageHeight);
        Assert.Equal(new[]{7,42,81},result.Placements.Select(p=>p.Index));
        for(int i=0;i<items.Length;i++)
        {
            var a=result.Placements[i];
            Assert.InRange(a.X-pad,0,result.PageWidth);Assert.InRange(a.Y-pad,0,result.PageHeight);
            Assert.InRange(a.Right+pad,0,result.PageWidth);Assert.InRange(a.Bottom+pad,0,result.PageHeight);
            for(int j=i+1;j<items.Length;j++)
            {
                var b=result.Placements[j];
                Assert.False(a.X-pad<b.Right+pad && b.X-pad<a.Right+pad && a.Y-pad<b.Bottom+pad && b.Y-pad<a.Bottom+pad);
            }
        }
        var reversed=AtlasPacker.Pack(items.Reverse().ToArray(),pad);
        Assert.Equal(result.Placements.OrderBy(p=>p.Index),reversed.Placements.OrderBy(p=>p.Index));
    }

    [Fact]
    public void PaddingAndOverflowArePartOfFeasibility()
    {
        var r=AtlasPacker.Pack([new(){Index=17,Width=7,Height=1}],1);
        Assert.Equal(16,r.PageWidth);Assert.Equal(4,r.PageHeight);
        Assert.ThrowsAny<ArgumentException>(()=>AtlasPacker.Pack([new(){Index=0,Width=1,Height=1}],int.MaxValue));
        Assert.Throws<InvalidOperationException>(()=>AtlasPacker.Pack([new(){Index=0,Width=int.MaxValue,Height=1}],2));
        Assert.Throws<ArgumentException>(()=>AtlasPacker.Pack([new(){Index=0,Width=1,Height=1},new(){Index=0,Width=1,Height=1}],0));
    }

    [Fact]
    public void TransparentTrimPreservesSourcePixelsAndBindCoordinates()
    {
        var recipe=SpineGenAssemblyTests.Fixture();var p=recipe.Parts[0];
        p.Crop=new(){X=2,Y=3,Width=12,Height=14};p.PivotX=9;p.PivotY=11;p.Rigid=false;p.Influences=["root","hand"];
        var rgba=new byte[20*20*4];
        for(int y=7;y<13;y++)for(int x=5;x<10;x++)
        {int i=(y*20+x)*4;rgba[i]=120;rgba[i+1]=80;rgba[i+3]=(byte)(x==5?1:y==7?73:255);}
        var cut=Assert.Single(recipe.Slice(rgba,20,20).Parts);
        Assert.Equal((5,7,5,6),(cut.X,cut.Y,cut.Width,cut.Height));
        for(int y=0;y<cut.Height;y++)for(int x=0;x<cut.Width;x++)
            for(int c=0;c<4;c++)Assert.Equal(rgba[((y+7)*20+x+5)*4+c],cut.Rgba[(y*cut.Width+x)*4+c]);
        var entry=new SlicePartEntry{Name=p.Name,X=cut.X,Y=cut.Y,Width=cut.Width,Height=cut.Height};
        var (doc,rig)=recipe.Build(new(){ImageWidth=20,ImageHeight=20,Recipe=recipe,Parts=[entry]});
        var region=doc.Skins![0].Attachments["sword_slot"]["sword"];
        Assert.Equal(3+5/2f-9,region.X);Assert.Equal(11-4-6/2f,region.Y);
        Assert.Equal(11,rig.Bones[^1].ImageX);Assert.Equal(14,rig.Bones[^1].ImageY);
        var mesh=AssemblySkinner.Build(p,doc,out _,entry);
        var worlds=AssemblySkinner.WorldTransforms(doc.Bones!);
        var expected=Vector2.Transform(new Vector2(3-9,11-4),worlds["b_sword"]);
        var values=mesh.Vertices!.Values!;
        Vector2 actual=Vector2.Zero;
        foreach(var (name,offset) in new[]{("root",0),("hand",3)})
            actual+=Vector2.Transform(new Vector2(values[offset],values[offset+1]),worlds[name])*values[offset+2];
        Assert.InRange(Vector2.Distance(expected,actual),0,.0001f);
        float v=4/14f;
        Assert.Equal(v*v*(3-2*v),values[5],5);
        Assert.All(mesh.Uvs!,uv=>Assert.InRange(uv,0,1));

        var atlasText=new StringWriter();
        SpineAtlasWriter.Write(atlasText,"test.png",64,64,[new(){Name=p.Name,X=7,Y=11,Width=cut.Width,Height=cut.Height}],true);
        var atlas=new global::Spine.Atlas(new StringReader(atlasText.ToString()),"",new XEngine.Spine.SpineNoopTextureLoader());
        var reader=new global::Spine.SkeletonJson(new global::Spine.AtlasAttachmentLoader(atlas));
        var regionSkeleton=new global::Spine.Skeleton(reader.ReadSkeletonData(new StringReader(SpineExporter.Write(doc))));
        regionSkeleton.UpdateWorldTransform(global::Spine.Physics.Update);
        var rs=regionSkeleton.Slots.Items[0];var ra=Assert.IsType<global::Spine.RegionAttachment>(rs.Pose.Attachment);
        var corners=new float[8];ra.ComputeWorldVertices(rs,ra.GetOffsets(rs.Pose),corners,0);
        doc.Skins[0].Attachments["sword_slot"]["sword"]=mesh;
        var skeleton=new global::Spine.Skeleton(reader.ReadSkeletonData(new StringReader(SpineExporter.Write(doc))));
        skeleton.UpdateWorldTransform(global::Spine.Physics.Update);
        var slot=skeleton.Slots.Items[0];var attachment=Assert.IsType<global::Spine.MeshAttachment>(slot.Pose.Attachment);
        var vertices=new float[attachment.WorldVerticesLength];attachment.ComputeWorldVertices(skeleton,slot,vertices);
        for(int c=0;c<8;c+=2)
            Assert.Contains(Enumerable.Range(0,vertices.Length/2),i=>MathF.Abs(vertices[i*2]-corners[c])<.001f&&MathF.Abs(vertices[i*2+1]-corners[c+1])<.001f);
        var uvPage=new float[attachment.RegionUVs.Length];
        global::Spine.MeshAttachment.ComputeUVs(atlas.FindRegion(p.Name),attachment.RegionUVs,uvPage);
        for(int i=0;i<uvPage.Length;i+=2)
        {Assert.InRange(uvPage[i],7/64f,12/64f);Assert.InRange(uvPage[i+1],11/64f,17/64f);}
    }
}
