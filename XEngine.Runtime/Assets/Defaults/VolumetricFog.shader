Shader "Default/VolumetricFog"

Variants
{
}

Properties
{
}

Pass "FogMarch"
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
        #include "Lighting"

        layout(location = 0) out vec4 OutputColor;

        in vec2 TexCoords;

        uniform sampler2D _MainTex;
        uniform vec2 _Resolution;
        uniform vec2 _LowResolution;
        uniform sampler2D _CameraDepthTexture;

        // Global fog uniforms
        uniform float _FogDensity;
        uniform float _FogScattering;
        uniform float _FogExtinction;
        uniform float _FogMaxDistance;
        uniform int _FogSteps;
        uniform vec4 _FogColorTint;
        uniform float _FogDithering;
        uniform vec4 _FogAmbientColor;
        uniform float _FogAmbientIntensity;

        // Type-level enable / shadow toggles. All BVH lights of an enabled type contribute to
        // fog scattering; per-light overrides were removed alongside the legacy FogLight
        // component when the BVH refactor landed.
        uniform int _FogEnableDirectional;
        uniform int _FogEnableDirectionalShadows;
        uniform int _FogEnablePointLights;
        uniform int _FogEnablePointShadows;
        uniform int _FogEnableSpotLights;
        uniform int _FogEnableSpotShadows;

        // Fog volumes
        #define MAX_FOG_VOLUMES 16
        uniform int _FogVolumeCount;
        uniform int _FogVolumeShape[MAX_FOG_VOLUMES];      // 0=Global, 1=Box, 2=Sphere, 3=Cylinder, 4=Cone
        uniform vec3 _FogVolumePosition[MAX_FOG_VOLUMES];
        uniform vec3 _FogVolumeSize[MAX_FOG_VOLUMES];
        uniform mat4 _FogVolumeWorldToLocal[MAX_FOG_VOLUMES];
        uniform float _FogVolumeDensity[MAX_FOG_VOLUMES];
        uniform vec4 _FogVolumeColor[MAX_FOG_VOLUMES];
        uniform float _FogVolumeFalloff[MAX_FOG_VOLUMES];
        uniform float _FogVolumeConeAngle[MAX_FOG_VOLUMES];

        // ── Phase function ──
        float HenyeyGreenstein(float cosTheta, float g)
        {
            float g2 = g * g;
            float denom = 1.0 + g2 - 2.0 * g * cosTheta;
            return (1.0 - g2) / (4.0 * 3.14159265 * pow(max(denom, 1e-4), 1.5));
        }

        // ── Camera reconstruction ──
        vec3 ReconstructWorldRay(vec2 uv)
        {
            vec4 clip = vec4(uv * 2.0 - 1.0, 1.0, 1.0);
            vec4 viewPos = XENGINE_MATRIX_I_P * clip;
            viewPos /= viewPos.w;
            vec3 worldDir = (XENGINE_MATRIX_I_V * vec4(viewPos.xyz, 0.0)).xyz;
            return normalize(worldDir);
        }

        float LinearEyeDepth(float rawDepth)
        {
            float zNear = _ProjectionParams.y;
            float zFar = _ProjectionParams.z;
            float z = rawDepth * 2.0 - 1.0;
            return (2.0 * zNear * zFar) / (zFar + zNear - z * (zFar - zNear));
        }

        // Returns world-space distance from camera to the geometry at this UV.
        // Different from LinearEyeDepth, which only gives the view-space Z component
        // (along camera forward). For off-axis pixels the actual world distance is
        // greater because the ray is at an angle.
        float WorldDistFromDepth(vec2 uv, float rawDepth)
        {
            vec4 clip = vec4(uv * 2.0 - 1.0, rawDepth * 2.0 - 1.0, 1.0);
            vec4 worldPos = XENGINE_MATRIX_I_VP * clip;
            worldPos.xyz /= worldPos.w;
            return distance(worldPos.xyz, _WorldSpaceCameraPos.xyz);
        }

        // ── Volumetric shadow sampling (no slope bias, no normal bias) ──
        float VolDirShadow(vec3 worldPos)
        {
            if (_CascadeCount == 0) return 0.0;

            float worldDistance = distance(worldPos, _WorldSpaceCameraPos.xyz) * 2.0;
            mat4 cascadeMatrix;
            vec4 cascadeParams;

            if (_CascadeCount >= 1 && worldDistance <= _CascadeAtlasParams0.w) {
                cascadeMatrix = _CascadeShadowMatrix0;
                cascadeParams = _CascadeAtlasParams0;
            } else if (_CascadeCount >= 2 && worldDistance <= _CascadeAtlasParams1.w) {
                cascadeMatrix = _CascadeShadowMatrix1;
                cascadeParams = _CascadeAtlasParams1;
            } else if (_CascadeCount >= 3 && worldDistance <= _CascadeAtlasParams2.w) {
                cascadeMatrix = _CascadeShadowMatrix2;
                cascadeParams = _CascadeAtlasParams2;
            } else if (_CascadeCount >= 4 && worldDistance <= _CascadeAtlasParams3.w) {
                cascadeMatrix = _CascadeShadowMatrix3;
                cascadeParams = _CascadeAtlasParams3;
            } else {
                if (_CascadeCount == 1)      { cascadeMatrix = _CascadeShadowMatrix0; cascadeParams = _CascadeAtlasParams0; }
                else if (_CascadeCount == 2) { cascadeMatrix = _CascadeShadowMatrix1; cascadeParams = _CascadeAtlasParams1; }
                else if (_CascadeCount == 3) { cascadeMatrix = _CascadeShadowMatrix2; cascadeParams = _CascadeAtlasParams2; }
                else                         { cascadeMatrix = _CascadeShadowMatrix3; cascadeParams = _CascadeAtlasParams3; }
            }

            if (cascadeParams.z <= 0.0) return 0.0;

            vec4 lightSpacePos = cascadeMatrix * vec4(worldPos, 1.0);
            vec3 projCoords = lightSpacePos.xyz / lightSpacePos.w;
            projCoords = projCoords * 0.5 + 0.5;
            if (projCoords.z > 1.0 || projCoords.x < 0.0 || projCoords.x > 1.0 || projCoords.y < 0.0 || projCoords.y > 1.0)
                return 0.0;

            vec2 atlasCoords, shadowMin, shadowMax;
            GetAtlasCoordinates(projCoords, cascadeParams, _ShadowAtlasSize.x, atlasCoords, shadowMin, shadowMax);

            float currentDepth = projCoords.z - max(_DirectionalLightShadowBias, 0.0005);
            float lit = texture(_ShadowAtlas, vec3(atlasCoords, currentDepth));
            return (1.0 - lit) * _DirectionalLightShadowStrength;
        }

        float VolPointShadow(LightSample L, int shadowSlot, vec3 worldPos)
        {
            vec3 lightToFrag = worldPos - L.Position;
            vec3 absDir = abs(lightToFrag);
            int faceIndex = 0;
            if (absDir.x >= absDir.y && absDir.x >= absDir.z)      faceIndex = lightToFrag.x > 0.0 ? 0 : 1;
            else if (absDir.y >= absDir.x && absDir.y >= absDir.z) faceIndex = lightToFrag.y > 0.0 ? 2 : 3;
            else                                                   faceIndex = lightToFrag.z > 0.0 ? 4 : 5;

            int idx = shadowSlot * 6 + faceIndex;
            mat4 shadowMatrix = _PointShadowMatrices[idx];
            vec4 faceParams = _PointShadowFaceParams[idx];

            vec4 lightSpacePos = shadowMatrix * vec4(worldPos, 1.0);
            vec3 projCoords = lightSpacePos.xyz / lightSpacePos.w;
            projCoords = projCoords * 0.5 + 0.5;
            if (projCoords.z > 1.0 || projCoords.x < 0.0 || projCoords.x > 1.0 || projCoords.y < 0.0 || projCoords.y > 1.0)
                return 0.0;

            vec2 atlasCoords, shadowMin, shadowMax;
            GetAtlasCoordinates(projCoords, faceParams, _ShadowAtlasSize.x, atlasCoords, shadowMin, shadowMax);

            float currentDepth = projCoords.z - max(L.ShadowBias, 0.0005);
            float lit = texture(_ShadowAtlas, vec3(atlasCoords, currentDepth));
            return (1.0 - lit) * L.ShadowStrength;
        }

        float VolSpotShadow(LightSample L, int shadowSlot, vec3 worldPos)
        {
            vec4 atlasParams = _SpotShadowAtlasParams[shadowSlot];
            if (atlasParams.z <= 0.0) return 0.0;

            vec4 lightSpacePos = _SpotShadowMatrices[shadowSlot] * vec4(worldPos, 1.0);
            vec3 projCoords = lightSpacePos.xyz / lightSpacePos.w;
            projCoords = projCoords * 0.5 + 0.5;
            if (projCoords.z > 1.0 || projCoords.x < 0.0 || projCoords.x > 1.0 || projCoords.y < 0.0 || projCoords.y > 1.0)
                return 0.0;

            vec2 atlasCoords, shadowMin, shadowMax;
            GetAtlasCoordinates(projCoords, atlasParams, _ShadowAtlasSize.x, atlasCoords, shadowMin, shadowMax);

            float currentDepth = projCoords.z - max(L.ShadowBias, 0.0005);
            float lit = texture(_ShadowAtlas, vec3(atlasCoords, currentDepth));
            return (1.0 - lit) * L.ShadowStrength;
        }

        float SpotAttenuation(LightSample L, vec3 lightDir)
        {
            // Cosines come pre-baked into the leaf data so we skip per-step trig here.
            float cosFromAxis = dot(-lightDir, normalize(L.Direction));
            return clamp((cosFromAxis - L.SpotCos) / max(L.InnerSpotCos - L.SpotCos, 1e-4), 0.0, 1.0);
        }

        // Physical inverse-square + smooth window cutoff. Matches Lighting.glsl exactly.
        float DistanceAttenuation(float dist, float range)
        {
            float dist2 = dist * dist;
            float invR2 = 1.0 / max(range * range, 1e-8);
            float factor = dist2 * invR2;
            float window = clamp(1.0 - factor * factor, 0.0, 1.0);
            window *= window;
            float invSqr = 1.0 / max(dist2, 0.01);
            return invSqr * window;
        }

        float Hash13(vec3 p)
        {
            p = fract(p * 0.1031);
            p += dot(p, p.yzx + 33.33);
            return fract((p.x + p.y) * p.z);
        }

        // ── Fog volume density evaluation ──
        // Returns density and color contribution at worldPos. Globals add their density
        // unconditionally; shaped volumes use SDF-based smooth falloff at edges.
        void EvaluateVolumes(vec3 worldPos, out float density, out vec3 color)
        {
            density = 0.0;
            color = vec3(0.0);
            float colorWeight = 0.0;

            int count = min(_FogVolumeCount, MAX_FOG_VOLUMES);
            for (int i = 0; i < count; i++)
            {
                int shape = _FogVolumeShape[i];
                float falloff = max(_FogVolumeFalloff[i], 0.001);
                float w = 0.0;

                if (shape == 0) {
                    // Global
                    w = 1.0;
                }
                else {
                    vec3 lp = (_FogVolumeWorldToLocal[i] * vec4(worldPos, 1.0)).xyz;
                    if (shape == 1) {
                        // Box: half-extents from world scale (already baked into local space)
                        vec3 d = abs(lp) - vec3(1.0);
                        float outside = length(max(d, 0.0));
                        float inside = min(max(d.x, max(d.y, d.z)), 0.0);
                        float sdf = outside + inside;
                        w = clamp(1.0 - sdf / falloff, 0.0, 1.0);
                    }
                    else if (shape == 2) {
                        // Sphere: radius 1 in local space
                        float r = length(lp);
                        w = clamp(1.0 - (r - 1.0) / falloff, 0.0, 1.0);
                    }
                    else if (shape == 3) {
                        // Cylinder: radius 1, height 1 (Y axis)
                        vec2 d = vec2(length(lp.xz) - 1.0, abs(lp.y) - 1.0);
                        float outside = length(max(d, 0.0));
                        float inside = min(max(d.x, d.y), 0.0);
                        float sdf = outside + inside;
                        w = clamp(1.0 - sdf / falloff, 0.0, 1.0);
                    }
                    else if (shape == 4) {
                        // Cone: apex at origin, axis +Y, height = 1, half-angle from uniform
                        float halfAngle = radians(_FogVolumeConeAngle[i]);
                        float cosA = cos(halfAngle);
                        float sinA = sin(halfAngle);
                        // distance from cone axis cap
                        vec2 q = vec2(length(lp.xz), lp.y);
                        // distance to slanted side
                        float side = q.x * cosA - q.y * sinA;
                        // distance to cap
                        float cap = q.y - 1.0;
                        // also need to be inside (apex pointing at origin, opening upward)
                        float sdf = max(side, max(cap, -lp.y));
                        w = clamp(1.0 - sdf / falloff, 0.0, 1.0);
                    }
                }

                float d = _FogVolumeDensity[i] * w;
                density += d;

                vec3 c = _FogVolumeColor[i].rgb;
                color += c * max(d, 0.0);
                colorWeight += max(d, 0.0);
            }

            color = colorWeight > 1e-4 ? color / colorWeight : vec3(1.0);
        }

        // Single local-light contribution (point or spot). Returns inscatter vec3 already
        // multiplied by the volume tint.
        vec3 ScatterFromLocalLight(LightSample L, vec3 worldPos, vec3 viewDir, vec3 volColor)
        {
            vec3 d = L.Position - worldPos;
            float dist = length(d);
            if (dist > L.Range) return vec3(0.0);
            vec3 toLight = d / max(dist, 1e-4);

            float att = DistanceAttenuation(dist, L.Range);
            if (L.Type == 2) att *= SpotAttenuation(L, toLight);
            if (att <= 0.0) return vec3(0.0);

            // Type-level enable / shadow toggles let users dim a whole light class without
            // touching every light individually.
            bool isPoint = (L.Type == 1);
            int  enableType   = isPoint ? _FogEnablePointLights : _FogEnableSpotLights;
            int  enableShadow = isPoint ? _FogEnablePointShadows : _FogEnableSpotShadows;
            if (enableType == 0) return vec3(0.0);

            float phase = HenyeyGreenstein(dot(viewDir, -toLight), _FogScattering);
            vec3 contrib = L.Color * (L.Intensity * 8.0) * phase * att;

            if (L.ShadowEnabled != 0 && L.ShadowSlot >= 0 && enableShadow != 0) {
                if (isPoint) contrib *= (1.0 - VolPointShadow(L, L.ShadowSlot, worldPos));
                else         contrib *= (1.0 - VolSpotShadow(L, L.ShadowSlot, worldPos));
            }

            return contrib * volColor;
        }

        // ── Per-step inscatter accumulation ──
        vec3 SampleScattering(vec3 worldPos, vec3 viewDir, vec3 volColor)
        {
            vec3 scatter = vec3(0.0);

            // Directional. _DirectionalLightDirection already points surface->sun.
            if (_DirectionalLightEnabled != 0 && _FogEnableDirectional != 0) {
                vec3 toLight = normalize(_DirectionalLightDirection);
                float phase = HenyeyGreenstein(dot(viewDir, -toLight), _FogScattering);
                vec3 contrib = _DirectionalLightColor * (_DirectionalLightIntensity * 8.0) * phase;
                if (_DirectionalLightShadowEnabled != 0 && _FogEnableDirectionalShadows != 0)
                    contrib *= (1.0 - VolDirShadow(worldPos));
                scatter += contrib * volColor;
            }

            // Static BVH.
            if (_StaticLightRoot >= 0) {
                LBVH_Iter it;
                LBVH_Begin(it, _StaticLightRoot);
                int slot;
                while ((slot = LBVH_Next(it, _StaticLightNodes, _StaticNodeTexSize, _StaticNodeTexShift, worldPos)) >= 0) {
                    LightSample L = LBVH_FetchLight(_StaticLightData, _StaticLightTexSize, _StaticLightTexShift, slot);
                    scatter += ScatterFromLocalLight(L, worldPos, viewDir, volColor);
                }
            }

            // Dynamic BVH.
            if (_DynamicLightRoot >= 0) {
                LBVH_Iter it;
                LBVH_Begin(it, _DynamicLightRoot);
                int slot;
                while ((slot = LBVH_Next(it, _DynamicLightNodes, _DynamicNodeTexSize, _DynamicNodeTexShift, worldPos)) >= 0) {
                    LightSample L = LBVH_FetchLight(_DynamicLightData, _DynamicLightTexSize, _DynamicLightTexShift, slot);
                    scatter += ScatterFromLocalLight(L, worldPos, viewDir, volColor);
                }
            }

            return scatter;
        }

        void main()
        {
            vec2 uv = TexCoords;

            float rawDepth = texture(_CameraDepthTexture, uv).r;

            // World distance from camera to the actual geometry (NOT eye-space Z).
            // We march along viewDir in world space, so we need world distance using
            // LinearEyeDepth here makes off-axis pixels under/overshoot the geometry,
            // causing fog to appear past surfaces (e.g. through the ground at oblique angles).
            float sceneDist = (rawDepth >= 0.9999) ? _FogMaxDistance : WorldDistFromDepth(uv, rawDepth);

            float marchDist = min(sceneDist, _FogMaxDistance);
            if (marchDist < 0.01) {
                OutputColor = vec4(0.0, 0.0, 0.0, 1.0);
                return;
            }

            vec3 cameraPos = _WorldSpaceCameraPos.xyz;
            vec3 viewDir = ReconstructWorldRay(uv);

            int steps = max(_FogSteps, 1);
            float stepSize = marchDist / float(steps);

            float jitter = InterleavedGradientNoise(gl_FragCoord.xy + _Time.y * 60.0);

            vec3 accum = vec3(0.0);
            float transmittance = 1.0;
            float t = stepSize * jitter;

            for (int i = 0; i < steps; i++)
            {
                vec3 samplePos = cameraPos + viewDir * t;

                // Density at this point: global + volumes (clamped to 0 if negatives carved it out)
                float volumeDensity;
                vec3 volColor;
                EvaluateVolumes(samplePos, volumeDensity, volColor);
                float density = max(_FogDensity + volumeDensity, 0.0);

                if (density > 0.0) {
                    vec3 lightInscatter = SampleScattering(samplePos, viewDir, volColor);
                    // Ambient adds a constant base keeps fog visible in shadow
                    // (sky bounce / GI approximation, not physically accurate).
                    vec3 ambient = _FogAmbientColor.rgb * _FogAmbientIntensity * volColor;
                    vec3 inscatter = (lightInscatter + ambient) * density;
                    inscatter *= _FogColorTint.rgb;

                    float stepTransmittance = exp(-density * _FogExtinction * stepSize);
                    accum += transmittance * inscatter * stepSize;
                    transmittance *= stepTransmittance;

                    if (transmittance < 0.01) break;
                }

                t += stepSize;
            }

            // Color dithering small per-pixel random shift to break up banding bands.
            if (_FogDithering > 0.0) {
                float dither = (Hash13(vec3(gl_FragCoord.xy, _Time.y)) - 0.5) * _FogDithering;
                accum += vec3(dither);
            }

            OutputColor = vec4(accum, transmittance);
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
            #include "XEngineCG"
            #include "LightBVH"
            #include "Shadow"

            cbuffer FogMarchPS : register(b2)
            {
                float4 _FogMarchParams0;
                float4 _FogColorTint;
                float4 _FogAmbientColor;
                float4 _FogMarchParams1;
                int4 _FogMarchFlags0;
                int4 _FogMarchFlags1;
                int4 _DirectionalFlags;
                float4 _DirectionalLightDirectionIntensity;
                float4 _DirectionalLightColorShadowStrength;
                float4 _DirectionalShadowParams;
                int4 _StaticBvhMeta;
                int4 _DynamicBvhMeta;
                int4 _BvhRoots;
                float4 _ShadowAtlasSize;
                float4x4 _CascadeShadowMatrices[4];
                float4 _CascadeAtlasParams[4];
                float4x4 _PointShadowMatrices[24];
                float4 _PointShadowFaceParams[24];
                float4x4 _SpotShadowMatrices[4];
                float4 _SpotShadowAtlasParams[4];
                int4 _FogVolumeShapes[4];
                float4x4 _FogVolumeWorldToLocal[16];
                float4 _FogVolumeDensities[4];
                float4 _FogVolumeColors[16];
                float4 _FogVolumeFalloffs[4];
                float4 _FogVolumeConeAngles[4];
            };

            Texture2D _CameraDepthTexture : register(t0);
            SamplerState _CameraDepthTextureSampler : register(s0);
            Texture2D _StaticLightData : register(t1);
            SamplerState _StaticLightDataSampler : register(s1);
            Texture2D _StaticLightNodes : register(t2);
            SamplerState _StaticLightNodesSampler : register(s2);
            Texture2D _DynamicLightData : register(t3);
            SamplerState _DynamicLightDataSampler : register(s3);
            Texture2D _DynamicLightNodes : register(t4);
            SamplerState _DynamicLightNodesSampler : register(s4);
            Texture2D _ShadowAtlas : register(t5);
            SamplerState _ShadowAtlasSampler : register(s5);

            struct PSInput
            {
                float4 position : SV_Position;
                float2 texCoords : TEXCOORD0;
            };

            float FogHenyeyGreenstein(float cosine, float anisotropy)
            {
                float g2 = anisotropy * anisotropy;
                float denominator = 1.0 + g2 - 2.0 * anisotropy * cosine;
                return (1.0 - g2) / (12.5663706 * pow(max(denominator, 1e-4), 1.5));
            }

            float3 FogReconstructWorldRay(float2 uv)
            {
                float4 viewPosition = mul(XENGINE_MATRIX_I_P, float4(uv * 2.0 - 1.0, 1.0, 1.0));
                viewPosition /= max(abs(viewPosition.w), 1e-6);
                return normalize(mul(XENGINE_MATRIX_I_V, float4(viewPosition.xyz, 0.0)).xyz);
            }

            float FogWorldDistanceFromDepth(float2 uv, float rawDepth)
            {
                float4 worldPosition = mul(XENGINE_MATRIX_I_VP, float4(uv * 2.0 - 1.0, rawDepth * 2.0 - 1.0, 1.0));
                worldPosition.xyz /= max(abs(worldPosition.w), 1e-6);
                return distance(worldPosition.xyz, _WorldSpaceCameraPos);
            }

            float FogInterleavedGradientNoise(float2 position)
            {
                float3 magic = float3(0.06711056, 0.00583715, 52.9829189);
                return frac(magic.z * frac(dot(position, magic.xy)));
            }

            float FogHash13(float3 position)
            {
                position = frac(position * 0.1031);
                position += dot(position, position.yzx + 33.33);
                return frac((position.x + position.y) * position.z);
            }

            float FogDirectionalShadow(float3 worldPosition)
            {
                int cascadeCount = _DirectionalFlags.z;
                if (cascadeCount <= 0)
                    return 0.0;

                float worldDistance = distance(worldPosition, _WorldSpaceCameraPos) * 2.0;
                int cascadeIndex = max(cascadeCount - 1, 0);
                if (cascadeCount >= 1 && worldDistance <= _CascadeAtlasParams[0].w) cascadeIndex = 0;
                else if (cascadeCount >= 2 && worldDistance <= _CascadeAtlasParams[1].w) cascadeIndex = 1;
                else if (cascadeCount >= 3 && worldDistance <= _CascadeAtlasParams[2].w) cascadeIndex = 2;
                else if (cascadeCount >= 4 && worldDistance <= _CascadeAtlasParams[3].w) cascadeIndex = 3;

                float4 cascadeParameters = _CascadeAtlasParams[cascadeIndex];
                if (cascadeParameters.z <= 0.0)
                    return 0.0;

                float4 lightPosition = mul(_CascadeShadowMatrices[cascadeIndex], float4(worldPosition, 1.0));
                float3 projected = lightPosition.xyz / max(abs(lightPosition.w), 1e-6) * 0.5 + 0.5;
                if (projected.z > 1.0 || any(projected.xy < 0.0) || any(projected.xy > 1.0))
                    return 0.0;

                float2 shadowMin;
                float2 shadowMax;
                float2 atlasUv = FogShadowAtlasUv(projected, cascadeParameters, _ShadowAtlasSize.x, shadowMin, shadowMax);
                float currentDepth = projected.z - max(_DirectionalShadowParams.x, 0.0005);
                return FogShadowAtlasSample(
                    _ShadowAtlas,
                    _ShadowAtlasSampler,
                    atlasUv,
                    shadowMin,
                    shadowMax,
                    currentDepth,
                    _DirectionalShadowParams.y,
                    _DirectionalLightColorShadowStrength.w);
            }

            int FogPointFace(float3 direction)
            {
                float3 absoluteDirection = abs(direction);
                if (absoluteDirection.x >= absoluteDirection.y && absoluteDirection.x >= absoluteDirection.z)
                    return direction.x > 0.0 ? 0 : 1;
                if (absoluteDirection.y >= absoluteDirection.x && absoluteDirection.y >= absoluteDirection.z)
                    return direction.y > 0.0 ? 2 : 3;
                return direction.z > 0.0 ? 4 : 5;
            }

            float FogPointShadow(FogLightSample light, float3 worldPosition)
            {
                int faceIndex = light.ShadowSlot * 6 + FogPointFace(worldPosition - light.Position);
                float4 faceParameters = _PointShadowFaceParams[faceIndex];
                float4 lightPosition = mul(_PointShadowMatrices[faceIndex], float4(worldPosition, 1.0));
                float3 projected = lightPosition.xyz / max(abs(lightPosition.w), 1e-6) * 0.5 + 0.5;
                if (projected.z > 1.0 || any(projected.xy < 0.0) || any(projected.xy > 1.0))
                    return 0.0;

                float2 shadowMin;
                float2 shadowMax;
                float2 atlasUv = FogShadowAtlasUv(projected, faceParameters, _ShadowAtlasSize.x, shadowMin, shadowMax);
                return FogShadowAtlasSample(
                    _ShadowAtlas,
                    _ShadowAtlasSampler,
                    atlasUv,
                    shadowMin,
                    shadowMax,
                    projected.z - max(light.ShadowBias, 0.0005),
                    light.ShadowQuality,
                    light.ShadowStrength);
            }

            float FogSpotShadow(FogLightSample light, float3 worldPosition)
            {
                float4 atlasParameters = _SpotShadowAtlasParams[light.ShadowSlot];
                if (atlasParameters.z <= 0.0)
                    return 0.0;

                float4 lightPosition = mul(_SpotShadowMatrices[light.ShadowSlot], float4(worldPosition, 1.0));
                float3 projected = lightPosition.xyz / max(abs(lightPosition.w), 1e-6) * 0.5 + 0.5;
                if (projected.z > 1.0 || any(projected.xy < 0.0) || any(projected.xy > 1.0))
                    return 0.0;

                float2 shadowMin;
                float2 shadowMax;
                float2 atlasUv = FogShadowAtlasUv(projected, atlasParameters, _ShadowAtlasSize.x, shadowMin, shadowMax);
                return FogShadowAtlasSample(
                    _ShadowAtlas,
                    _ShadowAtlasSampler,
                    atlasUv,
                    shadowMin,
                    shadowMax,
                    projected.z - max(light.ShadowBias, 0.0005),
                    light.ShadowQuality,
                    light.ShadowStrength);
            }

            float FogDistanceAttenuation(float distanceToLight, float range)
            {
                float distanceSquared = distanceToLight * distanceToLight;
                float factor = distanceSquared / max(range * range, 1e-8);
                float window = saturate(1.0 - factor * factor);
                return window * window / max(distanceSquared, 0.01);
            }

            float FogSpotAttenuation(FogLightSample light, float3 lightDirection)
            {
                float cosine = dot(-lightDirection, normalize(light.Direction));
                return saturate((cosine - light.SpotCos) / max(light.InnerSpotCos - light.SpotCos, 1e-4));
            }

            void FogEvaluateVolumes(float3 worldPosition, out float density, out float3 color)
            {
                density = 0.0;
                color = 0.0.xxx;
                float colorWeight = 0.0;
                int count = min(_FogMarchFlags1.w, 16);
                for (int index = 0; index < count; index++)
                {
                    int vectorIndex = index >> 2;
                    int componentIndex = index & 3;
                    int shape = _FogVolumeShapes[vectorIndex][componentIndex];
                    float falloff = max(_FogVolumeFalloffs[vectorIndex][componentIndex], 0.001);
                    float weight = 0.0;
                    if (shape == 0)
                    {
                        weight = 1.0;
                    }
                    else
                    {
                        float3 localPosition = mul(_FogVolumeWorldToLocal[index], float4(worldPosition, 1.0)).xyz;
                        if (shape == 1)
                        {
                            float3 distanceToBox = abs(localPosition) - 1.0.xxx;
                            float signedDistance = length(max(distanceToBox, 0.0.xxx)) + min(max(distanceToBox.x, max(distanceToBox.y, distanceToBox.z)), 0.0);
                            weight = saturate(1.0 - signedDistance / falloff);
                        }
                        else if (shape == 2)
                        {
                            weight = saturate(1.0 - (length(localPosition) - 1.0) / falloff);
                        }
                        else if (shape == 3)
                        {
                            float2 distanceToCylinder = float2(length(localPosition.xz) - 1.0, abs(localPosition.y) - 1.0);
                            float signedDistance = length(max(distanceToCylinder, 0.0.xx)) + min(max(distanceToCylinder.x, distanceToCylinder.y), 0.0);
                            weight = saturate(1.0 - signedDistance / falloff);
                        }
                        else if (shape == 4)
                        {
                            float halfAngle = radians(_FogVolumeConeAngles[vectorIndex][componentIndex]);
                            float side = length(localPosition.xz) * cos(halfAngle) - localPosition.y * sin(halfAngle);
                            float signedDistance = max(side, max(localPosition.y - 1.0, -localPosition.y));
                            weight = saturate(1.0 - signedDistance / falloff);
                        }
                    }

                    float volumeDensity = _FogVolumeDensities[vectorIndex][componentIndex] * weight;
                    density += volumeDensity;
                    float positiveDensity = max(volumeDensity, 0.0);
                    color += _FogVolumeColors[index].rgb * positiveDensity;
                    colorWeight += positiveDensity;
                }
                color = colorWeight > 1e-4 ? color / colorWeight : 1.0.xxx;
            }

            float3 FogScatterLocalLight(FogLightSample light, float3 worldPosition, float3 viewDirection, float3 volumeColor)
            {
                float3 delta = light.Position - worldPosition;
                float distanceToLight = length(delta);
                if (distanceToLight > light.Range)
                    return 0.0.xxx;

                float3 toLight = delta / max(distanceToLight, 1e-4);
                float attenuation = FogDistanceAttenuation(distanceToLight, light.Range);
                if (light.Type == 2)
                    attenuation *= FogSpotAttenuation(light, toLight);
                if (attenuation <= 0.0)
                    return 0.0.xxx;

                bool pointLight = light.Type == 1;
                int enabled = pointLight ? _FogMarchFlags0.w : _FogMarchFlags1.y;
                int shadowsEnabled = pointLight ? _FogMarchFlags1.x : _FogMarchFlags1.z;
                if (enabled == 0)
                    return 0.0.xxx;

                float phase = FogHenyeyGreenstein(dot(viewDirection, -toLight), _FogMarchParams0.y);
                float3 contribution = light.Color * (light.Intensity * 8.0) * phase * attenuation;
                if (light.ShadowEnabled != 0 && light.ShadowSlot >= 0 && shadowsEnabled != 0)
                    contribution *= 1.0 - (pointLight ? FogPointShadow(light, worldPosition) : FogSpotShadow(light, worldPosition));
                return contribution * volumeColor;
            }

            float3 FogSampleScattering(float3 worldPosition, float3 viewDirection, float3 volumeColor)
            {
                float3 scattering = 0.0.xxx;
                if (_DirectionalFlags.x != 0 && _FogMarchFlags0.y != 0)
                {
                    float3 toLight = normalize(_DirectionalLightDirectionIntensity.xyz);
                    float phase = FogHenyeyGreenstein(dot(viewDirection, -toLight), _FogMarchParams0.y);
                    float3 contribution = _DirectionalLightColorShadowStrength.rgb * (_DirectionalLightDirectionIntensity.w * 8.0) * phase;
                    if (_DirectionalFlags.y != 0 && _FogMarchFlags0.z != 0)
                        contribution *= 1.0 - FogDirectionalShadow(worldPosition);
                    scattering += contribution * volumeColor;
                }

                if (_BvhRoots.x >= 0 && _StaticBvhMeta.x > 0 && _StaticBvhMeta.z > 0)
                {
                    FogBvhIterator iterator;
                    FogBvhBegin(iterator, _BvhRoots.x);
                    int slot;
                    while ((slot = FogBvhNext(iterator, _StaticLightNodes, _StaticBvhMeta.z, _StaticBvhMeta.w, worldPosition)) >= 0)
                    {
                        FogLightSample light = FogBvhFetchLight(_StaticLightData, _StaticBvhMeta.x, _StaticBvhMeta.y, slot);
                        scattering += FogScatterLocalLight(light, worldPosition, viewDirection, volumeColor);
                    }
                }

                if (_BvhRoots.y >= 0 && _DynamicBvhMeta.x > 0 && _DynamicBvhMeta.z > 0)
                {
                    FogBvhIterator iterator;
                    FogBvhBegin(iterator, _BvhRoots.y);
                    int slot;
                    while ((slot = FogBvhNext(iterator, _DynamicLightNodes, _DynamicBvhMeta.z, _DynamicBvhMeta.w, worldPosition)) >= 0)
                    {
                        FogLightSample light = FogBvhFetchLight(_DynamicLightData, _DynamicBvhMeta.x, _DynamicBvhMeta.y, slot);
                        scattering += FogScatterLocalLight(light, worldPosition, viewDirection, volumeColor);
                    }
                }
                return scattering;
            }

            float4 main(PSInput input) : SV_Target
            {
                float rawDepth = _CameraDepthTexture.Sample(_CameraDepthTextureSampler, input.texCoords).r;
                float sceneDistance = rawDepth >= 0.9999
                    ? _FogMarchParams0.w
                    : FogWorldDistanceFromDepth(input.texCoords, rawDepth);
                float marchDistance = min(sceneDistance, _FogMarchParams0.w);
                if (marchDistance < 0.01)
                    return float4(0.0, 0.0, 0.0, 1.0);

                float3 viewDirection = FogReconstructWorldRay(input.texCoords);
                int steps = max(_FogMarchFlags0.x, 1);
                float stepSize = marchDistance / steps;
                float position = stepSize * FogInterleavedGradientNoise(input.position.xy + _Time.y * 60.0);
                float3 accumulated = 0.0.xxx;
                float transmittance = 1.0;

                for (int step = 0; step < steps; step++)
                {
                    float3 samplePosition = _WorldSpaceCameraPos + viewDirection * position;
                    float volumeDensity;
                    float3 volumeColor;
                    FogEvaluateVolumes(samplePosition, volumeDensity, volumeColor);
                    float density = max(_FogMarchParams0.x + volumeDensity, 0.0);
                    if (density > 0.0)
                    {
                        float3 lightScattering = FogSampleScattering(samplePosition, viewDirection, volumeColor);
                        float3 ambient = _FogAmbientColor.rgb * _FogMarchParams1.y * volumeColor;
                        float3 inscattering = (lightScattering + ambient) * density * _FogColorTint.rgb;
                        float stepTransmittance = exp(-density * _FogMarchParams0.z * stepSize);
                        accumulated += transmittance * inscattering * stepSize;
                        transmittance *= stepTransmittance;
                        if (transmittance < 0.01)
                            break;
                    }
                    position += stepSize;
                }

                if (_FogMarchParams1.x > 0.0)
                    accumulated += (FogHash13(float3(input.position.xy, _Time.y)) - 0.5) * _FogMarchParams1.x;
                return float4(accumulated, transmittance);
            }
        }
    ENDSLANG

}

