// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;

namespace XEngine.Editor.Projects.Build;

/// <summary>
/// Context handed to <see cref="IHarmonyOSStageProjectContributor"/> implementations after the
/// HarmonyOS player/Stage project has been materialized and verified, before hvigor packaging
/// or export publication. Contributors may add ArkTS sources, module permissions, and local
/// HAR dependencies so optional packages (ads, push, payment, …) can extend the generated
/// Stage project without the engine hardcoding package internals.
/// </summary>
public sealed class HarmonyOSStageProjectContext
{
    /// <summary>Materialized Stage project directory (<c>stage/</c> under the export root).</summary>
    public required string StageProjectDirectory { get; init; }
    /// <summary>hvigor project root that msbuild/hvigor targets consume.</summary>
    public required string ProjectPath { get; init; }
    public required string ApplicationId { get; init; }
    /// <summary>Single target ABI (e.g. <c>arm64-v8a</c>); HarmonyOS builds one ABI per build.</summary>
    public required string Abi { get; init; }
    public required int ApiLevel { get; init; }
}

/// <summary>
/// Optional Stage-project extension point. Packages implement this interface; implementations
/// are discovered from loaded assemblies on first HarmonyOS build (mirrors
/// <see cref="IBuildPostProcessor"/>) and run after the Stage project is materialized but
/// before it is packaged or exported. A contributor returns the list of human-readable errors
/// it produced (empty/null = success); any error fails the build.
/// </summary>
public interface IHarmonyOSStageProjectContributor
{
    /// <summary>Stable owner identifier (usually the package name).</summary>
    string Owner { get; }

    /// <summary>Contributes into the materialized Stage project. Return errors; empty/null = success.</summary>
    List<string> Contribute(HarmonyOSStageProjectContext context);
}

public static class HarmonyOSStageProjectContribution
{
    private static readonly List<IHarmonyOSStageProjectContributor> _registered = new();
    private static List<IHarmonyOSStageProjectContributor>? _discovered;
    private static readonly object _lock = new();

    /// <summary>Registers a contributor programmatically (tests, host tools).</summary>
    public static void Register(IHarmonyOSStageProjectContributor contributor)
    {
        lock (_lock)
        {
            if (_registered.Any(c => c.GetType() == contributor.GetType())) return;
            _registered.Add(contributor);
            _discovered = null; // force re-snapshot
        }
    }

    /// <summary>Discovers IHarmonyOSStageProjectContributor implementations across loaded assemblies.</summary>
    public static List<IHarmonyOSStageProjectContributor> Discover()
    {
        lock (_lock)
        {
            if (_discovered is not null) return _discovered;

            var found = new List<IHarmonyOSStageProjectContributor>(_registered);
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                if (asm.IsDynamic) continue;
                Type[] types;
                try { types = asm.GetTypes(); }
                catch { continue; }
                foreach (var t in types)
                {
                    if (t.IsAbstract || !typeof(IHarmonyOSStageProjectContributor).IsAssignableFrom(t)) continue;
                    if (found.Any(c => c.GetType() == t)) continue;
                    try
                    {
                        if (Activator.CreateInstance(t) is IHarmonyOSStageProjectContributor contributor)
                            found.Add(contributor);
                    }
                    catch
                    {
                        // A contributor that cannot construct must not block builds; it simply
                        // does not participate. Discovery errors are not build errors.
                    }
                }
            }
            found.Sort((a, b) => string.CompareOrdinal(a.Owner, b.Owner));
            _discovered = found;
            return found;
        }
    }

    /// <summary>Runs every discovered contributor. Returns aggregated errors; empty = success.</summary>
    public static List<string> RunAll(HarmonyOSStageProjectContext context)
    {
        var errors = new List<string>();
        foreach (IHarmonyOSStageProjectContributor contributor in Discover())
        {
            try
            {
                List<string>? issues = contributor.Contribute(context);
                if (issues is { Count: > 0 })
                    errors.AddRange(issues.Select(i => $"[{contributor.Owner}] {i}"));
            }
            catch (Exception exception)
            {
                errors.Add($"[{contributor.Owner}] contribution threw: {exception.Message}");
            }
        }
        return errors;
    }
}

/// <summary>
/// File-level patch helpers for Stage project contributions. Every helper is idempotent so
/// repeated builds over a reused export directory stay correct, and throws (failing the build
/// with a clear message) when the Stage template contract is broken rather than silently
/// skipping.
/// </summary>
public static class HarmonyOSStageProjectPatch
{
    private const string EntryEtsRelativeRoot = "entry/src/main/ets";
    private const string ModuleJson5RelativePath = "entry/src/main/module.json5";
    private const string OhPackageRelativePath = "entry/oh-package.json5";
    private const string EntryLibsRelativeRoot = "entry/libs";

