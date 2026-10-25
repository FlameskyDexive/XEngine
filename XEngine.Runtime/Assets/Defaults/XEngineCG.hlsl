#ifndef XENGINE_CG_HLSL
#define XENGINE_CG_HLSL

// Minimal HLSL counterpart of ShaderVariables / XEngineCG for dual-backend shaders.
// Layout mirrors the GLSL GlobalUniforms UBO where practical.

cbuffer GlobalUniforms : register(b0)
{
    float4x4 xengine_MatV;
    float4x4 xengine_MatIV;
    float4x4 xengine_MatP;
    float4x4 xengine_MatVP;
    float4x4 xengine_PrevViewProj;
    float4x4 xengine_MatIP;
    float4x4 xengine_MatIVP;
    float4x4 xengine_MatVP_NonJittered;

    float3 _WorldSpaceCameraPos;
#if defined(XENGINE_METAL)
    // Metal gives float3 a full 16-byte slot. Keep the following block aligned with
    // GlobalUniformsData by reserving a full register here; the Metal uploader inserts it.
    float4 _padding0;
#else
    float _padding0;
#endif

    float4 _ProjectionParams;
    float4 _ScreenParams;
    float2 _CameraJitter;
    float2 _CameraPreviousJitter;

    float4 _Time;
    float4 _SinTime;
    float4 _CosTime;
    float4 xengine_DeltaTime;
};

cbuffer ObjectUniforms : register(b1)
{
    float4x4 xengine_ObjectToWorld;
    float4x4 xengine_WorldToObject;
    float4x4 xengine_PrevObjectToWorld;
    int _ObjectID;
    // Per-object baked-GI state (CalculateGI in StandardSurface / StandardLighting). _GIMode selects
    // realtime-ambient (0) / baked lightmap (1) / light-probe SH (2). _LightmapUV picks the UV set
    // the lightmap was baked into (1 = UV2, 0 = UV0 fallback).
    int _GIMode;
    int _LightmapUV;
    // 8-bit rendering-layer membership. Default Everything (255) keeps lighting unchanged.
    int _RenderingLayerMask;
    // Per-renderer lightmap UV scale/offset, used when _GIMode == 1.
    float4 _LightmapScaleOffset;
    // Light-probe SH L2 coefficients (the standard 7-vec4 ShadeSH9 packing), used when _GIMode == 2.
    float4 xengine_SHAr;
    float4 xengine_SHAg;
    float4 xengine_SHAb;
    float4 xengine_SHBr;
    float4 xengine_SHBg;
    float4 xengine_SHBb;
    float4 xengine_SHC;
};

#define XENGINE_MATRIX_V xengine_MatV
#define XENGINE_MATRIX_VP_PREVIOUS xengine_PrevViewProj
#define XENGINE_MATRIX_I_V xengine_MatIV
#define XENGINE_MATRIX_P xengine_MatP
#define XENGINE_MATRIX_VP xengine_MatVP
#define XENGINE_MATRIX_I_P xengine_MatIP
#define XENGINE_MATRIX_I_VP xengine_MatIVP
#define XENGINE_MATRIX_VP_NONJITTERED xengine_MatVP_NonJittered
#if defined(GPU_INSTANCING) && defined(XENGINE_STANDARD_INSTANCE_VERTEX)
static float4x4 xengine_InstanceModel;
static float4 xengine_InstanceColor;
static int xengine_SkinPaletteRow;
static int xengine_MorphPaletteRow;
static int xengine_MorphActiveCount;
#define XENGINE_INSTANCE_ATTRIBUTES float4 instanceModel0 : TEXCOORD8; float4 instanceModel1 : TEXCOORD9; float4 instanceModel2 : TEXCOORD10; float4 instanceModel3 : TEXCOORD11; float4 instanceColor : TEXCOORD12; float4 instanceCustomData : TEXCOORD13;
#define XENGINE_SETUP_INSTANCE(v) xengine_InstanceModel = transpose(float4x4(v.instanceModel0, v.instanceModel1, v.instanceModel2, v.instanceModel3)); xengine_InstanceColor = v.instanceColor; xengine_SkinPaletteRow = (int)v.instanceCustomData.x; xengine_MorphPaletteRow = (int)v.instanceCustomData.y; xengine_MorphActiveCount = (int)v.instanceCustomData.z
#define XENGINE_MATRIX_M xengine_InstanceModel
#else
#define XENGINE_INSTANCE_ATTRIBUTES
#define XENGINE_SETUP_INSTANCE(v)
#define XENGINE_MATRIX_M xengine_ObjectToWorld
#endif
#define XENGINE_MATRIX_M_PREVIOUS xengine_PrevObjectToWorld

