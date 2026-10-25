Shader "Default/SSR"

Variants
{
}

// Stochastic screen-space reflections.
//   RayCast   : per-pixel GGX importance-sampled reflection ray -> hit UV + pdf + mask
//   SceneBlur : separable Gaussian, used to build a convolved scene-colour pyramid
//   Resolve   : gather neighbour rays, weight by BRDF/pdf, pick a pyramid level by a roughness
//               cone (contact hardening + glossy blur), firefly suppression, screen-edge fade
//   Temporal  : reproject + neighbourhood-clamp the reflection against a history buffer
//   Reproject : reproject a buffer by motion (used to feed last frame's result back in -> 1 bounce)
//   Combine   : Fresnel-weighted blend over the scene (albedo approximated from the tonemapped scene)
// Material data (per-pixel roughness/metallic) and view normals come from the unified prepass.

//------------------------------------------------------------------------------------------------
Pass "RayCast"
{
    Tags { "RenderOrder" = "Opaque" }
    Blend Override
    ZTest Off
    ZWrite Off
    Cull Off

    GLSLPROGRAM

    Vertex
    {
        layout (location = 0) in vec3 vertexPosition;
        layout (location = 1) in vec2 vertexTexCoord;
        out vec2 TexCoords;
        void main() { gl_Position = vec4(vertexPosition, 1.0); TexCoords = vertexTexCoord; }
    }

    Fragment
    {
        #include "XEngineCG"

        uniform sampler2D _CameraDepthTexture;
        uniform sampler2D _CameraNormalsTexture;
        uniform sampler2D _CameraMotionVectorsTexture; // .b roughness
        uniform sampler2D _Noise;
        uniform vec4 _JitterSizeAndOffset; // xy = rayUVsize/noiseSize, zw = per-frame offset
        uniform float _NumSteps;
        uniform float _BRDFBias;

        in vec2 TexCoords;
        layout(location = 0) out vec4 rayData; // xy = hit UV, z = pdf, w = mask

        // GGX microfacet half-vector importance sample (Karis / UE4). Returns half-vector + pdf.
        vec4 importanceSampleGGX(vec2 Xi, float roughness)
        {
            float m = roughness * roughness;
            float m2 = m * m;
            float phi = XENGINE_TWO_PI * Xi.x;
            float cosT = sqrt((1.0 - Xi.y) / (1.0 + (m2 - 1.0) * Xi.y));
            float sinT = sqrt(max(1e-5, 1.0 - cosT * cosT));
            vec3 h = vec3(sinT * cos(phi), sinT * sin(phi), cosT);
            float d = (cosT * m2 - cosT) * cosT + 1.0;
            float D = m2 / (XENGINE_PI * d * d);
            return vec4(h, D * cosT);
        }

        vec3 tangentToView(vec3 n, vec3 h)
        {
            vec3 up = abs(n.z) < 0.999 ? vec3(0.0, 0.0, 1.0) : vec3(1.0, 0.0, 0.0);
            vec3 t = normalize(cross(up, n));
            vec3 b = cross(n, t);
            return t * h.x + b * h.y + n * h.z;
        }

        vec3 projectToScreen(vec3 viewPos)
        {
            vec4 clip = XENGINE_MATRIX_P * vec4(viewPos, 1.0);
            clip.xyz /= clip.w;
            return vec3(clip.xy * 0.5 + 0.5, clip.z);
        }

        // March in screen space: step until the ray passes behind the depth buffer, then refine.
        bool trace(vec3 startScreen, vec3 reflViewDir, float jitter, out vec2 hitUV)
        {
            vec3 startView = getViewPos(startScreen.xy, startScreen.z);
            vec3 endScreen = projectToScreen(startView + reflViewDir * 100.0);
            vec3 delta = endScreen - startScreen;
            hitUV = startScreen.xy;
            if (length(delta.xy) < 1e-4) return false;

            float steps = min(max(abs(delta.x) * _ScreenParams.x, abs(delta.y) * _ScreenParams.y), _NumSteps);
            vec3 step = delta / max(steps, 1.0);
            vec3 cur = startScreen + step * (1.0 + jitter);

            bool crossed = false;
            for (float i = 1.0; i < steps; i += 1.0)
            {
                if (cur.x < 0.0 || cur.x > 1.0 || cur.y < 0.0 || cur.y > 1.0) return false;
                float sceneDepth = texture(_CameraDepthTexture, cur.xy).r;
                if (sceneDepth < 1.0 && cur.z > sceneDepth + 0.0001) { crossed = true; break; }
                cur += step;
            }
            if (!crossed) return false;

            vec3 lo = cur - step, hi = cur;
            for (int j = 0; j < 5; j++)
            {
                vec3 mid = (lo + hi) * 0.5;
                float d = texture(_CameraDepthTexture, mid.xy).r;
                if (mid.z > d) hi = mid; else lo = mid;
            }
            hitUV = hi.xy;
            return true;
        }

        void main()
        {
            float depth = texture(_CameraDepthTexture, TexCoords).r;
            if (depth >= 1.0) { rayData = vec4(0.0); return; }

            vec4 nd = texture(_CameraNormalsTexture, TexCoords);
            if (length(nd.xyz) < 0.01) { rayData = vec4(0.0); return; }
            vec3 viewNormal = normalize(nd.xyz * 2.0 - 1.0);
            float roughness = texture(_CameraMotionVectorsTexture, TexCoords).b;

            vec3 viewPos = getViewPos(TexCoords, depth);
            vec3 V = normalize(viewPos); // camera -> surface

            // Blue noise (R channel) for the stochastic sample, tiled + per-frame offset.
            vec2 Xi = texture(_Noise, (TexCoords + _JitterSizeAndOffset.zw) * _JitterSizeAndOffset.xy).rg;
            Xi.y = mix(Xi.y, 0.0, _BRDFBias); // bias toward the mirror direction

            vec4 H = importanceSampleGGX(Xi, roughness);
            vec3 h = tangentToView(viewNormal, H.xyz);
            vec3 reflDir = reflect(V, h);

            float jitter = (Xi.x + Xi.y) * (1.0 / max(_NumSteps, 1.0));

            vec2 hitUV;
            bool hit = trace(vec3(TexCoords, depth), reflDir, jitter, hitUV);

            rayData = hit ? vec4(hitUV, H.w, 1.0) : vec4(0.0);
        }
    }
    ENDGLSL

    SLANGPROGRAM

    Vertex
    {
        struct VSInput { float3 vertexPosition : POSITION; float2 vertexTexCoord : TEXCOORD0; };
        struct VSOutput { float4 position : SV_Position; float2 TexCoords : TEXCOORD0; };
        VSOutput main(VSInput input) { VSOutput output; output.TexCoords = input.vertexTexCoord; output.position = float4(input.vertexPosition, 1.0); return output; }
    }

    Fragment
    {
        #include "XEngineCG"
        struct PSInput { float4 position : SV_Position; float2 TexCoords : TEXCOORD0; };
        cbuffer SSRRayCastPS : register(b2) { float4 _JitterSizeAndOffset; float _NumSteps; float _BRDFBias; float2 _SSRRayCastPadding; };
        Texture2D _CameraDepthTexture : register(t0); SamplerState _CameraDepthSampler : register(s0);
        Texture2D _CameraNormalsTexture : register(t1); SamplerState _CameraNormalsSampler : register(s1);
        Texture2D _CameraMotionVectorsTexture : register(t2); SamplerState _CameraMotionVectorsSampler : register(s2);
        Texture2D _Noise : register(t3); SamplerState _NoiseSampler : register(s3);
        #define XENGINE_TWO_PI 6.28318530718
        #define XENGINE_PI 3.14159265359
        float3 GetViewPos(float2 uv, float depth) { float4 clip = float4(uv * 2.0 - 1.0, depth * 2.0 - 1.0, 1.0); float4 view = mul(xengine_MatIP, clip); return view.xyz / view.w; }
        float4 ImportanceSampleGGX(float2 Xi, float roughness) { float m = roughness * roughness; float m2 = m * m; float phi = XENGINE_TWO_PI * Xi.x; float cosT = sqrt((1.0 - Xi.y) / (1.0 + (m2 - 1.0) * Xi.y)); float sinT = sqrt(max(1e-5, 1.0 - cosT * cosT)); float3 h = float3(sinT * cos(phi), sinT * sin(phi), cosT); float d = (cosT * m2 - cosT) * cosT + 1.0; return float4(h, (m2 / (XENGINE_PI * d * d)) * cosT); }
        float3 TangentToView(float3 n, float3 h) { float3 up = abs(n.z) < 0.999 ? float3(0.0, 0.0, 1.0) : float3(1.0, 0.0, 0.0); float3 t = normalize(cross(up, n)); return t * h.x + cross(n, t) * h.y + n * h.z; }
        float3 ProjectToScreen(float3 viewPos) { float4 clip = mul(XENGINE_MATRIX_P, float4(viewPos, 1.0)); clip.xyz /= clip.w; return float3(clip.xy * 0.5 + 0.5, clip.z); }
        bool Trace(float3 startScreen, float3 reflViewDir, float jitter, out float2 hitUV)
        {
            float3 delta = ProjectToScreen(GetViewPos(startScreen.xy, startScreen.z) + reflViewDir * 100.0) - startScreen;
            hitUV = startScreen.xy;
            if (length(delta.xy) < 1e-4) return false;
            float stepsF = min(max(abs(delta.x) * _ScreenParams.x, abs(delta.y) * _ScreenParams.y), _NumSteps);
            int steps = (int)max(stepsF, 1.0); float3 stepSz = delta / max(stepsF, 1.0); float3 cur = startScreen + stepSz * (1.0 + jitter); bool crossed = false;
            [loop] for (int i = 1; i < steps; i++) { if (cur.x < 0.0 || cur.x > 1.0 || cur.y < 0.0 || cur.y > 1.0) return false; float sceneDepth = _CameraDepthTexture.Sample(_CameraDepthSampler, cur.xy).r; if (sceneDepth < 1.0 && cur.z > sceneDepth + 0.0001) { crossed = true; break; } cur += stepSz; }
            if (!crossed) return false;
            float3 lo = cur - stepSz, hi = cur;
            [unroll] for (int j = 0; j < 5; j++) { float3 mid = (lo + hi) * 0.5; if (mid.z > _CameraDepthTexture.Sample(_CameraDepthSampler, mid.xy).r) hi = mid; else lo = mid; }
            hitUV = hi.xy; return true;
        }
        float4 main(PSInput input) : SV_Target
        {
            float depth = _CameraDepthTexture.Sample(_CameraDepthSampler, input.TexCoords).r; if (depth >= 1.0) return 0.0;
            float4 nd = _CameraNormalsTexture.Sample(_CameraNormalsSampler, input.TexCoords); if (length(nd.xyz) < 0.01) return 0.0;
            float2 Xi = _Noise.Sample(_NoiseSampler, (input.TexCoords + _JitterSizeAndOffset.zw) * _JitterSizeAndOffset.xy).rg; Xi.y = lerp(Xi.y, 0.0, _BRDFBias);
            float4 H = ImportanceSampleGGX(Xi, _CameraMotionVectorsTexture.Sample(_CameraMotionVectorsSampler, input.TexCoords).b); float2 hitUV;
            return Trace(float3(input.TexCoords, depth), reflect(normalize(GetViewPos(input.TexCoords, depth)), TangentToView(normalize(nd.xyz * 2.0 - 1.0), H.xyz)), (Xi.x + Xi.y) / max(_NumSteps, 1.0), hitUV) ? float4(hitUV, H.w, 1.0) : 0.0;
        }
    }
    ENDSLANG
}

