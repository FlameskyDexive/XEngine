Shader "Voxel/Cutout"

Variants
{
    Variant("HAS_UV2")
}

Properties
{
    _VoxelAtlas ("Atlas", Texture2DArray) = "white"
    _MainColor ("Tint", Color) = (1.0, 1.0, 1.0, 1.0)
    _Tiling ("Tiling", Vector2) = (1.0, 1.0)
    _Offset ("Offset", Vector2) = (0.0, 0.0)
    _AlphaCutoff ("Alpha Cutoff", Float) = 0.5
}

Pass "VoxelCutout"
{
    Tags { "RenderOrder" = "Opaque" }
    Cull Back
    ZWrite On

    GLSLPROGRAM
        Vertex
        {
            #include "XEngineCG"
            #include "VertexAttributes"

            out vec2 texCoord0;
            out float slice;
            out vec3 worldPos;
            out vec4 vColor;

            uniform vec2 _Tiling;
            uniform vec2 _Offset;

            void main()
            {
                gl_Position = TransformClip(vertexPosition);
                texCoord0 = vertexTexCoord0 * _Tiling + _Offset;
                slice = vertexTexCoord1.x;
                worldPos = TransformPosition(vertexPosition);
                vColor = GetInstanceColor();
            }
        }

        Fragment
        {
            #include "XEngineCG"
            #include "Lighting"

            layout (location = 0) out vec4 fragColor;

            in vec2 texCoord0;
            in float slice;
            in vec3 worldPos;
            in vec4 vColor;

            uniform sampler2DArray _VoxelAtlas;
            uniform vec4 _MainColor;
            uniform float _AlphaCutoff;

            void main()
            {
                vec4 albedo = texture(_VoxelAtlas, vec3(texCoord0, slice)) * vColor * _MainColor;
                if (albedo.a < _AlphaCutoff)
                    discard;
                vec3 baseColor = gammaToLinearSpace(albedo.rgb);
                baseColor = ApplyFog(baseColor, worldPos);
                fragColor = vec4(baseColor, 1.0);
            }
        }
    ENDGLSL

    SLANGPROGRAM
        Vertex
        {
            #include "XEngineCG"

            cbuffer VoxelCutoutMaterial : register(b2)
            {
                float2 _Tiling;
                float2 _Offset;
                float4 _MainColor;
                float _AlphaCutoff;
            };

            struct VSInput
            {
                float3 vertexPosition : POSITION;
                float2 vertexTexCoord0 : TEXCOORD0;
                float2 vertexTexCoord1 : TEXCOORD1;
            };

            struct VSOutput
            {
                float4 position : SV_Position;
                float2 texCoord0 : TEXCOORD0;
                float slice : TEXCOORD1;
                float3 worldPos : TEXCOORD2;
                float4 vColor : COLOR0;
            };

            VSOutput main(VSInput input)
            {
                VSOutput o;
                o.position = TransformClip(input.vertexPosition);
                o.texCoord0 = input.vertexTexCoord0 * _Tiling + _Offset;
                o.slice = input.vertexTexCoord1.x;
                o.worldPos = TransformPosition(input.vertexPosition);
                o.vColor = GetInstanceColor();
                return o;
            }
        }

        Fragment
        {
            #include "XEngineCG"

            Texture2DArray _VoxelAtlas : register(t0);
            SamplerState _VoxelAtlasSampler : register(s0);

            cbuffer VoxelCutoutMaterial : register(b2)
            {
                float2 _Tiling;
                float2 _Offset;
                float4 _MainColor;
                float _AlphaCutoff;
            };

            struct PSInput
            {
                float4 position : SV_Position;
                float2 texCoord0 : TEXCOORD0;
                float slice : TEXCOORD1;
                float3 worldPos : TEXCOORD2;
                float4 vColor : COLOR0;
            };

            float4 main(PSInput input) : SV_Target
            {
                float4 albedo = _VoxelAtlas.Sample(_VoxelAtlasSampler, float3(input.texCoord0, input.slice)) * input.vColor * _MainColor;
                if (albedo.a < _AlphaCutoff)
                    discard;
                float3 baseColor = gammaToLinearSpace(albedo.rgb);
                baseColor = ApplyFog(baseColor, input.worldPos);
                return float4(baseColor, 1.0);
            }
        }
    ENDSLANG
}
