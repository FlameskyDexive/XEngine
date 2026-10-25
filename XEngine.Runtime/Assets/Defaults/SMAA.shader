Shader "Default/SMAA"

Variants
{
}

// Subpixel Morphological Anti-Aliasing (SMAA 1x), luma edge detection.
// Three chained fullscreen passes driven by SMAAEffect:
//   Pass 0 "EdgeDetection"    : scene color -> edges (rg)
//   Pass 1 "BlendWeights"     : edges + AreaTex + SearchTex -> blend weights (rgba)
//   Pass 2 "NeighborhoodBlend": scene color + weights -> antialiased color
// The heavy lifting is the upstream reference SMAA, pulled in via `#include "SMAA"`.
// Quality is HIGH (16 search steps, diagonal + corner detection); the edge
// threshold is driven live from the _EdgeThreshold uniform.

Properties
{
}

Pass "EdgeDetection"
{
    Tags { "RenderOrder" = "Opaque" }

    Blend Off
    Cull None
    ZTest Off
    ZWrite Off

    GLSLPROGRAM

    Vertex
    {
        layout (location = 0) in vec3 vertexPosition;
        layout (location = 1) in vec2 vertexTexCoord;

        out vec2 TexCoords;
        out vec4 offset[3];

        uniform vec2 _Resolution;

        #define SMAA_RT_METRICS vec4(1.0 / _Resolution.x, 1.0 / _Resolution.y, _Resolution.x, _Resolution.y)
        #define SMAA_GLSL_4
        #define SMAA_MAX_SEARCH_STEPS 16
        #define SMAA_MAX_SEARCH_STEPS_DIAG 8
        #define SMAA_CORNER_ROUNDING 25
        #define SMAA_INCLUDE_VS 1
        #define SMAA_INCLUDE_PS 0
        #include "SMAA"

        void main()
        {
            TexCoords = vertexTexCoord;
            gl_Position = vec4(vertexPosition, 1.0);

            vec4 o[3];
            SMAAEdgeDetectionVS(TexCoords, o);
            offset[0] = o[0];
            offset[1] = o[1];
            offset[2] = o[2];
        }
    }

    Fragment
    {
        layout(location = 0) out vec4 OutputColor;

        in vec2 TexCoords;
        in vec4 offset[3];

        uniform sampler2D _MainTex;
        uniform vec2 _Resolution;
        uniform float _EdgeThreshold;

        #define SMAA_RT_METRICS vec4(1.0 / _Resolution.x, 1.0 / _Resolution.y, _Resolution.x, _Resolution.y)
        #define SMAA_GLSL_4
        #define SMAA_THRESHOLD _EdgeThreshold
        #define SMAA_MAX_SEARCH_STEPS 16
        #define SMAA_MAX_SEARCH_STEPS_DIAG 8
        #define SMAA_CORNER_ROUNDING 25
        #define SMAA_INCLUDE_VS 0
        #define SMAA_INCLUDE_PS 1
        #include "SMAA"

        void main()
        {
            vec2 edges = SMAALumaEdgeDetectionPS(TexCoords, offset, _MainTex);
            OutputColor = vec4(edges, 0.0, 0.0);
        }
    }

    ENDGLSL

    SLANGPROGRAM

    Vertex
    {
        cbuffer SMAAPS : register(b0)
        {
            float2 _Resolution;
            float _EdgeThreshold;
            float Padding;
        };

        struct VSInput
        {
            float3 Position : POSITION;
            float2 TexCoord : TEXCOORD0;
        };

        struct VSOutput
        {
            float4 Position : SV_Position;
            float2 TexCoords : TEXCOORD0;
            float4 Offset0 : TEXCOORD1;
            float4 Offset1 : TEXCOORD2;
            float4 Offset2 : TEXCOORD3;
        };

        #define SMAA_RT_METRICS float4(1.0 / _Resolution.x, 1.0 / _Resolution.y, _Resolution.x, _Resolution.y)
        #define SMAA_HLSL_4
        #define SMAA_MAX_SEARCH_STEPS 16
        #define SMAA_MAX_SEARCH_STEPS_DIAG 8
        #define SMAA_CORNER_ROUNDING 25
        #define SMAA_INCLUDE_VS 1
        #define SMAA_INCLUDE_PS 0
        #include "SMAA"

        VSOutput main(VSInput input)
        {
            VSOutput output;
            output.Position = float4(input.Position, 1.0);
            output.TexCoords = input.TexCoord;
            float4 offsets[3];
            SMAAEdgeDetectionVS(output.TexCoords, offsets);
            output.Offset0 = offsets[0];
            output.Offset1 = offsets[1];
            output.Offset2 = offsets[2];
            return output;
        }
    }

    Fragment
    {
        cbuffer SMAAPS : register(b0)
        {
            float2 _Resolution;
            float _EdgeThreshold;
            float Padding;
        };

        Texture2D _MainTex : register(t0);

        struct PSInput
        {
            float2 TexCoords : TEXCOORD0;
            float4 Offset0 : TEXCOORD1;
            float4 Offset1 : TEXCOORD2;
            float4 Offset2 : TEXCOORD3;
        };

        #define SMAA_RT_METRICS float4(1.0 / _Resolution.x, 1.0 / _Resolution.y, _Resolution.x, _Resolution.y)
        #define SMAA_HLSL_4
        #define SMAA_THRESHOLD _EdgeThreshold
        #define SMAA_MAX_SEARCH_STEPS 16
        #define SMAA_MAX_SEARCH_STEPS_DIAG 8
        #define SMAA_CORNER_ROUNDING 25
        #define SMAA_INCLUDE_VS 0
        #define SMAA_INCLUDE_PS 1
        #include "SMAA"

        float4 main(PSInput input) : SV_Target
        {
            float4 offsets[3] = { input.Offset0, input.Offset1, input.Offset2 };
            float2 edges = SMAALumaEdgeDetectionPS(input.TexCoords, offsets, _MainTex);
            return float4(edges, 0.0, 0.0);
        }
    }

    ENDSLANG
}

