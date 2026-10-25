# Build~ — offline compile verification

Package sources are compiled per-project by the Editor's `ScriptCompiler`, so nothing in
`Packages/com.xengine.*/` is built by `XEngine.sln`. That leaves package code with no compile coverage
until someone opens a project that mounts it.

These projects close that gap for XUI. They are not part of the package: `Build~` ends in `~`, so the
package manager ignores it exactly like `Documentation~` and `Samples~`, and it never ships or compiles
as package content.

```bash
dotnet build Packages/com.xengine.xui/Build~/XEngine.XUI.Editor.Check.csproj
```

That one command compiles, in order: the XGUI package Runtime, the XUI package Runtime, and the XUI
package Editor — the same three assemblies a project mounts, with the same reference sets, so an
ambiguity or a missing using shows up here rather than in a user's project.

**Why XGUI is compiled here too:** XUI references `XEngine.XGUI.Runtime`, and that assembly only exists
once something compiles the XGUI package sources. Building against the engine's compatibility types
instead would not be the same compilation — several UI type names are declared in both assemblies, and
only a build with both references present proves XUI stays clear of them.
