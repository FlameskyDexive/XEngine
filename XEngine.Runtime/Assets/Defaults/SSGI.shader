Shader "Default/SSGI"

Variants
{
}

// Real-time screen-space global illumination (Surface Cache GI, screen-space tier).
//   Gather    : per-pixel cosine-distributed screen-space rays -> 1-bounce diffuse irradiance.
//               Hit radiance samples the scene colour directly: the lit surface colour is the
//               outgoing radiance of the bounce (albedo-tinted, emission included).
//   Temporal  : motion-vector reprojection + neighbourhood clamp against the history buffer.
//   Blur      : separable edge-aware bilateral (depth + view-normal weights).
//   Composite : full-res additive `sceneColor += irradiance * albedo * (1-metallic) * Intensity`
//               using the pipeline-provided `_GiAlbedoTexture` (GiAlbedo pass on Forward,
//               GBuffer albedo on Deferred). Debug views select irradiance/albedo/validity.
// Receiver view normals + roughness/metallic come from the unified prepass, like SSR/GTAO.

//------------------------------------------------------------------------------------------------
Pass "Gather"
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

        uniform sampler2D _MainTex;               // opaque scene colour (radiance source)
        uniform sampler2D _CameraDepthTexture;
        uniform sampler2D _CameraNormalsTexture;  // encoded view normal
        uniform sampler2D _Noise;
        uniform vec2 _NoiseScale;   // gather size / noise size
        uniform vec2 _JitterOffset; // per-frame Halton offset
        uniform float _NumSteps;
        uniform float _Radius;      // max bounce distance in view units
        uniform int _GiRays;

        in vec2 TexCoords;
        layout(location = 0) out vec4 fragColor; // rgb = irradiance estimate, a = ray hit fraction

        vec3 projectToScreen(vec3 viewPos)
        {
            vec4 clip = XENGINE_MATRIX_P * vec4(viewPos, 1.0);
            clip.xyz /= clip.w;
            return clip.xyz * 0.5 + 0.5; // screen space: depth texture domain ([0,1])
        }

        // March in screen space until the ray passes behind the depth buffer, then refine.
        bool trace(vec3 startView, vec3 dirView, float jitter, out vec2 hitUV)
        {
            vec3 startScreen = projectToScreen(startView);
            vec3 endScreen = projectToScreen(startView + dirView * _Radius);
            vec3 delta = endScreen - startScreen;
            hitUV = startScreen.xy;
            if (length(delta.xy) < 1e-5) return false;

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
            for (int j = 0; j < 4; j++)
            {
                vec3 mid = (lo + hi) * 0.5;
                float d = texture(_CameraDepthTexture, mid.xy).r;
                if (mid.z > d) hi = mid; else lo = mid;
            }
            hitUV = hi.xy;
            return true;
        }

        // Cosine-weighted hemisphere direction around n in view space (pdf = cos/pi).
        vec3 cosineSample(vec3 n, vec2 Xi)
        {
            float phi = 2.0 * XENGINE_PI * Xi.x;
            float r = sqrt(Xi.y);
            vec3 up = abs(n.z) < 0.999 ? vec3(0.0, 0.0, 1.0) : vec3(1.0, 0.0, 0.0);
            vec3 t = normalize(cross(up, n));
            vec3 b = cross(n, t);
            return normalize(t * (r * cos(phi)) + b * (r * sin(phi)) + n * sqrt(max(0.0, 1.0 - Xi.y)));
        }

        void main()
        {
            float depth = texture(_CameraDepthTexture, TexCoords).r;
            if (depth >= 1.0) { fragColor = vec4(0.0); return; }

            vec4 nd = texture(_CameraNormalsTexture, TexCoords);
            if (length(nd.xyz) < 0.01) { fragColor = vec4(0.0); return; }
            vec3 viewNormal = normalize(nd.xyz * 2.0 - 1.0);

            vec3 viewPos = getViewPos(TexCoords, depth);
            // Lift the ray origin off the surface so it cannot immediately hit its own depth texel.
            vec3 origin = viewPos + viewNormal * 0.05;

            vec2 Xi = texture(_Noise, (TexCoords + _JitterOffset) * _NoiseScale).rg;

            // Cosine importance sampling makes E = (1/N) * sum(L_i) * PI and the Lambert
            // albedo/PI cancels the PI: the averaged hit radiance is the diffuse GI factor.
            vec3 irradiance = vec3(0.0);
            int hits = 0;
            for (int r = 0; r < _GiRays; r++)
            {
                vec2 XiR = fract(Xi + vec2(float(r) * 0.61803398875, float(r) * 0.38196601125));
                vec3 dir = cosineSample(viewNormal, XiR);
                float jitter = (XiR.x + XiR.y) * (1.0 / max(_NumSteps, 1.0));

                vec2 hitUV;
                if (trace(origin, dir, jitter, hitUV))
                {
                    vec3 radiance = texture(_MainTex, hitUV).rgb;
                    // Firefly clamp: one bright texel must not smear over the whole blur kernel.
                    irradiance += min(radiance, vec3(8.0));
                    hits++;
                }
            }

            if (hits == 0) { fragColor = vec4(0.0); return; }
            fragColor = vec4(irradiance / float(hits), float(hits) / float(max(_GiRays, 1)));
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
        cbuffer SsgiGatherPS : register(b2)
        {
            float2 _NoiseScale; float2 _JitterOffset;
            float _NumSteps; float _Radius; int _GiRays; float _SsgiGatherPadding;
        };
        Texture2D _MainTex : register(t0); SamplerState _MainTexSampler : register(s0);
        Texture2D _CameraDepthTexture : register(t1); SamplerState _CameraDepthSampler : register(s1);
        Texture2D _CameraNormalsTexture : register(t2); SamplerState _CameraNormalsSampler : register(s2);
        Texture2D _Noise : register(t3); SamplerState _NoiseSampler : register(s3);
        #define XENGINE_TWO_PI 6.28318530718
        float3 GetViewPos(float2 uv, float depth) { float4 view = mul(xengine_MatIP, float4(uv * 2.0 - 1.0, depth * 2.0 - 1.0, 1.0)); return view.xyz / view.w; }
        float3 ProjectToScreen(float3 viewPos) { float4 clip = mul(XENGINE_MATRIX_P, float4(viewPos, 1.0)); clip.xyz /= clip.w; return clip.xyz * 0.5 + 0.5; }
        float3 CosineSample(float3 n, float2 Xi)
        {
            float phi = XENGINE_TWO_PI * Xi.x; float r = sqrt(Xi.y);
            float3 up = abs(n.z) < 0.999 ? float3(0.0, 0.0, 1.0) : float3(1.0, 0.0, 0.0);
            float3 t = normalize(cross(up, n));
            return normalize(t * (r * cos(phi)) + cross(n, t) * (r * sin(phi)) + n * sqrt(max(0.0, 1.0 - Xi.y)));
        }
        bool Trace(float3 startView, float3 dirView, float jitter, out float2 hitUV)
        {
            float3 delta = ProjectToScreen(startView + dirView * _Radius) - ProjectToScreen(startView);
            hitUV = 0.0;
            if (length(delta.xy) < 1e-5) return false;
            float stepsF = min(max(abs(delta.x) * _ScreenParams.x, abs(delta.y) * _ScreenParams.y), _NumSteps);
            int steps = (int)max(stepsF, 1.0); float3 stepSz = delta / max(stepsF, 1.0);
            float3 startScreen = ProjectToScreen(startView);
            float3 cur = startScreen + stepSz * (1.0 + jitter); bool crossed = false;
            [loop] for (int i = 1; i < steps; i++) { if (cur.x < 0.0 || cur.x > 1.0 || cur.y < 0.0 || cur.y > 1.0) return false; float sceneDepth = _CameraDepthTexture.Sample(_CameraDepthSampler, cur.xy).r; if (sceneDepth < 1.0 && cur.z > sceneDepth + 0.0001) { crossed = true; break; } cur += stepSz; }
            if (!crossed) return false;
            float3 lo = cur - stepSz, hi = cur;
            [unroll] for (int j = 0; j < 4; j++) { float3 mid = (lo + hi) * 0.5; if (mid.z > _CameraDepthTexture.Sample(_CameraDepthSampler, mid.xy).r) hi = mid; else lo = mid; }
            hitUV = hi.xy; return true;
        }
        float4 main(PSInput input) : SV_Target
        {
            float depth = _CameraDepthTexture.Sample(_CameraDepthSampler, input.TexCoords).r; if (depth >= 1.0) return 0.0;
            float3 viewNormal = normalize(_CameraNormalsTexture.Sample(_CameraNormalsSampler, input.TexCoords).xyz * 2.0 - 1.0); if (length(viewNormal) < 0.01) return 0.0;
            float3 viewPos = GetViewPos(input.TexCoords, depth);
            float3 origin = viewPos + viewNormal * 0.05;
            float2 Xi = _Noise.Sample(_NoiseSampler, (input.TexCoords + _JitterOffset) * _NoiseScale).rg;
            float3 irradiance = 0.0; int hits = 0;
            [loop] for (int r = 0; r < _GiRays; r++)
            {
                float2 XiR = frac(Xi + float2(r * 0.61803398875, r * 0.38196601125));
                float2 hitUV;
                if (Trace(origin, CosineSample(viewNormal, XiR), (XiR.x + XiR.y) / max(_NumSteps, 1.0), hitUV))
                { irradiance += min(_MainTex.Sample(_MainTexSampler, hitUV).rgb, float3(8.0, 8.0, 8.0)); hits++; }
            }
            if (hits == 0) return 0.0;
            return float4(irradiance / (float)hits, (float)hits / (float)max(_GiRays, 1));
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

        uniform sampler2D _MainTex;                    // current gather result
        uniform sampler2D _PreviousBuffer;             // irradiance history
        uniform sampler2D _CameraMotionVectorsTexture; // .rg motion
        uniform float _TResponse;

        in vec2 TexCoords;
        layout(location = 0) out vec4 fragColor;

        void main()
        {
            vec2 velocity = texture(_CameraMotionVectorsTexture, TexCoords).rg;
            vec4 current = texture(_MainTex, TexCoords);
            vec4 previous = texture(_PreviousBuffer, TexCoords - velocity);

            // Neighbourhood clamp (3x3) keeps fast-moving light from ghosting.
            vec2 du = vec2(1.0 / _ScreenParams.x, 0.0);
            vec2 dv = vec2(0.0, 1.0 / _ScreenParams.y);
            vec4 mn = current, mx = current;
            for (int x = -1; x <= 1; x++)
            for (int y = -1; y <= 1; y++)
            {
                vec4 c = texture(_MainTex, TexCoords + du * float(x) + dv * float(y));
                mn = min(mn, c); mx = max(mx, c);
            }
            previous = clamp(previous, mn, mx);

            // Damp accumulation where the pixel moved so history does not smear.
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
        cbuffer SsgiTemporalPS : register(b2) { float _TResponse; float3 _SsgiTemporalPadding; };
        Texture2D _MainTex : register(t0); SamplerState _MainTexSampler : register(s0);
        Texture2D _PreviousBuffer : register(t1); SamplerState _PreviousBufferSampler : register(s1);
        Texture2D _CameraMotionVectorsTexture : register(t2); SamplerState _CameraMotionVectorsSampler : register(s2);
        float4 main(PSInput input) : SV_Target
        {
            float2 velocity = _CameraMotionVectorsTexture.Sample(_CameraMotionVectorsSampler, input.TexCoords).rg;
            float4 current = _MainTex.Sample(_MainTexSampler, input.TexCoords); float4 previous = _PreviousBuffer.Sample(_PreviousBufferSampler, input.TexCoords - velocity);
            float2 du = float2(1.0 / _ScreenParams.x, 0.0); float2 dv = float2(0.0, 1.0 / _ScreenParams.y);
            float4 mn = current, mx = current;
            [unroll] for (int x = -1; x <= 1; x++) [unroll] for (int y = -1; y <= 1; y++) { float4 c = _MainTex.Sample(_MainTexSampler, input.TexCoords + du * x + dv * y); mn = min(mn, c); mx = max(mx, c); }
            previous = clamp(previous, mn, mx);
            return lerp(current, previous, saturate(_TResponse * (1.0 - length(velocity) * 8.0)));
        }
    }
    ENDSLANG
}

//------------------------------------------------------------------------------------------------
Pass "Blur"
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
        uniform sampler2D _CameraDepthTexture;
        uniform sampler2D _CameraNormalsTexture;
        uniform vec2 _BlurDirection; // (1,0) or (0,1) in texels
        uniform float _BlurRadius;

        in vec2 TexCoords;
        layout(location = 0) out vec4 fragColor;

        void main()
        {
            vec2 texel = _BlurDirection / vec2(textureSize(_MainTex, 0));
            float depth = texture(_CameraDepthTexture, TexCoords).r;
            vec3 normal = normalize(texture(_CameraNormalsTexture, TexCoords).xyz * 2.0 - 1.0);

            vec4 sum = texture(_MainTex, TexCoords);
            float weightSum = 1.0;
            for (int i = 1; i <= 4; i++)
            {
                float w = exp(-float(i * i) / (2.0 * max(_BlurRadius, 0.001))) ;
                vec2 offset = texel * float(i);
                vec2 uvA = TexCoords + offset;
                vec2 uvB = TexCoords - offset;
                float dA = texture(_CameraDepthTexture, uvA).r;
                float dB = texture(_CameraDepthTexture, uvB).r;
                vec3 nA = normalize(texture(_CameraNormalsTexture, uvA).xyz * 2.0 - 1.0);
                vec3 nB = normalize(texture(_CameraNormalsTexture, uvB).xyz * 2.0 - 1.0);
                float wa = w * exp(-abs(dA - depth) * 40.0) * pow(max(dot(normal, nA), 0.0), 8.0);
                float wb = w * exp(-abs(dB - depth) * 40.0) * pow(max(dot(normal, nB), 0.0), 8.0);
                sum += texture(_MainTex, uvA) * wa;
                sum += texture(_MainTex, uvB) * wb;
                weightSum += wa + wb;
            }
            fragColor = sum / weightSum;
        }
    }
    ENDGLSL

    SLANGPROGRAM
    Vertex { struct VSInput { float3 vertexPosition : POSITION; float2 vertexTexCoord : TEXCOORD0; }; struct VSOutput { float4 position : SV_Position; float2 TexCoords : TEXCOORD0; }; VSOutput main(VSInput input) { VSOutput output; output.TexCoords = input.vertexTexCoord; output.position = float4(input.vertexPosition, 1.0); return output; } }
    Fragment
    {
        #include "XEngineCG"
        struct PSInput { float4 position : SV_Position; float2 TexCoords : TEXCOORD0; };
        cbuffer SsgiBlurPS : register(b2) { float2 _BlurDirection; float _BlurRadius; float _SsgiBlurPadding; };
        Texture2D _MainTex : register(t0); SamplerState _MainTexSampler : register(s0);
        Texture2D _CameraDepthTexture : register(t1); SamplerState _CameraDepthSampler : register(s1);
        Texture2D _CameraNormalsTexture : register(t2); SamplerState _CameraNormalsSampler : register(s2);
        float4 main(PSInput input) : SV_Target
        {
            uint texW, texH; _MainTex.GetDimensions(texW, texH);
            float2 texel = _BlurDirection / float2(texW, texH);
            float depth = _CameraDepthTexture.Sample(_CameraDepthSampler, input.TexCoords).r;
            float3 normal = normalize(_CameraNormalsTexture.Sample(_CameraNormalsSampler, input.TexCoords).xyz * 2.0 - 1.0);
            float4 sum = _MainTex.Sample(_MainTexSampler, input.TexCoords); float weightSum = 1.0;
            [unroll] for (int i = 1; i <= 4; i++)
            {
                float w = exp(-(float)(i * i) / (2.0 * max(_BlurRadius, 0.001)));
                float2 offset = texel * i;
                float2 uvA = input.TexCoords + offset; float2 uvB = input.TexCoords - offset;
                float wa = w * exp(-abs(_CameraDepthTexture.Sample(_CameraDepthSampler, uvA).r - depth) * 40.0) * pow(max(dot(normal, normalize(_CameraNormalsTexture.Sample(_CameraNormalsSampler, uvA).xyz * 2.0 - 1.0)), 0.0), 8.0);
                float wb = w * exp(-abs(_CameraDepthTexture.Sample(_CameraDepthSampler, uvB).r - depth) * 40.0) * pow(max(dot(normal, normalize(_CameraNormalsTexture.Sample(_CameraNormalsSampler, uvB).xyz * 2.0 - 1.0)), 0.0), 8.0);
                sum += _MainTex.Sample(_MainTexSampler, uvA) * wa + _MainTex.Sample(_MainTexSampler, uvB) * wb;
                weightSum += wa + wb;
            }
            return sum / weightSum;
        }
    }
    ENDSLANG
}

