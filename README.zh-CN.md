<div align="center">

<img src="docs/readme/logo.png" width="128" alt="XEngine logo" />

# XEngine

**用纯 C# / .NET 10 打造的 AI 原生、类 Unity 游戏引擎。**

[English](README.md) · **简体中文**

[![Status](https://img.shields.io/badge/status-%CE%B10.1%20preview-orange?style=flat-square)](#-项目状态)
[![License](https://img.shields.io/badge/license-MIT-green?style=flat-square)](LICENSE)
[![.NET](https://img.shields.io/badge/.NET-10.0-512BD4?style=flat-square&logo=dotnet)](https://dotnet.microsoft.com)
[![Language](https://img.shields.io/badge/language-C%23-239120?style=flat-square)](https://learn.microsoft.com/dotnet/csharp/)
[![Backends](https://img.shields.io/badge/RHI-D3D12%20%C2%B7%20Vulkan%20%C2%B7%20OpenGL%20%C2%B7%20Metal%20%C2%B7%20WebGPU%20%C2%B7%20WebGL2-informational?style=flat-square)](#-图形与渲染)

</div>

---

> ## 🚧 α0.1 preview — 即将开源
>
> XEngine 正在准备首个公开版本 **α0.1 preview**。这份 README 是先于代码发布的引擎导览：
> 已经建成什么、验证到什么程度、还有什么在推进中。
> α 阶段之间可能存在破坏性变更 —— API 尚未冻结。

---

<img src="docs/readme/editor-overview.png" width="100%" alt="XEngine 编辑器 —— Vulkan 后端、程序化天空盒、实时光照" />

<div align="center"><sub>XEngine 编辑器 · Vulkan 后端 · 程序化天空盒 + 实时光照 · 121 FPS / 8.2 ms</sub></div>

---

## 📑 目录

1. [XEngine 是什么](#-xengine-是什么)
2. [为什么是 XEngine](#-为什么是-xengine)
3. [截图一览](#-截图一览)
4. [已完成模块](#-已完成模块)
5. [图形与渲染](#-图形与渲染)
6. [第一方包](#-第一方包)
7. [平台支持](#-平台支持)
8. [AI 原生工具层（MCP）](#-ai-原生工具层mcp)
9. [架构](#-架构)
10. [性能文化](#-性能文化)
11. [快速开始](#-快速开始)
12. [项目状态](#-项目状态)
13. [路线图](#-路线图)
14. [参与贡献](#-参与贡献)
15. [致谢](#-致谢)
16. [许可协议](#-许可协议)

---

## 🎮 XEngine 是什么

XEngine 是一款 **开源、MIT 协议的实时 2D/3D 引擎** —— 包含运行时、编辑器与完整工具链 ——
完全使用 **现代 C# / .NET 10 / CoreCLR** 编写。它有意沿用 Unity 的心智模型
（GameObject + MonoBehaviour，Scene / Hierarchy / Inspector / Project / Console / Game 视图，
Prefab，基于 GUID 的 `.meta` 资产），让熟悉 Unity 的开发者无需重学基础即可迁移过来；
同时保持小巧、可读、可魔改，且没有任何引擎授权费用。

它不只是一个 Unity 克隆，有两点根本差异：

- **引擎从架构层就把 AI 智能体当作一等用户。** MCP（Model Context Protocol）内建于内核，
  而不是以插件形式外挂。Agent 可以读写场景、资产、动画图、着色器与代码，运行工程、截图并验收结果 ——
  全部通过一套带类型、带风险分级的工具面完成。
- **所有源资产都是人类可读的文本。** 场景、Prefab、Shader Graph、VFX Graph、Timeline 与设置
  都是 `git diff` 能直接看懂、Agent 能安全编辑的文本；二进制文件只作为导入缓存存在。

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

**当前代码规模：** 运行时、编辑器、包与工具链合计约 **35.3 万行** 第一方 C#（1,568 个文件），
另有约 10.7 万行测试代码，以及约 37.3 万行以源码形式内置在仓库中的第三方库。
**4,000+ 个 xUnit 测试方法** 覆盖运行时、编辑器、分析器与 AI Host。

---

## ✨ 为什么是 XEngine

<table>
<tr>
<td width="25%"><b>🤖 AI 原生设计</b></td>
<td>MCP 双通道：<b>105 个离线 Host 工具</b>（场景 / 资产 / 动画 / 导航 / Shader Graph / VFX /
代码 / 构建 / 生成）+ <b>29 个编辑器运行时工具</b>（播放模式、实时场景、帧调试、截图、统计）。
内置 <b>36 份创作技能文档</b>，让 Agent 读完就会用。所有写操作都带四级
<code>RiskLevel</code>、<code>dryRun</code> 预演、哈希前置校验与幂等键，并写入过程感知日志。</td>
</tr>
<tr>
<td><b>📝 文本化资产优先</b></td>
<td>所有源资产 —— <code>.scene</code>、<code>.prefab</code>、<code>.shadergraph</code>、
<code>.vfx</code>、<code>.timeline</code>、<code>.volumeprofile</code> —— 均为可 diff 的文本
（Echo 序列化器 + 源生成器）。合并冲突看得懂，Agent 也能做外科手术式的精确修改。</td>
</tr>
<tr>
<td><b>⚡ 实测驱动的性能</b></td>
<td>零分配异步库（<code>XTask</code>：同步完成 0.93 ns / 0 B）；Roslyn 分析器
（PR0001–PR0007）把热路径上的 GC 分配变成<i>编译错误</i>；BenchmarkDotNet 证据随仓库归档；
截图逐字节回归验证。</td>
</tr>
<tr>
<td><b>🧱 自包含构建</b></td>
<td>配套基础库与 Slang 原生编译器均以源码形式内置在 <code>ThirdParty/</code>。
<code>dotnet build</code> 可完全离线 restore 与构建，无需任何私有源或外部包服务器。</td>
</tr>
</table>

---

## 📸 截图一览

| 帧调试器 Frame Debugger | Game 视图 + 渲染统计 |
|:-:|:-:|
| <img src="docs/readme/frame-debugger.png" width="100%" alt="帧调试器" /> | <img src="docs/readme/game-view-stats.png" width="100%" alt="带渲染统计的 Game 视图" /> |
| 事件树、渲染目标通道预览、逐事件渲染状态、网格、Keywords、纹理与常量缓冲。 | Full-HD Game 视图，Draws / Batches / 三角面 / 光源 / 阴影分区明细。 |

| Spine 运行时 | Timeline · 包管理器 · Profiler |
|:-:|:-:|
| <img src="docs/readme/spine-demo.png" width="100%" alt="编辑器中运行的 Spine 骨骼动画" /> | <img src="docs/readme/timeline-packages.jpg" width="100%" alt="Timeline、Package Manager 与 Profiler 窗口" /> |
| Spine 2D 骨骼动画，含动画剪辑列表、换肤与 Inspector 实时预览。 | 多轨道过场编排（含信号），同屏展示包管理与性能分析器。 |

| Animator 与动画工作流 | 设备模拟器 |
|:-:|:-:|
| <img src="docs/readme/animator-window.jpg" width="100%" alt="与场景、Game、Inspector 停靠在一起的 Animator 窗口" /> | <img src="docs/readme/device-simulator.png" width="100%" alt="编辑器内设备模拟器" /> |
| Animator 状态机窗口与 Scene、Game、Hierarchy、Inspector、Console 一同停靠。 | 按真实设备分辨率预览，支持安全区与横竖屏切换。 |

| 图形后端切换 |
|:-:|
| <img src="docs/readme/backend-preferences.png" width="100%" alt="编辑器 Preferences 中的图形后端选择" /> |
| 一套 RHI，多个后端 —— 在 Preferences 里切换，或启动时用 `--graphics=opengl\|vulkan\|d3d12` 覆盖。 |

---

## 🧩 已完成模块

本节列出的能力均已实现，并由测试和/或仓库内归档的截图证据覆盖。

### 核心运行时与编辑器

| 模块 | 能力 |
|---|---|
| **对象模型** | GameObject + MonoBehaviour 组件、Tags & Layers、`XEngine Actions`（可在 Inspector 配置的持久事件回调）、完整生命周期语义 |
| **场景与 Prefab** | 带雾效与环境光的场景系统；嵌套 Prefab，支持 Apply / Revert / Break Instance 与覆盖追踪 |
| **编辑器** | Scene View、Hierarchy、Inspector、Project Browser、Console、Game View、Profiler、Package Manager、Preferences、Build Profiles；可停靠可缩放面板与布局持久化；拖放；多选；搜索过滤；资产缩略图与 3D 预览；变换 Gizmo；完整 Undo/Redo；可重绑定快捷键；编辑器主题；编辑器内 Playtest；脚本热重载 |
| **本地化** | 编辑器 UI 支持 12 种语言（英、德、西、法、意、日、韩、波兰、葡、俄、土、中） |
| **脚本** | C# / .NET 10 / CoreCLR；`dotnet build` 编译 Game 与 Editor 程序集；Assembly Definitions；托管与原生插件 |
| **XTask** | 零分配异步 / 协程库，替代 `Task` 与 `IEnumerator` |
| **序列化** | Echo 文本序列化器 + Roslyn 源生成器 |
| **输入** | Input Action 系统，含 `.inputactions` 资产与专用编辑器：动作阶段、复合绑定（WASD → Float2、D-pad）、处理器；支持键盘、鼠标、手柄 |
| **数学库** | 64 位数学库 —— `Float4x4`、四元数、`Transform2D`、AABB、Bounds、Frustum、Cone、Ray、Plane、LineSegment、Rect |

### 游戏玩法系统

| 模块 | 能力 |
|---|---|
| **3D 物理** | 刚体，Box / Sphere / Capsule / Cylinder / Cone / 凸包 / Mesh / Terrain 碰撞体，带悬挂与抓地滑移的车轮碰撞器，关节与约束（球铰、铰链、铰链角、固定角、锥角限制、距离限制、扭转角、棱柱、万向、点在线上、点在面上、角电机、直线电机），角色控制器，触发体，层过滤，射线与形状查询 |
| **2D 物理** | 每场景独立的 `PhysicsWorld2D`（1 单位 = 1 米，FixedUpdate 步进）、`Rigidbody2D`、Box / Circle / Capsule / Polygon / Composite 碰撞体、碰撞与触发事件、`PhysicsMaterial2D`、NonAlloc `Raycast` / `OverlapCircle` / `OverlapBox`、调试绘制 |
| **动画** | 骨骼动画与 Blendshape；Animator Controller 状态机，支持多层、AvatarMask、Root Motion、CrossFade 与混合树；Humanoid 骨骼映射与重定向；含曲线与事件的动画剪辑编辑器 |
| **Playable 与 Timeline** | 对齐 Unity 的 `PlayableGraph` / `AnimationPlayableOutput` / 自定义 Playable；可视化 Timeline 编辑器，含轨道、片段、混合与信号 |
| **导航** | Recast/Detour 风格的 NavMesh 烘焙，`NavMeshSurface` / `NavMeshAgent` / `NavMeshObstacle` / `NavMeshLink` / 修饰器与体积，动态重建的 Tile Cache，群体（Crowd）模拟，路径查询与 Navigation 窗口 |
| **音频** | 空间 3D 音频，带衰减与多普勒；WAV / MP3 / OGG / FLAC；效果链（Delay、Distortion、Biquad Filter、Reverb、Phaser）与自定义 `IAudioEffect` |
| **UI** | 基于 GameObject 的 UI，含 World Space；`RectTransform` 布局、按钮、滑动条、布局组、拖放事件；**XGUI** retained-mode 包，支持富文本、超链接、行内 Sprite、安全区、IME 输入与批次合并 |
| **2D** | Sprite、Sprite Atlas、Sorting Group、九宫格绘制模式，Tilemap（含调色板与 Rule Tile），SpriteShape 样条，Pixel Perfect 相机，2D 骨骼动画（SpriteSkin / Bone2D / Sprite Library / IK），Spine 运行时，PSD 与 Aseprite 导入器 |
| **地形** | 四叉树 LOD 高度图地形、Splatmap 绘制、GPU 实例化草地、带 LOD 距离的树木渲染、洞穴，以及专用的高度 / 绘制 / 草 / 树 / 设置编辑器 |
| **VFX** | 节点式 GPU Visual Effect Graph（Spawn / Initialize / Update / Output 块；Flipbook、Mesh 输出、受光粒子、SDF 与平面碰撞、湍流、软粒子、子图），以及经典 CPU 粒子系统（GPU 实例化渲染） |

### 内容管线

| 模块 | 能力 |
|---|---|
| **资产数据库** | 基于 GUID 的引用与 `.meta` 文件、导入缓存与文件监视自动重导入、通过特性扩展的自定义导入器、确定性 GUID 的子资产、正向与反向依赖追踪、多线程加载 |
| **支持格式** | 模型：GLTF / GLB / OBJ / FBX · 纹理：PNG / JPG / BMP / TGA / PSD / HDR / DDS / EXR · 音频：WAV / MP3 / OGG / FLAC · 2D：Aseprite、带骨骼的分层 PSD |
| **AssetBundle 与热更新** | Bundle 构建管线、Provider/Handle 双层引用计数、清单版本流、带资源流式加载的虚拟文件系统、LZ4 压缩、下载器与解密分层 |
| **构建系统** | 独立应用导出、打包资产文件（`.xenginepak`）、仅导出用到的资产、每平台构建配置、Build Profiles 界面 |
| **过程溯源** | 每一次编辑都把 author / intent / delta 写入可查询的日志 —— 人和 Agent 都能回溯 |

---

## 🎨 图形与渲染

**一套 RHI 抽象层下的六个图形后端**：Direct3D 12 · Vulkan · OpenGL · Metal · WebGPU · WebGL2。
其中 Vulkan、D3D12、OpenGL 是当前已完整验收的桌面路径；Metal、WebGPU、WebGL2 处于接入调优中
（见[平台支持](#-平台支持)）。

- **专用渲染线程**，可扩展的可编程渲染管线
- **Universal Render Pipeline (URP)** —— `PipelineAsset` / `ScriptableRenderer` / `RendererFeature` /
  Volume / RenderGraph / RTHandle / 相机堆叠，对齐 URP 17.7（Forward+、STP、SSGI、APV、LOD CrossFade）
- **渲染路径** —— Forward-Lit、Forward+、Deferred GBuffer、Renderer2D
- **RenderGraph** —— 瞬时资源 + Raster / Compute / Unsafe Pass
- **实时全局光照** —— 屏幕空间 GI + DDGI 动态辐照探针体，六后端全路径验收
- **Virtual Geometry** —— Nanite 类方案：GPU 剔除、簇选择、驻留管理
- **Shader Graph** —— 节点编辑器 + CLI `inspect` / `patch` / `compile`，输出 GLSL 与 Slang
- **自定义着色器语言** —— 支持 `#include`、多 Pass、Shader Keywords 与变体
- **光照** —— HDR + PBR 金属度工作流（Albedo、Normal、Surface AO/粗糙度/金属度、Emission），
  点光 / 聚光 / 方向光，所有光源类型支持阴影贴图，最多 4 级级联，点光立方体阴影，
  动态打包的阴影图集，Light Cookie，Rendering Layers，反射探针，烘焙光照探针，
  渐进式 CPU 光照贴图烘焙与 UV 展开
- **后处理** —— ACES / Reinhard / Uncharted / Filmic / Melon / AgX 色调映射，Bloom，
  FXAA / SMAA / TAA，GTAO，随机屏幕空间反射（SSR），Bokeh 景深，体积雾，Lens Flare，
  屏幕空间阴影，屏幕空间贴花，电影效果（颗粒 / 暗角 / 色差）；
  另有可选 Post Processing 包，提供 PPv2 风格的 Layer / Volume / Profile 工作流
- **Volume 框架** —— 通过 `.volumeprofile` 资产做全局与局部混合
- **渲染器** —— Mesh、Skinned Mesh（骨骼 + Blendshape）、Line、Sprite，GPU 实例化，视锥剔除，
  带 Stencil 的 LOD CrossFade
- **其他** —— Render Texture、Texture3D、Grab Pass（深度感知折射 / 热浪 / 磨砂玻璃）、透明、
  程序化 / 立方体 / 渐变 Skybox、STP 时域升采样、MSAA
- **调试** —— Frame Debugger（事件树、RT 预览、完整状态检视）与 Rendering Debugger

---

## 📦 第一方包

核心 2D 与渲染基础能力内置；下列包为可选，可在编辑器内的 Package Manager 中安装。

| 包 | 提供什么 |
|---|---|
| `com.xengine.xgui` | Retained-mode 的 GameObject UI |
| `com.xengine.xui` | 构建于 XGUI 之上的 AI 优先 UI 应用层 —— 紧凑的 `.xui` XML 界面 |
| `com.xengine.timeline` | 基于 PlayableGraph 运行时的过场 / 序列编辑器 |
| `com.xengine.vfx` | 节点式 GPU Visual Effect Graph |
| `com.xengine.terrain` | 高度图地形，含 Splat 图层、草、树与雕刻工具 |
| `com.xengine.ai.navigation` | NavMesh 烘焙、查询、Agent 与 Navigation 窗口 |
| `com.xengine.spine` | Esoteric Spine 2D 骨骼动画运行时 |
| `com.xengine.postprocessing` | PPv2 风格的后处理 Layer / Volume / Profile |
| `com.xengine.feature.2d` | 元包，一次性引入全部第一方 2D 子包 |
| `com.xengine.2d.*` | `common`、`sprite`、`tilemap`、`tilemap.extras`、`animation`、`spriteshape`、`pixel-perfect`、`psdimporter`、`aseprite`、`tooling` |

---

## 🌍 平台支持

| 平台 | 状态 | 说明 |
|---|---|---|
| **Windows** | ✅ 可用 | 编辑器与 Player；D3D12 / Vulkan / OpenGL |
| **Linux** | ✅ 可用 | 编辑器与 Player；Vulkan / OpenGL |
| **macOS** | ✅ 可用 | 编辑器与 Player；Metal 后端接入中 |
| **Android** | ✅ 可用 | APK / AAB 构建与 `--build-and-run` 真机/模拟器部署，Vulkan + 实时阴影，ETC2 纹理变体 |
| **HarmonyOS 鸿蒙** | 🔧 推进中 | 平台层与打包管线已完成 —— Vulkan RHI 基线、NAPI 原生宿主、Hvigor 工程生成、HAP/APP 打包、设备桥。打包应用已在 HarmonyOS 6.1 真机跑通 3D 渲染 Demo；兼容性矩阵验收推进中。 |
| **Web · 微信 / 抖音小游戏** | 🔧 推进中 | 导出管线已打通：小游戏工程生成、WXWebAssembly / TTWebAssembly 加载 ABI、分包 CDN、包体预算门禁。微信开发者工具已渲染出受光 3D 场景；主包体积优化进行中。 |
| **iOS** | 🔧 推进中 | 构建管线骨架已就绪，等待 Metal 后端 |
| **WASM（独立）** | ⬜ 规划中 | 依赖 WebGPU 后端 |

| HarmonyOS 6.1 真机 | Android arm64（Vulkan + 实时阴影） | 微信开发者工具 |
|:-:|:-:|:-:|
| <img src="docs/readme/harmonyos-device.jpg" width="100%" alt="XEngine 打包应用在 HarmonyOS 6.1 真机运行" /> | <img src="docs/readme/android-run.png" width="100%" alt="XEngine 在 Android 上以 Vulkan 渲染实时阴影" /> | <img src="docs/readme/wechat-cube.png" width="100%" alt="XEngine 在微信开发者工具中渲染" /> |

---

## 🤖 AI 原生工具层（MCP）

XEngine 通过 **stdio 上的 MCP** 把自身暴露给 AI 智能体与 CLI，分两条通道：

- **离线 Host —— 105 个工具。** 无需编辑器运行，直接作用于磁盘上的工程：场景与 Prefab 编辑，
  资产导入 / 搜索 / 检视 / 修改，Animator 状态机、混合树、AvatarMask、剪辑曲线与事件，
  NavMesh，Shader Graph，VFX Graph，Volume，材质，脚本创建 / 编辑 / 删除，代码符号检索，
  包管理，构建，Headless 运行，跑测试，工作流，以及生成网关。
- **编辑器运行时 —— 29 个工具。** 需要编辑器以 `--serve` 运行：播放模式控制、实时场景修改、
  选择、菜单、表达式求值、截图与面板截图、渲染统计、帧调试、渲染调试、Animator 与动画预览、
  NavMesh 烘焙与 Agent 控制、Spine 播放与换肤、VFX 预览。

**安全护栏是协议的一部分**，而非事后补丁：能力目录带四级 `RiskLevel`、`dryRun` 预演、
针对并发编辑的哈希前置校验、幂等键，以及记录每次变更 author / intent / delta 的过程感知日志。

**36 份创作技能文档**（`cli/skills/`）覆盖每一类工作流 —— 编辑场景、Animator、Shader Graph、
VFX Graph、Timeline、材质、Volume、管线、XUI 界面；检视帧与渲染状态；生成资产；
跑测试、工作流与实时场景 —— Agent 读完技能就能一次做对。

**生成式资产网关** —— `gen_submit` / `gen_status` / `gen_fetch` / `gen_candidates` 等工具，
对接六家提供商：**Meshy**（3D 模型）、**PixelLab**（精灵）、**Blockade Labs**（天空盒）、
**ElevenLabs**（音效）、**OpenAI GPT Image**，以及自托管的 **ComfyUI**。
生成配方会写入资产的 `.meta`，任何资产都可复现地重新生成。

---

## 🧱 架构

```
XEngine.sln
├── XEngine.Runtime/              # 运行时核心 —— 可独立发布，零编辑器依赖
├── XEngine.Editor/               # 编辑器可执行程序，构建于 Origami/Paper 之上
├── XEngine.Runtime.Test/         # 运行时测试
├── XEngine.Editor.Test/          # 编辑器测试
├── Tools/
│   ├── XEngine.Ai.Host/          # 离线 MCP Host（105 个工具）
│   ├── XEngine.Ai.Server/        # 编辑器内 MCP 服务（29 个运行时工具）
│   ├── XEngine.Ai.Gateway/       # 生成式资产网关（6 家提供商）
│   ├── XEngine.Analyzers/        # 性能分析器（PR0001–PR0007）
│   ├── XEngine.Data.Provenance/  # 过程感知日志
│   └── XEngine.Runtime.Benchmarks/
├── Packages/                     # 第一方包（com.xengine.*）
├── Libraries/                    # 按 RID 组织的平台原生库（miniaudio 等）
├── ThirdParty/                   # 内置源码 —— 无需任何外部包源
│   ├── Echo/                     # 序列化（含源生成器）
│   ├── Vector/                   # 64 位数学库
│   ├── Paper/ Origami/           # 即时模式 UI 框架 + 编辑器组件库
│   ├── Quill/ Scribe/            # 矢量图形 + TrueType 解析与字形栅格化
│   ├── Clay/                     # 模型导入（GLTF/GLB/OBJ/FBX）
│   ├── Photonic/ Unwrapper/      # 渐进式光照贴图烘焙 + UV 展开
│   ├── Jitter2/                  # 3D 物理
│   ├── DotRecast/                # 导航网格生成与寻路
│   ├── Slang/ Graphite/          # 着色器编译器封装 + Vulkan 抽象
│   ├── Rosetta/ Wicked/          # 本地化 + 网络（IL weaved RPC 与 SyncVar）
│   └── Crumb/ Drift/             # 工具库
├── cli/skills/                   # 36 份 Agent 创作技能文档
├── Samples/                      # 示例工程
└── docs/                         # 文档、截图、benchmark 证据
```

---

## 📊 性能文化

这里的性能决策一律来自 **实测的帧时间、吞吐量、内存分配与 GPU 开销** —— 而不是直觉。
仓库内建了强制手段：

- **`XEngine.Analyzers`（PR0001–PR0007）** —— 针对每帧 CPU 热路径的 Roslyn 分析器：禁用 LINQ、
  `[HotPath]` 检测、分配检测。没有 benchmark 证据与配套测试，就不允许放宽规则或扩大允许清单。
- **`XEngine.Runtime.Benchmarks`** —— BenchmarkDotNet 基准套件，可复现证据归档在 `docs/benchmarks/`。
- **性能契约测试** —— 例如 `FrameLoopPerformanceContractTests`、`StageAPerformanceContractTests`，
  以及可以把分配字节数逐轮精确复现的零分配探针。
- **跨图形后端的截图逐字节回归** —— 任何改变像素的渲染改动都不可能悄悄合入。

代表性实测：`XTask` 同步完成 **0.93 ns / 0 B**；Transform 六条路径 **0 B** 分配；
编辑器空闲占用 **单核 0.365%**（空场景、空闲态、Release + Vulkan、30 秒采样）。

---

## 🚀 快速开始

### 前置要求

- [.NET 10 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/10.0)
- 支持 Vulkan、D3D12 或 OpenGL 4.1+ 的显卡
- 推荐 IDE：Visual Studio 17.8+、VS Code 或 JetBrains Rider

### 构建与运行

```bash
dotnet restore XEngine.sln
```

```bash
dotnet build XEngine.sln -c Debug
```

```bash
dotnet run --project XEngine.Editor
```

启动时指定图形后端：

```bash
dotnet run --project XEngine.Editor -- --graphics=vulkan
```

> 配套基础库与 Slang 原生编译器均已内置在 `ThirdParty/`，因此 restore 与构建可完全离线完成 ——
> 无需私有源，也无需额外的包服务器。

### 接入 AI 智能体

把任意 MCP 客户端指向离线 Host；若还需要运行时工具，用 `--serve` 启动编辑器即可。
`cli/skills/` 下的技能文档描述了每一类工作流。

---

## 📌 项目状态

**α0.1 preview。** 运行时、编辑器、渲染器、物理、音频、动画、UI 与资产管线均已完整重写，
并由 4,000+ 项自动化测试覆盖。这是一个预览版本：API 仍会变化，部分子系统
（Metal、WebGPU/WebGL2、iOS、鸿蒙兼容性验收）仍在接入中，围绕引擎的生态还很年轻。
它面向的是愿意用一个自己能从头读到尾的引擎去做游戏 —— 或者去改引擎本身 —— 的人。

---

## 🧭 路线图

长期目标是覆盖现代通用引擎的核心功能集，让既有项目能以较低成本迁移，同时保留 XEngine 自己的
风格（Paper UI、XTask、Echo、MCP）。接下来的重点：

| 领域 | 下一步 |
|---|---|
| **渲染** | Metal 后端（解锁 iOS）、SRP Batcher、GLES3 移动端调优、Adaptive Performance、FSR 升采样 |
| **平台** | 鸿蒙兼容性矩阵验收与 Beta、小游戏包体门禁达标、iOS 出包、独立 WASM |
| **玩法** | Cinemachine 式虚拟摄像机、Animation Rigging / 运行时 IK、行为树 |
| **网络** | 把已内置的网络库挂入运行时 —— `NetworkManager` / `NetworkBehaviour` / NetworkTransform、可插拔 Transport、多实例联机测试 |
| **数据** | Addressables 风格的地址化加载，含分组与远程 CDN |
| **工具** | Presets、Quick Search、Grid & Snap、Project Auditor、Recorder、可视化脚本、编辑器内版本控制 |
| **性能** | 数据并行的 Job System、ECS 方案评估、以 NativeAOT + Intrinsics 作为 Burst 等价物 |

明确不做：XR/AR/VR、云游戏服务，以及已废弃的历史技术栈。

---

## 🤝 参与贡献

欢迎贡献。请先阅读 [CONTRIBUTING.md](CONTRIBUTING.md) 与 [CODE_OF_CONDUCT.md](CODE_OF_CONDUCT.md)。

改动代码前，请按改动领域阅读对应的技能指南（`skills/`）：

- **LINQ 性能**（`skills/linq-performance/`）—— PR0001 与 LINQ 允许清单
- **热路径性能**（`skills/hot-path-performance/`）—— `[HotPath]`、PR0002–PR0007、池化
- **渲染与 GPU**（`skills/rendering-gpu-performance/`）—— 渲染管线、RHI、GPU 资源
- **性能验证**（`skills/performance-validation/`）—— benchmark、回归验证、证据归档

**不要在没有 benchmark 证据与配套测试的情况下放宽分析器规则或扩大允许清单。**

---

## 🙏 致谢

### 血缘

XEngine 站在巨人的肩膀上：

- **[Prowl Game Engine](https://github.com/ProwlEngine/Prowl)**，作者 **Michael Sakharov / Wulferis**
  （MIT 协议）。XEngine 基于 Prowl 的核心架构与渲染管线演进而来。Prowl 版权声明：
  `MIT License — Copyright (c) 2023 Michael Sakharov`。
- **[Anthology](https://github.com/ProwlEngine/Anthology)**，作者 **Wulferis**（MIT 协议）——
  Echo、Paper、Origami、Quill、Scribe、Clay、Vector、Photonic、Unwrapper、Rosetta、Wicked、
  Graphite、Slang、Crumb、Drift，以源码形式内置在 `ThirdParty/`。
  `Copyright (c) 2026 Wulferis`。
- **[Raylib](https://github.com/raysan5/raylib)** —— Prowl 早期的基础，加速了引擎达到可用状态。

### 设计参考

XEngine 的 AssetBundle 系统与资源管理层，参考了以下项目的**设计**（非源码或类型命名），
并依据 MIT 协议独立重新实现：
**[YooAsset](https://github.com/tuyoogame/YooAsset)**（Apache 2.0）、
Unity **ScriptableBuildPipeline**（Unity Companion License。

### 核心依赖

| 库 | 用途 | 来源 |
|---|---|---|
| **Anthology**（Echo / Paper / Origami / Quill / Scribe / Clay / Vector / Photonic / Unwrapper / Rosetta / Wicked / Graphite / Slang / Crumb / Drift） | 序列化、UI、矢量图形、文本、模型导入、数学、光照烘焙、UV 展开、本地化、网络、渲染抽象 | 内置源码（`ThirdParty/`，MIT） |
| [Silk.NET](https://github.com/dotnet/Silk.NET) | 窗口、输入、OpenGL / Vulkan 绑定 | NuGet |
| [Vortice.Direct3D12](https://github.com/amerkoleci/Vortice.Windows) | Direct3D 12 绑定 | NuGet |
| [Jitter Physics 2](https://github.com/notgiven688/jitterphysics2) | 3D 物理 | 内置源码（MIT，有本地补丁） |
| [Box2D.NET](https://github.com/ikpil/Box2D.NET) | 2D 物理（Box2D 3.1 绑定） | NuGet |
| [Magick.NET](https://github.com/dlemstra/Magick.NET) | 纹理导入的图像处理 | NuGet |
| [Slang](https://github.com/shader-slang/slang) | 着色器编译器 | MIT，原生库已内置 |
| [K4os.Compression.LZ4](https://github.com/MiloszKrajewski/K4os.Compression.LZ4) | AssetBundle 的 LZ4 块压缩 | MIT，NuGet |
| [DotRecast](https://github.com/ikpil/DotRecast) | 导航网格生成与寻路 | 内置源码（`ThirdParty/DotRecast/`，MIT） |
| [spine-csharp](https://github.com/EsotericSoftware/spine-runtimes) | Spine 骨骼动画运行时 | Spine Runtimes License |

完整的第三方许可声明见 [THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md)。

### Prowl 贡献者

以下贡献者参与了上游 Prowl 项目，XEngine 继承了他们的工作：

[Michael (Wulferis)](https://twitter.com/Wulferis) · [Abdiel Lopez](https://github.com/PaperPrototype) ·
[Josh Davis](https://github.com/10xJosh) · [ReCore67](https://github.com/recore67) ·
[Isaac Marovitz](https://github.com/IsaacMarovitz) · [Kuvrot](https://github.com/Kuvrot) ·
[JaggerJo](https://github.com/JaggerJo) · [Jihad Khawaja](https://github.com/jihadkhawaja) ·
[Jasper Honkasalo](https://github.com/japsuu) · [Kai Angulo](https://github.com/sinnwrig) ·
[Bruno Massa](https://github.com/brmassa) · [Mark Saba](https://github.com/ZeppelinGames) ·
[Chandler Cox](https://github.com/Tryibion) · [EJTP](https://github.com/EJTP) ·
[Paolo](https://github.com/xZekro51) · [Kouame Benoit Junior Augustin](https://github.com/ZedDevStuff)

---

## 📜 许可协议

XEngine 基于 **MIT 协议** 分发，详见 [LICENSE](LICENSE)。

```
MIT License
Copyright (c) 2026 Flamesky
```

XEngine 由 [Prowl Game Engine](https://github.com/ProwlEngine/Prowl)（MIT）演进而来，
并内置了其 Anthology 配套库（MIT）与 Slang 编译器的原生库（MIT）。
这些上游版权声明**不会**并入本 LICENSE 文件，而是完整保留在
[THIRD_PARTY_NOTICES.md](THIRD_PARTY_NOTICES.md) 与上文[致谢](#-致谢)中。
MIT 协议允许此类衍生与再分发。

---

<div align="center"><sub>XEngine —— 为开发者，也为与他们并肩工作的智能体而生。</sub></div>
