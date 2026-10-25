// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

using XEngine.Runtime.RHI;
using XEngine.Vector;

namespace XEngine.Runtime.Rendering;

[StructLayout(LayoutKind.Sequential, Pack = 16)]
internal struct UnlitMaterialUniformsData
{
#pragma warning disable IDE1006
    public Float2 _Tiling;
    public Float2 _Offset;
    public Float4 _MainColor;
#pragma warning restore IDE1006
}

[StructLayout(LayoutKind.Sequential, Pack = 16)]
internal struct ScreenSpaceDecalUniformsData
{
#pragma warning disable IDE1006
    public int _DecalRenderingLayers;
    public float _Opacity;
    public float _NormalFade;
    public float _Padding;
    public Float4 _MainColor;
#pragma warning restore IDE1006
}

[StructLayout(LayoutKind.Sequential, Pack = 16)]
internal struct ScreenSpaceShadowsUniformsData
{
#pragma warning disable IDE1006
    public int _Steps;
    public float _Thickness;
    public float _RayLength;
    public int _DirectionalLightEnabled;
    public Float4 _DirectionalLightDirection;
#pragma warning restore IDE1006
}

[StructLayout(LayoutKind.Sequential, Pack = 16)]
internal struct BlitMaterialUniformsData
{
#pragma warning disable IDE1006
    public float _FlipY;
#pragma warning restore IDE1006
    public Float3 Padding;
}

[StructLayout(LayoutKind.Sequential, Pack = 16)]
internal struct ParticleMaterialUniformsData
{
#pragma warning disable IDE1006
    public Float4 _MainColor;
    public float _SoftParticlesFactor;
    public float _VelocityScale;
    public float _LengthScale;
#pragma warning restore IDE1006
    public float _Padding0;
}

[StructLayout(LayoutKind.Sequential, Pack = 16)]
internal struct VfxOutputMaterialUniformsData
{
#pragma warning disable IDE1006
    public Float4 _MainColor;
    public float _FlipbookColumns;
    public float _FlipbookRows;
    public float _OutputMode;
    public float _LitMode;
    public Float4 _LightDirection;
    public Float4 _LightColor;
    public Float4 _AmbientColor;
    public float _SoftParticleFade;
    public float _DistortionIntensity;
    public float _CameraFadeNear;
    public float _CameraFadeFar;
    public Float4 _DecalPlane;
    public float _FlipbookBlend;
    public float _MotionVectorMode;
    public float _MotionVectorScale;
    public float _OrientMode;
#pragma warning restore IDE1006
}

[StructLayout(LayoutKind.Sequential, Pack = 16)]
internal struct DefaultUIMaterialUniformsData
{
#pragma warning disable IDE1006
    public Float2 _Tiling;
    public Float2 _Offset;
    public Float4 _MainColor;
    public Float4x4 _ClipToLocal;
    public Float4 _ClipRect;
    public float _ClipRadius;
    public float _ClipSoftness;
    public float _ClipEnable;
#pragma warning restore IDE1006
    public float _ClipPadding;
}

[StructLayout(LayoutKind.Sequential, Pack = 16)]
internal struct StandardMaterialUniformsData
{
#pragma warning disable IDE1006
    public Float2 _Tiling;
    public Float2 _Offset;
    public Float4 _MainColor;
    public float _EmissionIntensity;
    public float _AlphaCutoff;
    public float _Parallax;
    public int _ParallaxSteps;
    public float _TranslucencyStrength;
    public float _ScatteringPower;
    public float _ScatteringDistortion;
    public float _ScatteringScale;
#pragma warning restore IDE1006
}

[StructLayout(LayoutKind.Sequential, Pack = 16)]
internal struct AnisotropicMaterialUniformsData
{
#pragma warning disable IDE1006
    public Float2 _Tiling;
    public Float2 _Offset;
    public Float4 _MainColor;
    public float _EmissionIntensity;
    public float _AlphaCutoff;
    public float _Anisotropy;
#pragma warning restore IDE1006
    public float Padding;
}

[StructLayout(LayoutKind.Sequential, Pack = 16)]
internal struct TerrainMaterialUniformsData
{
#pragma warning disable IDE1006
    public Float4x4 _TerrainWorldToLocal;
    public Float4x4 _TerrainLocalToWorld;
    public Float2 _BrushPosition;
    public float _TerrainSize;
    public float _TerrainHeight;
    public float _BrushRadius;
    public float _BrushFalloff;
    public float _BrushVisible;
    public int _HasHoles;
    public int _LayerCount;
    public Float3 Padding;
    public Float4 _Layer0Params;
    public Float4 _Layer1Params;
    public Float4 _Layer2Params;
    public Float4 _Layer3Params;
    public Float4 _Layer4Params;
    public Float4 _Layer5Params;
    public Float4 _Layer6Params;
    public Float4 _Layer7Params;
#pragma warning restore IDE1006
}

[StructLayout(LayoutKind.Sequential, Pack = 16)]
internal struct StandardLightingUniformsData
{
#pragma warning disable IDE1006
    public int _DirectionalLightEnabled;
    public int _StaticLightRoot;
    public int _DynamicLightRoot;
    public int _DirectionalLightRenderingLayers;
    public int _StaticLightTexSize;
    public int _StaticLightTexShift;
    public int _StaticNodeTexSize;
    public int _StaticNodeTexShift;
    public int _DynamicLightTexSize;
    public int _DynamicLightTexShift;
    public int _DynamicNodeTexSize;
    public int _DynamicNodeTexShift;
    public Float4 _DirectionalLightDirectionIntensity;
    public Float4 _DirectionalLightColor;
    public Float4 _AmbientModeStrength;
    public Float4 _AmbientColor;
    public Float4 _AmbientSkyColor;
    public Float4 _AmbientGroundColor;
    public Float4 _FogColor;
    public Float4 _FogParams;
    public Float4 _FogStates;
    // Clustered light list (P3.2). Appended after the legacy 192-byte block so all existing
    // member offsets stay stable. Must mirror the cbuffer StandardLighting tail in
    // StandardLighting.hlsl exactly.
    public Float4 _ClusterGridParams;    // x=tilesX, y=tilesY, z=zSlices, w=enabled
    public Float4 _ClusterZParams;       // x=sliceNear, y=slicesPerLog2Z, z=texSize, w=texShift
    public Float4 _ClusterCameraForward; // xyz=camera forward, w unused
#pragma warning restore IDE1006
}

// Standard shadow state for the modern HLSL backends. Mirrors the GLSL Lighting.glsl uniform
// surface: the shared shadow atlas size, the single directional cascade set, and the closest-N
// point + spot shadow slots. Kept in its own cbuffer (b4) so the 192-byte StandardLighting ABI
// stays stable and the shadow slice can be exercised independently.
//
// MAX_SHADOW_CASTERS is ShaderConfig.MaxShadowCasters; point lights use
// six face matrices + params per slot, spots use one. The HLSL cbuffer is authored as all
// float4/float4x4 slots so it packs exactly the way this LayoutKind.Explicit struct marshals --
// no scalar packing surprises across 16-byte slot boundaries. The packer writes the shadow
// scalars into the matching channel of the float4 header slots (_ShadowParams0/1) and the HLSL
// readers pull them back out by swizzle.
[StructLayout(LayoutKind.Explicit, Size = 2608)]
internal unsafe struct StandardShadowsUniformsData
{
    public const int MaxShadowCasters = ShaderConfig.MaxShadowCasters;
    public const int PointFaceCount = MaxShadowCasters * 6;

#pragma warning disable IDE1006
    // Header slot 0: xy = _ShadowAtlasSize, z = _ScreenSpaceShadowsEnabled, w = pad.
    [FieldOffset(0)] public Float4 _ShadowAtlasSizePad;
    // Header slot 1: x = _DirectionalLightShadowEnabled (as float), y = bias, z = normalBias, w = strength.
    [FieldOffset(16)] public Float4 _ShadowParams0;
    // Header slot 2: x = quality, y = _CascadeCount (as float), z = atlas projected-Y scale, w = _SoftShadowTaps.
    [FieldOffset(32)] public Float4 _ShadowParams1;

    [FieldOffset(48)]  public Float4x4 _CascadeShadowMatrix0;
    [FieldOffset(112)] public Float4x4 _CascadeShadowMatrix1;
    [FieldOffset(176)] public Float4x4 _CascadeShadowMatrix2;
    [FieldOffset(240)] public Float4x4 _CascadeShadowMatrix3;
    [FieldOffset(304)] public Float4 _CascadeAtlasParams0;
    [FieldOffset(320)] public Float4 _CascadeAtlasParams1;
    [FieldOffset(336)] public Float4 _CascadeAtlasParams2;
    [FieldOffset(352)] public Float4 _CascadeAtlasParams3;

    // Point shadow slots: MaxShadowCasters * 6 faces. Each matrix is 16 floats (64 bytes); each face param is 4 floats (16 bytes).
    [FieldOffset(368)] public fixed float _PointShadowMatrices[PointFaceCount * 16];      // 368 .. 1904
    [FieldOffset(1904)] public fixed float _PointShadowFaceParams[PointFaceCount * 4];    // 1904 .. 2288

    // Spot shadow slots: MaxShadowCasters single-tile matrices + atlas params.
    [FieldOffset(2288)] public fixed float _SpotShadowMatrices[MaxShadowCasters * 16];    // 2288 .. 2544
    [FieldOffset(2544)] public fixed float _SpotShadowAtlasParams[MaxShadowCasters * 4];  // 2544 .. 2608
#pragma warning restore IDE1006
}

[StructLayout(LayoutKind.Explicit, Size = 112)]
internal struct StandardReflectionProbesUniformsData
{
#pragma warning disable IDE1006
    [FieldOffset(0)] public Float4 _ProbeCountWeights;
    [FieldOffset(16)] public Float4 _Probe0PositionIntensity;
    [FieldOffset(32)] public Float4 _Probe0BoxMinProjection;
    [FieldOffset(48)] public Float4 _Probe0BoxMaxMips;
    [FieldOffset(64)] public Float4 _Probe1PositionIntensity;
    [FieldOffset(80)] public Float4 _Probe1BoxMinProjection;
    [FieldOffset(96)] public Float4 _Probe1BoxMaxMips;
#pragma warning restore IDE1006
}

[StructLayout(LayoutKind.Explicit, Size = 80)]
internal struct StandardLightCookiesUniformsData
{
#pragma warning disable IDE1006
    [FieldOffset(0)] public Float4 _LightCookieMainParams;
    [FieldOffset(16)] public Float4x4 _LightCookieMainWorldToLight;
#pragma warning restore IDE1006
}

[StructLayout(LayoutKind.Sequential, Pack = 16)]
internal struct GrassMaterialUniformsData
{
#pragma warning disable IDE1006
    public Float4x4 _TerrainWorldToLocal;
    public Float4x4 _TerrainLocalToWorld;
    public Float3 _TerrainUp;
    public float _TerrainSize;
    public float _TerrainHeight;
    public float _AlphaCutoff;
    public float _WindStrength;
    public float _WindSpeed;
    public float _Billboard;
    public float _AlignToNormal;
    public float _Translucency;
    public float _ScatterPower;
    public float _ScatterDistortion;
    public float _ScatterScale;
    public float _GrassDistance;
    public float _GrassFadeStart;
#pragma warning restore IDE1006
}

[StructLayout(LayoutKind.Sequential, Pack = 16)]
internal struct CutoutMaterialUniformsData
{
#pragma warning disable IDE1006
    public Float2 _Tiling;
    public Float2 _Offset;
    public Float4 _MainColor;
    public float _AlphaCutoff;
#pragma warning restore IDE1006
    public Float3 Padding;
}

[StructLayout(LayoutKind.Sequential, Pack = 16)]
internal struct GradientSkyboxUniformsData
{
#pragma warning disable IDE1006
    public Float4 _TopColor;
    public Float4 _BottomColor;
    public float _Exponent;
#pragma warning restore IDE1006
    public Float3 Padding;
}

[StructLayout(LayoutKind.Sequential, Pack = 16)]
internal struct CubemapSkyboxUniformsData
{
#pragma warning disable IDE1006
    public Float4 _Tint;
    public float _Exposure;
#pragma warning restore IDE1006
    public Float3 Padding;
}

[StructLayout(LayoutKind.Sequential, Pack = 16)]
internal struct GizmoIconUniformsData
{
#pragma warning disable IDE1006
    public Float4 _IconColor;
    public Float3 _IconCenter;
    public float _IconScale;
#pragma warning restore IDE1006
}

[StructLayout(LayoutKind.Sequential, Pack = 16)]
internal struct ProceduralSkyboxUniformsData
{
    public Float2 Resolution;
    public float fogDensity;
    public float Padding0;
#pragma warning disable IDE1006
    public Float3 _SunDir;
#pragma warning restore IDE1006
    public float Padding1;
}

[StructLayout(LayoutKind.Sequential, Pack = 16)]
internal struct TonemapperUniformsData
{
    public float Contrast;
    public float Saturation;
#pragma warning disable IDE1006
    public float _FlipY;
#pragma warning restore IDE1006
    public float Padding;
}

[StructLayout(LayoutKind.Sequential, Pack = 16)]
internal struct FrameDebugPreviewUniformsData
{
#pragma warning disable IDE1006
    public float _ChannelMode;
    public float _LevelsMin;
    public float _LevelsMax;
    public float _FlipY;
#pragma warning restore IDE1006
}

[StructLayout(LayoutKind.Sequential, Pack = 16)]
internal struct UIBlurUniformsData
{
#pragma warning disable IDE1006
    public float _Offset;
#pragma warning restore IDE1006
    public Float3 Padding;
}

[StructLayout(LayoutKind.Sequential, Pack = 16)]
internal struct FXAAUniformsData
{
#pragma warning disable IDE1006
    public Float2 _Resolution;
    public float _EdgeThresholdMin;
    public float _EdgeThresholdMax;
    public float _SubpixelQuality;
#pragma warning restore IDE1006
    public Float3 Padding;
}

[StructLayout(LayoutKind.Sequential, Pack = 16)]
internal struct SMAAUniformsData
{
#pragma warning disable IDE1006
    public Float2 _Resolution;
    public float _EdgeThreshold;
#pragma warning restore IDE1006
    public float Padding;
}

[StructLayout(LayoutKind.Sequential, Pack = 16)]
internal struct FogTemporalUniformsData
{
#pragma warning disable IDE1006
    public Float2 _LowResolution;
    public float _FogHistoryValid;
    public float _FogTemporalBlend;
#pragma warning restore IDE1006
}

[StructLayout(LayoutKind.Sequential, Pack = 16)]
internal unsafe struct FogMarchUniformsData
{
#pragma warning disable IDE1006
    public Float4 _FogMarchParams0;
    public Float4 _FogColorTint;
    public Float4 _FogAmbientColor;
    public Float4 _FogMarchParams1;

    public int _FogMarchFlags0X;
    public int _FogMarchFlags0Y;
    public int _FogMarchFlags0Z;
    public int _FogMarchFlags0W;
    public int _FogMarchFlags1X;
    public int _FogMarchFlags1Y;
    public int _FogMarchFlags1Z;
    public int _FogMarchFlags1W;
    public int _DirectionalFlagsX;
    public int _DirectionalFlagsY;
    public int _DirectionalFlagsZ;
    public int _DirectionalFlagsW;

    public Float4 _DirectionalLightDirectionIntensity;
    public Float4 _DirectionalLightColorShadowStrength;
    public Float4 _DirectionalShadowParams;
    public int _StaticBvhMetaX;
    public int _StaticBvhMetaY;
    public int _StaticBvhMetaZ;
    public int _StaticBvhMetaW;
    public int _DynamicBvhMetaX;
    public int _DynamicBvhMetaY;
    public int _DynamicBvhMetaZ;
    public int _DynamicBvhMetaW;
    public int _BvhRootsX;
    public int _BvhRootsY;
    public int _BvhRootsZ;
    public int _BvhRootsW;
    public Float4 _ShadowAtlasSize;

    public fixed float _CascadeShadowMatrices[4 * 16];
    public fixed float _CascadeAtlasParams[4 * 4];
    public fixed float _PointShadowMatrices[24 * 16];
    public fixed float _PointShadowFaceParams[24 * 4];
    public fixed float _SpotShadowMatrices[4 * 16];
    public fixed float _SpotShadowAtlasParams[4 * 4];
    public fixed int _FogVolumeShapes[16];
    public fixed float _FogVolumeWorldToLocal[16 * 16];
    public fixed float _FogVolumeDensities[16];
    public fixed float _FogVolumeColors[16 * 4];
    public fixed float _FogVolumeFalloffs[16];
    public fixed float _FogVolumeConeAngles[16];
#pragma warning restore IDE1006
}

[StructLayout(LayoutKind.Sequential, Pack = 16)]
internal struct FogCompositeUniformsData
{
#pragma warning disable IDE1006
    public Float2 _Resolution;
    public Float2 _LowResolution;
    public float _FogUpsampleThreshold;
#pragma warning restore IDE1006
    public Float3 Padding;
}

[StructLayout(LayoutKind.Sequential, Pack = 16)]
internal struct RefractionMaterialUniformsData
{
#pragma warning disable IDE1006
    public float _RefractionStrength;
    public float _NoiseScale;
#pragma warning restore IDE1006
    public Float2 Padding0;
#pragma warning disable IDE1006
    public Float4 _Tint;
    public float _BlurRadius;
    public float _BlurMIP;
    public int _BlurSteps;
#pragma warning restore IDE1006
    public float Padding1;
}

