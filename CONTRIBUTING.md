# Contributing to XEngine

🚀 **欢迎加入 XEngine 社区！感谢你有兴趣贡献。**

开发一款游戏引擎是一项庞大的工程，我们无法独自完成。我们需要热爱引擎开发的程序员、设计师、测试者与文档作者——像你这样的人——一起来把 XEngine 做得更好。💪

---

## 📋 目录

- [如何贡献](#-如何贡献)
- [开始之前](#-开始之前)
- [开发流程](#-开发流程)
- [代码规范与性能要求](#-代码规范与性能要求)
- [提交 Pull Request](#-提交-pull-request)
- [报告问题](#-报告问题)
- [行为准则](#-行为准则)

---

## 🤝 如何贡献

无论你是资深开发者还是刚入门，都有适合你的贡献方式：

### 💻 代码贡献
查看 [open issues](https://github.com/FlameskyDexive/XE/issues)，挑一个感兴趣的任务；也欢迎主动提出新功能或优化。引擎的待实现清单见 [README 的 Roadmap](README.md#-roadmap-待实现清单)——Android/iOS/鸿蒙/WASM、URP、Playable、AI CLI、Spine 等大量工作等待认领。

### 🐛 报告 Bug
遇到 Bug？请到 [issue 跟踪器](https://github.com/FlameskyDexive/XE/issues) 提交详细报告，帮助我们定位问题。见下方 [报告问题](#-报告问题) 章节。

### 💡 功能建议
有绝佳的新功能想法？开一个 [feature request](https://github.com/FlameskyDexive/XE/issues)，让我们一起讨论如何让 XEngine 更好。

### 📝 文档与示例
文档、教程、示例项目同样重要。`Books/`、`docs/`、`Samples/` 目录都欢迎补充。

### 🌍 本地化
编辑器支持多语言（英语、德语、西班牙语、法语、意大利语、日语、韩语、波兰语、葡萄牙语、俄语、土耳其语、中文）。翻译文件在 `XEngine.Editor/Resources/Locale/*.json`，欢迎新增语言或修正现有翻译。

---

## 🔧 开始之前

### 环境要求

- **[.NET 10 SDK](https://dotnet.microsoft.com/en-us/download/dotnet/10.0)**
- 推荐 IDE：[Visual Studio 17.8+](https://visualstudio.microsoft.com/)、[VSCode](https://code.visualstudio.com/)、[Rider](https://www.jetbrains.com/rider/)

### 仓库结构

```
XEngine.sln
├── XEngine.Runtime/         # 运行时核心
├── XEngine.Editor/          # 编辑器
├── ThirdParty/              # vendored Anthology 库（源码内置）
├── Tools/                   # 分析器、benchmark
├── Libraries/               # 平台原生库
├── Samples/                 # 示例项目
└── docs/ skills/ Books/     # 文档
```

> **重要**：Anthology 配套库已作为源码内置在 `ThirdParty/` 下，Slang 原生库也已提交。整个仓库**无需联网、无需外部 NuGet 源**即可完整 restore 与构建。修改 `ThirdParty/` 下的库等同于修改引擎自身——请谨慎，并说明动机。

---

## 🔄 开发流程

1. **Fork & Clone**
   ```bash
   git clone https://github.com/<你的用户名>/XE.git
   cd XE
   git remote add upstream https://github.com/FlameskyDexive/XE.git
   ```

2. **新建分支**——不要直接在 `main` 上开发
   ```bash
   git checkout -b feat/your-feature    # 或 fix/your-bugfix
   ```

3. **保持分支同步**
   ```bash
   git fetch upstream
   git rebase upstream/main             # 推荐用 rebase 保持线性历史
   ```

4. **提交频繁、清晰**——每个 commit 聚焦一件事，用 [Conventional Commits](https://www.conventionalcommits.org/) 风格：
   ```
   feat(render): add WebGPU backend skeleton
   fix(physics): correct capsule collider margin
   refactor(echo): simplify serializer dispatch
   docs(readme): update roadmap status
   chore(deps): bump Silk.NET to 2.24
   ```

---

## 📏 代码规范与性能要求

XEngine 是一款**性能优先**的引擎。改动代码前，请务必阅读 [`AGENTS.md`](AGENTS.md) 与对应的 [`skills/`](skills/) 指南：

| 改动领域 | 必读 skill | 关键规则 |
|---|---|---|
| LINQ 使用 | [`skills/linq-performance/`](skills/linq-performance/SKILL.md) | PR0001：LINQ 仅允许 benchmark 背书的操作符 |
| 每帧 CPU 热路径 | [`skills/hot-path-performance/`](skills/hot-path-performance/SKILL.md) | PR0002-PR0007：`[HotPath]` 标注、零分配、池化 |
| 渲染 / RHI / GPU | [`skills/rendering-gpu-performance/`](skills/rendering-gpu-performance/SKILL.md) | 渲染管线、命令缓冲、GPU 资源 |
| 性能验证 | [`skills/performance-validation/`](skills/performance-validation/SKILL.md) | benchmark、回归验证、证据归档 |

### ⚠️ 红线规则

- **不要在没有 benchmark 证据与配套测试的情况下，放宽分析器规则或扩大允许清单。**
- **不要在热路径（`[HotPath]` 标注的方法、每帧调用的代码）里引入 LINQ、闭包分配、`params` 数组等。**
- 改动涉及性能时，**附上 BenchmarkDotNet 结果**（放 `docs/benchmarks/`），并说明测量环境。
- 新增公开 API 必须有 XML 文档注释（`///`）。

### 通用规范

- C# 代码遵循仓库根 `.editorconfig` 的风格规则（命名、格式、可空性）。
- 文件头注释由 `.editorconfig` 的 `file_header_template` 自动生成，无需手写。
- 提交前确保 `dotnet build XEngine.sln` 零错误、`dotnet test` 通过。

---

## 🚀 提交 Pull Request

1. **自我审查**——提交 PR 前先 review 自己的 diff，剔除调试代码、注释掉的逻辑。
2. **PR 描述清晰**——说明：
   - 这个改动解决了什么问题 / 实现了什么功能
   - 如何测试的（附 benchmark 或测试用例）
   - 是否有破坏性变更（Breaking Change）
3. **关联 Issue**——如果对应某个 issue，在 PR 描述里写 `Closes #123`。
4. **通过 CI**——确保所有 CI 检查（构建、测试、分析器）通过。
5. **响应 review 意见**——保持开放心态，review 是为了让代码更好，不是针对个人。

### PR 标题示例
```
feat(render): add URP-style ScriptableRenderPass framework
fix(physics): resolve Jitter2 soft constraint drift at high FPS
perf(rhi): eliminate per-draw uniform allocation in Vulkan backend
```

---

## 🐛 报告问题

提交 [GitHub Issue](https://github.com/FlameskyDexive/XE/issues) 时请包含：

- **XEngine 版本**（commit hash 或 release tag）
- **操作系统与 .NET 版本**
- **复现步骤**（尽可能精简）
- **预期行为 vs 实际行为**
- **错误日志 / 截图 / 最小复现项目**（如有）

Bug 报告越详细，我们定位得越快。

---

## 📜 行为准则

参与 XEngine 社区即代表你同意遵守我们的 [行为准则](CODE_OF_CONDUCT.md)。请始终保持尊重与友善——我们对不友善行为零容忍。

---

**再次感谢你考虑贡献 XEngine。让我们一起打造出色的引擎！** 🎉

<p align="center"><em>XEngine — 由热爱驱动，为开发者而生</em></p>
