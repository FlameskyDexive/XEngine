#ifndef XENGINE_RENDERING_LAYERS_HLSL
#define XENGINE_RENDERING_LAYERS_HLSL

#include "ShaderLibrary/ShaderConfig"

// Uses ObjectUniforms._RenderingLayerMask (b1). Default 255 = Everything.
bool LightAffectsObject(int lightLayers)
{
    return (lightLayers & _RenderingLayerMask) != 0;
}

#endif
