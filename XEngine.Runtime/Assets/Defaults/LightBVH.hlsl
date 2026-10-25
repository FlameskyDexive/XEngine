#ifndef XENGINE_LIGHT_BVH_HLSL
#define XENGINE_LIGHT_BVH_HLSL

#include "ShaderLibrary/ShaderConfig"

struct FogLightSample
{
    float3 Position;
    float Range;
    float3 Color;
    float Intensity;
    float3 Direction;
    int Type;
    float SpotCos;
    float InnerSpotCos;
    float ShadowBias;
    float ShadowNormalBias;
    float ShadowStrength;
    float ShadowQuality;
    int ShadowSlot;
    int ShadowEnabled;
};

struct FogBvhIterator
{
    int Current;
};

int2 FogBvhCoord(int linearIndex, int dimension, int shift)
{
    int mask = dimension - 1;
    return int2(linearIndex & mask, linearIndex >> shift);
}

FogLightSample FogBvhFetchLight(Texture2D lightTexture, int dimension, int shift, int slot)
{
    int baseIndex = slot * LBVH_TEXELS_PER_LIGHT;
    float4 t0 = lightTexture.Load(int3(FogBvhCoord(baseIndex + 0, dimension, shift), 0));
    float4 t1 = lightTexture.Load(int3(FogBvhCoord(baseIndex + 1, dimension, shift), 0));
    float4 t2 = lightTexture.Load(int3(FogBvhCoord(baseIndex + 2, dimension, shift), 0));

    int typeAndFlags = asint(t2.w);
    int type = typeAndFlags & 3;
    int shadowEnabled = (typeAndFlags >> 2) & 1;

    FogLightSample light;
    light.Position = t0.xyz;
    light.Range = t0.w;
    light.Color = t1.rgb;
    light.Intensity = t1.w;
    light.Direction = t2.xyz;
    light.Type = type;
    light.ShadowEnabled = shadowEnabled;
    light.SpotCos = -1.0;
    light.InnerSpotCos = 1.0;
    light.ShadowBias = 0.0;
    light.ShadowNormalBias = 0.0;
    light.ShadowStrength = 0.0;
    light.ShadowQuality = 0.0;
    light.ShadowSlot = -1;

    if (type == 2 || shadowEnabled != 0)
    {
        float4 t3 = lightTexture.Load(int3(FogBvhCoord(baseIndex + 3, dimension, shift), 0));
        light.SpotCos = t3.x;
        light.InnerSpotCos = t3.y;
        light.ShadowBias = t3.z;
        light.ShadowNormalBias = t3.w;
    }

    if (shadowEnabled != 0)
    {
        float4 t4 = lightTexture.Load(int3(FogBvhCoord(baseIndex + 4, dimension, shift), 0));
        light.ShadowStrength = t4.x;
        light.ShadowQuality = t4.y;
        light.ShadowSlot = asint(t4.z);
    }

    return light;
}

void FogBvhBegin(out FogBvhIterator iterator, int root)
{
    iterator.Current = root;
}

int FogBvhNext(
    inout FogBvhIterator iterator,
    Texture2D nodeTexture,
    int dimension,
    int shift,
    float3 worldPosition)
{
    int budget = LBVH_MAX_NODE_VISITS;
    while (iterator.Current >= 0 && budget > 0)
    {
        budget--;
        int baseIndex = iterator.Current * 2;
        float4 lo = nodeTexture.Load(int3(FogBvhCoord(baseIndex + 0, dimension, shift), 0));
        float4 hi = nodeTexture.Load(int3(FogBvhCoord(baseIndex + 1, dimension, shift), 0));
        int hitNext = asint(lo.w);
        int missNext = asint(hi.w);

        if (hitNext < 0)
        {
            float3 center = (lo.xyz + hi.xyz) * 0.5;
            float radius = (hi.x - lo.x) * 0.5;
            float3 delta = worldPosition - center;
            iterator.Current = missNext;
            if (dot(delta, delta) <= radius * radius)
                return -hitNext - 1;
        }
        else
        {
            bool inside = all(worldPosition >= lo.xyz) && all(worldPosition <= hi.xyz);
            iterator.Current = inside ? hitNext : missNext;
        }
    }
    return -1;
}

#endif