[StructLayout(LayoutKind.Sequential, Pack = 16)]
internal struct CinematicEffectsUniformsData
{
#pragma warning disable IDE1006
    public Float2 _Resolution;
    public int _EffectFlags;
#pragma warning restore IDE1006
    public float Padding0;
#pragma warning disable IDE1006
    public float _VignetteIntensity;
    public float _VignetteSmoothness;
    public float _VignetteRoundness;
#pragma warning restore IDE1006
    public float Padding1;
#pragma warning disable IDE1006
    public float _ChromaticIntensity;
    public float _ChromaticDistortion;
    public float _GrainIntensity;
    public float _GrainResponse;
    public float _PostExposure;
    public float _Contrast;
    public float _Saturation;
    public float _Temperature;
    public Float4 _Lift;
    public Float4 _Gamma;
    public Float4 _Gain;
    public float _LUTContribution;
    public float _LUTSize;
    public float _SharpenAmount;
    public float _SharpenRadius;
    public float _EdgeIntensity;
    public float _EdgeBackgroundFade;
    public float _PixelSize;
    public float _GodRayIntensity;
    public Float4 _EdgeColor;
    public float _GodRayDecay;
    public float _GodRayDensity;
    public float _GodRayWeight;
    public int _GodRaySamples;
    public float _GodRayThreshold;
    public Float2 _SunScreenPos;
#pragma warning restore IDE1006
    public float Padding2;
}

[StructLayout(LayoutKind.Sequential, Pack = 16)]
internal struct BloomThresholdUniformsData
{
#pragma warning disable IDE1006
    public float _Threshold;
#pragma warning restore IDE1006
    public Float3 Padding;
}

[StructLayout(LayoutKind.Sequential, Pack = 16)]
internal struct BokehDoFUniformsData
{
#pragma warning disable IDE1006
    public Float2 _Resolution;
    public float _FocusStrength;
    public float _ManualFocusPoint;
    public float _MaxBlurRadius;
#pragma warning restore IDE1006
    public Float3 Padding;
}

[StructLayout(LayoutKind.Sequential, Pack = 16)]
internal struct SSRRayCastUniformsData
{
#pragma warning disable IDE1006
    public Float4 _JitterSizeAndOffset;
    public float _NumSteps;
    public float _BRDFBias;
#pragma warning restore IDE1006
    public Float2 Padding;
}

[StructLayout(LayoutKind.Sequential, Pack = 16)]
internal struct SSRSceneBlurUniformsData
{
#pragma warning disable IDE1006
    public Float2 _BlurDir;
#pragma warning restore IDE1006
    public Float2 Padding;
}

[StructLayout(LayoutKind.Sequential, Pack = 16)]
internal struct SSRResolveUniformsData
{
#pragma warning disable IDE1006
    public Float2 _NoiseSize;
    public Float2 _ResolveSize;
    public Float4 _JitterSizeAndOffset;
    public float _BRDFBias;
    public float _EdgeFactor;
    public float _MaxMipMap;
    public int _RayReuse;
    public int _UseNormalization;
    public int _Fireflies;
#pragma warning restore IDE1006
    public float Padding;
}

[StructLayout(LayoutKind.Sequential, Pack = 16)]
internal struct SSRTemporalUniformsData
{
#pragma warning disable IDE1006
    public int _ReflectionVelocity;
    public float _TScale;
    public float _TResponse;
#pragma warning restore IDE1006
    public float Padding;
}

[StructLayout(LayoutKind.Sequential, Pack = 16)]
internal struct SSRCombineUniformsData
{
#pragma warning disable IDE1006
    public int _UseFresnel;
#pragma warning restore IDE1006
    public Float3 Padding;
}

[StructLayout(LayoutKind.Sequential, Pack = 16)]
internal struct BloomCompositeUniformsData
{
#pragma warning disable IDE1006
    public float _Intensity;
#pragma warning restore IDE1006
    public Float3 Padding;
}

[StructLayout(LayoutKind.Sequential, Pack = 16)]
internal struct MotionBlurUniformsData
{
#pragma warning disable IDE1006
    public Float2 _Resolution;
    public float _Intensity;
    public int _Samples;
    public float _MaxBlurRadius;
#pragma warning restore IDE1006
    public Float3 Padding;
}

[StructLayout(LayoutKind.Sequential, Pack = 16)]
internal struct AutoExposureAdaptUniformsData
{
#pragma warning disable IDE1006
    public float _AdaptSpeedUp;
    public float _AdaptSpeedDown;
    public float _HistoryValid;
#pragma warning restore IDE1006
    public float Padding;
}

[StructLayout(LayoutKind.Sequential, Pack = 16)]
internal struct AutoExposureApplyUniformsData
{
#pragma warning disable IDE1006
    public float _ExposureComp;
    public float _MinExposure;
    public float _MaxExposure;
#pragma warning restore IDE1006
    public float Padding;
}

[StructLayout(LayoutKind.Sequential, Pack = 16)]
internal struct TAAUniformsData
{
#pragma warning disable IDE1006
    public Float2 _Resolution;
    public Float2 _Jitter;
    public float _HistoryValid;
    public float _BlendFactor;
    public float _MotionScale;
    public float _Sharpness;
#pragma warning restore IDE1006
}

[StructLayout(LayoutKind.Sequential, Pack = 16)]
internal struct GTAOCalculateUniformsData
{
#pragma warning disable IDE1006
    public int _Slices;
    public int _DirectionSamples;
    public float _Radius;
    public float _Intensity;
    public Float2 _NoiseScale;
    public Float2 _JitterOffset;
#pragma warning restore IDE1006
}

[StructLayout(LayoutKind.Sequential, Pack = 16)]
internal struct SsgiGatherUniformsData
{
#pragma warning disable IDE1006
    public Float2 _NoiseScale;
    public Float2 _JitterOffset;
    public float _NumSteps;
    public float _Radius;
    public int _GiRays;
#pragma warning restore IDE1006
    public float Padding;
}

[StructLayout(LayoutKind.Sequential, Pack = 16)]
internal struct SsgiTemporalUniformsData
{
#pragma warning disable IDE1006
    public float _TResponse;
#pragma warning restore IDE1006
    public Float3 Padding;
}

[StructLayout(LayoutKind.Sequential, Pack = 16)]
internal struct SsgiBlurUniformsData
{
#pragma warning disable IDE1006
    public Float2 _BlurDirection;
    public float _BlurRadius;
#pragma warning restore IDE1006
    public float Padding;
}

[StructLayout(LayoutKind.Sequential, Pack = 16)]
internal struct SsgiCompositeUniformsData
{
#pragma warning disable IDE1006
    public float _Intensity;
    public int _GiDebugView;
#pragma warning restore IDE1006
    public Float2 Padding;
}

[StructLayout(LayoutKind.Sequential, Pack = 16)]
internal struct SsgiProbeUniformsData
{
#pragma warning disable IDE1006
    public Float4 _GiUpdateRange;
    public Float4 _GiProbeOriginSpacing;
    public Float4 _GiProbeGridParams;
    public Float4 _GiMissColor;
    public float _GiUpdateRate;
#pragma warning restore IDE1006
    public Float3 Padding;
}

[StructLayout(LayoutKind.Sequential, Pack = 16)]
internal struct GTAOBlurUniformsData
{
#pragma warning disable IDE1006
    public Float2 _BlurDirection;
    public float _BlurRadius;
#pragma warning restore IDE1006
    public float Padding;
}

[StructLayout(LayoutKind.Sequential, Pack = 16)]
internal struct GTAOTemporalUniformsData
{
#pragma warning disable IDE1006
    public float _TResponse;
#pragma warning restore IDE1006
    public Float3 Padding;
}

[StructLayout(LayoutKind.Sequential, Pack = 16)]
internal struct GridUniformsData
{
#pragma warning disable IDE1006
    public Float4 _GridColor;
    public float _PrimaryGridSize;
    public float _SecondaryGridSize;
    public float _LineWidth;
    public float _Falloff;
    public float _MaxDist;
#pragma warning restore IDE1006
    public Float3 Padding;
}

[StructLayout(LayoutKind.Sequential, Pack = 16)]
internal struct UIVertexUniformsData
{
    public Float4x4 projection;
}

// Matches the UIPS cbuffer in Paper/UI.shader: dpiScale at c0.x, viewportSize straddling
// c0.y-.z, backdropBlurAmount at c0.w, backdropFlipY at c1.x, and texture0SrgbDecode at c1.y.
// Brush and scissor state moved out of the per-draw cbuffer into the UiBrushes record array.
[StructLayout(LayoutKind.Sequential, Pack = 16)]
internal struct UIFragmentUniformsData
{
    public float dpiScale;
    public Float2 viewportSize;
    public float backdropBlurAmount;
    public int backdropFlipY;
    public float texture0SrgbDecode;
    public Float2 Padding0;
}

internal static class UIUniformPacking
{
    public static bool TrySetFloat(ref UIFragmentUniformsData data, string name, float value)
    {
        if (name == "dpiScale")
            data.dpiScale = value;
        else if (name == "backdropBlurAmount")
            data.backdropBlurAmount = value;
        else if (name == "texture0SrgbDecode")
            data.texture0SrgbDecode = value;
        else
            return false;
        return true;
    }

    public static bool TrySetInt(ref UIFragmentUniformsData data, string name, int value)
    {
        if (name == "backdropFlipY")
            data.backdropFlipY = value;
        else
            return false;
        return true;
    }

    public static bool TrySetVector2(ref UIFragmentUniformsData data, string name, Float2 value)
    {
        if (name == "viewportSize")
            data.viewportSize = value;
        else
            return false;
        return true;
    }

    public static bool TrySetVector4(ref UIFragmentUniformsData data, string name, Float4 value)
    {
        _ = data;
        _ = name;
        _ = value;
        return false;
    }

    public static bool TrySetMatrix(ref UIFragmentUniformsData data, string name, Float4x4 value)
    {
        _ = data;
        _ = name;
        _ = value;
        return false;
    }
}

