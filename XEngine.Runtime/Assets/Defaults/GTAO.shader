Shader "Default/GTAO"

Variants
{
}

Properties
{
}

Pass "CalculateGTAO"
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
            gl_Position = vec4(vertexPosition, 1.0);
            TexCoords = vertexTexCoord;
        }
    }

    Fragment
    {
        #include "XEngineCG"

        uniform sampler2D _CameraDepthTexture;
        uniform sampler2D _CameraNormalsTexture; // View-space normals from depth pre-pass

        uniform int _Slices;
        uniform int _DirectionSamples;
        uniform float _Radius;
        uniform float _Intensity;
        uniform sampler2D _Noise;
        uniform vec2 _NoiseScale;   // ao-res / noise-res, tiles the blue noise 1:1 per pixel
        uniform vec2 _JitterOffset; // per-frame Halton offset so temporal accumulation converges

        in vec2 TexCoords;

        layout(location = 0) out vec4 aoOutput;

        void SampleHorizonCos(vec2 coord, vec2 offset, vec3 viewPos, vec3 viewDir, vec2 falloff, inout float cHorizonCos) {
            vec2 sTexCoord = coord + offset;

            // Check bounds
            if (sTexCoord.x < 0.0 || sTexCoord.x > 1.0 || sTexCoord.y < 0.0 || sTexCoord.y > 1.0)
                return;

            float sDepth = texture(_CameraDepthTexture, sTexCoord).r;
            if (sDepth >= 1.0) return;

            vec3 sHorizonV = getViewPos(sTexCoord, sDepth) - viewPos;

            float sLenV = sdot(sHorizonV);
            // Reject samples that reconstruct to essentially the same view position as the center.
            // On large flat / distant surfaces, depth-buffer precision makes neighbours collapse
            // together; the resulting near-zero horizon vector is pure quantization noise, not real
            // occlusion. Scale the threshold with view depth since depth precision worsens with distance.
            float minLen = 0.0015 * abs(viewPos.z);
            if (sLenV < minLen * minLen) return;
            float sNormV = inversesqrt(sLenV);

            float sHorizonCos = dot(sHorizonV, viewDir) * sNormV;
            sHorizonCos = mix(sHorizonCos, cHorizonCos, linearstep(falloff.x, falloff.y, sLenV));
            cHorizonCos = max(sHorizonCos, cHorizonCos);
        }

        float CalculateGTAO(vec2 coord, vec3 viewPos, vec3 normal, vec2 dither) {
            float viewDistance = sdot(viewPos);
            float norm = inversesqrt(viewDistance);
            viewDistance *= norm;

            vec3 viewDir = viewPos * -norm;

            int sliceCount = _Slices;
            float rSliceCount = 1.0 / float(sliceCount);

            int sampleCount = _DirectionSamples;
            float rSampleCount = 1.0 / float(sampleCount);

            float radius = _Radius * saturate(0.25 + viewDistance * rcp(64.0));
            vec2 sRadius = rSampleCount * radius * norm * diagonal2(XENGINE_MATRIX_P);
            // Floor the per-sample step so consecutive samples are at least ~1.5 texels apart in the
            // (full-res) depth buffer. Without this, distant/flat surfaces sample sub-texel and the
            // horizon estimate degenerates into depth-precision noise.
            sRadius = max(sRadius, vec2(1.5) / _ScreenParams.xy);
            vec2 falloff = sqr(radius * vec2(1.0, 4.0));

            float visibility = 0.0;

            for (int slice = 0; slice < sliceCount; ++slice) {
                float slicePhi = (float(slice) + dither.x) * (XENGINE_PI * rSliceCount);

                vec3 directionV = vec3(cos(slicePhi), sin(slicePhi), 0.0);
                vec3 orthoDirectionV = directionV - dot(directionV, viewDir) * viewDir;
                vec3 axisV = cross(directionV, viewDir);
                vec3 projNormalV = normal - axisV * dot(normal, axisV);

                float lenV = sdot(projNormalV);
                float normV = inversesqrt(lenV);
                lenV *= normV;

                float sgnN = fastSign(dot(orthoDirectionV, projNormalV));
                float cosN = saturate(dot(projNormalV, viewDir) * normV);
                float n = sgnN * fastAcos(cosN);

                vec2 cHorizonCos = vec2(-1.0);

                for (int samp = 0; samp < sampleCount; ++samp) {
                    vec2 stepDir = directionV.xy * sRadius;
                    vec2 offset = (float(samp) + dither.y) * stepDir;

                    SampleHorizonCos(coord, offset, viewPos, viewDir, falloff, cHorizonCos.x);
                    SampleHorizonCos(coord, -offset, viewPos, viewDir, falloff, cHorizonCos.y);
                }

                vec2 h = n + clamp(vec2(fastAcos(cHorizonCos.x), -fastAcos(cHorizonCos.y)) - n, -XENGINE_HALF_PI, XENGINE_HALF_PI);
                h = cosN + 2.0 * h * sin(n) - cos(2.0 * h - n);

                visibility += lenV * (h.x + h.y);
            }

            return 0.25 * rSliceCount * visibility;
        }

        vec3 ApproxMultiBounce(float ao, vec3 albedo) {
            vec3 a = 2.0404 * albedo - 0.3324;
            vec3 b = 4.7951 * albedo - 0.6417;
            vec3 c = 2.7552 * albedo + 0.6903;

            return max(vec3(ao), ((ao * a - b) * ao + c) * ao);
        }

        void main()
        {
            float depth = texture(_CameraDepthTexture, TexCoords).r;

            // Sky
            if (depth >= 1.0) {
                aoOutput = vec4(1.0);
                return;
            }

            // Get view space data
            vec3 viewPos = getViewPos(TexCoords, depth);

            // Get view space normal from GBuffer
            vec4 normalData = texture(_CameraNormalsTexture, TexCoords);
            vec3 viewNormal = normalize(normalData.xyz * 2.0 - 1.0);

            // Blue-noise dither (R channel of the built-in noise texture), scrolled each frame by a
            // Halton offset so temporal accumulation converges. .r drives the slice angle, .g the step.
            vec2 noise = texture(_Noise, TexCoords * _NoiseScale + _JitterOffset).rg;

            // Calculate GTAO
            float ao = CalculateGTAO(TexCoords, viewPos, viewNormal, noise);

            // Apply intensity
            ao = pow(saturate(ao), _Intensity);

            aoOutput = vec4(ao, ao, ao, 1.0);
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

        cbuffer GTAOCalculatePS : register(b2)
        {
            int _Slices;
            int _DirectionSamples;
            float _Radius;
            float _Intensity;
            float2 _NoiseScale;
            float2 _JitterOffset;
        };

        Texture2D _CameraDepthTexture : register(t0);
        SamplerState _CameraDepthSampler : register(s0);
        Texture2D _CameraNormalsTexture : register(t1);
        SamplerState _CameraNormalsSampler : register(s1);
        Texture2D _Noise : register(t2);
        SamplerState _NoiseSampler : register(s2);

        static const float GTAO_PI = 3.14159265359;
        static const float GTAO_HALF_PI = 1.57079632679;

        float Sdot(float3 value) { return dot(value, value); }
        float Sqr(float value) { return value * value; }
        float2 Sqr(float2 value) { return value * value; }
        float LinearStep(float a, float b, float value) { return saturate((value - a) / (b - a)); }
        float FastSign(float value) { return value >= 0.0 ? 1.0 : -1.0; }
        float FastAcos(float value)
        {
            float y = abs(value);
            float p = -0.0187293 * y + 0.0742610;
            p = p * y - 0.2121144;
            p = p * y + 1.5707288;
            p *= sqrt(1.0 - y);
            return value >= 0.0 ? p : GTAO_PI - p;
        }

        float3 ViewPosition(float2 uv, float depth)
        {
            float4 clip = float4(uv * 2.0 - 1.0, depth * 2.0 - 1.0, 1.0);
            float4 view = mul(xengine_MatIP, clip);
            return view.xyz / view.w;
        }

        void SampleHorizonCos(float2 coord, float2 offset, float3 viewPosition, float3 viewDirection, float2 falloff, inout float horizonCos)
        {
            float2 sampleUV = coord + offset;
            if (sampleUV.x < 0.0 || sampleUV.x > 1.0 || sampleUV.y < 0.0 || sampleUV.y > 1.0)
                return;
            float sampleDepth = _CameraDepthTexture.Sample(_CameraDepthSampler, sampleUV).r;
            if (sampleDepth >= 1.0)
                return;
            float3 horizonVector = ViewPosition(sampleUV, sampleDepth) - viewPosition;
            float horizonLength = Sdot(horizonVector);
            float minimumLength = 0.0015 * abs(viewPosition.z);
            if (horizonLength < minimumLength * minimumLength)
                return;
            float normalizedLength = rsqrt(horizonLength);
            float sampleHorizonCos = dot(horizonVector, viewDirection) * normalizedLength;
            sampleHorizonCos = lerp(sampleHorizonCos, horizonCos, LinearStep(falloff.x, falloff.y, horizonLength));
            horizonCos = max(sampleHorizonCos, horizonCos);
        }

        float CalculateGTAO(float2 coord, float3 viewPosition, float3 normal, float2 dither)
        {
            float viewDistance = Sdot(viewPosition);
            float normalizer = rsqrt(viewDistance);
            viewDistance *= normalizer;
            float3 viewDirection = -viewPosition * normalizer;
            int sliceCount = max(_Slices, 1);
            int sampleCount = max(_DirectionSamples, 1);
            float inverseSliceCount = 1.0 / float(sliceCount);
            float inverseSampleCount = 1.0 / float(sampleCount);
            float radius = _Radius * saturate(0.25 + viewDistance * (1.0 / 64.0));
            float2 projectionDiagonal = float2(xengine_MatP._m00, xengine_MatP._m11);
            float2 sampleRadius = inverseSampleCount * radius * normalizer * projectionDiagonal;
            sampleRadius = max(sampleRadius, 1.5 / _ScreenParams.xy);
            float2 falloff = Sqr(radius * float2(1.0, 4.0));
            float visibility = 0.0;

            for (int slice = 0; slice < sliceCount; slice++)
            {
                float slicePhi = (float(slice) + dither.x) * (GTAO_PI * inverseSliceCount);
                float3 direction = float3(cos(slicePhi), sin(slicePhi), 0.0);
                float3 orthogonalDirection = direction - dot(direction, viewDirection) * viewDirection;
                float3 axis = cross(direction, viewDirection);
                float3 projectedNormal = normal - axis * dot(normal, axis);
                float projectedLength = Sdot(projectedNormal);
                float projectedNormalizer = rsqrt(projectedLength);
                float normalizedLength = projectedLength * projectedNormalizer;
                float signedNormal = FastSign(dot(orthogonalDirection, projectedNormal));
                float normalCos = saturate(dot(projectedNormal, viewDirection) * projectedNormalizer);
                float normalAngle = signedNormal * FastAcos(normalCos);
                float2 horizonCos = float2(-1.0, -1.0);

                for (int sample = 0; sample < sampleCount; sample++)
                {
                    float2 step = direction.xy * sampleRadius;
                    float2 offset = (float(sample) + dither.y) * step;
                    SampleHorizonCos(coord, offset, viewPosition, viewDirection, falloff, horizonCos.x);
                    SampleHorizonCos(coord, -offset, viewPosition, viewDirection, falloff, horizonCos.y);
                }

                float2 horizon = normalAngle + clamp(float2(FastAcos(horizonCos.x), -FastAcos(horizonCos.y)) - normalAngle, -GTAO_HALF_PI, GTAO_HALF_PI);
                horizon = normalCos + 2.0 * horizon * sin(normalAngle) - cos(2.0 * horizon - normalAngle);
                visibility += normalizedLength * (horizon.x + horizon.y);
            }
            return 0.25 * inverseSliceCount * visibility;
        }

        float4 main(PSInput input) : SV_Target
        {
            float depth = _CameraDepthTexture.Sample(_CameraDepthSampler, input.TexCoords).r;
            if (depth >= 1.0)
                return float4(1.0, 1.0, 1.0, 1.0);
            float3 viewPosition = ViewPosition(input.TexCoords, depth);
            float3 normal = normalize(_CameraNormalsTexture.Sample(_CameraNormalsSampler, input.TexCoords).xyz * 2.0 - 1.0);
            float2 noise = _Noise.Sample(_NoiseSampler, input.TexCoords * _NoiseScale + _JitterOffset).rg;
            float ao = CalculateGTAO(input.TexCoords, viewPosition, normal, noise);
            ao = pow(saturate(ao), _Intensity);
            return float4(ao, ao, ao, 1.0);
        }
    }

    ENDSLANG
}

