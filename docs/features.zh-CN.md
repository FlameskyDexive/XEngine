# XEngine —— 功能清单

XEngine V1.0.0f1 的详细模块清单。项目概览、快速开始与平台矩阵见 [README](../README.zh-CN.md)。
以下内容均在本仓库中实现，并由自动化测试套件覆盖（6,000+ 个 xUnit 测试方法）。

---

## 核心运行时与编辑器

| 模块 | 提供的能力 |
|---|---|
| **对象模型** | GameObject + MonoBehaviour 组件、标签与层级、可在 Inspector 配置的持久化回调、完整的生命周期语义 |
| **场景与预制体** | 带雾效与环境光的场景系统；嵌套预制体支持 Apply / Revert / Break Instance 与覆盖跟踪 |
| **编辑器** | Scene 视图、Hierarchy、Inspector、Project 浏览器、Console、Game 视图、Profiler、包管理器、偏好设置、构建配置；可停靠/可缩放面板与持久化布局；拖放；多选；搜索与过滤；资产缩略图与 3D 预览；变换 Gizmo；完整撤销/重做；可重绑快捷键；编辑器主题；编辑器内试运行；脚本热重载 |
| **本地化** | 编辑器 UI 支持 12 种语言（EN、DE、ES、FR、IT、JA、KO、PL、PT、RU、TR、ZH） |
| **脚本** | .NET 10 / CoreCLR 上的 C#；`dotnet build` 编译 Game 与 Editor 程序集；程序集定义；托管与原生插件 |
| **XTask** | 零分配异步 / 协程库，替代 `Task` 与 `IEnumerator` |
| **序列化** | Echo 文本序列化器 + Roslyn 源生成器 |
| **输入** | Input Action 系统，`.inputactions` 资产与专用编辑器：动作阶段、组合（WASD → Float2、D-pad）、处理器；键盘、鼠标与手柄 |
| **数学** | 64 位数学库 —— `Float4x4`、四元数、`Transform2D`、AABB、Bounds、视锥体、圆锥、Ray、Plane、LineSegment、Rect |

## 玩法系统

| 模块 | 提供的能力 |
|---|---|
| **3D 物理** | 刚体，盒/球/胶囊/圆柱/圆锥/凸包/网格/地形碰撞体，带悬挂与抓地打滑的车轮碰撞体，关节与约束（球窝、铰链、铰链角度、固定角度、圆锥限制、距离限制、扭转角、棱柱、万向、线上点、面上点、角电机与线电机），角色控制器，触发器，层级过滤，射线检测与形状查询 |
| **2D 物理** | 每场景 `PhysicsWorld2D`（1 单位 = 1 米，FixedUpdate 步进）、`Rigidbody2D`、盒/圆/胶囊/多边形/复合碰撞体、碰撞与触发事件、`PhysicsMaterial2D`、NonAlloc `Raycast` / `OverlapCircle` / `OverlapBox`、调试绘制 |
| **动画** | 骨骼动画与混合变形；Animator Controller 状态机（层级、AvatarMask、根运动、CrossFade、混合树）；人形骨骼映射与重定向；带曲线与事件的动画片段编辑器 |
| **Playable 与 Timeline** | 兼容 Unity 的 `PlayableGraph` / `AnimationPlayableOutput` / 自定义 Playable；可视化 Timeline 编辑器（轨道、片段、混合、信号） |
| **导航** | Recast/Detour 风格 NavMesh 烘焙、`NavMeshSurface` / `NavMeshAgent` / `NavMeshObstacle` / `NavMeshLink` / 修饰符与体积、动态重建的 tile cache、群体模拟、寻路查询与 Navigation 窗口 |
| **音频** | 带衰减与多普勒的空间 3D 音频；WAV / MP3 / OGG / FLAC；效果链（延迟、失真、双二阶滤波器、混响、移相器）与自定义 `IAudioEffect` |
| **UI** | 含 World Space 的 GameObject 驱动 UI；`RectTransform` 布局、按钮、滑动条、布局组、拖放事件；**XGUI** 保留模式 UI 包（富文本、超链接、内联精灵、安全区、IME 输入、合批） |
| **2D** | 精灵、精灵图集、排序组、九宫格绘制模式、带调板与规则瓦片的 Tilemap、SpriteShape 样条、像素级相机、2D 骨骼动画（SpriteSkin / Bone2D / Sprite Library / IK）、Spine 运行时、PSD 与 Aseprite 导入器 |
| **地形** | 四叉树 LOD 高度图地形、Splatmap 绘制、GPU 实例化草、带 LOD 距离的树渲染、地形孔洞，以及雕刻/绘制/草/树/设置专用编辑器 |
| **VFX** | 节点式 GPU Visual Effect Graph（生成、初始化、更新、输出块；序列帧、网格输出、受光粒子、SDF 与平面碰撞、湍流、软粒子、子图），以及 GPU 实例化渲染的经典 CPU 粒子系统 |

