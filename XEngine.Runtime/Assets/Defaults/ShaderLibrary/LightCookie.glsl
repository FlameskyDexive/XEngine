#ifndef XENGINE_LIGHT_COOKIE
#define XENGINE_LIGHT_COOKIE

// Directional cookie: dedicated 2D texture. Local cookies: packed into _LightCookieAtlas.
// No cookie / outside projection → rgb 1 so lighting is unchanged.

uniform sampler2D _LightCookieMain;
uniform int _LightCookieMainEnabled;
uniform float _LightCookieMainIntensity;
uniform mat4 _LightCookieMainWorldToLight;
uniform sampler2D _LightCookieAtlas;
uniform vec2 _LightCookieAtlasSize;

vec3 SampleMainLightCookie(vec3 worldPos)
{
    if (_LightCookieMainEnabled == 0)
        return vec3(1.0);

    vec4 clip = _LightCookieMainWorldToLight * vec4(worldPos, 1.0);
    if (abs(clip.w) < 1e-8)
        return vec3(1.0);
    vec2 uv = clip.xy / clip.w * 0.5 + 0.5;
    if (uv.x < 0.0 || uv.x > 1.0 || uv.y < 0.0 || uv.y > 1.0)
        return vec3(1.0);
    vec3 cookieColor = texture(_LightCookieMain, uv).rgb;
    return mix(vec3(1.0), cookieColor, clamp(_LightCookieMainIntensity, 0.0, 1.0));
}

vec2 LightCookieSpotUv(vec3 worldPos, vec3 lightPos, vec3 lightDir, float range, float outerCos)
{
    vec3 f = normalize(lightDir);
    vec3 upHint = abs(f.y) < 0.999 ? vec3(0.0, 1.0, 0.0) : vec3(1.0, 0.0, 0.0);
    vec3 s = normalize(cross(upHint, f));
    vec3 u = cross(f, s);
    vec3 rel = worldPos - lightPos;
    vec3 view = vec3(dot(s, rel), dot(u, rel), dot(f, rel));
    float fov = 2.0 * acos(clamp(outerCos, -0.999, 0.999));
    float nearP = 0.1;
    float farP = max(nearP + 0.01, range);
    float height = 1.0 / tan(fov * 0.5);
    float zRange = farP / (farP - nearP);
    vec4 clip = vec4(view.x * height, view.y * height, view.z * zRange - zRange * nearP, view.z);
    if (abs(clip.w) < 1e-8)
        return vec2(-1.0);
    return clip.xy / clip.w * 0.5 + 0.5;
}

vec2 LightCookiePointUv(vec3 worldPos, vec3 lightPos)
{
    vec3 d = normalize(worldPos - lightPos);
    float u = atan(d.x, d.z) * (0.5 / 3.14159265) + 0.5;
    float v = acos(clamp(d.y, -1.0, 1.0)) / 3.14159265;
    return vec2(u, v);
}

vec3 SampleLightCookieAtlas(vec2 localUv, vec4 rect, float intensity)
{
    if (rect.z <= 0.0 || rect.w <= 0.0)
        return vec3(1.0);
    if (localUv.x < 0.0 || localUv.x > 1.0 || localUv.y < 0.0 || localUv.y > 1.0)
        return vec3(1.0);
    vec2 atlasUv = rect.xy + localUv * rect.zw;
    vec3 cookieColor = texture(_LightCookieAtlas, atlasUv).rgb;
    return mix(vec3(1.0), cookieColor, clamp(intensity, 0.0, 1.0));
}

vec3 SampleLocalLightCookie(LightSample L, vec3 worldPos)
{
    if (L.CookieEnabled == 0)
        return vec3(1.0);
    vec2 uv = L.Type == 2
        ? LightCookieSpotUv(worldPos, L.Position, L.Direction, L.Range, L.SpotCos)
        : LightCookiePointUv(worldPos, L.Position);
    return SampleLightCookieAtlas(uv, L.CookieRect, L.CookieIntensity);
}

#endif