Pass "FogTemporal"
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

        uniform sampler2D _FogCurrentTex;   // this frame's low-res march result
        uniform sampler2D _FogHistoryTex;   // previous frame's (already temporally blended) result
        uniform sampler2D _CameraDepthTexture;
        uniform vec2 _LowResolution;
        uniform float _FogHistoryValid;     // 0 on first frame / after resize, 1 otherwise
        uniform float _FogTemporalBlend;    // history weight (0..0.99)

        void main()
        {
            vec2 uv = TexCoords;
            vec4 current = texture(_FogCurrentTex, uv);

            if (_FogHistoryValid < 0.5) {
                OutputColor = current;
                return;
            }

            // Reproject this pixel's world position into the previous frame's screen space.
            // Uses the scene depth at the current pixel (not the fog march distance) since the
            // fog volume effectively sits on the geometry behind each pixel.
            float depth = texture(_CameraDepthTexture, uv).r;
            vec3 prev = Reproject(uv, depth, XENGINE_MATRIX_VP_PREVIOUS);
            vec2 prevUV = prev.xy;

            if (prevUV.x < 0.0 || prevUV.x > 1.0 || prevUV.y < 0.0 || prevUV.y > 1.0) {
                OutputColor = current;
                return;
            }

            vec4 history = texture(_FogHistoryTex, prevUV);

            // Neighborhood clamp: limit history to the min/max of the 3x3 current
            // neighborhood. Handles disocclusion and fast-moving lights without
            // needing previous-frame depth / motion vectors.
            vec2 texel = 1.0 / _LowResolution;
            vec4 mn = current;
            vec4 mx = current;
            for (int dx = -1; dx <= 1; dx++) {
                for (int dy = -1; dy <= 1; dy++) {
                    if (dx == 0 && dy == 0) continue;
                    vec4 s = texture(_FogCurrentTex, uv + vec2(float(dx), float(dy)) * texel);
                    mn = min(mn, s);
                    mx = max(mx, s);
                }
            }
            history = clamp(history, mn, mx);

            float alpha = clamp(_FogTemporalBlend, 0.0, 0.99);
            OutputColor = mix(current, history, alpha);
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
            #include "XEngineCG"

            cbuffer FogTemporalPS : register(b2)
            {
                float2 _LowResolution;
                float _FogHistoryValid;
                float _FogTemporalBlend;
            };

            Texture2D _FogCurrentTex : register(t0);
            SamplerState _FogCurrentTexSampler : register(s0);
            Texture2D _FogHistoryTex : register(t1);
            SamplerState _FogHistoryTexSampler : register(s1);
            Texture2D _CameraDepthTexture : register(t2);
            SamplerState _CameraDepthTextureSampler : register(s2);

            struct PSInput
            {
                float4 position : SV_Position;
                float2 texCoords : TEXCOORD0;
            };

            float2 ReprojectUv(float2 uv, float depth)
            {
                float4 world = mul(XENGINE_MATRIX_I_VP, float4(uv * 2.0 - 1.0, depth * 2.0 - 1.0, 1.0));
                world.xyz /= max(abs(world.w), 1e-6);
                float4 previous = mul(XENGINE_MATRIX_VP_PREVIOUS, float4(world.xyz, 1.0));
                return previous.xy / max(abs(previous.w), 1e-6) * 0.5 + 0.5;
            }

            float4 main(PSInput input) : SV_Target
            {
                float2 uv = input.texCoords;
                float4 current = _FogCurrentTex.Sample(_FogCurrentTexSampler, uv);
                if (_FogHistoryValid < 0.5)
                    return current;

                float2 previousUv = ReprojectUv(uv, _CameraDepthTexture.Sample(_CameraDepthTextureSampler, uv).r);
                if (any(previousUv < 0.0) || any(previousUv > 1.0))
                    return current;

                float4 history = _FogHistoryTex.Sample(_FogHistoryTexSampler, previousUv);
                float2 texel = rcp(max(_LowResolution, 1.0.xx));
                float4 minimum = current;
                float4 maximum = current;
                for (int dx = -1; dx <= 1; dx++)
                {
                    for (int dy = -1; dy <= 1; dy++)
                    {
                        if (dx == 0 && dy == 0) continue;
                        float4 sample = _FogCurrentTex.Sample(_FogCurrentTexSampler, uv + float2(dx, dy) * texel);
                        minimum = min(minimum, sample);
                        maximum = max(maximum, sample);
                    }
                }
                history = clamp(history, minimum, maximum);
                return lerp(current, history, saturate(min(_FogTemporalBlend, 0.99)));
            }
        }
    ENDSLANG

}