internal static class MaterialUniformPacking
{
    public static void ApplyTextureBindings(
        Dictionary<string, GraphicsTexture> bindings,
        PropertyState? properties,
        Resources.Shader? shader,
        ShaderBindingSlot[]? textureSlots = null)
    {
        ClearTextureBindings(bindings);

        Resources.Texture2D? mainTexture = GetTextureOverride(properties, "_MainTex");
        Resources.Texture2D? normalTexture = GetTextureOverride(properties, "_NormalTex");
        Resources.Texture2D? surfaceTexture = GetTextureOverride(properties, "_SurfaceTex");
        Resources.Texture2D? emissionTexture = GetTextureOverride(properties, "_EmissionTex");
        Resources.Texture2D? parallaxMapTexture = GetTextureOverride(properties, "_ParallaxMap");
        Resources.Texture2D? translucencyMapTexture = GetTextureOverride(properties, "_TranslucencyMap");
        Resources.Texture2D? lightmapTexture = GetTextureOverride(properties, "_Lightmap");
        Resources.Texture2D? anisoDirectionTexture = GetTextureOverride(properties, "_AnisoDirectionMap");
        Resources.Texture2D? heightmapTexture = GetTextureOverride(properties, "_Heightmap");
        Resources.Texture2D? splatmap0Texture = GetTextureOverride(properties, "_Splatmap0");
        Resources.Texture2D? splatmap1Texture = GetTextureOverride(properties, "_Splatmap1");
        Resources.Texture2D? holesMapTexture = GetTextureOverride(properties, "_HolesMap");
        Resources.Texture2D? layer0Texture = GetTextureOverride(properties, "_Layer0");
        Resources.Texture2D? layer0NormalTexture = GetTextureOverride(properties, "_Layer0Normal");
        Resources.Texture2D? layer1Texture = GetTextureOverride(properties, "_Layer1");
        Resources.Texture2D? layer1NormalTexture = GetTextureOverride(properties, "_Layer1Normal");
        Resources.Texture2D? layer2Texture = GetTextureOverride(properties, "_Layer2");
        Resources.Texture2D? layer2NormalTexture = GetTextureOverride(properties, "_Layer2Normal");
        Resources.Texture2D? layer3Texture = GetTextureOverride(properties, "_Layer3");
        Resources.Texture2D? layer3NormalTexture = GetTextureOverride(properties, "_Layer3Normal");
        Resources.Texture2D? layer4Texture = GetTextureOverride(properties, "_Layer4");
        Resources.Texture2D? layer4NormalTexture = GetTextureOverride(properties, "_Layer4Normal");
        Resources.Texture2D? layer5Texture = GetTextureOverride(properties, "_Layer5");
        Resources.Texture2D? layer5NormalTexture = GetTextureOverride(properties, "_Layer5Normal");
        Resources.Texture2D? layer6Texture = GetTextureOverride(properties, "_Layer6");
        Resources.Texture2D? layer6NormalTexture = GetTextureOverride(properties, "_Layer6Normal");
        Resources.Texture2D? layer7Texture = GetTextureOverride(properties, "_Layer7");
        Resources.Texture2D? layer7NormalTexture = GetTextureOverride(properties, "_Layer7Normal");
        Resources.Texture2D? bloomTexture = GetTextureOverride(properties, "_BloomTex");
        Resources.Texture2D? motionVectorsTexture = GetTextureOverride(properties, "_MotionVectorsTex");
        Resources.Texture2D? adaptedTexture = GetTextureOverride(properties, "_AdaptedTex");
        Resources.Texture2D? historyTexture = GetTextureOverride(properties, "_HistoryTex");
        Resources.Texture2D? fogCurrentTexture = GetTextureOverride(properties, "_FogCurrentTex");
        Resources.Texture2D? fogHistoryTexture = GetTextureOverride(properties, "_FogHistoryTex");
        Resources.Texture2D? fogTexture = GetTextureOverride(properties, "_FogTex");
        Resources.Texture2D? staticLightDataTexture = GetTextureOverrideOrGlobal(properties, "_StaticLightData");
        Resources.Texture2D? staticLightNodesTexture = GetTextureOverrideOrGlobal(properties, "_StaticLightNodes");
        Resources.Texture2D? dynamicLightDataTexture = GetTextureOverrideOrGlobal(properties, "_DynamicLightData");
        Resources.Texture2D? dynamicLightNodesTexture = GetTextureOverrideOrGlobal(properties, "_DynamicLightNodes");
        Resources.Texture2D? shadowAtlasTexture = GetTextureOverrideOrGlobal(properties, "_ShadowAtlas");
        Resources.Texture2D? screenSpaceShadowmap = GetTextureOverrideOrGlobal(properties, "_ScreenSpaceShadowmap");
        Resources.Texture2D? lightCookieMainTexture = GetTextureOverrideOrGlobal(properties, "_LightCookieMain");
        Resources.Texture2D? lightCookieAtlasTexture = GetTextureOverrideOrGlobal(properties, "_LightCookieAtlas");
        Resources.Texture2D? noiseTexture = GetTextureOverride(properties, "_Noise");
        Resources.Texture2D? aoTexture = GetTextureOverride(properties, "_AOTex");
        Resources.Texture2D? previousBuffer = GetTextureOverride(properties, "_PreviousBuffer");
        Resources.Texture2D? cameraMotionVectorsTexture = GetTextureOverride(properties, "_CameraMotionVectorsTexture");
        Resources.Texture2D? cameraDepthTexture = GetTextureOverrideOrGlobal(properties, "_CameraDepthTexture");
        Resources.Texture2D? cameraNormalsTexture = GetTextureOverrideOrGlobal(properties, "_CameraNormalsTexture");
        Resources.Texture2D? horizRTexture = GetTextureOverride(properties, "_HorizR");
        Resources.Texture2D? blurredTexture = GetTextureOverride(properties, "_BlurredTex");
        Resources.Texture2D? rayCastTexture = GetTextureOverride(properties, "_RayCast");
        Resources.Texture2D? scene0Texture = GetTextureOverride(properties, "_Scene0");
        Resources.Texture2D? scene1Texture = GetTextureOverride(properties, "_Scene1");
        Resources.Texture2D? scene2Texture = GetTextureOverride(properties, "_Scene2");
        Resources.Texture2D? scene3Texture = GetTextureOverride(properties, "_Scene3");
        Resources.Texture2D? scene4Texture = GetTextureOverride(properties, "_Scene4");
        Resources.Texture2D? reflectionBuffer = GetTextureOverride(properties, "_ReflectionBuffer");
        Resources.Texture2D? areaTexture = GetTextureOverride(properties, "_AreaTex");
        Resources.Texture2D? searchTexture = GetTextureOverride(properties, "_SearchTex");
        Resources.Texture2D? blendTexture = GetTextureOverride(properties, "_BlendTex");
        Resources.Texture2D? lutTexture = GetTextureOverride(properties, "_LUTTex");
        Resources.Texture2D? cubeRight = GetTextureOverride(properties, "_CubeRight");
        Resources.Texture2D? cubeLeft = GetTextureOverride(properties, "_CubeLeft");
        Resources.Texture2D? cubeTop = GetTextureOverride(properties, "_CubeTop");
        Resources.Texture2D? cubeBottom = GetTextureOverride(properties, "_CubeBottom");
        Resources.Texture2D? cubeFront = GetTextureOverride(properties, "_CubeFront");
        Resources.Texture2D? cubeBack = GetTextureOverride(properties, "_CubeBack");
        Rendering.Shaders.ShaderProperty[]? defaults = shader != null ? shader.PropertyArray : null;
        if (defaults != null)
        {
            for (int i = 0; i < defaults.Length; i++)
            {
                Rendering.Shaders.ShaderProperty property = defaults[i];
                if (property.Name == "_MainTex" && mainTexture == null)
                    mainTexture = property.Texture2DValue;
                else if (property.Name == "_NormalTex" && normalTexture == null)
                    normalTexture = property.Texture2DValue;
                else if (property.Name == "_SurfaceTex" && surfaceTexture == null)
                    surfaceTexture = property.Texture2DValue;
                else if (property.Name == "_EmissionTex" && emissionTexture == null)
                    emissionTexture = property.Texture2DValue;
                else if (property.Name == "_ParallaxMap" && parallaxMapTexture == null)
                    parallaxMapTexture = property.Texture2DValue;
                else if (property.Name == "_TranslucencyMap" && translucencyMapTexture == null)
                    translucencyMapTexture = property.Texture2DValue;
                else if (property.Name == "_Lightmap" && lightmapTexture == null)
                    lightmapTexture = property.Texture2DValue;
                else if (property.Name == "_AnisoDirectionMap" && anisoDirectionTexture == null)
                    anisoDirectionTexture = property.Texture2DValue;
                else if (property.Name == "_Heightmap" && heightmapTexture == null) heightmapTexture = property.Texture2DValue;
                else if (property.Name == "_Splatmap0" && splatmap0Texture == null) splatmap0Texture = property.Texture2DValue;
                else if (property.Name == "_Splatmap1" && splatmap1Texture == null) splatmap1Texture = property.Texture2DValue;
                else if (property.Name == "_HolesMap" && holesMapTexture == null) holesMapTexture = property.Texture2DValue;
                else if (property.Name == "_Layer0" && layer0Texture == null) layer0Texture = property.Texture2DValue;
                else if (property.Name == "_Layer0Normal" && layer0NormalTexture == null) layer0NormalTexture = property.Texture2DValue;
                else if (property.Name == "_Layer1" && layer1Texture == null) layer1Texture = property.Texture2DValue;
                else if (property.Name == "_Layer1Normal" && layer1NormalTexture == null) layer1NormalTexture = property.Texture2DValue;
                else if (property.Name == "_Layer2" && layer2Texture == null) layer2Texture = property.Texture2DValue;
                else if (property.Name == "_Layer2Normal" && layer2NormalTexture == null) layer2NormalTexture = property.Texture2DValue;
                else if (property.Name == "_Layer3" && layer3Texture == null) layer3Texture = property.Texture2DValue;
                else if (property.Name == "_Layer3Normal" && layer3NormalTexture == null) layer3NormalTexture = property.Texture2DValue;
                else if (property.Name == "_Layer4" && layer4Texture == null) layer4Texture = property.Texture2DValue;
                else if (property.Name == "_Layer4Normal" && layer4NormalTexture == null) layer4NormalTexture = property.Texture2DValue;
                else if (property.Name == "_Layer5" && layer5Texture == null) layer5Texture = property.Texture2DValue;
                else if (property.Name == "_Layer5Normal" && layer5NormalTexture == null) layer5NormalTexture = property.Texture2DValue;
                else if (property.Name == "_Layer6" && layer6Texture == null) layer6Texture = property.Texture2DValue;
                else if (property.Name == "_Layer6Normal" && layer6NormalTexture == null) layer6NormalTexture = property.Texture2DValue;
                else if (property.Name == "_Layer7" && layer7Texture == null) layer7Texture = property.Texture2DValue;
                else if (property.Name == "_Layer7Normal" && layer7NormalTexture == null) layer7NormalTexture = property.Texture2DValue;
                else if (property.Name == "_BloomTex" && bloomTexture == null)
                    bloomTexture = property.Texture2DValue;
                else if (property.Name == "_MotionVectorsTex" && motionVectorsTexture == null)
                    motionVectorsTexture = property.Texture2DValue;
                else if (property.Name == "_AdaptedTex" && adaptedTexture == null)
                    adaptedTexture = property.Texture2DValue;
                else if (property.Name == "_HistoryTex" && historyTexture == null)
                    historyTexture = property.Texture2DValue;
                else if (property.Name == "_FogCurrentTex" && fogCurrentTexture == null)
                    fogCurrentTexture = property.Texture2DValue;
                else if (property.Name == "_FogHistoryTex" && fogHistoryTexture == null)
                    fogHistoryTexture = property.Texture2DValue;
                else if (property.Name == "_FogTex" && fogTexture == null)
                    fogTexture = property.Texture2DValue;
                else if (property.Name == "_Noise" && noiseTexture == null)
                    noiseTexture = property.Texture2DValue;
                else if (property.Name == "_AOTex" && aoTexture == null)
                    aoTexture = property.Texture2DValue;
                else if (property.Name == "_PreviousBuffer" && previousBuffer == null)
                    previousBuffer = property.Texture2DValue;
                else if (property.Name == "_CameraMotionVectorsTexture" && cameraMotionVectorsTexture == null)
                    cameraMotionVectorsTexture = property.Texture2DValue;
                else if (property.Name == "_CameraDepthTexture" && cameraDepthTexture == null)
                    cameraDepthTexture = property.Texture2DValue;
                else if (property.Name == "_CameraNormalsTexture" && cameraNormalsTexture == null)
                    cameraNormalsTexture = property.Texture2DValue;
                else if (property.Name == "_HorizR" && horizRTexture == null)
                    horizRTexture = property.Texture2DValue;
                else if (property.Name == "_BlurredTex" && blurredTexture == null)
                    blurredTexture = property.Texture2DValue;
                else if (property.Name == "_RayCast" && rayCastTexture == null)
                    rayCastTexture = property.Texture2DValue;
                else if (property.Name == "_Scene0" && scene0Texture == null)
                    scene0Texture = property.Texture2DValue;
                else if (property.Name == "_Scene1" && scene1Texture == null)
                    scene1Texture = property.Texture2DValue;
                else if (property.Name == "_Scene2" && scene2Texture == null)
                    scene2Texture = property.Texture2DValue;
                else if (property.Name == "_Scene3" && scene3Texture == null)
                    scene3Texture = property.Texture2DValue;
                else if (property.Name == "_Scene4" && scene4Texture == null)
                    scene4Texture = property.Texture2DValue;
                else if (property.Name == "_ReflectionBuffer" && reflectionBuffer == null)
                    reflectionBuffer = property.Texture2DValue;
                else if (property.Name == "_AreaTex" && areaTexture == null)
                    areaTexture = property.Texture2DValue;
                else if (property.Name == "_SearchTex" && searchTexture == null)
                    searchTexture = property.Texture2DValue;
                else if (property.Name == "_BlendTex" && blendTexture == null)
                    blendTexture = property.Texture2DValue;
                else if (property.Name == "_LUTTex" && lutTexture == null)
                    lutTexture = property.Texture2DValue;
                else if (property.Name == "_CubeRight" && cubeRight == null)
                    cubeRight = property.Texture2DValue;
                else if (property.Name == "_CubeLeft" && cubeLeft == null)
                    cubeLeft = property.Texture2DValue;
                else if (property.Name == "_CubeTop" && cubeTop == null)
                    cubeTop = property.Texture2DValue;
                else if (property.Name == "_CubeBottom" && cubeBottom == null)
                    cubeBottom = property.Texture2DValue;
                else if (property.Name == "_CubeFront" && cubeFront == null)
                    cubeFront = property.Texture2DValue;
                else if (property.Name == "_CubeBack" && cubeBack == null)
                    cubeBack = property.Texture2DValue;
            }
        }

        AddTextureBinding(bindings, "_MainTex", mainTexture);
        AddTextureBinding(bindings, "_NormalTex", normalTexture);
        AddTextureBinding(bindings, "_SurfaceTex", surfaceTexture);
        AddTextureBinding(bindings, "_EmissionTex", emissionTexture);
        AddTextureBinding(bindings, "_ParallaxMap", parallaxMapTexture);
        AddTextureBinding(bindings, "_TranslucencyMap", translucencyMapTexture);
        AddTextureBinding(bindings, "_Lightmap", lightmapTexture);
        AddTextureBinding(bindings, "_AnisoDirectionMap", anisoDirectionTexture);
        AddTextureBinding(bindings, "_Heightmap", heightmapTexture);
        AddTextureBinding(bindings, "_Splatmap0", splatmap0Texture);
        AddTextureBinding(bindings, "_Splatmap1", splatmap1Texture);
        AddTextureBinding(bindings, "_HolesMap", holesMapTexture);
        AddTextureBinding(bindings, "_Layer0", layer0Texture);
        AddTextureBinding(bindings, "_Layer0Normal", layer0NormalTexture);
        AddTextureBinding(bindings, "_Layer1", layer1Texture);
        AddTextureBinding(bindings, "_Layer1Normal", layer1NormalTexture);
        AddTextureBinding(bindings, "_Layer2", layer2Texture);
        AddTextureBinding(bindings, "_Layer2Normal", layer2NormalTexture);
        AddTextureBinding(bindings, "_Layer3", layer3Texture);
        AddTextureBinding(bindings, "_Layer3Normal", layer3NormalTexture);
        AddTextureBinding(bindings, "_Layer4", layer4Texture);
        AddTextureBinding(bindings, "_Layer4Normal", layer4NormalTexture);
        AddTextureBinding(bindings, "_Layer5", layer5Texture);
        AddTextureBinding(bindings, "_Layer5Normal", layer5NormalTexture);
        AddTextureBinding(bindings, "_Layer6", layer6Texture);
        AddTextureBinding(bindings, "_Layer6Normal", layer6NormalTexture);
        AddTextureBinding(bindings, "_Layer7", layer7Texture);
        AddTextureBinding(bindings, "_Layer7Normal", layer7NormalTexture);
        AddTextureBinding(bindings, "_BloomTex", bloomTexture);
        AddTextureBinding(bindings, "_MotionVectorsTex", motionVectorsTexture);
        AddTextureBinding(bindings, "_AdaptedTex", adaptedTexture);
        AddTextureBinding(bindings, "_HistoryTex", historyTexture);
        AddTextureBinding(bindings, "_FogCurrentTex", fogCurrentTexture);
        AddTextureBinding(bindings, "_FogHistoryTex", fogHistoryTexture);
        AddTextureBinding(bindings, "_FogTex", fogTexture);
        AddTextureBinding(bindings, "_StaticLightData", staticLightDataTexture);
        AddTextureBinding(bindings, "_StaticLightNodes", staticLightNodesTexture);
        AddTextureBinding(bindings, "_DynamicLightData", dynamicLightDataTexture);
        AddTextureBinding(bindings, "_DynamicLightNodes", dynamicLightNodesTexture);
        AddTextureBinding(bindings, "_ShadowAtlas", shadowAtlasTexture);
        AddTextureBinding(bindings, "_ScreenSpaceShadowmap", screenSpaceShadowmap);
        AddTextureBinding(bindings, "_LightCookieMain", lightCookieMainTexture);
        AddTextureBinding(bindings, "_LightCookieAtlas", lightCookieAtlasTexture);
        AddTextureBinding(bindings, "_Noise", noiseTexture);
        AddTextureBinding(bindings, "_AOTex", aoTexture);
        AddTextureBinding(bindings, "_PreviousBuffer", previousBuffer);
        AddTextureBinding(bindings, "_CameraMotionVectorsTexture", cameraMotionVectorsTexture);
        AddTextureBinding(bindings, "_CameraDepthTexture", cameraDepthTexture);
        AddTextureBinding(bindings, "_CameraNormalsTexture", cameraNormalsTexture);
        AddTextureBinding(bindings, "_HorizR", horizRTexture);
        AddTextureBinding(bindings, "_BlurredTex", blurredTexture);
        AddTextureBinding(bindings, "_RayCast", rayCastTexture);
        AddTextureBinding(bindings, "_Scene0", scene0Texture);
        AddTextureBinding(bindings, "_Scene1", scene1Texture);
        AddTextureBinding(bindings, "_Scene2", scene2Texture);
        AddTextureBinding(bindings, "_Scene3", scene3Texture);
        AddTextureBinding(bindings, "_Scene4", scene4Texture);
        AddTextureBinding(bindings, "_ReflectionBuffer", reflectionBuffer);
        AddTextureBinding(bindings, "_AreaTex", areaTexture);
        AddTextureBinding(bindings, "_SearchTex", searchTexture);
        AddTextureBinding(bindings, "_BlendTex", blendTexture);
        AddTextureBinding(bindings, "_LUTTex", lutTexture);
        AddTextureBinding(bindings, "_CubeRight", cubeRight);
        AddTextureBinding(bindings, "_CubeLeft", cubeLeft);
        AddTextureBinding(bindings, "_CubeTop", cubeTop);
        AddTextureBinding(bindings, "_CubeBottom", cubeBottom);
        AddTextureBinding(bindings, "_CubeFront", cubeFront);
        AddTextureBinding(bindings, "_CubeBack", cubeBack);

        // Generic fallback (N2): bind every sampler slot the shader *declares* that the
        // whitelist above did not already bind. Resolution order — material PropertyState
        // override first, then the shader Properties block default (ParseDefault already
        // resolves white/gray/grid/black/normal/surface/noise to built-in textures); else
        // leave unbound. This makes game-owned shaders work without touching the engine.
        if (textureSlots == null)
            return;

        for (int i = 0; i < textureSlots.Length; i++)
        {
            ShaderBindingSlot slot = textureSlots[i];
            if (slot.Kind != ShaderBindingKind.Texture)
                continue;

            string name = slot.Name;
            if (bindings.ContainsKey(name))
                continue;

            Resources.Texture2D? texture = GetTextureOverride(properties, name);
            if (texture == null && shader != null)
                texture = GetShaderDefaultTexture(shader, name);
            if (texture != null)
            {
                AddTextureBinding(bindings, name, texture);
                continue;
            }

            Resources.Texture2DArray? array = properties?.GetTexture2DArray(name);
            if (array == null && shader != null)
                array = GetShaderDefaultTexture2DArray(shader, name);
            if (array != null && !array.IsDisposed)
            {
                bindings[name] = array.Handle;
                continue;
            }

            Resources.Texture3D? volume = properties?.GetTexture3D(name);
            if (volume == null && shader != null)
                volume = GetShaderDefaultTexture3D(shader, name);
            if (volume != null && !volume.IsDisposed)
            {
                bindings[name] = volume.Handle;
                continue;
            }

            // Deliberately NO eager fallback here: leaving the slot unbound lets the backend's
            // draw-time TryGetBoundTexture resolve PropertyState.s_globalTextures (SetGlobalTexture
            // pass inputs like _CameraDepthTexture / _ShadowAtlas / light BVH textures). Eagerly
            // binding white shadowed those lookups (dict hit wins over the global fallback) and the
            // entry was never cleared for non-whitelist names — bloom/SSAO/soft particles read pure
            // white for the rest of the session and the whole editor rendered washed out. The
            // backend throws on a slot that resolves to nothing at draw time, which keeps genuinely
            // broken materials loud instead of silently white.
        }
    }

    /// <summary>Resolve a shader Properties-block texture default by name, or null when absent.</summary>
    private static Resources.Texture2D? GetShaderDefaultTexture(Resources.Shader shader, string name)
    {
        Rendering.Shaders.ShaderProperty[] defaults = shader.PropertyArray;
        for (int i = 0; i < defaults.Length; i++)
        {
            Rendering.Shaders.ShaderProperty property = defaults[i];
            if (property.Name == name && property.PropertyType == Rendering.Shaders.ShaderPropertyType.Texture2D)
                return property.Texture2DValue;
        }
        return null;
    }

    private static Resources.Texture2DArray? GetShaderDefaultTexture2DArray(Resources.Shader shader, string name)
    {
        Rendering.Shaders.ShaderProperty[] defaults = shader.PropertyArray;
        for (int i = 0; i < defaults.Length; i++)
        {
            Rendering.Shaders.ShaderProperty property = defaults[i];
            if (property.Name == name && property.PropertyType == Rendering.Shaders.ShaderPropertyType.Texture2DArray)
                return property.Texture2DArrayValue;
        }
        return null;
    }

    private static Resources.Texture3D? GetShaderDefaultTexture3D(Resources.Shader shader, string name)
    {
        Rendering.Shaders.ShaderProperty[] defaults = shader.PropertyArray;
        for (int i = 0; i < defaults.Length; i++)
        {
            Rendering.Shaders.ShaderProperty property = defaults[i];
            if (property.Name == name && property.PropertyType == Rendering.Shaders.ShaderPropertyType.Texture3D)
                return property.Texture3DValue;
        }
        return null;
    }

    public static void ClearTextureBindings(Dictionary<string, GraphicsTexture> bindings)
    {
        // Clear EVERY entry, not just the engine-known whitelist names. Generic (N2) slots are
        // resolved by shader-declared names (boneMatrixTexture, morph textures, game-owned
        // material textures...); leaving them in the dictionary made ApplyTextureBindings' N2
        // loop "already bound → skip" and permanently pinned the FIRST resolved texture to the
        // slot — every skinned renderer in the scene then sampled one renderer's bone texture.
        // Callers re-resolve the full slot list right after this call; engine-global textures
        // survive a dict miss via PropertyState.s_globalTextures at draw time.
        bindings.Clear();
    }

    public static UnlitMaterialUniformsData PackUnlit(PropertyState? properties, Resources.Shader? shader)
    {
        UnlitMaterialUniformsData data = default;
        Rendering.Shaders.ShaderProperty[]? defaults = shader != null ? shader.PropertyArray : null;
        if (defaults != null)
        {
            for (int i = 0; i < defaults.Length; i++)
                ApplyCommonDefault(defaults[i], ref data._Tiling, ref data._Offset, ref data._MainColor);
        }

        ApplyCommonOverrides(properties, ref data._Tiling, ref data._Offset, ref data._MainColor);
        return data;
    }

