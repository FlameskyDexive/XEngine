using System.Numerics;

namespace XEngine.Ai.SpineGen;

/// <summary>Small alpha-backed grid for explicit soft parts. The source image carries the
/// silhouette; interior vertices permit smooth deformation without triangulation cutting hair.
/// Influence order is top-to-bottom. Every influence is encoded in its inverse bind space.</summary>
public static class AssemblySkinner
{
    public static SpineAttachment Build(AssemblyPart part, SpineSkeletonDoc doc, out SkinPartResult report, SlicePartEntry? crop = null)
    {
        const int columns=4, rows=6;
        int width=crop?.Width??part.Crop.Width,height=crop?.Height??part.Crop.Height;
        int trimX=crop==null?0:crop.X-part.Crop.X,trimY=crop==null?0:crop.Y-part.Crop.Y;
        var points=new List<(int X,int Y)>();
        var indices=new int[columns+1,rows+1];
        void Add(int x,int y){indices[x,y]=points.Count;points.Add((x,y));}
        for(int x=0;x<=columns;x++)Add(x,0);
        for(int y=1;y<=rows;y++)Add(columns,y);
        for(int x=columns-1;x>=0;x--)Add(x,rows);
        for(int y=rows-1;y>0;y--)Add(0,y);
        int hull=points.Count;
        for(int y=1;y<rows;y++)for(int x=1;x<columns;x++)Add(x,y);
        var triangles=new List<int>();
        for(int y=0;y<rows;y++)for(int x=0;x<columns;x++)
        {
            int a=indices[x,y],b=indices[x+1,y],c=indices[x,y+1],d=indices[x+1,y+1];
            triangles.AddRange([a,c,b,b,c,d]);
        }
        List<SpineBoneDef> bones=doc.Bones!;
        var worlds=WorldTransforms(bones);
        var boneIndices=new Dictionary<string,int>(StringComparer.Ordinal);
        for(int i=0;i<bones.Count;i++)boneIndices.Add(bones[i].Name,i);
        Matrix3x2 source=worlds["b_"+part.Name];
        string[] influences=part.Influences.Length>0?part.Influences:["b_"+part.Name];
        var inverse=new Matrix3x2[influences.Length];
        for(int i=0;i<inverse.Length;i++)
            if(!Matrix3x2.Invert(worlds[influences[i]],out inverse[i]))throw new ArgumentException("Singular bind matrix.");
        var uvs=new List<float>();var encodedBones=new List<int>();var values=new List<float>();
        foreach(var (x,y) in points)
        {
            float u=(float)x/columns,v=(float)y/rows;
            uvs.Add(u);uvs.Add(v);
            Vector2 world=Vector2.Transform(new Vector2(trimX+u*width-part.PivotX,part.PivotY-trimY-v*height),source);
            float originalV=(trimY+v*height)/part.Crop.Height;
            if (part.WeightAxis is { } axis)
            {
                float originalU=(trimX+u*width)/part.Crop.Width;
                float dx=axis[2]-axis[0],dy=axis[3]-axis[1];
                originalV=((originalU-axis[0])*dx+(originalV-axis[1])*dy)/(dx*dx+dy*dy);
            }
            float transition=Math.Clamp((originalV-part.WeightStart)/(part.WeightEnd-part.WeightStart),0,1);
            float bottom=transition*transition*(3-2*transition); // Cropping must not move the weight transition.
            encodedBones.Add(influences.Length);
            for(int i=0;i<influences.Length;i++)
            {
                Vector2 local=Vector2.Transform(world,inverse[i]);
                encodedBones.Add(boneIndices[influences[i]]);
                values.Add(local.X);values.Add(local.Y);values.Add(influences.Length==1?1:i==0?1-bottom:bottom);
            }
        }
        report=new SkinPartResult {Name=part.Name,Meshed=true,Reason="explicit bind-space grid",HullVertexCount=points.Count,
            TriangleCount=triangles.Count/3,Coverage=1,Weighted=true,InfluenceBones=[..influences]};
        return SpineAttachment.Mesh(part.Name,SpineMeshVertices.Weighted(encodedBones.ToArray(),values.ToArray()),
            uvs.ToArray(),triangles.ToArray(),hull,width,height);
    }

    public static Dictionary<string,Matrix3x2> WorldTransforms(List<SpineBoneDef> bones)
    {
        var result=new Dictionary<string,Matrix3x2>(StringComparer.Ordinal);
        foreach(var b in SpineExporter.OrderBonesParentFirst(bones))
        {
            float rx=((b.Rotation??0)+(b.ShearX??0))*MathF.PI/180;
            float ry=((b.Rotation??0)+90+(b.ShearY??0))*MathF.PI/180;
            var local=new Matrix3x2(MathF.Cos(rx)*(b.ScaleX??1),MathF.Sin(rx)*(b.ScaleX??1),
                MathF.Cos(ry)*(b.ScaleY??1),MathF.Sin(ry)*(b.ScaleY??1),b.X??0,b.Y??0);
            result.Add(b.Name,b.Parent==null?local:local*result[b.Parent]);
        }
        return result;
    }
}
