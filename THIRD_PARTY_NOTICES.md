# Third-Party Notices

This file documents third-party software vendored into this repository.

---

## Anthology (XEngine.* libraries)

- **Source**: https://github.com/XEngine/Anthology
- **Vendored version**: 2.8.6
- **License**: MIT
- **Location in this repo**: `ThirdParty/`

The following libraries from the Anthology monorepo are included as source under
`ThirdParty/` and consumed via `ProjectReference`:

| Package | Path |
|---|---|
| XEngine.Clay | `ThirdParty/Clay/` |
| XEngine.Echo | `ThirdParty/Echo/` |
| XEngine.Origami | `ThirdParty/Origami/` |
| XEngine.Paper | `ThirdParty/Paper/` |
| XEngine.Photonic | `ThirdParty/Photonic/` |
| XEngine.Quill | `ThirdParty/Quill/` |
| XEngine.Rosetta | `ThirdParty/Rosetta/` |
| XEngine.Scribe | `ThirdParty/Scribe/` |
| XEngine.Slang | `ThirdParty/Slang/` |
| XEngine.Unwrapper | `ThirdParty/Unwrapper/` |
| XEngine.Vector | `ThirdParty/Vector/` |

### Anthology MIT License

```
MIT License

Copyright (c) 2026 Wulferis

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
```

---

## Slang compiler native binaries

