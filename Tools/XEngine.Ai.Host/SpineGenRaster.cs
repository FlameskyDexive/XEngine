namespace XEngine.Ai.Host;

/// <summary>Area resampling in premultiplied space, returned as editable straight RGBA.
/// High-resolution repair sources need not inflate the packed runtime texture.</summary>
internal static class SpineGenRaster
{
    public static byte[] Resize(byte[] src,int sw,int sh,int width,int height)
    {
        if(sw==width && sh==height)return src;
        var dst=new byte[checked(width*height*4)];
        for(int y=0;y<height;y++)for(int x=0;x<width;x++)
        {
            double x0=(double)x*sw/width,x1=(double)(x+1)*sw/width,y0=(double)y*sh/height,y1=(double)(y+1)*sh/height;
            double alpha=0,red=0,green=0,blue=0,area=(x1-x0)*(y1-y0);
            for(int sy=(int)y0;sy<Math.Ceiling(y1);sy++)for(int sx=(int)x0;sx<Math.Ceiling(x1);sx++)
            {
                double weight=(Math.Min(sx+1,x1)-Math.Max(sx,x0))*(Math.Min(sy+1,y1)-Math.Max(sy,y0));
                int i=(Math.Min(sy,sh-1)*sw+Math.Min(sx,sw-1))*4;
                double a=src[i+3]*weight;
                alpha+=a;red+=src[i]*a;green+=src[i+1]*a;blue+=src[i+2]*a;
            }
            int d=(y*width+x)*4;
            dst[d+3]=(byte)Math.Clamp((int)Math.Round(alpha/area),0,255);
            if(alpha>0)
            {dst[d]=(byte)Math.Round(red/alpha);dst[d+1]=(byte)Math.Round(green/alpha);dst[d+2]=(byte)Math.Round(blue/alpha);}
        }
        return dst;
    }
}
