Shader "Prototype/ImGui"
Variants { }
Properties { }
Pass "UI"
{
    Tags { "RenderOrder" = "Opaque" }
    Blend { Src One Dst OneMinusSrcAlpha Mode Add }
    ZTest Off
    ZWrite Off
    Cull Off
    GLSLPROGRAM
    Vertex
    {
        layout(location=0) in vec2 aPosition;
        layout(location=1) in vec2 aUV;
        layout(location=4) in vec4 aColor;
        uniform mat4 projection;
        out vec2 uv;
        out vec4 color;
        void main() { gl_Position = projection * vec4(aPosition,0,1); uv=aUV; color=aColor; }
    }
    Fragment
    {
        in vec2 uv;
        in vec4 color;
        uniform sampler2D texture0;
        out vec4 result;
        void main()
        {
            vec4 t=texture(texture0,uv);
            vec3 c=color.rgb;
            c=c*(c*(c*0.305306011+0.682171111)+0.012522878);
            float a=color.a*t.a;
            result=vec4(c*t.rgb*a,a);
        }
    }
    ENDGLSL
    SLANGPROGRAM
    Vertex
    {
        cbuffer UIVS : register(b0) { float4x4 projection; };
        struct Input { float2 pos:POSITION; float2 uv:TEXCOORD0; float4 color:COLOR0; };
        struct Output { float4 pos:SV_Position; float2 uv:TEXCOORD0; float4 color:COLOR0; };
        Output main(Input i) { Output o; o.pos=mul(projection,float4(i.pos,0,1)); o.uv=i.uv; o.color=i.color; return o; }
    }
    Fragment
    {
        Texture2D texture0:register(t0);
        SamplerState texture0Sampler:register(s0);
        struct Input { float4 pos:SV_Position; float2 uv:TEXCOORD0; float4 color:COLOR0; };
        float4 main(Input i):SV_Target
        {
            float4 t=texture0.Sample(texture0Sampler,i.uv);
            float3 c=i.color.rgb;
            c=c*(c*(c*0.305306011+0.682171111)+0.012522878);
            float a=i.color.a*t.a;
            return float4(c*t.rgb*a,a);
        }
    }
    ENDSLANG
}