float4x4 GetModelMatrix() { return XENGINE_MATRIX_M; }

// =============================================================
//  Skeletal Animation (Skinning) — clean-base reimplementation.
//  Lesson from the failed first attempt (skinning-wip branch): the path was
//  built and debugged on top of THREE stacked breakages, so no probe result
//  was ever trustworthy. This version differs in one decisive way: boneCount
//  is a compile-time constant (XENGINE_SKIN_BONE_COUNT) instead of a cbuffer —
//  the old dedicated cbuffer(b10) had no binder and read 0, collapsing every
//  weighted vertex. Texture slot is t15, the only slot no other shader claims
//  (Standard materials t0-t3/t9/t10, lighting t4-t8/t11/t14, Toon spec t12,
//  light cookie atlas t13). Bound BY NAME from SkinnedMeshRenderer.
// =============================================================
#ifdef SKINNED

Texture2D<float4> boneMatrixTexture : register(t15);
#define XENGINE_SKIN_BONE_COUNT 512

static const int XENGINE_MAX_BONE_INFLUENCE = 4;

float4x4 XSkinGetBoneMatrix(int boneIndex)
{
    // C# Float4x4 stores columns consecutively (c0..c3, one texel each), while Slang matrix
    // initializers consume rows regardless of the selected uniform-memory layout. Fold the four
    // uploaded columns into rows so mul(matrix, columnVector) matches the CPU transform exactly.
    int t = boneIndex * 4;
    int row = 0;
#if defined(GPU_INSTANCING) && defined(XENGINE_STANDARD_INSTANCE_VERTEX)
    row = xengine_SkinPaletteRow;
#endif
    float4 c0 = boneMatrixTexture.Load(int3(t + 0, row, 0));
    float4 c1 = boneMatrixTexture.Load(int3(t + 1, row, 0));
    float4 c2 = boneMatrixTexture.Load(int3(t + 2, row, 0));
    float4 c3 = boneMatrixTexture.Load(int3(t + 3, row, 0));
    return float4x4(
        float4(c0.x, c1.x, c2.x, c3.x),
        float4(c0.y, c1.y, c2.y, c3.y),
        float4(c0.z, c1.z, c2.z, c3.z),
        float4(c0.w, c1.w, c2.w, c3.w));
}

float4 XSkinPosition(float3 position, float4 boneIdx, float4 boneWgt)
{
    float4 skinned = float4(0.0, 0.0, 0.0, 0.0);
    [unroll]
    for (int i = 0; i < XENGINE_MAX_BONE_INFLUENCE; i++)
    {
        int boneIndex = (int)boneIdx[i];
        float weight = boneWgt[i];
        if (boneIndex > 0 && weight > 0.0 && boneIndex <= XENGINE_SKIN_BONE_COUNT)
            skinned += mul(XSkinGetBoneMatrix(boneIndex - 1), float4(position, 1.0)) * weight;
    }
    if (boneWgt.x + boneWgt.y + boneWgt.z + boneWgt.w < 0.01)
        skinned = float4(position, 1.0);
    return skinned;
}

float3 XSkinNormal(float3 normal, float4 boneIdx, float4 boneWgt)
{
    float3 skinned = float3(0.0, 0.0, 0.0);
    [unroll]
    for (int i = 0; i < XENGINE_MAX_BONE_INFLUENCE; i++)
    {
        int boneIndex = (int)boneIdx[i];
        float weight = boneWgt[i];
        if (boneIndex > 0 && weight > 0.0 && boneIndex <= XENGINE_SKIN_BONE_COUNT)
            skinned += mul((float3x3)XSkinGetBoneMatrix(boneIndex - 1), normal) * weight;
    }
    return skinned;
}

float4 TransformClipSkinned(float3 objectPos, float4 boneIdx, float4 boneWgt)
{
    return mul(XENGINE_MATRIX_VP, mul(XENGINE_MATRIX_M, XSkinPosition(objectPos, boneIdx, boneWgt)));
}

float3 TransformPositionSkinned(float3 objectPos, float4 boneIdx, float4 boneWgt)
{
    return mul(XENGINE_MATRIX_M, XSkinPosition(objectPos, boneIdx, boneWgt)).xyz;
}