Pass "Blur"
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
            gl_Position = vec4(vertexPosition, 1.0);
            TexCoords = vertexTexCoord;
        }
    }

    Fragment
    {
        #include "XEngineCG"

        uniform sampler2D _MainTex;
        uniform sampler2D _CameraDepthTexture;
        uniform vec2 _BlurDirection;
        uniform float _BlurRadius;

        in vec2 TexCoords;

        layout(location = 0) out vec4 fragColor;

        void main()
        {
            vec2 texelSize = 1.0 / _ScreenParams.xy;
            float centerDepth = texture(_CameraDepthTexture, TexCoords).r;

            vec4 result = texture(_MainTex, TexCoords);
            float totalWeight = 1.0;

            // Depth-aware bilateral blur
            for (int i = -2; i <= 2; i++) {
                if (i == 0) continue;

                float offset = float(i) * _BlurRadius;
                vec2 sampleUV = TexCoords + _BlurDirection * texelSize * offset;

                float sampleDepth = texture(_CameraDepthTexture, sampleUV).r;
                float depthDiff = abs(centerDepth - sampleDepth);

                // Weight based on depth similarity
                float weight = exp(-depthDiff * 100.0) * exp(-0.5 * float(i * i) / 2.0);

                result += texture(_MainTex, sampleUV) * weight;
                totalWeight += weight;
            }

            fragColor = result / totalWeight;
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

        cbuffer GTAOBlurPS : register(b2)
        {
            float2 _BlurDirection;
            float _BlurRadius;
            float _Padding;
        };

        Texture2D _MainTex : register(t0);
        SamplerState _MainTexSampler : register(s0);
        Texture2D _CameraDepthTexture : register(t1);
        SamplerState _CameraDepthSampler : register(s1);

        float4 main(PSInput input) : SV_Target
        {
            float2 texelSize = 1.0 / _ScreenParams.xy;
            float centerDepth = _CameraDepthTexture.Sample(_CameraDepthSampler, input.TexCoords).r;
            float4 result = _MainTex.Sample(_MainTexSampler, input.TexCoords);
            float totalWeight = 1.0;

            for (int sample = -2; sample <= 2; sample++)
            {
                if (sample == 0)
                    continue;
                float offset = float(sample) * _BlurRadius;
                float2 sampleUV = input.TexCoords + _BlurDirection * texelSize * offset;
                float sampleDepth = _CameraDepthTexture.Sample(_CameraDepthSampler, sampleUV).r;
                float depthDifference = abs(centerDepth - sampleDepth);
                float weight = exp(-depthDifference * 100.0) * exp(-0.5 * float(sample * sample) / 2.0);
                result += _MainTex.Sample(_MainTexSampler, sampleUV) * weight;
                totalWeight += weight;
            }

            return result / totalWeight;
        }
    }

    ENDSLANG
}