    public static ScreenSpaceDecalUniformsData PackScreenSpaceDecal(PropertyState? properties, Resources.Shader? shader)
    {
        ScreenSpaceDecalUniformsData data = default;
        data._DecalRenderingLayers = unchecked((int)RenderingLayerMask.Everything.Mask);
        data._Opacity = 1f;
        data._MainColor = new Float4(1f, 1f, 1f, 1f);
        Rendering.Shaders.ShaderProperty[]? defaults = shader != null ? shader.PropertyArray : null;
        if (defaults != null)
        {
            for (int i = 0; i < defaults.Length; i++)
            {
                Rendering.Shaders.ShaderProperty property = defaults[i];
                if (property.Name == "_MainColor")
                    data._MainColor = property.Value;
                else if (property.Name == "_Opacity")
                    data._Opacity = property.Value.X;
                else if (property.Name == "_DecalRenderingLayers")
                    data._DecalRenderingLayers = (int)property.Value.X;
            }
        }

        if (properties != null)
        {
            if (properties._colors.TryGetValue("_MainColor", out Color color))
                data._MainColor = new Float4(color.R, color.G, color.B, color.A);
            else if (properties._vectors4.TryGetValue("_MainColor", out Float4 vectorColor))
                data._MainColor = vectorColor;
            if (properties._floats.TryGetValue("_Opacity", out float opacity))
                data._Opacity = opacity;
            if (properties._ints.TryGetValue("_DecalRenderingLayers", out int layers))
                data._DecalRenderingLayers = layers;
        }

        return data;
    }

    public static ScreenSpaceShadowsUniformsData PackScreenSpaceShadows(PropertyState? properties, Resources.Shader? shader)
    {
        ScreenSpaceShadowsUniformsData data = default;
        data._Steps = 8;
        data._Thickness = 0.03f;
        data._RayLength = 0.25f;
        data._DirectionalLightEnabled = GetGlobalInt("_DirectionalLightEnabled");
        Float3 direction = GetGlobalVector3("_DirectionalLightDirection");
        data._DirectionalLightDirection = new Float4(direction.X, direction.Y, direction.Z, 0f);

        Rendering.Shaders.ShaderProperty[]? defaults = shader != null ? shader.PropertyArray : null;
        if (defaults != null)
        {
            for (int i = 0; i < defaults.Length; i++)
            {
                Rendering.Shaders.ShaderProperty property = defaults[i];
                if (property.Name == "_Steps")
                    data._Steps = (int)property.Value.X;
                else if (property.Name == "_Thickness")
                    data._Thickness = property.Value.X;
                else if (property.Name == "_RayLength")
                    data._RayLength = property.Value.X;
            }
        }

        if (properties != null)
        {
            if (properties._ints.TryGetValue("_Steps", out int steps))
                data._Steps = steps;
            if (properties._floats.TryGetValue("_Thickness", out float thickness))
                data._Thickness = thickness;
            if (properties._floats.TryGetValue("_RayLength", out float rayLength))
                data._RayLength = rayLength;
        }

        return data;
    }

    public static BlitMaterialUniformsData PackBlit(PropertyState? properties, Resources.Shader? shader)
    {
        BlitMaterialUniformsData data = default;
        Rendering.Shaders.ShaderProperty[]? defaults = shader != null ? shader.PropertyArray : null;
        if (defaults != null)
        {
            for (int i = 0; i < defaults.Length; i++)
            {
                if (defaults[i].Name == "_FlipY")
                    data._FlipY = defaults[i].Value.X;
            }
        }

        if (properties != null && properties._floats.TryGetValue("_FlipY", out float flipY))
            data._FlipY = flipY;
        return data;
    }

    public static ParticleMaterialUniformsData PackParticle(PropertyState? properties, Resources.Shader? shader)
    {
        ParticleMaterialUniformsData data = default;
        data._SoftParticlesFactor = 1f;
        data._VelocityScale = 1f;
        data._LengthScale = 1f;
        Rendering.Shaders.ShaderProperty[]? defaults = shader != null ? shader.PropertyArray : null;
        if (defaults != null)
        {
            for (int i = 0; i < defaults.Length; i++)
            {
                Rendering.Shaders.ShaderProperty property = defaults[i];
                if (property.Name == "_MainColor")
                    data._MainColor = property.Value;
                else if (property.Name == "_SoftParticlesFactor")
                    data._SoftParticlesFactor = property.Value.X;
                else if (property.Name == "_VelocityScale")
                    data._VelocityScale = property.Value.X;
                else if (property.Name == "_LengthScale")
                    data._LengthScale = property.Value.X;
            }
        }

        if (properties != null)
        {
            ApplyColorOverride(properties, "_MainColor", ref data._MainColor);
            if (properties._floats.TryGetValue("_SoftParticlesFactor", out float softParticles))
                data._SoftParticlesFactor = softParticles;
            if (properties._floats.TryGetValue("_VelocityScale", out float velocityScale))
                data._VelocityScale = velocityScale;
            if (properties._floats.TryGetValue("_LengthScale", out float lengthScale))
                data._LengthScale = lengthScale;
        }
        return data;
    }

    public static VfxOutputMaterialUniformsData PackVfxOutput(PropertyState? properties, Resources.Shader? shader)
    {
        VfxOutputMaterialUniformsData data = default;
        data._MainColor = new Float4(1f, 1f, 1f, 1f);
        data._FlipbookColumns = 1f;
        data._FlipbookRows = 1f;
        data._LightDirection = new Float4(0.35f, 0.85f, 0.4f, 0f);
        data._LightColor = new Float4(1f, 1f, 1f, 1f);
        data._AmbientColor = new Float4(0.18f, 0.18f, 0.22f, 32f);
        data._SoftParticleFade = 0f;
        data._DistortionIntensity = 0f;
        data._CameraFadeNear = 0f;
        data._CameraFadeFar = 0f;
        data._DecalPlane = new Float4(0f, 1f, 0f, 0f);
        data._FlipbookBlend = 0f;
        data._MotionVectorMode = 0f;
        data._MotionVectorScale = 1f;
        data._OrientMode = 0f;
        Rendering.Shaders.ShaderProperty[]? defaults = shader != null ? shader.PropertyArray : null;
        if (defaults != null)
        {
            for (int i = 0; i < defaults.Length; i++)
            {
                Rendering.Shaders.ShaderProperty property = defaults[i];
                if (property.Name == "_MainColor")
                    data._MainColor = property.Value;
                else if (property.Name == "_FlipbookColumns")
                    data._FlipbookColumns = property.Value.X;
                else if (property.Name == "_FlipbookRows")
                    data._FlipbookRows = property.Value.X;
                else if (property.Name == "_OutputMode")
                    data._OutputMode = property.Value.X;
                else if (property.Name == "_LitMode")
                    data._LitMode = property.Value.X;
                else if (property.Name == "_LightDirection")
                    data._LightDirection = property.Value;
                else if (property.Name == "_LightColor")
                    data._LightColor = property.Value;
                else if (property.Name == "_AmbientColor")
                    data._AmbientColor = property.Value;
                else if (property.Name == "_SoftParticleFade")
                    data._SoftParticleFade = property.Value.X;
                else if (property.Name == "_DistortionIntensity")
                    data._DistortionIntensity = property.Value.X;
                else if (property.Name == "_CameraFadeNear")
                    data._CameraFadeNear = property.Value.X;
                else if (property.Name == "_CameraFadeFar")
                    data._CameraFadeFar = property.Value.X;
                else if (property.Name == "_DecalPlane")
                    data._DecalPlane = property.Value;
                else if (property.Name == "_FlipbookBlend")
                    data._FlipbookBlend = property.Value.X;
                else if (property.Name == "_MotionVectorMode")
                    data._MotionVectorMode = property.Value.X;
                else if (property.Name == "_MotionVectorScale")
                    data._MotionVectorScale = property.Value.X;
                else if (property.Name == "_OrientMode")
                    data._OrientMode = property.Value.X;
            }
        }

        if (properties != null)
        {
            ApplyColorOverride(properties, "_MainColor", ref data._MainColor);
            ApplyColorOverride(properties, "_LightColor", ref data._LightColor);
            ApplyColorOverride(properties, "_AmbientColor", ref data._AmbientColor);
            if (properties._floats.TryGetValue("_FlipbookColumns", out float columns))
                data._FlipbookColumns = columns;
            if (properties._floats.TryGetValue("_FlipbookRows", out float rows))
                data._FlipbookRows = rows;
            if (properties._floats.TryGetValue("_OutputMode", out float outputMode))
                data._OutputMode = outputMode;
            if (properties._floats.TryGetValue("_LitMode", out float litMode))
                data._LitMode = litMode;
            if (properties._vectors4.TryGetValue("_LightDirection", out Float4 lightDir4))
                data._LightDirection = lightDir4;
            else if (properties._vectors3.TryGetValue("_LightDirection", out Float3 lightDir3))
                data._LightDirection = new Float4(lightDir3.X, lightDir3.Y, lightDir3.Z, 0f);
            if (properties._floats.TryGetValue("_SoftParticleFade", out float softFade))
                data._SoftParticleFade = softFade;
            if (properties._floats.TryGetValue("_DistortionIntensity", out float distort))
                data._DistortionIntensity = distort;
            if (properties._floats.TryGetValue("_CameraFadeNear", out float fadeNear))
                data._CameraFadeNear = fadeNear;
            if (properties._floats.TryGetValue("_CameraFadeFar", out float fadeFar))
                data._CameraFadeFar = fadeFar;
            if (properties._floats.TryGetValue("_FlipbookBlend", out float flipbookBlend))
                data._FlipbookBlend = flipbookBlend;
            if (properties._floats.TryGetValue("_MotionVectorMode", out float motionMode))
                data._MotionVectorMode = motionMode;
            if (properties._floats.TryGetValue("_MotionVectorScale", out float motionScale))
                data._MotionVectorScale = motionScale;
            if (properties._floats.TryGetValue("_OrientMode", out float orientMode))
                data._OrientMode = orientMode;
            if (properties._vectors4.TryGetValue("_DecalPlane", out Float4 decalPlane4))
                data._DecalPlane = decalPlane4;
            else if (properties._vectors3.TryGetValue("_DecalPlane", out Float3 decalPlane3))
                data._DecalPlane = new Float4(decalPlane3.X, decalPlane3.Y, decalPlane3.Z, 0f);
        }
        return data;
    }

    public static DefaultUIMaterialUniformsData PackDefaultUI(PropertyState? properties, Resources.Shader? shader)
    {
        DefaultUIMaterialUniformsData data = default;
        Rendering.Shaders.ShaderProperty[]? defaults = shader != null ? shader.PropertyArray : null;
        if (defaults != null)
        {
            for (int i = 0; i < defaults.Length; i++)
                ApplyCommonDefault(defaults[i], ref data._Tiling, ref data._Offset, ref data._MainColor);
        }

        ApplyCommonOverrides(properties, ref data._Tiling, ref data._Offset, ref data._MainColor);
        if (properties != null)
        {
            if (properties._matrices.TryGetValue("_ClipToLocal", out Float4x4 clipToLocal))
                data._ClipToLocal = clipToLocal;
            if (properties._vectors4.TryGetValue("_ClipRect", out Float4 clipRect))
                data._ClipRect = clipRect;
            if (properties._floats.TryGetValue("_ClipRadius", out float clipRadius))
                data._ClipRadius = clipRadius;
            if (properties._floats.TryGetValue("_ClipSoftness", out float clipSoftness))
                data._ClipSoftness = clipSoftness;
            if (properties._floats.TryGetValue("_ClipEnable", out float clipEnable))
                data._ClipEnable = clipEnable;
        }
        return data;
    }

    public static StandardMaterialUniformsData PackStandard(PropertyState? properties, Resources.Shader? shader)
    {
        StandardMaterialUniformsData data = default;
        Rendering.Shaders.ShaderProperty[]? defaults = shader != null ? shader.PropertyArray : null;
        if (defaults != null)
        {
            for (int i = 0; i < defaults.Length; i++)
            {
                Rendering.Shaders.ShaderProperty property = defaults[i];
                ApplyCommonDefault(property, ref data._Tiling, ref data._Offset, ref data._MainColor);
                if (property.Name == "_EmissionIntensity")
                    data._EmissionIntensity = property.Value.X;
                else if (property.Name == "_AlphaCutoff")
                    data._AlphaCutoff = property.Value.X;
                else if (property.Name == "_Parallax")
                    data._Parallax = property.Value.X;
                else if (property.Name == "_ParallaxSteps")
                    data._ParallaxSteps = (int)property.Value.X;
                else if (property.Name == "_TranslucencyStrength")
                    data._TranslucencyStrength = property.Value.X;
                else if (property.Name == "_ScatteringPower")
                    data._ScatteringPower = property.Value.X;
                else if (property.Name == "_ScatteringDistortion")
                    data._ScatteringDistortion = property.Value.X;
                else if (property.Name == "_ScatteringScale")
                    data._ScatteringScale = property.Value.X;
            }
        }

        ApplyCommonOverrides(properties, ref data._Tiling, ref data._Offset, ref data._MainColor);
        if (properties != null)
        {
            if (properties._floats.TryGetValue("_EmissionIntensity", out float emissionIntensity))
                data._EmissionIntensity = emissionIntensity;
            if (properties._floats.TryGetValue("_AlphaCutoff", out float alphaCutoff))
                data._AlphaCutoff = alphaCutoff;
            if (properties._floats.TryGetValue("_Parallax", out float parallax))
                data._Parallax = parallax;
            if (properties._ints.TryGetValue("_ParallaxSteps", out int parallaxSteps))
                data._ParallaxSteps = parallaxSteps;
            if (properties._floats.TryGetValue("_TranslucencyStrength", out float translucencyStrength))
                data._TranslucencyStrength = translucencyStrength;
            if (properties._floats.TryGetValue("_ScatteringPower", out float scatteringPower))
                data._ScatteringPower = scatteringPower;
            if (properties._floats.TryGetValue("_ScatteringDistortion", out float scatteringDistortion))
                data._ScatteringDistortion = scatteringDistortion;
            if (properties._floats.TryGetValue("_ScatteringScale", out float scatteringScale))
                data._ScatteringScale = scatteringScale;
        }
        return data;
    }

    public static StandardLightingUniformsData PackStandardLighting()
    {
        StandardLightingUniformsData data = default;
        Float3 directionalDirection = GetGlobalVector3("_DirectionalLightDirection");
        Float3 directionalColor = GetGlobalVector3("_DirectionalLightColor");
        data._DirectionalLightEnabled = GetGlobalInt("_DirectionalLightEnabled");
        data._StaticLightRoot = GetGlobalInt("_StaticLightRoot", -1);
        data._DynamicLightRoot = GetGlobalInt("_DynamicLightRoot", -1);
        data._DirectionalLightRenderingLayers = GetGlobalInt("_DirectionalLightRenderingLayers", unchecked((int)RenderingLayerMask.Everything.Mask));
        data._StaticLightTexSize = GetGlobalInt("_StaticLightTexSize");
        data._StaticLightTexShift = GetGlobalInt("_StaticLightTexShift");
        data._StaticNodeTexSize = GetGlobalInt("_StaticNodeTexSize");
        data._StaticNodeTexShift = GetGlobalInt("_StaticNodeTexShift");
        data._DynamicLightTexSize = GetGlobalInt("_DynamicLightTexSize");
        data._DynamicLightTexShift = GetGlobalInt("_DynamicLightTexShift");
        data._DynamicNodeTexSize = GetGlobalInt("_DynamicNodeTexSize");
        data._DynamicNodeTexShift = GetGlobalInt("_DynamicNodeTexShift");
        data._DirectionalLightDirectionIntensity = new Float4(
            directionalDirection.X,
            directionalDirection.Y,
            directionalDirection.Z,
            GetGlobalFloat("_DirectionalLightIntensity"));
        data._DirectionalLightColor = new Float4(
            directionalColor.X,
            directionalColor.Y,
            directionalColor.Z,
            0f);

        Float2 ambientMode = GetGlobalVector2("_AmbientMode");
        data._AmbientModeStrength = new Float4(
            ambientMode.X,
            ambientMode.Y,
            GetGlobalFloat("_AmbientStrength"),
            0f);
        data._AmbientColor = GetGlobalVector4("_AmbientColor");
        data._AmbientSkyColor = GetGlobalVector4("_AmbientSkyColor");
        data._AmbientGroundColor = GetGlobalVector4("_AmbientGroundColor");
        data._FogColor = GetGlobalVector4("_FogColor");
        data._FogParams = GetGlobalVector4("_FogParams");
        Float3 fogStates = GetGlobalVector3("_FogStates");
        data._FogStates = new Float4(fogStates.X, fogStates.Y, fogStates.Z, 0f);

        // Clustered light list (P3.2): same command-ordered globals the GLSL path reads directly.
        // Default-zero (cleared globals) leaves the enabled flag at 0 -> BVH fallback in-shader.
        data._ClusterGridParams = GetGlobalVector4("_ClusterGridParams");
        data._ClusterZParams = GetGlobalVector4("_ClusterZParams");
        data._ClusterCameraForward = GetGlobalVector4("_ClusterCameraForward");
        return data;
    }

