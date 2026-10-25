Shader "Default/CinematicEffects"

Variants
{
}

Properties
{
}

Pass "CinematicEffects"
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

        layout(location = 0) out vec4 OutputColor;

        in vec2 TexCoords;

        uniform sampler2D _MainTex;
        uniform vec2 _Resolution;
        uniform int _EffectFlags;

        // ── Vignette ──────────────────────────────────────
        uniform float _VignetteIntensity;
        uniform float _VignetteSmoothness;
        uniform float _VignetteRoundness;

        // ── Chromatic Aberration ──────────────────────────
        uniform float _ChromaticIntensity;
        uniform float _ChromaticDistortion;

        // ── Film Grain ────────────────────────────────────
        uniform float _GrainIntensity;
        uniform float _GrainResponse;

        // ── Color Grading ─────────────────────────────────
        uniform float _PostExposure;
        uniform float _Contrast;
        uniform float _Saturation;
        uniform float _Temperature;
        uniform vec4 _Lift;
        uniform vec4 _Gamma;
        uniform vec4 _Gain;

        // ── LUT ───────────────────────────────────────────
        uniform sampler2D _LUTTex;
        uniform float _LUTContribution;
        uniform float _LUTSize;

        // ── Sharpen (CAS) ─────────────────────────────────
        uniform float _SharpenAmount;
        uniform float _SharpenRadius;

        // ── Edge Detection ────────────────────────────────
        uniform float _EdgeIntensity;
        uniform vec4 _EdgeColor;
        uniform float _EdgeBackgroundFade;

        // ── Pixelation ────────────────────────────────────
        uniform float _PixelSize;

        // ── God Rays ──────────────────────────────────────
        uniform float _GodRayIntensity;
        uniform float _GodRayDecay;
        uniform float _GodRayDensity;
        uniform float _GodRayWeight;
        uniform int _GodRaySamples;
        uniform float _GodRayThreshold;
        uniform vec2 _SunScreenPos;
        uniform sampler2D _CameraDepthTexture;

        const int EFFECT_VIGNETTE = 1 << 0;
        const int EFFECT_CHROMATIC_ABERRATION = 1 << 1;
        const int EFFECT_FILM_GRAIN = 1 << 2;
        const int EFFECT_COLOR_GRADING = 1 << 3;
        const int EFFECT_LUT = 1 << 4;
        const int EFFECT_SHARPEN = 1 << 5;
        const int EFFECT_EDGE_DETECTION = 1 << 6;
        const int EFFECT_PIXELATION = 1 << 7;
        const int EFFECT_GOD_RAYS = 1 << 8;

        bool hasEffect(int effectFlag) {
            return (_EffectFlags & effectFlag) != 0;
        }

        // ── Utility functions ─────────────────────────────

        float getLuminance(vec3 c) {
            return dot(c, vec3(0.2126, 0.7152, 0.0722));
        }

        // Hash-based noise
        float hash12(vec2 p) {
            vec3 p3 = fract(vec3(p.xyx) * 0.1031);
            p3 += dot(p3, p3.yzx + 33.33);
            return fract((p3.x + p3.y) * p3.z);
        }

        // ── Effect implementations ────────────────────────

        vec2 applyPixelation(vec2 uv) {
            vec2 pixelCount = _Resolution / _PixelSize;
            return floor(uv * pixelCount) / pixelCount;
        }

        vec3 applyChromaticAberration(vec2 uv) {
            // Barrel distortion-based chromatic aberration
            vec2 center = uv - 0.5;
            float dist2 = dot(center, center);

            // Apply barrel distortion per channel with different magnitudes
            float intensity = _ChromaticIntensity / _Resolution.x;
            float distortion = _ChromaticDistortion;

            // Red shifts outward, blue shifts inward
            vec2 offsetR = center * (1.0 + dist2 * distortion) * intensity;
            vec2 offsetB = center * (1.0 - dist2 * distortion) * intensity;

            float r = texture(_MainTex, uv + offsetR).r;
            float g = texture(_MainTex, uv).g;
            float b = texture(_MainTex, uv - offsetB).b;
            return vec3(r, g, b);
        }

        // Contrast Adaptive Sharpening (CAS)
        // Reference: Lou Kramer, FidelityFX CAS, AMD Developer Day 2019
        vec3 applyContrastAdaptiveSharpening(ivec2 texcoord) {
            int r = int(_SharpenRadius);
            vec3 a = texelFetch(_MainTex, texcoord + ivec2( 0, -1) * r, 0).rgb;
            vec3 b = texelFetch(_MainTex, texcoord + ivec2(-1,  0) * r, 0).rgb;
            vec3 c = texelFetch(_MainTex, texcoord, 0).rgb;
            vec3 d = texelFetch(_MainTex, texcoord + ivec2( 1,  0) * r, 0).rgb;
            vec3 e = texelFetch(_MainTex, texcoord + ivec2( 0,  1) * r, 0).rgb;

            float mn = min(a.g, min(b.g, min(c.g, min(d.g, e.g))));
            float mx = max(a.g, max(b.g, max(c.g, max(d.g, e.g))));

            float w = sqrt(min(1.0 - mx, mn) / max(mx, 0.001)) * mix(-0.125, -0.2, _SharpenAmount);
            return (w * (a + b + d + e) + c) / (4.0 * w + 1.0);
        }

        float sobelEdge(vec2 uv) {
            vec2 texel = 1.0 / _Resolution;

            float tl = getLuminance(texture(_MainTex, uv + vec2(-texel.x,  texel.y)).rgb);
            float t  = getLuminance(texture(_MainTex, uv + vec2(     0.0,  texel.y)).rgb);
            float tr = getLuminance(texture(_MainTex, uv + vec2( texel.x,  texel.y)).rgb);
            float l  = getLuminance(texture(_MainTex, uv + vec2(-texel.x,      0.0)).rgb);
            float r  = getLuminance(texture(_MainTex, uv + vec2( texel.x,      0.0)).rgb);
            float bl = getLuminance(texture(_MainTex, uv + vec2(-texel.x, -texel.y)).rgb);
            float b  = getLuminance(texture(_MainTex, uv + vec2(     0.0, -texel.y)).rgb);
            float br = getLuminance(texture(_MainTex, uv + vec2( texel.x, -texel.y)).rgb);

            float gx = -tl - 2.0*l - bl + tr + 2.0*r + br;
            float gy = -tl - 2.0*t - tr + bl + 2.0*b + br;

            return sqrt(gx*gx + gy*gy);
        }

        vec3 applyColorGrading(vec3 color) {
            // Post-exposure (EV stops)
            color *= exp2(_PostExposure);

            // Contrast (around mid-gray 0.18)
            color = mix(vec3(0.18), color, 1.0 + _Contrast);

            // Temperature (approximate Planckian locus shift)
            color.r *= 1.0 + _Temperature * 0.1;
            color.b *= 1.0 - _Temperature * 0.1;

            // Saturation
            float lum = getLuminance(color);
            color = mix(vec3(lum), color, 1.0 + _Saturation);

            // Lift / Gamma / Gain (ASC CDL style)
            // Lift affects shadows, Gain affects highlights, Gamma adjusts midtones
            vec3 lift = _Lift.rgb;
            vec3 gamma = _Gamma.rgb;
            vec3 gain = _Gain.rgb;

            // Apply gain (multiply)
            color *= (vec3(1.0) + gain);

            // Apply lift (add to shadows weighted by inverse luminance)
            color += lift * (1.0 - color);

            // Apply gamma (power curve on midtones)
            // gamma offset of 0 = no change, positive = brighten mids, negative = darken mids
            vec3 gammaExp = 1.0 / max(vec3(1.0) + gamma, vec3(0.01));
            color = pow(max(color, vec3(0.0)), gammaExp);

            return max(color, vec3(0.0));
        }

        // Sample a strip-format LUT texture (e.g., 256x16 for 16^3 LUT)
        vec3 applyLUT(vec3 color) {
            float size = _LUTSize;
            float invSize = 1.0 / size;
            float sliceSize = 1.0 / size; // width of one slice in UV space

            // Clamp input to valid range
            color = clamp(color, 0.0, 1.0);

            // Blue channel selects the slice pair
            float blueScaled = color.b * (size - 1.0);
            float sliceLow = floor(blueScaled);
            float sliceHigh = min(sliceLow + 1.0, size - 1.0);
            float sliceFrac = blueScaled - sliceLow;

            // Half-texel offset for proper sampling
            float halfTexel = 0.5 * invSize;

            // UV within a single slice (red = x, green = y)
            vec2 innerUV = vec2(
                halfTexel + color.r * (1.0 - invSize),
                halfTexel + color.g * (1.0 - invSize)
            );

            // Offset to the correct slice
            vec2 uv1 = vec2(innerUV.x * sliceSize + sliceLow * sliceSize, innerUV.y);
            vec2 uv2 = vec2(innerUV.x * sliceSize + sliceHigh * sliceSize, innerUV.y);

            vec3 graded1 = texture(_LUTTex, uv1).rgb;
            vec3 graded2 = texture(_LUTTex, uv2).rgb;

            vec3 graded = mix(graded1, graded2, sliceFrac);
            return mix(color, graded, _LUTContribution);
        }

        vec3 applyFilmGrain(vec3 color, vec2 uv) {
            float noise = hash12(uv * _Resolution + fract(_Time.y) * 1000.0) * 2.0 - 1.0;
            float lum = getLuminance(color);
            float response = mix(1.0, lum, _GrainResponse);
            color += noise * _GrainIntensity * response;
            return max(color, vec3(0.0));
        }

        vec3 applyVignette(vec3 color, vec2 uv) {
            vec2 d = abs(uv - 0.5) * 2.0;
            d = mix(d, vec2(length(d)), _VignetteRoundness);
            float vfactor = pow(clamp(1.0 - dot(d, d) * _VignetteIntensity, 0.0, 1.0), _VignetteSmoothness + 0.01);
            return color * vfactor;
        }

        vec3 applyGodRays(vec3 color, vec2 uv) {
            vec2 deltaUV = (uv - _SunScreenPos) * _GodRayDensity / float(_GodRaySamples);
            vec2 sampleUV = uv;
            float illumination = 0.0;
            float decay = 1.0;

            int sampleCount = min(_GodRaySamples, 128);
            for (int i = 0; i < sampleCount; i++) {
                sampleUV -= deltaUV;
                vec2 clampedUV = clamp(sampleUV, 0.001, 0.999);

                // Depth test sky pixels (depth ~1.0) contribute light
                float depth = texture(_CameraDepthTexture, clampedUV).r;
                float sky = step(0.9999, depth);

                // Luminance threshold only bright sky areas contribute
                vec3 sampleColor = texture(_MainTex, clampedUV).rgb;
                float brightness = getLuminance(sampleColor);
                float bright = sky * step(_GodRayThreshold, brightness);

                illumination += bright * decay * _GodRayWeight;
                decay *= _GodRayDecay;
            }

            // Warm-tinted light shafts
            return color + illumination * _GodRayIntensity * vec3(1.0, 0.95, 0.85);
        }

        void main()
        {
            vec2 uv = TexCoords;

            // ── Pixelation (modify UV first) ──────────────
            if (hasEffect(EFFECT_PIXELATION))
                uv = applyPixelation(uv);

            // ── Sample color ──────────────────────────────
            vec3 color;
            if (hasEffect(EFFECT_CHROMATIC_ABERRATION))
                color = applyChromaticAberration(uv);
            else
                color = texture(_MainTex, uv).rgb;

            float alpha = texture(_MainTex, TexCoords).a;

            // ── Sharpen (CAS) ─────────────────────────────
            if (hasEffect(EFFECT_SHARPEN))
                color = applyContrastAdaptiveSharpening(ivec2(uv * _Resolution));

            // ── Edge Detection ────────────────────────────
            if (hasEffect(EFFECT_EDGE_DETECTION)) {
                float edge = sobelEdge(uv) * _EdgeIntensity;
                edge = clamp(edge, 0.0, 1.0);
                color = mix(color * _EdgeBackgroundFade, _EdgeColor.rgb, edge);
            }

            // ── Color Grading ─────────────────────────────
            if (hasEffect(EFFECT_COLOR_GRADING))
                color = applyColorGrading(color);

            // ── LUT ───────────────────────────────────────
            if (hasEffect(EFFECT_LUT))
                color = applyLUT(color);

            // ── God Rays ──────────────────────────────────
            if (hasEffect(EFFECT_GOD_RAYS))
                color = applyGodRays(color, TexCoords);

            // ── Film Grain ────────────────────────────────
            if (hasEffect(EFFECT_FILM_GRAIN))
                color = applyFilmGrain(color, uv);

            // ── Vignette (last applies to final image) ──
            if (hasEffect(EFFECT_VIGNETTE))
                color = applyVignette(color, uv);

            OutputColor = vec4(color, alpha);
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
            output.position = float4(input.vertexPosition, 1.0);
            output.TexCoords = input.vertexTexCoord;
            return output;
        }
    }

    Fragment
    {
        #include "XEngineCG"

        cbuffer CinematicEffectsPS : register(b2)
        {
            float2 _Resolution;
            int _EffectFlags;
            float Padding0;
            float _VignetteIntensity;
            float _VignetteSmoothness;
            float _VignetteRoundness;
            float Padding1;
            float _ChromaticIntensity;
            float _ChromaticDistortion;
            float _GrainIntensity;
            float _GrainResponse;
            float _PostExposure;
            float _Contrast;
            float _Saturation;
            float _Temperature;
            float4 _Lift;
            float4 _Gamma;
            float4 _Gain;
            float _LUTContribution;
            float _LUTSize;
            float _SharpenAmount;
            float _SharpenRadius;
            float _EdgeIntensity;
            float _EdgeBackgroundFade;
            float _PixelSize;
            float _GodRayIntensity;
            float4 _EdgeColor;
            float _GodRayDecay;
            float _GodRayDensity;
            float _GodRayWeight;
            int _GodRaySamples;
            float _GodRayThreshold;
            float2 _SunScreenPos;
            float Padding2;
        };

        Texture2D _MainTex : register(t0);
        SamplerState _MainTexSampler : register(s0);
        Texture2D _LUTTex : register(t1);
        SamplerState _LUTTexSampler : register(s1);
        Texture2D _CameraDepthTexture : register(t2);
        SamplerState _CameraDepthSampler : register(s2);

        struct PSInput
        {
            float4 position : SV_Position;
            float2 TexCoords : TEXCOORD0;
        };

        float GetLuminance(float3 color) { return dot(color, float3(0.2126, 0.7152, 0.0722)); }

        static const int EFFECT_VIGNETTE = 1 << 0;
        static const int EFFECT_CHROMATIC_ABERRATION = 1 << 1;
        static const int EFFECT_FILM_GRAIN = 1 << 2;
        static const int EFFECT_COLOR_GRADING = 1 << 3;
        static const int EFFECT_LUT = 1 << 4;
        static const int EFFECT_SHARPEN = 1 << 5;
        static const int EFFECT_EDGE_DETECTION = 1 << 6;
        static const int EFFECT_PIXELATION = 1 << 7;
        static const int EFFECT_GOD_RAYS = 1 << 8;

        bool HasEffect(int effectFlag) { return (_EffectFlags & effectFlag) != 0; }

        float Hash12(float2 p)
        {
            float3 p3 = frac(float3(p.x, p.y, p.x) * 0.1031);
            p3 += dot(p3, p3.yzx + 33.33);
            return frac((p3.x + p3.y) * p3.z);
        }

        float2 ApplyPixelation(float2 uv)
        {
            float2 pixelCount = _Resolution / _PixelSize;
            return floor(uv * pixelCount) / pixelCount;
        }

        float3 ApplyChromaticAberration(float2 uv)
        {
            float2 center = uv - 0.5;
            float dist2 = dot(center, center);
            float intensity = _ChromaticIntensity / _Resolution.x;
            float2 offsetR = center * (1.0 + dist2 * _ChromaticDistortion) * intensity;
            float2 offsetB = center * (1.0 - dist2 * _ChromaticDistortion) * intensity;
            return float3(
                _MainTex.Sample(_MainTexSampler, uv + offsetR).r,
                _MainTex.Sample(_MainTexSampler, uv).g,
                _MainTex.Sample(_MainTexSampler, uv - offsetB).b);
        }

        float3 ApplyContrastAdaptiveSharpening(int2 texcoord)
        {
            int radius = (int)_SharpenRadius;
            float3 a = _MainTex.Load(int3(texcoord + int2(0, -1) * radius, 0)).rgb;
            float3 b = _MainTex.Load(int3(texcoord + int2(-1, 0) * radius, 0)).rgb;
            float3 c = _MainTex.Load(int3(texcoord, 0)).rgb;
            float3 d = _MainTex.Load(int3(texcoord + int2(1, 0) * radius, 0)).rgb;
            float3 e = _MainTex.Load(int3(texcoord + int2(0, 1) * radius, 0)).rgb;
            float minimum = min(a.g, min(b.g, min(c.g, min(d.g, e.g))));
            float maximum = max(a.g, max(b.g, max(c.g, max(d.g, e.g))));
            float weight = sqrt(min(1.0 - maximum, minimum) / max(maximum, 0.001)) * lerp(-0.125, -0.2, _SharpenAmount);
            return (weight * (a + b + d + e) + c) / (4.0 * weight + 1.0);
        }

        float SobelEdge(float2 uv)
        {
            float2 texel = 1.0 / _Resolution;
            float tl = GetLuminance(_MainTex.Sample(_MainTexSampler, uv + float2(-texel.x, texel.y)).rgb);
            float t = GetLuminance(_MainTex.Sample(_MainTexSampler, uv + float2(0.0, texel.y)).rgb);
            float tr = GetLuminance(_MainTex.Sample(_MainTexSampler, uv + texel).rgb);
            float l = GetLuminance(_MainTex.Sample(_MainTexSampler, uv + float2(-texel.x, 0.0)).rgb);
            float r = GetLuminance(_MainTex.Sample(_MainTexSampler, uv + float2(texel.x, 0.0)).rgb);
            float bl = GetLuminance(_MainTex.Sample(_MainTexSampler, uv - texel).rgb);
            float b = GetLuminance(_MainTex.Sample(_MainTexSampler, uv + float2(0.0, -texel.y)).rgb);
            float br = GetLuminance(_MainTex.Sample(_MainTexSampler, uv + float2(texel.x, -texel.y)).rgb);
            float gx = -tl - 2.0 * l - bl + tr + 2.0 * r + br;
            float gy = -tl - 2.0 * t - tr + bl + 2.0 * b + br;
            return sqrt(gx * gx + gy * gy);
        }

        float3 ApplyColorGrading(float3 color)
        {
            color *= exp2(_PostExposure);
            color = lerp(0.18.xxx, color, 1.0 + _Contrast);
            color.r *= 1.0 + _Temperature * 0.1;
            color.b *= 1.0 - _Temperature * 0.1;
            float luminance = GetLuminance(color);
            color = lerp(luminance.xxx, color, 1.0 + _Saturation);
            color *= 1.0.xxx + _Gain.rgb;
            color += _Lift.rgb * (1.0.xxx - color);
            float3 gammaExponent = 1.0.xxx / max(1.0.xxx + _Gamma.rgb, 0.01.xxx);
            return max(pow(max(color, 0.0.xxx), gammaExponent), 0.0.xxx);
        }

        float3 ApplyLUT(float3 color)
        {
            float size = _LUTSize;
            float inverseSize = 1.0 / size;
            color = saturate(color);
            float blueScaled = color.b * (size - 1.0);
            float sliceLow = floor(blueScaled);
            float sliceHigh = min(sliceLow + 1.0, size - 1.0);
            float sliceFraction = blueScaled - sliceLow;
            float halfTexel = 0.5 * inverseSize;
            float2 innerUV = float2(
                halfTexel + color.r * (1.0 - inverseSize),
                halfTexel + color.g * (1.0 - inverseSize));
            float2 uv1 = float2(innerUV.x * inverseSize + sliceLow * inverseSize, innerUV.y);
            float2 uv2 = float2(innerUV.x * inverseSize + sliceHigh * inverseSize, innerUV.y);
            float3 graded = lerp(
                _LUTTex.Sample(_LUTTexSampler, uv1).rgb,
                _LUTTex.Sample(_LUTTexSampler, uv2).rgb,
                sliceFraction);
            return lerp(color, graded, _LUTContribution);
        }

        float3 ApplyFilmGrain(float3 color, float2 uv)
        {
            float noise = Hash12(uv * _Resolution + frac(_Time.y) * 1000.0) * 2.0 - 1.0;
            float response = lerp(1.0, GetLuminance(color), _GrainResponse);
            return max(color + noise * _GrainIntensity * response, 0.0.xxx);
        }

        float3 ApplyVignette(float3 color, float2 uv)
        {
            float2 distanceFromCenter = abs(uv - 0.5) * 2.0;
            distanceFromCenter = lerp(distanceFromCenter, length(distanceFromCenter).xx, _VignetteRoundness);
            float factor = pow(saturate(1.0 - dot(distanceFromCenter, distanceFromCenter) * _VignetteIntensity), _VignetteSmoothness + 0.01);
            return color * factor;
        }

        float3 ApplyGodRays(float3 color, float2 uv)
        {
            float2 deltaUV = (uv - _SunScreenPos) * _GodRayDensity / float(_GodRaySamples);
            float2 sampleUV = uv;
            float illumination = 0.0;
            float decay = 1.0;
            int sampleCount = min(_GodRaySamples, 128);
            for (int i = 0; i < sampleCount; i++)
            {
                sampleUV -= deltaUV;
                float2 clampedUV = clamp(sampleUV, 0.001, 0.999);
                float sky = step(0.9999, _CameraDepthTexture.Sample(_CameraDepthSampler, clampedUV).r);
                float brightness = GetLuminance(_MainTex.Sample(_MainTexSampler, clampedUV).rgb);
                illumination += sky * step(_GodRayThreshold, brightness) * decay * _GodRayWeight;
                decay *= _GodRayDecay;
            }
            return color + illumination * _GodRayIntensity * float3(1.0, 0.95, 0.85);
        }

        float4 main(PSInput input) : SV_Target
        {
            float2 uv = input.TexCoords;
            if (HasEffect(EFFECT_PIXELATION))
                uv = ApplyPixelation(uv);

            float3 color;
            if (HasEffect(EFFECT_CHROMATIC_ABERRATION))
                color = ApplyChromaticAberration(uv);
            else
                color = _MainTex.Sample(_MainTexSampler, uv).rgb;
            float alpha = _MainTex.Sample(_MainTexSampler, input.TexCoords).a;
            if (HasEffect(EFFECT_SHARPEN))
                color = ApplyContrastAdaptiveSharpening((int2)(uv * _Resolution));
            if (HasEffect(EFFECT_EDGE_DETECTION))
            {
                float edge = saturate(SobelEdge(uv) * _EdgeIntensity);
                color = lerp(color * _EdgeBackgroundFade, _EdgeColor.rgb, edge);
            }
            if (HasEffect(EFFECT_COLOR_GRADING))
                color = ApplyColorGrading(color);
            if (HasEffect(EFFECT_LUT))
                color = ApplyLUT(color);
            if (HasEffect(EFFECT_GOD_RAYS))
                color = ApplyGodRays(color, input.TexCoords);
            if (HasEffect(EFFECT_FILM_GRAIN))
                color = ApplyFilmGrain(color, uv);
            if (HasEffect(EFFECT_VIGNETTE))
                color = ApplyVignette(color, uv);
            return float4(color, alpha);
        }
    }

    ENDSLANG
}
