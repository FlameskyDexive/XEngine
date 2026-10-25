Shader "Default/Terrain"

Variants
{
    Variant("GPU_INSTANCING")
    Variant("TERRAIN_8_LAYERS")
    Variant("TERRAIN_BICUBIC")
    Variant("GPU_INSTANCING", "TERRAIN_8_LAYERS")
    Variant("GPU_INSTANCING", "TERRAIN_BICUBIC")
    Variant("TERRAIN_8_LAYERS", "TERRAIN_BICUBIC")
    Variant("GPU_INSTANCING", "TERRAIN_8_LAYERS", "TERRAIN_BICUBIC")
}

Properties
{
    _Heightmap ("Heightmap", Texture2D) = "black"
    _Splatmap0 ("Splatmap 0 (Layers 0-3)", Texture2D) = "white"
    _Splatmap1 ("Splatmap 1 (Layers 4-7)", Texture2D) = "black"
    _HolesMap ("Holes Map", Texture2D) = "white"
    _HasHoles ("Has Holes", Int) = 0
    _LayerCount ("Layer Count", Int) = 4
    _Layer0 ("Layer 0 Albedo", Texture2D) = "white"
    _Layer0Normal ("Layer 0 Normal", Texture2D) = "normal"
    _Layer0Tiling ("Layer 0 Tiling", Float) = 10.0
    _Layer0Roughness ("Layer 0 Roughness", Float) = 1.0
    _Layer0Metallic ("Layer 0 Metallic", Float) = 0.0
    _Layer1 ("Layer 1 Albedo", Texture2D) = "white"
    _Layer1Normal ("Layer 1 Normal", Texture2D) = "normal"
    _Layer1Tiling ("Layer 1 Tiling", Float) = 10.0
    _Layer1Roughness ("Layer 1 Roughness", Float) = 1.0
    _Layer1Metallic ("Layer 1 Metallic", Float) = 0.0
    _Layer2 ("Layer 2 Albedo", Texture2D) = "white"
    _Layer2Normal ("Layer 2 Normal", Texture2D) = "normal"
    _Layer2Tiling ("Layer 2 Tiling", Float) = 10.0
    _Layer2Roughness ("Layer 2 Roughness", Float) = 1.0
    _Layer2Metallic ("Layer 2 Metallic", Float) = 0.0
    _Layer3 ("Layer 3 Albedo", Texture2D) = "white"
    _Layer3Normal ("Layer 3 Normal", Texture2D) = "normal"
    _Layer3Tiling ("Layer 3 Tiling", Float) = 10.0
    _Layer3Roughness ("Layer 3 Roughness", Float) = 1.0
    _Layer3Metallic ("Layer 3 Metallic", Float) = 0.0
    _Layer4 ("Layer 4 Albedo", Texture2D) = "white"
    _Layer4Normal ("Layer 4 Normal", Texture2D) = "normal"
    _Layer4Tiling ("Layer 4 Tiling", Float) = 10.0
    _Layer4Roughness ("Layer 4 Roughness", Float) = 1.0
    _Layer4Metallic ("Layer 4 Metallic", Float) = 0.0
    _Layer5 ("Layer 5 Albedo", Texture2D) = "white"
    _Layer5Normal ("Layer 5 Normal", Texture2D) = "normal"
    _Layer5Tiling ("Layer 5 Tiling", Float) = 10.0
    _Layer5Roughness ("Layer 5 Roughness", Float) = 1.0
    _Layer5Metallic ("Layer 5 Metallic", Float) = 0.0
    _Layer6 ("Layer 6 Albedo", Texture2D) = "white"
    _Layer6Normal ("Layer 6 Normal", Texture2D) = "normal"
    _Layer6Tiling ("Layer 6 Tiling", Float) = 10.0
    _Layer6Roughness ("Layer 6 Roughness", Float) = 1.0
    _Layer6Metallic ("Layer 6 Metallic", Float) = 0.0
    _Layer7 ("Layer 7 Albedo", Texture2D) = "white"
    _Layer7Normal ("Layer 7 Normal", Texture2D) = "normal"
    _Layer7Tiling ("Layer 7 Tiling", Float) = 10.0
    _Layer7Roughness ("Layer 7 Roughness", Float) = 1.0
    _Layer7Metallic ("Layer 7 Metallic", Float) = 0.0
    _TerrainSize ("Terrain Size", Float) = 1024.0
    _TerrainHeight ("Terrain Height", Float) = 100.0
    _BrushPosition ("Brush Position", Vector2) = (0.0, 0.0)
    _BrushRadius ("Brush Radius", Float) = 0.0
    _BrushFalloff ("Brush Falloff", Float) = 0.5
    _BrushVisible ("Brush Visible", Float) = 0
}

