// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;

using XEngine.Editor.Projects;
using XEngine.Editor.Projects.Scripting;

namespace XEngine.Editor.Build.Ios;

/// <summary>Collects the RID-specific Runtime, iOS scripts, and platform-enabled managed plugins.</summary>
internal static class IosManagedDependencyCollector
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

    internal static IReadOnlyList<IosManagedAssemblyInput> Collect(
        string runtimePayloadDirectory,
        Project project,
        string runtimeIdentifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(runtimePayloadDirectory);
        ArgumentNullException.ThrowIfNull(project);
        if (runtimeIdentifier is not ("ios-arm64" or "iossimulator-arm64"))
            throw new ArgumentOutOfRangeException(nameof(runtimeIdentifier), runtimeIdentifier, "Unsupported iOS RID.");

        string payload = Path.GetFullPath(runtimePayloadDirectory);
        if (!Directory.Exists(payload))
            throw new DirectoryNotFoundException($"iOS Runtime payload is missing: {payload}");
        string depsPath = Path.Combine(payload, "XEngine.Runtime.deps.json");
        if (!File.Exists(depsPath))
            throw new FileNotFoundException("iOS Runtime dependency manifest is missing.", depsPath);

        var selected = new Dictionary<string, IosManagedAssemblyInput>(StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<string> runtimeFiles = ReadRuntimeAssemblyFiles(depsPath, runtimeIdentifier);
        for (int i = 0; i < runtimeFiles.Count; i++)
        {
            string fileName = runtimeFiles[i];
            AddRequired(selected, new IosManagedAssemblyInput(
                Path.GetFileNameWithoutExtension(fileName),
                Path.Combine(payload, fileName),
                "engine-runtime",
                fileName.Equals("XEngine.Runtime.dll", StringComparison.OrdinalIgnoreCase)
                    ? $"{runtimeIdentifier} XEngine Runtime root"
                    : $"Transitive iOS Runtime dependency from XEngine.Runtime.deps.json ({runtimeIdentifier})"));
        }

        List<ScriptCompiler.BuildAssembly> scripts = ScriptCompiler.GetBuildAssemblies(project, BuildPlatforms.iOS);
        for (int i = 0; i < scripts.Count; i++)
        {
            ScriptCompiler.BuildAssembly assembly = scripts[i];
            AddRequired(selected, new IosManagedAssemblyInput(
                assembly.Name,
                assembly.DllPath,
                "user-assembly",
                "Compiled user assembly included by asmdef iOS platform filtering"));
        }

        List<PluginInfo> plugins = PluginScanner.ScanAll(project);
        for (int i = 0; i < plugins.Count; i++)
        {
            PluginInfo plugin = plugins[i];
            if (!plugin.IsManaged)
                continue;

            string name = GetAssemblyName(plugin.AbsolutePath);
            if (!plugin.AppliesToBuild(BuildPlatforms.iOS))
                continue;

            AddRequired(selected, new IosManagedAssemblyInput(
                name,
                plugin.AbsolutePath,
                "managed-plugin",
                "Managed plugin enabled for iOS"));
        }

        var assemblies = new List<IosManagedAssemblyInput>(selected.Values);
        assemblies.Sort(static (left, right) => StringComparer.Ordinal.Compare(left.Name, right.Name));
        return assemblies.AsReadOnly();
    }

    internal static IReadOnlyList<string> ReadRuntimeAssemblyFiles(string depsPath, string runtimeIdentifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(depsPath);
        if (runtimeIdentifier is not ("ios-arm64" or "iossimulator-arm64"))
            throw new ArgumentOutOfRangeException(nameof(runtimeIdentifier), runtimeIdentifier, "Unsupported iOS RID.");

        using JsonDocument document = JsonDocument.Parse(File.ReadAllBytes(depsPath));
        JsonElement targets = document.RootElement.GetProperty("targets");
        JsonElement runtimeTarget = default;
        string suffix = "/" + runtimeIdentifier;
        foreach (JsonProperty target in targets.EnumerateObject())
        {
            if (!target.Name.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
                continue;
            runtimeTarget = target.Value;
            break;
        }
        if (runtimeTarget.ValueKind != JsonValueKind.Object)
            throw new InvalidDataException(
                $"XEngine.Runtime.deps.json has no '{runtimeIdentifier}' target.");

        var files = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (JsonProperty library in runtimeTarget.EnumerateObject())
        {
            if (!library.Value.TryGetProperty("runtime", out JsonElement runtime) ||
                runtime.ValueKind != JsonValueKind.Object)
                continue;

            foreach (JsonProperty asset in runtime.EnumerateObject())
            {
                string fileName = Path.GetFileName(asset.Name.Replace('/', Path.DirectorySeparatorChar));
                if (fileName.EndsWith(".dll", StringComparison.OrdinalIgnoreCase))
                    files.Add(fileName);
            }
        }
        if (!files.Contains("XEngine.Runtime.dll"))
            throw new InvalidDataException(
                $"XEngine.Runtime.deps.json '{runtimeIdentifier}' target omits XEngine.Runtime.dll.");

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
        Dictionary<string, IosManagedAssemblyInput> selected,
        IosManagedAssemblyInput input)
    {
        if (IsForbidden(input.Name))
            throw new InvalidDataException($"iOS managed closure contains forbidden assembly '{input.Name}'.");
        if (!File.Exists(input.SourcePath))
            throw new FileNotFoundException($"Required iOS managed assembly '{input.Name}' is missing.", input.SourcePath);

        string identity = GetAssemblyName(input.SourcePath);
        if (!string.Equals(identity, input.Name, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException(
                $"iOS managed assembly identity '{identity}' does not match expected '{input.Name}'.");

        if (selected.TryGetValue(identity, out IosManagedAssemblyInput? existing))
        {
            if (!FilesEqual(existing.SourcePath, input.SourcePath))
                throw new InvalidDataException($"iOS managed closure has conflicting copies of '{identity}'.");
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
}
