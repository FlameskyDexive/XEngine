Shader "Default/Bloom"

Variants
{
}

Pass "Threshold"
{
    Tags { "RenderOrder" = "Opaque" }

    Blend Off
    ZTest Off
    ZWrite Off
    Cull Off

    GLSLPROGRAM

    Vertex
    {
        layout (location = 0) in vec3 vertexPosition;
        layout (location = 1) in vec2 vertexTexCoord;

        out vec2 TexCoords;

        void main()
        {
            TexCoords = vertexTexCoord;
            gl_Position = vec4(vertexPosition, 1.0);
        }
    }

    Fragment
    {
        #include "XEngineCG"

        uniform sampler2D _MainTex;
        uniform float _Threshold;

        layout(location = 0) out vec4 FragColor;

        in vec2 TexCoords;

        void main()
        {
            vec4 base = texture(_MainTex, TexCoords);
            vec3 color = base.rgb;

            float luminance = dot(color, vec3(0.2126, 0.7152, 0.0722));
            float contribution = max(0.0, luminance - _Threshold);
            contribution /= max(luminance, 0.00001);

            FragColor = vec4(color * contribution, base.a);
        }
    }

    ENDGLSL

    SLANGPROGRAM

    Vertex
    {
        struct VSInput
        {
            float3 vertexPosition : POSITION;
            float2 vertexTexCoord : TEXCOORD0;
        };

        struct VSOutput
        {
            float4 position : SV_Position;
            float2 TexCoords : TEXCOORD0;
        };

        VSOutput main(VSInput input)
        {
            VSOutput output;
            output.TexCoords = input.vertexTexCoord;
            output.position = float4(input.vertexPosition, 1.0);
            return output;
        }
    }

    Fragment
    {
        struct PSInput
        {
            float4 position : SV_Position;
            float2 TexCoords : TEXCOORD0;
        };

        cbuffer BloomThresholdPS : register(b0)
        {
            float _Threshold;
            float3 _BloomThresholdPadding;
        };

        Texture2D _MainTex : register(t0);
        SamplerState _MainTexSampler : register(s0);

        float4 main(PSInput input) : SV_Target
        {
            float4 baseColor = _MainTex.Sample(_MainTexSampler, input.TexCoords);
            float luminance = dot(baseColor.rgb, float3(0.2126, 0.7152, 0.0722));
            float contribution = max(0.0, luminance - _Threshold);
            contribution /= max(luminance, 0.00001);
            return float4(baseColor.rgb * contribution, baseColor.a);
        }
    }

    ENDSLANG
}

