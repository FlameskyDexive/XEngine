// HovlCore — shared vertex/fragment logic for the ported Hovl Studio particle shaders
// (package com.xengine.rpgvfx). GLSL side; HovlCore.hlsl is the Slang mirror.
//
// Stage contract: the .shader files define HOVL_STAGE_VERTEX before including this file in
// the Vertex block and HOVL_STAGE_FRAGMENT in the Fragment block, so the varyings below
// compile with the right in/out qualification for each stage.
//
// Engine contract (rpgvfx A2/A4):
//   instanceCustomData = (normalized lifetime, uv offset X, uv offset Y, uv scale)
//   instanceModelRow0.w / Row1.w = Unity UV0.zw (selected Custom1.xy vertex stream)
//   instanceModelRow2 = (world velocity, 1 + total length) for stretched billboards
//   instanceModelRow3.w = UV scale Y / X (1 for square flipbooks or no animation)

#include "XEngineCG"

#if defined(HOVL_STAGE_VERTEX)
out vec2 hovlUv;
out vec4 hovlColor;
out float hovlRandom;
out float hovlEyeDepth;
out float hovlLife;
out vec4 hovlScreen;
#elif defined(HOVL_STAGE_FRAGMENT)
in vec2 hovlUv;
in vec4 hovlColor;
in float hovlRandom;
in float hovlEyeDepth;
in float hovlLife;
in vec4 hovlScreen;
#endif

#if defined(HOVL_STAGE_VERTEX)

// Full billboard/mesh vertex for instanced particles (mirrors Default/Particle):
// view-plane billboard with Z rotation, flipbook uv from instanceCustomData, authored
// Custom1.xy in the unused affine matrix w lanes, and eye depth for soft particles.
void HovlBillboardVS(vec3 vertexPosition, vec2 vertexTexCoord0, out vec4 clipPos)
{
#ifdef GPU_INSTANCING
    vec3 particlePosition = instanceModelRow3.xyz;
    float scaleX = length(instanceModelRow0.xyz);
    float scaleY = length(instanceModelRow1.xyz);

#ifdef PARTICLE_MESH
    // InstanceData stores Float4x4.c0..c3 despite the historic Row attribute names.
    vec3 worldPosition = instanceModelRow0.xyz * vertexPosition.x
        + instanceModelRow1.xyz * vertexPosition.y
        + instanceModelRow2.xyz * vertexPosition.z
        + instanceModelRow3.xyz;
#else
    vec3 matrixRight = instanceModelRow0.xyz / max(scaleX, 0.0001);
    float rotationAngle = atan(matrixRight.y, matrixRight.x);
    vec3 cameraRight = vec3(XENGINE_MATRIX_V[0][0], XENGINE_MATRIX_V[1][0], XENGINE_MATRIX_V[2][0]);
    vec3 cameraUp = vec3(XENGINE_MATRIX_V[0][1], XENGINE_MATRIX_V[1][1], XENGINE_MATRIX_V[2][1]);
    float cosRot = cos(rotationAngle);
    float sinRot = sin(rotationAngle);
    vec2 rotated = vec2(
        vertexPosition.x * cosRot - vertexPosition.y * sinRot,
        vertexPosition.x * sinRot + vertexPosition.y * cosRot);
    vec3 worldPosition = particlePosition
        + cameraRight * rotated.x * scaleX
        + cameraUp * rotated.y * scaleY;
    if (instanceModelRow2.w > 0.5)
    {
        vec3 velocity = instanceModelRow2.xyz;
        float speed = length(velocity);
        vec3 velocityDir = speed > 0.0001 ? velocity / speed : vec3(0.0);
        // Legacy Unity Stretch keeps width in the camera plane, while its tail
        // follows the full 3D velocity. UV.x=0 is the head, not the quad center.
        vec3 projectedSide = cross(_WorldSpaceCameraPos.xyz - particlePosition, velocity);
        vec2 sideInView = vec2(dot(projectedSide, cameraRight), dot(projectedSide, cameraUp));
        float sideLength = length(sideInView);
        vec3 side = sideLength > 0.0001
            ? (cameraRight * sideInView.x + cameraUp * sideInView.y) / sideLength : cameraRight;
        float stretchLength = max(instanceModelRow2.w - 1.0, 0.0);
        worldPosition = particlePosition + side * vertexPosition.y * scaleY
            - velocityDir * (vertexPosition.x + 0.5) * stretchLength;
    }
#endif

    clipPos = XENGINE_MATRIX_VP * vec4(worldPosition, 1.0);

    // Flipbook: offsets and X scale ride in instanceCustomData.yzw; c3.w carries Y/X.
    hovlUv = vertexTexCoord0 * vec2(instanceCustomData.w, instanceCustomData.w * instanceModelRow3.w)
        + instanceCustomData.yz;
    hovlColor = instanceColor;
    hovlEyeDepth = abs((XENGINE_MATRIX_V * vec4(worldPosition, 1.0)).z);
    // Legacy varying names are preserved; these are authored UV0.zw, not age/seed.
    hovlLife = instanceModelRow0.w;
    hovlRandom = instanceModelRow1.w;
    hovlScreen = vec4(clipPos.xy,
        clipPos.w / max(distance(_WorldSpaceCameraPos.xyz, worldPosition), 0.0001), clipPos.w);
#else
    clipPos = XENGINE_MATRIX_MVP * vec4(vertexPosition, 1.0);
    hovlUv = vertexTexCoord0;
    // Ribbon trails carry authored lifetime/color gradients in the mesh COLOR stream.
    // VertexAttributes supplies white for ordinary meshes that have no color channel.
    hovlColor = vertexColor;
    hovlRandom = 0.0;
    hovlLife = 0.0;
    vec3 worldPosition = (XENGINE_MATRIX_M * vec4(vertexPosition, 1.0)).xyz;
    hovlEyeDepth = abs((XENGINE_MATRIX_V * vec4(worldPosition, 1.0)).z);
    hovlScreen = vec4(clipPos.xy,
        clipPos.w / max(distance(_WorldSpaceCameraPos.xyz, worldPosition), 0.0001), clipPos.w);
#endif
}