Pass "Terrain"
{
    Tags { "RenderOrder" = "Opaque" }
    Cull Back
    ZWrite On
    Blend Off

    GLSLPROGRAM

        Vertex
        {
            #include "XEngineCG"
            #include "VertexAttributes"

            out vec2 texCoord0;
            out vec3 worldPos;
            out vec3 worldNormal;


            uniform sampler2D _Heightmap;
            uniform float _TerrainSize;
            uniform float _TerrainHeight;
            uniform mat4 _TerrainWorldToLocal;
            uniform mat4 _TerrainLocalToWorld;

            // Vertex UV -> texel-center UV remap
            vec2 hmSampleUV(vec2 uv)
            {
                vec2 s = vec2(textureSize(_Heightmap, 0));
                return uv * (s - 1.0) / s + 0.5 / s;
            }

#ifdef TERRAIN_BICUBIC
            // Bicubic B-spline filtering using 4 bilinear taps (GPU-friendly)
            // Based on the "Fast Cubic Filtering" technique by Sigg & Hadwiger
            float sampleHeightBicubic(vec2 uv)
            {
                vec2 texSize = vec2(textureSize(_Heightmap, 0));
                vec2 invTexSize = 1.0 / texSize;

                // Transform to texel space
                vec2 coord = uv * texSize - 0.5;
                vec2 f = fract(coord);
                coord -= f;

                // Catmull-Rom weights from cubic B-spline
                vec2 f2 = f * f;
                vec2 f3 = f2 * f;

                // w0 = -0.5*t^3 + t^2 - 0.5*t
                // w1 =  1.5*t^3 - 2.5*t^2 + 1
                // w2 = -1.5*t^3 + 2*t^2 + 0.5*t
                // w3 =  0.5*t^3 - 0.5*t^2
                vec2 w0 = -0.5 * f3 + f2 - 0.5 * f;
                vec2 w1 =  1.5 * f3 - 2.5 * f2 + 1.0;
                vec2 w2 = -1.5 * f3 + 2.0 * f2 + 0.5 * f;
                vec2 w3 =  0.5 * f3 - 0.5 * f2;

                // Combine pairs for 4-tap bilinear trick
                vec2 s0 = w0 + w1;
                vec2 s1 = w2 + w3;
                vec2 f0 = w1 / s0;
                vec2 f1 = w3 / s1;

                // Compute the 4 sample positions (leveraging bilinear filtering)
                vec2 t0 = (coord - 0.5 + f0) * invTexSize + 0.5 * invTexSize;
                vec2 t1 = (coord + 1.5 + f1) * invTexSize + 0.5 * invTexSize;

                // 4 bilinear taps
                float h00 = texture(_Heightmap, vec2(t0.x, t0.y)).r;
                float h10 = texture(_Heightmap, vec2(t1.x, t0.y)).r;
                float h01 = texture(_Heightmap, vec2(t0.x, t1.y)).r;
                float h11 = texture(_Heightmap, vec2(t1.x, t1.y)).r;

                // Blend
                float row0 = mix(h00, h10, s1.x / (s0.x + s1.x));
                float row1 = mix(h01, h11, s1.x / (s0.x + s1.x));
                return mix(row0, row1, s1.y / (s0.y + s1.y));
            }

            float sampleHeight(vec2 uv) { return sampleHeightBicubic(uv) * _TerrainHeight; }
#else
            float sampleHeight(vec2 uv) { return texture(_Heightmap, hmSampleUV(uv)).r * _TerrainHeight; }
#endif

            void main()
            {
#ifdef GPU_INSTANCING
                mat4 instanceModel = mat4(instanceModelRow0, instanceModelRow1, instanceModelRow2, instanceModelRow3);
                vec4 worldPos4 = instanceModel * vec4(vertexPosition, 1.0);
                vec3 terrainLocal = (_TerrainWorldToLocal * worldPos4).xyz;
                vec2 terrainUV = terrainLocal.xz / _TerrainSize;
                texCoord0 = terrainUV;


                // Displace: add height along terrain-local Y, transformed back to world
                // terrainLocal with height applied
                float height = sampleHeight(terrainUV);
                vec3 displacedLocal = vec3(terrainLocal.x, height, terrainLocal.z);
                vec3 worldPosition = (_TerrainLocalToWorld * vec4(displacedLocal, 1.0)).xyz;

                // Normal via central differences
                float hmSize = float(textureSize(_Heightmap, 0).x);
                float vertStep = hmSize > 1.0 ? (1.0 / (hmSize - 1.0)) : 0.001;

                float hR = sampleHeight(terrainUV + vec2(vertStep, 0.0));
                float hL = sampleHeight(terrainUV - vec2(vertStep, 0.0));
                float hU = sampleHeight(terrainUV + vec2(0.0, vertStep));
                float hD = sampleHeight(terrainUV - vec2(0.0, vertStep));

                float wStep = vertStep * _TerrainSize;
                float slopeX = (hR - hL) / (wStep * 2.0);
                float slopeZ = (hU - hD) / (wStep * 2.0);

                vec3 localNormal = normalize(vec3(-slopeX, 1.0, -slopeZ));
                worldNormal = normalize((_TerrainLocalToWorld * vec4(localNormal, 0.0)).xyz);

                worldPos = worldPosition;
                gl_Position = XENGINE_MATRIX_VP * vec4(worldPosition, 1.0);
#else
                gl_Position = XENGINE_MATRIX_MVP * vec4(vertexPosition, 1.0);
                texCoord0 = vertexTexCoord0;
                worldPos = (XENGINE_MATRIX_M * vec4(vertexPosition, 1.0)).xyz;
                worldNormal = normalize((XENGINE_MATRIX_M * vec4(0.0, 1.0, 0.0, 0.0)).xyz);
#endif
            }
        }

        Fragment
        {
            #include "XEngineCG"
            #include "Lighting"
            #include "ShaderLibrary/ImageBasedLighting"

            layout (location = 0) out vec4 fragColor;

            in vec2 texCoord0;
            in vec3 worldPos;
            in vec3 worldNormal;

            // Splatmap textures (each holds 4 layer weights as RGBA)
            uniform sampler2D _Splatmap0;
            uniform sampler2D _HolesMap;
            uniform int _HasHoles;
            uniform int _LayerCount;

            // Layer 0-3 (splatmap 0)
            uniform sampler2D _Layer0;
            uniform sampler2D _Layer0Normal;
            uniform float _Layer0Tiling;
            uniform float _Layer0Roughness;
            uniform float _Layer0Metallic;

            uniform sampler2D _Layer1;
            uniform sampler2D _Layer1Normal;
            uniform float _Layer1Tiling;
            uniform float _Layer1Roughness;
            uniform float _Layer1Metallic;

            uniform sampler2D _Layer2;
            uniform sampler2D _Layer2Normal;
            uniform float _Layer2Tiling;
            uniform float _Layer2Roughness;
            uniform float _Layer2Metallic;

            uniform sampler2D _Layer3;
            uniform sampler2D _Layer3Normal;
            uniform float _Layer3Tiling;
            uniform float _Layer3Roughness;
            uniform float _Layer3Metallic;

#ifdef TERRAIN_8_LAYERS
            // Layer 4-7 (splatmap 1)
            uniform sampler2D _Splatmap1;

            uniform sampler2D _Layer4;
            uniform sampler2D _Layer4Normal;
            uniform float _Layer4Tiling;
            uniform float _Layer4Roughness;
            uniform float _Layer4Metallic;

            uniform sampler2D _Layer5;
            uniform sampler2D _Layer5Normal;
            uniform float _Layer5Tiling;
            uniform float _Layer5Roughness;
            uniform float _Layer5Metallic;

            uniform sampler2D _Layer6;
            uniform sampler2D _Layer6Normal;
            uniform float _Layer6Tiling;
            uniform float _Layer6Roughness;
            uniform float _Layer6Metallic;

            uniform sampler2D _Layer7;
            uniform sampler2D _Layer7Normal;
            uniform float _Layer7Tiling;
            uniform float _Layer7Roughness;
            uniform float _Layer7Metallic;
#endif

            uniform vec2 _BrushPosition;
            uniform float _BrushRadius;
            uniform float _BrushFalloff;
            uniform float _BrushVisible;

            vec3 unpackNormal(vec4 packednormal)
            {
                vec3 normal;
                normal.xy = packednormal.rg * 2.0 - 1.0;
                normal.z = sqrt(max(0.0, 1.0 - dot(normal.xy, normal.xy)));
                return normal;
            }

            void main()
            {
                // Terrain holes
                if (_HasHoles > 0 && texture(_HolesMap, texCoord0).r < 0.5)
                    discard;

                // Sample splatmap 0 (layers 0-3)
                vec4 w0 = texture(_Splatmap0, texCoord0);

                // Accumulate albedo, normal, roughness, metallic from layers 0-3
                vec3 albedo = vec3(0.0);
                vec3 blendedNormalTS = vec3(0.0);
                float roughness = 0.0;
                float metallic = 0.0;
                float totalWeight = 0.0;

                // Layer 0
                if (w0.r > 0.001) {
                    vec2 uv = texCoord0 * _Layer0Tiling;
                    albedo += texture(_Layer0, uv).rgb * w0.r;
                    blendedNormalTS += unpackNormal(texture(_Layer0Normal, uv)) * w0.r;
                    roughness += _Layer0Roughness * w0.r;
                    metallic += _Layer0Metallic * w0.r;
                    totalWeight += w0.r;
                }
                // Layer 1
                if (w0.g > 0.001) {
                    vec2 uv = texCoord0 * _Layer1Tiling;
                    albedo += texture(_Layer1, uv).rgb * w0.g;
                    blendedNormalTS += unpackNormal(texture(_Layer1Normal, uv)) * w0.g;
                    roughness += _Layer1Roughness * w0.g;
                    metallic += _Layer1Metallic * w0.g;
                    totalWeight += w0.g;
                }
                // Layer 2
                if (w0.b > 0.001) {
                    vec2 uv = texCoord0 * _Layer2Tiling;
                    albedo += texture(_Layer2, uv).rgb * w0.b;
                    blendedNormalTS += unpackNormal(texture(_Layer2Normal, uv)) * w0.b;
                    roughness += _Layer2Roughness * w0.b;
                    metallic += _Layer2Metallic * w0.b;
                    totalWeight += w0.b;
                }
                // Layer 3
                if (w0.a > 0.001) {
                    vec2 uv = texCoord0 * _Layer3Tiling;
                    albedo += texture(_Layer3, uv).rgb * w0.a;
                    blendedNormalTS += unpackNormal(texture(_Layer3Normal, uv)) * w0.a;
                    roughness += _Layer3Roughness * w0.a;
                    metallic += _Layer3Metallic * w0.a;
                    totalWeight += w0.a;
                }

#ifdef TERRAIN_8_LAYERS
                // Sample splatmap 1 (layers 4-7)
                vec4 w1 = texture(_Splatmap1, texCoord0);

                if (w1.r > 0.001) {
                    vec2 uv = texCoord0 * _Layer4Tiling;
                    albedo += texture(_Layer4, uv).rgb * w1.r;
                    blendedNormalTS += unpackNormal(texture(_Layer4Normal, uv)) * w1.r;
                    roughness += _Layer4Roughness * w1.r;
                    metallic += _Layer4Metallic * w1.r;
                    totalWeight += w1.r;
                }
                if (w1.g > 0.001) {
                    vec2 uv = texCoord0 * _Layer5Tiling;
                    albedo += texture(_Layer5, uv).rgb * w1.g;
                    blendedNormalTS += unpackNormal(texture(_Layer5Normal, uv)) * w1.g;
                    roughness += _Layer5Roughness * w1.g;
                    metallic += _Layer5Metallic * w1.g;
                    totalWeight += w1.g;
                }
                if (w1.b > 0.001) {
                    vec2 uv = texCoord0 * _Layer6Tiling;
                    albedo += texture(_Layer6, uv).rgb * w1.b;
                    blendedNormalTS += unpackNormal(texture(_Layer6Normal, uv)) * w1.b;
                    roughness += _Layer6Roughness * w1.b;
                    metallic += _Layer6Metallic * w1.b;
                    totalWeight += w1.b;
                }
                if (w1.a > 0.001) {
                    vec2 uv = texCoord0 * _Layer7Tiling;
                    albedo += texture(_Layer7, uv).rgb * w1.a;
                    blendedNormalTS += unpackNormal(texture(_Layer7Normal, uv)) * w1.a;
                    roughness += _Layer7Roughness * w1.a;
                    metallic += _Layer7Metallic * w1.a;
                    totalWeight += w1.a;
                }
#endif

                // Normalize
                if (totalWeight > 0.0) {
                    albedo /= totalWeight;
                    roughness /= totalWeight;
                    metallic /= totalWeight;
                }

                vec3 baseColor = gammaToLinearSpace(albedo);
                blendedNormalTS = normalize(blendedNormalTS);

                vec3 N = normalize(worldNormal);
                vec3 T = normalize(cross(N, vec3(0.0, 0.0, 1.0)));
                vec3 B = cross(T, N);
                vec3 finalWorldNormal = normalize(mat3(T, B, N) * blendedNormalTS);

                // Brush visualization
                if (_BrushVisible > 0.5 && _BrushRadius > 0.0)
                {
                    float dist = length(texCoord0 - _BrushPosition);
                    if (dist < _BrushRadius)
                    {
                        float t = dist / _BrushRadius;
                        float alpha = 1.0 - smoothstep(1.0 - _BrushFalloff, 1.0, t);
                        baseColor = mix(baseColor, vec3(0.2, 0.8, 0.6), alpha * 0.3);
                    }
                }

                // Forward lighting
                vec3 viewDir = normalize(_WorldSpaceCameraPos.xyz - worldPos);
                vec3 lighting = CalculateForwardLighting(worldPos, finalWorldNormal, viewDir,
                                                         baseColor, metallic, roughness, 1.0);
                // Ambient with specular approximation for metallic surfaces
                vec3 ambientLight = CalculateAmbient(finalWorldNormal) * _AmbientStrength;
                vec3 diffuseColor = baseColor * (1.0 - metallic);
                vec3 ambientDiffuse = ambientLight * diffuseColor;

                vec3 F0 = mix(vec3(0.04), baseColor, metallic);
                float NdotV = max(dot(finalWorldNormal, viewDir), 0.0);
                vec3 F = FresnelSchlickRoughness(NdotV, F0, roughness);
                float specOcclusion = 1.0 - roughness * roughness;
                vec3 ambientSpecular = MixIndirectSpecular(
                    ambientLight * F * mix(specOcclusion, 1.0, 0.25),
                    worldPos, finalWorldNormal, viewDir, F0, roughness);

                vec3 color = ambientDiffuse + ambientSpecular + lighting;
                color = ApplyFog(color, worldPos);

                fragColor = vec4(color, 1.0);
            }
        }
    ENDGLSL

    SLANGPROGRAM
        Vertex
        {
            #include "XEngineCG"

            cbuffer TerrainMaterial : register(b2)
            {
                float4x4 _TerrainWorldToLocal;
                float4x4 _TerrainLocalToWorld;
                float2 _BrushPosition;
                float _TerrainSize;
                float _TerrainHeight;
                float _BrushRadius;
                float _BrushFalloff;
                float _BrushVisible;
                int _HasHoles;
                int _LayerCount;
                float4 _Layer0Params;
                float4 _Layer1Params;
                float4 _Layer2Params;
                float4 _Layer3Params;
                float4 _Layer4Params;
                float4 _Layer5Params;
                float4 _Layer6Params;
                float4 _Layer7Params;
            };

            Texture2D _Heightmap : register(t0);
            SamplerState _HeightmapSampler : register(s0);

            struct VSInput
            {
                float3 vertexPosition : POSITION;
                float2 vertexTexCoord0 : TEXCOORD0;
#ifdef GPU_INSTANCING
                float4 instanceModelRow0 : TEXCOORD8;
                float4 instanceModelRow1 : TEXCOORD9;
                float4 instanceModelRow2 : TEXCOORD10;
                float4 instanceModelRow3 : TEXCOORD11;
#endif
            };

            struct VSOutput
            {
                float4 position : SV_Position;
                float2 texCoord0 : TEXCOORD0;
                float3 worldPos : TEXCOORD1;
                float3 worldNormal : TEXCOORD2;
            };

            float2 HeightmapUv(float2 uv)
            {
                uint width, height;
                _Heightmap.GetDimensions(width, height);
                float2 size = float2(width, height);
                return uv * (size - 1.0) / size + 0.5 / size;
            }

#ifdef TERRAIN_BICUBIC
            float SampleHeightBicubic(float2 uv)
            {
                uint width, height;
                _Heightmap.GetDimensions(width, height);
                float2 textureSize = float2(width, height);
                float2 inverseTextureSize = 1.0 / textureSize;
                float2 coordinate = uv * textureSize - 0.5;
                float2 fraction = frac(coordinate);
                coordinate -= fraction;

                float2 fraction2 = fraction * fraction;
                float2 fraction3 = fraction2 * fraction;
                float2 weight0 = -0.5 * fraction3 + fraction2 - 0.5 * fraction;
                float2 weight1 = 1.5 * fraction3 - 2.5 * fraction2 + 1.0;
                float2 weight2 = -1.5 * fraction3 + 2.0 * fraction2 + 0.5 * fraction;
                float2 weight3 = 0.5 * fraction3 - 0.5 * fraction2;
                float2 sum0 = weight0 + weight1;
                float2 sum1 = weight2 + weight3;
                float2 offset0 = weight1 / sum0;
                float2 offset1 = weight3 / sum1;
                float2 sample0 = (coordinate - 0.5 + offset0) * inverseTextureSize + 0.5 * inverseTextureSize;
                float2 sample1 = (coordinate + 1.5 + offset1) * inverseTextureSize + 0.5 * inverseTextureSize;

                float height00 = _Heightmap.SampleLevel(_HeightmapSampler, float2(sample0.x, sample0.y), 0).r;
                float height10 = _Heightmap.SampleLevel(_HeightmapSampler, float2(sample1.x, sample0.y), 0).r;
                float height01 = _Heightmap.SampleLevel(_HeightmapSampler, float2(sample0.x, sample1.y), 0).r;
                float height11 = _Heightmap.SampleLevel(_HeightmapSampler, float2(sample1.x, sample1.y), 0).r;
                float row0 = lerp(height00, height10, sum1.x / (sum0.x + sum1.x));
                float row1 = lerp(height01, height11, sum1.x / (sum0.x + sum1.x));
                return lerp(row0, row1, sum1.y / (sum0.y + sum1.y));
            }
#endif

            float SampleHeight(float2 uv)
            {
#ifdef TERRAIN_BICUBIC
                return SampleHeightBicubic(uv) * _TerrainHeight;
#else
                return _Heightmap.SampleLevel(_HeightmapSampler, HeightmapUv(uv), 0).r * _TerrainHeight;
#endif
            }

            VSOutput main(VSInput input)
            {
                VSOutput o;
#ifdef GPU_INSTANCING
                float4x4 instanceModel = transpose(float4x4(
                    input.instanceModelRow0, input.instanceModelRow1,
                    input.instanceModelRow2, input.instanceModelRow3));
                float3 terrainLocal = mul(_TerrainWorldToLocal, mul(instanceModel, float4(input.vertexPosition, 1.0))).xyz;
                o.texCoord0 = terrainLocal.xz / _TerrainSize;
                float height = SampleHeight(o.texCoord0);
                float3 worldPosition = mul(_TerrainLocalToWorld, float4(terrainLocal.x, height, terrainLocal.z, 1.0)).xyz;
                uint width, ignoredHeight;
                _Heightmap.GetDimensions(width, ignoredHeight);
                float step = width > 1 ? 1.0 / (width - 1.0) : 0.001;
                float slopeX = (SampleHeight(o.texCoord0 + float2(step, 0.0)) - SampleHeight(o.texCoord0 - float2(step, 0.0))) / (2.0 * step * _TerrainSize);
                float slopeZ = (SampleHeight(o.texCoord0 + float2(0.0, step)) - SampleHeight(o.texCoord0 - float2(0.0, step))) / (2.0 * step * _TerrainSize);
                o.worldNormal = normalize(mul((float3x3)_TerrainLocalToWorld, float3(-slopeX, 1.0, -slopeZ)));
                o.worldPos = worldPosition;
                o.position = mul(XENGINE_MATRIX_VP, float4(worldPosition, 1.0));
#else
                o.texCoord0 = input.vertexTexCoord0;
                o.worldPos = TransformPosition(input.vertexPosition);
                o.worldNormal = TransformDirection(float3(0.0, 1.0, 0.0));
                o.position = TransformClip(input.vertexPosition);
#endif
                return o;
            }
        }

        Fragment
        {
            #include "XEngineCG"
            #define XENGINE_STANDARD_STATIC_LIGHT_DATA_TEXTURE_REGISTER t20
            #define XENGINE_STANDARD_STATIC_LIGHT_DATA_SAMPLER_REGISTER s20
            #define XENGINE_STANDARD_STATIC_LIGHT_NODES_TEXTURE_REGISTER t21
            #define XENGINE_STANDARD_STATIC_LIGHT_NODES_SAMPLER_REGISTER s21
            #define XENGINE_STANDARD_DYNAMIC_LIGHT_DATA_TEXTURE_REGISTER t22
            #define XENGINE_STANDARD_DYNAMIC_LIGHT_DATA_SAMPLER_REGISTER s22
            #define XENGINE_STANDARD_DYNAMIC_LIGHT_NODES_TEXTURE_REGISTER t23
            #define XENGINE_STANDARD_DYNAMIC_LIGHT_NODES_SAMPLER_REGISTER s23
            #define XENGINE_STANDARD_SHADOW_ATLAS_TEXTURE_REGISTER t24
            #define XENGINE_STANDARD_SHADOW_ATLAS_SAMPLER_REGISTER s24
            #define XENGINE_STANDARD_SCREEN_SPACE_SHADOW_TEXTURE_REGISTER t29
            #define XENGINE_STANDARD_SCREEN_SPACE_SHADOW_SAMPLER_REGISTER s29
            #define XENGINE_STANDARD_LIGHTMAP_TEXTURE_REGISTER t25
            #define XENGINE_STANDARD_LIGHTMAP_SAMPLER_REGISTER s25
            #include "StandardLighting"

            cbuffer TerrainMaterial : register(b2)
            {
                float4x4 _TerrainWorldToLocal;
                float4x4 _TerrainLocalToWorld;
                float2 _BrushPosition;
                float _TerrainSize;
                float _TerrainHeight;
                float _BrushRadius;
                float _BrushFalloff;
                float _BrushVisible;
                int _HasHoles;
                int _LayerCount;
                float4 _Layer0Params;
                float4 _Layer1Params;
                float4 _Layer2Params;
                float4 _Layer3Params;
                float4 _Layer4Params;
                float4 _Layer5Params;
                float4 _Layer6Params;
                float4 _Layer7Params;
            };

            Texture2D _Heightmap : register(t0); SamplerState _HeightmapSampler : register(s0);
            Texture2D _Splatmap0 : register(t1); SamplerState _Splatmap0Sampler : register(s1);
            Texture2D _Splatmap1 : register(t2); SamplerState _Splatmap1Sampler : register(s2);
            Texture2D _HolesMap : register(t3); SamplerState _HolesMapSampler : register(s3);
            Texture2D _Layer0 : register(t4); SamplerState _Layer0Sampler : register(s4);
            Texture2D _Layer0Normal : register(t5); SamplerState _Layer0NormalSampler : register(s5);
            Texture2D _Layer1 : register(t6); SamplerState _Layer1Sampler : register(s6);
            Texture2D _Layer1Normal : register(t7); SamplerState _Layer1NormalSampler : register(s7);
            Texture2D _Layer2 : register(t8); SamplerState _Layer2Sampler : register(s8);
            Texture2D _Layer2Normal : register(t9); SamplerState _Layer2NormalSampler : register(s9);
            Texture2D _Layer3 : register(t10); SamplerState _Layer3Sampler : register(s10);
            Texture2D _Layer3Normal : register(t11); SamplerState _Layer3NormalSampler : register(s11);
            Texture2D _Layer4 : register(t12); SamplerState _Layer4Sampler : register(s12);
            Texture2D _Layer4Normal : register(t13); SamplerState _Layer4NormalSampler : register(s13);
            Texture2D _Layer5 : register(t14); SamplerState _Layer5Sampler : register(s14);
            Texture2D _Layer5Normal : register(t15); SamplerState _Layer5NormalSampler : register(s15);
            Texture2D _Layer6 : register(t16); SamplerState _Layer6Sampler : register(s16);
            Texture2D _Layer6Normal : register(t17); SamplerState _Layer6NormalSampler : register(s17);
            Texture2D _Layer7 : register(t18); SamplerState _Layer7Sampler : register(s18);
            Texture2D _Layer7Normal : register(t19); SamplerState _Layer7NormalSampler : register(s19);

            struct PSInput
            {
                float4 position : SV_Position;
                float2 texCoord0 : TEXCOORD0;
                float3 worldPos : TEXCOORD1;
                float3 worldNormal : TEXCOORD2;
            };

            void AccumulateLayer(
                Texture2D albedoTexture,
                SamplerState albedoSampler,
                Texture2D normalTexture,
                SamplerState normalSampler,
                float weight,
                float4 parameters,
                float2 terrainUv,
                inout float3 albedo,
                inout float3 normal,
                inout float roughness,
                inout float metallic,
                inout float totalWeight)
            {
                if (weight <= 0.001) return;
                float2 layerUv = terrainUv * parameters.x;
                albedo += albedoTexture.Sample(albedoSampler, layerUv).rgb * weight;
                float3 sampledNormal = normalTexture.Sample(normalSampler, layerUv).xyz * 2.0 - 1.0;
                sampledNormal.z = sqrt(max(0.0, 1.0 - dot(sampledNormal.xy, sampledNormal.xy)));
                normal += sampledNormal * weight;
                roughness += parameters.y * weight;
                metallic += parameters.z * weight;
                totalWeight += weight;
            }

            float4 main(PSInput input) : SV_Target
            {
                if (_HasHoles > 0 && _HolesMap.Sample(_HolesMapSampler, input.texCoord0).r < 0.5)
                    discard;
                float4 weights0 = _Splatmap0.Sample(_Splatmap0Sampler, input.texCoord0);
                float3 albedo = 0.0;
                float3 tangentNormal = 0.0;
                float roughness = 0.0;
                float metallic = 0.0;
                float totalWeight = 0.0;
                AccumulateLayer(_Layer0, _Layer0Sampler, _Layer0Normal, _Layer0NormalSampler, weights0.r, _Layer0Params, input.texCoord0, albedo, tangentNormal, roughness, metallic, totalWeight);
                AccumulateLayer(_Layer1, _Layer1Sampler, _Layer1Normal, _Layer1NormalSampler, weights0.g, _Layer1Params, input.texCoord0, albedo, tangentNormal, roughness, metallic, totalWeight);
                AccumulateLayer(_Layer2, _Layer2Sampler, _Layer2Normal, _Layer2NormalSampler, weights0.b, _Layer2Params, input.texCoord0, albedo, tangentNormal, roughness, metallic, totalWeight);
                AccumulateLayer(_Layer3, _Layer3Sampler, _Layer3Normal, _Layer3NormalSampler, weights0.a, _Layer3Params, input.texCoord0, albedo, tangentNormal, roughness, metallic, totalWeight);
#ifdef TERRAIN_8_LAYERS
                float4 weights1 = _Splatmap1.Sample(_Splatmap1Sampler, input.texCoord0);
                AccumulateLayer(_Layer4, _Layer4Sampler, _Layer4Normal, _Layer4NormalSampler, weights1.r, _Layer4Params, input.texCoord0, albedo, tangentNormal, roughness, metallic, totalWeight);
                AccumulateLayer(_Layer5, _Layer5Sampler, _Layer5Normal, _Layer5NormalSampler, weights1.g, _Layer5Params, input.texCoord0, albedo, tangentNormal, roughness, metallic, totalWeight);
                AccumulateLayer(_Layer6, _Layer6Sampler, _Layer6Normal, _Layer6NormalSampler, weights1.b, _Layer6Params, input.texCoord0, albedo, tangentNormal, roughness, metallic, totalWeight);
                AccumulateLayer(_Layer7, _Layer7Sampler, _Layer7Normal, _Layer7NormalSampler, weights1.a, _Layer7Params, input.texCoord0, albedo, tangentNormal, roughness, metallic, totalWeight);
#endif
                if (totalWeight > 0.0)
                {
                    albedo /= totalWeight;
                    roughness /= totalWeight;
                    metallic /= totalWeight;
                }
                float3 baseColor = gammaToLinearSpace(albedo);
                float3 worldNormal = normalize(input.worldNormal);
                float3 tangentReference = abs(worldNormal.z) < 0.999 ? float3(0.0, 0.0, 1.0) : float3(1.0, 0.0, 0.0);
                float3 tangent = normalize(cross(worldNormal, tangentReference));
                float3 bitangent = cross(tangent, worldNormal);
                tangentNormal = normalize(tangentNormal);
                float3 finalWorldNormal = normalize(tangentNormal.x * tangent + tangentNormal.y * bitangent + tangentNormal.z * worldNormal);
                if (_BrushVisible > 0.5 && _BrushRadius > 0.0)
                {
                    float distanceToBrush = length(input.texCoord0 - _BrushPosition);
                    if (distanceToBrush < _BrushRadius)
                    {
                        float t = distanceToBrush / _BrushRadius;
                        float alpha = 1.0 - smoothstep(1.0 - _BrushFalloff, 1.0, t);
                        baseColor = lerp(baseColor, float3(0.2, 0.8, 0.6), alpha * 0.3);
                    }
                }
                float3 viewDirection = normalize(_WorldSpaceCameraPos - input.worldPos);
                float3 ambientLight = StandardCalculateAmbient(finalWorldNormal);
                float3 diffuseColor = baseColor * (1.0 - metallic);
                float3 ambientDiffuse = ambientLight * diffuseColor;
                float3 f0 = lerp(0.04.xxx, baseColor, metallic);
                float nDotV = max(dot(finalWorldNormal, viewDirection), 0.0);
                float3 fresnel = StandardAmbientFresnel(nDotV, f0, roughness);
                float specularOcclusion = 1.0 - roughness * roughness;
                float3 ambientSpecular = ambientLight * fresnel * lerp(specularOcclusion, 1.0, 0.25);
                float3 color = ambientDiffuse + ambientSpecular;
                color += StandardCalculateLighting(input.worldPos, finalWorldNormal, viewDirection, input.position.xy,
                    baseColor, metallic, roughness, 1.0, 0.0, 1.0, 0.0, 0.0);
                return float4(StandardApplyFog(color, input.worldPos), 1.0);
            }
        }
    ENDSLANG
}

