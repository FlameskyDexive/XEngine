// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;

using XEngine.Editor.Projects;
using XEngine.Editor.Projects.Scripting;

namespace XEngine.Editor.Build.HarmonyOS;

internal sealed record HarmonyOSManagedAssemblyInput(
    string Name,
    string SourcePath,
    string Role,
    string Reason);

internal sealed record HarmonyOSManagedAssemblyExclusion(
    string Name,
    string Role,
    string Reason);

internal sealed record HarmonyOSManagedDependencyCollection(
    IReadOnlyList<HarmonyOSManagedAssemblyInput> Assemblies,
    IReadOnlyList<HarmonyOSManagedAssemblyExclusion> Exclusions);

/// <summary>Builds the static managed assembly closure consumed by the NativeAOT player.</summary>
internal static class HarmonyOSManagedDependencyCollector
{
    private static readonly string[] s_forbiddenPrefixes =
    [
        "Android.",
        "Java.Interop",
        "Magick.NET",
        "Microsoft.Android",
        "Mono.Android",
        "Silk.NET.GLFW",
        "Silk.NET.Input",
        "Silk.NET.OpenAL",
        "Silk.NET.OpenGL",
        "Silk.NET.Windowing",
        "Vortice.",
        "Xamarin.",
        "XEngine.Editor",
        "XEngine.Slang",
    ];

