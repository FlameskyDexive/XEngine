using System.Text.Json.Serialization;
using System.Text.Json;

namespace XEngine.Ai.SpineGen;

/// <summary>Explicit exploded-sheet assembly. Crop/pivot/mask coordinates are image Y-down;
/// bone and part translations/rotations use Spine local Y-up coordinates, in source pixels.</summary>
public sealed class AssemblyRecipe
{
    [JsonPropertyName("version")] public int Version { get; set; } = 1;
    [JsonPropertyName("bones")] public List<SpineBoneDef> Bones { get; set; } = [];
    [JsonPropertyName("parts")] public List<AssemblyPart> Parts { get; set; } = [];
    [JsonPropertyName("joints")] public List<AssemblyJoint> Joints { get; set; } = [];

    public void Validate(int width, int height)
    {
        if (Version != 1) throw new ArgumentException("Unsupported assembly recipe version.");
        if (Bones.Count == 0) throw new ArgumentException("Recipe needs bones.");
        SpineExporter.OrderBonesParentFirst(Bones);
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var bone in Bones)
        {
            if (string.IsNullOrWhiteSpace(bone.Name) || bone.Name.StartsWith("b_", StringComparison.Ordinal))
                throw new ArgumentException("Recipe bones need names outside the reserved b_ namespace.");
            names.Add(bone.Name);
            Finite(bone.X ?? 0, bone.Y ?? 0, bone.Rotation ?? 0, bone.ScaleX ?? 1, bone.ScaleY ?? 1, bone.ShearX ?? 0, bone.ShearY ?? 0);
            if ((bone.ScaleX ?? 1) == 0 || (bone.ScaleY ?? 1) == 0)
                throw new ArgumentException("Zero bone scale.");
        }
        var partNames = new HashSet<string>(StringComparer.Ordinal);
        int enabled = 0;
        foreach (var p in Parts)
        {
            if (string.IsNullOrWhiteSpace(p.Name) || p.Name.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is '_' or '-')) || !partNames.Add(p.Name))
                throw new ArgumentException("Part names must be unique safe file names.");
            if (!p.Enabled) continue;
            enabled++;
            int sourceWidth=p.Image==null?width:p.ImageWidth,sourceHeight=p.Image==null?height:p.ImageHeight;
            if(sourceWidth<=0 || sourceHeight<=0 || p.Image!=null&&(sourceWidth>4096 || sourceHeight>4096))
                throw new ArgumentException($"Invalid source raster dimensions: {p.Name}");
            if (!names.Contains(p.Bone)) throw new ArgumentException($"Unknown bone for {p.Name}: {p.Bone}");
            if (p.Crop.Width <= 0 || p.Crop.Height <= 0 || p.Crop.X < 0 || p.Crop.Y < 0 ||
                (long)p.Crop.X + p.Crop.Width > sourceWidth || (long)p.Crop.Y + p.Crop.Height > sourceHeight)
                throw new ArgumentException($"Crop outside source: {p.Name}");
            Finite(p.PivotX,p.PivotY,p.X,p.Y,p.Rotation,p.ScaleX,p.ScaleY,p.ShearX,p.ShearY);
            Finite(p.ExclusionFeather,p.MaskFeather,p.AlphaScale,p.WeightStart,p.WeightEnd);
            if (p.WeightAxis != null)
            {
                if (p.WeightAxis.Length != 4) throw new ArgumentException($"Weight axis needs two normalized XY endpoints: {p.Name}");
                Finite(p.WeightAxis);
                float dx=p.WeightAxis[2]-p.WeightAxis[0],dy=p.WeightAxis[3]-p.WeightAxis[1];
                if (dx*dx+dy*dy < 1e-8f || p.WeightAxis.Any(static v=>v<0 || v>1))
                    throw new ArgumentException($"Invalid weight axis: {p.Name}");
            }
            if(p.ExclusionFeather<0 || p.ExclusionFeather>64 || p.MaskFeather<0 || p.MaskFeather>64 || p.AlphaScale<=0 || p.AlphaScale>4 || p.WeightStart<0 || p.WeightEnd>1 || p.WeightStart>=p.WeightEnd)
                throw new ArgumentException($"Invalid feather or weight transition: {p.Name}");
            if (p.ScaleX == 0 || p.ScaleY == 0) throw new ArgumentException($"Zero scale: {p.Name}");
            if (p.MaskPolygon != null)
            {
                if (p.MaskPolygon.Length < 6 || p.MaskPolygon.Length % 2 != 0)
                    throw new ArgumentException($"Mask polygon needs at least three XY pairs: {p.Name}");
                Finite(p.MaskPolygon);
            }
            foreach(var polygon in p.ExcludePolygons)
            {
                if(polygon.Length<6 || polygon.Length%2!=0)throw new ArgumentException($"Invalid exclusion polygon: {p.Name}");
                Finite(polygon);
            }
            foreach (string influence in p.Influences)
                if (!names.Contains(influence)) throw new ArgumentException($"Unknown influence: {influence}");
            if(p.Influences.Length>2 || p.Influences.Length==2&&p.Influences[0]==p.Influences[1])
                throw new ArgumentException("Use zero, one, or two distinct top-to-bottom influences.");
        }
        if (enabled == 0) throw new ArgumentException("Recipe has no enabled parts.");
        var constrained=new HashSet<string>(StringComparer.Ordinal);
        foreach(var joint in Joints)
        {
            var bone=Bones.Find(b=>b.Name==joint.Bone);
            var part=Parts.Find(p=>p.Name==joint.ParentPart&&p.Enabled);
            if(bone==null || part==null || bone.Parent!=part.Bone || !constrained.Add(joint.Bone))
                throw new ArgumentException("Joint must uniquely connect a bone to a part on its direct parent bone.");
            Finite(joint.SourceX,joint.SourceY);
        }
    }

    private static void Finite(params float[] values)
    {
        foreach (float value in values)
            if (!float.IsFinite(value)) throw new ArgumentException("Non-finite assembly transform.");
    }

    public SliceRunResult Slice(byte[] rgba, int width, int height, IReadOnlyDictionary<string,AssemblyImage>? images = null)
    {
        Validate(width, height);
        var parts = new List<SlicePartImage>();
        foreach (AssemblyPart p in Parts.Where(p => p.Enabled).OrderBy(p => p.Layer).ThenBy(p => p.Name, StringComparer.Ordinal))
        {
            byte[] source=rgba;int stride=width;
            if(p.Image!=null)
            {
                if(images==null || !images.TryGetValue(p.Name,out var image) || image.Width!=p.ImageWidth || image.Height!=p.ImageHeight)
                    throw new ArgumentException($"Missing or mismatched part source: {p.Name}");
                source=image.Rgba;stride=image.Width;
            }
            int w = p.Crop.Width, h = p.Crop.Height, pixels = 0;
            var crop = new byte[w*h*4];
            var mask = new byte[w*h];
            for (int y=0;y<h;y++) for (int x=0;x<w;x++)
            {
                if (p.MaskPolygon != null && !Inside(p.MaskPolygon,x+0.5f,y+0.5f)) continue;
                bool excluded=false;float coverage=1;
                if(p.MaskPolygon!=null && p.MaskFeather>0)
                    coverage=Math.Clamp(Distance(p.MaskPolygon,x+0.5f,y+0.5f)/p.MaskFeather,0,1);
                foreach(var polygon in p.ExcludePolygons)
                {
                    if(Inside(polygon,x+0.5f,y+0.5f)){excluded=true;break;}
                    // Feather only authored internal seams, never the original silhouette.
                    if(p.ExclusionFeather>0)coverage=Math.Min(coverage,Math.Clamp(Distance(polygon,x+0.5f,y+0.5f)/p.ExclusionFeather,0,1));
                }
                if(excluded)continue;
                int src=((p.Crop.Y+y)*stride+p.Crop.X+x)*4, dst=(y*w+x)*4;
                Buffer.BlockCopy(source,src,crop,dst,4);
                crop[dst+3]=(byte)MathF.Round(Math.Min(255,crop[dst+3]*p.AlphaScale)*coverage);
                if(crop[dst+3]>0) { mask[y*w+x]=255; pixels++; }
            }
            if(pixels==0) throw new ArgumentException($"Empty part: {p.Name}");
            if (p.KeepLargestIsland)
            {
                // A crop may contain an unrelated neighbouring piece. Select the principal
                // alpha island, retain its original RGBA plus a one-pixel antialias fringe.
                var islands = Slicer.Slice(crop,w,h,new SliceOptions { AlphaThreshold=8,MinAreaPixels=1 });
                var largest = islands.Parts.MaxBy(part=>part.PixelCount)
                    ?? throw new ArgumentException($"No alpha island: {p.Name}");
                var keep = new bool[w*h];
                for(int yy=0;yy<largest.Height;yy++) for(int xx=0;xx<largest.Width;xx++)
                    if(largest.Mask[yy*largest.Width+xx]!=0)
                        for(int dy=-1;dy<=1;dy++) for(int dx=-1;dx<=1;dx++)
                        {
                            int tx=largest.X+xx+dx,ty=largest.Y+yy+dy;
                            if(tx>=0&&tx<w&&ty>=0&&ty<h)keep[ty*w+tx]=true;
                        }
                pixels=0;
                for(int i=0;i<keep.Length;i++)
                    if(!keep[i]) { Array.Clear(crop,i*4,4);mask[i]=0; }
                    else if(crop[i*4+3]>0)pixels++;
            }
            // Tighten only truly transparent borders. Keep every remaining alpha value,
            // including the low-alpha fringe; the recipe retains the original crop/pivot.
            int left=w,top=h,right=-1,bottom=-1;
            for(int y=0;y<h;y++)for(int x=0;x<w;x++)
                if(crop[(y*w+x)*4+3]!=0)
                {left=Math.Min(left,x);top=Math.Min(top,y);right=Math.Max(right,x);bottom=Math.Max(bottom,y);}
            if(right<left)throw new ArgumentException($"Empty part after selection: {p.Name}");
            int tw=right-left+1,th=bottom-top+1;
            var tight=new byte[tw*th*4];var tightMask=new byte[tw*th];
            for(int y=0;y<th;y++)
            {
                Buffer.BlockCopy(crop,((top+y)*w+left)*4,tight,y*tw*4,tw*4);
                Buffer.BlockCopy(mask,(top+y)*w+left,tightMask,y*tw,tw);
            }
            parts.Add(new SlicePartImage { Name=p.Name,SourceIsland=parts.Count,X=p.Crop.X+left,Y=p.Crop.Y+top,
                Width=tw,Height=th,PixelCount=pixels,LayerIndex=parts.Count,Rgba=tight,Mask=tightMask });
        }
        return new SliceRunResult { ImageWidth=width,ImageHeight=height,OpaqueRatio=(float)parts.Sum(p=>p.PixelCount)/(width*height),
            DiscardedNoiseParts=0,Split=SliceSplitMode.ConnectedComponents,MarkerMinDepth=0,MarkerSpacing=0,Parts=parts };
    }

    private static float Distance(float[] polygon,float x,float y)
    {
        float best=float.PositiveInfinity;
        for(int i=0,j=polygon.Length-2;i<polygon.Length;j=i,i+=2)
        {
            float dx=polygon[i]-polygon[j],dy=polygon[i+1]-polygon[j+1],length=dx*dx+dy*dy;
            float t=length==0?0:Math.Clamp(((x-polygon[j])*dx+(y-polygon[j+1])*dy)/length,0,1);
            float ex=x-polygon[j]-t*dx,ey=y-polygon[j+1]-t*dy;
            best=Math.Min(best,ex*ex+ey*ey);
        }
        return MathF.Sqrt(best);
    }

    private static bool Inside(float[] poly,float x,float y)
    {
        bool inside=false;
        for(int i=0,j=poly.Length-2;i<poly.Length;j=i,i+=2)
            if((poly[i+1]>y)!=(poly[j+1]>y) && x<(poly[j]-poly[i])*(y-poly[i+1])/(poly[j+1]-poly[i+1])+poly[i]) inside=!inside;
        return inside;
    }

    public (SpineSkeletonDoc Doc,RigManifest Manifest) Build(SliceManifest slice)
    {
        Validate(slice.ImageWidth,slice.ImageHeight);
        // Resolve source landmarks without mutating the saved recipe or its hash.
        var resolved=JsonSerializer.Deserialize<List<SpineBoneDef>>(JsonSerializer.Serialize(Bones))!;
        foreach(var joint in Joints)
        {
            var p=Parts.Find(p=>p.Name==joint.ParentPart)!;
            var bone=resolved.Find(b=>b.Name==joint.Bone)!;
            float ax=(p.Rotation+p.ShearX)*MathF.PI/180,ay=(p.Rotation+90+p.ShearY)*MathF.PI/180;
            float dx=(joint.SourceX-p.PivotX)*p.ScaleX,dy=(p.PivotY-joint.SourceY)*p.ScaleY;
            bone.X=p.X+MathF.Cos(ax)*dx+MathF.Cos(ay)*dy;
            bone.Y=p.Y+MathF.Sin(ax)*dx+MathF.Sin(ay)*dy;
        }
        var skin = new SpineSkinDef();
        var doc = new SpineSkeletonDoc { Skeleton=new SpineSkeletonMeta(),Bones=resolved,Slots=[],Skins=[skin],Animations=[] };
        var rig = new RigManifest { ImageWidth=slice.ImageWidth,ImageHeight=slice.ImageHeight };
        var selected = Parts.Where(p=>p.Enabled).ToDictionary(p=>p.Name,StringComparer.Ordinal);
        if (slice.Parts.Count!=selected.Count) throw new ArgumentException("Recipe and slice part sets differ.");
        foreach(var bone in resolved)
            rig.Bones.Add(new RigBoneInfo {Name=bone.Name,Parent=bone.Parent,ImageX=bone.X??0,ImageY=-(bone.Y??0),Length=bone.Length??1});
        foreach(var crop in slice.Parts.OrderBy(p=>p.Layer))
        {
            if(!selected.TryGetValue(crop.Name,out var p)) throw new ArgumentException($"Unknown sliced part {crop.Name}");
            string bone="b_"+p.Name, slot=p.Name+"_slot";
            doc.Bones.Add(new SpineBoneDef {Name=bone,Parent=p.Bone,X=p.X,Y=p.Y,Rotation=p.Rotation,ScaleX=p.ScaleX,ScaleY=p.ScaleY,ShearX=p.ShearX,ShearY=p.ShearY});
            doc.Slots.Add(new SpineSlotDef {Name=slot,Bone=bone,Attachment=p.Visible?p.Name:null});
            float trimX=crop.X-p.Crop.X,trimY=crop.Y-p.Crop.Y;
            skin.Add(slot,p.Name,SpineAttachment.Region(p.Name,trimX+crop.Width/2f-p.PivotX,p.PivotY-trimY-crop.Height/2f,0,crop.Width,crop.Height));
            rig.Bones.Add(new RigBoneInfo {Name=bone,Parent=p.Bone,ImageX=p.Crop.X+p.PivotX,ImageY=p.Crop.Y+p.PivotY,Length=1});
            rig.Parts.Add(new RigPartBinding {Part=p.Name,Bone=bone,DeformBone=p.Bone,Role=p.Rigid?RigRole.Prop:RigRole.Spine});
        }
        doc.Bones=SpineExporter.OrderBonesParentFirst(doc.Bones);
        return (doc,rig);
    }
}