Pass "BlendWeights"
{
    Tags { "RenderOrder" = "Opaque" }

    Blend Off
    Cull None
    ZTest Off
    ZWrite Off

    GLSLPROGRAM

    Vertex
    {
        layout (location = 0) in vec3 vertexPosition;
        layout (location = 1) in vec2 vertexTexCoord;

        out vec2 TexCoords;
        out vec2 pixcoord;
        out vec4 offset[3];

        uniform vec2 _Resolution;

        #define SMAA_RT_METRICS vec4(1.0 / _Resolution.x, 1.0 / _Resolution.y, _Resolution.x, _Resolution.y)
        #define SMAA_GLSL_4
        #define SMAA_MAX_SEARCH_STEPS 16
        #define SMAA_MAX_SEARCH_STEPS_DIAG 8
        #define SMAA_CORNER_ROUNDING 25
        #define SMAA_INCLUDE_VS 1
        #define SMAA_INCLUDE_PS 0
        #include "SMAA"

        void main()
        {
            TexCoords = vertexTexCoord;
            gl_Position = vec4(vertexPosition, 1.0);

            vec2 pc;
            vec4 o[3];
            SMAABlendingWeightCalculationVS(TexCoords, pc, o);
            pixcoord = pc;
            offset[0] = o[0];
            offset[1] = o[1];
            offset[2] = o[2];
        }
    }

    Fragment
    {
        layout(location = 0) out vec4 OutputColor;

        in vec2 TexCoords;
        in vec2 pixcoord;
        in vec4 offset[3];

        uniform sampler2D _MainTex;    // edges texture (bound by Blit source)
        uniform sampler2D _AreaTex;
        uniform sampler2D _SearchTex;
        uniform vec2 _Resolution;
        uniform float _EdgeThreshold;

        #define SMAA_RT_METRICS vec4(1.0 / _Resolution.x, 1.0 / _Resolution.y, _Resolution.x, _Resolution.y)
        #define SMAA_GLSL_4
        #define SMAA_THRESHOLD _EdgeThreshold
        #define SMAA_MAX_SEARCH_STEPS 16
        #define SMAA_MAX_SEARCH_STEPS_DIAG 8
        #define SMAA_CORNER_ROUNDING 25
        #define SMAA_INCLUDE_VS 0
        #define SMAA_INCLUDE_PS 1
        #include "SMAA"

        void main()
        {
            OutputColor = SMAABlendingWeightCalculationPS(
                TexCoords, pixcoord, offset,
                _MainTex, _AreaTex, _SearchTex, vec4(0.0));
        }
    }

    ENDGLSL

    SLANGPROGRAM

    Vertex
    {
        cbuffer SMAAPS : register(b0)
        {
            float2 _Resolution;
            float _EdgeThreshold;
            float Padding;
        };

        struct VSInput
        {
            float3 Position : POSITION;
            float2 TexCoord : TEXCOORD0;
        };

        struct VSOutput
        {
            float4 Position : SV_Position;
            float2 TexCoords : TEXCOORD0;
            float2 PixCoord : TEXCOORD1;
            float4 Offset0 : TEXCOORD2;
            float4 Offset1 : TEXCOORD3;
            float4 Offset2 : TEXCOORD4;
        };

        #define SMAA_RT_METRICS float4(1.0 / _Resolution.x, 1.0 / _Resolution.y, _Resolution.x, _Resolution.y)
        #define SMAA_HLSL_4
        #define SMAA_MAX_SEARCH_STEPS 16
        #define SMAA_MAX_SEARCH_STEPS_DIAG 8
        #define SMAA_CORNER_ROUNDING 25
        #define SMAA_INCLUDE_VS 1
        #define SMAA_INCLUDE_PS 0
        #include "SMAA"

        VSOutput main(VSInput input)
        {
            VSOutput output;
            output.Position = float4(input.Position, 1.0);
            output.TexCoords = input.TexCoord;
            float4 offsets[3];
            SMAABlendingWeightCalculationVS(output.TexCoords, output.PixCoord, offsets);
            output.Offset0 = offsets[0];
            output.Offset1 = offsets[1];
            output.Offset2 = offsets[2];
            return output;
        }
    }

    Fragment
    {
        cbuffer SMAAPS : register(b0)
        {
            float2 _Resolution;
            float _EdgeThreshold;
            float Padding;
        };

        Texture2D _MainTex : register(t0);
        Texture2D _AreaTex : register(t1);
        Texture2D _SearchTex : register(t2);

        struct PSInput
        {
            float2 TexCoords : TEXCOORD0;
            float2 PixCoord : TEXCOORD1;
            float4 Offset0 : TEXCOORD2;
            float4 Offset1 : TEXCOORD3;
            float4 Offset2 : TEXCOORD4;
        };

        #define SMAA_RT_METRICS float4(1.0 / _Resolution.x, 1.0 / _Resolution.y, _Resolution.x, _Resolution.y)
        #define SMAA_HLSL_4
        #define SMAA_THRESHOLD _EdgeThreshold
        #define SMAA_MAX_SEARCH_STEPS 16
        #define SMAA_MAX_SEARCH_STEPS_DIAG 8
        #define SMAA_CORNER_ROUNDING 25
        #define SMAA_INCLUDE_VS 0
        #define SMAA_INCLUDE_PS 1
        #include "SMAA"

        float4 main(PSInput input) : SV_Target
        {
            float4 offsets[3] = { input.Offset0, input.Offset1, input.Offset2 };
            return SMAABlendingWeightCalculationPS(
                input.TexCoords,
                input.PixCoord,
                offsets,
                _MainTex,
                _AreaTex,
                _SearchTex,
                float4(0.0, 0.0, 0.0, 0.0));
        }
    }

    ENDSLANG
}

