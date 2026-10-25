// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;

using XEngine.Editor.Projects;
using XEngine.Editor.Projects.Scripting;

namespace XEngine.Editor.Build.Android;

internal sealed record AndroidManagedAssemblyInput(
    string Name,
    string SourcePath,
    string Role,
    string Reason);

internal sealed record AndroidManagedAssemblyExclusion(
    string Name,
    string Role,
    string Reason);

internal sealed record AndroidManagedDependencyCollection(
    IReadOnlyList<AndroidManagedAssemblyInput> Assemblies,
    IReadOnlyList<AndroidManagedAssemblyExclusion> Exclusions);

internal static class AndroidManagedDependencyCollector
{
    private static readonly string[] s_forbiddenPrefixes =
    [
        "XEngine.Editor",
        "XEngine.Slang",
        "ImageMagick",
        "Magick.NET",
        "Silk.NET.GLFW",
        "Silk.NET.Input",
        "Silk.NET.OpenAL",
        "Silk.NET.OpenGL",
        "Silk.NET.Windowing",
        "Vortice.",
    ];

    internal static AndroidManagedDependencyCollection Collect(
        string runtimePayloadDirectory,
        Project project)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimePayloadDirectory);
        ArgumentNullException.ThrowIfNull(project);
        string payload = Path.GetFullPath(runtimePayloadDirectory);
        if (!Directory.Exists(payload))
            throw new DirectoryNotFoundException($"Android Runtime payload is missing: {payload}");

        string depsPath = Path.Combine(payload, "XEngine.Runtime.deps.json");
        if (!File.Exists(depsPath))
            throw new FileNotFoundException("Android Runtime dependency manifest is missing.", depsPath);

        var selected = new Dictionary<string, AndroidManagedAssemblyInput>(StringComparer.OrdinalIgnoreCase);
        var exclusions = new List<AndroidManagedAssemblyExclusion>();
        IReadOnlyList<string> runtimeFiles = ReadRuntimeAssemblyFiles(depsPath);
        for (int i = 0; i < runtimeFiles.Count; i++)
        {
            string fileName = runtimeFiles[i];
            string source = Path.Combine(payload, fileName);
            AddRequired(selected, new AndroidManagedAssemblyInput(
                Path.GetFileNameWithoutExtension(fileName),
                source,
                "engine-runtime",
                fileName.Equals("XEngine.Runtime.dll", StringComparison.OrdinalIgnoreCase)
                    ? "Android-target XEngine Runtime root"
                    : "Transitive runtime dependency from XEngine.Runtime.deps.json"));
        }

        List<ScriptCompiler.BuildAssembly> userAssemblies = ScriptCompiler.GetBuildAssemblies(
            project,
            BuildPlatforms.Android);
        for (int i = 0; i < userAssemblies.Count; i++)
        {
            ScriptCompiler.BuildAssembly assembly = userAssemblies[i];
            AddRequired(selected, new AndroidManagedAssemblyInput(
                assembly.Name,
                assembly.DllPath,
                "user-assembly",
                "Compiled user assembly included by asmdef Android platform filtering"));
        }

        List<PluginInfo> plugins = PluginScanner.ScanAll(project);
        for (int i = 0; i < plugins.Count; i++)
        {
            PluginInfo plugin = plugins[i];
            if (!plugin.IsManaged)
                continue;
            string name = GetAssemblyName(plugin.AbsolutePath);
            if (!plugin.AppliesToBuild(BuildPlatforms.Android))
            {
                exclusions.Add(new AndroidManagedAssemblyExclusion(
                    name,
                    "managed-plugin",
                    plugin.EditorOnly
                        ? "Editor-only plugin"
                        : "Plugin platform settings exclude Android"));
                continue;
            }
            AddRequired(selected, new AndroidManagedAssemblyInput(
                name,
                plugin.AbsolutePath,
                "managed-plugin",
                "Managed plugin enabled for Android"));
        }

        string[] payloadDlls = Directory.GetFiles(payload, "*.dll", SearchOption.TopDirectoryOnly);
        Array.Sort(payloadDlls, StringComparer.Ordinal);
        for (int i = 0; i < payloadDlls.Length; i++)
        {
            string name = Path.GetFileNameWithoutExtension(payloadDlls[i]);
            if (selected.ContainsKey(name))
                continue;
            exclusions.Add(new AndroidManagedAssemblyExclusion(
                name,
                "runtime-build-output",
                IsForbidden(name)
                    ? "Desktop/editor/runtime-compiler dependency forbidden on Android"
                    : "Build-time assembly not present in the Runtime dependency graph"));
        }

        var assemblies = new List<AndroidManagedAssemblyInput>(selected.Values);
        assemblies.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.Name, right.Name));
        exclusions.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.Name, right.Name));
        return new AndroidManagedDependencyCollection(assemblies, exclusions);
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

    private static IReadOnlyList<string> ReadRuntimeAssemblyFiles(string depsPath)
    {
        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(depsPath));
        JsonElement targets = document.RootElement.GetProperty("targets");
        JsonElement runtimeTarget = default;
        bool found = false;
        foreach (JsonProperty target in targets.EnumerateObject())
        {
            if (!target.Name.Contains("/android-", StringComparison.Ordinal))
                continue;
            runtimeTarget = target.Value;
            found = true;
            break;
        }
        if (!found)
            throw new InvalidDataException("XEngine.Runtime.deps.json has no Android RID target.");

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
        var result = new List<string>(files);
        result.Sort(StringComparer.Ordinal);
        return result;
    }

    private static void AddRequired(
        Dictionary<string, AndroidManagedAssemblyInput> selected,
        AndroidManagedAssemblyInput input)
    {
        if (IsForbidden(input.Name))
            throw new InvalidDataException($"Android managed closure contains forbidden assembly '{input.Name}'.");
        if (!File.Exists(input.SourcePath))
            throw new FileNotFoundException($"Required Android managed assembly '{input.Name}' is missing.", input.SourcePath);
        string identity = GetAssemblyName(input.SourcePath);
        if (!string.Equals(identity, input.Name, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidDataException(
                $"Android managed assembly identity '{identity}' does not match expected '{input.Name}'.");
        }
        if (selected.TryGetValue(identity, out AndroidManagedAssemblyInput? existing))
        {
            if (!FilesEqual(existing.SourcePath, input.SourcePath))
                throw new InvalidDataException($"Android managed closure has conflicting copies of '{identity}'.");
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
        if (leftInfo.Length != rightInfo.Length)
            return false;
        return File.ReadAllBytes(left).AsSpan().SequenceEqual(File.ReadAllBytes(right));
    }
}
