using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Loader;
using System.Threading.Tasks;

using XEngine.Echo;
using XEngine.Editor.Core;
using XEngine.Editor.GUI;
using XEngine.Editor.GUI.SceneView;
using XEngine.Rosetta;
using XEngine.Runtime;
using XEngine.Runtime.Resources;

namespace XEngine.Editor.Projects.Scripting;

/// <summary>
/// Manages script assembly compilation, loading, and hot-reload.
/// Uses a collectible AssemblyLoadContext so assemblies can be fully unloaded
/// and replaced. Falls back to an editor restart if unloading fails.
/// </summary>
public static class ScriptAssemblyManager
{
    private static bool _recompileRequested;
    private static DateTime _lastScriptChange;
    private static bool _isCompiling;
    private static ScriptCompiler.CompileResult? _pendingResult;
    private static bool _restartDeferred;
    private static bool _pendingHotReload;
    private static Core.Progress.ProgressHandle _updateCompileProgress;
    private static TimeSpan DebounceDelay = TimeSpan.FromSeconds(1);

    /// <summary>True while a watcher-triggered background compile is running (Unity's
    /// isCompiling): blocks Play-mode entry and shows the compiling progress card.</summary>
    public static bool IsCompiling => _isCompiling;

    private static AssemblyLoadContext? s_scriptContext;
    private static readonly List<Assembly> s_scriptAssemblies = [];
    private const int MaxGCAttempts = 10;

    // The GC verification loop runs synchronously inside Window.Update, so the OS message pump is
    // frozen while it spins. Without a wall-clock budget, ten full collections on a large editor
    // heap can stall the window for tens of seconds (Windows flags it "not responding"), and a
    // wedged finalizer can block GC.WaitForPendingFinalizers forever. Both are bounded here so a
    // genuine leak degrades into a fast restart instead of a hang.
    private static readonly TimeSpan UnloadTimeBudget = TimeSpan.FromSeconds(10);
    private static readonly TimeSpan FinalizerWaitTimeout = TimeSpan.FromSeconds(5);

    // Plugin resolution state, refreshed each time assemblies are (re)loaded.
    private static readonly Dictionary<string, string> s_managedPlugins = new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<PluginInfo> s_nativePlugins = [];
    private static DllImportResolver? s_nativeResolver;
    private static string s_pluginLoadDir = "";

    /// <summary>Signal that scripts have changed and need recompilation.</summary>
    public static void RequestRecompile()
    {
        _recompileRequested = true;
        _lastScriptChange = DateTime.UtcNow;
    }