//------------------------------------------------------------------------------------------------
Pass "SceneBlur"
{
    Tags { "RenderOrder" = "Opaque" }
    Blend Off ZTest Off ZWrite Off Cull Off

    GLSLPROGRAM

    Vertex
    {
        layout (location = 0) in vec3 vertexPosition;
        layout (location = 1) in vec2 vertexTexCoord;
        out vec2 TexCoords;
        void main() { gl_Position = vec4(vertexPosition, 1.0); TexCoords = vertexTexCoord; }
    }

    Fragment
    {
        #include "XEngineCG"

        uniform sampler2D _MainTex;
        uniform vec2 _BlurDir; // (1,0) or (0,1)

        in vec2 TexCoords;
        layout(location = 0) out vec4 fragColor;

        void main()
        {
            // Separable 7-tap Gaussian over the source texel grid (downsamples on the V write).
            vec2 step = _BlurDir / vec2(textureSize(_MainTex, 0));
            vec4 c  = texture(_MainTex, TexCoords) * 0.474;
            c      += texture(_MainTex, TexCoords + step * 1.0) * 0.233;
            c      += texture(_MainTex, TexCoords - step * 1.0) * 0.233;
            c      += texture(_MainTex, TexCoords + step * 2.0) * 0.028;
            c      += texture(_MainTex, TexCoords - step * 2.0) * 0.028;
            c      += texture(_MainTex, TexCoords + step * 3.0) * 0.001;
            c      += texture(_MainTex, TexCoords - step * 3.0) * 0.001;
            fragColor = c;
        }
    }
    ENDGLSL

    SLANGPROGRAM
    Vertex
    {
        struct VSInput { float3 vertexPosition : POSITION; float2 vertexTexCoord : TEXCOORD0; };
        struct VSOutput { float4 position : SV_Position; float2 TexCoords : TEXCOORD0; };
        VSOutput main(VSInput input) { VSOutput output; output.TexCoords = input.vertexTexCoord; output.position = float4(input.vertexPosition, 1.0); return output; }
    }
    Fragment
    {
        struct PSInput { float4 position : SV_Position; float2 TexCoords : TEXCOORD0; };
        cbuffer SSRSceneBlurPS : register(b0) { float2 _BlurDir; float2 _SSRSceneBlurPadding; };
        Texture2D _MainTex : register(t0); SamplerState _MainTexSampler : register(s0);
        float4 main(PSInput input) : SV_Target
        {
            uint texW, texH; _MainTex.GetDimensions(texW, texH); float2 stepSz = _BlurDir / float2(texW, texH);
            float4 c = _MainTex.Sample(_MainTexSampler, input.TexCoords) * 0.474;
            c += _MainTex.Sample(_MainTexSampler, input.TexCoords + stepSz) * 0.233 + _MainTex.Sample(_MainTexSampler, input.TexCoords - stepSz) * 0.233;
            c += _MainTex.Sample(_MainTexSampler, input.TexCoords + stepSz * 2.0) * 0.028 + _MainTex.Sample(_MainTexSampler, input.TexCoords - stepSz * 2.0) * 0.028;
            return c + _MainTex.Sample(_MainTexSampler, input.TexCoords + stepSz * 3.0) * 0.001 + _MainTex.Sample(_MainTexSampler, input.TexCoords - stepSz * 3.0) * 0.001;
        }
    }
    ENDSLANG
}

