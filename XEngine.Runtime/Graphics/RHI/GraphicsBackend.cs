// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

namespace XEngine.Runtime.RHI;

/// <summary>Selects which graphics API the device factory should create.</summary>
public enum GraphicsBackend
{
    /// <summary>Pick a suitable backend for the current platform (D3D12 → Vulkan → OpenGL).</summary>
    Auto = 0,

    /// <summary>No-op device for headless / dedicated-server runs.</summary>
    Null = 1,

    OpenGL = 2,
    Vulkan = 3,
    Direct3D12 = 4,

    /// <summary>
    /// WebGPU, the only modern API available on the web. One C# implementation serves two hosts:
    /// wgpu-native on desktop (so the backend can be developed and verified without a browser) and
    /// emdawnwebgpu inside the WASM player. Never chosen by <see cref="Auto"/> on desktop - it exists
    /// there for development and cross-backend comparison, not as a fallback.
    /// </summary>
    WebGPU = 5,

    /// <summary>
    /// Native Metal. The only modern API on iOS; also available on macOS for headless Stage C.
    /// Never chosen by <see cref="Auto"/> on desktop. On iOS, Auto is Metal.
    /// Binding path is the I0-frozen objc_msgSend P/Invoke surface, not Microsoft.iOS or SharpMetal.
    /// </summary>
    Metal = 6,

    /// <summary>
    /// WebGL 2.0 / OpenGL ES 3.0. Coverage backend for browsers and mini-games that do not
    /// expose WebGPU (Douyin today; WeChat when the WebGPU preview is unavailable). Never chosen
    /// by <see cref="Auto"/> on desktop — select it with <c>--graphics=webgl2</c>.
    /// Desktop hosts a Core 4.1 context and rewrites GLSL ES to <c>#version 330</c>; the browser
    /// keeps <c>#version 300 es</c>.
    /// </summary>
    WebGL2 = 7,
}
