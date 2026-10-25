// HovlCore — Slang mirror of HovlCore.glsl (see that file for the contract).
// Stage contract: Vertex blocks compile HovlBillboardVS; Fragment blocks use the helpers.

#include "XEngineCG"

struct HovlVaryings
{
    float2 uv;
    float4 color;
    float random;
    float eyeDepth;
};

struct HovlVSInput
{
    float3 vertexPosition : POSITION;
    float2 vertexTexCoord0 : TEXCOORD0;
#if defined(HAS_COLORS) && !defined(GPU_INSTANCING)
    float4 vertexColor : COLOR0;
#endif
#ifdef GPU_INSTANCING
    float4 instanceModelRow0 : TEXCOORD8;
    float4 instanceModelRow1 : TEXCOORD9;
    float4 instanceModelRow2 : TEXCOORD10;
    float4 instanceModelRow3 : TEXCOORD11;
    float4 instanceColor : TEXCOORD12;
    float4 instanceCustomData : TEXCOORD13;
#endif
};

struct HovlVSOutput
{
    float4 clipPos : SV_Position;
    float2 uv : TEXCOORD0;
    float4 color : COLOR0;
    float random : TEXCOORD1;
    float eyeDepth : TEXCOORD2;
    float life : TEXCOORD3;
    float4 screen : TEXCOORD4;
};

#if defined(HOVL_STAGE_VERTEX)

void HovlBillboardVS(HovlVSInput input, out HovlVSOutput o)
{
#ifdef GPU_INSTANCING
    float3 particlePosition = input.instanceModelRow3.xyz;
    float scaleX = length(input.instanceModelRow0.xyz);
    float scaleY = length(input.instanceModelRow1.xyz);

#ifdef PARTICLE_MESH
    // InstanceData stores Float4x4.c0..c3 despite the historic Row attribute names.
    float3 worldPosition = input.instanceModelRow0.xyz * input.vertexPosition.x
        + input.instanceModelRow1.xyz * input.vertexPosition.y
        + input.instanceModelRow2.xyz * input.vertexPosition.z
        + input.instanceModelRow3.xyz;
#else
    float3 matrixRight = input.instanceModelRow0.xyz / max(scaleX, 0.0001);
    float rotationAngle = atan2(matrixRight.y, matrixRight.x);
    float3 cameraRight = float3(XENGINE_MATRIX_V[0][0], XENGINE_MATRIX_V[0][1], XENGINE_MATRIX_V[0][2]);
    float3 cameraUp = float3(XENGINE_MATRIX_V[1][0], XENGINE_MATRIX_V[1][1], XENGINE_MATRIX_V[1][2]);
    float cosRot = cos(rotationAngle);
    float sinRot = sin(rotationAngle);
    float2 rotated = float2(
        input.vertexPosition.x * cosRot - input.vertexPosition.y * sinRot,
        input.vertexPosition.x * sinRot + input.vertexPosition.y * cosRot);
    float3 worldPosition = particlePosition
        + cameraRight * rotated.x * scaleX
        + cameraUp * rotated.y * scaleY;
    if (input.instanceModelRow2.w > 0.5)
    {
        float3 velocity = input.instanceModelRow2.xyz;
        float speed = length(velocity);
        float3 velocityDir = speed > 0.0001 ? velocity / speed : float3(0.0, 0.0, 0.0);
        float3 projectedSide = cross(_WorldSpaceCameraPos.xyz - particlePosition, velocity);
        float2 sideInView = float2(dot(projectedSide, cameraRight), dot(projectedSide, cameraUp));
        float sideLength = length(sideInView);
        float3 side = sideLength > 0.0001
            ? (cameraRight * sideInView.x + cameraUp * sideInView.y) / sideLength : cameraRight;
        float stretchLength = max(input.instanceModelRow2.w - 1.0, 0.0);
        worldPosition = particlePosition + side * input.vertexPosition.y * scaleY
            - velocityDir * (input.vertexPosition.x + 0.5) * stretchLength;
    }
#endif

    o.clipPos = mul(XENGINE_MATRIX_VP, float4(worldPosition, 1.0));
    o.uv = input.vertexTexCoord0 * float2(input.instanceCustomData.w,
        input.instanceCustomData.w * input.instanceModelRow3.w) + input.instanceCustomData.yz;
    o.color = input.instanceColor;
    o.eyeDepth = abs(mul(XENGINE_MATRIX_V, float4(worldPosition, 1.0)).z);
    o.life = input.instanceModelRow0.w;
    o.random = input.instanceModelRow1.w;
    o.screen = float4(o.clipPos.xy,
        o.clipPos.w / max(distance(_WorldSpaceCameraPos.xyz, worldPosition), 0.0001), o.clipPos.w);
#else
    o.clipPos = mul(XENGINE_MATRIX_VP, mul(XENGINE_MATRIX_M, float4(input.vertexPosition, 1.0)));
    o.uv = input.vertexTexCoord0;
#ifdef HAS_COLORS
    o.color = input.vertexColor;
#else
    o.color = float4(1.0, 1.0, 1.0, 1.0);
#endif
    o.random = 0.0;
    o.life = 0.0;
    float3 worldPosition = mul(XENGINE_MATRIX_M, float4(input.vertexPosition, 1.0)).xyz;
    o.eyeDepth = abs(mul(XENGINE_MATRIX_V, float4(worldPosition, 1.0)).z);
    o.screen = float4(o.clipPos.xy,
        o.clipPos.w / max(distance(_WorldSpaceCameraPos.xyz, worldPosition), 0.0001), o.clipPos.w);
#endif
}

#endif // HOVL_STAGE_VERTEX

float HovlLinearEyeDepth(float rawDepth)
{
    float nearPlane = _ProjectionParams.y;
    float farPlane = _ProjectionParams.z;
    float ndc = rawDepth * 2.0 - 1.0;
    return 2.0 * nearPlane * farPlane / (farPlane + nearPlane - ndc * (farPlane - nearPlane));
}

float HovlSoftFade(float particleEyeDepth, float sceneEyeDepthRaw, float power, float useDepth)
{
    if (useDepth < 0.5 || particleEyeDepth <= 0.0)
        return 1.0;
    float sceneEye = HovlLinearEyeDepth(sceneEyeDepthRaw);
    return saturate((sceneEye - particleEyeDepth) / max(power, 0.001));
}

float2 HovlPanner(float time, float2 speed, float2 uv)
{
    return time * speed + uv;
}

float4 HovlToScreen(float3 rgb, float alpha)
{
    // Preserve the source's smooth low-alpha tails and additive premultiplied RGB.
    // See HovlCore.glsl: color-space conversion belongs before HDR multiplication.
    return float4(rgb, alpha);
}
