#ifndef SHADER_VERTEXATTRIBUTES_HLSL
#define SHADER_VERTEXATTRIBUTES_HLSL

// HLSL vertex attribute layout matching VertexAttributes.glsl locations.

struct XEngineVertexInput
{
    float3 vertexPosition : POSITION;
#ifdef HAS_UV
    float2 vertexTexCoord0 : TEXCOORD0;
#endif
#ifdef HAS_UV2
    float2 vertexTexCoord1 : TEXCOORD1;
#endif
#ifdef HAS_NORMALS
    float3 vertexNormal : NORMAL;
#endif
#ifdef HAS_COLORS
    float4 vertexColor : COLOR;
#endif
#ifdef HAS_TANGENTS
    float4 vertexTangent : TANGENT;
#endif
#ifdef HAS_BONEINDICES
    float4 vertexBoneIndices : BLENDINDICES;
#endif
#ifdef HAS_BONEWEIGHTS
    float4 vertexBoneWeights : BLENDWEIGHT;
#endif
};

#ifndef HAS_UV
static float2 vertexTexCoord0 = float2(0.0, 0.0);
#endif
#ifndef HAS_UV2
static float2 vertexTexCoord1 = float2(0.0, 0.0);
#endif
#ifndef HAS_NORMALS
static float3 vertexNormal = float3(0.0, 1.0, 0.0);
#endif
#ifndef HAS_COLORS
static float4 vertexColor = float4(1.0, 1.0, 1.0, 1.0);
#endif
#ifndef HAS_TANGENTS
static float4 vertexTangent = float4(1.0, 0.0, 0.0, 1.0);
#endif
#ifndef HAS_BONEINDICES
static float4 vertexBoneIndices = float4(0.0, 0.0, 0.0, 0.0);
#endif
#ifndef HAS_BONEWEIGHTS
static float4 vertexBoneWeights = float4(0.0, 0.0, 0.0, 0.0);
#endif

#endif