## 内容管线

| 模块 | 提供的能力 |
|---|---|
| **资产数据库** | `.meta` 文件 GUID 引用、导入缓存与文件监视自动重导入、基于特性的自定义导入器、确定性 GUID 的子资产、正向与反向依赖跟踪、多线程加载 |
| **格式** | 模型：GLTF / GLB / OBJ / FBX · 纹理：PNG / JPG / BMP / TGA / PSD / HDR / DDS / EXR · 音频：WAV / MP3 / OGG / FLAC · 2D：Aseprite、带骨骼的分层 PSD |
| **AssetBundles 与热更新** | Bundle 构建管线、Provider/Handle 引用计数、清单版本管理、带资产流式加载的虚拟文件系统、LZ4 压缩、下载器与解密分层 |
| **构建系统** | 独立播放器导出、打包资产文件（`.xenginepak`）、仅导出已用资产、按平台的构建配置、构建配置 UI |
| **过程感知** | 每次编辑将作者/意图/增量记入可查询的日志 —— 对人类与 Agent 同等开放 |

## 图形与渲染

**一套 RHI 抽象，六个图形后端**：Direct3D 12 · Vulkan · OpenGL · Metal · WebGPU · WebGL2。
Vulkan、D3D12 与 OpenGL 是当前完全验收的桌面路径；Metal、WebGPU 与 WebGL2 正在接入中。

- **独立渲染线程**与可扩展、可脚本化的渲染管线
- **通用渲染管线（URP）** —— `PipelineAsset` / `ScriptableRenderer` / `RendererFeature` / Volume / RenderGraph / RTHandle / 相机堆叠，对齐 URP 17.7（Forward+、STP、SSGI、APV、LOD 交叉淡出）
- **渲染路径** —— Forward-Lit、Forward+、延迟 GBuffer、Renderer2D
- **RenderGraph** —— 瞬态资源与光栅/计算/非安全通道
- **实时 GI** —— 屏幕空间 GI 加 DDGI 动态辐照度探针体积，六个后端全部验收通过
- **Virtual Geometry** —— Nanite 级 GPU 剔除、簇选择与驻留管理
- **Shader Graph** —— 节点编辑器加 CLI `inspect` / `patch` / `compile`，输出 GLSL 与 Slang
- **自定义着色器语言** —— `#include`、多 Pass、关键字与变体
- **光照** —— HDR + PBR 金属度工作流，点光/聚光/平行光，每种光源的阴影贴图，最多 4 级联，点光立方体阴影，动态打包阴影图集，光 Cookie，渲染层，反射探针，烘焙光探针，渐进式 CPU 光照贴图烘焙与 UV 展开
- **后处理** —— ACES / Reinhard / Uncharted / Filmic / Melon / AgX 色调映射、Bloom、FXAA / SMAA / TAA、GTAO、随机 SSR、Bokeh 景深、体积雾、镜头光晕、屏幕空间阴影、屏幕空间贴花、电影级噪点/暗角/色差；可选 Post Processing 包提供 PPv2 风格的 Layer / Volume / Profile 工作流
- **Volume 框架** —— 通过 `.volumeprofile` 资产做全局与局部混合
- **渲染器** —— 网格、蒙皮网格（骨骼+混合变形）、线、精灵、GPU 实例化、视锥剔除、带模板的 LOD 交叉淡出
- **其他** —— 渲染纹理、Texture3D、grab pass（深度感知折射/热浪/磨砂玻璃）、透明、程序化/立方体/渐变天空盒、STP 时域超分、MSAA
- **调试** —— Frame Debugger（事件树、RT 预览、完整状态检查）与 Rendering Debugger

## 第一方包

