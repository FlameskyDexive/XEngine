#ifndef SHADER_SHADERVARIABLES
#define SHADER_SHADERVARIABLES

// Global Uniform Buffer containing per-frame shared rendering data
// Uses std140 layout for compatibility across all GPUs
// This data is uploaded once per frame and is constant across all draw calls
// Note: binding qualifier requires GLSL 420+, otherwise binding is set via glUniformBlockBinding in C# code
#if __VERSION__ >= 420
layout(std140, binding = 0) uniform GlobalUniforms
#else
layout(std140) uniform GlobalUniforms
#endif
{
    // Camera matrices
    mat4 xengine_MatV;
    mat4 xengine_MatIV;
    mat4 xengine_MatP;
    mat4 xengine_MatVP;
    mat4 xengine_PrevViewProj;
    mat4 xengine_MatIP;
    mat4 xengine_MatIVP;
    mat4 xengine_MatVP_NonJittered;

    // Camera parameters
    vec3 _WorldSpaceCameraPos;
    float _padding0;

    vec4 _ProjectionParams;
    vec4 _ScreenParams;
    vec2 _CameraJitter;
    vec2 _CameraPreviousJitter;

    // Time parameters
    vec4 _Time;
    vec4 _SinTime;
    vec4 _CosTime;
    vec4 xengine_DeltaTime;
};

// Per-object uniforms (set per draw call)
uniform mat4 xengine_ObjectToWorld;
uniform mat4 xengine_WorldToObject;
uniform mat4 xengine_PrevObjectToWorld;
uniform int _ObjectID;
uniform int _RenderingLayerMask;

#define XENGINE_MATRIX_V xengine_MatV
#define XENGINE_MATRIX_VP_PREVIOUS xengine_PrevViewProj
#define XENGINE_MATRIX_I_V xengine_MatIV
#define XENGINE_MATRIX_P xengine_MatP
#define XENGINE_MATRIX_VP xengine_MatVP
#define XENGINE_MATRIX_I_P xengine_MatIP
#define XENGINE_MATRIX_I_VP xengine_MatIVP
#define XENGINE_MATRIX_VP_NONJITTERED xengine_MatVP_NonJittered
#define XENGINE_MATRIX_M xengine_ObjectToWorld
#define XENGINE_MATRIX_M_PREVIOUS xengine_PrevObjectToWorld

// Derived matrices
mat4 xengine_MatMV = xengine_MatV * xengine_ObjectToWorld;
mat4 xengine_MatMVP = xengine_MatVP * xengine_ObjectToWorld;
mat4 xengine_MatTMV = transpose(xengine_MatV * xengine_ObjectToWorld);
mat4 xengine_MatITMV = transpose(xengine_WorldToObject * xengine_MatIV);

#define XENGINE_MATRIX_MV xengine_MatMV
#define XENGINE_MATRIX_MVP xengine_MatMVP
#define XENGINE_MATRIX_T_MV xengine_MatTMV
#define XENGINE_MATRIX_IT_MV xengine_MatITMV

#endif