    // Pack the modern-backend Standard shadow block (b4). Reads the same command-ordered globals
    // SceneLightSystem.UploadGlobalUniforms writes for the GLSL path: the directional cascade
    // matrices/params, the per-slot point (6 faces) and spot (1 tile) shadow matrices, the shared
    // shadow atlas size, and the directional shadow control scalars. Slots that were never written
    // this frame stay at their default-zero / Identity state and the HLSL sampler rejects them via
    // the per-slot .z <= 0 / matrix-Identity guards that mirror Lighting.glsl.
    //
    // The PointShadow / SpotShadow arrays are uploaded through SetGlobalMatrices (numeric
    // System.Numerics.Matrix4x4 storage); we copy them into the fixed-size float rows here so the
    // structured-layout marshal writes contiguous HLSL float4x4 rows.
    public static unsafe StandardShadowsUniformsData PackStandardShadows()
    {
        StandardShadowsUniformsData data = default;

        Float2 shadowAtlasSize = GetGlobalVector2("_ShadowAtlasSize");
        data._ShadowAtlasSizePad = new Float4(
            shadowAtlasSize.X,
            shadowAtlasSize.Y,
            GetGlobalInt("_ScreenSpaceShadowsEnabled"),
            0f);
        // HLSL reads these back out by swizzle (_ShadowParams0.x = enabled, etc.), so pack the
        // command-ordered globals into the matching channels. Scalars are stored as float so the
        // HLSL int compares (_ShadowParams0.x != 0.0) and the cascade-count read ((int)_ShadowParams1.y)
        // round-trip without reinterpretation.
        data._ShadowParams0 = new Float4(
            GetGlobalInt("_DirectionalLightShadowEnabled"),
            GetGlobalFloat("_DirectionalLightShadowBias"),
            GetGlobalFloat("_DirectionalLightShadowNormalBias"),
            GetGlobalFloat("_DirectionalLightShadowStrength"));
        float shadowAtlasProjectedYScale = GetGlobalFloat("_ShadowAtlasProjectedYScale");
        if (MathF.Abs(shadowAtlasProjectedYScale) < 0.25f)
            shadowAtlasProjectedYScale = -0.5f;
        data._ShadowParams1 = new Float4(
            GetGlobalFloat("_DirectionalLightShadowQuality"),
            GetGlobalInt("_CascadeCount"),
            shadowAtlasProjectedYScale,
            GetGlobalInt("_SoftShadowTaps"));

        data._CascadeShadowMatrix0 = GetGlobalMatrix("_CascadeShadowMatrix0");
        data._CascadeShadowMatrix1 = GetGlobalMatrix("_CascadeShadowMatrix1");
        data._CascadeShadowMatrix2 = GetGlobalMatrix("_CascadeShadowMatrix2");
        data._CascadeShadowMatrix3 = GetGlobalMatrix("_CascadeShadowMatrix3");
        data._CascadeAtlasParams0 = GetGlobalVector4("_CascadeAtlasParams0");
        data._CascadeAtlasParams1 = GetGlobalVector4("_CascadeAtlasParams1");
        data._CascadeAtlasParams2 = GetGlobalVector4("_CascadeAtlasParams2");
        data._CascadeAtlasParams3 = GetGlobalVector4("_CascadeAtlasParams3");

        // fixed-size buffers are already pinned; access them directly as pointers.
        CopyGlobalMatrixArray("_PointShadowMatrices", data._PointShadowMatrices, StandardShadowsUniformsData.PointFaceCount);
        CopyGlobalFloat4Array("_PointShadowFaceParams", data._PointShadowFaceParams, StandardShadowsUniformsData.PointFaceCount);
        CopyGlobalMatrixArray("_SpotShadowMatrices", data._SpotShadowMatrices, StandardShadowsUniformsData.MaxShadowCasters);
        CopyGlobalFloat4Array("_SpotShadowAtlasParams", data._SpotShadowAtlasParams, StandardShadowsUniformsData.MaxShadowCasters);

        return data;
    }

    public static StandardReflectionProbesUniformsData PackStandardReflectionProbes()
    {
        StandardReflectionProbesUniformsData data = default;
        Float2 weights = GetGlobalVector2("_ProbeWeights");
        data._ProbeCountWeights = new Float4(GetGlobalInt("_ReflectionProbeCount"), weights.X, weights.Y, 0f);

        Float3 p0 = GetGlobalVector3("_Probe0Position");
        Float4 params0 = GetGlobalVector4("_Probe0Params");
        Float3 min0 = GetGlobalVector3("_Probe0BoxMin");
        Float3 max0 = GetGlobalVector3("_Probe0BoxMax");
        data._Probe0PositionIntensity = new Float4(p0.X, p0.Y, p0.Z, params0.X);
        data._Probe0BoxMinProjection = new Float4(min0.X, min0.Y, min0.Z, params0.Y);
        data._Probe0BoxMaxMips = new Float4(max0.X, max0.Y, max0.Z, params0.Z);

        Float3 p1 = GetGlobalVector3("_Probe1Position");
        Float4 params1 = GetGlobalVector4("_Probe1Params");
        Float3 min1 = GetGlobalVector3("_Probe1BoxMin");
        Float3 max1 = GetGlobalVector3("_Probe1BoxMax");
        data._Probe1PositionIntensity = new Float4(p1.X, p1.Y, p1.Z, params1.X);
        data._Probe1BoxMinProjection = new Float4(min1.X, min1.Y, min1.Z, params1.Y);
        data._Probe1BoxMaxMips = new Float4(max1.X, max1.Y, max1.Z, params1.Z);
        return data;
    }

    public static StandardLightCookiesUniformsData PackStandardLightCookies()
    {
        StandardLightCookiesUniformsData data = default;
        data._LightCookieMainParams = new Float4(
            GetGlobalInt("_LightCookieMainEnabled"),
            GetGlobalFloat("_LightCookieMainIntensity"),
            0f,
            0f);
        data._LightCookieMainWorldToLight = GetGlobalMatrix("_LightCookieMainWorldToLight");
        return data;
    }

    private static unsafe void CopyGlobalMatrixArray(string name, float* destination, int destinationCount)
    {
        System.Numerics.Matrix4x4[]? source = PropertyState.GetGlobalMatrices(name);
        if (source == null)
            return;
        int count = Math.Min(source.Length, destinationCount);
        for (int i = 0; i < count; i++)
        {
            Float4x4 value = (Float4x4)source[i];
            // Float4x4 is a blittable 16-float struct; copy row-by-row through a managed pointer so
            // we don't take a second fixed on an already-pinned span.
            ReadOnlySpan<float> sourceValues = MemoryMarshal.Cast<Float4x4, float>(MemoryMarshal.CreateReadOnlySpan(ref value, 1));
            for (int element = 0; element < 16; element++)
                destination[i * 16 + element] = sourceValues[element];
        }
    }

    private static unsafe void CopyGlobalFloat4Array(string name, float* destination, int destinationCount)
    {
        // Array globals currently only exist for matrices (SetGlobalMatrices). The face/atlas params
        // are uploaded element-by-element as _PointShadowFaceParams[i] / _SpotShadowAtlasParams[i]
        // by SceneLightSystem, so read them back through the scalar vector4 store with the bracketed
        // index name the pipeline writes.
        for (int i = 0; i < destinationCount; i++)
        {
            Float4 value = GetGlobalVector4($"{name}[{i}]");
            destination[i * 4 + 0] = value.X;
            destination[i * 4 + 1] = value.Y;
            destination[i * 4 + 2] = value.Z;
            destination[i * 4 + 3] = value.W;
        }
    }

    public static AnisotropicMaterialUniformsData PackAnisotropic(PropertyState? properties, Resources.Shader? shader)
    {
        AnisotropicMaterialUniformsData data = default;
        Rendering.Shaders.ShaderProperty[]? defaults = shader != null ? shader.PropertyArray : null;
        if (defaults != null)
        {
            for (int i = 0; i < defaults.Length; i++)
            {
                Rendering.Shaders.ShaderProperty property = defaults[i];
                ApplyCommonDefault(property, ref data._Tiling, ref data._Offset, ref data._MainColor);
                if (property.Name == "_EmissionIntensity")
                    data._EmissionIntensity = property.Value.X;
                else if (property.Name == "_AlphaCutoff")
                    data._AlphaCutoff = property.Value.X;
                else if (property.Name == "_Anisotropy")
                    data._Anisotropy = property.Value.X;
            }
        }

        ApplyCommonOverrides(properties, ref data._Tiling, ref data._Offset, ref data._MainColor);
        if (properties != null)
        {
            if (properties._floats.TryGetValue("_EmissionIntensity", out float emissionIntensity))
                data._EmissionIntensity = emissionIntensity;
            if (properties._floats.TryGetValue("_AlphaCutoff", out float alphaCutoff))
                data._AlphaCutoff = alphaCutoff;
            if (properties._floats.TryGetValue("_Anisotropy", out float anisotropy))
                data._Anisotropy = anisotropy;
        }
        return data;
    }

    public static TerrainMaterialUniformsData PackTerrain(PropertyState? properties, Resources.Shader? shader)
    {
        TerrainMaterialUniformsData data = default;
        data._TerrainWorldToLocal = Float4x4.Identity;
        data._TerrainLocalToWorld = Float4x4.Identity;
        Rendering.Shaders.ShaderProperty[]? defaults = shader != null ? shader.PropertyArray : null;
        if (defaults != null)
        {
            for (int i = 0; i < defaults.Length; i++)
                ApplyTerrainDefault(defaults[i], ref data);
        }

        if (properties != null)
        {
            if (properties._matrices.TryGetValue("_TerrainWorldToLocal", out Float4x4 worldToLocal)) data._TerrainWorldToLocal = worldToLocal;
            if (properties._matrices.TryGetValue("_TerrainLocalToWorld", out Float4x4 localToWorld)) data._TerrainLocalToWorld = localToWorld;
            if (properties._vectors2.TryGetValue("_BrushPosition", out Float2 brushPosition)) data._BrushPosition = brushPosition;
            if (properties._floats.TryGetValue("_TerrainSize", out float terrainSize)) data._TerrainSize = terrainSize;
            if (properties._floats.TryGetValue("_TerrainHeight", out float terrainHeight)) data._TerrainHeight = terrainHeight;
            if (properties._floats.TryGetValue("_BrushRadius", out float brushRadius)) data._BrushRadius = brushRadius;
            if (properties._floats.TryGetValue("_BrushFalloff", out float brushFalloff)) data._BrushFalloff = brushFalloff;
            if (properties._floats.TryGetValue("_BrushVisible", out float brushVisible)) data._BrushVisible = brushVisible;
            if (properties._ints.TryGetValue("_HasHoles", out int hasHoles)) data._HasHoles = hasHoles;
            if (properties._ints.TryGetValue("_LayerCount", out int layerCount)) data._LayerCount = layerCount;
            ApplyTerrainLayerOverrides(properties, ref data);
        }
        return data;
    }

    public static GrassMaterialUniformsData PackGrass(PropertyState? properties, Resources.Shader? shader)
    {
        GrassMaterialUniformsData data = default;
        data._TerrainWorldToLocal = Float4x4.Identity;
        data._TerrainLocalToWorld = Float4x4.Identity;
        data._TerrainUp = Float3.UnitY;
        data._TerrainSize = 1f;
        Rendering.Shaders.ShaderProperty[]? defaults = shader != null ? shader.PropertyArray : null;
        if (defaults != null)
        {
            for (int i = 0; i < defaults.Length; i++)
                ApplyGrassDefault(defaults[i], ref data);
        }

        if (properties != null)
        {
            if (properties._matrices.TryGetValue("_TerrainWorldToLocal", out Float4x4 worldToLocal)) data._TerrainWorldToLocal = worldToLocal;
            if (properties._matrices.TryGetValue("_TerrainLocalToWorld", out Float4x4 localToWorld)) data._TerrainLocalToWorld = localToWorld;
            if (properties._vectors3.TryGetValue("_TerrainUp", out Float3 terrainUp)) data._TerrainUp = terrainUp;
            if (properties._floats.TryGetValue("_TerrainSize", out float terrainSize)) data._TerrainSize = terrainSize;
            if (properties._floats.TryGetValue("_TerrainHeight", out float terrainHeight)) data._TerrainHeight = terrainHeight;
            ApplyGrassOverrides(properties, ref data);
        }
        return data;
    }

    public static CutoutMaterialUniformsData PackCutout(PropertyState? properties, Resources.Shader? shader)
    {
        CutoutMaterialUniformsData data = default;
        Rendering.Shaders.ShaderProperty[]? defaults = shader != null ? shader.PropertyArray : null;
        if (defaults != null)
        {
            for (int i = 0; i < defaults.Length; i++)
            {
                Rendering.Shaders.ShaderProperty property = defaults[i];
                ApplyCommonDefault(property, ref data._Tiling, ref data._Offset, ref data._MainColor);
                if (property.Name == "_AlphaCutoff")
                    data._AlphaCutoff = property.Value.X;
            }
        }

        ApplyCommonOverrides(properties, ref data._Tiling, ref data._Offset, ref data._MainColor);
        if (properties != null && properties._floats.TryGetValue("_AlphaCutoff", out float alphaCutoff))
            data._AlphaCutoff = alphaCutoff;
        return data;
    }

    public static GradientSkyboxUniformsData PackGradientSkybox(PropertyState? properties, Resources.Shader? shader)
    {
        GradientSkyboxUniformsData data = default;
        Rendering.Shaders.ShaderProperty[]? defaults = shader != null ? shader.PropertyArray : null;
        if (defaults != null)
        {
            for (int i = 0; i < defaults.Length; i++)
            {
                Rendering.Shaders.ShaderProperty property = defaults[i];
                if (property.Name == "_TopColor")
                    data._TopColor = property.Value;
                else if (property.Name == "_BottomColor")
                    data._BottomColor = property.Value;
                else if (property.Name == "_Exponent")
                    data._Exponent = property.Value.X;
            }
        }

        if (properties != null)
        {
            ApplyColorOverride(properties, "_TopColor", ref data._TopColor);
            ApplyColorOverride(properties, "_BottomColor", ref data._BottomColor);
            if (properties._floats.TryGetValue("_Exponent", out float exponent))
                data._Exponent = exponent;
        }
        return data;
    }

    public static CubemapSkyboxUniformsData PackCubemapSkybox(PropertyState? properties, Resources.Shader? shader)
    {
        CubemapSkyboxUniformsData data = default;
        Rendering.Shaders.ShaderProperty[]? defaults = shader != null ? shader.PropertyArray : null;
        if (defaults != null)
        {
            for (int i = 0; i < defaults.Length; i++)
            {
                Rendering.Shaders.ShaderProperty property = defaults[i];
                if (property.Name == "_Tint")
                    data._Tint = property.Value;
                else if (property.Name == "_Exposure")
                    data._Exposure = property.Value.X;
            }
        }

        if (properties != null)
        {
            ApplyColorOverride(properties, "_Tint", ref data._Tint);
            if (properties._floats.TryGetValue("_Exposure", out float exposure))
                data._Exposure = exposure;
        }
        return data;
    }

    public static GizmoIconUniformsData PackGizmoIcon(PropertyState? properties, Resources.Shader? shader)
    {
        GizmoIconUniformsData data = default;
        Rendering.Shaders.ShaderProperty[]? defaults = shader != null ? shader.PropertyArray : null;
        if (defaults != null)
        {
            for (int i = 0; i < defaults.Length; i++)
            {
                Rendering.Shaders.ShaderProperty property = defaults[i];
                if (property.Name == "_IconColor")
                    data._IconColor = property.Value;
                else if (property.Name == "_IconCenter")
                    data._IconCenter = new Float3(property.Value.X, property.Value.Y, property.Value.Z);
                else if (property.Name == "_IconScale")
                    data._IconScale = property.Value.X;
            }
        }

        if (properties != null)
        {
            if (properties._vectors4.TryGetValue("_IconColor", out Float4 iconColor))
                data._IconColor = iconColor;
            if (properties._vectors3.TryGetValue("_IconCenter", out Float3 iconCenter))
                data._IconCenter = iconCenter;
            if (properties._floats.TryGetValue("_IconScale", out float iconScale))
                data._IconScale = iconScale;
        }
        return data;
    }

    public static ProceduralSkyboxUniformsData PackProceduralSkybox(PropertyState? properties, Resources.Shader? shader)
    {
        ProceduralSkyboxUniformsData data = default;
        Rendering.Shaders.ShaderProperty[]? defaults = shader != null ? shader.PropertyArray : null;
        if (defaults != null)
        {
            for (int i = 0; i < defaults.Length; i++)
            {
                Rendering.Shaders.ShaderProperty property = defaults[i];
                if (property.Name == "Resolution")
                    data.Resolution = new Float2(property.Value.X, property.Value.Y);
                else if (property.Name == "fogDensity")
                    data.fogDensity = property.Value.X;
                else if (property.Name == "_SunDir")
                    data._SunDir = new Float3(property.Value.X, property.Value.Y, property.Value.Z);
            }
        }

        if (properties != null)
        {
            if (properties._vectors2.TryGetValue("Resolution", out Float2 resolution))
                data.Resolution = resolution;
            if (properties._floats.TryGetValue("fogDensity", out float fogDensity))
                data.fogDensity = fogDensity;
            if (properties._vectors3.TryGetValue("_SunDir", out Float3 sunDirection))
                data._SunDir = sunDirection;
            else if (properties._vectors4.TryGetValue("_SunDir", out Float4 sunDirection4))
                data._SunDir = new Float3(sunDirection4.X, sunDirection4.Y, sunDirection4.Z);
        }
        return data;
    }

    public static FrameDebugPreviewUniformsData PackFrameDebugPreview(PropertyState? properties, Resources.Shader? shader)
    {
        FrameDebugPreviewUniformsData data = default;
        data._LevelsMax = 1f;
        Rendering.Shaders.ShaderProperty[]? defaults = shader != null ? shader.PropertyArray : null;
        if (defaults != null)
        {
            for (int i = 0; i < defaults.Length; i++)
            {
                Rendering.Shaders.ShaderProperty property = defaults[i];
                if (property.Name == "_ChannelMode")
                    data._ChannelMode = property.Value.X;
                else if (property.Name == "_LevelsMin")
                    data._LevelsMin = property.Value.X;
                else if (property.Name == "_LevelsMax")
                    data._LevelsMax = property.Value.X;
                else if (property.Name == "_FlipY")
                    data._FlipY = property.Value.X;
            }
        }

        if (properties != null)
        {
            if (properties._floats.TryGetValue("_ChannelMode", out float mode))
                data._ChannelMode = mode;
            if (properties._floats.TryGetValue("_LevelsMin", out float levelsMin))
                data._LevelsMin = levelsMin;
            if (properties._floats.TryGetValue("_LevelsMax", out float levelsMax))
                data._LevelsMax = levelsMax;
            if (properties._floats.TryGetValue("_FlipY", out float flipY))
                data._FlipY = flipY;
        }
        return data;
    }