public sealed class AssemblyPart
{
    [JsonPropertyName("image")] public string? Image { get; set; }
    [JsonPropertyName("imageWidth")] public int ImageWidth { get; set; }
    [JsonPropertyName("imageHeight")] public int ImageHeight { get; set; }
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("enabled")] public bool Enabled { get; set; } = true;
    [JsonPropertyName("crop")] public SliceRect Crop { get; set; } = new();
    [JsonPropertyName("maskPolygon")] public float[]? MaskPolygon { get; set; }
    [JsonPropertyName("maskFeather")] public float MaskFeather { get; set; }
    [JsonPropertyName("alphaScale")] public float AlphaScale { get; set; } = 1;
    [JsonPropertyName("keepLargestIsland")] public bool KeepLargestIsland { get; set; }
    [JsonPropertyName("excludePolygons")] public List<float[]> ExcludePolygons { get; set; } = [];
    [JsonPropertyName("exclusionFeather")] public float ExclusionFeather { get; set; }
    [JsonPropertyName("bone")] public string Bone { get; set; } = "";
    [JsonPropertyName("pivotX")] public float PivotX { get; set; }
    [JsonPropertyName("pivotY")] public float PivotY { get; set; }
    [JsonPropertyName("x")] public float X { get; set; }
    [JsonPropertyName("y")] public float Y { get; set; }
    [JsonPropertyName("rotation")] public float Rotation { get; set; }
    [JsonPropertyName("shearX")] public float ShearX { get; set; }
    [JsonPropertyName("shearY")] public float ShearY { get; set; }
    [JsonPropertyName("scaleX")] public float ScaleX { get; set; } = 1;
    [JsonPropertyName("scaleY")] public float ScaleY { get; set; } = 1;
    [JsonPropertyName("layer")] public int Layer { get; set; }
    [JsonPropertyName("rigid")] public bool Rigid { get; set; } = true;
    [JsonPropertyName("influences")] public string[] Influences { get; set; } = [];
    [JsonPropertyName("weightStart")] public float WeightStart { get; set; }
    [JsonPropertyName("weightEnd")] public float WeightEnd { get; set; } = 1;
    /// <summary>Optional normalized original-crop XY axis, first influence to second, image Y down.</summary>
    [JsonPropertyName("weightAxis")] public float[]? WeightAxis { get; set; }
    [JsonPropertyName("visible")] public bool Visible { get; set; } = true;
}

public sealed class AssemblyJoint
{
    [JsonPropertyName("bone")] public string Bone { get; set; } = "";
    [JsonPropertyName("parentPart")] public string ParentPart { get; set; } = "";
    [JsonPropertyName("sourceX")] public float SourceX { get; set; }
    [JsonPropertyName("sourceY")] public float SourceY { get; set; }
}

public sealed record AssemblyImage(byte[] Rgba,int Width,int Height);