Pass "Composite"
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
            gl_Position = vec4(vertexPosition, 1.0);
            TexCoords = vertexTexCoord;
        }
    }

    Fragment
    {
        #include "XEngineCG"

        uniform sampler2D _MainTex;
        uniform sampler2D _AOTex;
        uniform float _Intensity;

        in vec2 TexCoords;

        layout(location = 0) out vec4 fragColor;

        vec3 ApproxMultiBounce(float ao, vec3 albedo) {
            vec3 a = 2.0404 * albedo - 0.3324;
            vec3 b = 4.7951 * albedo - 0.6417;
            vec3 c = 2.7552 * albedo + 0.6903;

            return max(vec3(ao), ((ao * a - b) * ao + c) * ao);
        }

        void main()
        {
            vec4 sceneColor = texture(_MainTex, TexCoords);
            float ao = texture(_AOTex, TexCoords).r;

            vec3 finalColor = sceneColor.rgb;

            finalColor *= ao;

            fragColor = vec4(finalColor, sceneColor.a);
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

        Texture2D _MainTex : register(t0);
        SamplerState _MainTexSampler : register(s0);
        Texture2D _AOTex : register(t1);
        SamplerState _AOTexSampler : register(s1);

        float4 main(PSInput input) : SV_Target
        {
            float4 sceneColor = _MainTex.Sample(_MainTexSampler, input.TexCoords);
            float ao = _AOTex.Sample(_AOTexSampler, input.TexCoords).r;
            return float4(sceneColor.rgb * ao, sceneColor.a);
        }
    }

    ENDSLANG
}

