<div align="center">

<img src="docs/readme/logo.png" width="128" alt="XEngine logo" />

# XEngine

**AI 原生、类 Unity 的纯 C# / .NET 10 游戏引擎。**

[English](README.md) · **简体中文**

[![Release](https://img.shields.io/badge/release-V1.0.0f1-blue?style=flat-square)](#-项目状态)
[![License](https://img.shields.io/badge/license-MIT-green?style=flat-square)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?style=flat-square&logo=dotnet)](https://dotnet.microsoft.com)
[![Language](https://img.shields.io/badge/language-C%23-239120?style=flat-square)](https://learn.microsoft.com/dotnet/csharp/)
[![Backends](https://img.shields.io/badge/RHI-D3D12%20%C2%B7%20Vulkan%20%C2%B7%20OpenGL%20%C2%B7%20Metal%20%C2%B7%20WebGPU%20%C2%B7%20WebGL2-informational?style=flat-square)](#-图形与渲染)

🎉 **V1.0.0f1 —— MIT 协议正式开源，首个正式版本。**

</div>

---

<img src="docs/readme/editor-overview.png" width="100%" alt="XEngine 编辑器 —— Vulkan 后端、内置 com.xengine.voxelworld 包生成的体素世界" />

<div align="center"><sub>XEngine 编辑器 V1.0.0f1 · Vulkan 后端 · 内置 com.xengine.voxelworld 包生成的体素世界</sub></div>

---

## 🎮 XEngine 是什么

XEngine 是一个**开源（MIT 协议）的实时 2D/3D 引擎** —— 运行时、编辑器与工具链 —— 全部以现代 C#
编写，运行于 .NET 10 / CoreCLR。它刻意对齐 Unity 的心智模型（GameObject + MonoBehaviour、
Scene/Hierarchy/Inspector/Project/Console/Game 视图、预制体、`.meta` GUID 资产），让 Unity 开发者
无需重学基础即可迁移 —— 同时保持小巧、可读、可魔改，且没有引擎授权费。

两件事让它不止是一个 Unity 复制品：

- **引擎把 AI Agent 当作一等公民设计。** MCP 内建于内核而非外挂插件。Agent 可以读取和编辑场景、
  资产、动画图、着色器与代码，运行工程、截图并验证结果 —— 全程经过类型化、风险分级的工具面。
- **所有源资产都是人类可读的文本。** 场景、预制体、Shader Graph、VFX Graph、Timeline 与设置都是
  `git diff` 可显示、Agent 可安全编辑的文本。

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

**规模：** 约 68 万行第一方 C#、3,000+ 个文件，由 6,000+ 项自动化测试覆盖。
完整模块清单见 **[docs/features.zh-CN.md](docs/features.zh-CN.md)**。

## ✨ 核心亮点

- 🤖 **AI 原生设计** —— 双 MCP 通道：120+ 个离线 Host 工具（`xengine-cli`）加 20+ 个编辑器内运行时
  工具（`--serve`），带四级 `RiskLevel`、`dryRun` 预演、哈希预检、幂等键与过程感知日志；
  `cli/skills/` 内置 40+ 份创作技能文档。
- 📝 **文本优先的资产** —— `.scene`、`.prefab`、`.shadergraph`、`.vfx`、`.timeline`、
  `.volumeprofile` 全部是可 diff 的文本；合并冲突可读，Agent 可精准打补丁。
- 🎨 **一套 RHI，六个后端** —— D3D12 · Vulkan · OpenGL · Metal · WebGPU · WebGL2 共享同一抽象层，
  配套对齐 URP 的管线（Forward+、STP、SSGI、DDGI 实时 GI、Virtual Geometry、Shader Graph）。
- ⚡ **实测驱动的性能** —— 零分配 `XTask` 异步、让热路径分配变成编译错误的 Roslyn 分析器
  （PR0001–PR0007）、性能契约测试、字节级截图回归。
- 🧱 **自包含的第三方依赖** —— 配套库以源码内置 `ThirdParty/`；NuGet 依赖仅来自
  nuget.org，无私有包源。

## 📸 画廊

| 帧调试器 | Game 视图 + 渲染统计 |
|:-:|:-:|
| <img src="docs/readme/frame-debugger.png" width="100%" alt="帧调试器" /> | <img src="docs/readme/game-view-stats.png" width="100%" alt="带渲染统计的 Game 视图" /> |
| 事件树、渲染目标通道预览、逐事件渲染状态。 | 全高清 Game 视图，含 draws / batches / tris / 灯光 / 阴影 Pass 分解。 |

| Spine 运行时 | Timeline · 包管理器 |
|:-:|:-:|
| <img src="docs/readme/spine-demo.png" width="100%" alt="编辑器中运行的 Spine 骨骼动画" /> | <img src="docs/readme/timeline-packages.jpg" width="100%" alt="Timeline 与包管理器窗口" /> |
| 编辑器中播放的 Spine 骨骼 2D 动画。 | 带信号的多轨过场编辑，旁边是包管理器。 |

| Animator + 动画工作流 | 设备模拟器 · 后端切换 |
|:-:|:-:|
| <img src="docs/readme/animator-window.jpg" width="100%" alt="Animator 窗口与场景、Game、Inspector 停靠" /> | <img src="docs/readme/device-simulator.png" width="100%" alt="编辑器内设备模拟器" /> |
| Animator 状态机与 Scene、Game、Hierarchy、Inspector 停靠。 | 真机分辨率预览带安全区；在偏好设置或 `--graphics=vulkan\|d3d12\|opengl` 切换后端。 |

## 🌍 平台支持

| 平台 | 状态 | 说明 |
|---|---|---|
| **Windows** | ✅ 已发布 | 编辑器与播放器；D3D12 / Vulkan / OpenGL |
| **Linux** | ✅ 已发布 | 编辑器与播放器；Vulkan / OpenGL |
| **macOS** | ✅ 已发布 | 编辑器与播放器；Metal 后端接入中 |
| **Android** | ✅ 已发布 | APK / AAB 构建与 `--build-and-run` 部署，Vulkan 实时阴影 |
| **鸿蒙 HarmonyOS** | 🔧 进行中 | Vulkan RHI 基线、NAPI 宿主、Hvigor 工程生成、HAP/APP 打包；兼容性验收进行中 |
| **微信 / 抖音小游戏** | 🔧 进行中 | 小游戏工程生成、WASM 加载 ABI、分包 CDN；微信开发者工具中已可渲染 |
| **iOS** | 🔧 进行中 | 构建管线骨架就绪；等待 Metal 后端 |
| **WASM（独立）** | ⬜ 规划中 | 依赖 WebGPU 后端 |

| 鸿蒙 6.1 真机 | Android arm64 | 微信开发者工具 |
|:-:|:-:|:-:|
| <img src="docs/readme/harmonyos-device.jpg" width="100%" alt="XEngine 应用运行于鸿蒙 6.1 设备" /> | <img src="docs/readme/android-run.png" width="100%" alt="XEngine 运行于 Android（Vulkan + 实时阴影）" /> | <img src="docs/readme/wechat-cube.png" width="100%" alt="微信开发者工具中的 XEngine 渲染" /> |

## 🧱 架构

```
XEngine.sln                            # 缩略图 —— 打开可见完整工程图
├── XEngine.Runtime/                   # 运行时核心 —— 可独立发布，零编辑器依赖
├── XEngine.Editor/ (+ .UI, .UI.Rendering)   # 编辑器可执行程序与 UI 框架
├── XEngine.Runtime.Test/ · XEngine.Editor.Test/ · XHub/ (+ Test)
├── Tools/                             # Analyzers · Ai.Host (MCP CLI) · Ai.Server · Ai.Gateway ·
│                                      # Data.Provenance · PackageRegistry · 平台工具链
├── Packages/                          # 38 个第一方包（com.xengine.*）
├── ThirdParty/                        # 内置源码：Echo · Vector · Paper/Origami · Quill/Scribe ·
│                                      # Clay · Photonic/Unwrapper · Jitter2 · Slang · Rosetta · DotRecast
├── Libraries/                         # 按 RID 的原生库（FFmpeg、miniaudio）
├── cli/skills/                        # Agent 创作技能
└── docs/                              # 功能清单（features.zh-CN.md）与截图
```

## 🚀 快速开始

- [.NET 10 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/10.0) 与支持 Vulkan、D3D12 或
  OpenGL 4.1+ 的 GPU（IDE：Visual Studio 17.8+、VS Code 或 Rider）

```bash
dotnet restore XEngine.sln
dotnet build XEngine.sln -c Debug
dotnet run --project XEngine.Editor                 # 或：--graphics=vulkan|d3d12|opengl
```

**接入 AI Agent** —— 把任意 MCP 客户端指向离线 Host（`xengine-cli`），或以 `--serve` 启动编辑器
同时开放运行时工具。`cli/skills/` 记录了每个工作流。NuGet 依赖来自 nuget.org；配套库以源码形式
内置 `ThirdParty/`。

## 📌 项目状态

**V1.0.0f1 —— 首个正式开源版本。** 运行时、编辑器、渲染器、物理、音频、动画、UI 与资产管线均已
完整重写，由 6,000+ 项自动化测试覆盖。API 仍会变化，部分子系统（Metal、WebGPU/WebGL2、iOS、鸿蒙
兼容性验收）仍在接入中，生态还很年轻。它面向愿意用一个自己能从头读到尾的引擎去做游戏 —— 或者去改
引擎本身 —— 的人。

## 🧭 路线图

| 领域 | 下一步 |
|---|---|
| **渲染** | Metal 后端（解锁 iOS）、SRP batcher、GLES3 移动端调优、自适应性能、FSR 超分 |
| **平台** | 鸿蒙兼容性验收与 Beta、小游戏包体闸门、iOS 导出、独立 WASM |
| **玩法** | 动画绑定 / 运行时 IK、行为树 |
| **网络** | 多人运行时（`NetworkManager` / `NetworkBehaviour`、可插拔传输层）—— 设计阶段 |
| **数据** | Addressables 风格的编址加载（分组与远程 CDN） |
| **工具** | Presets、快速搜索、Grid & Snap、Project Auditor、Recorder、可视化脚本、编辑器内版本控制 |
| **性能** | 面向数据并行的 Job 系统、ECS 评估、NativeAOT + 内建函数作为 Burst 等价物 |

明确不做：XR/AR/VR、云游戏服务、遗留废弃技术栈。

## 🤝 参与贡献

欢迎贡献 —— 请先阅读 [CONTRIBUTING.md](CONTRIBUTING.md) 与
[CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md)。改代码前，请先读 `skills/` 下对应领域的技能指南
（LINQ / 热路径 / 渲染 / 性能验证）。**没有 benchmark 证据与配套测试，不允许放宽分析器规则或扩大
允许清单。**

## 🙏 致谢

- **[Prowl Game Engine](https://github.com/ProwlEngine/Prowl)**，作者 Michael Sakharov / Wulferis
  （MIT）—— XEngine 基于 Prowl 的核心架构与渲染管线演进而来。
- **[Anthology](https://github.com/ProwlEngine/Anthology)**，作者 Wulferis（MIT）—— Echo、Paper、
  Origami、Quill、Scribe、Clay、Vector、Photonic、Unwrapper、Rosetta、Slang 以源码形式内置
  `ThirdParty/`。
- **[Raylib](https://github.com/raysan5/raylib)** —— Prowl 早期的基础。
- 设计参考：[YooAsset](https://github.com/tuyoogame/YooAsset)（Apache 2.0）与 Unity
  ScriptableBuildPipeline 启发了 AssetBundle 系统的设计（MIT 下独立重实现）。

主要依赖：Silk.NET、Vortice.Direct3D12、Jitter Physics 2、Box2D.NET、Magick.NET、Slang、
K4os.Compression.LZ4、DotRecast、spine-csharp。完整声明见
[THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。

XEngine 继承其工作的 Prowl 贡献者：Michael (Wulferis) · Abdiel Lopez · Josh Davis · ReCore67 ·
Isaac Marovitz · Kuvrot · JaggerJo · Jihad Khawaja · Jasper Honkasalo · Kai Angulo · Bruno Massa ·
Mark Saba · Chandler Cox · EJTP · Paolo · Kouame Benoit Junior Augustin

## 📜 许可

XEngine 以 **MIT 协议**发布 —— 见 [LICENSE](LICENSE)。它衍生自 Prowl Game Engine（MIT），并内置其
Anthology 配套库与 Slang 编译器原生库（MIT）；上游声明完整保留于
[THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。

---

<div align="center"><sub>XEngine —— 为开发者，也为与开发者并肩工作的 Agent 而生。</sub></div>
