#ifndef XENGINE_IMAGE_BASED_LIGHTING
#define XENGINE_IMAGE_BASED_LIGHTING

#include "ShaderLibrary/ShaderConfig"

uniform int _ReflectionProbeCount;
uniform samplerCube _SpecCube0;
uniform samplerCube _SpecCube1;
uniform sampler2D _BRDFLut;
uniform vec3 _Probe0Position;
uniform vec3 _Probe0BoxMin;
uniform vec3 _Probe0BoxMax;
uniform vec4 _Probe0Params; // x intensity, y boxProjection, z mipCount
uniform vec3 _Probe1Position;
uniform vec3 _Probe1BoxMin;
uniform vec3 _Probe1BoxMax;
uniform vec4 _Probe1Params;
uniform vec2 _ProbeWeights;

vec3 BoxProjectedCubemapDirection(vec3 dir, vec3 worldPos, vec3 probePos, vec3 boxMin, vec3 boxMax)
{
    vec3 nrDir = normalize(dir);
    vec3 rbmax = (boxMax - worldPos) / nrDir;
    vec3 rbmin = (boxMin - worldPos) / nrDir;
    vec3 rbminmax = mix(rbmin, rbmax, step(vec3(0.0), nrDir));
    float fa = min(min(rbminmax.x, rbminmax.y), rbminmax.z);
    vec3 posOnBox = worldPos + nrDir * fa;
    return posOnBox - probePos;
}

vec3 SampleIBLCube(samplerCube cube, vec3 dir, float roughness, float mipCount)
{
    float mip = roughness * max(mipCount - 1.0, 0.0);
    return textureLod(cube, dir, mip).rgb;
}

vec3 MixIndirectSpecular(vec3 approx, vec3 worldPos, vec3 worldNormal, vec3 viewDir, vec3 F0, float roughness)
{
    if (_ReflectionProbeCount < 1)
        return approx;

    vec3 N = normalize(worldNormal);
    vec3 V = normalize(viewDir);
    vec3 R = reflect(-V, N);
    float NdotV = max(dot(N, V), 0.0);

    vec3 dir0 = R;
    if (_Probe0Params.y > 0.5)
        dir0 = BoxProjectedCubemapDirection(R, worldPos, _Probe0Position, _Probe0BoxMin, _Probe0BoxMax);
    vec3 env = SampleIBLCube(_SpecCube0, dir0, roughness, _Probe0Params.z) * _Probe0Params.x * _ProbeWeights.x;

    if (_ReflectionProbeCount > 1)
    {
        vec3 dir1 = R;
        if (_Probe1Params.y > 0.5)
            dir1 = BoxProjectedCubemapDirection(R, worldPos, _Probe1Position, _Probe1BoxMin, _Probe1BoxMax);
        env += SampleIBLCube(_SpecCube1, dir1, roughness, _Probe1Params.z) * _Probe1Params.x * _ProbeWeights.y;
    }

    float w = clamp(_ProbeWeights.x + _ProbeWeights.y, 0.0, 1.0);
    vec2 brdf = texture(_BRDFLut, vec2(NdotV, roughness)).rg;
    vec3 ibl = env * (F0 * brdf.x + brdf.y);
    return mix(approx, ibl, w);
}

#endif