#endif // SKINNED

float4 TransformClip(float3 objectPos)
{
    return mul(XENGINE_MATRIX_VP, mul(XENGINE_MATRIX_M, float4(objectPos, 1.0)));
}

float3 TransformPosition(float3 objectPos)
{
    return mul(XENGINE_MATRIX_M, float4(objectPos, 1.0)).xyz;
}

float3 TransformDirection(float3 dir)
{
    return normalize(mul((float3x3)XENGINE_MATRIX_M, dir));
}

// Camera-centered sky dome: drop view translation so the mesh is not a world-space sphere.
// Perspective already fills the far plane. Ortho maps a unit dome to a tiny NDC circle
// (P scales by 2/orthoSize). Scale XY well past the NDC corners so a coarse sphere
// tessellation still covers the rectangle (mesh radius ~0.95, inscribed-polygon chords).
float4 TransformSkyboxClip(float3 objectPos)
{
    float3 viewDir = mul((float3x3)XENGINE_MATRIX_V, objectPos);
    float4 clip = mul(XENGINE_MATRIX_P, float4(viewDir, 1.0));
    float4 origin = mul(XENGINE_MATRIX_P, float4(0.0, 0.0, 0.0, 1.0));
    if (abs(origin.w) > 0.5)
    {
        clip.x = viewDir.x * 4.0 * origin.w;
        clip.y = viewDir.y * 4.0 * origin.w;
    }
    clip.z = clip.w;
    // Ortho flattens the dome along view Z, so with ZTest Off the behind-camera hemisphere
    // lands on the same pixels as the sky and submission order decides each pixel.
    // Perspective is safe (w = -viewZ hardware-clips it); here push those verts past the
    // far plane so the clipper removes them.
    if (abs(origin.w) > 0.5 && viewDir.z > 0.0)
        clip.z = 2.0 * abs(clip.w) + 1.0;
    return clip;
}

float3 linearToGammaSpace(float3 lin)
{
    return max(1.055 * pow(max(lin, 0.0.xxx), 0.416666667.xxx) - 0.055.xxx, 0.0.xxx);
}

float3 gammaToLinearSpace(float3 gamma)
{
    return gamma * (gamma * (gamma * 0.305306011 + 0.682171111) + 0.012522878);
}

float linearizeDepth(float depth, float nearZ, float farZ)
{
    float z = depth * 2.0 - 1.0;
    return (2.0 * nearZ * farZ) / (farZ + nearZ - z * (farZ - nearZ));
}

float linearizeDepthFromProjection(float depth)
{
    return linearizeDepth(depth, _ProjectionParams.y, _ProjectionParams.z);
}

float4 EncodeViewNormal(float3 worldNormal)
{
    float3 viewN = normalize(mul((float3x3)XENGINE_MATRIX_V, worldNormal));
    return float4(viewN * 0.5 + 0.5, _RenderingLayerMask / 255.0);
}

float3 DecodeViewNormal(float4 encoded)
{
    float3 viewN = encoded.xyz * 2.0 - 1.0;
    return normalize(mul((float3x3)XENGINE_MATRIX_I_V, viewN));
}

float3 ApplyFog(float3 color, float3 worldPos)
{
    // Stub: full fog lives in Lighting.glsl; keep identity until HLSL lighting is ported.
    return color;
}

float4 GetInstanceColor()
{
#if defined(GPU_INSTANCING) && defined(XENGINE_STANDARD_INSTANCE_VERTEX)
    return xengine_InstanceColor;
#else
    return float4(1, 1, 1, 1);
#endif
}

// =============================================================
//  Blend Shapes (morph targets) — Slang path.
//  Mirrors the GLSL block in VertexAttributes.glsl: per-mesh RGBA32F delta
//  textures hold one "layer" per blend-shape frame, laid out linearly as
//  idx = layer * morphVertexCount + vertexID and tiled 2D as
//  (idx % morphTexWidth, idx / morphTexWidth). The renderer uploads only the
//  ACTIVE layers (non-zero weight) into morphWeightTexture, one texel each:
//  (layerIndex, weight, 0, 0). Morphing happens on the rest-pose vertex
//  BEFORE skinning.
//
//  Binding channels (both proven by the skinned path): the four textures ride
//  the per-draw instance texture state by name (like boneMatrixTexture t15),
//  and the five scalars ride the per-draw instance ints, which qualify the
//  draw for a material-block repack — the generic reflection packer fills the
//  MorphParams block declared beneath this comment on every backend that has
//  one (D3D12/WebGPU). Note: avoid the word "cbuffer" in this comment — the
//  register scanner's regex captures "<cbuffer> <identifier>" even in prose.
//  b7 / t16-t19 are the only slots no other shader claims (b3-b6 = lighting,
//  shadows, reflection probes, light cookies).
// =============================================================
#ifdef BLENDSHAPES

