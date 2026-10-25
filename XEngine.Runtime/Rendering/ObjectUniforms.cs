// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Runtime.InteropServices;

using XEngine.Vector;

namespace XEngine.Runtime.Rendering;

// Per-object constant buffer (HLSL ObjectUniforms : register(b1)). Carries the matrices the engine
// always uploads plus the per-object baked-GI state the Standard shader's CalculateGI reads:
// _GIMode selects realtime-ambient / lightmap / light-probe-SH, _LightmapUV picks the UV set the
// lightmap was baked into, _LightmapScaleOffset is per-renderer, and xengine_SH* carry the sampled
// light-probe coefficients for dynamic objects. Kept in b1 so every Standard draw already pays for
// one per-object uniform snapshot and no second cbuffer is needed.
//
// Layout is fixed by LayoutKind.Explicit at the byte offsets the matching HLSL cbuffer reports, so
// the marshal is a byte-exact mirror of the ObjectUniforms the DXC reflection produces.
[StructLayout(LayoutKind.Explicit, Size = 352)]
internal struct ObjectUniformsData
{
#pragma warning disable IDE1006
    [FieldOffset(0)]   public Float4x4 xengine_ObjectToWorld;
    [FieldOffset(64)]  public Float4x4 xengine_WorldToObject;
    [FieldOffset(128)] public Float4x4 xengine_PrevObjectToWorld;
    [FieldOffset(192)] public int _ObjectID;
    // _GIMode + _LightmapUV + _RenderingLayerMask share the trailing 12 bytes of the
    // 192..208 slot so the existing ObjectUniforms layout stays backward-compatible.
    [FieldOffset(196)] public int _GIMode;
    [FieldOffset(200)] public int _LightmapUV;
    // 8-bit rendering-layer membership (default Everything). Replaces the unused padding scalar.
    [FieldOffset(204)] public int _RenderingLayerMask;
    // Per-renderer lightmap UV scale/offset, used when _GIMode == 1 (baked lightmap).
    [FieldOffset(208)] public Float4 _LightmapScaleOffset;
    // Light-probe SH L2 coefficients (the standard 7-vec4 ShadeSH9 packing), used when _GIMode == 2.
    [FieldOffset(224)] public Float4 xengine_SHAr;
    [FieldOffset(240)] public Float4 xengine_SHAg;
    [FieldOffset(256)] public Float4 xengine_SHAb;
    [FieldOffset(272)] public Float4 xengine_SHBr;
    [FieldOffset(288)] public Float4 xengine_SHBg;
    [FieldOffset(304)] public Float4 xengine_SHBb;
    [FieldOffset(320)] public Float4 xengine_SHC;
    // Skinned-draw bone count (SkinnedMeshRenderer props "boneCount"), mirrors the tail
    // int boneCount member of the HLSL ObjectUniforms cbuffer. 0 = unskinned draws.
    [FieldOffset(336)] public int BoneCount;
#pragma warning restore IDE1006

    public static ObjectUniformsData Identity => new()
    {
        xengine_ObjectToWorld = Float4x4.Identity,
        xengine_WorldToObject = Float4x4.Identity,
        xengine_PrevObjectToWorld = Float4x4.Identity,
        // Default _GIMode == 0 -> realtime ambient (the existing Standard path); _LightmapUV
        // defaults to 0 (primary UV), which matches LightmapBinding's meshHasUV2-false fallback.
        // Everything (255) so draws that omit _RenderingLayerMask keep full lighting.
        _RenderingLayerMask = unchecked((int)RenderingLayerMask.Everything.Mask),
    };
}