//------------------------------------------------------------------------------------------------
Pass "Resolve"
{
    Tags { "RenderOrder" = "Opaque" }
    Blend Off ZTest Off ZWrite Off Cull Off

    GLSLPROGRAM

    Vertex
    {
        layout (location = 0) in vec3 vertexPosition;
        layout (location = 1) in vec2 vertexTexCoord;
        out vec2 TexCoords;
        void main() { gl_Position = vec4(vertexPosition, 1.0); TexCoords = vertexTexCoord; }
    }

    Fragment
    {
        #include "XEngineCG"

        uniform sampler2D _RayCast;
        uniform sampler2D _CameraDepthTexture;
        uniform sampler2D _CameraNormalsTexture;
        uniform sampler2D _CameraMotionVectorsTexture; // .b roughness
        uniform sampler2D _Noise;
        uniform vec2 _NoiseSize;
        uniform vec4 _JitterSizeAndOffset;
        uniform vec2 _ResolveSize;
        uniform float _BRDFBias;
        uniform float _EdgeFactor;
        uniform float _MaxMipMap;
        uniform int _RayReuse;
        uniform int _UseNormalization;
        uniform int _Fireflies;

        // Convolved scene-colour pyramid (level 0 = sharp ... level 4 = blurriest).
        uniform sampler2D _Scene0;
        uniform sampler2D _Scene1;
        uniform sampler2D _Scene2;
        uniform sampler2D _Scene3;
        uniform sampler2D _Scene4;

        in vec2 TexCoords;
        layout(location = 0) out vec4 fragColor;

        const vec2 resolveOffset[4] = vec2[]( vec2(0,0), vec2(2,-2), vec2(-2,-2), vec2(0,2) );

        vec3 samplePyramid(vec2 uv, float mip)
        {
            vec3 c = mix(texture(_Scene0, uv).rgb, texture(_Scene1, uv).rgb, clamp(mip, 0.0, 1.0));
            c = mix(c, texture(_Scene2, uv).rgb, clamp(mip - 1.0, 0.0, 1.0));
            c = mix(c, texture(_Scene3, uv).rgb, clamp(mip - 2.0, 0.0, 1.0));
            c = mix(c, texture(_Scene4, uv).rgb, clamp(mip - 3.0, 0.0, 1.0));
            return c;
        }

        // Smith-GGX visibility * GGX NDF * PI/4 -> the resolve weight (matched with 1/pdf).
        float brdfWeight(vec3 V, vec3 L, vec3 N, float roughness)
        {
            vec3 H = normalize(L + V);
            float NdotH = clamp(dot(N, H), 0.0, 1.0);
            float NdotL = clamp(dot(N, L), 0.0, 1.0);
            float NdotV = clamp(dot(N, V), 0.0, 1.0);
            float m = roughness * roughness;
            float m2 = m * m;
            float d = (NdotH * m2 - NdotH) * NdotH + 1.0;
            float D = m2 / (XENGINE_PI * d * d);
            float visL = NdotV * sqrt((NdotL - NdotL * m2) * NdotL + m2);
            float visV = NdotL * sqrt((NdotV - NdotV * m2) * NdotV + m2);
            float Vis = 0.5 / max(1e-5, visL + visV);
            return D * Vis * (XENGINE_PI / 4.0);
        }

        float edgeFade(vec2 pos, float value)
        {
            float d = min(1.0 - max(pos.x, pos.y), min(pos.x, pos.y));
            return clamp(value > 0.0 ? d / value : 1.0, 0.0, 1.0);
        }

        void main()
        {
            vec3 viewNormal = normalize(texture(_CameraNormalsTexture, TexCoords).xyz * 2.0 - 1.0);
            float roughness = texture(_CameraMotionVectorsTexture, TexCoords).b;
            float depth = texture(_CameraDepthTexture, TexCoords).r;
            vec3 viewPos = getViewPos(TexCoords, depth);
            vec3 V = normalize(-viewPos); // surface -> camera

            // Per-pixel rotation of the reuse offsets from blue noise.
            vec2 bn = texture(_Noise, (TexCoords + _JitterSizeAndOffset.zw) * _ScreenParams.xy / _NoiseSize).rg * 2.0 - 1.0;
            mat2 rot = mat2(bn.x, bn.y, -bn.y, bn.x);

            int numResolve = _RayReuse == 1 ? 4 : 1;
            float NdotV = clamp(dot(viewNormal, V), 0.0, 1.0);
            float coneTangent = mix(0.0, roughness * (1.0 - _BRDFBias), NdotV * sqrt(roughness));
            float maxMip = _MaxMipMap - 1.0;

            vec4 result = vec4(0.0);
            float weightSum = 0.0;
            for (int i = 0; i < numResolve; i++)
            {
                vec2 offset = rot * (resolveOffset[i] / _ResolveSize);
                vec2 nUV = TexCoords + offset;

                vec4 rd = texture(_RayCast, nUV);
                vec2 hitUV = rd.xy;
                float pdf = rd.z;
                float mask = rd.w;
                if (mask <= 0.0) continue;

                float hitDepth = texture(_CameraDepthTexture, hitUV).r;
                vec3 hitViewPos = getViewPos(hitUV, hitDepth);

                float weight = 1.0;
                if (_UseNormalization == 1)
                    weight = brdfWeight(V, normalize(hitViewPos - viewPos), viewNormal, roughness) / max(1e-5, pdf);

                float coneRadius = coneTangent * length(hitUV - TexCoords);
                float mip = clamp(log2(max(1e-5, coneRadius) * max(_ResolveSize.x, _ResolveSize.y)), 0.0, maxMip);

                vec4 s;
                s.rgb = samplePyramid(hitUV, mip);
                s.a = edgeFade(hitUV, _EdgeFactor) * mask;
                if (_Fireflies == 1) s.rgb /= 1.0 + luminance(s.rgb);

                result += s * weight;
                weightSum += weight;
            }

            result /= max(1e-5, weightSum);
            if (_Fireflies == 1)
            {
                // Bounded inverse of the per-sample tonemap: cap luminance so a very bright
                // reflection can't divide by ~0 and explode to white.
                float lum = min(luminance(result.rgb), 0.95);
                result.rgb /= 1.0 - lum;
            }
            fragColor = max(vec4(1e-5), result);
        }
    }
    ENDGLSL

    SLANGPROGRAM
    Vertex
    {
        struct VSInput { float3 vertexPosition : POSITION; float2 vertexTexCoord : TEXCOORD0; };
        struct VSOutput { float4 position : SV_Position; float2 TexCoords : TEXCOORD0; };
        VSOutput main(VSInput input) { VSOutput output; output.TexCoords = input.vertexTexCoord; output.position = float4(input.vertexPosition, 1.0); return output; }
    }
    Fragment
    {
        #include "XEngineCG"
        struct PSInput { float4 position : SV_Position; float2 TexCoords : TEXCOORD0; };
        cbuffer SSRResolvePS : register(b2)
        {
            float2 _NoiseSize; float2 _ResolveSize; float4 _JitterSizeAndOffset;
            float _BRDFBias; float _EdgeFactor; float _MaxMipMap; int _RayReuse;
            int _UseNormalization; int _Fireflies; float _SSRResolvePadding;
        };
        Texture2D _RayCast : register(t0); SamplerState _RayCastSampler : register(s0);
        Texture2D _CameraDepthTexture : register(t1); SamplerState _CameraDepthSampler : register(s1);
        Texture2D _CameraNormalsTexture : register(t2); SamplerState _CameraNormalsSampler : register(s2);
        Texture2D _CameraMotionVectorsTexture : register(t3); SamplerState _CameraMotionVectorsSampler : register(s3);
        Texture2D _Noise : register(t4); SamplerState _NoiseSampler : register(s4);
        Texture2D _Scene0 : register(t5); SamplerState _Scene0Sampler : register(s5);
        Texture2D _Scene1 : register(t6); SamplerState _Scene1Sampler : register(s6);
        Texture2D _Scene2 : register(t7); SamplerState _Scene2Sampler : register(s7);
        Texture2D _Scene3 : register(t8); SamplerState _Scene3Sampler : register(s8);
        Texture2D _Scene4 : register(t9); SamplerState _Scene4Sampler : register(s9);
        #define XENGINE_PI 3.14159265359
        float Luminance(float3 color) { return dot(color, float3(0.2126, 0.7152, 0.0722)); }
        float3 GetViewPos(float2 uv, float depth) { float4 clip = float4(uv * 2.0 - 1.0, depth * 2.0 - 1.0, 1.0); float4 view = mul(xengine_MatIP, clip); return view.xyz / view.w; }
        float3 SamplePyramid(float2 uv, float mip)
        {
            if (mip < 1.0) return lerp(_Scene0.Sample(_Scene0Sampler, uv).rgb, _Scene1.Sample(_Scene1Sampler, uv).rgb, mip);
            if (mip < 2.0) return lerp(_Scene1.Sample(_Scene1Sampler, uv).rgb, _Scene2.Sample(_Scene2Sampler, uv).rgb, mip - 1.0);
            if (mip < 3.0) return lerp(_Scene2.Sample(_Scene2Sampler, uv).rgb, _Scene3.Sample(_Scene3Sampler, uv).rgb, mip - 2.0);
            return lerp(_Scene3.Sample(_Scene3Sampler, uv).rgb, _Scene4.Sample(_Scene4Sampler, uv).rgb, saturate(mip - 3.0));
        }
        float BrdfWeight(float3 V, float3 L, float3 N, float roughness)
        {
            float3 H = normalize(L + V); float NdotH = saturate(dot(N, H)); float NdotL = saturate(dot(N, L)); float NdotV = saturate(dot(N, V));
            float m = roughness * roughness; float m2 = m * m; float d = (NdotH * m2 - NdotH) * NdotH + 1.0; float D = m2 / (XENGINE_PI * d * d);
            float visL = NdotV * sqrt((NdotL - NdotL * m2) * NdotL + m2); float visV = NdotL * sqrt((NdotV - NdotV * m2) * NdotV + m2);
            return D * (0.5 / max(1e-5, visL + visV)) * (XENGINE_PI / 4.0);
        }
        float EdgeFade(float2 pos, float value) { float d = min(1.0 - max(pos.x, pos.y), min(pos.x, pos.y)); return saturate(value > 0.0 ? d / value : 1.0); }
        float2 RotateOffset(float2 offset, float2 rotation) { return float2(rotation.x * offset.x - rotation.y * offset.y, rotation.y * offset.x + rotation.x * offset.y); }
        void AccumulateRay(float2 resolveUV, float2 rayUV, float3 viewPosition, float3 viewDirection, float3 viewNormal, float roughness, float coneTangent, float maxMip, inout float4 result, inout float weightSum)
        {
            float4 rayData = _RayCast.Sample(_RayCastSampler, rayUV); if (rayData.w <= 0.0) return;
            float3 hitViewPosition = GetViewPos(rayData.xy, _CameraDepthTexture.Sample(_CameraDepthSampler, rayData.xy).r); float weight = 1.0;
            if (_UseNormalization == 1) weight = BrdfWeight(viewDirection, normalize(hitViewPosition - viewPosition), viewNormal, roughness) / max(1e-5, rayData.z);
            float coneRadius = coneTangent * length(rayData.xy - resolveUV); float mip = clamp(log2(max(1e-5, coneRadius) * max(_ResolveSize.x, _ResolveSize.y)), 0.0, maxMip);
            float4 sampleColor = float4(SamplePyramid(rayData.xy, mip), EdgeFade(rayData.xy, _EdgeFactor) * rayData.w);
            if (_Fireflies == 1) sampleColor.rgb /= 1.0 + Luminance(sampleColor.rgb);
            result += sampleColor * weight; weightSum += weight;
        }
        float4 main(PSInput input) : SV_Target
        {
            float3 viewNormal = normalize(_CameraNormalsTexture.Sample(_CameraNormalsSampler, input.TexCoords).xyz * 2.0 - 1.0);
            float roughness = _CameraMotionVectorsTexture.Sample(_CameraMotionVectorsSampler, input.TexCoords).b; float3 viewPosition = GetViewPos(input.TexCoords, _CameraDepthTexture.Sample(_CameraDepthSampler, input.TexCoords).r); float3 viewDirection = normalize(-viewPosition);
            float2 blueNoise = _Noise.Sample(_NoiseSampler, (input.TexCoords + _JitterSizeAndOffset.zw) * _ScreenParams.xy / _NoiseSize).rg * 2.0 - 1.0;
            float coneTangent = lerp(0.0, roughness * (1.0 - _BRDFBias), saturate(dot(viewNormal, viewDirection)) * sqrt(roughness)); float4 result = 0.0; float weightSum = 0.0;
            AccumulateRay(input.TexCoords, input.TexCoords, viewPosition, viewDirection, viewNormal, roughness, coneTangent, _MaxMipMap - 1.0, result, weightSum);
            if (_RayReuse == 1)
            {
                float2 inverseSize = 1.0 / _ResolveSize;
                AccumulateRay(input.TexCoords, input.TexCoords + RotateOffset(float2(2.0, -2.0) * inverseSize, blueNoise), viewPosition, viewDirection, viewNormal, roughness, coneTangent, _MaxMipMap - 1.0, result, weightSum);
                AccumulateRay(input.TexCoords, input.TexCoords + RotateOffset(float2(-2.0, -2.0) * inverseSize, blueNoise), viewPosition, viewDirection, viewNormal, roughness, coneTangent, _MaxMipMap - 1.0, result, weightSum);
                AccumulateRay(input.TexCoords, input.TexCoords + RotateOffset(float2(0.0, 2.0) * inverseSize, blueNoise), viewPosition, viewDirection, viewNormal, roughness, coneTangent, _MaxMipMap - 1.0, result, weightSum);
            }
            result /= max(1e-5, weightSum); if (_Fireflies == 1) result.rgb /= 1.0 - min(Luminance(result.rgb), 0.95); return max(float4(1e-5, 1e-5, 1e-5, 1e-5), result);
        }
    }
    ENDSLANG
}

