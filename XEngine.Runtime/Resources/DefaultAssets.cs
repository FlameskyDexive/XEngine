// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

namespace XEngine.Runtime.Resources;

/// <summary>
/// Default shaders embedded in the runtime
/// </summary>
public enum DefaultShader
{
    Standard,
    StandardTransparent,
    StandardAnisotropic,
    Unlit,
    Sprite,
    Line,
    Invalid,
    UI,
    Gizmos,
    Blit,
    Particle,
    Refraction,
    DefaultUI,
    DefaultText,
    DefaultTextMesh,

    ProceduralSkybox,
    GradientSkybox,
    CubemapSkybox,
    Tonemapper,
    SSR,
    FXAA,
    SMAA,
    Bloom,
    BokehDoF,
    GTAO,
    Grid,
    CinematicEffects,
    VolumetricFog,
    TAA,
    MotionBlur,
    GizmoIcon,
    AutoExposure,
    ScreenSpaceDecal,
    ScreenSpaceShadows,
    DeferredLighting,
    SpriteLit,
    Light2D,
    Shadow2D,
    SSGI,
    VirtualGeometryCull,
    FrameDebugPreview,
    /// <summary>★ M7 — Screen-space selection outline composite (reads a silhouette mask).</summary>
    SelectionOutline,
    /// <summary>★ B7 — World-space WebSurface variant of DefaultUI with scene depth test on
    /// (ZTest LEqual, ZWrite Off): the panel is occluded by scene geometry instead of always
    /// drawing on top. Same cbuffer/properties as DefaultUI so material packing is shared.</summary>
    DefaultUIWorld,
}

/// <summary>
/// Default models/meshes embedded in the runtime
/// </summary>
public enum DefaultModel
{
    Cube,
    Sphere,
    Cylinder,
    Plane,
    SkyDome,
}

/// <summary>
/// Default materials embedded in the runtime
/// </summary>
public enum DefaultMaterial
{
    Standard,
    Particle,
}

/// <summary>
/// Default sprites embedded in the runtime
/// </summary>
public enum DefaultSprite
{
    /// <summary>A rounded nine-slice panel (white fill, subtle border) used as the default UI background.</summary>
    UIPanel,
}

/// <summary>
/// Default textures embedded in the runtime
/// </summary>
public enum DefaultTexture
{
    White,
    Gray18, // Also accessible as Gray
    Normal,
    Surface,
    Emission,
    Grid,
    Noise,

    // UI
    UIPanel,

    // Scene helpers
    Handle,

    // Gizmo icons
    IconCamera,
    IconLight,
    IconAudioSource,
    IconParticleSystem,
    IconDecal,
}

/// <summary>
/// Default fonts embedded in the runtime
/// </summary>
public enum DefaultFont
{
    Default, // Geist-Regular
}

/// <summary>
/// Default shader include files (GLSL)
/// </summary>
public enum DefaultShaderInclude
{
    XEngineCG,
    PBR,
    Random,
    ShaderVariables,
    Shadow,
    VertexAttributes,
    Lighting,
    LightBVH,
    StandardSurface,
    FastNoiseLite,
    SimplexNoise4D
}
