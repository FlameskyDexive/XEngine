# NodePlay（可选包）—— 可视化行为图套件

> 反应行为树 · 层次状态机 · 多角色对话树，共享一个 Echo 文本核心。
> 状态：M0–M21 已落地（`Packages/com.xengine.nodeplay`），M22/M23 收尾中。
> 规格：`docs/superpowers/specs/2026-08-27-nodeplay-package.md` · 证据:`docs/superpowers/evidence/nodeplay/`

## 一分钟上手

1. Package Manager → NodePlay → Install（`manifest.json` pin `com.xengine.nodeplay: 0.1.0`）。
2. Assets ▸ Create ▸ NodePlay ▸ Behaviour Tree / State Machine / Dialogue Tree（`.nodeplay` Echo 文本）。
3. 双击资产打开图编辑器；工具栏 **+ Node**（分类/收藏/首选浏览器）、**Wizard**（脚手架新任务）、
   Live 开关（运行中 Runner 的节点状态四色回染）。
4. 场景物体挂 `NPRunner`（BT 用 `NPBehaviourTreeOwner`）→ StartBehaviour 驱动。

## 三模块

| 模块 | 基类/资产 | 亮点 |
|---|---|---|
| 行为树 | `NPBehaviourTreeAsset` / `BT*` | dynamic 反应式重扫、Parallel 策略、效用 PrioritySelector、13 装饰器（条件门/中断/守卫/过滤/迭代…）、SubTree 嵌套 |
| 状态机 | `NPFSMAsset` / `FSM*` | 三转移调用模式（含 **Stacked 压栈 resume**）、AnyState、OnEnter/Update/Exit 伪状态、并发子图、NestedFSM/BT/DT |
| 对话树 | `NPDialogueAsset` / `DT*` | 请求事件解耦任意 UI（无 UI 时 AutoPump 自动推进）、黑板插值 `[var]`、SELF 演员、SubDialogue |

## 黑板与参数

三层作用域（图局部 → GameObject Runner → GlobalBlackboard），`Scope/var` 斜杠语法，
`NPParameter` 三态参数（内联/绑定/`_`动态自建），场景数据绑定（`Type.Member` 字段属性双向），
类型转换表 + 注册扩展。变量更名级联更新全部参数绑定（Refactor）。

## AI / 自动化

- 离线：`nodeplay_graph_inspect / _patch（漂移守卫+日志溯源）/ _compile（纯校验）`
  —— `cli/skills/nodeplay-graph/SKILL.md`。
- 实时（--serve）：`nodeplay_open` / `nodeplay_focus`；截图用
  `runtime_panel_screenshot panelType=XEngine.NodePlay.Editor.NodePlayWindow`。
- 格式即文本：图=扁平 Echo 字段，`$type` 逐节点信封，串改即生效。

## 扩展

40 枚内置任务（Transform/GameObject/Blackboard/Time/Math/Debug/Flow 七类）；
Task Wizard 一键脚手架到 `Assets/Scripts/NodePlay/`；首选类型 Project Settings 页钉选浏览器置顶。

## Roadmap（v1 后）

- 网络同步变量（`NPBlackboardSource.VariableWritten` 钩子已预留）
- 黑画布根治后的 MiniMap 比例视口框（现条带版可用）
- 反射任务族（ExecuteFunction/SetProperty 等,基于 XEngineCall）与 Physics 查询任务
