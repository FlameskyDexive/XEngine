// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Runtime.CompilerServices;

namespace XEngine.Runtime.Rendering;

/// <summary>
/// Echo <c>$type</c> aliases for the Unity 6.7 rename of the pipeline asset classes. Data
/// serialized before the rename (as <c>XEngine.Runtime.Rendering.Scriptable.PipelineAsset</c> /
/// <c>...Scriptable.RendererData</c> in .pipeline / .renderer files) resolves to
/// <see cref="UniversalRenderPipelineAsset"/> / <see cref="UniversalRenderData"/>. Both the
/// assembly-qualified and bare-full-name forms are registered because Echo writers have emitted
/// each at different times; misses fall through untouched to the normal resolution chain.
/// </summary>
internal static class UniversalPipelineTypeCompat
{
    [ModuleInitializer]
    internal static void RegisterLegacyAliases()
    {
        RuntimeTypeRegistry.RegisterAlias(
            "XEngine.Runtime.Rendering.Scriptable.PipelineAsset, XEngine.Runtime",
            "XEngine.Runtime.Rendering.UniversalRenderPipelineAsset, XEngine.Runtime");
        RuntimeTypeRegistry.RegisterAlias(
            "XEngine.Runtime.Rendering.Scriptable.PipelineAsset",
            "XEngine.Runtime.Rendering.UniversalRenderPipelineAsset");
        RuntimeTypeRegistry.RegisterAlias(
            "XEngine.Runtime.Rendering.Scriptable.RendererData, XEngine.Runtime",
            "XEngine.Runtime.Rendering.UniversalRenderData, XEngine.Runtime");
        RuntimeTypeRegistry.RegisterAlias(
            "XEngine.Runtime.Rendering.Scriptable.RendererData",
            "XEngine.Runtime.Rendering.UniversalRenderData");
    }
}
