Shader "Default/AutoExposure"

Variants
{
}

// Pass 0: Extract log-luminance from HDR scene and downsample to half-res
Pass "LuminanceExtract"
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
            vec2 texelSize = 1.0 / vec2(textureSize(_MainTex, 0));

            // 4-tap box filter at half resolution
            vec3 c0 = texture(_MainTex, TexCoords + vec2(-0.5, -0.5) * texelSize).rgb;
            vec3 c1 = texture(_MainTex, TexCoords + vec2( 0.5, -0.5) * texelSize).rgb;
            vec3 c2 = texture(_MainTex, TexCoords + vec2(-0.5,  0.5) * texelSize).rgb;
            vec3 c3 = texture(_MainTex, TexCoords + vec2( 0.5,  0.5) * texelSize).rgb;

            // Compute log-luminance for each sample (log of geometric mean)
            float l0 = log(max(luminance(c0), 0.0001));
            float l1 = log(max(luminance(c1), 0.0001));
            float l2 = log(max(luminance(c2), 0.0001));
            float l3 = log(max(luminance(c3), 0.0001));

            float avgLogLum = (l0 + l1 + l2 + l3) * 0.25;
            FragColor = vec4(avgLogLum, 0.0, 0.0, 1.0);
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

        float Luminance(float3 color)
        {
            return dot(color, float3(0.2126, 0.7152, 0.0722));
        }

        float4 main(PSInput input) : SV_Target
        {
            uint width;
            uint height;
            _MainTex.GetDimensions(width, height);
            float2 texelSize = 1.0 / float2(width, height);
            float3 color0 = _MainTex.Sample(_MainTexSampler, input.TexCoords + float2(-0.5, -0.5) * texelSize).rgb;
            float3 color1 = _MainTex.Sample(_MainTexSampler, input.TexCoords + float2(0.5, -0.5) * texelSize).rgb;
            float3 color2 = _MainTex.Sample(_MainTexSampler, input.TexCoords + float2(-0.5, 0.5) * texelSize).rgb;
            float3 color3 = _MainTex.Sample(_MainTexSampler, input.TexCoords + float2(0.5, 0.5) * texelSize).rgb;
            float luminance0 = log(max(Luminance(color0), 0.0001));
            float luminance1 = log(max(Luminance(color1), 0.0001));
            float luminance2 = log(max(Luminance(color2), 0.0001));
            float luminance3 = log(max(Luminance(color3), 0.0001));
            return float4((luminance0 + luminance1 + luminance2 + luminance3) * 0.25, 0.0, 0.0, 1.0);
        }
    }

    ENDSLANG
}

// Pass 1: Downsample log-luminance (box filter, reused in chain)
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
        uniform sampler2D _MainTex;

        layout(location = 0) out vec4 FragColor;

        in vec2 TexCoords;

        void main()
        {
            vec2 texelSize = 1.0 / vec2(textureSize(_MainTex, 0));

            // 4-tap box filter
            float s0 = texture(_MainTex, TexCoords + vec2(-0.5, -0.5) * texelSize).r;
            float s1 = texture(_MainTex, TexCoords + vec2( 0.5, -0.5) * texelSize).r;
            float s2 = texture(_MainTex, TexCoords + vec2(-0.5,  0.5) * texelSize).r;
            float s3 = texture(_MainTex, TexCoords + vec2( 0.5,  0.5) * texelSize).r;

            FragColor = vec4((s0 + s1 + s2 + s3) * 0.25, 0.0, 0.0, 1.0);
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
            float2 texelSize = 1.0 / float2(width, height);
            float sample0 = _MainTex.Sample(_MainTexSampler, input.TexCoords + float2(-0.5, -0.5) * texelSize).r;
            float sample1 = _MainTex.Sample(_MainTexSampler, input.TexCoords + float2(0.5, -0.5) * texelSize).r;
            float sample2 = _MainTex.Sample(_MainTexSampler, input.TexCoords + float2(-0.5, 0.5) * texelSize).r;
            float sample3 = _MainTex.Sample(_MainTexSampler, input.TexCoords + float2(0.5, 0.5) * texelSize).r;
            return float4((sample0 + sample1 + sample2 + sample3) * 0.25, 0.0, 0.0, 1.0);
        }
    }

    ENDSLANG
}