Pass "Downsample"
{
    Blend Off
    ZTest Off
    ZWrite Off
    Cull Off

    GLSLPROGRAM

    Vertex
    {
        layout (location = 0) in vec3 vertexPosition;
        layout (location = 1) in vec2 vertexTexCoord;

        out vec2 TexCoords;

        void main()
        {
            TexCoords = vertexTexCoord;
            gl_Position = vec4(vertexPosition, 1.0);
        }
    }

    Fragment
    {
        #include "XEngineCG"

        uniform sampler2D _MainTex;

        layout(location = 0) out vec4 FragColor;

        in vec2 TexCoords;

        void main()
        {
            vec2 halfpixel = 0.5 / vec2(textureSize(_MainTex, 0));

            vec4 sum = texture(_MainTex, TexCoords) * 4.0;
            sum += texture(_MainTex, TexCoords - halfpixel);
            sum += texture(_MainTex, TexCoords + halfpixel);
            sum += texture(_MainTex, TexCoords + vec2(halfpixel.x, -halfpixel.y));
            sum += texture(_MainTex, TexCoords - vec2(halfpixel.x, -halfpixel.y));

            FragColor = sum / 8.0;
        }
    }

    ENDGLSL

    SLANGPROGRAM

    Vertex
    {
        struct VSInput
        {
            float3 vertexPosition : POSITION;
            float2 vertexTexCoord : TEXCOORD0;
        };

        struct VSOutput
        {
            float4 position : SV_Position;
            float2 TexCoords : TEXCOORD0;
        };

        VSOutput main(VSInput input)
        {
            VSOutput output;
            output.TexCoords = input.vertexTexCoord;
            output.position = float4(input.vertexPosition, 1.0);
            return output;
        }
    }

    Fragment
    {
        struct PSInput
        {
            float4 position : SV_Position;
            float2 TexCoords : TEXCOORD0;
        };

        Texture2D _MainTex : register(t0);
        SamplerState _MainTexSampler : register(s0);

        float4 main(PSInput input) : SV_Target
        {
            uint width;
            uint height;
            _MainTex.GetDimensions(width, height);
            float2 halfpixel = 0.5 / float2(width, height);

            float4 sum = _MainTex.Sample(_MainTexSampler, input.TexCoords) * 4.0;
            sum += _MainTex.Sample(_MainTexSampler, input.TexCoords - halfpixel);
            sum += _MainTex.Sample(_MainTexSampler, input.TexCoords + halfpixel);
            sum += _MainTex.Sample(_MainTexSampler, input.TexCoords + float2(halfpixel.x, -halfpixel.y));
            sum += _MainTex.Sample(_MainTexSampler, input.TexCoords - float2(halfpixel.x, -halfpixel.y));
            return sum / 8.0;
        }
    }

    ENDSLANG
}

Pass "Upsample"
{
    Blend Additive
    ZTest Off
    ZWrite Off
    Cull Off

    GLSLPROGRAM

    Vertex
    {
        layout (location = 0) in vec3 vertexPosition;
        layout (location = 1) in vec2 vertexTexCoord;

        out vec2 TexCoords;

        void main()
        {
            TexCoords = vertexTexCoord;
            gl_Position = vec4(vertexPosition, 1.0);
        }
    }

    Fragment
    {
        #include "XEngineCG"

        uniform sampler2D _MainTex;

        layout(location = 0) out vec4 FragColor;

        in vec2 TexCoords;

        void main()
        {
            vec2 halfpixel = 0.5 / vec2(textureSize(_MainTex, 0));

            vec4 sum = texture(_MainTex, TexCoords + vec2(-halfpixel.x * 2.0, 0.0));
            sum += texture(_MainTex, TexCoords + vec2(-halfpixel.x, halfpixel.y)) * 2.0;
            sum += texture(_MainTex, TexCoords + vec2(0.0, halfpixel.y * 2.0));
            sum += texture(_MainTex, TexCoords + vec2(halfpixel.x, halfpixel.y)) * 2.0;
            sum += texture(_MainTex, TexCoords + vec2(halfpixel.x * 2.0, 0.0));
            sum += texture(_MainTex, TexCoords + vec2(halfpixel.x, -halfpixel.y)) * 2.0;
            sum += texture(_MainTex, TexCoords + vec2(0.0, -halfpixel.y * 2.0));
            sum += texture(_MainTex, TexCoords + vec2(-halfpixel.x, -halfpixel.y)) * 2.0;

            FragColor = sum / 12.0;
        }
    }

    ENDGLSL

    SLANGPROGRAM

    Vertex
    {
        struct VSInput
        {
            float3 vertexPosition : POSITION;
            float2 vertexTexCoord : TEXCOORD0;
        };

        struct VSOutput
        {
            float4 position : SV_Position;
            float2 TexCoords : TEXCOORD0;
        };

        VSOutput main(VSInput input)
        {
            VSOutput output;
            output.TexCoords = input.vertexTexCoord;
            output.position = float4(input.vertexPosition, 1.0);
            return output;
        }
    }

    Fragment
    {
        struct PSInput
        {
            float4 position : SV_Position;
            float2 TexCoords : TEXCOORD0;
        };

        Texture2D _MainTex : register(t0);
        SamplerState _MainTexSampler : register(s0);

        float4 main(PSInput input) : SV_Target
        {
            uint width;
            uint height;
            _MainTex.GetDimensions(width, height);
            float2 halfpixel = 0.5 / float2(width, height);

            float4 sum = _MainTex.Sample(_MainTexSampler, input.TexCoords + float2(-halfpixel.x * 2.0, 0.0));
            sum += _MainTex.Sample(_MainTexSampler, input.TexCoords + float2(-halfpixel.x, halfpixel.y)) * 2.0;
            sum += _MainTex.Sample(_MainTexSampler, input.TexCoords + float2(0.0, halfpixel.y * 2.0));
            sum += _MainTex.Sample(_MainTexSampler, input.TexCoords + float2(halfpixel.x, halfpixel.y)) * 2.0;
            sum += _MainTex.Sample(_MainTexSampler, input.TexCoords + float2(halfpixel.x * 2.0, 0.0));
            sum += _MainTex.Sample(_MainTexSampler, input.TexCoords + float2(halfpixel.x, -halfpixel.y)) * 2.0;
            sum += _MainTex.Sample(_MainTexSampler, input.TexCoords + float2(0.0, -halfpixel.y * 2.0));
            sum += _MainTex.Sample(_MainTexSampler, input.TexCoords + float2(-halfpixel.x, -halfpixel.y)) * 2.0;
            return sum / 12.0;
        }
    }

    ENDSLANG
}