- **Source**: https://github.com/shader-slang/slang
- **Vendored version**: 2026.12 (release tag `v2026.12`)
- **License**: MIT (see https://github.com/shader-slang/slang/blob/master/LICENSE)
- **Location in this repo**: `ThirdParty/Slang/Native/lib/<RID>/`

Pre-built native libraries for 6 RIDs (windows-x64, windows-arm64, linux-x64,
linux-arm64, macos-x64, macos-arm64) are committed to the repository so that the
build does not require network access. Each RID ships three binaries:
`slang-compiler`, `slang-glsl-module`, and `slang-glslang`.

The fetch script at `ThirdParty/Slang/Native/UpdateSources.cs` documents how these
binaries are obtained from the upstream Slang GitHub release.

---

## K4os.Compression.LZ4

- **Source**: https://github.com/MiloszKrajewski/K4os.Compression.LZ4
- **Consumed as**: NuGet package (`K4os.Compression.LZ4`, referenced by `XEngine.Runtime.csproj`)
- **License**: MIT
- **Used for**: LZ4 block compression of the AssetBundle container data region

The AssetBundle system uses `K4os.Compression.LZ4.LZ4Codec` to compress bundle payloads in fixed
blocks; decompression is on demand via the per-block offset table so individual assets can be read
without decompressing the whole bundle.

### K4os.Compression.LZ4 MIT License

```
MIT License

Copyright (c) 2017-2023 Milosz Krajewski
```

---

## Box2D.NET

- **Source**: https://github.com/ikpil/Box2D.NET
- **Consumed as**: NuGet package (`Box2D.NET` 3.1.654, referenced by `XEngine.Runtime.csproj`)
- **License**: MIT
- **Used for**: Per-scene Physics2D (`PhysicsWorld2D`, Rigidbody2D, colliders). One engine unit is one meter. Reproducible only for the same platform, library version, and fixed step.

### Box2D.NET MIT License

```
MIT License

Copyright (c) 2023 ikpil
Copyright (c) 2006-2025 Erin Catto
```

---

## Jitter Physics 2

- **Source**: https://github.com/notgiven688/jitterphysics2
- **Consumed as**: vendored, modified source (`ThirdParty/Jitter2/`, `ProjectReference` from `XEngine.Runtime.csproj`). Previously the `Jitter2` NuGet package.
- **Vendored at**: commit `8bf70b32` (`2.8.9-1-g8bf70b32`)
- **License**: MIT
- **Used for**: Per-scene 3D physics (`PhysicsWorld`, `Rigidbody3D`, colliders, joints, character controller, queries). Local patches are listed in `ThirdParty/Jitter2/PATCHES.md`.

### Jitter2 MIT License

```
MIT License

Copyright (c) Thorben Linneweber and contributors
```

---

## RectpackSharp

- **Source**: https://github.com/ThomasMiz/RectpackSharp
- **Consumed as**: NuGet package (`RectpackSharp` 1.2.0, referenced by `XEngine.Runtime.csproj`)
- **License**: MIT
- **Used for**: Deterministic SpriteAtlas rectangle packing. Rotation is disabled so nine-slice, normal maps, and pixel-perfect sampling stay axis-aligned.

### RectpackSharp MIT License

```
MIT License

Copyright (c) 2020 Thomas Mizrahi
```

---

## Magick.NET

- **Source**: https://github.com/dlemstra/Magick.NET
- **Consumed as**: NuGet package (`Magick.NET-Q8-AnyCPU` 14.13.0, referenced by `XEngine.Runtime.csproj`)
- **License**: Apache License 2.0 (https://github.com/dlemstra/Magick.NET/blob/main/License.txt)
- **Used for**: Texture decode and PSD layer names in `PsdImporter`. Layer sprites are not generated in headless imports.

---

## AsepriteDotNet

- **Source**: https://github.com/AristurtleDev/AsepriteDotNet
- **Consumed as**: NuGet package (`AsepriteDotNet` 1.9.1, referenced by `XEngine.Editor.csproj`)
- **License**: MIT
- **Used for**: Aseprite frames, tags, and slices in `AsepriteImporter`. Header-only files fall back to the engine's CPU chunk reader.

### AsepriteDotNet MIT License

```
MIT License

Copyright 2024 Christopher Whitley

Permission is hereby granted, free of charge, to any person obtaining a copy of
this software and associated documentation files (the "Software"), to deal in
the Software without restriction, including without limitation the rights to
use, copy, modify, merge, publish, distribute, sublicense, and/or sell copies of
the Software, and to permit persons to whom the Software is furnished to do so,
subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY, FITNESS
FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE AUTHORS OR
COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER LIABILITY, WHETHER
IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM, OUT OF OR IN
CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE SOFTWARE.
```

---

## DotRecast

- **Source**: https://github.com/ikpil/DotRecast (C# port of Recast Navigation / recast4j)
- **Vendored version**: local snapshot from `D:\Engine\DotRecast` (2026-08-15)
- **License**: zlib
- **Location in this repo**: `ThirdParty/DotRecast/`
- **Used for**: NavMesh baking and runtime pathfinding (`Packages/com.xengine.ai.navigation/`)

Original Recast Navigation: https://github.com/recastnavigation/recastnavigation  
recast4j: https://github.com/ppiastucki/recast4j

Vendored modules: `DotRecast.Core`, `DotRecast.Recast`, `DotRecast.Detour`,
`DotRecast.Detour.TileCache`, `DotRecast.Detour.Crowd`, `DotRecast.Detour.Dynamic`.
The Recast demo, Recast.Toolset, and Detour.Extras trees are not vendored.

Refresh: `powershell -File ThirdParty/DotRecast/UpdateSources.ps1 <DotRecast-checkout>`

### zlib License

```
Copyright (c) 2015 Mikko Mononen memon@inside.org, Piotr Piastucki, choi ikpil ikpil@naver.com
Copyright (c) 2009-2010 Mikko Mononen
Copyright (c) 2015-2021 Piotr Piastucki
Copyright (c) 2023-2024 Choi Ikpil

This software is provided 'as-is', without any express or implied
warranty.  In no event will the authors be held liable for any damages
arising from the use of this software.

Permission is granted to anyone to use this software for any purpose,
including commercial applications, and to alter it and redistribute it
freely, subject to the following restrictions:

1. The origin of this software must not be misrepresented; you must not
claim that you wrote the original software. If you use this software
in a product, an acknowledgment in the product documentation would be
appreciated but is not required.
2. Altered source versions must be plainly marked as such, and must not be
misrepresented as being the original software.
3. This notice may not be removed or altered from any source distribution.
```

---

## LibTessDotNet

- **Source**: https://github.com/speps/LibTessDotNet (vendored inside Quill)
- **Consumed as**: `ThirdParty/Quill/Quill/External/LibTessDotNet/` (`XEngine.Quill.External.LibTessDotNet`)
- **License**: SGI Free Software License B 2.0 (libtess / libtess2 / LibTessDotNet)
- **Used for**: SpriteShape closed-spline fill triangulation (`SpriteShapeController.Bake`). Edge/corner sprite meshes are a later slice.

---

## wgpu-native

- **Source**: https://github.com/gfx-rs/wgpu-native
- **Vendored version**: v29.0.1.1
- **Location in this repo**: `ThirdParty/wgpu-native/`
- **Used for**: desktop host of the WebGPU graphics backend (`XEngine.Runtime/Graphics/Backends/WebGPU`)
- **License**: MIT OR Apache-2.0 (wgpu-native); BSD-3-Clause (`webgpu.h` from webgpu-native/webgpu-headers)

The C# backend P/Invokes the unified `webgpu.h` ABI. emdawnwebgpu (browser) is expected to implement the same header; wgpu-native extras from `wgpu.h` are not vendored into the binding.

---

## emdawnwebgpu

- **Source**: https://github.com/google/dawn (Emdawnwebgpu package)
- **Vendored version**: v20260814.182433 (`56f332d7`)
- **Location in this repo**: `ThirdParty/emdawnwebgpu_pkg/`
- **Used for**: WASM WebGPU (`webgpu.h` + JS bindings) linked into the browser / WeChat player. .NET 10 `wasm-tools` ships Emscripten 3.1.56, which has no named `emdawnwebgpu` port, so the local package is copied into the generated Player and passed as `--use-port=native/emdawnwebgpu_pkg/emdawnwebgpu.port.py`.
- **License**: BSD 3-Clause (Dawn); some files under Emscripten's MIT / UIUC license (see `webgpu/src/LICENSE`)