//------------------------------------------------------------------------------------------------
Pass "Composite"
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
        uniform sampler2D _GiTex;            // blurred irradiance (rgb) + hit fraction (a)
        uniform sampler2D _CameraDepthTexture;
        uniform sampler2D _CameraMotionVectorsTexture; // .a metallic
        uniform sampler2D _GiAlbedoTexture;  // rgb = albedo, a = AO
        uniform float _Intensity;
        uniform int _GiDebugView; // 0 = off, 1 = irradiance, 2 = albedo, 3 = validity

        in vec2 TexCoords;
        layout(location = 0) out vec4 fragColor;

        void main()
        {
            vec4 sceneColor = texture(_MainTex, TexCoords);
            float depth = texture(_CameraDepthTexture, TexCoords).r;
            vec4 gi = texture(_GiTex, TexCoords);
            vec4 albedo = texture(_GiAlbedoTexture, TexCoords);

            if (_GiDebugView == 1) { fragColor = vec4(gi.rgb, 1.0); return; }
            if (_GiDebugView == 2) { fragColor = vec4(albedo.rgb, 1.0); return; }
            if (_GiDebugView == 3) { fragColor = vec4(albedo.a, gi.a, 0.0, 1.0); return; }

            // Diffuse GI only: scale by (1 - metallic) like the ambient diffuse path.
            // Skip sky and surfaces without an albedo capture (no GiAlbedo pass -> cleared 0).
            float metallic = texture(_CameraMotionVectorsTexture, TexCoords).a;
            vec3 bounce = (depth < 1.0 && albedo.a > 0.0)
                ? gi.rgb * albedo.rgb * albedo.a * (1.0 - metallic) * _Intensity
                : vec3(0.0);
            fragColor = vec4(sceneColor.rgb + bounce, sceneColor.a);
        }
    }
    ENDGLSL

    SLANGPROGRAM
    Vertex { struct VSInput { float3 vertexPosition : POSITION; float2 vertexTexCoord : TEXCOORD0; }; struct VSOutput { float4 position : SV_Position; float2 TexCoords : TEXCOORD0; }; VSOutput main(VSInput input) { VSOutput output; output.TexCoords = input.vertexTexCoord; output.position = float4(input.vertexPosition, 1.0); return output; } }
    Fragment
    {
        #include "XEngineCG"
        struct PSInput { float4 position : SV_Position; float2 TexCoords : TEXCOORD0; };
        cbuffer SsgiCompositePS : register(b2) { float _Intensity; int _GiDebugView; float2 _SsgiCompositePadding; };
        Texture2D _MainTex : register(t0); SamplerState _MainTexSampler : register(s0);
        Texture2D _GiTex : register(t1); SamplerState _GiTexSampler : register(s1);
        Texture2D _CameraDepthTexture : register(t2); SamplerState _CameraDepthSampler : register(s2);
        Texture2D _GiAlbedoTexture : register(t3); SamplerState _GiAlbedoSampler : register(s3);
        Texture2D _CameraMotionVectorsTexture : register(t4); SamplerState _CameraMotionVectorsSampler : register(s4);
        float4 main(PSInput input) : SV_Target
        {
            float4 sceneColor = _MainTex.Sample(_MainTexSampler, input.TexCoords);
            float depth = _CameraDepthTexture.Sample(_CameraDepthSampler, input.TexCoords).r;
            float4 gi = _GiTex.Sample(_GiTexSampler, input.TexCoords);
            float4 albedo = _GiAlbedoTexture.Sample(_GiAlbedoSampler, input.TexCoords);
            if (_GiDebugView == 1) return float4(gi.rgb, 1.0);
            if (_GiDebugView == 2) return float4(albedo.rgb, 1.0);
            if (_GiDebugView == 3) return float4(albedo.a, gi.a, 0.0, 1.0);
            float metallic = _CameraMotionVectorsTexture.Sample(_CameraMotionVectorsSampler, input.TexCoords).a;
            float3 bounce = (depth < 1.0 && albedo.a > 0.0) ? gi.rgb * albedo.rgb * albedo.a * (1.0 - metallic) * _Intensity : 0.0;
            return float4(sceneColor.rgb + bounce, sceneColor.a);
        }
    }
    ENDSLANG
}