核心 2D 与渲染原语内置于引擎；下表以可选包形式经编辑器内包管理器安装（`Packages/` 下共 38 个第一方包）。

| 包 | 新增能力 |
|---|---|
| `com.xengine.xgui` | 保留模式 GameObject UI |
| `com.xengine.xui` | 基于 XGUI 的 AI 优先 UI 应用层 —— 紧凑的 `.xui` XML 界面 |
| `com.xengine.timeline` | 基于 PlayableGraph 运行时的过场/序列编辑器 |
| `com.xengine.vfx` | 节点式 GPU Visual Effect Graph |
| `com.xengine.terrain` | 带 Splat 层、草、树与雕刻工具的高度图地形 |
| `com.xengine.ai.navigation` | NavMesh 烘焙、查询、Agent 与 Navigation 窗口 |
| `com.xengine.spine` | Esoteric Spine 骨骼 2D 动画运行时 |
| `com.xengine.postprocessing` | PPv2 风格后处理 Layer / Volume / Profile |
| `com.xengine.cinemachine` | 虚拟相机系统 |
| `com.xengine.feature.2d` | 汇总全部第一方 2D 子包的元包 |
| `com.xengine.2d.*` | `common`、`sprite`、`tilemap`、`tilemap.extras`、`animation`、`spriteshape`、`pixel-perfect`、`psdimporter`、`aseprite`、`tooling` |

## AI 原生工具链（MCP）

XEngine 通过 **stdio 上的 MCP** 向 AI Agent 与 CLI 开放自身，含两条通道：

- **离线 Host —— 120+ 工具**（`Tools/XEngine.Ai.Host`，即 `xengine-cli`）。无需编辑器运行即可操作磁盘上的工程：场景与预制体编辑，资产导入/搜索/检查/补丁，Animator 状态机、混合树、Avatar 遮罩、片段曲线与事件，NavMesh，Shader Graph，VFX Graph，Volume，材质，脚本增删改，代码符号搜索，包管理，构建，无头运行，测试运行，工作流与生成网关。
- **编辑器内运行时 —— 20+ 工具**（`--serve`）。需要活动编辑器：播放模式控制、实时场景修改、选择、菜单、表达式求值、截图与面板截图、渲染统计、帧调试、渲染调试、Animator 与动画预览、NavMesh 烘焙与 Agent 控制、Spine 播放与换肤、VFX 预览。

**安全护栏**是协议的一部分而非事后补丁：带四级 `RiskLevel` 的能力目录、`dryRun` 预演、针对并发编辑的哈希预检、幂等键，以及为每次变更记录作者/意图/增量的过程感知日志。

**40+ 份创作技能**（`cli/skills/`）逐一记录工作流 —— 编辑场景、Animator、Shader Graph、VFX Graph、Timeline、材质、Volume、管线、XUI 界面；检查帧与渲染状态；生成资产；运行测试、工作流与实时场景 —— Agent 读完技能文档即可一次做对。

**生成式资产网关** —— `gen_submit` / `gen_status` / `gen_fetch` / `gen_candidates` 等工具对接六家提供商：**Meshy**（3D 模型）、**PixelLab**（精灵）、**Blockade Labs**（天空盒）、**ElevenLabs**（音频）、**OpenAI GPT Image** 与自托管 **ComfyUI**。生成配方写入资产的 `.meta`，任何资产都可复现地重新生成。

## 性能文化

性能决策一律来自**实测的帧时间、吞吐、内存分配与 GPU 开销** —— 而非直觉。仓库内建了强制手段：

- **`XEngine.Analyzers`（PR0001–PR0007）** —— 针对每帧 CPU 热路径的 Roslyn 分析器：禁用 LINQ、`[HotPath]` 检测、分配检测。没有 benchmark 证据与配套测试，不允许放宽规则或扩大允许清单。
- **性能契约测试** —— 如 `FrameLoopPerformanceContractTests`、`StageAPerformanceContractTests`，以及可逐轮复现分配字节数的零分配探针。
- **跨图形后端的字节级截图回归**，改变像素的渲染变更无法悄悄混入。

引擎开发过程中的代表性测量：`XTask` 同步完成 **0.93 ns / 0 B**；六条 Transform 路径 **0 B** 分配；编辑器空闲占用**单核 0.365%**（空场景、空闲态、Release + Vulkan、30 秒采样）。