//------------------------------------------------------------------------------------------------
Pass "Temporal"
{
    Tags { "RenderOrder" = "Opaque" }
    Blend Off ZTest Off ZWrite Off Cull Off

    GLSLPROGRAM

    Vertex
    {
        layout (location = 0) in vec3 vertexPosition;
        layout (location = 1) in vec2 vertexTexCoord;
        out vec2 TexCoords;
        void main() { gl_Position = vec4(vertexPosition, 1.0); TexCoords = vertexTexCoord; }
    }

    Fragment
    {
        #include "XEngineCG"

        uniform sampler2D _MainTex;        // current resolved reflection
        uniform sampler2D _PreviousBuffer; // reflection history
        uniform sampler2D _RayCast;
        uniform sampler2D _CameraDepthTexture;
        uniform sampler2D _CameraMotionVectorsTexture; // .rg motion
        uniform int _ReflectionVelocity;  // 1 = derive velocity from reflection hit depth
        uniform float _TScale;
        uniform float _TResponse;

        in vec2 TexCoords;
        layout(location = 0) out vec4 fragColor;

        // Reproject this pixel through the previous view-projection to get screen-space motion.
        vec2 reflectionVelocity(vec2 uv)
        {
            // Use the reflection hit point's depth so the history follows the reflected geometry.
            float hitDepthW = texture(_RayCast, uv).w > 0.0 ? texture(_CameraDepthTexture, texture(_RayCast, uv).xy).r
                                                            : texture(_CameraDepthTexture, uv).r;
            vec4 clip = vec4(uv * 2.0 - 1.0, hitDepthW * 2.0 - 1.0, 1.0);
            vec4 world = XENGINE_MATRIX_I_VP * clip;
            world /= world.w;
            vec4 prevClip = XENGINE_MATRIX_VP_PREVIOUS * world;
            vec2 prevUV = (prevClip.xy / prevClip.w) * 0.5 + 0.5;
            return uv - prevUV;
        }

        void main()
        {
            vec2 velocity = _ReflectionVelocity == 1 ? reflectionVelocity(TexCoords)
                                                     : texture(_CameraMotionVectorsTexture, TexCoords).rg;
            vec4 current = texture(_MainTex, TexCoords);
            vec4 previous = texture(_PreviousBuffer, TexCoords - velocity);

            vec2 du = vec2(1.0 / _ScreenParams.x, 0.0);
            vec2 dv = vec2(0.0, 1.0 / _ScreenParams.y);
            vec4 mn = current, mx = current;
            for (int x = -1; x <= 1; x++)
            for (int y = -1; y <= 1; y++)
            {
                vec4 c = texture(_MainTex, TexCoords + du * float(x) + dv * float(y));
                mn = min(mn, c); mx = max(mx, c);
            }
            vec4 center = (mn + mx) * 0.5;
            mn = (mn - center) * _TScale + center;
            mx = (mx - center) * _TScale + center;
            previous = clamp(previous, mn, mx);

            fragColor = mix(current, previous, clamp(_TResponse * (1.0 - length(velocity) * 8.0), 0.0, 1.0));
        }
    }
    ENDGLSL
    SLANGPROGRAM
    Vertex { struct VSInput { float3 vertexPosition : POSITION; float2 vertexTexCoord : TEXCOORD0; }; struct VSOutput { float4 position : SV_Position; float2 TexCoords : TEXCOORD0; }; VSOutput main(VSInput input) { VSOutput output; output.TexCoords = input.vertexTexCoord; output.position = float4(input.vertexPosition, 1.0); return output; } }
    Fragment
    {
        #include "XEngineCG"
        struct PSInput { float4 position : SV_Position; float2 TexCoords : TEXCOORD0; };
        cbuffer SSRTemporalPS : register(b2) { int _ReflectionVelocity; float _TScale; float _TResponse; float _SSRTemporalPadding; };
        Texture2D _MainTex : register(t0); SamplerState _MainTexSampler : register(s0);
        Texture2D _PreviousBuffer : register(t1); SamplerState _PreviousBufferSampler : register(s1);
        Texture2D _RayCast : register(t2); SamplerState _RayCastSampler : register(s2);
        Texture2D _CameraDepthTexture : register(t3); SamplerState _CameraDepthSampler : register(s3);
        Texture2D _CameraMotionVectorsTexture : register(t4); SamplerState _CameraMotionVectorsSampler : register(s4);
        float2 ReflectionVelocity(float2 uv) { float4 rd = _RayCast.Sample(_RayCastSampler, uv); float depth = _CameraDepthTexture.Sample(_CameraDepthSampler, rd.w > 0.0 ? rd.xy : uv).r; float4 world = mul(XENGINE_MATRIX_I_VP, float4(uv * 2.0 - 1.0, depth * 2.0 - 1.0, 1.0)); world /= world.w; float4 previous = mul(XENGINE_MATRIX_VP_PREVIOUS, world); float2 previousUV = (previous.xy / previous.w) * 0.5 + 0.5; return uv - previousUV; }
        float4 main(PSInput input) : SV_Target
        {
            float2 velocity = _ReflectionVelocity == 1 ? ReflectionVelocity(input.TexCoords) : _CameraMotionVectorsTexture.Sample(_CameraMotionVectorsSampler, input.TexCoords).rg;
            float4 current = _MainTex.Sample(_MainTexSampler, input.TexCoords); float4 previous = _PreviousBuffer.Sample(_PreviousBufferSampler, input.TexCoords - velocity);
            float2 du = float2(1.0 / _ScreenParams.x, 0.0); float2 dv = float2(0.0, 1.0 / _ScreenParams.y); float4 mn = current, mx = current;
            [unroll] for (int x = -1; x <= 1; x++) [unroll] for (int y = -1; y <= 1; y++) { float4 c = _MainTex.Sample(_MainTexSampler, input.TexCoords + du * float(x) + dv * float(y)); mn = min(mn, c); mx = max(mx, c); }
            float4 center = (mn + mx) * 0.5; previous = clamp(previous, (mn - center) * _TScale + center, (mx - center) * _TScale + center);
            return lerp(current, previous, saturate(_TResponse * (1.0 - length(velocity) * 8.0)));
        }
    }
    ENDSLANG
}