    public static TonemapperUniformsData PackTonemapper(PropertyState? properties, Resources.Shader? shader)
    {
        TonemapperUniformsData data = default;
        Rendering.Shaders.ShaderProperty[]? defaults = shader != null ? shader.PropertyArray : null;
        if (defaults != null)
        {
            for (int i = 0; i < defaults.Length; i++)
            {
                Rendering.Shaders.ShaderProperty property = defaults[i];
                if (property.Name == "Contrast")
                    data.Contrast = property.Value.X;
                else if (property.Name == "Saturation")
                    data.Saturation = property.Value.X;
                else if (property.Name == "_FlipY")
                    data._FlipY = property.Value.X;
            }
        }

        if (properties != null)
        {
            if (properties._floats.TryGetValue("Contrast", out float contrast))
                data.Contrast = contrast;
            if (properties._floats.TryGetValue("Saturation", out float saturation))
                data.Saturation = saturation;
            if (properties._floats.TryGetValue("_FlipY", out float flipY))
                data._FlipY = flipY;
        }
        return data;
    }

    public static UIBlurUniformsData PackUIBlur(PropertyState? properties, Resources.Shader? shader)
    {
        UIBlurUniformsData data = default;
        Rendering.Shaders.ShaderProperty[]? defaults = shader != null ? shader.PropertyArray : null;
        if (defaults != null)
        {
            for (int i = 0; i < defaults.Length; i++)
            {
                Rendering.Shaders.ShaderProperty property = defaults[i];
                if (property.Name == "_Offset")
                    data._Offset = property.Value.X;
            }
        }

        if (properties != null && properties._floats.TryGetValue("_Offset", out float offset))
            data._Offset = offset;
        return data;
    }

    public static FXAAUniformsData PackFXAA(PropertyState? properties, Resources.Shader? shader)
    {
        FXAAUniformsData data = default;
        Rendering.Shaders.ShaderProperty[]? defaults = shader != null ? shader.PropertyArray : null;
        if (defaults != null)
        {
            for (int i = 0; i < defaults.Length; i++)
            {
                Rendering.Shaders.ShaderProperty property = defaults[i];
                if (property.Name == "_Resolution")
                    data._Resolution = new Float2(property.Value.X, property.Value.Y);
                else if (property.Name == "_EdgeThresholdMin")
                    data._EdgeThresholdMin = property.Value.X;
                else if (property.Name == "_EdgeThresholdMax")
                    data._EdgeThresholdMax = property.Value.X;
                else if (property.Name == "_SubpixelQuality")
                    data._SubpixelQuality = property.Value.X;
            }
        }

        if (properties != null)
        {
            if (properties._vectors2.TryGetValue("_Resolution", out Float2 resolution))
                data._Resolution = resolution;
            if (properties._floats.TryGetValue("_EdgeThresholdMin", out float thresholdMin))
                data._EdgeThresholdMin = thresholdMin;
            if (properties._floats.TryGetValue("_EdgeThresholdMax", out float thresholdMax))
                data._EdgeThresholdMax = thresholdMax;
            if (properties._floats.TryGetValue("_SubpixelQuality", out float subpixelQuality))
                data._SubpixelQuality = subpixelQuality;
        }
        return data;
    }

    public static SMAAUniformsData PackSMAA(PropertyState? properties, Resources.Shader? shader)
    {
        SMAAUniformsData data = default;
        Rendering.Shaders.ShaderProperty[]? defaults = shader != null ? shader.PropertyArray : null;
        if (defaults != null)
        {
            for (int i = 0; i < defaults.Length; i++)
            {
                Rendering.Shaders.ShaderProperty property = defaults[i];
                if (property.Name == "_Resolution")
                    data._Resolution = new Float2(property.Value.X, property.Value.Y);
                else if (property.Name == "_EdgeThreshold")
                    data._EdgeThreshold = property.Value.X;
            }
        }

        if (properties != null)
        {
            if (properties._vectors2.TryGetValue("_Resolution", out Float2 resolution))
                data._Resolution = resolution;
            if (properties._floats.TryGetValue("_EdgeThreshold", out float edgeThreshold))
                data._EdgeThreshold = edgeThreshold;
        }
        return data;
    }

    public static FogTemporalUniformsData PackFogTemporal(PropertyState? properties)
    {
        FogTemporalUniformsData data = default;
        if (properties == null)
            return data;
        if (properties._vectors2.TryGetValue("_LowResolution", out Float2 lowResolution))
            data._LowResolution = lowResolution;
        if (properties._floats.TryGetValue("_FogHistoryValid", out float historyValid))
            data._FogHistoryValid = historyValid;
        if (properties._floats.TryGetValue("_FogTemporalBlend", out float temporalBlend))
            data._FogTemporalBlend = temporalBlend;
        return data;
    }

    public static FogCompositeUniformsData PackFogComposite(PropertyState? properties)
    {
        FogCompositeUniformsData data = default;
        if (properties == null)
            return data;
        if (properties._vectors2.TryGetValue("_Resolution", out Float2 resolution))
            data._Resolution = resolution;
        if (properties._vectors2.TryGetValue("_LowResolution", out Float2 lowResolution))
            data._LowResolution = lowResolution;
        if (properties._floats.TryGetValue("_FogUpsampleThreshold", out float upsampleThreshold))
            data._FogUpsampleThreshold = upsampleThreshold;
        return data;
    }

    public static unsafe FogMarchUniformsData PackFogMarch(PropertyState? properties)
    {
        FogMarchUniformsData data = default;
        data._FogMarchParams0 = new Float4(
            GetPropertyFloat(properties, "_FogDensity"),
            GetPropertyFloat(properties, "_FogScattering"),
            GetPropertyFloat(properties, "_FogExtinction"),
            GetPropertyFloat(properties, "_FogMaxDistance"));
        data._FogColorTint = GetPropertyFloat4(properties, "_FogColorTint", new Float4(1f, 1f, 1f, 1f));
        data._FogAmbientColor = GetPropertyFloat4(properties, "_FogAmbientColor", Float4.Zero);
        data._FogMarchParams1 = new Float4(
            GetPropertyFloat(properties, "_FogDithering"),
            GetPropertyFloat(properties, "_FogAmbientIntensity"),
            0f,
            0f);

        data._FogMarchFlags0X = GetPropertyInt(properties, "_FogSteps", 1);
        data._FogMarchFlags0Y = GetPropertyInt(properties, "_FogEnableDirectional");
        data._FogMarchFlags0Z = GetPropertyInt(properties, "_FogEnableDirectionalShadows");
        data._FogMarchFlags0W = GetPropertyInt(properties, "_FogEnablePointLights");
        data._FogMarchFlags1X = GetPropertyInt(properties, "_FogEnablePointShadows");
        data._FogMarchFlags1Y = GetPropertyInt(properties, "_FogEnableSpotLights");
        data._FogMarchFlags1Z = GetPropertyInt(properties, "_FogEnableSpotShadows");
        data._FogMarchFlags1W = GetPropertyInt(properties, "_FogVolumeCount");

        Float3 directionalDirection = GetGlobalVector3("_DirectionalLightDirection");
        Float3 directionalColor = GetGlobalVector3("_DirectionalLightColor");
        data._DirectionalFlagsX = GetGlobalInt("_DirectionalLightEnabled");
        data._DirectionalFlagsY = GetGlobalInt("_DirectionalLightShadowEnabled");
        data._DirectionalFlagsZ = GetGlobalInt("_CascadeCount");
        data._DirectionalLightDirectionIntensity = new Float4(
            directionalDirection.X,
            directionalDirection.Y,
            directionalDirection.Z,
            GetGlobalFloat("_DirectionalLightIntensity"));
        data._DirectionalLightColorShadowStrength = new Float4(
            directionalColor.X,
            directionalColor.Y,
            directionalColor.Z,
            GetGlobalFloat("_DirectionalLightShadowStrength"));
        data._DirectionalShadowParams = new Float4(
            GetGlobalFloat("_DirectionalLightShadowBias"),
            GetGlobalFloat("_DirectionalLightShadowQuality"),
            0f,
            0f);

        data._StaticBvhMetaX = GetGlobalInt("_StaticLightTexSize");
        data._StaticBvhMetaY = GetGlobalInt("_StaticLightTexShift");
        data._StaticBvhMetaZ = GetGlobalInt("_StaticNodeTexSize");
        data._StaticBvhMetaW = GetGlobalInt("_StaticNodeTexShift");
        data._DynamicBvhMetaX = GetGlobalInt("_DynamicLightTexSize");
        data._DynamicBvhMetaY = GetGlobalInt("_DynamicLightTexShift");
        data._DynamicBvhMetaZ = GetGlobalInt("_DynamicNodeTexSize");
        data._DynamicBvhMetaW = GetGlobalInt("_DynamicNodeTexShift");
        data._BvhRootsX = GetGlobalInt("_StaticLightRoot", -1);
        data._BvhRootsY = GetGlobalInt("_DynamicLightRoot", -1);
        Float2 shadowAtlasSize = GetGlobalVector2("_ShadowAtlasSize");
        data._ShadowAtlasSize = new Float4(shadowAtlasSize.X, shadowAtlasSize.Y, 0f, 0f);

        float* cascadeMatrices = data._CascadeShadowMatrices;
        float* cascadeParameters = data._CascadeAtlasParams;
        float* pointMatrices = data._PointShadowMatrices;
        float* pointParameters = data._PointShadowFaceParams;
        float* spotMatrices = data._SpotShadowMatrices;
        float* spotParameters = data._SpotShadowAtlasParams;
        float* volumeMatrices = data._FogVolumeWorldToLocal;
        float* volumeDensities = data._FogVolumeDensities;
        float* volumeColors = data._FogVolumeColors;
        float* volumeFalloffs = data._FogVolumeFalloffs;
        float* volumeConeAngles = data._FogVolumeConeAngles;
        int* volumeShapes = data._FogVolumeShapes;
        {
            for (int index = 0; index < 4; index++)
            {
                WriteMatrix(cascadeMatrices + index * 16, GetGlobalMatrix($"_CascadeShadowMatrix{index}"));
                WriteFloat4(cascadeParameters + index * 4, GetGlobalVector4($"_CascadeAtlasParams{index}"));
                WriteMatrix(spotMatrices + index * 16, GetGlobalMatrix($"_SpotShadowMatrices[{index}]"));
                WriteFloat4(spotParameters + index * 4, GetGlobalVector4($"_SpotShadowAtlasParams[{index}]"));
            }

            for (int index = 0; index < 24; index++)
            {
                WriteMatrix(pointMatrices + index * 16, GetGlobalMatrix($"_PointShadowMatrices[{index}]"));
                WriteFloat4(pointParameters + index * 4, GetGlobalVector4($"_PointShadowFaceParams[{index}]"));
            }

            for (int index = 0; index < 16; index++)
            {
                volumeShapes[index] = GetPropertyInt(properties, $"_FogVolumeShape[{index}]");
                WriteMatrix(
                    volumeMatrices + index * 16,
                    GetPropertyMatrix(properties, $"_FogVolumeWorldToLocal[{index}]", Float4x4.Identity));
                volumeDensities[index] = GetPropertyFloat(properties, $"_FogVolumeDensity[{index}]");
                WriteFloat4(
                    volumeColors + index * 4,
                    GetPropertyFloat4(properties, $"_FogVolumeColor[{index}]", new Float4(1f, 1f, 1f, 1f)));
                volumeFalloffs[index] = GetPropertyFloat(properties, $"_FogVolumeFalloff[{index}]");
                volumeConeAngles[index] = GetPropertyFloat(properties, $"_FogVolumeConeAngle[{index}]");
            }
        }

        return data;
    }

    public static RefractionMaterialUniformsData PackRefraction(PropertyState? properties, Resources.Shader? shader)
    {
        RefractionMaterialUniformsData data = default;
        Rendering.Shaders.ShaderProperty[]? defaults = shader != null ? shader.PropertyArray : null;
        if (defaults != null)
        {
            for (int i = 0; i < defaults.Length; i++)
            {
                Rendering.Shaders.ShaderProperty property = defaults[i];
                if (property.Name == "_RefractionStrength")
                    data._RefractionStrength = property.Value.X;
                else if (property.Name == "_NoiseScale")
                    data._NoiseScale = property.Value.X;
                else if (property.Name == "_Tint")
                    data._Tint = property.Value;
                else if (property.Name == "_BlurRadius")
                    data._BlurRadius = property.Value.X;
                else if (property.Name == "_BlurMIP")
                    data._BlurMIP = property.Value.X;
                else if (property.Name == "_BlurSteps")
                    data._BlurSteps = (int)property.Value.X;
            }
        }

        if (properties != null)
        {
            if (properties._floats.TryGetValue("_RefractionStrength", out float refractionStrength))
                data._RefractionStrength = refractionStrength;
            if (properties._floats.TryGetValue("_NoiseScale", out float noiseScale))
                data._NoiseScale = noiseScale;
            ApplyColorOverride(properties, "_Tint", ref data._Tint);
            if (properties._floats.TryGetValue("_BlurRadius", out float blurRadius))
                data._BlurRadius = blurRadius;
            if (properties._floats.TryGetValue("_BlurMIP", out float blurMip))
                data._BlurMIP = blurMip;
            if (properties._ints.TryGetValue("_BlurSteps", out int blurSteps))
                data._BlurSteps = blurSteps;
        }
        return data;
    }

    public static CinematicEffectsUniformsData PackCinematicEffects(PropertyState? properties)
    {
        CinematicEffectsUniformsData data = default;
        if (properties == null)
            return data;

        if (properties._vectors2.TryGetValue("_Resolution", out Float2 resolution)) data._Resolution = resolution;
        if (properties._floats.TryGetValue("_VignetteIntensity", out float vignetteIntensity)) data._VignetteIntensity = vignetteIntensity;
        if (properties._floats.TryGetValue("_VignetteSmoothness", out float vignetteSmoothness)) data._VignetteSmoothness = vignetteSmoothness;
        if (properties._floats.TryGetValue("_VignetteRoundness", out float vignetteRoundness)) data._VignetteRoundness = vignetteRoundness;
        if (properties._floats.TryGetValue("_ChromaticIntensity", out float chromaticIntensity)) data._ChromaticIntensity = chromaticIntensity;
        if (properties._floats.TryGetValue("_ChromaticDistortion", out float chromaticDistortion)) data._ChromaticDistortion = chromaticDistortion;
        if (properties._floats.TryGetValue("_GrainIntensity", out float grainIntensity)) data._GrainIntensity = grainIntensity;
        if (properties._floats.TryGetValue("_GrainResponse", out float grainResponse)) data._GrainResponse = grainResponse;
        if (properties._floats.TryGetValue("_PostExposure", out float postExposure)) data._PostExposure = postExposure;
        if (properties._floats.TryGetValue("_Contrast", out float contrast)) data._Contrast = contrast;
        if (properties._floats.TryGetValue("_Saturation", out float saturation)) data._Saturation = saturation;
        if (properties._floats.TryGetValue("_Temperature", out float temperature)) data._Temperature = temperature;
        ApplyColorOverride(properties, "_Lift", ref data._Lift);
        ApplyColorOverride(properties, "_Gamma", ref data._Gamma);
        ApplyColorOverride(properties, "_Gain", ref data._Gain);
        if (properties._floats.TryGetValue("_LUTContribution", out float lutContribution)) data._LUTContribution = lutContribution;
        if (properties._floats.TryGetValue("_LUTSize", out float lutSize)) data._LUTSize = lutSize;
        if (properties._floats.TryGetValue("_SharpenAmount", out float sharpenAmount)) data._SharpenAmount = sharpenAmount;
        if (properties._floats.TryGetValue("_SharpenRadius", out float sharpenRadius)) data._SharpenRadius = sharpenRadius;
        if (properties._floats.TryGetValue("_EdgeIntensity", out float edgeIntensity)) data._EdgeIntensity = edgeIntensity;
        if (properties._floats.TryGetValue("_EdgeBackgroundFade", out float edgeBackgroundFade)) data._EdgeBackgroundFade = edgeBackgroundFade;
        if (properties._floats.TryGetValue("_PixelSize", out float pixelSize)) data._PixelSize = pixelSize;
        if (properties._floats.TryGetValue("_GodRayIntensity", out float godRayIntensity)) data._GodRayIntensity = godRayIntensity;
        ApplyColorOverride(properties, "_EdgeColor", ref data._EdgeColor);
        if (properties._floats.TryGetValue("_GodRayDecay", out float godRayDecay)) data._GodRayDecay = godRayDecay;
        if (properties._floats.TryGetValue("_GodRayDensity", out float godRayDensity)) data._GodRayDensity = godRayDensity;
        if (properties._floats.TryGetValue("_GodRayWeight", out float godRayWeight)) data._GodRayWeight = godRayWeight;
        if (properties._ints.TryGetValue("_GodRaySamples", out int godRaySamples)) data._GodRaySamples = godRaySamples;
        if (properties._floats.TryGetValue("_GodRayThreshold", out float godRayThreshold)) data._GodRayThreshold = godRayThreshold;
        if (properties._vectors2.TryGetValue("_SunScreenPos", out Float2 sunScreenPos)) data._SunScreenPos = sunScreenPos;
        if (properties._ints.TryGetValue("_EffectFlags", out int effectFlags)) data._EffectFlags = effectFlags;
        return data;
    }

    public static BloomThresholdUniformsData PackBloomThreshold(PropertyState? properties, Resources.Shader? shader)
    {
        BloomThresholdUniformsData data = default;
        Rendering.Shaders.ShaderProperty[]? defaults = shader != null ? shader.PropertyArray : null;
        if (defaults != null)
        {
            for (int i = 0; i < defaults.Length; i++)
            {
                Rendering.Shaders.ShaderProperty property = defaults[i];
                if (property.Name == "_Threshold")
                    data._Threshold = property.Value.X;
            }
        }

        if (properties != null && properties._floats.TryGetValue("_Threshold", out float threshold))
            data._Threshold = threshold;
        return data;
    }