    /// <summary>Call once per frame. Triggers compilation after debounce period.</summary>
    public static void Update()
    {
        // Hot reload deferred one frame so the "Reloading assemblies…" busy dialog got presented
        // before the blocking save/unload/GC/restore sequence runs.
        if (_pendingHotReload)
        {
            _pendingHotReload = false;
            try
            {
                if (!TryHotReload(Project.Current!))
                {
                    Runtime.Debug.LogWarning("[ScriptAssemblyManager] Hot-reload failed. Restarting editor...");
                    GUI.Popups.BusyDialog.Close();
                    RestartEditor(Project.Current!);
                    return;
                }
            }
            finally
            {
                GUI.Popups.BusyDialog.Close();
            }
            return;
        }

        // Check if background compilation finished
        if (_pendingResult.HasValue)
        {
            // A compile that kicked off during the debounce window can finish after the user
            // has entered play mode. Restarting now would snapshot the in-play scene over
            // the user's saved scene, so park the result until play mode ends.
            if (Application.IsPlaying)
            {
                if (!_restartDeferred)
                {
                    _restartDeferred = true;
                    Runtime.Debug.Log("[ScriptAssemblyManager] Compilation finished during play. Restart deferred until play mode ends.");
                }
                return;
            }

            var result = _pendingResult.Value;
            _pendingResult = null;
            _isCompiling = false;
            _restartDeferred = false;
            Core.Progress.EditorProgress.Finish(_updateCompileProgress, success: result.Success);
            _updateCompileProgress = default;

            if (result.Success)
            {
                if (result.RequiresReload)
                {
                    // The CLR keeps collectible assemblies alive for the whole debug session, so an
                    // unload under a debugger ALWAYS fails. Attempting it anyway needlessly destroys
                    // the scene/references, stalls the UI in the GC loop, then restarts the editor
                    // (killing the debug session). Skip the reload instead and keep editing - the
                    // compiled changes apply the next time the editor starts.
                    if (System.Diagnostics.Debugger.IsAttached)
                    {
                        Runtime.Debug.LogWarning("[ScriptAssemblyManager] Debugger attached - skipping hot-reload. " +
                                                 "The CLR cannot unload collectible assemblies while a debugger is attached, " +
                                                 "so attempting it would force an editor restart. Run without the debugger " +
                                                 "(Ctrl+F5) for true hot-reload; compiled changes apply on next editor start.");
                        return;
                    }

                    Runtime.Debug.Log("[ScriptAssemblyManager] Compilation successful. Attempting hot-reload...");

                    // Show the busy dialog and defer the blocking reload to the next frame so it
                    // gets presented first (Unity's "Reloading…" phase; the GC loop pumps OS
                    // messages so the window stays movable throughout).
                    _pendingHotReload = true;
                    GUI.Popups.BusyDialog.Show(Loc.Get("progress.reload.title"));
                    return;
                }
                else
                {
                    Runtime.Debug.LogSuccess("[ScriptAssemblyManager] Packages restored.");
                }
            }
            else
            {
                Runtime.Debug.LogError("[ScriptAssemblyManager] Compilation failed. Fix errors and save to retry.");
            }
            return;
        }

        if (!_recompileRequested || _isCompiling) return;
        if (Project.Current == null) return;

        // A frame-sliced asset-db initialization is mid-flight: importing scripts can request a
        // recompile, but starting it now would let a finished background compile trigger the
        // hot-reload sequence (scene save → unload → restore) while the database is half
        // initialized. The pending request stays queued and runs once the open completes.
        if (EditorAssetBackend.Instance is { IsInitializing: true }) return;

        // Only compile when the editor window is focused (don't spam while user is editing
        // externally). A --serve launch is driven by an agent over MCP and may stay unfocused or
        // minimized forever, so the focus gate would postpone its recompiles indefinitely.
        if (!Window.IsFocused && !Program.Serve) return;

        // Only compile when not Playing
        if (Application.IsPlaying) return;

        // Wait for debounce
        if (DateTime.UtcNow - _lastScriptChange < DebounceDelay) return;

        _recompileRequested = false;

        var project = Project.Current;
        // The source set spans Assets and every resolved package root. In particular, a blank
        // project still has XGUI source because it is shipped with the Editor as a built-in package.
        bool hasCompilationSources = ScriptCompiler.HasCompilationSources(project);
        bool hasNuGetPackages = ScriptCompiler.ProjectDeclaresPackages(project);

        // Nothing to build and no NuGet packages to restore, so there is nothing to do.
        if (!hasCompilationSources && !hasNuGetPackages)
        {
            Runtime.Debug.Log("[ScriptAssemblyManager] No scripts or packages found, skipping compilation.");
            return;
        }

        // Staleness guard: a recompile request only means "sources may have changed", not "they
        // did". Requests arrive from file watchers (often redundant — the same save fires Changed
        // twice), from the initial asset scan's ScriptImporter (whose per-script mtime probe is a
        // cheap heuristic that misfires for package-only projects, where GameAssemblyPath never
        // exists), and from a build-profile or plugin .meta edit that touched no script at all.
        // Without this re-check, every one of those runs a full `dotnet build -t:Rebuild` (~dozens
        // of seconds across all package assemblies), which is exactly the "reopened the project but
        // it still takes minutes" symptom: the first open compiles once, then the initial scan
        // requests a second identical full compile. If nothing is actually stale, drop the request.
        if (hasCompilationSources && !ScriptCompiler.IsCompilationStale(project))
        {
            Runtime.Debug.Log("[ScriptAssemblyManager] Script assemblies are up to date; skipping recompile.");
            return;
        }

        _isCompiling = true;
        Runtime.Debug.Log(hasCompilationSources
            ? "[ScriptAssemblyManager] Starting compilation..."
            : "[ScriptAssemblyManager] Restoring packages...");

        // Unity-style compiling card: blocks editing while dotnet build runs on the background
        // thread, live per-assembly detail, auto-closes when the result lands.
        _updateCompileProgress = Core.Progress.EditorProgress.Start(
            Core.Progress.EditorProgressKind.Compile,
            Loc.Get("progress.compile.title"),
            total: 0,
            Core.Progress.EditorProgressVisibility.ImmediatePopup);

        // Notify [OnScriptCompile] listeners only when assemblies will actually reload.
        if (hasCompilationSources)
            Core.ScriptReloadCallbacks.InvokeScriptCompile();

        // Run on background thread result polled on main thread via _pendingResult
        Task.Run(() =>
        {
            try
            {
                _pendingResult = ScriptCompiler.CompileAll(project, p =>
                {
                    string detail = Loc.Get("launcher.progress.compiling",
                        new { assembly = p.AssemblyName, index = p.Index + 1, total = p.Total });
                    GUI.ProjectOpenProgress.SetDetail(detail);
                    Core.Progress.EditorProgress.Report(_updateCompileProgress, p.Index + 1, detail);
                });
            }
            catch (Exception ex)
            {
                Runtime.Debug.LogError($"[ScriptAssemblyManager] Compilation exception: {ex.Message}");
                _pendingResult = new ScriptCompiler.CompileResult { Success = false, Errors = ex.Message };
            }
        });
    }