    /// <summary>
    /// Ensures each named <c>ohos.permission.*</c> is declared in the staged module's
    /// <c>requestPermissions</c>. Existing declarations are kept; duplicates are skipped.
    /// </summary>
    public static void EnsureModulePermissions(string stageProjectDirectory, params string[] permissions)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stageProjectDirectory);
        if (permissions is { Length: 0 }) return;

        string modulePath = Path.Combine(stageProjectDirectory, ModuleJson5RelativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(modulePath))
            throw new FileNotFoundException("HarmonyOS Stage module manifest is missing.", modulePath);
        string json = File.ReadAllText(modulePath);

        var missing = permissions
            .Where(p => !string.IsNullOrWhiteSpace(p))
            .Where(p => !json.Contains(p, StringComparison.Ordinal))
            .Distinct(StringComparer.Ordinal)
            .ToArray();
        if (missing.Length == 0) return;

        int arrayStart = json.IndexOf("\"requestPermissions\"", StringComparison.Ordinal);
        if (arrayStart >= 0)
        {
            int openBracket = json.IndexOf('[', arrayStart);
            if (openBracket < 0)
                throw new InvalidDataException($"HarmonyOS Stage module.json5 has 'requestPermissions' without '[': {modulePath}");
            string entries = string.Join(Environment.NewLine, missing.Select(p => "        { \"name\": \"" + p + "\" },"));
            json = json.Insert(openBracket + 1, Environment.NewLine + entries);
        }
        else
        {
            int insertion = json.IndexOf("\"abilities\"", StringComparison.Ordinal);
            if (insertion < 0)
                throw new InvalidDataException($"HarmonyOS Stage module.json5 has no 'abilities' anchor: {modulePath}");
            string block = "    \"requestPermissions\": [ " +
                string.Join(", ", missing.Select(p => "{ \"name\": \"" + p + "\" }")) + " ]," + Environment.NewLine;
            json = json.Insert(insertion, block);
        }
        File.WriteAllText(modulePath, json, new UTF8Encoding(false));
    }

    /// <summary>
    /// Writes an ArkTS source file under <c>entry/src/main/ets</c>. <paramref name="relativePath"/>
    /// must be a relative path (forward or back slashes); escaping above the ets root is rejected.
    /// </summary>
    public static void WriteEntrySourceFile(string stageProjectDirectory, string relativePath, string content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stageProjectDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        ArgumentNullException.ThrowIfNull(content);

        string root = Path.GetFullPath(Path.Combine(stageProjectDirectory, EntryEtsRelativeRoot.Replace('/', Path.DirectorySeparatorChar)));
        string normalized = relativePath.Replace('/', Path.DirectorySeparatorChar);
        string target = Path.GetFullPath(Path.Combine(root, normalized));
        if (!target.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"HarmonyOS Stage contribution path escapes the ets root: {relativePath}");
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        File.WriteAllText(target, content, new UTF8Encoding(false));
    }

    /// <summary>
    /// Copies a local HAR archive into <c>entry/libs/&lt;moduleName&gt;.har</c> and references it from
    /// the entry module's <c>oh-package.json5</c> dependencies as <c>file:./libs/&lt;moduleName&gt;.har</c>
    /// (the DevEco local-har convention). Re-runs overwrite the archive and skip an existing
    /// dependency entry.
    /// </summary>
    public static void AddLocalHarDependency(string stageProjectDirectory, string moduleName, string harSourcePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(stageProjectDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(moduleName);
        ArgumentException.ThrowIfNullOrWhiteSpace(harSourcePath);
        if (moduleName.Any(c => !char.IsLetterOrDigit(c) && c is not '.' and not '-' and not '_'))
            throw new InvalidDataException($"HarmonyOS HAR module name contains invalid characters: {moduleName}");
        if (!File.Exists(harSourcePath))
            throw new FileNotFoundException("HarmonyOS HAR archive to contribute is missing.", harSourcePath);
        if (!string.Equals(Path.GetExtension(harSourcePath), ".har", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"HarmonyOS HAR dependency must be a .har archive: {harSourcePath}");

        string libsRoot = Path.Combine(stageProjectDirectory, EntryLibsRelativeRoot.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(libsRoot);
        string harTarget = Path.Combine(libsRoot, moduleName + ".har");
        File.Copy(harSourcePath, harTarget, overwrite: true);

        string packagePath = Path.Combine(stageProjectDirectory, OhPackageRelativePath.Replace('/', Path.DirectorySeparatorChar));
        if (!File.Exists(packagePath))
            throw new FileNotFoundException("HarmonyOS Stage entry oh-package.json5 is missing.", packagePath);
        string json = File.ReadAllText(packagePath);
        string reference = "\"" + moduleName + "\": \"file:./libs/" + moduleName + ".har\"";
        if (json.Contains(reference, StringComparison.Ordinal)) return;

        int dependencies = json.IndexOf("\"dependencies\"", StringComparison.Ordinal);
        if (dependencies < 0)
            throw new InvalidDataException($"HarmonyOS Stage oh-package.json5 has no 'dependencies' block: {packagePath}");
        int openBrace = json.IndexOf('{', dependencies);
        if (openBrace < 0)
            throw new InvalidDataException($"HarmonyOS Stage oh-package.json5 'dependencies' has no '{{': {packagePath}");
        json = json.Insert(openBrace + 1, Environment.NewLine + "        " + reference + ",");
        File.WriteAllText(packagePath, json, new UTF8Encoding(false));
    }
}