    internal static HarmonyOSManagedDependencyCollection Collect(
        string runtimePayloadDirectory,
        Project project)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimePayloadDirectory);
        ArgumentNullException.ThrowIfNull(project);
        string payload = Path.GetFullPath(runtimePayloadDirectory);
        if (!Directory.Exists(payload))
            throw new DirectoryNotFoundException($"HarmonyOS Runtime payload is missing: {payload}");

        string depsPath = Path.Combine(payload, "XEngine.Runtime.deps.json");
        if (!File.Exists(depsPath))
            throw new FileNotFoundException("HarmonyOS Runtime dependency manifest is missing.", depsPath);

        var selected = new Dictionary<string, HarmonyOSManagedAssemblyInput>(StringComparer.OrdinalIgnoreCase);
        var exclusions = new List<HarmonyOSManagedAssemblyExclusion>();
        IReadOnlyList<string> runtimeFiles = ReadRuntimeAssemblyFiles(depsPath);
        for (int i = 0; i < runtimeFiles.Count; i++)
        {
            string fileName = runtimeFiles[i];
            AddRequired(selected, new HarmonyOSManagedAssemblyInput(
                Path.GetFileNameWithoutExtension(fileName),
                Path.Combine(payload, fileName),
                "engine-runtime",
                fileName.Equals("XEngine.Runtime.dll", StringComparison.OrdinalIgnoreCase)
                    ? "HarmonyOS-target XEngine Runtime root"
                    : "Transitive runtime dependency from XEngine.Runtime.deps.json"));
        }

        List<ScriptCompiler.BuildAssembly> userAssemblies = ScriptCompiler.GetBuildAssemblies(
            project,
            BuildPlatforms.HarmonyOS);
        for (int i = 0; i < userAssemblies.Count; i++)
        {
            ScriptCompiler.BuildAssembly assembly = userAssemblies[i];
            AddRequired(selected, new HarmonyOSManagedAssemblyInput(
                assembly.Name,
                assembly.DllPath,
                "user-assembly",
                "Compiled user assembly included by asmdef HarmonyOS platform filtering"));
        }

        List<PluginInfo> plugins = PluginScanner.ScanAll(project);
        for (int i = 0; i < plugins.Count; i++)
        {
            PluginInfo plugin = plugins[i];
            if (!plugin.IsManaged)
                continue;
            string name = GetAssemblyName(plugin.AbsolutePath);
            if (!plugin.AppliesToBuild(BuildPlatforms.HarmonyOS))
            {
                exclusions.Add(new HarmonyOSManagedAssemblyExclusion(
                    name,
                    "managed-plugin",
                    plugin.EditorOnly ? "Editor-only plugin" : "Plugin platform settings exclude HarmonyOS"));
                continue;
            }
            AddRequired(selected, new HarmonyOSManagedAssemblyInput(
                name,
                plugin.AbsolutePath,
                "managed-plugin",
                "Managed plugin enabled for HarmonyOS"));
        }

        string[] payloadDlls = Directory.GetFiles(payload, "*.dll", SearchOption.TopDirectoryOnly);
        Array.Sort(payloadDlls, StringComparer.Ordinal);
        for (int i = 0; i < payloadDlls.Length; i++)
        {
            string name = Path.GetFileNameWithoutExtension(payloadDlls[i]);
            if (selected.ContainsKey(name))
                continue;
            exclusions.Add(new HarmonyOSManagedAssemblyExclusion(
                name,
                "runtime-build-output",
                IsForbidden(name)
                    ? "Desktop, Android, editor, or runtime-compiler dependency forbidden on HarmonyOS"
                    : "Build-time assembly not present in the Runtime dependency graph"));
        }

        var assemblies = new List<HarmonyOSManagedAssemblyInput>(selected.Values);
        assemblies.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.Name, right.Name));
        exclusions.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.Name, right.Name));
        return new HarmonyOSManagedDependencyCollection(assemblies.AsReadOnly(), exclusions.AsReadOnly());
    }

    internal static IReadOnlyList<string> ReadRuntimeAssemblyFiles(string depsPath)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(depsPath));
        JsonElement targets = document.RootElement.GetProperty("targets");
        JsonElement runtimeTarget = default;
        bool found = false;
        foreach (JsonProperty target in targets.EnumerateObject())
        {
            // The NativeAOT cross-compile is published under the linux-musl RID (PublishAotCross
            // rewrites the toolchain to the OpenHarmony SDK from there), so the deps.json runtime
            // target is the matching linux-musl-* entry. Accept either the legacy openharmony-* RID
            // or the linux-musl RID used by the current toolchain.
            if (!found || IsHarmonyOSRuntimeTarget(target.Name))
            {
                runtimeTarget = target.Value;
                found = true;
            }
            if (IsHarmonyOSRuntimeTarget(target.Name))
                break;
        }
        if (!found || runtimeTarget.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException("XEngine.Runtime.deps.json has no managed Runtime target.");

        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonProperty library in runtimeTarget.EnumerateObject())
        {
            if (!library.Value.TryGetProperty("runtime", out JsonElement runtime))
                continue;
            foreach (JsonProperty asset in runtime.EnumerateObject())
            {
                string fileName = Path.GetFileName(asset.Name.Replace('/', Path.DirectorySeparatorChar));
                if (fileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                    files.Add(fileName);
            }
        }
        if (!files.Contains("XEngine.Runtime.dll"))
            throw new InvalidDataException("XEngine.Runtime.deps.json does not contain XEngine.Runtime.dll.");
        var result = new List<string>(files);
        result.Sort(StringComparer.Ordinal);
        return result.AsReadOnly();
    }

    internal static bool IsForbidden(string assemblyName)
    {
        for (int i = 0; i < s_forbiddenPrefixes.Length; i++)
        {
            if (assemblyName.StartsWith(s_forbiddenPrefixes[i], StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return assemblyName.Equals("dxcompiler", StringComparison.OrdinalIgnoreCase) ||
            assemblyName.Equals("dxil", StringComparison.OrdinalIgnoreCase);
    }

    private static void AddRequired(
        Dictionary<string, HarmonyOSManagedAssemblyInput> selected,
        HarmonyOSManagedAssemblyInput input)
    {
        if (IsForbidden(input.Name))
            throw new InvalidDataException($"HarmonyOS managed closure contains forbidden assembly '{input.Name}'.");
        if (!File.Exists(input.SourcePath))
            throw new FileNotFoundException($"Required HarmonyOS managed assembly '{input.Name}' is missing.", input.SourcePath);
        string identity = GetAssemblyName(input.SourcePath);
        if (!string.Equals(identity, input.Name, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException($"HarmonyOS managed assembly identity '{identity}' does not match expected '{input.Name}'.");
        if (selected.TryGetValue(identity, out HarmonyOSManagedAssemblyInput? existing))
        {
            if (!FilesEqual(existing.SourcePath, input.SourcePath))
                throw new InvalidDataException($"HarmonyOS managed closure has conflicting copies of '{identity}'.");
            return;
        }
        selected.Add(identity, input with { Name = identity, SourcePath = Path.GetFullPath(input.SourcePath) });
    }

    private static string GetAssemblyName(string path) =>
        AssemblyName.GetAssemblyName(path).Name
        ?? throw new InvalidDataException($"Managed assembly has no identity: {path}");

    private static bool FilesEqual(string left, string right)
    {
        var leftInfo = new FileInfo(left);
        var rightInfo = new FileInfo(right);
        return leftInfo.Length == rightInfo.Length &&
            File.ReadAllBytes(left).AsSpan().SequenceEqual(File.ReadAllBytes(right));
    }

    /// <summary>
    /// True when the deps.json target name is the OpenHarmony player runtime target. PublishAotCross
    /// cross-compiles under a linux-musl-* RID (the OpenHarmony SDK toolchain is selected from there),
    /// so the runtime target is the matching linux-musl entry; the legacy openharmony-* RID is also
    /// accepted for back-compat with older runtime payloads.
    /// </summary>
    private static bool IsHarmonyOSRuntimeTarget(string targetName) =>
        targetName.Contains("/openharmony-", StringComparison.Ordinal) ||
        targetName.Contains("/linux-musl-arm64", StringComparison.Ordinal) ||
        targetName.Contains("/linux-musl-x64", StringComparison.Ordinal);
}