Pass "TerrainShadow"
{
    Tags { "LightMode" = "ShadowCaster" }
    Cull Back

    GLSLPROGRAM

        Vertex
        {
            #include "XEngineCG"
            #include "VertexAttributes"

            out vec3 worldPos;
            out vec2 texCoord0;

            uniform sampler2D _Heightmap;
            uniform float _TerrainSize;
            uniform float _TerrainHeight;
            uniform mat4 _TerrainWorldToLocal;
            uniform mat4 _TerrainLocalToWorld;

            vec2 hmSampleUV(vec2 uv)
            {
                vec2 s = vec2(textureSize(_Heightmap, 0));
                return uv * (s - 1.0) / s + 0.5 / s;
            }

#ifdef TERRAIN_BICUBIC
            float sampleHeightBicubic(vec2 uv)
            {
                vec2 texSize = vec2(textureSize(_Heightmap, 0));
                vec2 invTexSize = 1.0 / texSize;
                vec2 coord = uv * texSize - 0.5;
                vec2 f = fract(coord);
                coord -= f;
                vec2 f2 = f * f; vec2 f3 = f2 * f;
                vec2 w0 = -0.5*f3 + f2 - 0.5*f;
                vec2 w1 = 1.5*f3 - 2.5*f2 + 1.0;
                vec2 w2 = -1.5*f3 + 2.0*f2 + 0.5*f;
                vec2 w3 = 0.5*f3 - 0.5*f2;
                vec2 s0 = w0+w1; vec2 s1 = w2+w3;
                vec2 f0 = w1/s0; vec2 f1 = w3/s1;
                vec2 t0 = (coord-0.5+f0)*invTexSize + 0.5*invTexSize;
                vec2 t1 = (coord+1.5+f1)*invTexSize + 0.5*invTexSize;
                float h00=texture(_Heightmap,vec2(t0.x,t0.y)).r;
                float h10=texture(_Heightmap,vec2(t1.x,t0.y)).r;
                float h01=texture(_Heightmap,vec2(t0.x,t1.y)).r;
                float h11=texture(_Heightmap,vec2(t1.x,t1.y)).r;
                float row0=mix(h00,h10,s1.x/(s0.x+s1.x));
                float row1=mix(h01,h11,s1.x/(s0.x+s1.x));
                return mix(row0,row1,s1.y/(s0.y+s1.y));
            }
            float sampleHeight(vec2 uv) { return sampleHeightBicubic(uv) * _TerrainHeight; }
#else
            float sampleHeight(vec2 uv) { return texture(_Heightmap, hmSampleUV(uv)).r * _TerrainHeight; }
#endif

            void main()
            {
#ifdef GPU_INSTANCING
                mat4 instanceModel = mat4(instanceModelRow0, instanceModelRow1, instanceModelRow2, instanceModelRow3);
                vec4 worldPos4 = instanceModel * vec4(vertexPosition, 1.0);
                vec3 terrainLocal = (_TerrainWorldToLocal * worldPos4).xyz;
                vec2 terrainUV = terrainLocal.xz / _TerrainSize;
                texCoord0 = terrainUV;

                float height = sampleHeight(terrainUV);
                vec3 displacedLocal = vec3(terrainLocal.x, height, terrainLocal.z);
                vec3 worldPosition = (_TerrainLocalToWorld * vec4(displacedLocal, 1.0)).xyz;

                worldPos = worldPosition;
                gl_Position = XENGINE_MATRIX_VP * vec4(worldPosition, 1.0);
#else
                gl_Position = XENGINE_MATRIX_MVP * vec4(vertexPosition, 1.0);
                worldPos = (XENGINE_MATRIX_M * vec4(vertexPosition, 1.0)).xyz;
                texCoord0 = vertexTexCoord0;
#endif
            }
        }

        Fragment
        {
            #include "XEngineCG"

            in vec3 worldPos;
            in vec2 texCoord0;

            uniform sampler2D _HolesMap;
            uniform int _HasHoles;

            void main()
            {
                if (_HasHoles > 0 && texture(_HolesMap, texCoord0).r < 0.5)
                    discard;
                gl_FragDepth = gl_FragCoord.z;
            }
        }
    ENDGLSL

    SLANGPROGRAM
        Vertex
        {
            #include "XEngineCG"

            cbuffer TerrainMaterial : register(b2)
            {
                float4x4 _TerrainWorldToLocal; float4x4 _TerrainLocalToWorld;
                float2 _BrushPosition; float _TerrainSize; float _TerrainHeight;
                float _BrushRadius; float _BrushFalloff; float _BrushVisible; int _HasHoles;
                int _LayerCount;
                float4 _Layer0Params; float4 _Layer1Params; float4 _Layer2Params; float4 _Layer3Params;
                float4 _Layer4Params; float4 _Layer5Params; float4 _Layer6Params; float4 _Layer7Params;
            };
            Texture2D _Heightmap : register(t0);
            SamplerState _HeightmapSampler : register(s0);

            struct VSInput
            {
                float3 vertexPosition : POSITION;
                float2 vertexTexCoord0 : TEXCOORD0;
#ifdef GPU_INSTANCING
                float4 instanceModelRow0 : TEXCOORD8;
                float4 instanceModelRow1 : TEXCOORD9;
                float4 instanceModelRow2 : TEXCOORD10;
                float4 instanceModelRow3 : TEXCOORD11;
#endif
            };
            struct VSOutput { float4 position : SV_Position; float2 texCoord0 : TEXCOORD0; };

            float SampleHeight(float2 uv)
            {
                uint width, height;
                _Heightmap.GetDimensions(width, height);
                float2 size = float2(width, height);
#ifdef TERRAIN_BICUBIC
                float2 inverseSize = 1.0 / size;
                float2 coordinate = uv * size - 0.5;
                float2 fraction = frac(coordinate);
                coordinate -= fraction;
                float2 fraction2 = fraction * fraction;
                float2 fraction3 = fraction2 * fraction;
                float2 weight0 = -0.5 * fraction3 + fraction2 - 0.5 * fraction;
                float2 weight1 = 1.5 * fraction3 - 2.5 * fraction2 + 1.0;
                float2 weight2 = -1.5 * fraction3 + 2.0 * fraction2 + 0.5 * fraction;
                float2 weight3 = 0.5 * fraction3 - 0.5 * fraction2;
                float2 sum0 = weight0 + weight1;
                float2 sum1 = weight2 + weight3;
                float2 sample0 = (coordinate - 0.5 + weight1 / sum0) * inverseSize + 0.5 * inverseSize;
                float2 sample1 = (coordinate + 1.5 + weight3 / sum1) * inverseSize + 0.5 * inverseSize;
                float height00 = _Heightmap.SampleLevel(_HeightmapSampler, float2(sample0.x, sample0.y), 0).r;
                float height10 = _Heightmap.SampleLevel(_HeightmapSampler, float2(sample1.x, sample0.y), 0).r;
                float height01 = _Heightmap.SampleLevel(_HeightmapSampler, float2(sample0.x, sample1.y), 0).r;
                float height11 = _Heightmap.SampleLevel(_HeightmapSampler, float2(sample1.x, sample1.y), 0).r;
                float row0 = lerp(height00, height10, sum1.x / (sum0.x + sum1.x));
                float row1 = lerp(height01, height11, sum1.x / (sum0.x + sum1.x));
                return lerp(row0, row1, sum1.y / (sum0.y + sum1.y)) * _TerrainHeight;
#else
                float2 sampleUv = uv * (size - 1.0) / size + 0.5 / size;
                return _Heightmap.SampleLevel(_HeightmapSampler, sampleUv, 0).r * _TerrainHeight;
#endif
            }

            VSOutput main(VSInput input)
            {
                VSOutput o;
#ifdef GPU_INSTANCING
                float4x4 instanceModel = transpose(float4x4(input.instanceModelRow0, input.instanceModelRow1, input.instanceModelRow2, input.instanceModelRow3));
                float3 terrainLocal = mul(_TerrainWorldToLocal, mul(instanceModel, float4(input.vertexPosition, 1.0))).xyz;
                o.texCoord0 = terrainLocal.xz / _TerrainSize;
                float3 worldPosition = mul(_TerrainLocalToWorld, float4(terrainLocal.x, SampleHeight(o.texCoord0), terrainLocal.z, 1.0)).xyz;
                o.position = mul(XENGINE_MATRIX_VP, float4(worldPosition, 1.0));
#else
                o.texCoord0 = input.vertexTexCoord0;
                o.position = TransformClip(input.vertexPosition);
#endif
                return o;
            }
        }
        Fragment
        {
            cbuffer TerrainMaterial : register(b2)
            {
                float4x4 _TerrainWorldToLocal; float4x4 _TerrainLocalToWorld;
                float2 _BrushPosition; float _TerrainSize; float _TerrainHeight;
                float _BrushRadius; float _BrushFalloff; float _BrushVisible; int _HasHoles;
                int _LayerCount;
                float4 _Layer0Params; float4 _Layer1Params; float4 _Layer2Params; float4 _Layer3Params;
                float4 _Layer4Params; float4 _Layer5Params; float4 _Layer6Params; float4 _Layer7Params;
            };
            Texture2D _HolesMap : register(t3);
            SamplerState _HolesMapSampler : register(s3);
            struct PSInput { float4 position : SV_Position; float2 texCoord0 : TEXCOORD0; };
            void main(PSInput input)
            {
                if (_HasHoles > 0 && _HolesMap.Sample(_HolesMapSampler, input.texCoord0).r < 0.5) discard;
            }
        }
    ENDSLANG
}

