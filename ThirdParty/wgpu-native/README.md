# wgpu-native

Prebuilt [wgpu-native](https://github.com/gfx-rs/wgpu-native) binaries used by the desktop
WebGPU graphics backend (`XEngine.Runtime/Graphics/Backends/WebGPU`).

- **Version**: see `VERSION` (currently `v29.0.1.1`)
- **Header**: unified `webgpu.h` (`WGPUStringView`, `WGPUFuture`, `wgpuInstanceWaitAny`)
- **Desktop host**: `DllImport("wgpu_native")`
- **WASM host**: the same C# P/Invoke surface; emdawnwebgpu is linked later by the browser player

This directory vendors **only** the unified `webgpu.h` ABI. wgpu-native extras from `wgpu.h`
(`WGPUInstanceExtras`, native features, …) stay out of the shared binding so the C# backend can
target emdawnwebgpu without a second ABI.

## Layout

```
include/webgpu/webgpu.h          # locked header, do not edit
runtimes/win-x64/native/wgpu_native.dll
VERSION
```

Linux / macOS RIDs are added when those desktop hosts are wired. Do not commit `.lib`, `.pdb`,
or the upstream zip.

## Refresh (Windows x64)

```powershell
$tag = 'v29.0.1.1'
$zip = "wgpu-windows-x86_64-msvc-release.zip"
Invoke-WebRequest "https://github.com/gfx-rs/wgpu-native/releases/download/$tag/$zip" -OutFile $zip
Expand-Archive $zip -DestinationPath extract -Force
Copy-Item extract/include/webgpu/webgpu.h include/webgpu/webgpu.h -Force
Copy-Item extract/lib/wgpu_native.dll runtimes/win-x64/native/wgpu_native.dll -Force
Set-Content VERSION "$tag"
```

Licenses: wgpu-native is MIT OR Apache-2.0; `webgpu.h` is BSD-3-Clause. See
`THIRD_PARTY_NOTICES.md` in the repo root.