    public static BloomCompositeUniformsData PackBloomComposite(PropertyState? properties, Resources.Shader? shader)
    {
        BloomCompositeUniformsData data = default;
        Rendering.Shaders.ShaderProperty[]? defaults = shader != null ? shader.PropertyArray : null;
        if (defaults != null)
        {
            for (int i = 0; i < defaults.Length; i++)
            {
                Rendering.Shaders.ShaderProperty property = defaults[i];
                if (property.Name == "_Intensity")
                    data._Intensity = property.Value.X;
            }
        }

        if (properties != null && properties._floats.TryGetValue("_Intensity", out float intensity))
            data._Intensity = intensity;
        return data;
    }

    public static BokehDoFUniformsData PackBokehDoF(PropertyState? properties, Resources.Shader? shader)
    {
        BokehDoFUniformsData data = default;
        Rendering.Shaders.ShaderProperty[]? defaults = shader != null ? shader.PropertyArray : null;
        if (defaults != null)
        {
            for (int i = 0; i < defaults.Length; i++)
            {
                Rendering.Shaders.ShaderProperty property = defaults[i];
                if (property.Name == "_Resolution")
                    data._Resolution = new Float2(property.Value.X, property.Value.Y);
                else if (property.Name == "_FocusStrength")
                    data._FocusStrength = property.Value.X;
                else if (property.Name == "_ManualFocusPoint")
                    data._ManualFocusPoint = property.Value.X;
                else if (property.Name == "_MaxBlurRadius")
                    data._MaxBlurRadius = property.Value.X;
            }
        }

        if (properties != null)
        {
            if (properties._vectors2.TryGetValue("_Resolution", out Float2 resolution))
                data._Resolution = resolution;
            if (properties._floats.TryGetValue("_FocusStrength", out float focusStrength))
                data._FocusStrength = focusStrength;
            if (properties._floats.TryGetValue("_ManualFocusPoint", out float manualFocusPoint))
                data._ManualFocusPoint = manualFocusPoint;
            if (properties._floats.TryGetValue("_MaxBlurRadius", out float maxBlurRadius))
                data._MaxBlurRadius = maxBlurRadius;
        }
        return data;
    }

    public static SSRRayCastUniformsData PackSSRRayCast(PropertyState? properties, Resources.Shader? shader)
    {
        SSRRayCastUniformsData data = default;
        Rendering.Shaders.ShaderProperty[]? defaults = shader != null ? shader.PropertyArray : null;
        if (defaults != null)
        {
            for (int i = 0; i < defaults.Length; i++)
            {
                Rendering.Shaders.ShaderProperty property = defaults[i];
                if (property.Name == "_JitterSizeAndOffset")
                    data._JitterSizeAndOffset = property.Value;
                else if (property.Name == "_NumSteps")
                    data._NumSteps = property.Value.X;
                else if (property.Name == "_BRDFBias")
                    data._BRDFBias = property.Value.X;
            }
        }

        if (properties != null)
        {
            if (properties._vectors4.TryGetValue("_JitterSizeAndOffset", out Float4 jitter))
                data._JitterSizeAndOffset = jitter;
            if (properties._floats.TryGetValue("_NumSteps", out float numSteps))
                data._NumSteps = numSteps;
            if (properties._floats.TryGetValue("_BRDFBias", out float brdfBias))
                data._BRDFBias = brdfBias;
        }
        return data;
    }

    public static SSRSceneBlurUniformsData PackSSRSceneBlur(PropertyState? properties, Resources.Shader? shader)
    {
        SSRSceneBlurUniformsData data = default;
        Rendering.Shaders.ShaderProperty[]? defaults = shader != null ? shader.PropertyArray : null;
        if (defaults != null)
        {
            for (int i = 0; i < defaults.Length; i++)
            {
                Rendering.Shaders.ShaderProperty property = defaults[i];
                if (property.Name == "_BlurDir")
                    data._BlurDir = new Float2(property.Value.X, property.Value.Y);
            }
        }

        if (properties != null && properties._vectors2.TryGetValue("_BlurDir", out Float2 blurDir))
            data._BlurDir = blurDir;
        return data;
    }

    public static SSRResolveUniformsData PackSSRResolve(PropertyState? properties, Resources.Shader? shader)
    {
        SSRResolveUniformsData data = default;
        data._RayReuse = 1;
        data._UseNormalization = 1;
        data._Fireflies = 1;
        Rendering.Shaders.ShaderProperty[]? defaults = shader != null ? shader.PropertyArray : null;
        if (defaults != null)
        {
            for (int i = 0; i < defaults.Length; i++)
            {
                Rendering.Shaders.ShaderProperty property = defaults[i];
                if (property.Name == "_NoiseSize")
                    data._NoiseSize = new Float2(property.Value.X, property.Value.Y);
                else if (property.Name == "_ResolveSize")
                    data._ResolveSize = new Float2(property.Value.X, property.Value.Y);
                else if (property.Name == "_JitterSizeAndOffset")
                    data._JitterSizeAndOffset = property.Value;
                else if (property.Name == "_BRDFBias")
                    data._BRDFBias = property.Value.X;
                else if (property.Name == "_EdgeFactor")
                    data._EdgeFactor = property.Value.X;
                else if (property.Name == "_MaxMipMap")
                    data._MaxMipMap = property.Value.X;
                else if (property.Name == "_RayReuse")
                    data._RayReuse = (int)property.Value.X;
                else if (property.Name == "_UseNormalization")
                    data._UseNormalization = (int)property.Value.X;
                else if (property.Name == "_Fireflies")
                    data._Fireflies = (int)property.Value.X;
            }
        }

        if (properties != null)
        {
            if (properties._vectors2.TryGetValue("_NoiseSize", out Float2 noiseSize))
                data._NoiseSize = noiseSize;
            if (properties._vectors2.TryGetValue("_ResolveSize", out Float2 resolveSize))
                data._ResolveSize = resolveSize;
            if (properties._vectors4.TryGetValue("_JitterSizeAndOffset", out Float4 jitter))
                data._JitterSizeAndOffset = jitter;
            if (properties._floats.TryGetValue("_BRDFBias", out float brdfBias))
                data._BRDFBias = brdfBias;
            if (properties._floats.TryGetValue("_EdgeFactor", out float edgeFactor))
                data._EdgeFactor = edgeFactor;
            if (properties._floats.TryGetValue("_MaxMipMap", out float maxMipMap))
                data._MaxMipMap = maxMipMap;
            if (properties._ints.TryGetValue("_RayReuse", out int rayReuse))
                data._RayReuse = rayReuse;
            if (properties._ints.TryGetValue("_UseNormalization", out int useNormalization))
                data._UseNormalization = useNormalization;
            if (properties._ints.TryGetValue("_Fireflies", out int fireflies))
                data._Fireflies = fireflies;
        }
        return data;
    }

    public static SSRTemporalUniformsData PackSSRTemporal(PropertyState? properties, Resources.Shader? shader)
    {
        SSRTemporalUniformsData data = default;
        Rendering.Shaders.ShaderProperty[]? defaults = shader != null ? shader.PropertyArray : null;
        if (defaults != null)
        {
            for (int i = 0; i < defaults.Length; i++)
            {
                Rendering.Shaders.ShaderProperty property = defaults[i];
                if (property.Name == "_ReflectionVelocity")
                    data._ReflectionVelocity = (int)property.Value.X;
                else if (property.Name == "_TScale")
                    data._TScale = property.Value.X;
                else if (property.Name == "_TResponse")
                    data._TResponse = property.Value.X;
            }
        }

        if (properties != null)
        {
            if (properties._ints.TryGetValue("_ReflectionVelocity", out int reflectionVelocity))
                data._ReflectionVelocity = reflectionVelocity;
            if (properties._floats.TryGetValue("_TScale", out float tScale))
                data._TScale = tScale;
            if (properties._floats.TryGetValue("_TResponse", out float tResponse))
                data._TResponse = tResponse;
        }
        return data;
    }

    public static SSRCombineUniformsData PackSSRCombine(PropertyState? properties, Resources.Shader? shader)
    {
        SSRCombineUniformsData data = default;
        Rendering.Shaders.ShaderProperty[]? defaults = shader != null ? shader.PropertyArray : null;
        if (defaults != null)
        {
            for (int i = 0; i < defaults.Length; i++)
            {
                Rendering.Shaders.ShaderProperty property = defaults[i];
                if (property.Name == "_UseFresnel")
                    data._UseFresnel = (int)property.Value.X;
            }
        }

        if (properties != null && properties._ints.TryGetValue("_UseFresnel", out int useFresnel))
            data._UseFresnel = useFresnel;
        return data;
    }

    public static MotionBlurUniformsData PackMotionBlur(PropertyState? properties, Resources.Shader? shader)
    {
        MotionBlurUniformsData data = default;
        Rendering.Shaders.ShaderProperty[]? defaults = shader != null ? shader.PropertyArray : null;
        if (defaults != null)
        {
            for (int i = 0; i < defaults.Length; i++)
            {
                Rendering.Shaders.ShaderProperty property = defaults[i];
                if (property.Name == "_Resolution")
                    data._Resolution = new Float2(property.Value.X, property.Value.Y);
                else if (property.Name == "_Intensity")
                    data._Intensity = property.Value.X;
                else if (property.Name == "_Samples")
                    data._Samples = (int)property.Value.X;
                else if (property.Name == "_MaxBlurRadius")
                    data._MaxBlurRadius = property.Value.X;
            }
        }

        if (properties != null)
        {
            if (properties._vectors2.TryGetValue("_Resolution", out Float2 resolution))
                data._Resolution = resolution;
            if (properties._floats.TryGetValue("_Intensity", out float intensity))
                data._Intensity = intensity;
            if (properties._ints.TryGetValue("_Samples", out int samples))
                data._Samples = samples;
            if (properties._floats.TryGetValue("_MaxBlurRadius", out float maxBlurRadius))
                data._MaxBlurRadius = maxBlurRadius;
        }
        return data;
    }

    public static AutoExposureAdaptUniformsData PackAutoExposureAdapt(PropertyState? properties, Resources.Shader? shader)
    {
        AutoExposureAdaptUniformsData data = default;
        Rendering.Shaders.ShaderProperty[]? defaults = shader != null ? shader.PropertyArray : null;
        if (defaults != null)
        {
            for (int i = 0; i < defaults.Length; i++)
            {
                Rendering.Shaders.ShaderProperty property = defaults[i];
                if (property.Name == "_AdaptSpeedUp")
                    data._AdaptSpeedUp = property.Value.X;
                else if (property.Name == "_AdaptSpeedDown")
                    data._AdaptSpeedDown = property.Value.X;
                else if (property.Name == "_HistoryValid")
                    data._HistoryValid = property.Value.X;
            }
        }

        if (properties != null)
        {
            if (properties._floats.TryGetValue("_AdaptSpeedUp", out float speedUp))
                data._AdaptSpeedUp = speedUp;
            if (properties._floats.TryGetValue("_AdaptSpeedDown", out float speedDown))
                data._AdaptSpeedDown = speedDown;
            if (properties._floats.TryGetValue("_HistoryValid", out float historyValid))
                data._HistoryValid = historyValid;
        }
        return data;
    }

    public static AutoExposureApplyUniformsData PackAutoExposureApply(PropertyState? properties, Resources.Shader? shader)
    {
        AutoExposureApplyUniformsData data = default;
        Rendering.Shaders.ShaderProperty[]? defaults = shader != null ? shader.PropertyArray : null;
        if (defaults != null)
        {
            for (int i = 0; i < defaults.Length; i++)
            {
                Rendering.Shaders.ShaderProperty property = defaults[i];
                if (property.Name == "_ExposureComp")
                    data._ExposureComp = property.Value.X;
                else if (property.Name == "_MinExposure")
                    data._MinExposure = property.Value.X;
                else if (property.Name == "_MaxExposure")
                    data._MaxExposure = property.Value.X;
            }
        }

        if (properties != null)
        {
            if (properties._floats.TryGetValue("_ExposureComp", out float exposureCompensation))
                data._ExposureComp = exposureCompensation;
            if (properties._floats.TryGetValue("_MinExposure", out float minExposure))
                data._MinExposure = minExposure;
            if (properties._floats.TryGetValue("_MaxExposure", out float maxExposure))
                data._MaxExposure = maxExposure;
        }
        return data;
    }

    public static TAAUniformsData PackTAA(PropertyState? properties, Resources.Shader? shader)
    {
        TAAUniformsData data = default;
        Rendering.Shaders.ShaderProperty[]? defaults = shader != null ? shader.PropertyArray : null;
        if (defaults != null)
        {
            for (int i = 0; i < defaults.Length; i++)
            {
                Rendering.Shaders.ShaderProperty property = defaults[i];
                if (property.Name == "_Resolution")
                    data._Resolution = new Float2(property.Value.X, property.Value.Y);
                else if (property.Name == "_Jitter")
                    data._Jitter = new Float2(property.Value.X, property.Value.Y);
                else if (property.Name == "_HistoryValid")
                    data._HistoryValid = property.Value.X;
                else if (property.Name == "_BlendFactor")
                    data._BlendFactor = property.Value.X;
                else if (property.Name == "_MotionScale")
                    data._MotionScale = property.Value.X;
                else if (property.Name == "_Sharpness")
                    data._Sharpness = property.Value.X;
            }
        }

        if (properties != null)
        {
            if (properties._vectors2.TryGetValue("_Resolution", out Float2 resolution))
                data._Resolution = resolution;
            if (properties._vectors2.TryGetValue("_Jitter", out Float2 jitter))
                data._Jitter = jitter;
            if (properties._floats.TryGetValue("_HistoryValid", out float historyValid))
                data._HistoryValid = historyValid;
            if (properties._floats.TryGetValue("_BlendFactor", out float blendFactor))
                data._BlendFactor = blendFactor;
            if (properties._floats.TryGetValue("_MotionScale", out float motionScale))
                data._MotionScale = motionScale;
            if (properties._floats.TryGetValue("_Sharpness", out float sharpness))
                data._Sharpness = sharpness;
        }
        return data;
    }

    public static GTAOCalculateUniformsData PackGTAOCalculate(PropertyState? properties, Resources.Shader? shader)
    {
        GTAOCalculateUniformsData data = default;
        Rendering.Shaders.ShaderProperty[]? defaults = shader != null ? shader.PropertyArray : null;
        if (defaults != null)
        {
            for (int i = 0; i < defaults.Length; i++)
            {
                Rendering.Shaders.ShaderProperty property = defaults[i];
                if (property.Name == "_Slices")
                    data._Slices = (int)property.Value.X;
                else if (property.Name == "_DirectionSamples")
                    data._DirectionSamples = (int)property.Value.X;
                else if (property.Name == "_Radius")
                    data._Radius = property.Value.X;
                else if (property.Name == "_Intensity")
                    data._Intensity = property.Value.X;
                else if (property.Name == "_NoiseScale")
                    data._NoiseScale = new Float2(property.Value.X, property.Value.Y);
                else if (property.Name == "_JitterOffset")
                    data._JitterOffset = new Float2(property.Value.X, property.Value.Y);
            }
        }

        if (properties != null)
        {
            if (properties._ints.TryGetValue("_Slices", out int slices))
                data._Slices = slices;
            if (properties._ints.TryGetValue("_DirectionSamples", out int directionSamples))
                data._DirectionSamples = directionSamples;
            if (properties._floats.TryGetValue("_Radius", out float radius))
                data._Radius = radius;
            if (properties._floats.TryGetValue("_Intensity", out float intensity))
                data._Intensity = intensity;
            if (properties._vectors2.TryGetValue("_NoiseScale", out Float2 noiseScale))
                data._NoiseScale = noiseScale;
            if (properties._vectors2.TryGetValue("_JitterOffset", out Float2 jitterOffset))
                data._JitterOffset = jitterOffset;
        }
        return data;
    }

    public static SsgiGatherUniformsData PackSsgiGather(PropertyState? properties, Resources.Shader? shader)
    {
        SsgiGatherUniformsData data = default;
        data._NumSteps = 24f;
        data._Radius = 2.5f;
        data._GiRays = 2;
        if (properties != null)
        {
            if (properties._floats.TryGetValue("_NumSteps", out float numSteps))
                data._NumSteps = numSteps;
            if (properties._floats.TryGetValue("_Radius", out float radius))
                data._Radius = radius;
            if (properties._ints.TryGetValue("_GiRays", out int rays))
                data._GiRays = rays;
            if (properties._vectors2.TryGetValue("_NoiseScale", out Float2 noiseScale))
                data._NoiseScale = noiseScale;
            if (properties._vectors2.TryGetValue("_JitterOffset", out Float2 jitterOffset))
                data._JitterOffset = jitterOffset;
        }
        return data;
    }

    public static SsgiTemporalUniformsData PackSsgiTemporal(PropertyState? properties, Resources.Shader? shader)
    {
        SsgiTemporalUniformsData data = default;
        data._TResponse = 0.9f;
        if (properties != null && properties._floats.TryGetValue("_TResponse", out float response))
            data._TResponse = response;
        return data;
    }

    public static SsgiBlurUniformsData PackSsgiBlur(PropertyState? properties, Resources.Shader? shader)
    {
        SsgiBlurUniformsData data = default;
        data._BlurRadius = 1f;
        if (properties != null)
        {
            if (properties._vectors2.TryGetValue("_BlurDirection", out Float2 direction))
                data._BlurDirection = direction;
            if (properties._floats.TryGetValue("_BlurRadius", out float radius))
                data._BlurRadius = radius;
        }
        return data;
    }