Pass "TerrainPrepass"
{
    Tags { "LightMode" = "Prepass" }
    Cull Back
    ZWrite On

    GLSLPROGRAM

        Vertex
        {
            #include "XEngineCG"
            #include "VertexAttributes"

            out vec3 worldNormal;
            out vec2 texCoord0;
            out vec4 vCurrClipNJ;
            out vec4 vPrevClip;

            uniform sampler2D _Heightmap;
            uniform float _TerrainSize;
            uniform float _TerrainHeight;
            uniform mat4 _TerrainWorldToLocal;
            uniform mat4 _TerrainLocalToWorld;

            vec2 hmSampleUV(vec2 uv)
            {
                vec2 s = vec2(textureSize(_Heightmap, 0));
                return uv * (s - 1.0) / s + 0.5 / s;
            }

#ifdef TERRAIN_BICUBIC
            float sampleHeightBicubic(vec2 uv)
            {
                vec2 texSize = vec2(textureSize(_Heightmap, 0));
                vec2 invTexSize = 1.0 / texSize;
                vec2 coord = uv * texSize - 0.5;
                vec2 f = fract(coord);
                coord -= f;
                vec2 f2 = f * f; vec2 f3 = f2 * f;
                vec2 w0 = -0.5*f3 + f2 - 0.5*f;
                vec2 w1 = 1.5*f3 - 2.5*f2 + 1.0;
                vec2 w2 = -1.5*f3 + 2.0*f2 + 0.5*f;
                vec2 w3 = 0.5*f3 - 0.5*f2;
                vec2 s0 = w0+w1; vec2 s1 = w2+w3;
                vec2 f0 = w1/s0; vec2 f1 = w3/s1;
                vec2 t0 = (coord-0.5+f0)*invTexSize + 0.5*invTexSize;
                vec2 t1 = (coord+1.5+f1)*invTexSize + 0.5*invTexSize;
                float h00=texture(_Heightmap,vec2(t0.x,t0.y)).r;
                float h10=texture(_Heightmap,vec2(t1.x,t0.y)).r;
                float h01=texture(_Heightmap,vec2(t0.x,t1.y)).r;
                float h11=texture(_Heightmap,vec2(t1.x,t1.y)).r;
                float row0=mix(h00,h10,s1.x/(s0.x+s1.x));
                float row1=mix(h01,h11,s1.x/(s0.x+s1.x));
                return mix(row0,row1,s1.y/(s0.y+s1.y));
            }
            float sampleHeight(vec2 uv) { return sampleHeightBicubic(uv) * _TerrainHeight; }
#else
            float sampleHeight(vec2 uv) { return texture(_Heightmap, hmSampleUV(uv)).r * _TerrainHeight; }
#endif

            void main()
            {
#ifdef GPU_INSTANCING
                mat4 instanceModel = mat4(instanceModelRow0, instanceModelRow1, instanceModelRow2, instanceModelRow3);
                vec4 worldPos4 = instanceModel * vec4(vertexPosition, 1.0);
                vec3 terrainLocal = (_TerrainWorldToLocal * worldPos4).xyz;
                vec2 terrainUV = terrainLocal.xz / _TerrainSize;
                texCoord0 = terrainUV;

                float height = sampleHeight(terrainUV);
                vec3 displacedLocal = vec3(terrainLocal.x, height, terrainLocal.z);
                vec3 worldPosition = (_TerrainLocalToWorld * vec4(displacedLocal, 1.0)).xyz;

                float hmSize = float(textureSize(_Heightmap, 0).x);
                float vertStep = hmSize > 1.0 ? (1.0 / (hmSize - 1.0)) : 0.001;
                float hR = sampleHeight(terrainUV + vec2(vertStep, 0.0));
                float hL = sampleHeight(terrainUV - vec2(vertStep, 0.0));
                float hU = sampleHeight(terrainUV + vec2(0.0, vertStep));
                float hD = sampleHeight(terrainUV - vec2(0.0, vertStep));
                float wStep = vertStep * _TerrainSize;
                float slopeX = (hR - hL) / (wStep * 2.0);
                float slopeZ = (hU - hD) / (wStep * 2.0);
                vec3 localNormal = normalize(vec3(-slopeX, 1.0, -slopeZ));
                worldNormal = normalize((_TerrainLocalToWorld * vec4(localNormal, 0.0)).xyz);

                gl_Position = XENGINE_MATRIX_VP * vec4(worldPosition, 1.0);

                // Static terrain: only the camera moves, so previous world position is identical.
                vCurrClipNJ = XENGINE_MATRIX_VP_NONJITTERED * vec4(worldPosition, 1.0);
                vPrevClip = XENGINE_MATRIX_VP_PREVIOUS * vec4(worldPosition, 1.0);
#else
                gl_Position = XENGINE_MATRIX_MVP * vec4(vertexPosition, 1.0);
                worldNormal = normalize((XENGINE_MATRIX_M * vec4(0.0, 1.0, 0.0, 0.0)).xyz);
                texCoord0 = vertexTexCoord0;

                vec4 wp = XENGINE_MATRIX_M * vec4(vertexPosition, 1.0);
                vCurrClipNJ = XENGINE_MATRIX_VP_NONJITTERED * wp;
                vPrevClip = XENGINE_MATRIX_VP_PREVIOUS * (XENGINE_MATRIX_M_PREVIOUS * vec4(vertexPosition, 1.0));
#endif
            }
        }

        Fragment
        {
            #include "XEngineCG"

            layout (location = 0) out vec4 normalOut;
            layout (location = 1) out vec4 motionRM;
            in vec3 worldNormal;
            in vec2 texCoord0;
            in vec4 vCurrClipNJ;
            in vec4 vPrevClip;

            uniform sampler2D _HolesMap;
            uniform int _HasHoles;

            void main()
            {
                if (_HasHoles > 0 && texture(_HolesMap, texCoord0).r < 0.5)
                    discard;
                normalOut = EncodeViewNormal(worldNormal);

                // Motion vectors (jitter-free). Terrain has no per-pixel material textures here,
                // so pack a diffuse default: roughness 1, metallic 0.
                vec2 currNDC = (vCurrClipNJ.xy / vCurrClipNJ.w) * 0.5 + 0.5;
                vec2 prevNDC = (vPrevClip.xy / vPrevClip.w) * 0.5 + 0.5;
                motionRM = vec4(currNDC - prevNDC, 1.0, 0.0);
            }
        }
    ENDGLSL

    SLANGPROGRAM
        Vertex
        {
            #include "XEngineCG"

            cbuffer TerrainMaterial : register(b2)
            {
                float4x4 _TerrainWorldToLocal; float4x4 _TerrainLocalToWorld;
                float2 _BrushPosition; float _TerrainSize; float _TerrainHeight;
                float _BrushRadius; float _BrushFalloff; float _BrushVisible; int _HasHoles;
                int _LayerCount;
                float4 _Layer0Params; float4 _Layer1Params; float4 _Layer2Params; float4 _Layer3Params;
                float4 _Layer4Params; float4 _Layer5Params; float4 _Layer6Params; float4 _Layer7Params;
            };
            Texture2D _Heightmap : register(t0);
            SamplerState _HeightmapSampler : register(s0);

            struct VSInput
            {
                float3 vertexPosition : POSITION;
                float2 vertexTexCoord0 : TEXCOORD0;
#ifdef GPU_INSTANCING
                float4 instanceModelRow0 : TEXCOORD8;
                float4 instanceModelRow1 : TEXCOORD9;
                float4 instanceModelRow2 : TEXCOORD10;
                float4 instanceModelRow3 : TEXCOORD11;
#endif
            };
            struct VSOutput
            {
                float4 position : SV_Position;
                float3 worldNormal : TEXCOORD0;
                float2 texCoord0 : TEXCOORD1;
                float4 currentClip : TEXCOORD2;
                float4 previousClip : TEXCOORD3;
            };

            float SampleHeight(float2 uv)
            {
                uint width, height;
                _Heightmap.GetDimensions(width, height);
                float2 size = float2(width, height);
#ifdef TERRAIN_BICUBIC
                float2 inverseSize = 1.0 / size;
                float2 coordinate = uv * size - 0.5;
                float2 fraction = frac(coordinate);
                coordinate -= fraction;
                float2 fraction2 = fraction * fraction;
                float2 fraction3 = fraction2 * fraction;
                float2 weight0 = -0.5 * fraction3 + fraction2 - 0.5 * fraction;
                float2 weight1 = 1.5 * fraction3 - 2.5 * fraction2 + 1.0;
                float2 weight2 = -1.5 * fraction3 + 2.0 * fraction2 + 0.5 * fraction;
                float2 weight3 = 0.5 * fraction3 - 0.5 * fraction2;
                float2 sum0 = weight0 + weight1;
                float2 sum1 = weight2 + weight3;
                float2 sample0 = (coordinate - 0.5 + weight1 / sum0) * inverseSize + 0.5 * inverseSize;
                float2 sample1 = (coordinate + 1.5 + weight3 / sum1) * inverseSize + 0.5 * inverseSize;
                float height00 = _Heightmap.SampleLevel(_HeightmapSampler, float2(sample0.x, sample0.y), 0).r;
                float height10 = _Heightmap.SampleLevel(_HeightmapSampler, float2(sample1.x, sample0.y), 0).r;
                float height01 = _Heightmap.SampleLevel(_HeightmapSampler, float2(sample0.x, sample1.y), 0).r;
                float height11 = _Heightmap.SampleLevel(_HeightmapSampler, float2(sample1.x, sample1.y), 0).r;
                float row0 = lerp(height00, height10, sum1.x / (sum0.x + sum1.x));
                float row1 = lerp(height01, height11, sum1.x / (sum0.x + sum1.x));
                return lerp(row0, row1, sum1.y / (sum0.y + sum1.y)) * _TerrainHeight;
#else
                float2 sampleUv = uv * (size - 1.0) / size + 0.5 / size;
                return _Heightmap.SampleLevel(_HeightmapSampler, sampleUv, 0).r * _TerrainHeight;
#endif
            }

            VSOutput main(VSInput input)
            {
                VSOutput o;
#ifdef GPU_INSTANCING
                float4x4 instanceModel = transpose(float4x4(input.instanceModelRow0, input.instanceModelRow1, input.instanceModelRow2, input.instanceModelRow3));
                float3 terrainLocal = mul(_TerrainWorldToLocal, mul(instanceModel, float4(input.vertexPosition, 1.0))).xyz;
                o.texCoord0 = terrainLocal.xz / _TerrainSize;
                float height = SampleHeight(o.texCoord0);
                float3 worldPosition = mul(_TerrainLocalToWorld, float4(terrainLocal.x, height, terrainLocal.z, 1.0)).xyz;
                uint width, ignoredHeight; _Heightmap.GetDimensions(width, ignoredHeight);
                float step = width > 1 ? 1.0 / (width - 1.0) : 0.001;
                float slopeX = (SampleHeight(o.texCoord0 + float2(step, 0.0)) - SampleHeight(o.texCoord0 - float2(step, 0.0))) / (2.0 * step * _TerrainSize);
                float slopeZ = (SampleHeight(o.texCoord0 + float2(0.0, step)) - SampleHeight(o.texCoord0 - float2(0.0, step))) / (2.0 * step * _TerrainSize);
                o.worldNormal = normalize(mul((float3x3)_TerrainLocalToWorld, float3(-slopeX, 1.0, -slopeZ)));
                o.position = mul(XENGINE_MATRIX_VP, float4(worldPosition, 1.0));
                o.currentClip = mul(XENGINE_MATRIX_VP_NONJITTERED, float4(worldPosition, 1.0));
                o.previousClip = mul(XENGINE_MATRIX_VP_PREVIOUS, float4(worldPosition, 1.0));
#else
                o.texCoord0 = input.vertexTexCoord0;
                o.worldNormal = TransformDirection(float3(0.0, 1.0, 0.0));
                float4 worldPosition = mul(XENGINE_MATRIX_M, float4(input.vertexPosition, 1.0));
                o.position = mul(XENGINE_MATRIX_VP, worldPosition);
                o.currentClip = mul(XENGINE_MATRIX_VP_NONJITTERED, worldPosition);
                o.previousClip = mul(XENGINE_MATRIX_VP_PREVIOUS, mul(XENGINE_MATRIX_M_PREVIOUS, float4(input.vertexPosition, 1.0)));
#endif
                return o;
            }
        }
        Fragment
        {
            #include "XEngineCG"

            cbuffer TerrainMaterial : register(b2)
            {
                float4x4 _TerrainWorldToLocal; float4x4 _TerrainLocalToWorld;
                float2 _BrushPosition; float _TerrainSize; float _TerrainHeight;
                float _BrushRadius; float _BrushFalloff; float _BrushVisible; int _HasHoles;
                int _LayerCount;
                float4 _Layer0Params; float4 _Layer1Params; float4 _Layer2Params; float4 _Layer3Params;
                float4 _Layer4Params; float4 _Layer5Params; float4 _Layer6Params; float4 _Layer7Params;
            };
            Texture2D _HolesMap : register(t3);
            SamplerState _HolesMapSampler : register(s3);
            struct PSInput
            {
                float4 position : SV_Position;
                float3 worldNormal : TEXCOORD0;
                float2 texCoord0 : TEXCOORD1;
                float4 currentClip : TEXCOORD2;
                float4 previousClip : TEXCOORD3;
            };
            struct PSOutput { float4 normalOut : SV_Target0; float4 motionRM : SV_Target1; };
            PSOutput main(PSInput input)
            {
                if (_HasHoles > 0 && _HolesMap.Sample(_HolesMapSampler, input.texCoord0).r < 0.5) discard;
                PSOutput o;
                o.normalOut = EncodeViewNormal(input.worldNormal);
                float2 currentNdc = (input.currentClip.xy / input.currentClip.w) * 0.5 + 0.5;
                float2 previousNdc = (input.previousClip.xy / input.previousClip.w) * 0.5 + 0.5;
                o.motionRM = float4(currentNdc - previousNdc, 1.0, 0.0);
                return o;
            }
        }
    ENDSLANG
}