//------------------------------------------------------------------------------------------------
Pass "Reproject"
{
    Tags { "RenderOrder" = "Opaque" }
    Blend Off ZTest Off ZWrite Off Cull Off

    GLSLPROGRAM

    Vertex
    {
        layout (location = 0) in vec3 vertexPosition;
        layout (location = 1) in vec2 vertexTexCoord;
        out vec2 TexCoords;
        void main() { gl_Position = vec4(vertexPosition, 1.0); TexCoords = vertexTexCoord; }
    }

    Fragment
    {
        #include "XEngineCG"

        uniform sampler2D _MainTex;                    // buffer to reproject (previous combined)
        uniform sampler2D _CameraMotionVectorsTexture; // .rg motion

        in vec2 TexCoords;
        layout(location = 0) out vec4 fragColor;

        void main()
        {
            vec2 velocity = texture(_CameraMotionVectorsTexture, TexCoords).rg;
            fragColor = texture(_MainTex, TexCoords - velocity);
        }
    }
    ENDGLSL
    SLANGPROGRAM
    Vertex { struct VSInput { float3 vertexPosition : POSITION; float2 vertexTexCoord : TEXCOORD0; }; struct VSOutput { float4 position : SV_Position; float2 TexCoords : TEXCOORD0; }; VSOutput main(VSInput input) { VSOutput output; output.TexCoords = input.vertexTexCoord; output.position = float4(input.vertexPosition, 1.0); return output; } }
    Fragment
    {
        struct PSInput { float4 position : SV_Position; float2 TexCoords : TEXCOORD0; };
        Texture2D _MainTex : register(t0); SamplerState _MainTexSampler : register(s0);
        Texture2D _CameraMotionVectorsTexture : register(t1); SamplerState _CameraMotionVectorsSampler : register(s1);
        float4 main(PSInput input) : SV_Target { float2 velocity = _CameraMotionVectorsTexture.Sample(_CameraMotionVectorsSampler, input.TexCoords).rg; return _MainTex.Sample(_MainTexSampler, input.TexCoords - velocity); }
    }
    ENDSLANG
}

