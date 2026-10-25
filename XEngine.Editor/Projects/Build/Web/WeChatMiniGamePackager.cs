// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace XEngine.Editor.Build.Web;

internal sealed record WeChatPackageLayout(
    int NativeWasmCopied,
    int JsModulesTransformed,
    int AssembliesCopied,
    IReadOnlyList<string> CodeSubpackageRoots);

/// <summary>
/// Assembles a WeChat code package from published <c>_framework</c>:
/// native wasm stays in <c>wechat/wasm/</c> (main, path instantiate);
/// ESM glue is rewritten to CommonJS and packed with Webcil assemblies into
/// <c>dotnet</c> / <c>dotnet2</c> / … code subpackages (not CDN).
/// </summary>
internal static class WeChatMiniGamePackager
{
    internal const long SubpackagePackBudgetBytes = (7L * 1024 * 1024) / 2; // 3.5 MiB
    internal const string FirstCodeSubpackage = "dotnet";
    internal const string BootManifestFileName = "boot-manifest.json";
    internal const string AssemblyMapFileName = "assembly-map.json";

    internal static WeChatPackageLayout Pack(string frameworkDirectory, string wechatDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(frameworkDirectory);
        ArgumentException.ThrowIfNullOrWhiteSpace(wechatDirectory);
        if (!Directory.Exists(frameworkDirectory))
            throw new DirectoryNotFoundException("Published _framework was not found at " + frameworkDirectory);

        Directory.CreateDirectory(wechatDirectory);
        int nativeCopied = WebPublishPlanner.CopyWeChatRuntimeAliases(frameworkDirectory, wechatDirectory);
        ClearCodeSubpackages(wechatDirectory);

        var jsModules = new List<string>();
        var assemblies = new List<string>();
        foreach (string file in Directory.EnumerateFiles(frameworkDirectory))
        {
            string name = Path.GetFileName(file);
            switch (Classify(name))
            {
                case WeChatPackKind.JsModule:
                    jsModules.Add(file);
                    break;
                case WeChatPackKind.Assembly:
                    assemblies.Add(file);
                    break;
            }
        }

        jsModules.Sort(StringComparer.Ordinal);
        assemblies.Sort(StringComparer.Ordinal);

        var packs = new List<CodePack>();
        var first = new CodePack(FirstCodeSubpackage);
        packs.Add(first);
        var jsDestNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (string file in jsModules)
        {
            string destName = StableJsFileName(Path.GetFileName(file));
            if (!jsDestNames.Add(destName))
                continue;
            AddToPack(first, file, transformJs: true, destFileName: destName);
        }

        int packIndex = 0;
        foreach (string file in assemblies)
        {
            long size = new FileInfo(file).Length;
            if (packs[packIndex].Bytes > 0 &&
                packs[packIndex].Bytes + size > SubpackagePackBudgetBytes)
            {
                packIndex++;
                packs.Add(new CodePack(FirstCodeSubpackage + (packIndex + 1).ToString()));
            }

            AddToPack(packs[packIndex], file, transformJs: false);
        }

        var roots = new List<string>(packs.Count);
        int transformed = 0;
        int copiedAssemblies = 0;
        foreach (CodePack pack in packs)
        {
            string destDir = Path.Combine(wechatDirectory, pack.Name);
            Directory.CreateDirectory(destDir);
            roots.Add(pack.Name + "/");
            foreach (PackFile entry in pack.Files)
            {
                string destName = string.IsNullOrEmpty(entry.DestFileName)
                    ? Path.GetFileName(entry.Source)
                    : entry.DestFileName;
                string dest = Path.Combine(destDir, destName);
                if (entry.TransformJs)
                {
                    string source = File.ReadAllText(entry.Source);
                    string relative = pack.Name + "/" + destName;
                    File.WriteAllText(dest, WeChatEsmCommonJs.ToCommonJs(
                        source,
                        WeChatEsmCommonJs.ImportMetaUrlFor(relative)));
                    transformed++;
                }
                else
                {
                    File.Copy(entry.Source, dest, overwrite: true);
                    copiedAssemblies++;
                }
            }
        }

        WriteBootManifest(wechatDirectory, roots);
        WriteAssemblyMap(wechatDirectory, packs);
        RewriteGameJson(wechatDirectory, roots);
        WriteSubpackageEntryJs(wechatDirectory, roots);
        File.WriteAllText(
            Path.Combine(wechatDirectory, "game.js"),
            WebHostTemplates.CreateWeChatGameJs());
        return new WeChatPackageLayout(nativeCopied, transformed, copiedAssemblies, roots);
    }

