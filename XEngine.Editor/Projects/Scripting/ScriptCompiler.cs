using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;

using XEngine.Editor.Projects.Packages;
using XEngine.Editor.Projects.Settings;
using XEngine.Editor.Build;

namespace XEngine.Editor.Projects.Scripting;

/// <summary>
/// Generates .csproj files for user scripts and compiles them via `dotnet build`.
///
/// User code is split into one assembly per <see cref="AssemblyDefinition"/> (.asmdef), plus two
/// default assemblies for scripts not covered by any asmdef: <c>{Project}.Game</c> (runtime) and
/// <c>{Project}.Editor</c> (editor-only). Managed plugins under <c>Plugins/</c> folders are
/// referenced automatically (or explicitly via an asmdef's precompiled references).
/// </summary>
public static class ScriptCompiler
{
    public struct CompileResult
    {
        public bool Success;
        public string Output;
        public string Errors;
        public bool RequiresReload;   // true when script assemblies were rebuilt and need a hot-reload
    }

    /// <summary>Per-assembly progress emitted before each unit compiles (index is 0-based).</summary>
    public readonly record struct CompileProgress(int Index, int Total, string AssemblyName);

    /// <summary>A single user assembly to be generated and compiled.</summary>
    internal sealed class CompilationUnit
    {
        public required string Name;
        public bool IsEditorOnly;   // editor-side: gets editor-only packages/plugins, never shipped in a build
        public bool IsInterpreted;  // compiled to the IL-interpreter module folder, never CLR-loaded or shipped
        public bool AllowUnsafe = true;
        public bool NoEngineReferences;
        public AsmDefFile? Source;  // null for the default Game/Editor assemblies
        public readonly List<string> Scripts = new();
        public readonly List<string> AssemblyReferences = new();   // other unit names
        public readonly List<string> ManagedPluginPaths = new();   // absolute .dll paths

        public string CsprojPath = "";
        public string OutputDllPath = "";
    }

    // ================================================================
    //  Public entry point
    // ================================================================

    /// <summary>Generate .csproj files for all user assemblies and compile them in dependency order.</summary>
    public static CompileResult CompileAll(Project project, Action<CompileProgress>? onProgress = null)
    {
        var (units, error) = BuildPlan(project);
        if (error != null)
            return new CompileResult { Success = false, Errors = error };

        // Generate the default Game/Editor csproj (and any asmdef unit that owns scripts) up front so
        // NuGet packages restore and IDEs resolve references even before the first script exists.
        var generatedUnits = units.Where(u => u.Source == null || u.Scripts.Count > 0).ToList();
        foreach (var unit in generatedUnits)
            GenerateCsproj(project, unit, units);

        // Give IDEs a single solution to open for all generated script assemblies. Only written when
        // absent: the file is a user-editable entry point (they may add their own projects to it).
        GenerateSolutionIfMissing(project, generatedUnits);

        var compileUnits = units.Where(u => u.Scripts.Count > 0).ToList();
        if (compileUnits.Count == 0)
            return RestorePackagesOnly(project, units);

        var output = new StringBuilder();
        var errors = new StringBuilder();

        // Compile in dependency order so each unit's referenced DLLs already exist on disk.
        // `-t:Rebuild` forces a full recompile even when MSBuild thinks inputs are unchanged,
        // avoiding the stale-DLL bug where edits appear to compile but the old code still runs.
        for (int i = 0; i < compileUnits.Count; i++)
        {
            var unit = compileUnits[i];
            onProgress?.Invoke(new CompileProgress(i, compileUnits.Count, unit.Name));
            Runtime.Debug.Log($"[ScriptCompiler] Compiling {unit.Name} ({unit.Scripts.Count} scripts)...");
            var result = RunDotnetCommand($"build \"{unit.CsprojPath}\" --configuration Release -t:Rebuild", project.RootPath);
            output.AppendLine(result.stdout);
            if (!string.IsNullOrEmpty(result.stderr))
                errors.AppendLine(result.stderr);

            if (result.exitCode != 0)
            {
                Runtime.Debug.LogError($"[ScriptCompiler] {unit.Name} compilation failed.");
                LogBuildOutput(result.stdout, result.stderr);
                return new CompileResult { Success = false, Output = output.ToString(), Errors = errors.ToString() };
            }
            Runtime.Debug.Log($"[ScriptCompiler] {unit.Name} compiled successfully.");
        }

        WritePlanStamp(project, units);

        return new CompileResult { Success = true, Output = output.ToString(), Errors = errors.ToString(), RequiresReload = true };
    }

    // ================================================================
    //  Staleness detection
    // ================================================================

    /// <summary>
    /// True when the compiled script assemblies are missing or stale relative to the current build
    /// plan, i.e. loading the existing DLLs would run outdated code. Covers three cases: an output
    /// DLL is missing, a compilation input (script, asmdef, managed plugin, Directory.Build.props)
    /// is newer than its output DLL, or the plan identity recorded at the last successful compile
    /// differs from the current one. The identity check is what catches a built-in package version
    /// bump: the freshly materialized PackageCache root preserves source timestamps (File.Copy),
    /// so only the changed script paths reveal that the old DLLs no longer match.
    /// </summary>
    public static bool IsCompilationStale(Project project)
    {
        var (units, error) = BuildPlan(project);
        if (error != null)
            return true; // Let CompileAll surface the plan error instead of silently loading old DLLs.

        bool anyScripts = false;
        foreach (var unit in units)
        {
            if (unit.Scripts.Count == 0) continue;
            anyScripts = true;

            if (!File.Exists(unit.OutputDllPath))
                return true;
            if (NewestInputTimeUtc(project, unit) > File.GetLastWriteTimeUtc(unit.OutputDllPath))
                return true;
        }
        if (!anyScripts)
            return false;

        // A missing stamp (project last compiled before stamps existed) also reads as stale, which
        // heals outputs that may already have been poisoned by an undetected plan change.
        string? recorded = null;
        try
        {
            string stampPath = GetPlanStampPath(project);
            if (File.Exists(stampPath)) recorded = File.ReadAllText(stampPath);
        }
        catch { }
        return !string.Equals(recorded, ComputePlanStamp(units), StringComparison.Ordinal);
    }