// Pass 2: Temporal adaptation - smoothly blend current luminance toward measured value
Pass "Adapt"
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
        #include "ShaderVariables"

        uniform sampler2D _MainTex;      // Current measured log-luminance (1x1 or small)
        uniform sampler2D _AdaptedTex;   // Previous adapted luminance
        uniform float _AdaptSpeedUp;     // Speed when going brighter (EV/s)
        uniform float _AdaptSpeedDown;   // Speed when going darker (EV/s)
        uniform float _HistoryValid;     // 0.0 = first frame, snap to current

        layout(location = 0) out vec4 FragColor;

        in vec2 TexCoords;

        void main()
        {
            // Current geometric mean luminance from downsample chain
            float currentLogLum = texture(_MainTex, vec2(0.5)).r;
            float currentLum = exp(currentLogLum);

            // Previous adapted luminance
            float prevLum = texture(_AdaptedTex, vec2(0.5)).r;

            if (_HistoryValid < 0.5)
            {
                // First frame - snap to current
                FragColor = vec4(currentLum, 0.0, 0.0, 1.0);
                return;
            }

            // Asymmetric adaptation speed: faster going dark->bright, slower bright->dark
            // (or vice versa depending on user config)
            float speed = (currentLum > prevLum) ? _AdaptSpeedUp : _AdaptSpeedDown;

            float dt = xengine_DeltaTime.x;
            float adaptFactor = 1.0 - exp(-dt * speed);
            float adaptedLum = prevLum + (currentLum - prevLum) * adaptFactor;

            // Clamp to reasonable range to prevent extreme values
            adaptedLum = clamp(adaptedLum, 0.0001, 100.0);

            FragColor = vec4(adaptedLum, 0.0, 0.0, 1.0);
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
        #include "XEngineCG"

        struct PSInput
        {
            float4 position : SV_Position;
            float2 TexCoords : TEXCOORD0;
        };

        cbuffer AutoExposureAdaptPS : register(b2)
        {
            float _AdaptSpeedUp;
            float _AdaptSpeedDown;
            float _HistoryValid;
            float _AutoExposureAdaptPadding;
        };

        Texture2D _MainTex : register(t0);
        SamplerState _MainTexSampler : register(s0);
        Texture2D _AdaptedTex : register(t1);
        SamplerState _AdaptedTexSampler : register(s1);

        float4 main(PSInput input) : SV_Target
        {
            float currentLogLuminance = _MainTex.Sample(_MainTexSampler, float2(0.5, 0.5)).r;
            float currentLuminance = exp(currentLogLuminance);
            float previousLuminance = _AdaptedTex.Sample(_AdaptedTexSampler, float2(0.5, 0.5)).r;
            if (_HistoryValid < 0.5)
                return float4(currentLuminance, 0.0, 0.0, 1.0);

            float speed = currentLuminance > previousLuminance ? _AdaptSpeedUp : _AdaptSpeedDown;
            float adaptationFactor = 1.0 - exp(-xengine_DeltaTime.x * speed);
            float adaptedLuminance = previousLuminance + (currentLuminance - previousLuminance) * adaptationFactor;
            return float4(clamp(adaptedLuminance, 0.0001, 100.0), 0.0, 0.0, 1.0);
        }
    }

    ENDSLANG
}

// Pass 3: Apply exposure to HDR scene color
Pass "ApplyExposure"
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
        uniform sampler2D _MainTex;      // HDR scene color
        uniform sampler2D _AdaptedTex;   // 1x1 adapted luminance
        uniform float _ExposureComp;     // Exposure compensation in EV stops
        uniform float _MinExposure;      // Minimum exposure multiplier
        uniform float _MaxExposure;      // Maximum exposure multiplier

        layout(location = 0) out vec4 FragColor;

        in vec2 TexCoords;

        void main()
        {
            vec4 sceneColor = texture(_MainTex, TexCoords);
            float adaptedLum = texture(_AdaptedTex, vec2(0.5)).r;

            // Standard exposure formula: key / luminance
            // 0.18 is the standard "middle gray" key value
            float exposure = 0.18 / max(adaptedLum, 0.0001);

            // Apply EV compensation (each stop doubles/halves exposure)
            exposure *= exp2(_ExposureComp);

            // Clamp exposure to user-defined range
            exposure = clamp(exposure, _MinExposure, _MaxExposure);

            FragColor = vec4(sceneColor.rgb * exposure, sceneColor.a);
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

        cbuffer AutoExposureApplyPS : register(b0)
        {
            float _ExposureComp;
            float _MinExposure;
            float _MaxExposure;
            float _AutoExposureApplyPadding;
        };

        Texture2D _MainTex : register(t0);
        SamplerState _MainTexSampler : register(s0);
        Texture2D _AdaptedTex : register(t1);
        SamplerState _AdaptedTexSampler : register(s1);

        float4 main(PSInput input) : SV_Target
        {
            float4 sceneColor = _MainTex.Sample(_MainTexSampler, input.TexCoords);
            float adaptedLuminance = _AdaptedTex.Sample(_AdaptedTexSampler, float2(0.5, 0.5)).r;
            float exposure = 0.18 / max(adaptedLuminance, 0.0001);
            exposure *= exp2(_ExposureComp);
            exposure = clamp(exposure, _MinExposure, _MaxExposure);
            return float4(sceneColor.rgb * exposure, sceneColor.a);
        }
    }

    ENDSLANG
}