    // ================================================================
    //  Startup load (frame-paced open step 0)
    // ================================================================

    private enum StartupLoadState { Idle, Compiling }
    private static StartupLoadState _startupLoadState = StartupLoadState.Idle;
    private static ScriptCompiler.CompileResult? _startupCompileResult;
    private static Core.Progress.ProgressHandle _startupCompileProgress;

    /// <summary>True while the frame-paced startup load (open step 0) is mid-flight.</summary>
    public static bool IsStartupLoadInProgress => _startupLoadState != StartupLoadState.Idle;

    /// <summary>
    /// Frame-paced startup load for project-open step 0. The potentially minutes-long `dotnet
    /// build` of stale assemblies runs on a background thread (first call), so the editor keeps
    /// presenting frames — the startup/launcher screen shows live "Compiling …" detail instead of
    /// freezing the window. Subsequent calls return false until the compile finishes; then the
    /// assembly load (fast: DLL copy + LoadFromAssemblyPath) runs synchronously and the call
    /// returns true. Fresh DLLs load synchronously on the first call, exactly like before.
    /// </summary>
    public static bool StartupLoadStep(Project project)
    {
        if (_startupLoadState == StartupLoadState.Idle)
        {
            if (!ScriptCompiler.IsCompilationStale(project))
            {
                Core.StartupTrace.Time("Scripts.LoadAssemblies", () => LoadAssembliesCore(project));
                return true;
            }

            _startupLoadState = StartupLoadState.Compiling;
            _startupCompileResult = null;
            _startupCompileProgress = Core.Progress.EditorProgress.Start(
                Core.Progress.EditorProgressKind.Compile,
                Loc.Get("progress.compile.title"),
                total: 0,
                Core.Progress.EditorProgressVisibility.StatusBarOnly);
            Runtime.Debug.Log("[ScriptAssemblyManager] Starting background startup compilation...");
            Task.Run(() =>
            {
                try
                {
                    _startupCompileResult = ScriptCompiler.CompileAll(project, p =>
                    {
                        GUI.ProjectOpenProgress.SetDetail(Loc.Get("launcher.progress.compiling",
                            new { assembly = p.AssemblyName, index = p.Index + 1, total = p.Total }));
                        Core.Progress.EditorProgress.Report(_startupCompileProgress, p.Index + 1,
                            Loc.Get("launcher.progress.compiling",
                                new { assembly = p.AssemblyName, index = p.Index + 1, total = p.Total }));
                    });
                }
                catch (Exception ex)
                {
                    Runtime.Debug.LogError($"[ScriptAssemblyManager] Startup compilation exception: {ex.Message}");
                    _startupCompileResult = new ScriptCompiler.CompileResult { Success = false, Errors = ex.Message };
                }
            });
            return false;
        }

        if (_startupLoadState == StartupLoadState.Compiling)
        {
            if (_startupCompileResult == null)
                return false; // background compile still running

            _startupLoadState = StartupLoadState.Idle;
            var result = _startupCompileResult;
            _startupCompileResult = null;
            Core.Progress.EditorProgress.Finish(_startupCompileProgress, success: result is { Success: true });
            _startupCompileProgress = default;

            if (result is { Success: true })
            {
                Core.StartupTrace.Time("Scripts.LoadAssemblies", () => LoadAssembliesCore(project));
            }
            else
            {
                // Historical failure behavior: log and keep editing without script assemblies —
                // LoadAssembliesCore would re-run the same compile synchronously otherwise.
                Runtime.Debug.LogError($"[ScriptAssemblyManager] Startup compilation failed: {result?.Errors}");
            }
            return true;
        }

        return true;
    }

