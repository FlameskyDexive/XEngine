#ifndef XENGINE_SHADER_CONFIG
#define XENGINE_SHADER_CONFIG

// Keep in lockstep with XEngine.Runtime.Rendering.ShaderConfig (C#) and ShaderConfig.hlsl.

#define MAX_SHADOW_CASTERS 4
#define XENGINE_STANDARD_MAX_SHADOW_CASTERS 4
#define LBVH_MAX_NODE_VISITS 4096
#define LBVH_TEXELS_PER_LIGHT 6
#define LBVH_TEXELS_PER_NODE 2
#define MAX_REFLECTION_PROBES 2
#define MAX_RENDERING_LAYERS 8
#define MAX_SOFT_SHADOW_TAPS 16
#define GI_PROBE_TILE_SIZE 8

#endif