    internal static WeChatPackKind Classify(string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        if (fileName is ".keep" or ".gitkeep")
            return WeChatPackKind.Skip;
        if (fileName.Contains("Magick", StringComparison.OrdinalIgnoreCase) ||
            fileName.Contains("Vulkan", StringComparison.OrdinalIgnoreCase) ||
            fileName.Contains("Slang", StringComparison.OrdinalIgnoreCase))
        {
            return WeChatPackKind.Skip;
        }

        if (fileName.EndsWith(".br", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".gz", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".map", StringComparison.OrdinalIgnoreCase) ||
            fileName.EndsWith(".pdb", StringComparison.OrdinalIgnoreCase))
        {
            return WeChatPackKind.Skip;
        }

        if (fileName.StartsWith("icudt", StringComparison.OrdinalIgnoreCase) ||
            fileName.StartsWith("dotnet.diagnostics", StringComparison.Ordinal))
        {
            return WeChatPackKind.Skip;
        }

        if (fileName.StartsWith("dotnet.native.", StringComparison.Ordinal) &&
            fileName.EndsWith(".wasm", StringComparison.OrdinalIgnoreCase))
        {
            return WeChatPackKind.NativeWasm;
        }

        if (fileName.EndsWith(".js", StringComparison.OrdinalIgnoreCase) &&
            fileName.StartsWith("dotnet", StringComparison.Ordinal))
        {
            return WeChatPackKind.JsModule;
        }

        if (fileName.EndsWith(".wasm", StringComparison.OrdinalIgnoreCase))
            return WeChatPackKind.Assembly;

        return WeChatPackKind.Skip;
    }

    private static void ClearCodeSubpackages(string wechatDirectory)
    {
        foreach (string dir in Directory.GetDirectories(wechatDirectory))
        {
            string name = Path.GetFileName(dir);
            if (!name.StartsWith("dotnet", StringComparison.Ordinal) ||
                !WebPackageSizeReport.IsMiniGameSubpackageRelative(name))
            {
                continue;
            }

            foreach (string file in Directory.GetFiles(dir))
            {
                string fileName = Path.GetFileName(file);
                if (fileName is ".keep" or ".gitkeep")
                    continue;
                File.Delete(file);
            }
        }
    }

