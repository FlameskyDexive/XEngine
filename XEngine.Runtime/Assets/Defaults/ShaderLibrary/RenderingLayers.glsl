#ifndef XENGINE_RENDERING_LAYERS
#define XENGINE_RENDERING_LAYERS

#include "ShaderLibrary/ShaderConfig"
#include "ShaderVariables"

// Per-draw object membership (8 bits) lives in ShaderVariables._RenderingLayerMask.
// Lights store the same mask in LightBVH TypeAndFlags bits 8-15 and in
// _DirectionalLightRenderingLayers. Default Everything (255) keeps lighting.

bool LightAffectsObject(int lightLayers)
{
    return (lightLayers & _RenderingLayerMask) != 0;
}

#endif
