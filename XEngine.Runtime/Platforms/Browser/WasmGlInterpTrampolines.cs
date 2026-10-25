// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

#if XENGINE_WEB
using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Runtime.InteropServices;

namespace XEngine.Runtime.Platforms.Browser;

/// <summary>
/// Unique DllImports scanned by <c>ManagedToNativeGenerator</c> so Mono-WASM emits
/// interp-to-native trampolines Silk.NET.OpenGL needs. Silk.NET uses
/// <c>delegate* unmanaged</c>, which that scan misses; <c>glClearColor</c> is VFFFF.
/// Entry points must not collide with <c>xengine_webgl2_*</c> (duplicate names break lookup).
/// </summary>
internal static class WasmGlInterpTrampolines
{
    private const string Lib = "xengine_webgl2";

    [DllImport(Lib, EntryPoint = "xengine_wasm_seed_vffff")]
    private static extern void Seed_VFFFF(float a, float b, float c, float d);

    [DllImport(Lib, EntryPoint = "xengine_wasm_seed_vf")]
    private static extern void Seed_VF(float a);

    [DllImport(Lib, EntryPoint = "xengine_wasm_seed_vd")]
    private static extern void Seed_VD(double a);

    [DllImport(Lib, EntryPoint = "xengine_wasm_seed_vi")]
    private static extern void Seed_VI(int a);

    [DllImport(Lib, EntryPoint = "xengine_wasm_seed_vii")]
    private static extern void Seed_VII(int a, int b);

    [DllImport(Lib, EntryPoint = "xengine_wasm_seed_viiii")]
    private static extern void Seed_VIIII(int a, int b, int c, int d);

    [DllImport(Lib, EntryPoint = "xengine_wasm_seed_vfi")]
    private static extern void Seed_VFI(float a, int b);

    [DllImport(Lib, EntryPoint = "xengine_wasm_seed_vif")]
    private static extern void Seed_VIF(int a, float b);

    [DllImport(Lib, EntryPoint = "xengine_wasm_seed_viff")]
    private static extern void Seed_VIFF(int a, float b, float c);

    [DllImport(Lib, EntryPoint = "xengine_wasm_seed_vifff")]
    private static extern void Seed_VIFFF(int a, float b, float c, float d);

    [DllImport(Lib, EntryPoint = "xengine_wasm_seed_viffff")]
    private static extern void Seed_VIFFFF(int a, float b, float c, float d, float e);

    [DllImport(Lib, EntryPoint = "xengine_wasm_seed_viii")]
    private static extern void Seed_VIII(int a, int b, int c);

    [DllImport(Lib, EntryPoint = "xengine_wasm_seed_viiiii")]
    private static extern void Seed_VIIIII(int a, int b, int c, int d, int e);

    [DllImport(Lib, EntryPoint = "xengine_wasm_seed_viiiiii")]
    private static extern void Seed_VIIIIII(int a, int b, int c, int d, int e, int f);

    [DllImport(Lib, EntryPoint = "xengine_wasm_seed_viibp")]
    private static extern void Seed_VIIBP(int a, int b, bool c, nint d);

    [DynamicDependency(DynamicallyAccessedMemberTypes.NonPublicMethods, typeof(WasmGlInterpTrampolines))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(BlockingCollection<int>))]
    [DynamicDependency(DynamicallyAccessedMemberTypes.All, typeof(IProducerConsumerCollection<int>))]
    public static void Keep()
    {
    }
}
#endif