    // ================================================================
    //  Recompile (file-watch driven)
    // ================================================================

    // ================================================================
    //  Assembly Enumeration
    // ================================================================

    public static IReadOnlyList<Assembly> GetScriptAssemblies() => s_scriptAssemblies;

    /// <summary>
    /// Returns all assemblies that registries should scan:
    /// script assemblies first (so hot-reloaded types are found), then default context.
    /// </summary>
    public static IEnumerable<Assembly> GetAllRelevantAssemblies()
    {
        foreach (var asm in s_scriptAssemblies)
            yield return asm;
        foreach (var asm in AssemblyLoadContext.Default.Assemblies)
            yield return asm;
    }

    /// <summary>
    /// Enumerates all types from all relevant assemblies (engine + scripts).
    /// Safely handles <see cref="ReflectionTypeLoadException"/>.
    /// </summary>
    public static IEnumerable<Type> GetAllTypes()
    {
        foreach (var assembly in GetAllRelevantAssemblies())
        {
            Type[] types;
            try { types = assembly.GetTypes(); }
            catch (ReflectionTypeLoadException ex)
            {
                types = ex.Types.Where(t => t != null).ToArray()!;
            }
            catch { continue; }

            foreach (var type in types)
                yield return type;
        }
    }

    // ================================================================
    //  Loading
    // ================================================================

    /// <summary>Load all pre-built user assemblies into a collectible AssemblyLoadContext.</summary>
    public static void LoadAssemblies(Project project)
        => Core.StartupTrace.Time("Scripts.LoadAssemblies", () => LoadAssembliesCore(project));

    private static void LoadAssembliesCore(Project project)
    {
        RefreshPluginResolution(project);

        // A freshly created project may have no manifest entry or Assets script, while the Editor
        // distribution still contributes built-in package assemblies (for example XGUI). Compile
        // those package sources before probing DLLs; otherwise the first editor launch silently
        // skips the package and only a later file-system change would load it.
        //
        // Staleness matters as much as existence: after a built-in package version bump the old
        // DLLs still sit in Library/ScriptAssemblies, so a missing-file check alone would load
        // outdated package code on every startup (and a --serve launch never recovers, because the
        // at-runtime recompile path requires window focus plus a file-watcher event).
        if (ScriptCompiler.IsCompilationStale(project))
        {
            var compile = ScriptCompiler.CompileAll(project, static p =>
                GUI.ProjectOpenProgress.SetDetail(
                    Loc.Get("launcher.progress.compiling", new { assembly = p.AssemblyName, index = p.Index + 1, total = p.Total })));
            if (!compile.Success)
            {
                Runtime.Debug.LogError($"[ScriptAssemblyManager] Initial package compilation failed: {compile.Errors}");
                return;
            }
        }
        var assemblyPaths = ScriptCompiler.GetEditorAssemblyPaths(project);

        if (s_scriptContext == null)
        {
            s_scriptContext = new AssemblyLoadContext("XEngineScripts", isCollectible: true);
            // Resolve managed plugin dependencies of user assemblies (engine assemblies fall back
            // to the default context automatically; plugins are not in any context yet).
            s_scriptContext.Resolving += ResolveManagedPlugin;
        }

        // Load every produced user assembly in dependency order.
        foreach (var dll in assemblyPaths)
            LoadAssembly(dll, Path.GetFileNameWithoutExtension(dll));
    }

    /// <summary>Snapshot the project's plugins so the resolvers can satisfy user-assembly imports.</summary>
    private static void RefreshPluginResolution(Project project)
    {
        s_managedPlugins.Clear();
        s_nativePlugins.Clear();
        s_pluginLoadDir = Path.Combine(project.ScriptAssemblyPath, ".loaded");

        foreach (var plugin in PluginScanner.ScanAll(project))
        {
            if (plugin.IsManaged)
                s_managedPlugins[Path.GetFileNameWithoutExtension(plugin.FileName)] = plugin.AbsolutePath;
            else
                s_nativePlugins.Add(plugin);
        }

        s_nativeResolver ??= ResolveNativePlugin;
    }