Pass "NeighborhoodBlend"
{
    Tags { "RenderOrder" = "Opaque" }

    Blend Off
    Cull None
    ZTest Off
    ZWrite Off

    GLSLPROGRAM

    Vertex
    {
        layout (location = 0) in vec3 vertexPosition;
        layout (location = 1) in vec2 vertexTexCoord;

        out vec2 TexCoords;
        out vec4 offset;

        uniform vec2 _Resolution;

        #define SMAA_RT_METRICS vec4(1.0 / _Resolution.x, 1.0 / _Resolution.y, _Resolution.x, _Resolution.y)
        #define SMAA_GLSL_4
        #define SMAA_MAX_SEARCH_STEPS 16
        #define SMAA_MAX_SEARCH_STEPS_DIAG 8
        #define SMAA_CORNER_ROUNDING 25
        #define SMAA_INCLUDE_VS 1
        #define SMAA_INCLUDE_PS 0
        #include "SMAA"

        void main()
        {
            TexCoords = vertexTexCoord;
            gl_Position = vec4(vertexPosition, 1.0);

            vec4 o;
            SMAANeighborhoodBlendingVS(TexCoords, o);
            offset = o;
        }
    }

    Fragment
    {
        layout(location = 0) out vec4 OutputColor;

        in vec2 TexCoords;
        in vec4 offset;

        uniform sampler2D _MainTex;    // scene color (bound by Blit source)
        uniform sampler2D _BlendTex;   // blend weights
        uniform vec2 _Resolution;

        #define SMAA_RT_METRICS vec4(1.0 / _Resolution.x, 1.0 / _Resolution.y, _Resolution.x, _Resolution.y)
        #define SMAA_GLSL_4
        #define SMAA_MAX_SEARCH_STEPS 16
        #define SMAA_MAX_SEARCH_STEPS_DIAG 8
        #define SMAA_CORNER_ROUNDING 25
        #define SMAA_INCLUDE_VS 0
        #define SMAA_INCLUDE_PS 1
        #include "SMAA"

        void main()
        {
            OutputColor = SMAANeighborhoodBlendingPS(TexCoords, offset, _MainTex, _BlendTex);
        }
    }

    ENDGLSL

    SLANGPROGRAM

    Vertex
    {
        cbuffer SMAAPS : register(b0)
        {
            float2 _Resolution;
            float _EdgeThreshold;
            float Padding;
        };

        struct VSInput
        {
            float3 Position : POSITION;
            float2 TexCoord : TEXCOORD0;
        };

        struct VSOutput
        {
            float4 Position : SV_Position;
            float2 TexCoords : TEXCOORD0;
            float4 Offset : TEXCOORD1;
        };

        #define SMAA_RT_METRICS float4(1.0 / _Resolution.x, 1.0 / _Resolution.y, _Resolution.x, _Resolution.y)
        #define SMAA_HLSL_4
        #define SMAA_MAX_SEARCH_STEPS 16
        #define SMAA_MAX_SEARCH_STEPS_DIAG 8
        #define SMAA_CORNER_ROUNDING 25
        #define SMAA_INCLUDE_VS 1
        #define SMAA_INCLUDE_PS 0
        #include "SMAA"

        VSOutput main(VSInput input)
        {
            VSOutput output;
            output.Position = float4(input.Position, 1.0);
            output.TexCoords = input.TexCoord;
            SMAANeighborhoodBlendingVS(output.TexCoords, output.Offset);
            return output;
        }
    }

    Fragment
    {
        cbuffer SMAAPS : register(b0)
        {
            float2 _Resolution;
            float _EdgeThreshold;
            float Padding;
        };

        Texture2D _MainTex : register(t0);
        Texture2D _BlendTex : register(t1);

        struct PSInput
        {
            float2 TexCoords : TEXCOORD0;
            float4 Offset : TEXCOORD1;
        };

        #define SMAA_RT_METRICS float4(1.0 / _Resolution.x, 1.0 / _Resolution.y, _Resolution.x, _Resolution.y)
        #define SMAA_HLSL_4
        #define SMAA_MAX_SEARCH_STEPS 16
        #define SMAA_MAX_SEARCH_STEPS_DIAG 8
        #define SMAA_CORNER_ROUNDING 25
        #define SMAA_INCLUDE_VS 0
        #define SMAA_INCLUDE_PS 1
        #include "SMAA"

        float4 main(PSInput input) : SV_Target
        {
            return SMAANeighborhoodBlendingPS(input.TexCoords, input.Offset, _MainTex, _BlendTex);
        }
    }

    ENDSLANG
}