Pass "Composite"
{
    Blend Off
    ZTest Off
    ZWrite Off
    Cull Off

    GLSLPROGRAM

    Vertex
    {
        layout (location = 0) in vec3 vertexPosition;
        layout (location = 1) in vec2 vertexTexCoord;

        out vec2 TexCoords;

        void main()
        {
            TexCoords = vertexTexCoord;
            gl_Position = vec4(vertexPosition, 1.0);
        }
    }

    Fragment
    {
        #include "XEngineCG"

        uniform sampler2D _MainTex;
        uniform sampler2D _BloomTex;
        uniform float _Intensity;

        layout(location = 0) out vec4 FragColor;

        in vec2 TexCoords;

        void main()
        {
            vec4 originalColor = texture(_MainTex, TexCoords);
            vec3 bloomColor = texture(_BloomTex, TexCoords).rgb;

            vec3 finalColor = originalColor.rgb + bloomColor * _Intensity;

            FragColor = vec4(finalColor, originalColor.a);
        }
    }

    ENDGLSL

    SLANGPROGRAM

    Vertex
    {
        struct VSInput
        {
            float3 vertexPosition : POSITION;
            float2 vertexTexCoord : TEXCOORD0;
        };

        struct VSOutput
        {
            float4 position : SV_Position;
            float2 TexCoords : TEXCOORD0;
        };

        VSOutput main(VSInput input)
        {
            VSOutput output;
            output.TexCoords = input.vertexTexCoord;
            output.position = float4(input.vertexPosition, 1.0);
            return output;
        }
    }

    Fragment
    {
        struct PSInput
        {
            float4 position : SV_Position;
            float2 TexCoords : TEXCOORD0;
        };

        cbuffer BloomCompositePS : register(b0)
        {
            float _Intensity;
            float3 _BloomCompositePadding;
        };

        Texture2D _MainTex : register(t0);
        SamplerState _MainTexSampler : register(s0);
        Texture2D _BloomTex : register(t1);
        SamplerState _BloomTexSampler : register(s1);

        float4 main(PSInput input) : SV_Target
        {
            float4 originalColor = _MainTex.Sample(_MainTexSampler, input.TexCoords);
            float3 bloomColor = _BloomTex.Sample(_BloomTexSampler, input.TexCoords).rgb;
            return float4(originalColor.rgb + bloomColor * _Intensity, originalColor.a);
        }
    }

    ENDSLANG
}