    private static Assembly? ResolveManagedPlugin(AssemblyLoadContext context, AssemblyName name)
    {
        if (name.Name == null || !s_managedPlugins.TryGetValue(name.Name, out var path) || !File.Exists(path))
            return null;

        try
        {
            // Copy to the unlocked .loaded directory (under ScriptAssemblies, NOT under Assets, so it
            // is never rescanned as a plugin) so the source plugin stays writable.
            string tempDir = string.IsNullOrEmpty(s_pluginLoadDir)
                ? Path.Combine(Path.GetDirectoryName(path)!, ".loaded")
                : s_pluginLoadDir;
            Directory.CreateDirectory(tempDir);
            string tempPath = Path.Combine(tempDir, $"{name.Name}_{Guid.NewGuid():N}.dll");
            File.Copy(path, tempPath, true);
            return context.LoadFromAssemblyPath(tempPath);
        }
        catch (Exception ex)
        {
            Runtime.Debug.LogError($"[ScriptAssemblyManager] Failed to resolve managed plugin '{name.Name}': {ex.Message}");
            return null;
        }
    }

    private static IntPtr ResolveNativePlugin(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        string requested = libraryName;
        foreach (var plugin in s_nativePlugins)
        {
            string stem = Path.GetFileNameWithoutExtension(plugin.FileName);
            if (stem.StartsWith("lib", StringComparison.Ordinal)) stem = stem[3..];

            bool matches = plugin.FileName.Equals(requested, StringComparison.OrdinalIgnoreCase)
                || stem.Equals(requested, StringComparison.OrdinalIgnoreCase)
                || Path.GetFileNameWithoutExtension(plugin.FileName).Equals(requested, StringComparison.OrdinalIgnoreCase);

            if (matches && NativeLibrary.TryLoad(plugin.AbsolutePath, out IntPtr handle))
                return handle;
        }
        return IntPtr.Zero;
    }

    private static void LoadAssembly(string dllPath, string label)
    {
        if (!File.Exists(dllPath) || s_scriptContext == null) return;

        try
        {
            // Copy to a unique temp path so the original stays unlocked for recompilation
            string tempDir = Path.Combine(Path.GetDirectoryName(dllPath)!, ".loaded");
            Directory.CreateDirectory(tempDir);

            // Purge leftover files from previous sessions
            foreach (var f in Directory.EnumerateFiles(tempDir))
                try { File.Delete(f); } catch { }

            string stem = Path.GetFileNameWithoutExtension(dllPath);
            string tempPath = Path.Combine(tempDir, $"{stem}_{Guid.NewGuid():N}.dll");
            File.Copy(dllPath, tempPath, true);

            // Also copy the .pdb so stack traces resolve to user source lines
            string pdbPath = Path.ChangeExtension(dllPath, ".pdb");
            if (File.Exists(pdbPath))
            {
                try { File.Copy(pdbPath, Path.ChangeExtension(tempPath, ".pdb"), true); } catch { }
            }

            var asm = s_scriptContext.LoadFromAssemblyPath(Path.GetFullPath(tempPath));
            s_scriptAssemblies.Add(asm);

            // CLR runs [ModuleInitializer] methods lazily — on the first *code* access to the
            // module — and a freshly loaded game assembly may never be touched before gameplay
            // code relies on it (e.g. a bootstrapper subscribing to Scene.OnSceneLoaded from
            // [ModuleInitializer] would silently never run). Honor the attribute's contract the
            // way an exe reference load would: run every module initializer explicitly.
            foreach (var module in asm.GetModules())
            {
                try { System.Runtime.CompilerServices.RuntimeHelpers.RunModuleConstructor(module.ModuleHandle); }
                catch (Exception ex)
                {
                    Runtime.Debug.LogError($"[ScriptAssemblyManager] Module initializer of {Path.GetFileName(dllPath)} threw: {ex.Message}");
                }
            }

            // Let this assembly's P/Invokes resolve against the project's native plugins.
            if (s_nativeResolver != null && s_nativePlugins.Count > 0)
                try { NativeLibrary.SetDllImportResolver(asm, s_nativeResolver); } catch { }

            Runtime.Debug.Log($"[ScriptAssemblyManager] Loaded {label} assembly: {Path.GetFileName(dllPath)}");
        }
        catch (Exception ex)
        {
            Runtime.Debug.LogError($"[ScriptAssemblyManager] Failed to load {label} assembly: {ex.Message}");
        }
    }