    /// <summary>Newest last-write time among everything that feeds this unit's compilation.</summary>
    private static DateTime NewestInputTimeUtc(Project project, CompilationUnit unit)
    {
        // GetLastWriteTimeUtc returns a sentinel far in the past for missing files, so races where
        // an input disappears mid-check simply don't count toward "newest".
        DateTime newest = DateTime.MinValue;
        void Consider(string path)
        {
            DateTime t = File.GetLastWriteTimeUtc(path);
            if (t > newest) newest = t;
        }

        foreach (var script in unit.Scripts) Consider(script);
        foreach (var plugin in unit.ManagedPluginPaths) Consider(plugin);
        if (unit.Source != null) Consider(unit.Source.FilePath);
        Consider(Path.Combine(project.RootPath, "Directory.Build.props")); // NuGet package edits
        return newest;
    }

    internal static string GetPlanStampPath(Project project)
        => Path.Combine(project.ScriptAssemblyPath, "buildplan.stamp");

    /// <summary>
    /// Deterministic fingerprint of the build plan: engine version plus, per producing unit, its
    /// flags and full input paths (absolute, so a resolved package cache root like
    /// <c>PackageCache/com.xengine.spine@0.1.1</c> changes the stamp when the version changes).
    /// Timestamps are deliberately excluded - in-place edits are caught by the write-time check.
    /// </summary>
    private static string ComputePlanStamp(List<CompilationUnit> units)
    {
        var sb = new StringBuilder();
        sb.Append("v1|").Append(GetVersionDefine()).Append('\n');
        foreach (var unit in units.Where(u => u.Scripts.Count > 0).OrderBy(u => u.Name, StringComparer.OrdinalIgnoreCase))
        {
            sb.Append(unit.Name).Append('|')
              .Append(unit.IsEditorOnly ? 'E' : 'G').Append(unit.AllowUnsafe ? 'U' : 'u')
              .Append(unit.NoEngineReferences ? 'N' : 'n').Append('|')
              .Append(unit.Source?.FilePath ?? "").Append('\n');
            foreach (var s in unit.Scripts.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                sb.Append(" s:").Append(s).Append('\n');
            foreach (var r in unit.AssemblyReferences.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                sb.Append(" r:").Append(r).Append('\n');
            foreach (var p in unit.ManagedPluginPaths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase))
                sb.Append(" p:").Append(p).Append('\n');
        }
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(sb.ToString())));
    }

    private static void WritePlanStamp(Project project, List<CompilationUnit> units)
    {
        try
        {
            Directory.CreateDirectory(project.ScriptAssemblyPath);
            File.WriteAllText(GetPlanStampPath(project), ComputePlanStamp(units));
        }
        catch (Exception ex)
        {
            // Never fail a successful compile over the stamp; worst case the next startup recompiles.
            Runtime.Debug.LogWarning($"[ScriptCompiler] Failed to record build plan stamp: {ex.Message}");
        }
    }

    /// <summary>
    /// No scripts to build, so restore the default assemblies instead. This validates configured NuGet
    /// packages (a bad name or version surfaces now instead of only after the first script is written)
    /// and lets IDEs resolve them. Returns success with nothing to reload when there is nothing to do.
    /// </summary>
    private static CompileResult RestorePackagesOnly(Project project, List<CompilationUnit> units)
    {
        if (!ProjectDeclaresPackages(project))
            return new CompileResult { Success = true, Output = "No scripts to compile." };

        var output = new StringBuilder();
        var errors = new StringBuilder();

        foreach (var unit in units.Where(u => u.Source == null))
        {
            Runtime.Debug.Log($"[ScriptCompiler] Restoring packages for {unit.Name}...");
            var result = RunDotnetCommand($"restore \"{unit.CsprojPath}\"", project.RootPath);
            output.AppendLine(result.stdout);
            if (!string.IsNullOrEmpty(result.stderr))
                errors.AppendLine(result.stderr);

            if (result.exitCode != 0)
            {
                Runtime.Debug.LogError($"[ScriptCompiler] Package restore failed for {unit.Name}.");
                LogBuildOutput(result.stdout, result.stderr);
                return new CompileResult { Success = false, Output = output.ToString(), Errors = errors.ToString() };
            }
        }

        Runtime.Debug.Log("[ScriptCompiler] Package restore complete.");
        return new CompileResult { Success = true, Output = output.ToString(), Errors = errors.ToString() };
    }

    /// <summary>
    /// Output DLL paths for every user assembly that has scripts, in dependency order.
    /// Used by the assembly manager to load them all into the editor.
    /// </summary>
    public static List<string> GetEditorAssemblyPaths(Project project)
    {
        var (units, _) = BuildPlan(project);
        // Interpreted assemblies are VM data: they compile to the IL-interpreter module
        // folder and are never loaded through the CLR (the interpreter reads them as files).
        return units.Where(u => u.Scripts.Count > 0 && !u.IsInterpreted).Select(u => u.OutputDllPath).ToList();
    }

    /// <summary>
    /// True when the current project or any resolved package contributes a compiled assembly.
    /// This deliberately includes Editor-shipped packages: they are compilation inputs even when a
    /// newly created project has no manifest entry or scripts under Assets.
    /// </summary>
    internal static bool HasCompilationSources(Project project)
    {
        var (units, error) = BuildPlan(project);
        if (error != null)
            return true; // Let CompileAll surface the plan error instead of silently skipping it.

        for (int i = 0; i < units.Count; i++)
            if (units[i].Scripts.Count > 0)
                return true;

        return false;
    }

    /// <summary>A user assembly destined for a player build.</summary>
    public readonly record struct BuildAssembly(string Name, string DllPath);

    /// <summary>
    /// Game-side user assemblies (not editor-only, included for the target platform) in dependency
    /// order. These are copied next to the player and loaded at startup.
    /// </summary>
    public static List<BuildAssembly> GetBuildAssemblies(Project project, string targetPlatform)
    {
        var (units, _) = BuildPlan(project);
        var result = new List<BuildAssembly>();

        // Build a map of package-root -> SupportedPlatforms so a player build for a target platform
        // can drop any package (and therefore its asmdefs) that doesn't support that target. This is
        // the package-level gate that complements the asmdef's own IncludePlatforms/ExcludePlatforms.
        var packages = Packages.PackageManager.Resolve(project);
        var packagePlatformMap = new Dictionary<string, HashSet<string>>(StringComparer.OrdinalIgnoreCase);
        foreach (var pkg in packages)
            packagePlatformMap[pkg.ResolvedRoot] = pkg.SupportedPlatforms;

        foreach (var unit in units)
        {
            if (unit.IsEditorOnly || unit.Scripts.Count == 0) continue;
            // Interpreted assemblies ride in the build as hot-update content, not as
            // managed player assemblies: they must never enter the AOT reference closure.
            if (unit.IsInterpreted) continue;
            if (unit.Source != null && !unit.Source.Definition.IncludedFor(targetPlatform)) continue;

            // Package-level platform gate: if this asmdef lives inside a package that declares
            // supportedPlatforms not covering the build target, skip the whole assembly.
            if (unit.Source != null && IsPackageExcludedForPlatform(unit.Source.FilePath, packagePlatformMap, targetPlatform))
                continue;

            result.Add(new BuildAssembly(unit.Name, unit.OutputDllPath));
        }
        PlayerAssemblyDependencyAudit.ValidateRuntimeClosure(result);
        return result;
    }

    /// <summary>True when the asmdef at <paramref name="asmdefPath"/> lives in a package whose
    /// supportedPlatforms does not include <paramref name="targetPlatform"/>.</summary>
    private static bool IsPackageExcludedForPlatform(
        string asmdefPath,
        Dictionary<string, HashSet<string>> packagePlatformMap,
        string targetPlatform)
    {
        foreach (var (root, supported) in packagePlatformMap)
        {
            if (supported.Count == 0) continue; // empty = all platforms supported
            if (!asmdefPath.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                && !asmdefPath.StartsWith(root + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                continue;
            // The asmdef is inside this package; check platform support (case-insensitive).
            bool ok = false;
            foreach (var p in supported)
                if (p.Equals(targetPlatform, StringComparison.OrdinalIgnoreCase)) { ok = true; break; }
            return !ok;
        }
        return false;
    }

    // ================================================================
    //  Build plan
    // ================================================================

    private static (List<CompilationUnit> units, string? error) BuildPlan(Project project)
    {
        var asmdefs = AssemblyDefinitionDatabase.LoadAll(project);

        // Reject duplicate assembly names early - they would clobber each other's output.
        var dupes = asmdefs.GroupBy(a => a.Name, StringComparer.OrdinalIgnoreCase).Where(g => g.Count() > 1).ToList();
        if (dupes.Count > 0)
            return (new(), $"Duplicate assembly definition name(s): {string.Join(", ", dupes.Select(d => d.Key))}");

        // Classification: interpreted assemblies (com.xengine.ilinterpreter). The flag on the
        // asmdef is inert while the package is not installed; with the package present, host
        // (non-interpreted) assemblies must not reference interpreted ones - the dependency
        // direction is interpreted -> contracts/host-registry only, enforced before anything compiles.
        var interpretedActive = IsInterpreterInstalled(project);
        var interpretedNames = new HashSet<string>(
            asmdefs.Where(a => a.Definition.IsInterpreted && interpretedActive).Select(a => a.Name),
            StringComparer.OrdinalIgnoreCase);
        foreach (var file in asmdefs)
        {
            if (file.Definition.IsInterpreted) continue;
            foreach (var refName in file.Definition.References)
            {
                if (interpretedNames.Contains(refName))
                    return (new(), $"[ILInterpreter] Host assembly '{file.Name}' must not reference interpreted assembly '{refName}'. " +
                        $"Interpreted assemblies are hot-update data: reference them only from other interpreted assemblies or via host bindings.");
            }
        }

        // Reject asmdef names reserved for the default assemblies - they would clobber the defaults.
        var reserved = new[] { $"{project.Name}.Game", $"{project.Name}.Editor", $"{project.Name}.Tests" };
        var clashing = asmdefs.Where(a => reserved.Contains(a.Name, StringComparer.OrdinalIgnoreCase))
            .Select(a => a.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
        if (clashing.Count > 0)
            return (new(), $"Assembly definition name(s) reserved for the default assemblies: {string.Join(", ", clashing)}");

        string defaultDefines = $"XENGINE;XENGINE_EDITOR;{GetVersionDefine()};{GetEditorPlatformDefines()}";
        var activeDefines = new HashSet<string>(defaultDefines.Split(';'), StringComparer.OrdinalIgnoreCase);

        var units = new List<CompilationUnit>();
        var byName = new Dictionary<string, CompilationUnit>(StringComparer.OrdinalIgnoreCase);

        // Default assemblies (scripts not owned by any asmdef).
        var defaultGame = new CompilationUnit { Name = $"{project.Name}.Game", IsEditorOnly = false };
        var defaultEditor = new CompilationUnit { Name = $"{project.Name}.Editor", IsEditorOnly = true };
        // Default test assembly: a testable package's Tests/ subfolder (and any loose Tests/ scripts
        // under Assets/) compile here. Marked editor-only so it never ships in a player build; the
        // unit only emits a DLL when something routes into it, so projects with no testables pay nothing.
        var defaultTests = new CompilationUnit { Name = $"{project.Name}.Tests", IsEditorOnly = true };
        units.Add(defaultGame);
        units.Add(defaultEditor);
        units.Add(defaultTests);

        // Load the manifest once to look up testables (the package names whose Tests/ participates).
        var testables = LoadTestables(project);

        // Asmdef-defined assemblies (only those whose define constraints are satisfied).
        var asmdefUnits = new List<(AsmDefFile file, CompilationUnit unit)>();
        foreach (var file in asmdefs)
        {
            if (!EvaluateDefineConstraints(file.Definition.DefineConstraints, activeDefines))
                continue;

            var unit = new CompilationUnit
            {
                Name = file.Name,
                IsEditorOnly = file.Definition.IsEditorOnly,
                AllowUnsafe = file.Definition.AllowUnsafeCode,
                NoEngineReferences = file.Definition.NoEngineReferences,
                IsInterpreted = file.Definition.IsInterpreted && interpretedActive,
                Source = file,
            };
            units.Add(unit);
            asmdefUnits.Add((file, unit));
        }

        foreach (var unit in units)
            byName[unit.Name] = unit;

        // Classify scripts into their owning assembly. Walk both the project Assets/ root and every
        // resolved package root (package asmdefs are already in `asmdefs`, so FindOwner assigns them
        // correctly; loose package scripts — those without an owning asmdef — go to the project
        // defaults so a minimal package with just a Runtime/*.cs still compiles).
        var resolvedPackages = Packages.PackageManager.Resolve(project);
        var scriptRoots = new List<(string root, bool isPackage, string packageName)> { (project.AssetsPath, false, "") };
        foreach (var pkg in resolvedPackages)
            scriptRoots.Add((pkg.ResolvedRoot, true, pkg.Name));

        foreach (var (root, isPackage, packageName) in scriptRoots)
        {
            if (!Directory.Exists(root)) continue;
            foreach (var script in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                if (isPackage && Packages.PackageManager.IsUnderIgnoredPackageDir(script, root)) continue;

                // Tests/ routing: a script under a "Tests" path segment from a testable package (or
                // from Assets/, which is always testable) goes into the default Tests unit rather than
                // Game/Editor. This makes manifest "testables" actually take effect.
                if (HasTestsSegment(script) && (!isPackage || testables.Contains(packageName)))
                {
                    defaultTests.Scripts.Add(script);
                    continue;
                }

                var owner = AssemblyDefinitionDatabase.FindOwner(script, asmdefs);
                if (owner != null)
                {
                    // Owned by an asmdef. If that asmdef was excluded by define constraints it has no
                    // unit, so the script is simply not compiled (it must NOT leak into the defaults).
                    if (byName.TryGetValue(owner.Name, out var ownerUnit))
                        ownerUnit.Scripts.Add(script);
                }
                else
                {
                    (PackageManager.HasEditorSegment(script) ? defaultEditor : defaultGame).Scripts.Add(script);
                }
            }
        }

        // Resolve plugin and assembly references.
        var plugins = PluginScanner.ScanAll(project);
        var autoManaged = plugins.Where(p => p.IsManaged && p.AutoReferenced).ToList();
        // TryAdd (not ToDictionary) so duplicate plugin file names don't throw; first one wins.
        var managedByFile = new Dictionary<string, PluginInfo>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in plugins.Where(p => p.IsManaged))
            managedByFile.TryAdd(p.FileName, p);

        // Set of unit names that actually produce a DLL (have scripts) - only these are referenceable.
        var producing = new HashSet<string>(units.Where(u => u.Scripts.Count > 0).Select(u => u.Name), StringComparer.OrdinalIgnoreCase);

        foreach (var unit in units)
        {
            // Managed plugin references.
            IEnumerable<PluginInfo> pluginRefs;
            if (unit.Source is { Definition.OverrideReferences: true } src)
            {
                pluginRefs = src.Definition.PrecompiledReferences
                    .Select(name => managedByFile.GetValueOrDefault(name))
                    .Where(p => p != null)!;
            }
            else
            {
                // Game assemblies may only reference non-editor-only plugins (build separation);
                // editor-only assemblies can reference everything.
                pluginRefs = autoManaged.Where(p => unit.IsEditorOnly || !p.EditorOnly);
            }
            foreach (var p in pluginRefs)
                unit.ManagedPluginPaths.Add(p!.AbsolutePath);

            // Assembly references.
            if (unit.Source != null)
            {
                foreach (var refName in unit.Source.Definition.References)
                {
                    if (refName.Equals(unit.Name, StringComparison.OrdinalIgnoreCase))
                        continue; // ignore self-reference
                    if (producing.Contains(refName))
                        unit.AssemblyReferences.Add(refName);
                    else if (byName.ContainsKey(refName))
                        { /* referenced asmdef has no scripts -> nothing to link */ }
                    else if (IsImplicitEngineAssembly(refName))
                        { /* XEngine.Runtime/Editor are added by GenerateCsproj, not user units. */ }
                    else
                        Runtime.Debug.LogWarning($"[ScriptCompiler] {unit.Name} references unknown assembly '{refName}'.");
                }
            }
        }

        // Default assemblies auto-reference auto-referenced asmdef assemblies, so loose scripts can
        // use asmdef code without an explicit reference.
        foreach (var (file, unit) in asmdefUnits)
        {
            if (unit.Scripts.Count == 0 || !file.Definition.AutoReferenced) continue;
            // Interpreted assemblies are invisible to loose scripts and defaults: the VM
            // resolves them through the module set, never through the CLR reference graph.
            if (unit.IsInterpreted) continue;

            // The default Game ships to every player platform, so it may only auto-reference an asmdef
            // that also ships everywhere - otherwise a build for an excluded platform would carry a
            // dangling reference. The default Editor never ships, so it can reference anything.
            bool shipsEverywhere = BuildPlatforms.Players.All(p => file.Definition.IncludedFor(p));
            if (!unit.IsEditorOnly && shipsEverywhere)
                defaultGame.AssemblyReferences.Add(unit.Name);
            defaultEditor.AssemblyReferences.Add(unit.Name);
            // The default Tests assembly never ships (editor-only) so, like Editor, it can reference
            // any auto-referenced asmdef — test code needs to exercise package/runtime code freely.
            if (defaultTests.Scripts.Count > 0)
                defaultTests.AssemblyReferences.Add(unit.Name);
        }
        if (defaultGame.Scripts.Count > 0)
            defaultEditor.AssemblyReferences.Add(defaultGame.Name);

        // The Tests assembly may reference Game and Editor so test code can exercise both; it never
        // ships in a player build (IsEditorOnly = true), so these references are editor-only.
        if (defaultTests.Scripts.Count > 0)
        {
            if (defaultGame.Scripts.Count > 0) defaultTests.AssemblyReferences.Add(defaultGame.Name);
            if (defaultEditor.Scripts.Count > 0) defaultTests.AssemblyReferences.Add(defaultEditor.Name);
        }

        // Fill in generated file paths. Interpreted assemblies compile to their own module
        // folder so the ordinary script-assembly loader and player collectors never see them.
        string interpretedModuleRoot = Path.Combine(project.LibraryPath, "ILInterpreter", "Modules");
        foreach (var unit in units)
        {
            unit.CsprojPath = Path.Combine(project.RootPath, $"{unit.Name}.csproj");
            unit.OutputDllPath = unit.IsInterpreted
                ? Path.Combine(interpretedModuleRoot, unit.Name, $"{unit.Name}.dll")
                : Path.Combine(project.ScriptAssemblyPath, $"{unit.Name}.dll");
        }

        // Order by dependency (referenced assemblies build first).
        var (ordered, cycle) = TopologicalSort(units, byName);
        if (cycle != null)
            return (new(), $"Cyclic assembly references detected: {cycle}");

        return (ordered, null);
    }

    /// <summary>Whether the official IL interpreter package is installed for this project.
    /// A name check only - the core never references package internals (plan §7).</summary>
    internal static bool IsInterpreterInstalled(Project project)
        => Packages.PackageManager.Resolve(project)
            .Any(p => p.Name.Equals("com.xengine.ilinterpreter", StringComparison.OrdinalIgnoreCase));

    private static bool IsImplicitEngineAssembly(string name)
        => name.Equals("XEngine.Runtime", StringComparison.OrdinalIgnoreCase)
            || name.Equals("XEngine.Editor", StringComparison.OrdinalIgnoreCase);

    private static (List<CompilationUnit> ordered, string? cycle) TopologicalSort(
        List<CompilationUnit> units, Dictionary<string, CompilationUnit> byName)
    {
        var ordered = new List<CompilationUnit>();
        var state = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase); // 0=unseen,1=visiting,2=done
        string? cycle = null;

        void Visit(CompilationUnit u, List<string> stack)
        {
            if (cycle != null) return;
            state.TryGetValue(u.Name, out int s);
            if (s == 2) return;
            if (s == 1)
            {
                cycle = string.Join(" -> ", stack.SkipWhile(n => !n.Equals(u.Name, StringComparison.OrdinalIgnoreCase)).Append(u.Name));
                return;
            }

            state[u.Name] = 1;
            stack.Add(u.Name);
            foreach (var dep in u.AssemblyReferences)
                if (byName.TryGetValue(dep, out var depUnit))
                    Visit(depUnit, stack);
            stack.RemoveAt(stack.Count - 1);
            state[u.Name] = 2;
            ordered.Add(u);
        }

        foreach (var u in units)
            Visit(u, new List<string>());

        return (cycle == null ? ordered : new(), cycle);
    }

    // ================================================================
    //  csproj generation
    // ================================================================

    /// <summary>
    /// Writes a .slnx listing the generated csproj files, so an IDE has a single solution to open.
    /// Only created when absent: the file is a user-editable entry point (they may add their own
    /// projects to it). Ported from upstream (e505a1d5).
    /// </summary>
    private static void GenerateSolutionIfMissing(Project project, List<CompilationUnit> generatedUnits)
    {
        if (generatedUnits.Count == 0) return;
        string solutionPath = Path.Combine(project.RootPath, $"{project.Name}.slnx");
        if (File.Exists(solutionPath)) return;

        var sb = new StringBuilder();
        sb.AppendLine("<Solution>");
        foreach (var unit in generatedUnits.OrderBy(u => u.Name, StringComparer.OrdinalIgnoreCase))
            sb.AppendLine($"  <Project Path=\"{Xml(GetProjectRelativePath(project, unit.CsprojPath))}\" />");
        sb.AppendLine("</Solution>");

        try
        {
            File.WriteAllText(solutionPath, sb.ToString());
        }
        catch (Exception ex)
        {
            // A missing solution file never blocks compilation.
            Runtime.Debug.LogWarning($"[ScriptCompiler] Failed to write solution file: {ex.Message}");
        }
    }

    /// <summary>
    /// macOS resolves /var (and /tmp) to /private/var; paths that cross that symlink make
    /// Path.GetRelativePath's purely lexical "../" chains off by one segment for csc, which
    /// fails CS2001. Normalize the prefix so the relative-math base matches the real location.
    /// </summary>
    internal static string NormalizeMacSymlinkPrefix(string path)
    {
        if (OperatingSystem.IsMacOS())
        {
            if (path == "/var" || path.StartsWith("/var/", StringComparison.Ordinal))
                return "/private/var" + path[4..];
            if (path == "/tmp" || path.StartsWith("/tmp/", StringComparison.Ordinal))
                return "/private/tmp" + path[4..];
        }
        return path;
    }

    private static string GetProjectRelativePath(Project project, string path)
    {
        string destination = NormalizeMacSymlinkPrefix(path);
        string relative = Path.GetRelativePath(NormalizeMacSymlinkPrefix(project.RootPath), destination);
        // MSBuild resolves the project directory through symlinks. Internal paths remain valid
        // relative to that directory, but an external ../ chain computed from the alias does not.
        // Keep external package sources absolute so compilation also works through a linked root
        // or parent directory, without resolving every existing/nonexistent path component.
        return relative == ".."
            || relative.StartsWith(".." + Path.DirectorySeparatorChar, StringComparison.Ordinal)
            || Path.IsPathRooted(relative)
            ? destination
            : relative;
    }

    private static void GenerateCsproj(Project project, CompilationUnit unit, List<CompilationUnit> allUnits)
    {
        string engineDir = Path.GetDirectoryName(Assembly.GetExecutingAssembly().Location)!;
        string outputDir = GetProjectRelativePath(
            project, Path.GetDirectoryName(unit.OutputDllPath) ?? project.ScriptAssemblyPath);

        // Only the user's own output assemblies must be excluded from the engine-reference sweep -
        // match them by exact name, never by a name prefix (a project named "XEngine" must still get
        // XEngine.Echo etc.).
        var unitNames = new HashSet<string>(allUnits.Select(u => u.Name), StringComparer.OrdinalIgnoreCase);
        var emitted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        var sb = new StringBuilder();
        sb.AppendLine("<Project Sdk=\"Microsoft.NET.Sdk\">");
        sb.AppendLine("  <PropertyGroup>");
        sb.AppendLine("    <TargetFramework>net10.0</TargetFramework>");
        // EnableDefaultItems=false stops MSBuild from auto-globbing DLLs sitting next to the
        // project; every reference is added explicitly below.
        sb.AppendLine("    <EnableDefaultItems>false</EnableDefaultItems>");
        sb.AppendLine($"    <AllowUnsafeBlocks>{(unit.AllowUnsafe ? "true" : "false")}</AllowUnsafeBlocks>");
        sb.AppendLine("    <Nullable>enable</Nullable>");
        sb.AppendLine($"    <OutputPath>{Xml(outputDir)}</OutputPath>");
        sb.AppendLine("    <AppendTargetFrameworkToOutputPath>false</AppendTargetFrameworkToOutputPath>");
        sb.AppendLine("    <AppendRuntimeIdentifierToOutputPath>false</AppendRuntimeIdentifierToOutputPath>");
        sb.AppendLine($"    <AssemblyName>{Xml(unit.Name)}</AssemblyName>");
        string defines = unit.IsEditorOnly
            ? $"XENGINE;XENGINE_EDITOR;{GetVersionDefine()};{GetEditorPlatformDefines()}"
            : $"XENGINE;{GetVersionDefine()}";
        sb.AppendLine($"    <DefineConstants>{defines}</DefineConstants>");
        sb.AppendLine("  </PropertyGroup>");

        sb.AppendLine("  <ItemGroup>");

        // Engine references (Private=true forces use of the HintPath instead of local probing).
        if (!unit.NoEngineReferences)
        {
            AppendReference(sb, emitted, "XEngine.Runtime", Path.Combine(engineDir, "XEngine.Runtime.dll"), copyLocal: true);
            if (unit.IsEditorOnly)
                AppendReference(sb, emitted, "XEngine.Editor", Path.Combine(engineDir, "XEngine.Editor.dll"), copyLocal: true);

            foreach (var dll in Directory.EnumerateFiles(engineDir, "*.dll"))
            {
                string name = Path.GetFileNameWithoutExtension(dll);
                if (name == "XEngine.Runtime" || name == "XEngine.Editor"
                    || unitNames.Contains(name)) continue;

                // The editor's install dir ships no test assemblies, but under a unit-test host this
                // sweep runs against the test bin. The test assembly compiles engine-namespace package
                // sources into itself (for coverage), so referencing it aliases those types against
                // the compiled package assemblies (CS0433).
                if (name.EndsWith(".Test", StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith(".Tests", StringComparison.OrdinalIgnoreCase)) continue;

                // A self-contained XEngine publish drops the whole .NET runtime next to the app. Never
                // reference the shared-framework assemblies (esp. System.Private.CoreLib): they are the
                // *implementation* copies, and referencing them on top of the SDK's reference assemblies
                // gives the compiler two definitions of System.Object/String/etc. (CS0433 -> CS0518).
                // Skip the native runtime libs too (coreclr/clrjit/hostfxr/... -> MSB3246 "bad image").
                if (IsFrameworkAssembly(name)) continue;
                // The script-safety analyzers ship as a Roslyn <Analyzer> item (see below), not a
                // plain reference (upstream 9edc7a4f pattern).
                if (name == "XEngine.Analyzers") continue;
                if (!IsManagedAssembly(dll)) continue;

                AppendReference(sb, emitted, name, dll, copyLocal: true);
            }

            // Script-safety analyzers (PR0008-PR0010) over user scripts, so the warnings show in the
            // IDE too. Emits only when the DLL actually ships beside the editor.
            string analyzerDll = Path.Combine(engineDir, "XEngine.Analyzers.dll");
            if (File.Exists(analyzerDll))
                sb.AppendLine($"    <Analyzer Include=\"{Xml(analyzerDll)}\" />");
        }

        // Managed plugin references.
        foreach (var pluginPath in unit.ManagedPluginPaths)
            AppendReference(sb, emitted, Path.GetFileNameWithoutExtension(pluginPath), pluginPath, copyLocal: true);

        // Other user assemblies (built earlier). Interpreted units live in their own module
        // folder, so resolve each reference through the referenced unit's output path.
        var outputByUnitName = allUnits.ToDictionary(u => u.Name, u => u.OutputDllPath, StringComparer.OrdinalIgnoreCase);
        foreach (var refName in unit.AssemblyReferences)
        {
            string dll = outputByUnitName.TryGetValue(refName, out var referencedOutput)
                ? referencedOutput
                : Path.Combine(project.ScriptAssemblyPath, $"{refName}.dll");
            AppendReference(sb, emitted, refName, dll, copyLocal: false);
        }

        sb.AppendLine("  </ItemGroup>");

        // NuGet packages, project references, and any other user MSBuild come from the project's
        // Directory.Build.props, which MSBuild auto-imports into every generated csproj (this one lives
        // at the project root; the player build lives under Temp/ which is also inside the root).

        // Compile items.
        sb.AppendLine("  <ItemGroup>");
        foreach (var script in unit.Scripts)
            sb.AppendLine($"    <Compile Include=\"{Xml(GetProjectRelativePath(project, script))}\" />");
        sb.AppendLine("  </ItemGroup>");

        sb.AppendLine("</Project>");

        File.WriteAllText(unit.CsprojPath, sb.ToString());
    }

    internal static string GetEditorPlatformDefines()
    {
        try
        {
            BuildSettings settings = EditorRegistries.GetSettings<BuildSettings>();
            BuildPipeline pipeline = ProjectBuilder.ResolvePipeline(settings);
            return BuildPipeline.FinalizeDefineString(settings, pipeline);
        }
        catch
        {
            // Script editing stays available if settings have not been initialized yet.
            return "XENGINE_WINDOWS";
        }
    }

    /// <summary>Appends a Reference item, skipping duplicates (by Include name) and XML-escaping values.</summary>
    private static void AppendReference(StringBuilder sb, HashSet<string> emitted, string include, string hintPath, bool copyLocal)
    {
        if (!emitted.Add(include)) return; // a later reference with the same name (e.g. plugin vs engine DLL) is dropped
        sb.AppendLine($"    <Reference Include=\"{Xml(include)}\">");
        sb.AppendLine($"      <HintPath>{Xml(hintPath)}</HintPath>");
        sb.AppendLine($"      <Private>{(copyLocal ? "true" : "false")}</Private>");
        sb.AppendLine("    </Reference>");
    }

    /// <summary>
    /// True for .NET shared-framework / runtime assemblies. The SDK's targeting pack already supplies
    /// reference assemblies for all of these, so a generated csproj must never reference the runtime's
    /// own implementation copies (which sit in the engine folder only on a self-contained publish).
    /// </summary>
    private static bool IsFrameworkAssembly(string name)
    {
        if (name.StartsWith("System.", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.StartsWith("Microsoft.VisualBasic", StringComparison.OrdinalIgnoreCase)) return true;
        if (name.StartsWith("Microsoft.Win32.", StringComparison.OrdinalIgnoreCase)) return true;
        switch (name.ToLowerInvariant())
        {
            case "system":
            case "mscorlib":
            case "netstandard":
            case "windowsbase":
            case "microsoft.csharp":
            case "microsoft.diasymreader.native":
                return true;
            default:
                return false;
        }
    }

    /// <summary>True if the file is a managed .NET assembly. Native libraries (coreclr.dll, clrjit.dll,
    /// hostfxr.dll, msquic.dll, …) shipped by a self-contained publish are not compile references and
    /// otherwise produce MSB3246 "bad image" warnings.</summary>
    private static bool IsManagedAssembly(string path)
    {
        try { System.Reflection.AssemblyName.GetAssemblyName(path); return true; }
        catch (BadImageFormatException) { return false; } // native library
        catch { return false; }
    }

    /// <summary>XML-escapes a value for safe interpolation into a generated .csproj.</summary>
    private static string Xml(string value) => System.Security.SecurityElement.Escape(value) ?? value;

    // ================================================================
    //  Helpers (kept stable for callers outside this class)
    // ================================================================

    /// <summary>Classify every .cs file into game vs editor scripts (by an "Editor" folder segment),
    /// across the project Assets/ root and every resolved package root.</summary>
    internal static (List<string> game, List<string> editor) ClassifyScripts(Project project)
    {
        var game = new List<string>();
        var editor = new List<string>();

        var roots = new List<(string root, bool isPackage)> { (project.AssetsPath, false) };
        foreach (var pkg in PackageManager.Resolve(project))
            roots.Add((pkg.ResolvedRoot, true));

        foreach (var (root, isPackage) in roots)
        {
            if (!Directory.Exists(root)) continue;
            foreach (var file in Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories))
            {
                if (isPackage && PackageManager.IsUnderIgnoredPackageDir(file, root)) continue;
                (PackageManager.HasEditorSegment(file) ? editor : game).Add(file);
            }
        }

        return (game, editor);
    }

    /// <summary>Evaluates asmdef define constraints (supports "SYMBOL" and "!SYMBOL").</summary>
    private static bool EvaluateDefineConstraints(List<string> constraints, HashSet<string> defined)
    {
        foreach (var raw in constraints)
        {
            string c = raw.Trim();
            if (c.Length == 0) continue;
            if (c.StartsWith('!'))
            {
                if (defined.Contains(c[1..])) return false;
            }
            else if (!defined.Contains(c))
            {
                return false;
            }
        }
        return true;
    }

    /// <summary>Read the manifest's <c>testables</c> list (package names whose Tests/ subfolder
    /// participates in the test assembly). Returns an empty set when there's no manifest.</summary>
    private static HashSet<string> LoadTestables(Project project)
    {
        var manifestPath = Path.Combine(project.PackagesPath, "manifest.json");
        var manifest = ProjectPackageManifest.ReadOrDefault(manifestPath);
        var set = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var t in manifest.Testables)
            if (!string.IsNullOrWhiteSpace(t)) set.Add(t.Trim());
        return set;
    }

    /// <summary>True when any path segment of <paramref name="absolutePath"/> is literally "Tests"
    /// (case-insensitive). Used to route test scripts into the default Tests assembly — mirrors
    /// <see cref="PackageManager.HasEditorSegment"/> for the Editor-vs-Game split.</summary>
    private static bool HasTestsSegment(string absolutePath)
        => absolutePath
            .Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
            .Any(s => s.Equals("Tests", StringComparison.OrdinalIgnoreCase));

    internal static string GetVersionDefine()
    {
        var version = Assembly.GetExecutingAssembly()
            .GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion
            ?? Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.1";
        int plus = version.IndexOf('+');
        if (plus >= 0) version = version[..plus];
        return "XENGINE_" + version.Replace('.', '_').Replace('-', '_').ToUpperInvariant();
    }

    /// <summary>
    /// True if the project's Directory.Build.props declares at least one NuGet PackageReference.
    /// Comments are stripped first so the commented examples in the generated template do not count.
    /// </summary>
    internal static bool ProjectDeclaresPackages(Project project)
    {
        string props = Path.Combine(project.RootPath, "Directory.Build.props");
        if (!File.Exists(props)) return false;

        string text;
        try { text = File.ReadAllText(props); }
        catch { return false; }

        text = System.Text.RegularExpressions.Regex.Replace(text, "<!--.*?-->", "",
            System.Text.RegularExpressions.RegexOptions.Singleline);
        return text.Contains("<PackageReference", StringComparison.OrdinalIgnoreCase);
    }

    internal static (int exitCode, string stdout, string stderr) RunDotnetCommand(string args, string workingDir)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "dotnet",
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            WorkingDirectory = workingDir,
        };

        try
        {
            using var process = System.Diagnostics.Process.Start(psi);
            if (process == null) return (-1, "", "Failed to start dotnet process");

            // Read both streams concurrently: draining stdout then stderr sequentially deadlocks
            // if the child fills the other stream's pipe buffer first and blocks waiting on us.
            Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
            Task<string> stderrTask = process.StandardError.ReadToEndAsync();

            bool exited = process.WaitForExit(120_000); // 120s timeout (multiple assemblies may build)
            if (!exited)
            {
                try { process.Kill(entireProcessTree: true); } catch { }
                process.WaitForExit();
            }

            Task.WaitAll(stdoutTask, stderrTask);

            return (process.ExitCode, stdoutTask.Result, stderrTask.Result);
        }
        catch (Exception ex)
        {
            return (-1, "", $"Failed to run dotnet build: {ex.Message}. Is the .NET SDK installed?");
        }
    }

    internal static void LogBuildOutput(string stdout, string stderr)
    {
        foreach (var line in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
        {
            string trimmed = line.Trim();
            if (trimmed.Contains(": error "))
                Runtime.Debug.LogError(trimmed);
            else if (trimmed.Contains(": warning "))
                Runtime.Debug.LogWarning(trimmed);
            else if (!string.IsNullOrWhiteSpace(trimmed))
                Runtime.Debug.Log(trimmed);
        }

        if (!string.IsNullOrEmpty(stderr))
            Runtime.Debug.LogError(stderr);
    }
}
