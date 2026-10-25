# HelloHotfix 示例

最小热更新模块：演示 interpreted 程序集 + 宿主桥接 + 异步 XTask 的完整闭环。

## 安装

1. 安装 com.xengine.ilinterpreter 包（Package Manager → Official）。
2. 菜单 Assets/IL Interpreter/Create Hot-update Module 创建模块骨架。
3. 将本目录的 HotModule.cs 复制到 Assets/Hotfix/。
4. 菜单 Assets/IL Interpreter/Check Compatibility 检查兼容性。
5. ILBindings.txt 声明宿主绑定后构建 Player——模块随 Content/ILInterpreter 打包。

## 模块内容

HotModule 依赖宿主提供的 ILInterpreterHost 组件（AOT 侧），演示
Start/Update 生命周期、Count 字段序列化，以及 `async XTask<int>` 等待宿主任务。