    // ================================================================
    //  Unloading
    // ================================================================

    /// <summary>
    /// Unload the script assembly context, then verify it is truly gone via a forced GC loop.
    /// Returns false if it survives (caller should restart). The strong reference to the context
    /// is confined entirely to <see cref="UnloadContext"/>: this method only ever holds the
    /// <see cref="WeakReference"/>, so no live local roots the context across the GC loop.
    /// </summary>
    /// <remarks>
    /// AggressiveOptimization matters here, not just NoInlining: this method runs once per reload,
    /// so it would otherwise be jitted at Tier0, where every stack slot that ever held an object
    /// reference is reported live for the whole method. That is exactly how the previous version
    /// pinned the context - the <c>foreach (var asm in s_scriptAssemblies)</c> temp stayed on the
    /// frame through the GC loop (gcdump root: [local vars] → RuntimeAssembly → LoaderAllocator),
    /// so every hot-reload fell back to a restart. With full opts the JIT tracks lifetimes precisely,
    /// and the assembly-touching work is split into <see cref="PrepareAssembliesForUnload"/> so no
    /// assembly reference is ever a local of this frame at all.
    /// </remarks>
    [MethodImpl(MethodImplOptions.NoInlining | MethodImplOptions.AggressiveOptimization)]
    private static bool UnloadScriptAssemblies()
    {
        if (s_scriptContext == null)
            return true;

        string[] assemblyNames = PrepareAssembliesForUnload();
        WeakReference weakCtx = UnloadContext();

        // Forced GC loop. Collect, run finalizers, collect again so anything resurrected for
        // finalization is reclaimed within the same iteration.
        // In this section, it's useless to re-assign s_scriptContext as it pins down the ALC and, if it fails to reload,
        // the editor will restart anyway so there's no need to have it here.
        // An unload either succeeds within the first couple of iterations or a real pin exists and
        // more attempts will not help, so the loop is bounded by BOTH attempts and elapsed time.
        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        int attempts = 0;
        for (; attempts < MaxGCAttempts && weakCtx.IsAlive; attempts++)
        {
            if (stopwatch.Elapsed >= UnloadTimeBudget)
            {
                break;
            }

            GC.Collect();

            // Bounded finalizer wait: a blocked finalizer (native interop deadlock, user finalizer)
            // would otherwise freeze the main thread forever.
            Task.Run(() => GC.WaitForPendingFinalizers()).Wait(FinalizerWaitTimeout);

            GC.Collect();

            // Keep the OS queue drained so the window answers move/resize while the loop spins
            // (the busy dialog itself only repaints between phases).
            XEngine.Runtime.Window.PumpOsMessages();
        }

        if (weakCtx.IsAlive)
        {
            string hint = System.Diagnostics.Debugger.IsAttached
                ? " A DEBUGGER IS ATTACHED - the CLR keeps collectible assemblies alive for the whole debug " +
                  "session, so hot-reload cannot unload while debugging. Run without the debugger (Ctrl+F5 / " +
                  "launch the built exe) to get true hot-reload; under the debugger the editor restarts instead."
                : " No debugger attached - a live reference is still rooting the script assemblies. The engine " +
                  "clears its reflection caches, static event subscriptions, and pending XTask operators on " +
                  "unload; the usual remaining culprits are script-created threads, timers, or native callbacks " +
                  "that are still running user code. Falling back to editor restart.";
            Debug.LogError($"[ScriptAssemblyManager] Script context still alive after {attempts} GC attempts " +
                           $"({stopwatch.Elapsed.TotalSeconds:F1}s). Pinned assemblies: {string.Join(", ", assemblyNames)}. " + hint);
            HoldForRootDiagnostics();
            return false;
        }

        Debug.Log("[ScriptAssemblyManager] Unload successful.");
        return true;
    }