//------------------------------------------------------------------------------------------------
Pass "Combine"
{
    Tags { "RenderOrder" = "Opaque" }
    Blend Off ZTest Off ZWrite Off Cull Off

    GLSLPROGRAM

    Vertex
    {
        layout (location = 0) in vec3 vertexPosition;
        layout (location = 1) in vec2 vertexTexCoord;
        out vec2 TexCoords;
        void main() { gl_Position = vec4(vertexPosition, 1.0); TexCoords = vertexTexCoord; }
    }

    Fragment
    {
        #include "XEngineCG"

        uniform sampler2D _MainTex;          // scene colour
        uniform sampler2D _ReflectionBuffer; // resolved (+temporal) reflection
        uniform sampler2D _CameraDepthTexture;
        uniform sampler2D _CameraNormalsTexture;
        uniform sampler2D _CameraMotionVectorsTexture; // .b roughness, .a metallic
        uniform int _UseFresnel;

        in vec2 TexCoords;
        layout(location = 0) out vec4 fragColor;

        // Environment BRDF approximation (Karis "mobile") for the specular reflection response.
        vec3 envBRDFApprox(vec3 F0, float roughness, float NdotV)
        {
            vec4 c0 = vec4(-1.0, -0.0275, -0.572, 0.022);
            vec4 c1 = vec4(1.0, 0.0425, 1.04, -0.04);
            vec4 r = roughness * c0 + c1;
            float a004 = min(r.x * r.x, exp2(-9.28 * NdotV)) * r.x + r.y;
            vec2 ab = vec2(-1.04, 1.04) * a004 + r.zw;
            return F0 * ab.x + ab.y;
        }

        void main()
        {
            vec3 sceneColor = texture(_MainTex, TexCoords).rgb;
            vec4 reflection = texture(_ReflectionBuffer, TexCoords);

            float roughness = texture(_CameraMotionVectorsTexture, TexCoords).b;
            float metallic = texture(_CameraMotionVectorsTexture, TexCoords).a;
            vec3 viewNormal = normalize(texture(_CameraNormalsTexture, TexCoords).xyz * 2.0 - 1.0);
            float depth = texture(_CameraDepthTexture, TexCoords).r;
            vec3 V = normalize(-getViewPos(TexCoords, depth));
            float NdotV = clamp(dot(viewNormal, V), 0.0, 1.0);

            // Forward SSR has no G-buffer albedo: approximate it from the tonemapped scene colour.
            vec3 approxAlbedo = sceneColor / (1.0 + sceneColor);
            vec3 F0 = mix(vec3(0.04), approxAlbedo, metallic);

            float mask = reflection.a * reflection.a;
            vec3 refl = reflection.rgb;
            if (_UseFresnel == 1)
                refl *= envBRDFApprox(F0, roughness, NdotV);
            // Guard against runaway HDR and the one-bounce feedback loop diverging when Fresnel
            // (which normally attenuates each bounce) is disabled.
            refl = min(refl, vec3(8.0));

            fragColor = vec4(sceneColor + refl * mask, 1.0);
        }
    }
    ENDGLSL
    SLANGPROGRAM
    Vertex { struct VSInput { float3 vertexPosition : POSITION; float2 vertexTexCoord : TEXCOORD0; }; struct VSOutput { float4 position : SV_Position; float2 TexCoords : TEXCOORD0; }; VSOutput main(VSInput input) { VSOutput output; output.TexCoords = input.vertexTexCoord; output.position = float4(input.vertexPosition, 1.0); return output; } }
    Fragment
    {
        #include "XEngineCG"
        struct PSInput { float4 position : SV_Position; float2 TexCoords : TEXCOORD0; };
        cbuffer SSRCombinePS : register(b2) { int _UseFresnel; float3 _SSRCombinePadding; };
        Texture2D _MainTex : register(t0); SamplerState _MainTexSampler : register(s0);
        Texture2D _ReflectionBuffer : register(t1); SamplerState _ReflectionBufferSampler : register(s1);
        Texture2D _CameraDepthTexture : register(t2); SamplerState _CameraDepthSampler : register(s2);
        Texture2D _CameraNormalsTexture : register(t3); SamplerState _CameraNormalsSampler : register(s3);
        Texture2D _CameraMotionVectorsTexture : register(t4); SamplerState _CameraMotionVectorsSampler : register(s4);
        float3 GetViewPos(float2 uv, float depth) { float4 view = mul(xengine_MatIP, float4(uv * 2.0 - 1.0, depth * 2.0 - 1.0, 1.0)); return view.xyz / view.w; }
        float3 EnvBRDFApprox(float3 F0, float roughness, float NdotV) { float4 r = roughness * float4(-1.0, -0.0275, -0.572, 0.022) + float4(1.0, 0.0425, 1.04, -0.04); float a004 = min(r.x * r.x, exp2(-9.28 * NdotV)) * r.x + r.y; float2 ab = float2(-1.04, 1.04) * a004 + r.zw; return F0 * ab.x + ab.y; }
        float4 main(PSInput input) : SV_Target
        {
            float3 sceneColor = _MainTex.Sample(_MainTexSampler, input.TexCoords).rgb; float4 reflection = _ReflectionBuffer.Sample(_ReflectionBufferSampler, input.TexCoords);
            float4 material = _CameraMotionVectorsTexture.Sample(_CameraMotionVectorsSampler, input.TexCoords); float3 normal = normalize(_CameraNormalsTexture.Sample(_CameraNormalsSampler, input.TexCoords).xyz * 2.0 - 1.0);
            float NdotV = saturate(dot(normal, normalize(-GetViewPos(input.TexCoords, _CameraDepthTexture.Sample(_CameraDepthSampler, input.TexCoords).r)))); float3 F0 = lerp(float3(0.04, 0.04, 0.04), sceneColor / (1.0 + sceneColor), material.a);
            float3 reflected = reflection.rgb; if (_UseFresnel == 1) reflected *= EnvBRDFApprox(F0, material.b, NdotV); reflected = min(reflected, float3(8.0, 8.0, 8.0));
            return float4(sceneColor + reflected * reflection.a * reflection.a, 1.0);
        }
    }
    ENDSLANG
}