cbuffer MorphParams : register(b7)
{
	// No trailing vec3 padding: WGSL gives int3 a 16-byte alignment, which would make this
	// block 48 bytes there while D3D12 packs it as 32 — keep plain scalars so every backend
	// reflects the same footprint.
	int morphActiveCount;
	int morphTexWidth;
	int morphVertexCount;
	int morphHasNormals;
	int morphHasTangents;
};

Texture2D<float4> morphPositionTexture : register(t16);
Texture2D<float4> morphNormalTexture : register(t17);
Texture2D<float4> morphTangentTexture : register(t18);
Texture2D<float4> morphWeightTexture : register(t19);

float3 XMorphSampleDelta(Texture2D<float4> deltaTex, int layer, uint vertexId)
{
	int idx = layer * morphVertexCount + (int)vertexId;
	int2 coord = int2(idx % morphTexWidth, idx / morphTexWidth);
	return deltaTex.Load(int3(coord, 0)).xyz;
}

float3 GetMorphedPosition(float3 position, uint vertexId)
{
	#if defined(GPU_INSTANCING) && defined(XENGINE_STANDARD_INSTANCE_VERTEX)
	int morphRow = xengine_MorphPaletteRow;
	int morphCount = xengine_MorphActiveCount;
	#else
	int morphRow = 0;
	int morphCount = morphActiveCount;
	#endif
	for (int i = 0; i < morphCount; i++)
	{
		float2 lw = morphWeightTexture.Load(int3(i, morphRow, 0)).xy;
		position += XMorphSampleDelta(morphPositionTexture, int(lw.x), vertexId) * lw.y;
	}
	return position;
}

float3 GetMorphedNormal(float3 normal, uint vertexId)
{
	if (morphHasNormals == 0) return normal;
	#if defined(GPU_INSTANCING) && defined(XENGINE_STANDARD_INSTANCE_VERTEX)
	int morphRow = xengine_MorphPaletteRow;
	int morphCount = xengine_MorphActiveCount;
	#else
	int morphRow = 0;
	int morphCount = morphActiveCount;
	#endif
	for (int i = 0; i < morphCount; i++)
	{
		float2 lw = morphWeightTexture.Load(int3(i, morphRow, 0)).xy;
		normal += XMorphSampleDelta(morphNormalTexture, int(lw.x), vertexId) * lw.y;
	}
	return normal;
}

float3 GetMorphedTangent(float3 tangent, uint vertexId)
{
	if (morphHasTangents == 0) return tangent;
	#if defined(GPU_INSTANCING) && defined(XENGINE_STANDARD_INSTANCE_VERTEX)
	int morphRow = xengine_MorphPaletteRow;
	int morphCount = xengine_MorphActiveCount;
	#else
	int morphRow = 0;
	int morphCount = morphActiveCount;
	#endif
	for (int i = 0; i < morphCount; i++)
	{
		float2 lw = morphWeightTexture.Load(int3(i, morphRow, 0)).xy;
		tangent += XMorphSampleDelta(morphTangentTexture, int(lw.x), vertexId) * lw.y;
	}
	return tangent;
}

#else

// No-op passthroughs so Slang vertex stages can call these unconditionally
// (GLSL gets the same contract from VertexAttributes.glsl with gl_VertexID implicit).
float3 GetMorphedPosition(float3 position, uint vertexId) { return position; }
float3 GetMorphedNormal(float3 normal, uint vertexId) { return normal; }
float3 GetMorphedTangent(float3 tangent, uint vertexId) { return tangent; }

#endif

// One-argument compatibility overloads: game shaders written before morphs reached the Slang
// path (e.g. Zonezero Toon) call these. They cannot fetch deltas (no SV_VertexID in hand), so
// they stay identity — upstream those call sites to the two-argument forms to opt into morphing.
float3 GetMorphedPosition(float3 position) { return position; }
float3 GetMorphedNormal(float3 normal) { return normal; }
float3 GetMorphedTangent(float3 tangent) { return tangent; }

#endif