    /// <summary>
    /// Opt-in diagnostic for a pinned script context: with <c>XENGINE_HOTRELOAD_HOLD_SECONDS</c> set
    /// the process stays alive (OS queue pumped) for that long after the unload verdict, so a heap
    /// dump can be taken while the collectible context is still rooted
    /// (<c>dotnet-dump collect -p PID</c>, then <c>dumpheap -type LoaderAllocator</c> / <c>gcroot</c>).
    /// Without the variable the restart fallback proceeds immediately.
    /// </summary>
    private static void HoldForRootDiagnostics()
    {
        string? raw = Environment.GetEnvironmentVariable("XENGINE_HOTRELOAD_HOLD_SECONDS");
        if (string.IsNullOrEmpty(raw) || !int.TryParse(raw, out int seconds) || seconds <= 0) return;
        Debug.LogWarning($"[ScriptAssemblyManager] Holding process {Environment.ProcessId} for {seconds}s for heap-dump root diagnostics (XENGINE_HOTRELOAD_HOLD_SECONDS).");
        var hold = System.Diagnostics.Stopwatch.StartNew();
        while (hold.Elapsed.TotalSeconds < seconds)
        {
            XEngine.Runtime.Window.PumpOsMessages();
            System.Threading.Thread.Sleep(50);
        }
    }

    /// <summary>
    /// Snapshots the assembly names for failure diagnostics, drops framework reflection caches
    /// keyed off the user assemblies, and clears the assembly list. Kept out of
    /// <see cref="UnloadScriptAssemblies"/> so the only frame alive during the GC loop never holds
    /// an <see cref="Assembly"/> reference (see the remarks there).
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static string[] PrepareAssembliesForUnload()
    {
        string[] assemblyNames = new string[s_scriptAssemblies.Count];
        for (int i = 0; i < s_scriptAssemblies.Count; i++)
            assemblyNames[i] = s_scriptAssemblies[i].GetName().Name ?? s_scriptAssemblies[i].FullName;

        foreach (var asm in s_scriptAssemblies)
            TypeDescriptor.Refresh(asm);
        s_scriptAssemblies.Clear();
        return assemblyNames;
    }

    /// <summary>
    /// Nulls the static field and unloads the context. The only strong reference to the context
    /// lives in this method's <c>ctx</c> local, which goes out of scope on return so the caller's
    /// GC loop can collect it. NoInlining keeps that local out of the caller's stack frame.
    /// </summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference UnloadContext()
    {
        AssemblyLoadContext ctx = s_scriptContext!;
        s_scriptContext = null;
        var weak = new WeakReference(ctx);
        ctx.Unload();
        return weak;
    }

    // ================================================================
    //  Hot-Reload
    // ================================================================

    /// <summary>Check if compiled assemblies exist for this project.</summary>
    public static bool HasScriptAssemblies(Project project)
        => Directory.Exists(project.ScriptAssemblyPath)
           && Directory.EnumerateFiles(project.ScriptAssemblyPath, "*.dll").Any();

    /// <summary>
    /// Attempt to hot-reload script assemblies without restarting the editor.
    /// Saves the scene, unloads old assemblies, loads new ones, reinitializes
    /// registries, and restores the scene. Returns false if unloading fails.
    /// </summary>
    private static bool TryHotReload(Project project)
    {
        try
        {
            // 1. Save the current scene state
            SaveSceneForRestart(project);
            EditorApplication.Instance?.SaveProjectState();
            XEngine.Runtime.Window.PumpOsMessages();

            // 2. Drop every editor/runtime strong reference into the old assemblies. Without this
            //    the collectible context stays rooted and the unload below fails, forcing a restart.
            EditorApplication.Instance?.ReleaseScriptReferences();

            // 3. Unload old assemblies - if this fails, caller will restart.
            if (!UnloadScriptAssemblies())
                return false;

            DebounceDelay = TimeSpan.FromSeconds(1);
            XEngine.Runtime.Window.PumpOsMessages();

            // 4. Load the new assemblies into a fresh context.
            LoadAssemblies(project);

            // 5. Reinitialize all editor registries (re-scans assemblies for attributes)
            EditorApplication.Instance?.ReinitializeAfterReload();
            XEngine.Runtime.Window.PumpOsMessages();

            // 6. Restore the scene
            string autoSavePath = project.AutoSaveScenePath;
            if (File.Exists(autoSavePath))
                EditorApplication.Instance?.RestoreAutoSavedScene(autoSavePath);

            EditorApplication.Instance?.RestoreSelectionAfterReload();

            Runtime.Debug.LogSuccess("[ScriptAssemblyManager] Hot-reload successful!");
            return true;
        }
        catch (Exception ex)
        {
            Runtime.Debug.LogError($"[ScriptAssemblyManager] Hot-reload failed: {ex.Message}\n{ex.StackTrace}");
            return false;
        }
    }

