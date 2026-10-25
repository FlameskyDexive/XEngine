#ifndef XENGINE_LIGHT_COOKIE_HLSL
#define XENGINE_LIGHT_COOKIE_HLSL

#ifndef XENGINE_STANDARD_LIGHT_COOKIE_MAIN_TEXTURE_REGISTER
#define XENGINE_STANDARD_LIGHT_COOKIE_MAIN_TEXTURE_REGISTER t12
#define XENGINE_STANDARD_LIGHT_COOKIE_MAIN_SAMPLER_REGISTER s12
#define XENGINE_STANDARD_LIGHT_COOKIE_ATLAS_TEXTURE_REGISTER t13
#define XENGINE_STANDARD_LIGHT_COOKIE_ATLAS_SAMPLER_REGISTER s13
#endif

Texture2D _LightCookieMain : register(XENGINE_STANDARD_LIGHT_COOKIE_MAIN_TEXTURE_REGISTER);
SamplerState _LightCookieMainSampler : register(XENGINE_STANDARD_LIGHT_COOKIE_MAIN_SAMPLER_REGISTER);
Texture2D _LightCookieAtlas : register(XENGINE_STANDARD_LIGHT_COOKIE_ATLAS_TEXTURE_REGISTER);
SamplerState _LightCookieAtlasSampler : register(XENGINE_STANDARD_LIGHT_COOKIE_ATLAS_SAMPLER_REGISTER);

cbuffer StandardLightCookies : register(b6)
{
    float4 _LightCookieMainParams; // x = enabled, y = intensity
    float4x4 _LightCookieMainWorldToLight;
};

float3 SampleMainLightCookie(float3 worldPos)
{
    if (_LightCookieMainParams.x < 0.5)
        return 1.0.xxx;

    float4 clip = mul(_LightCookieMainWorldToLight, float4(worldPos, 1.0));
    if (abs(clip.w) < 1e-8)
        return 1.0.xxx;
    float2 uv = clip.xy / clip.w * 0.5 + 0.5;
    if (uv.x < 0.0 || uv.x > 1.0 || uv.y < 0.0 || uv.y > 1.0)
        return 1.0.xxx;
    float3 sampleColor = _LightCookieMain.Sample(_LightCookieMainSampler, uv).rgb;
    return lerp(1.0.xxx, sampleColor, saturate(_LightCookieMainParams.y));
}

float2 LightCookieSpotUv(float3 worldPos, float3 lightPos, float3 lightDir, float range, float outerCos)
{
    float3 f = normalize(lightDir);
    float3 upHint = abs(f.y) < 0.999 ? float3(0.0, 1.0, 0.0) : float3(1.0, 0.0, 0.0);
    float3 s = normalize(cross(upHint, f));
    float3 u = cross(f, s);
    float3 rel = worldPos - lightPos;
    float3 view = float3(dot(s, rel), dot(u, rel), dot(f, rel));
    float fov = 2.0 * acos(clamp(outerCos, -0.999, 0.999));
    float nearP = 0.1;
    float farP = max(nearP + 0.01, range);
    float height = 1.0 / tan(fov * 0.5);
    float zRange = farP / (farP - nearP);
    float4 clip = float4(view.x * height, view.y * height, view.z * zRange - zRange * nearP, view.z);
    if (abs(clip.w) < 1e-8)
        return float2(-1.0, -1.0);
    return clip.xy / clip.w * 0.5 + 0.5;
}

float2 LightCookiePointUv(float3 worldPos, float3 lightPos)
{
    float3 d = normalize(worldPos - lightPos);
    float uCoord = atan2(d.x, d.z) * (0.5 / 3.14159265) + 0.5;
    float vCoord = acos(clamp(d.y, -1.0, 1.0)) / 3.14159265;
    return float2(uCoord, vCoord);
}

float3 SampleLightCookieAtlas(float2 localUv, float4 rect, float intensity)
{
    if (rect.z <= 0.0 || rect.w <= 0.0)
        return 1.0.xxx;
    if (localUv.x < 0.0 || localUv.x > 1.0 || localUv.y < 0.0 || localUv.y > 1.0)
        return 1.0.xxx;
    float2 atlasUv = rect.xy + localUv * rect.zw;
    float3 sampleColor = _LightCookieAtlas.Sample(_LightCookieAtlasSampler, atlasUv).rgb;
    return lerp(1.0.xxx, sampleColor, saturate(intensity));
}

#endif