Pass "Temporal"
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
            gl_Position = vec4(vertexPosition, 1.0);
            TexCoords = vertexTexCoord;
        }
    }

    Fragment
    {
        #include "XEngineCG"

        uniform sampler2D _MainTex;        // current-frame AO
        uniform sampler2D _PreviousBuffer; // accumulated AO history
        uniform sampler2D _CameraMotionVectorsTexture; // .rg motion
        uniform float _TResponse;

        in vec2 TexCoords;

        layout(location = 0) out vec4 fragColor;

        void main()
        {
            float current = texture(_MainTex, TexCoords).r;

            vec2 velocity = texture(_CameraMotionVectorsTexture, TexCoords).rg;
            vec2 prevUV = TexCoords - velocity;

            // Neighbourhood clamp: bound the history to the 3x3 range of the current AO so it can't
            // ghost where geometry/occlusion changed.
            vec2 texel = 1.0 / vec2(textureSize(_MainTex, 0));
            float mn = current, mx = current;
            for (int x = -1; x <= 1; x++)
            for (int y = -1; y <= 1; y++)
            {
                float s = texture(_MainTex, TexCoords + texel * vec2(float(x), float(y))).r;
                mn = min(mn, s); mx = max(mx, s);
            }

            float previous = clamp(texture(_PreviousBuffer, prevUV).r, mn, mx);

            // Drop history on disocclusion (reprojected off-screen).
            float response = (prevUV.x < 0.0 || prevUV.x > 1.0 || prevUV.y < 0.0 || prevUV.y > 1.0) ? 0.0 : _TResponse;

            float ao = mix(current, previous, response);
            fragColor = vec4(vec3(ao), 1.0);
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
            float2 texCoords : TEXCOORD0;
        };

        VSOutput main(VSInput input)
        {
            VSOutput output;
            output.position = float4(input.vertexPosition, 1.0);
            output.texCoords = input.vertexTexCoord;
            return output;
        }
    }

    Fragment
    {
        cbuffer GTAOTemporalPS : register(b2)
        {
            float _TResponse;
            float3 _GTAOTemporalPadding;
        };

        Texture2D _MainTex : register(t0);
        SamplerState _MainTexSampler : register(s0);
        Texture2D _PreviousBuffer : register(t1);
        SamplerState _PreviousBufferSampler : register(s1);
        Texture2D _CameraMotionVectorsTexture : register(t2);
        SamplerState _CameraMotionVectorsTextureSampler : register(s2);

        struct PSInput
        {
            float4 position : SV_Position;
            float2 texCoords : TEXCOORD0;
        };

        float4 main(PSInput input) : SV_Target
        {
            float current = _MainTex.Sample(_MainTexSampler, input.texCoords).r;
            float2 velocity = _CameraMotionVectorsTexture.Sample(_CameraMotionVectorsTextureSampler, input.texCoords).rg;
            float2 previousUv = input.texCoords - velocity;

            uint width;
            uint height;
            _MainTex.GetDimensions(width, height);
            float2 texel = 1.0 / float2(width, height);
            float minimum = current;
            float maximum = current;
            [unroll]
            for (int x = -1; x <= 1; x++)
            {
                [unroll]
                for (int y = -1; y <= 1; y++)
                {
                    float sampleValue = _MainTex.Sample(_MainTexSampler, input.texCoords + texel * float2(x, y)).r;
                    minimum = min(minimum, sampleValue);
                    maximum = max(maximum, sampleValue);
                }
            }

            float previous = clamp(_PreviousBuffer.Sample(_PreviousBufferSampler, previousUv).r, minimum, maximum);
            float response = (previousUv.x < 0.0 || previousUv.x > 1.0 || previousUv.y < 0.0 || previousUv.y > 1.0) ? 0.0 : _TResponse;
            float ao = lerp(current, previous, response);
            return float4(ao, ao, ao, 1.0);
        }
    }
    ENDSLANG
}
