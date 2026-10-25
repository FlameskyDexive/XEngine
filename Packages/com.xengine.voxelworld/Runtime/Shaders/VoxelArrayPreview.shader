Shader "VoxelWorld/ArrayPreview"

Variants
{
}

Properties
{
    _VoxelAtlas ("Atlas", Texture2DArray) = "white"
    _Slice ("Slice", Float) = 0
}

Pass "Preview"
{
    Tags { "RenderOrder" = "Opaque" }
    Cull Back

    GLSLPROGRAM
        Vertex
        {
            #include "XEngineCG"
            #include "VertexAttributes"

            out vec2 texCoord0;

            void main()
            {
                gl_Position = TransformClip(vertexPosition);
                texCoord0 = vertexTexCoord0;
            }
        }

        Fragment
        {
            layout (location = 0) out vec4 fragColor;

            in vec2 texCoord0;

            uniform sampler2DArray _VoxelAtlas;
            uniform float _Slice;

            void main()
            {
                fragColor = texture(_VoxelAtlas, vec3(texCoord0, _Slice));
            }
        }
    ENDGLSL

    SLANGPROGRAM
        Vertex
        {
            #include "XEngineCG"

            struct VSInput
            {
                float3 vertexPosition : POSITION;
                float2 vertexTexCoord0 : TEXCOORD0;
            };

            struct VSOutput
            {
                float4 position : SV_Position;
                float2 texCoord0 : TEXCOORD0;
            };

            VSOutput main(VSInput input)
            {
                VSOutput o;
                o.position = TransformClip(input.vertexPosition);
                o.texCoord0 = input.vertexTexCoord0;
                return o;
            }
        }

        Fragment
        {
            Texture2DArray _VoxelAtlas : register(t0);
            SamplerState _VoxelAtlasSampler : register(s0);

            cbuffer VoxelArrayPreview : register(b2)
            {
                float _Slice;
            };

            struct PSInput
            {
                float4 position : SV_Position;
                float2 texCoord0 : TEXCOORD0;
            };

            float4 main(PSInput input) : SV_Target
            {
                return _VoxelAtlas.Sample(_VoxelAtlasSampler, float3(input.texCoord0, _Slice));
            }
        }
    ENDSLANG
}
