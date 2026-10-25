using XEngine.Ai.SpineGen;

namespace XEngine.Ai.Host;

/// <summary>Publish straight-alpha editable parts into the PMA runtime page. Slice and
/// fill share this path so changed part pixels cannot leave a stale runtime texture.</summary>
internal static class SpineGenAtlasPage
{
    public static void Validate(string directory, SliceManifest manifest)
    {
        var page=manifest.Page;
        if(page.Width<=0 || page.Height<=0 || page.Width>AtlasPacker.MaxPage || page.Height>AtlasPacker.MaxPage ||
            page.Padding<0 || page.Padding>AtlasPacker.MaxPage/2 || string.IsNullOrWhiteSpace(page.Name))
            throw new InvalidOperationException("Invalid atlas manifest. Run spinegen-slice again before fill.");
        foreach(var part in manifest.Parts)
        {
            var at=part.Atlas;
            if(at.Width!=part.Width || at.Height!=part.Height || at.X<page.Padding || at.Y<page.Padding ||
                (long)at.X+at.Width+page.Padding>page.Width || (long)at.Y+at.Height+page.Padding>page.Height)
                throw new InvalidOperationException("Legacy or invalid atlas bounds. Run spinegen-slice again before fill.");
            PngRgba.Decode(File.ReadAllBytes(Path.Combine(directory,part.Png)),out int w,out int h);
            if(w!=part.Width || h!=part.Height)throw new InvalidOperationException($"Part dimensions changed: {part.Name}. Run spinegen-slice again.");
        }
    }

    public static void Write(string directory, SliceManifest manifest)
    {
        Validate(directory,manifest);
        var page=manifest.Page;
        if(page.Width<=0 || page.Height<=0 || string.IsNullOrWhiteSpace(page.Name))
            throw new InvalidOperationException("Slice manifest has no atlas page.");
        var rgba=new byte[checked(page.Width*page.Height*4)];
        int extrude=Math.Min(page.Padding,1);
        foreach(var part in manifest.Parts)
        {
            var pixels=PngRgba.Decode(File.ReadAllBytes(Path.Combine(directory,part.Png)),out int w,out int h);
            var at=part.Atlas;
            if(w!=part.Width || h!=part.Height || w!=at.Width || h!=at.Height ||
                at.X-extrude<0 || at.Y-extrude<0 || at.X+w+extrude>page.Width || at.Y+h+extrude>page.Height)
                throw new InvalidOperationException($"Invalid atlas content bounds: {part.Name}");
            for(int y=-extrude;y<h+extrude;y++)for(int x=-extrude;x<w+extrude;x++)
            {
                int s=(Math.Clamp(y,0,h-1)*w+Math.Clamp(x,0,w-1))*4;
                int d=((at.Y+y)*page.Width+at.X+x)*4;
                int a=pixels[s+3];
                for(int c=0;c<3;c++)rgba[d+c]=(byte)((pixels[s+c]*a+127)/255);
                rgba[d+3]=(byte)a;
            }
        }
        File.WriteAllBytes(Path.Combine(directory,page.Name),PngRgba.Encode(rgba,page.Width,page.Height));
        // Legacy straight-alpha pages must also update their atlas PMA declaration.
        var regions=new List<SpineAtlasRegion>();
        foreach(var p in manifest.Parts)regions.Add(new(){Name=p.Name,X=p.Atlas.X,Y=p.Atlas.Y,Width=p.Width,Height=p.Height});
        using var writer=new StreamWriter(Path.Combine(directory,Path.GetFileNameWithoutExtension(page.Name)+".atlas.txt"));
        SpineAtlasWriter.Write(writer,page.Name,page.Width,page.Height,regions,premultipliedAlpha:true);
    }
}