#endif // HOVL_STAGE_VERTEX

// ---- shared fragment helpers (both stages see them; only fragments call them) ----

float HovlLinearEyeDepth(float rawDepth)
{
    float nearPlane = _ProjectionParams.y;
    float farPlane = _ProjectionParams.z;
    float ndc = rawDepth * 2.0 - 1.0;
    return 2.0 * nearPlane * farPlane / (farPlane + nearPlane - ndc * (farPlane - nearPlane));
}

// Unity soft-particle semantics: the scene being FARTHER than the particle keeps it visible;
// the fade only bites when the particle sits right behind geometry. (The Default/Particle
// shader's inverted comparison is corrected here — see A4 evidence.)
float HovlSoftFade(float particleEyeDepth, float sceneEyeDepth, float power, float useDepth)
{
    if (useDepth < 0.5 || particleEyeDepth <= 0.0)
        return 1.0;
    float sceneEye = HovlLinearEyeDepth(sceneEyeDepth);
    float fade = clamp((sceneEye - particleEyeDepth) / max(power, 0.001), 0.0, 1.0);
    return fade;
}

vec2 HovlPanner(float time, vec2 speed, vec2 uv)
{
    return time * speed + uv;
}

vec4 HovlToScreen(vec3 rgb, float alpha)
{
    // The Unity source has no alpha test. In particular, additive RGB is already
    // premultiplied while its alpha is squared: clipping alpha removes visible glow.
    // Preserve HDR emission here. Matching the source project's Linear color space
    // requires decoding the individual authored color inputs before multiplication;
    // applying gammaToLinearSpace to the final RGB would also transform emission.
    return vec4(rgb, alpha);
}