//------------------------------------------------------------------------------------------------
// Dynamic irradiance probe refresh (Surface Cache GI, probe tier).
// One fullscreen draw over the atlas: fragments outside this frame's round-robin update range
// discard so the alpha-blended EMA leaves untouched probes intact. Per-texel = one octahedral
// ray direction from the probe; hits sample the (GI-composited) scene colour of the current
// frame, misses fall back to the CPU-provided sky/ambient colour.
Pass "ProbeUpdate"
{
    Tags { "RenderOrder" = "Opaque" }
    Blend Alpha
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
        #include "ShaderLibrary/ShaderConfig"

        uniform sampler2D _MainTex;            // scene colour (radiance source, blit input)
        uniform sampler2D _CameraDepthTexture;
        uniform vec4 _GiUpdateRange;           // x = first probe index, y = count, z = tiles per row
        uniform float _GiUpdateRate;
        uniform vec4 _GiProbeOriginSpacing;    // xyz = grid origin, w = spacing
        uniform vec4 _GiProbeGridParams;       // x/y/z = counts, w unused
        uniform vec4 _GiMissColor;             // sky/ambient fallback for rays that leave the screen

        layout(location = 0) out vec4 fragColor;

        vec2 giOctEncode(vec3 n)
        {
            n /= (abs(n.x) + abs(n.y) + abs(n.z));
            vec2 e = n.xy;
            if (n.z < 0.0)
                e = (1.0 - abs(n.yx)) * vec2(n.x >= 0.0 ? 1.0 : -1.0, n.y >= 0.0 ? 1.0 : -1.0);
            return e * 0.5 + 0.5;
        }

        vec3 giOctDecode(vec2 e)
        {
            e = e * 2.0 - 1.0;
            vec3 n = vec3(e.xy, 1.0 - abs(e.x) - abs(e.y));
            float t = max(-n.z, 0.0);
            n.x -= n.x >= 0.0 ? t : -t;
            n.y -= n.y >= 0.0 ? t : -t;
            return normalize(n);
        }

        vec3 giProjectToScreen(vec3 viewPos)
        {
            vec4 clip = XENGINE_MATRIX_P * vec4(viewPos, 1.0);
            clip.xyz /= clip.w;
            return clip.xyz * 0.5 + 0.5; // screen space: depth texture domain ([0,1])
        }

        void main()
        {
            ivec2 frag = ivec2(gl_FragCoord.xy);
            ivec2 tileCoord = frag / GI_PROBE_TILE_SIZE;
            int tilesPerRow = int(_GiUpdateRange.z);
            if (tilesPerRow <= 0) discard;
            int probeIndex = tileCoord.y * tilesPerRow + tileCoord.x;
            int first = int(_GiUpdateRange.x);
            if (probeIndex < first || probeIndex >= first + int(_GiUpdateRange.y)) discard;

            int cx = max(int(_GiProbeGridParams.x), 1);
            int cy = max(int(_GiProbeGridParams.y), 1);
            ivec3 cell = ivec3(probeIndex % cx, (probeIndex / cx) % cy, probeIndex / (cx * cy));
            vec3 probeWorld = _GiProbeOriginSpacing.xyz + vec3(cell) * _GiProbeOriginSpacing.w;

            vec2 tileUV = (vec2(frag % GI_PROBE_TILE_SIZE) + 0.5) / float(GI_PROBE_TILE_SIZE);
            vec3 dirWorld = giOctDecode(tileUV);

            vec3 probeView = (XENGINE_MATRIX_V * vec4(probeWorld, 1.0)).xyz;
            vec3 dirView = normalize(mat3(XENGINE_MATRIX_V) * dirWorld);
            vec3 startScreen = giProjectToScreen(probeView);
            if (startScreen.x < 0.0 || startScreen.x > 1.0 || startScreen.y < 0.0 || startScreen.y > 1.0)
            {
                // Probe off screen: refresh toward the ambient fallback so unseen regions
                // still carry sky light instead of staying stale.
                fragColor = vec4(_GiMissColor.rgb, _GiUpdateRate);
                return;
            }

            // Screen-space march away from the probe (16 fixed steps + 4-step refine).
            vec3 endScreen = giProjectToScreen(probeView + dirView * _GiProbeOriginSpacing.w * 8.0);
            vec3 delta = endScreen - startScreen;
            vec3 stepSz = delta / 16.0;
            vec3 cur = startScreen + stepSz;
            bool crossed = false;
            for (int i = 0; i < 16; i++)
            {
                if (cur.x < 0.0 || cur.x > 1.0 || cur.y < 0.0 || cur.y > 1.0) break;
                float sceneDepth = texture(_CameraDepthTexture, cur.xy).r;
                if (sceneDepth < 1.0 && cur.z > sceneDepth + 0.001) { crossed = true; break; }
                cur += stepSz;
            }
            if (crossed)
            {
                vec3 lo = cur - stepSz, hi = cur;
                for (int j = 0; j < 4; j++)
                {
                    vec3 mid = (lo + hi) * 0.5;
                    float d = texture(_CameraDepthTexture, mid.xy).r;
                    if (mid.z > d) hi = mid; else lo = mid;
                }
                vec3 radiance = texture(_MainTex, hi.xy).rgb;
                fragColor = vec4(min(radiance, vec3(8.0)), _GiUpdateRate);
            }
            else
            {
                fragColor = vec4(_GiMissColor.rgb, _GiUpdateRate);
            }
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
        #include "ShaderLibrary/ShaderConfig"
        struct PSInput { float4 position : SV_Position; float2 TexCoords : TEXCOORD0; };
        cbuffer SsgiProbePS : register(b2)
        {
            float4 _GiUpdateRange; float4 _GiProbeOriginSpacing; float4 _GiProbeGridParams;
            float4 _GiMissColor; float _GiUpdateRate; float3 _SsgiProbePadding;
        };
        Texture2D _MainTex : register(t0); SamplerState _MainTexSampler : register(s0);
        Texture2D _CameraDepthTexture : register(t1); SamplerState _CameraDepthSampler : register(s1);
        float2 GiOctEncodeLocal(float3 n)
        {
            n /= (abs(n.x) + abs(n.y) + abs(n.z));
            float2 e = n.xy;
            if (n.z < 0.0)
                e = (1.0 - abs(e.yx)) * float2(n.x >= 0.0 ? 1.0 : -1.0, n.y >= 0.0 ? 1.0 : -1.0);
            return e * 0.5 + 0.5;
        }
        float3 GiOctDecodeLocal(float2 e)
        {
            e = e * 2.0 - 1.0;
            float3 n = float3(e, 1.0 - abs(e.x) - abs(e.y));
            float t = max(-n.z, 0.0);
            n.x -= n.x >= 0.0 ? t : -t;
            n.y -= n.y >= 0.0 ? t : -t;
            return normalize(n);
        }
        float3 GiProjectToScreen(float3 viewPos) { float4 clip = mul(XENGINE_MATRIX_P, float4(viewPos, 1.0)); clip.xyz /= clip.w; return clip.xyz * 0.5 + 0.5; }
        float4 main(PSInput input) : SV_Target
        {
            int2 frag = (int2)input.position.xy;
            int2 tileCoord = frag / GI_PROBE_TILE_SIZE;
            int tilesPerRow = (int)_GiUpdateRange.z;
            if (tilesPerRow <= 0) discard;
            int probeIndex = tileCoord.y * tilesPerRow + tileCoord.x;
            int first = (int)_GiUpdateRange.x;
            if (probeIndex < first || probeIndex >= first + (int)_GiUpdateRange.y) discard;

            int cx = max((int)_GiProbeGridParams.x, 1);
            int cy = max((int)_GiProbeGridParams.y, 1);
            int3 cell = int3(probeIndex % cx, (probeIndex / cx) % cy, probeIndex / (cx * cy));
            float3 probeWorld = _GiProbeOriginSpacing.xyz + (float3)cell * _GiProbeOriginSpacing.w;

            float2 tileUV = ((float2)(frag % GI_PROBE_TILE_SIZE) + 0.5) / GI_PROBE_TILE_SIZE;
            float3 dirWorld = GiOctDecodeLocal(tileUV);

            float3 probeView = mul(XENGINE_MATRIX_V, float4(probeWorld, 1.0)).xyz;
            float3 dirView = normalize(mul((float3x3)XENGINE_MATRIX_V, dirWorld));
            float3 startScreen = GiProjectToScreen(probeView);
            if (startScreen.x < 0.0 || startScreen.x > 1.0 || startScreen.y < 0.0 || startScreen.y > 1.0)
                return float4(_GiMissColor.rgb, _GiUpdateRate);

            float3 endScreen = GiProjectToScreen(probeView + dirView * _GiProbeOriginSpacing.w * 8.0);
            float3 stepSz = (endScreen - startScreen) / 16.0;
            float3 cur = startScreen + stepSz;
            bool crossed = false;
            [loop] for (int i = 0; i < 16; i++)
            {
                if (cur.x < 0.0 || cur.x > 1.0 || cur.y < 0.0 || cur.y > 1.0) break;
                float sceneDepth = _CameraDepthTexture.Sample(_CameraDepthSampler, cur.xy).r;
                if (sceneDepth < 1.0 && cur.z > sceneDepth + 0.001) { crossed = true; break; }
                cur += stepSz;
            }
            if (crossed)
            {
                float3 lo = cur - stepSz, hi = cur;
                [unroll] for (int j = 0; j < 4; j++)
                {
                    float3 mid = (lo + hi) * 0.5;
                    if (mid.z > _CameraDepthTexture.Sample(_CameraDepthSampler, mid.xy).r) hi = mid; else lo = mid;
                }
                return float4(min(_MainTex.Sample(_MainTexSampler, hi.xy).rgb, float3(8.0, 8.0, 8.0)), _GiUpdateRate);
            }
            return float4(_GiMissColor.rgb, _GiUpdateRate);
        }
    }
    ENDSLANG
}