    public static SsgiCompositeUniformsData PackSsgiComposite(PropertyState? properties, Resources.Shader? shader)
    {
        SsgiCompositeUniformsData data = default;
        data._Intensity = 1f;
        if (properties != null)
        {
            if (properties._floats.TryGetValue("_Intensity", out float intensity))
                data._Intensity = intensity;
            if (properties._ints.TryGetValue("_GiDebugView", out int debugView))
                data._GiDebugView = debugView;
        }
        return data;
    }

    public static SsgiProbeUniformsData PackSsgiProbe(PropertyState? properties, Resources.Shader? shader)
    {
        SsgiProbeUniformsData data = default;
        data._GiUpdateRate = 0.15f;
        if (properties != null)
        {
            if (properties._vectors4.TryGetValue("_GiUpdateRange", out Float4 range))
                data._GiUpdateRange = range;
            if (properties._vectors4.TryGetValue("_GiProbeOriginSpacing", out Float4 originSpacing))
                data._GiProbeOriginSpacing = originSpacing;
            if (properties._vectors4.TryGetValue("_GiProbeGridParams", out Float4 gridParams))
                data._GiProbeGridParams = gridParams;
            if (properties._vectors4.TryGetValue("_GiMissColor", out Float4 missColor))
                data._GiMissColor = missColor;
            if (properties._floats.TryGetValue("_GiUpdateRate", out float rate))
                data._GiUpdateRate = rate;
        }
        return data;
    }

    public static GTAOBlurUniformsData PackGTAOBlur(PropertyState? properties, Resources.Shader? shader)
    {
        GTAOBlurUniformsData data = default;
        Rendering.Shaders.ShaderProperty[]? defaults = shader != null ? shader.PropertyArray : null;
        if (defaults != null)
        {
            for (int i = 0; i < defaults.Length; i++)
            {
                Rendering.Shaders.ShaderProperty property = defaults[i];
                if (property.Name == "_BlurDirection")
                    data._BlurDirection = new Float2(property.Value.X, property.Value.Y);
                else if (property.Name == "_BlurRadius")
                    data._BlurRadius = property.Value.X;
            }
        }

        if (properties != null)
        {
            if (properties._vectors2.TryGetValue("_BlurDirection", out Float2 blurDirection))
                data._BlurDirection = blurDirection;
            if (properties._floats.TryGetValue("_BlurRadius", out float blurRadius))
                data._BlurRadius = blurRadius;
        }
        return data;
    }

    public static GTAOTemporalUniformsData PackGTAOTemporal(PropertyState? properties, Resources.Shader? shader)
    {
        GTAOTemporalUniformsData data = default;
        Rendering.Shaders.ShaderProperty[]? defaults = shader != null ? shader.PropertyArray : null;
        if (defaults != null)
        {
            for (int i = 0; i < defaults.Length; i++)
            {
                if (defaults[i].Name == "_TResponse")
                    data._TResponse = defaults[i].Value.X;
            }
        }

        if (properties != null && properties._floats.TryGetValue("_TResponse", out float response))
            data._TResponse = response;
        return data;
    }

    public static GridUniformsData PackGrid(PropertyState? properties, Resources.Shader? shader)
    {
        GridUniformsData data = default;
        Rendering.Shaders.ShaderProperty[]? defaults = shader != null ? shader.PropertyArray : null;
        if (defaults != null)
        {
            for (int i = 0; i < defaults.Length; i++)
            {
                Rendering.Shaders.ShaderProperty property = defaults[i];
                if (property.Name == "_GridColor")
                    data._GridColor = property.Value;
                else if (property.Name == "_PrimaryGridSize")
                    data._PrimaryGridSize = property.Value.X;
                else if (property.Name == "_SecondaryGridSize")
                    data._SecondaryGridSize = property.Value.X;
                else if (property.Name == "_LineWidth")
                    data._LineWidth = property.Value.X;
                else if (property.Name == "_Falloff")
                    data._Falloff = property.Value.X;
                else if (property.Name == "_MaxDist")
                    data._MaxDist = property.Value.X;
            }
        }

        if (properties != null)
        {
            ApplyColorOverride(properties, "_GridColor", ref data._GridColor);
            if (properties._floats.TryGetValue("_PrimaryGridSize", out float primaryGridSize))
                data._PrimaryGridSize = primaryGridSize;
            if (properties._floats.TryGetValue("_SecondaryGridSize", out float secondaryGridSize))
                data._SecondaryGridSize = secondaryGridSize;
            if (properties._floats.TryGetValue("_LineWidth", out float lineWidth))
                data._LineWidth = lineWidth;
            if (properties._floats.TryGetValue("_Falloff", out float falloff))
                data._Falloff = falloff;
            if (properties._floats.TryGetValue("_MaxDist", out float maxDistance))
                data._MaxDist = maxDistance;
        }
        return data;
    }

    private static void ApplyTerrainDefault(Rendering.Shaders.ShaderProperty property, ref TerrainMaterialUniformsData data)
    {
        switch (property.Name)
        {
            case "_BrushPosition": data._BrushPosition = new Float2(property.Value.X, property.Value.Y); break;
            case "_TerrainSize": data._TerrainSize = property.Value.X; break;
            case "_TerrainHeight": data._TerrainHeight = property.Value.X; break;
            case "_BrushRadius": data._BrushRadius = property.Value.X; break;
            case "_BrushFalloff": data._BrushFalloff = property.Value.X; break;
            case "_BrushVisible": data._BrushVisible = property.Value.X; break;
            case "_HasHoles": data._HasHoles = (int)property.Value.X; break;
            case "_LayerCount": data._LayerCount = (int)property.Value.X; break;
            case "_Layer0Tiling": data._Layer0Params.X = property.Value.X; break;
            case "_Layer0Roughness": data._Layer0Params.Y = property.Value.X; break;
            case "_Layer0Metallic": data._Layer0Params.Z = property.Value.X; break;
            case "_Layer1Tiling": data._Layer1Params.X = property.Value.X; break;
            case "_Layer1Roughness": data._Layer1Params.Y = property.Value.X; break;
            case "_Layer1Metallic": data._Layer1Params.Z = property.Value.X; break;
            case "_Layer2Tiling": data._Layer2Params.X = property.Value.X; break;
            case "_Layer2Roughness": data._Layer2Params.Y = property.Value.X; break;
            case "_Layer2Metallic": data._Layer2Params.Z = property.Value.X; break;
            case "_Layer3Tiling": data._Layer3Params.X = property.Value.X; break;
            case "_Layer3Roughness": data._Layer3Params.Y = property.Value.X; break;
            case "_Layer3Metallic": data._Layer3Params.Z = property.Value.X; break;
            case "_Layer4Tiling": data._Layer4Params.X = property.Value.X; break;
            case "_Layer4Roughness": data._Layer4Params.Y = property.Value.X; break;
            case "_Layer4Metallic": data._Layer4Params.Z = property.Value.X; break;
            case "_Layer5Tiling": data._Layer5Params.X = property.Value.X; break;
            case "_Layer5Roughness": data._Layer5Params.Y = property.Value.X; break;
            case "_Layer5Metallic": data._Layer5Params.Z = property.Value.X; break;
            case "_Layer6Tiling": data._Layer6Params.X = property.Value.X; break;
            case "_Layer6Roughness": data._Layer6Params.Y = property.Value.X; break;
            case "_Layer6Metallic": data._Layer6Params.Z = property.Value.X; break;
            case "_Layer7Tiling": data._Layer7Params.X = property.Value.X; break;
            case "_Layer7Roughness": data._Layer7Params.Y = property.Value.X; break;
            case "_Layer7Metallic": data._Layer7Params.Z = property.Value.X; break;
        }
    }

    private static void ApplyGrassDefault(Rendering.Shaders.ShaderProperty property, ref GrassMaterialUniformsData data)
    {
        switch (property.Name)
        {
            case "_AlphaCutoff": data._AlphaCutoff = property.Value.X; break;
            case "_WindStrength": data._WindStrength = property.Value.X; break;
            case "_WindSpeed": data._WindSpeed = property.Value.X; break;
            case "_Billboard": data._Billboard = property.Value.X; break;
            case "_AlignToNormal": data._AlignToNormal = property.Value.X; break;
            case "_Translucency": data._Translucency = property.Value.X; break;
            case "_ScatterPower": data._ScatterPower = property.Value.X; break;
            case "_ScatterDistortion": data._ScatterDistortion = property.Value.X; break;
            case "_ScatterScale": data._ScatterScale = property.Value.X; break;
            case "_GrassDistance": data._GrassDistance = property.Value.X; break;
            case "_GrassFadeStart": data._GrassFadeStart = property.Value.X; break;
        }
    }

    private static void ApplyGrassOverrides(PropertyState properties, ref GrassMaterialUniformsData data)
    {
        if (properties._floats.TryGetValue("_AlphaCutoff", out float alphaCutoff)) data._AlphaCutoff = alphaCutoff;
        if (properties._floats.TryGetValue("_WindStrength", out float windStrength)) data._WindStrength = windStrength;
        if (properties._floats.TryGetValue("_WindSpeed", out float windSpeed)) data._WindSpeed = windSpeed;
        if (properties._floats.TryGetValue("_Billboard", out float billboard)) data._Billboard = billboard;
        if (properties._floats.TryGetValue("_AlignToNormal", out float alignToNormal)) data._AlignToNormal = alignToNormal;
        if (properties._floats.TryGetValue("_Translucency", out float translucency)) data._Translucency = translucency;
        if (properties._floats.TryGetValue("_ScatterPower", out float scatterPower)) data._ScatterPower = scatterPower;
        if (properties._floats.TryGetValue("_ScatterDistortion", out float scatterDistortion)) data._ScatterDistortion = scatterDistortion;
        if (properties._floats.TryGetValue("_ScatterScale", out float scatterScale)) data._ScatterScale = scatterScale;
        if (properties._floats.TryGetValue("_GrassDistance", out float grassDistance)) data._GrassDistance = grassDistance;
        if (properties._floats.TryGetValue("_GrassFadeStart", out float grassFadeStart)) data._GrassFadeStart = grassFadeStart;
    }

    private static void ApplyTerrainLayerOverrides(PropertyState properties, ref TerrainMaterialUniformsData data)
    {
        ApplyTerrainLayerOverride(properties, "_Layer0Tiling", "_Layer0Roughness", "_Layer0Metallic", ref data._Layer0Params);
        ApplyTerrainLayerOverride(properties, "_Layer1Tiling", "_Layer1Roughness", "_Layer1Metallic", ref data._Layer1Params);
        ApplyTerrainLayerOverride(properties, "_Layer2Tiling", "_Layer2Roughness", "_Layer2Metallic", ref data._Layer2Params);
        ApplyTerrainLayerOverride(properties, "_Layer3Tiling", "_Layer3Roughness", "_Layer3Metallic", ref data._Layer3Params);
        ApplyTerrainLayerOverride(properties, "_Layer4Tiling", "_Layer4Roughness", "_Layer4Metallic", ref data._Layer4Params);
        ApplyTerrainLayerOverride(properties, "_Layer5Tiling", "_Layer5Roughness", "_Layer5Metallic", ref data._Layer5Params);
        ApplyTerrainLayerOverride(properties, "_Layer6Tiling", "_Layer6Roughness", "_Layer6Metallic", ref data._Layer6Params);
        ApplyTerrainLayerOverride(properties, "_Layer7Tiling", "_Layer7Roughness", "_Layer7Metallic", ref data._Layer7Params);
    }

    private static void ApplyTerrainLayerOverride(PropertyState properties, string tilingName, string roughnessName, string metallicName, ref Float4 parameters)
    {
        if (properties._floats.TryGetValue(tilingName, out float tiling)) parameters.X = tiling;
        if (properties._floats.TryGetValue(roughnessName, out float roughness)) parameters.Y = roughness;
        if (properties._floats.TryGetValue(metallicName, out float metallic)) parameters.Z = metallic;
    }

    private static void ApplyCommonDefault(
        Rendering.Shaders.ShaderProperty property,
        ref Float2 tiling,
        ref Float2 offset,
        ref Float4 mainColor)
    {
        if (property.Name == "_Tiling")
            tiling = new Float2(property.Value.X, property.Value.Y);
        else if (property.Name == "_Offset")
            offset = new Float2(property.Value.X, property.Value.Y);
        else if (property.Name == "_MainColor")
            mainColor = property.Value;
    }

    /// <summary>
    /// True when an instance <see cref="PropertyState"/> carries keys that belong to a material
    /// uniform block (colors, floats, tiling/clip vectors, non-transform matrices) rather than to
    /// the per-object block (transforms, object id, baked-GI state). Packed-uniform backends use
    /// this to decide whether a draw's material cbuffer must be repacked with instance overrides;
    /// object-block-only states (the common 3D batch case) keep the packed buffer untouched.
    /// </summary>
    public static bool HasMaterialBlockOverrides(PropertyState properties)
    {
        if (properties._floats.Count > 0 || properties._colors.Count > 0
            || properties._vectors2.Count > 0 || properties._vectors3.Count > 0)
        {
            return true;
        }
        foreach (string key in properties._vectors4.Keys)
        {
            if (key != "_LightmapScaleOffset" && !key.StartsWith("xengine_SH", StringComparison.Ordinal))
                return true;
        }
        foreach (string key in properties._ints.Keys)
        {
            if (key is not ("_ObjectID" or "_GIMode" or "_LightmapUV" or "_RenderingLayerMask"))
                return true;
        }
        foreach (string key in properties._matrices.Keys)
        {
            if (!key.StartsWith("xengine_", StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private static void ApplyCommonOverrides(
        PropertyState? properties,
        ref Float2 tiling,
        ref Float2 offset,
        ref Float4 mainColor)
    {
        if (properties == null)
            return;

        if (properties._vectors2.TryGetValue("_Tiling", out Float2 tilingOverride))
            tiling = tilingOverride;
        if (properties._vectors2.TryGetValue("_Offset", out Float2 offsetOverride))
            offset = offsetOverride;
        if (properties._vectors4.TryGetValue("_MainColor", out Float4 vectorColor))
            mainColor = vectorColor;
        else if (properties._colors.TryGetValue("_MainColor", out Color color))
            mainColor = new Float4(color.R, color.G, color.B, color.A);
    }

    private static Resources.Texture2D? GetTextureOverride(PropertyState? properties, string name)
    {
        if (properties != null && properties._textures.TryGetValue(name, out var texture))
            return texture.Res;
        return null;
    }

    private static Resources.Texture2D? GetTextureOverrideOrGlobal(PropertyState? properties, string name)
    {
        return GetTextureOverride(properties, name) ??
            (PropertyState.s_globalTextures.TryGetValue(name, out Resources.Texture2D texture) ? texture : null);
    }

    private static float GetPropertyFloat(PropertyState? properties, string name, float defaultValue = 0f)
    {
        return properties != null && properties._floats.TryGetValue(name, out float value) ? value : defaultValue;
    }

    private static int GetPropertyInt(PropertyState? properties, string name, int defaultValue = 0)
    {
        return properties != null && properties._ints.TryGetValue(name, out int value) ? value : defaultValue;
    }

    private static Float4 GetPropertyFloat4(PropertyState? properties, string name, Float4 defaultValue)
    {
        if (properties != null)
        {
            if (properties._vectors4.TryGetValue(name, out Float4 vector))
                return vector;
            if (properties._colors.TryGetValue(name, out Color color))
                return new Float4(color.R, color.G, color.B, color.A);
        }
        return defaultValue;
    }

    private static Float4x4 GetPropertyMatrix(PropertyState? properties, string name, Float4x4 defaultValue)
    {
        return properties != null && properties._matrices.TryGetValue(name, out Float4x4 value) ? value : defaultValue;
    }

    private static float GetGlobalFloat(string name)
    {
        return PropertyState.s_globalFloats.TryGetValue(name, out float value) ? value : 0f;
    }

    private static int GetGlobalInt(string name, int defaultValue = 0)
    {
        return PropertyState.s_globalInts.TryGetValue(name, out int value) ? value : defaultValue;
    }

    private static Float2 GetGlobalVector2(string name)
    {
        return PropertyState.s_globalVectors2.TryGetValue(name, out Float2 value) ? value : Float2.Zero;
    }

    private static Float3 GetGlobalVector3(string name)
    {
        return PropertyState.s_globalVectors3.TryGetValue(name, out Float3 value) ? value : Float3.Zero;
    }

    private static Float4 GetGlobalVector4(string name)
    {
        if (PropertyState.s_globalVectors4.TryGetValue(name, out Float4 vector))
            return vector;
        if (PropertyState.s_globalColors.TryGetValue(name, out Color color))
            return new Float4(color.R, color.G, color.B, color.A);
        return Float4.Zero;
    }

    private static Float4x4 GetGlobalMatrix(string name)
    {
        return PropertyState.s_globalMatrices.TryGetValue(name, out Float4x4 value) ? value : Float4x4.Identity;
    }

    private static unsafe void WriteMatrix(float* destination, Float4x4 value)
    {
        ReadOnlySpan<float> source = MemoryMarshal.Cast<Float4x4, float>(MemoryMarshal.CreateReadOnlySpan(ref value, 1));
        source.CopyTo(new Span<float>(destination, 16));
    }

    private static unsafe void WriteFloat4(float* destination, Float4 value)
    {
        destination[0] = value.X;
        destination[1] = value.Y;
        destination[2] = value.Z;
        destination[3] = value.W;
    }

    private static void AddTextureBinding(
        Dictionary<string, GraphicsTexture> bindings,
        string name,
        Resources.Texture2D? texture)
    {
        if (texture != null && !texture.IsDisposed)
            bindings[name] = texture.Handle;
    }

    private static void ApplyColorOverride(PropertyState properties, string name, ref Float4 value)
    {
        if (properties._vectors4.TryGetValue(name, out Float4 vectorColor))
            value = vectorColor;
        else if (properties._colors.TryGetValue(name, out Color color))
            value = new Float4(color.R, color.G, color.B, color.A);
    }
}
