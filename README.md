<div align="center">

<img src="docs/readme/logo.png" width="128" alt="XEngine logo" />

# XEngine

**An AI-native, Unity-like game engine written in pure C# on .NET 10.**

**English** · [简体中文](README.zh-CN.md)

[![Release](https://img.shields.io/badge/release-V1.0.0f1-blue?style=flat-square)](#-project-status)
[![License](https://img.shields.io/badge/license-MIT-green?style=flat-square)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?style=flat-square&logo=dotnet)](https://dotnet.microsoft.com)
[![Language](https://img.shields.io/badge/language-C%23-239120?style=flat-square)](https://learn.microsoft.com/dotnet/csharp/)
[![Backends](https://img.shields.io/badge/RHI-D3D12%20%C2%B7%20Vulkan%20%C2%B7%20OpenGL%20%C2%B7%20Metal%20%C2%B7%20WebGPU%20%C2%B7%20WebGL2-informational?style=flat-square)](#-graphics--rendering)

🎉 **V1.0.0f1 — officially open source under MIT.** First official release.

</div>

---

<img src="docs/readme/editor-overview.png" width="100%" alt="XEngine Editor — Vulkan backend, voxel world from the built-in com.xengine.voxelworld package" />

<div align="center"><sub>XEngine Editor V1.0.0f1 · Vulkan backend · voxel world from the built-in com.xengine.voxelworld package</sub></div>

---

## 🎮 What is XEngine

XEngine is an **open source, MIT-licensed real-time 2D/3D engine** — runtime, editor and toolchain —
written entirely in modern C# on .NET 10 / CoreCLR. It mirrors the Unity mental model
(GameObject + MonoBehaviour, Scene/Hierarchy/Inspector/Project/Console/Game views, prefabs, `.meta`
GUID assets) so Unity developers can move over without relearning the fundamentals — while staying
small, readable, hackable and free of engine licensing fees.

Two things make it more than a Unity clone:

- **The engine is designed for AI agents as first-class users.** MCP is built into the kernel, not
  bolted on. An agent can read and edit scenes, assets, animation graphs, shaders and code, run the
  project, take screenshots and verify results — through a typed, risk-classified tool surface.
- **Every source asset is human-readable text.** Scenes, prefabs, shader graphs, VFX graphs,
  timelines and settings are text that `git diff` can show and an agent can safely edit.

```csharp
using XEngine.Runtime;
using XEngine.Vector;

public class Spinner : MonoBehaviour
{
    public float DegreesPerSecond = 90f;

    public override void Update()
    {
        Transform.Rotate(new Float3(0f, DegreesPerSecond * Time.DeltaTime, 0f));
    }
}
```

**Scale:** ≈680,000 lines of first-party C# across 3,000+ files, exercised by 6,000+ automated
tests. Full module inventory: **[docs/features.md](docs/features.md)**.

## ✨ Highlights

- 🤖 **AI-native by design** — dual MCP channels: 120+ offline host tools (`xengine-cli`) plus 20+
  in-editor runtime tools (`--serve`), with four-level `RiskLevel`, `dryRun` previews, hash
  pre-checks, idempotency keys and a provenance journal. 40+ authoring skills in `cli/skills/`.
- 📝 **Text-first assets** — `.scene`, `.prefab`, `.shadergraph`, `.vfx`, `.timeline`,
  `.volumeprofile` are all diffable text; merge conflicts are readable and agents can patch
  surgically.
- 🎨 **One RHI, six backends** — D3D12 · Vulkan · OpenGL · Metal · WebGPU · WebGL2 behind a single
  abstraction, with a URP-aligned pipeline (Forward+, STP, SSGI, DDGI realtime GI, Virtual
  Geometry, Shader Graph).
- ⚡ **Measured performance** — zero-allocation `XTask` async, Roslyn analyzers (PR0001–PR0007)
  that make hot-path allocation a compile error, performance contract tests, byte-exact screenshot
  regression.
- 🧱 **Self-contained third party** — companion libraries vendored as source in `ThirdParty/`;
  NuGet dependencies come from nuget.org only — no private feeds.

## 📸 Gallery

| Frame Debugger | Game view + render stats |
|:-:|:-:|
| <img src="docs/readme/frame-debugger.png" width="100%" alt="Frame Debugger" /> | <img src="docs/readme/game-view-stats.png" width="100%" alt="Game view with render stats overlay" /> |
| Event tree, render-target channel preview, per-event render state. | Full-HD game view with draws / batches / tris / lights / shadow-pass breakdown. |

| Spine runtime | Timeline · Package Manager |
|:-:|:-:|
| <img src="docs/readme/spine-demo.png" width="100%" alt="Spine skeletal animation running in the editor" /> | <img src="docs/readme/timeline-packages.jpg" width="100%" alt="Timeline and Package Manager windows" /> |
| Spine skeletal 2D animation playing in the editor. | Multi-track cutscene editing with signals, alongside package management. |

| Animator + animation workflow | Device simulator · backend switching |
|:-:|:-:|
| <img src="docs/readme/animator-window.jpg" width="100%" alt="Animator window docked with scene, game and inspector views" /> | <img src="docs/readme/device-simulator.png" width="100%" alt="In-editor device simulator" /> |
| Animator state machine docked with Scene, Game, Hierarchy and Inspector. | Real-device-resolution preview with safe area; pick a backend in Preferences or `--graphics=vulkan\|d3d12\|opengl`. |

## 🌍 Platform support

| Platform | Status | Notes |
|---|---|---|
| **Windows** | ✅ Shipping | Editor and player; D3D12 / Vulkan / OpenGL |
| **Linux** | ✅ Shipping | Editor and player; Vulkan / OpenGL |
| **macOS** | ✅ Shipping | Editor and player; Metal backend in bring-up |
| **Android** | ✅ Shipping | APK / AAB build and `--build-and-run` deployment, Vulkan with realtime shadows |
| **HarmonyOS** | 🔧 In progress | Vulkan RHI baseline, NAPI host, Hvigor project generation, HAP/APP packaging; compatibility acceptance ongoing |
| **WeChat / Douyin mini games** | 🔧 In progress | Mini-game project generation, WASM loading ABI, subpackage CDN; renders in WeChat DevTools |
| **iOS** | 🔧 In progress | Build pipeline skeleton in place; blocked on the Metal backend |
| **WASM (standalone)** | ⬜ Planned | Depends on the WebGPU backend |

| HarmonyOS 6.1 device | Android arm64 | WeChat DevTools |
|:-:|:-:|:-:|
| <img src="docs/readme/harmonyos-device.jpg" width="100%" alt="XEngine app running on a HarmonyOS 6.1 device" /> | <img src="docs/readme/android-run.png" width="100%" alt="XEngine running on Android with Vulkan and realtime shadows" /> | <img src="docs/readme/wechat-cube.png" width="100%" alt="XEngine rendering in WeChat DevTools" /> |

## 🧱 Architecture

```
XEngine.sln                            # abbreviated — open it for the full project graph
├── XEngine.Runtime/                   # Runtime core — shippable standalone, zero editor dependencies
├── XEngine.Editor/ (+ .UI, .UI.Rendering)   # Editor executable and UI framework
├── XEngine.Runtime.Test/ · XEngine.Editor.Test/ · XHub/ (+ Test)
├── Tools/                             # Analyzers · Ai.Host (MCP CLI) · Ai.Server · Ai.Gateway ·
│                                      # Data.Provenance · PackageRegistry · platform toolchains
├── Packages/                          # 38 first-party packages (com.xengine.*)
├── ThirdParty/                        # Vendored source: Echo · Vector · Paper/Origami · Quill/Scribe ·
│                                      # Clay · Photonic/Unwrapper · Jitter2 · Slang · Rosetta · DotRecast
├── Libraries/                         # Native libraries per RID (FFmpeg, miniaudio)
├── cli/skills/                        # Agent authoring skills
└── docs/                              # Feature tour (features.md) and screenshots
```

## 🚀 Getting started

- [.NET 10 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/10.0) and a GPU with Vulkan,
  D3D12 or OpenGL 4.1+ support (IDE: Visual Studio 17.8+, VS Code or Rider)

```bash
dotnet restore XEngine.sln
dotnet build XEngine.sln -c Debug
dotnet run --project XEngine.Editor                 # or: --graphics=vulkan|d3d12|opengl
```

**Connect an AI agent** — point any MCP client at the offline host (`xengine-cli`), or start the
editor with `--serve` to expose the runtime tools as well. Skills in `cli/skills/` document each
workflow. NuGet dependencies resolve from nuget.org; companion libraries are vendored in
`ThirdParty/` as source.

## 📌 Project status

**V1.0.0f1 — first official open source release.** Runtime, editor, renderer, physics, audio,
animation, UI and asset pipeline are all rewritten and exercised by 6,000+ automated tests. APIs
will still change, some subsystems (Metal, WebGPU/WebGL2, iOS, HarmonyOS acceptance) are in
bring-up, and the ecosystem is young. It is meant for people who want to build with — or on — an
engine they can read end to end.

## 🧭 Roadmap

| Area | Next up |
|---|---|
| **Rendering** | Metal backend (unblocks iOS), SRP batcher, GLES3 mobile tuning, adaptive performance, FSR upscaling |
| **Platforms** | HarmonyOS compatibility acceptance and Beta, mini-game package-size gate, iOS export, standalone WASM |
| **Gameplay** | Animation rigging / runtime IK, behaviour trees |
| **Networking** | Multiplayer runtime (`NetworkManager` / `NetworkBehaviour`, pluggable transports) — design phase |
| **Data** | Addressables-style addressed loading with groups and remote CDN |
| **Tooling** | Presets, quick search, grid & snap, project auditor, recorder, visual scripting, in-editor version control |
| **Performance** | Job system for data parallelism, ECS evaluation, NativeAOT + intrinsics as a Burst equivalent |

Explicitly out of scope: XR/AR/VR, cloud gaming services, legacy deprecated stacks.

## 🤝 Contributing

Contributions are welcome — please read [CONTRIBUTING.md](CONTRIBUTING.md) and
[CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md) first. Before touching code, read the matching skill
guide under `skills/` (LINQ / hot-path / rendering / performance validation). **Do not relax an
analyzer rule or widen an allow-list without benchmark evidence and matching tests.**

## 🙏 Acknowledgments

- **[Prowl Game Engine](https://github.com/ProwlEngine/Prowl)** by Michael Sakharov / Wulferis
  (MIT) — XEngine evolved from Prowl's core architecture and render pipeline.
- **[Anthology](https://github.com/ProwlEngine/Anthology)** by Wulferis (MIT) — Echo, Paper,
  Origami, Quill, Scribe, Clay, Vector, Photonic, Unwrapper, Rosetta and Slang are vendored as
  source under `ThirdParty/`.
- **[Raylib](https://github.com/raysan5/raylib)** — Prowl's early foundation.
- Design references: [YooAsset](https://github.com/tuyoogame/YooAsset) (Apache 2.0) and Unity
  ScriptableBuildPipeline inspired the AssetBundle system's design (independently reimplemented
  under MIT).

Key dependencies: Silk.NET, Vortice.Direct3D12, Jitter Physics 2, Box2D.NET, Magick.NET, Slang,
K4os.Compression.LZ4, DotRecast, spine-csharp. Full notices:
[THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).

Prowl contributors whose work XEngine inherits: Michael (Wulferis) · Abdiel Lopez · Josh Davis ·
ReCore67 · Isaac Marovitz · Kuvrot · JaggerJo · Jihad Khawaja · Jasper Honkasalo · Kai Angulo ·
Bruno Massa · Mark Saba · Chandler Cox · EJTP · Paolo · Kouame Benoit Junior Augustin

## 📜 License

XEngine is distributed under the **MIT License** — see [LICENSE](LICENSE). It derives from Prowl
Game Engine (MIT) and vendors its Anthology companion libraries and the Slang compiler's native
libraries (MIT); upstream notices are preserved in full in
[THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md).

---

<div align="center"><sub>XEngine — built for developers, and for the agents working alongside them.</sub></div>