    internal static string StableJsFileName(string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        if (fileName.StartsWith("dotnet.native.", StringComparison.Ordinal) &&
            fileName.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
        {
            return "dotnet.native.js";
        }

        if (fileName.StartsWith("dotnet.runtime.", StringComparison.Ordinal) &&
            fileName.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
        {
            return "dotnet.runtime.js";
        }

        if (fileName.StartsWith("dotnet.boot.", StringComparison.Ordinal) ||
            fileName.StartsWith("dotnet.diagnostics.", StringComparison.Ordinal))
        {
            return fileName;
        }

        if (fileName.StartsWith("dotnet.", StringComparison.Ordinal) &&
            fileName.EndsWith(".js", StringComparison.OrdinalIgnoreCase))
        {
            return "dotnet.js";
        }

        return fileName;
    }

    private static void AddToPack(CodePack pack, string file, bool transformJs, string? destFileName = null)
    {
        pack.Files.Add(new PackFile(file, transformJs, destFileName));
        pack.Bytes += new FileInfo(file).Length;
    }

    internal static string? StripAssemblyFingerprint(string fileName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(fileName);
        if (!fileName.EndsWith(".wasm", StringComparison.OrdinalIgnoreCase))
            return null;
        if (fileName.StartsWith("dotnet.native.", StringComparison.OrdinalIgnoreCase))
            return null;

        int ext = fileName.LastIndexOf('.');
        if (ext <= 0)
            return null;
        int hashDot = fileName.LastIndexOf('.', ext - 1);
        if (hashDot <= 0)
            return null;

        string hash = fileName.Substring(hashDot + 1, ext - hashDot - 1);
        if (hash.Length is < 8 or > 12)
            return null;
        for (int i = 0; i < hash.Length; i++)
        {
            if (!char.IsAsciiLetterOrDigit(hash[i]))
                return null;
        }

        return string.Concat(fileName.AsSpan(0, hashDot), fileName.AsSpan(ext));
    }

    private static void WriteAssemblyMap(string wechatDirectory, IReadOnlyList<CodePack> packs)
    {
        var files = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (CodePack pack in packs)
        {
            foreach (PackFile entry in pack.Files)
            {
                string destName = string.IsNullOrEmpty(entry.DestFileName)
                    ? Path.GetFileName(entry.Source)
                    : entry.DestFileName;
                string rel = pack.Name + "/" + destName;
                files[destName] = rel;
                string? unhashed = StripAssemblyFingerprint(destName);
                if (unhashed is not null)
                    files.TryAdd(unhashed, rel);
            }
        }

        File.WriteAllText(
            Path.Combine(wechatDirectory, AssemblyMapFileName),
            JsonSerializer.Serialize(
                new { files },
                new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void WriteBootManifest(string wechatDirectory, IReadOnlyList<string> roots)
    {
        string first = Path.Combine(wechatDirectory, FirstCodeSubpackage);
        Directory.CreateDirectory(first);
        File.WriteAllText(
            Path.Combine(first, BootManifestFileName),
            JsonSerializer.Serialize(
                new
                {
                    nativeWasm = "wasm/dotnet.native.wasm",
                    loader = FirstCodeSubpackage + "/dotnet.js",
                    importMetaUrl = WeChatEsmCommonJs.ImportMetaUrlFor(FirstCodeSubpackage + "/dotnet.js"),
                    codeSubpackages = roots,
                },
                new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void RewriteGameJson(string wechatDirectory, IReadOnlyList<string> codeRoots)
    {
        string path = Path.Combine(wechatDirectory, "game.json");
        JsonObject root;
        if (File.Exists(path))
        {
            root = JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? new JsonObject();
        }
        else
        {
            root = JsonNode.Parse(WebHostTemplates.CreateWeChatGameJson()) as JsonObject
                ?? new JsonObject();
        }

        var subpackages = new JsonArray();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (string codeRoot in codeRoots)
        {
            string name = codeRoot.TrimEnd('/');
            if (!seen.Add(name))
                continue;
            subpackages.Add(new JsonObject
            {
                ["name"] = name,
                ["root"] = name.EndsWith('/') ? name : name + "/",
            });
        }

        if (seen.Add("cdn"))
        {
            subpackages.Add(new JsonObject
            {
                ["name"] = "cdn",
                ["root"] = "cdn/",
            });
        }

        root["subpackages"] = subpackages;
        if (root["workers"] is JsonValue workers &&
            workers.TryGetValue(out string? workersPath) &&
            string.IsNullOrWhiteSpace(workersPath))
        {
            root.Remove("workers");
        }

        File.WriteAllText(
            path,
            root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void WriteSubpackageEntryJs(string wechatDirectory, IReadOnlyList<string> codeRoots)
    {
        string stub = WebHostTemplates.CreateWeChatSubpackageGameJs();
        var dirs = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "cdn" };
        foreach (string codeRoot in codeRoots)
        {
            string name = codeRoot.TrimEnd('/', '\\');
            if (!string.IsNullOrEmpty(name))
                dirs.Add(name);
        }

        foreach (string name in dirs)
        {
            string destDir = Path.Combine(wechatDirectory, name);
            Directory.CreateDirectory(destDir);
            File.WriteAllText(Path.Combine(destDir, "game.js"), stub);
        }
    }

    private sealed class CodePack
    {
        internal CodePack(string name)
        {
            Name = name;
        }

        internal string Name { get; }
        internal long Bytes { get; set; }
        internal List<PackFile> Files { get; } = new();
    }

    private readonly record struct PackFile(string Source, bool TransformJs, string? DestFileName);
}

internal enum WeChatPackKind
{
    Skip,
    NativeWasm,
    JsModule,
    Assembly,
}