Pass "FogComposite"
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
        uniform sampler2D _FogTex;
        uniform sampler2D _CameraDepthTexture;
        uniform vec2 _Resolution;
        uniform vec2 _LowResolution;
        uniform float _FogUpsampleThreshold;

        float LinearEyeDepthC(float rawDepth)
        {
            float zNear = _ProjectionParams.y;
            float zFar = _ProjectionParams.z;
            float z = rawDepth * 2.0 - 1.0;
            return (2.0 * zNear * zFar) / (zFar + zNear - z * (zFar - zNear));
        }

        // Depth-aware nearest-tap upsample. Picks the half-res tap whose depth is
        // closest to the full-res depth avoids bleeding fog across silhouettes
        // where bilinear blending would mix fog from sky pixels into ground pixels.
        vec4 BilateralUpsample(vec2 uv, float refDepth)
        {
            vec2 lowTexel = 1.0 / _LowResolution;
            vec2 offsets[4] = vec2[](
                vec2(-0.5, -0.5),
                vec2( 0.5, -0.5),
                vec2(-0.5,  0.5),
                vec2( 0.5,  0.5)
            );

            float bestDiff = 1e30;
            int bestIdx = 0;
            float depths[4];

            for (int i = 0; i < 4; i++)
            {
                vec2 sampleUV = uv + offsets[i] * lowTexel;
                depths[i] = LinearEyeDepthC(texture(_CameraDepthTexture, sampleUV).r);
                float diff = abs(depths[i] - refDepth);
                if (diff < bestDiff) { bestDiff = diff; bestIdx = i; }
            }

            float relDiff = bestDiff / max(refDepth, 1.0);

            // If the best match is within threshold, blend the matching neighbors bilinearly.
            // Otherwise fall back to point sampling the best tap to avoid edge bleed.
            if (relDiff < _FogUpsampleThreshold) {
                vec4 sumColor = vec4(0.0);
                float sumWeight = 0.0;
                for (int i = 0; i < 4; i++)
                {
                    float dDiff = abs(depths[i] - refDepth) / max(refDepth, 1.0);
                    float w = exp(-dDiff / max(_FogUpsampleThreshold, 1e-4));
                    vec2 sampleUV = uv + offsets[i] * lowTexel;
                    sumColor += texture(_FogTex, sampleUV) * w;
                    sumWeight += w;
                }
                return sumColor / max(sumWeight, 1e-4);
            }
            else {
                vec2 sampleUV = uv + offsets[bestIdx] * lowTexel;
                return texture(_FogTex, sampleUV);
            }
        }

        void main()
        {
            vec3 sceneColor = texture(_MainTex, TexCoords).rgb;
            float alpha = texture(_MainTex, TexCoords).a;

            float refDepth = LinearEyeDepthC(texture(_CameraDepthTexture, TexCoords).r);
            vec4 fog = BilateralUpsample(TexCoords, refDepth);

            // fog.rgb = scattering, fog.a = transmittance
            vec3 finalColor = sceneColor * fog.a + fog.rgb;
            OutputColor = vec4(finalColor, alpha);
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
            #include "XEngineCG"

            cbuffer FogCompositePS : register(b2)
            {
                float2 _Resolution;
                float2 _LowResolution;
                float _FogUpsampleThreshold;
                float3 _FogCompositePadding;
            };

            Texture2D _MainTex : register(t0);
            SamplerState _MainTexSampler : register(s0);
            Texture2D _FogTex : register(t1);
            SamplerState _FogTexSampler : register(s1);
            Texture2D _CameraDepthTexture : register(t2);
            SamplerState _CameraDepthTextureSampler : register(s2);

            struct PSInput
            {
                float4 position : SV_Position;
                float2 texCoords : TEXCOORD0;
            };

            float LinearEyeDepth(float rawDepth)
            {
                float zNear = _ProjectionParams.y;
                float zFar = _ProjectionParams.z;
                float z = rawDepth * 2.0 - 1.0;
                return (2.0 * zNear * zFar) / max(zFar + zNear - z * (zFar - zNear), 1e-6);
            }

            float4 BilateralUpsample(float2 uv, float referenceDepth)
            {
                static const float2 offsets[4] =
                {
                    float2(-0.5, -0.5), float2(0.5, -0.5),
                    float2(-0.5, 0.5), float2(0.5, 0.5)
                };
                float2 lowTexel = rcp(max(_LowResolution, 1.0.xx));
                float bestDifference = 1e30;
                int bestIndex = 0;
                float depths[4];
                for (int i = 0; i < 4; i++)
                {
                    float2 sampleUv = uv + offsets[i] * lowTexel;
                    depths[i] = LinearEyeDepth(_CameraDepthTexture.Sample(_CameraDepthTextureSampler, sampleUv).r);
                    float difference = abs(depths[i] - referenceDepth);
                    if (difference < bestDifference)
                    {
                        bestDifference = difference;
                        bestIndex = i;
                    }
                }

                float relativeDifference = bestDifference / max(referenceDepth, 1.0);
                if (relativeDifference < _FogUpsampleThreshold)
                {
                    float4 sum = 0.0.xxxx;
                    float weightSum = 0.0;
                    for (int i = 0; i < 4; i++)
                    {
                        float difference = abs(depths[i] - referenceDepth) / max(referenceDepth, 1.0);
                        float weight = exp(-difference / max(_FogUpsampleThreshold, 1e-4));
                        sum += _FogTex.Sample(_FogTexSampler, uv + offsets[i] * lowTexel) * weight;
                        weightSum += weight;
                    }
                    return sum / max(weightSum, 1e-4);
                }
                return _FogTex.Sample(_FogTexSampler, uv + offsets[bestIndex] * lowTexel);
            }

            float4 main(PSInput input) : SV_Target
            {
                float4 scene = _MainTex.Sample(_MainTexSampler, input.texCoords);
                float referenceDepth = LinearEyeDepth(_CameraDepthTexture.Sample(_CameraDepthTextureSampler, input.texCoords).r);
                float4 fog = BilateralUpsample(input.texCoords, referenceDepth);
                return float4(scene.rgb * fog.a + fog.rgb, scene.a);
            }
        }
    ENDSLANG
}