    // ================================================================
    //  Editor Restart (fallback)
    // ================================================================

    /// <summary>Save all state and restart the editor process.</summary>
    private static void RestartEditor(Project project)
    {
        SaveSceneForRestart(project);
        EditorApplication.Instance?.SaveProjectState();

        string? exePath = Environment.ProcessPath;
        if (string.IsNullOrEmpty(exePath))
        {
            // The scene was already auto-saved above, but the in-memory state (scene, registries,
            // script references) has been torn down by the failed reload. Lingering would leave a
            // half-broken, effectively hung editor - exiting is the safer fallback.
            Runtime.Debug.LogError("[ScriptAssemblyManager] Cannot determine editor executable path for restart. Exiting (scene was auto-saved).");
            Environment.Exit(1);
            return;
        }

        string args = $"--project \"{project.RootPath}\" --restart-parent {Environment.ProcessId}";
        if (File.Exists(project.AutoSaveScenePath))
            args += $" --restore-scene \"{project.AutoSaveScenePath}\"";
        if (!string.IsNullOrEmpty(Program.RunMenuPath))
            args += $" --run-menu \"{Program.RunMenuPath}\"";

        try
        {
            System.Diagnostics.Process? child;

            if (System.Runtime.InteropServices.RuntimeInformation.IsOSPlatform(System.Runtime.InteropServices.OSPlatform.OSX)
                && TryFindAppBundle(exePath, out string? appBundle))
            {
                string openArgs = $"-n -a \"{appBundle}\" --args {args}";
                Runtime.Debug.Log($"[ScriptAssemblyManager] Restarting via open: {openArgs}");
                child = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = "/usr/bin/open",
                    Arguments = openArgs,
                    UseShellExecute = false,
                });
            }
            else
            {
                Runtime.Debug.Log($"[ScriptAssemblyManager] Restarting: {exePath} {args}");
                child = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                {
                    FileName = exePath,
                    Arguments = args,
                    UseShellExecute = false,
                });
            }

            if (child != null)
                Runtime.Debug.Log($"[ScriptAssemblyManager] Spawned PID {child.Id}");
        }
        catch (Exception ex)
        {
            // Same reasoning as above: the failed reload already destroyed the live state, so keep
            // going and exit rather than strand the user in a broken editor window.
            Runtime.Debug.LogError($"[ScriptAssemblyManager] Failed to restart ({ex.Message}). Exiting; the auto-saved scene can be recovered manually.");
            Environment.Exit(1);
            return;
        }

        Environment.Exit(0);
    }

    private static bool TryFindAppBundle(string executablePath, out string? appBundle)
    {
        appBundle = null;
        var dir = new DirectoryInfo(Path.GetDirectoryName(executablePath) ?? "");
        while (dir != null)
        {
            if (dir.Extension.Equals(".app", StringComparison.OrdinalIgnoreCase))
            {
                appBundle = dir.FullName;
                return true;
            }
            dir = dir.Parent;
        }
        return false;
    }

    // ================================================================
    //  Scene Save/Restore
    // ================================================================

    private static void SaveSceneForRestart(Project project)
    {
        if (PrefabEditingMode.IsEditing)
            PrefabEditingMode.Exit();

        var scene = Scene.Current;
        if (scene == null) return;

        try
        {
            var savedId = scene.AssetID;
            scene.AssetID = Guid.Empty;

            var echo = Serializer.Serialize(scene);
            scene.AssetID = savedId;

            if (echo != null)
            {
                File.WriteAllText(project.AutoSaveScenePath, echo.WriteToString());

                string? originalPath = EditorSceneManager.CurrentScenePath;
                string sidecar = (originalPath ?? "") + "\n" + savedId.ToString();
                File.WriteAllText(project.AutoSaveScenePath + ".meta", sidecar);

                Runtime.Debug.Log("[ScriptAssemblyManager] Scene auto-saved for restart.");
            }
        }
        catch (Exception ex)
        {
            Runtime.Debug.LogWarning($"[ScriptAssemblyManager] Failed to auto-save scene: {ex.Message}");
        }
    }
}
