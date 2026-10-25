using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using XEngine.XTS.Binding;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using XEngine.Editor.Projects.Build;

namespace XEngine.XTS.Editor.Build;

/// <summary>
/// Player-build integration for the XTS package. Runs after a successful
/// pipeline build and:
///   1. finds every XTS script project in the built project
///      (xts.project.json marker) and bundles it with the editor toolchain,
///   2. verifies the packaged native library against the package's
///      native dependency manifest (platform, presence, sha256),
///   3. refuses the build when a script project requires binding ids the
///      host does not provide,
///   4. writes the content receipt (Content/XTS/receipt.json) pinning every
///      shipped artifact by sha256.
/// Any problem fails the build — a player never ships with missing
/// bindings, missing native libraries, or unverified content.
/// </summary>
public sealed class XtsBuildPostProcessor : IBuildPostProcessor
{
    public string Owner => "com.xengine.xts";

    public List<string> Process(BuildPostContext context)
    {
        var errors = new List<string>();
        var projectPath = context.ProjectPath;
        var output = context.OutputDirectory;
        var platform = context.Platform;

        // HarmonyOS builds hand the post-processor the packed .hap file, not a
        // player output directory. Content cannot enter the signed HAP after
        // packing, so derive the directory receipt next to it; the player-side
        // factory baseline comes from the content update pipeline instead.
        if (File.Exists(output))
            output = Path.GetDirectoryName(output) ?? output;

        var (packageRoot, manifestErrors) = LocatePackage(projectPath);
        errors.AddRange(manifestErrors);

        var scriptProjects = FindScriptProjects(projectPath);
        var shipped = new List<object>();

        foreach (var projectDir in scriptProjects)
        {
            var manifest = XtsProjectManifest.Parse(
                File.ReadAllText(Path.Combine(projectDir, XtsProjectManifest.FileName)));

            var toolchain = new XtsToolchainService();
            var build = toolchain.Build(projectDir);
            if (!build.Success)
            {
                errors.Add($"script project '{manifest.Name}' failed at {build.Step}: {build.Output}");
                continue;
            }

            var entrySource = Path.Combine(projectDir, manifest.Entry.Replace('/', Path.DirectorySeparatorChar));
            if (!File.Exists(entrySource))
            {
                errors.Add($"script project '{manifest.Name}' entry '{manifest.Entry}' is missing after build");
                continue;
            }

            var targetDir = Path.Combine(output, "Content", "XTS", manifest.Name);
            Directory.CreateDirectory(targetDir);
            var entryName = Path.GetFileName(manifest.Entry);
            File.Copy(entrySource, Path.Combine(targetDir, entryName), overwrite: true);
            var sha = HashFile(Path.Combine(targetDir, entryName));
            shipped.Add(new { project = manifest.Name, entry = entryName, sha256 = sha, requiredBindings = manifest.RequiredBindings });

            // binding gate: the project must not require ids the host lacks
            var hostCapabilityPath = Path.Combine(output, "Content", "XTS", "host-capabilities.json");
            if (File.Exists(hostCapabilityPath))
            {
                var capabilityJson = File.ReadAllText(hostCapabilityPath);
                foreach (var error in XtsBindingGenerator.VerifyCapabilityRequirements(capabilityJson, manifest.RequiredBindings))
                    errors.Add($"script project '{manifest.Name}': {error}");
            }
            else if (manifest.RequiredBindings.Count > 0)
            {
                errors.Add($"script project '{manifest.Name}' requires bindings but the host capability manifest " +
                           "is missing from the build; package the XTS binding declarations or clear RequiredBindings");
            }
        }

        // native library verification (only when the package ships runtime code)
        if (packageRoot != null)
        {
            var manifestPath = Path.Combine(packageRoot, "Native", "native-manifest.json");
            if (File.Exists(manifestPath))
            {
                var manifest = XtsNativeManifest.Parse(File.ReadAllText(manifestPath));
                var manifestPlatform = platform.ToLowerInvariant() switch
                {
                    "windows" or "win64" or "win-x64" => "windows-x64",
                    // HarmonyOS ships the native library through the package's
                    // Plugins/<abi> tree (hvigor packs entry/libs), so the
                    // manifest entry is keyed per ABI.
                    "xengine.harmonyos" => "harmonyos-arm64",
                    "xengine.desktop" => "windows-x64",
                    var p => p,
                };
                foreach (var error in manifest.Validate(manifestPlatform, packageRoot))
                    errors.Add("native manifest: " + error);

                if (manifest.Platforms.TryGetValue(manifestPlatform, out var entry))
                {
                    var shippedNative = manifestPlatform == "windows-x64"
                        ? Path.Combine(output, "runtimes", "win-x64", "native", Path.GetFileName(entry.Library))
                        // HarmonyOS: the packed HAP embeds the package's Plugins lib;
                        // verify the source of truth that hvigor packs.
                        : Path.Combine(packageRoot, entry.Library.Replace('/', Path.DirectorySeparatorChar));
                    if (!File.Exists(shippedNative))
                        errors.Add($"native library '{entry.Library}' was not copied to the player output " +
                                   $"(expected {Path.GetFileName(entry.Library)} under runtimes/win-x64/native)");
                    else
                    {
                        var shippedSha = HashFile(shippedNative);
                        if (!string.Equals(shippedSha, entry.Sha256, StringComparison.OrdinalIgnoreCase))
                            errors.Add($"native library hash mismatch: manifest pins {entry.Sha256}, player ships {shippedSha}");
                    }
                }
            }
            else
            {
                errors.Add("package native manifest is missing (Native/native-manifest.json)");
            }
        }

        // content receipt
        var receiptDir = Path.Combine(output, "Content", "XTS");
        Directory.CreateDirectory(receiptDir);
        var receipt = JsonSerializer.Serialize(new
        {
            schema = "xengine.xts.content.v1",
            platform = context.Platform,
            generatedUtc = DateTime.UtcNow.ToString("o"),
            projects = shipped,
        }, new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(Path.Combine(receiptDir, "receipt.json"), receipt);

        return errors;
    }

    private static string HashFile(string path) =>
        Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))).ToLowerInvariant();

    /// <summary>Locate the XTS package root near the built project (or the
    /// editor install); errors when it cannot be found.</summary>
    private static (string? Root, List<string> Errors) LocatePackage(string projectPath)
    {
        var errors = new List<string>();
        var roots = new List<string>(CandidateRoots(projectPath));
        // the package may also live in the editor install tree, above the
        // running editor/test assembly (typical for out-of-tree projects)
        var assemblyDir = Path.GetDirectoryName(typeof(XtsBuildPostProcessor).Assembly.Location);
        for (var d = assemblyDir; !string.IsNullOrEmpty(d) && d.Length > 3; d = Path.GetDirectoryName(d))
            roots.Add(d);

        foreach (var root in roots)
        {
            var candidate = Path.Combine(root, "Packages", "com.xengine.xts");
            if (Directory.Exists(candidate))
                return (candidate, errors);
        }
        errors.Add("com.xengine.xts package not found near the built project; cannot verify the native dependency manifest");
        return (null, errors);
    }

    private static IEnumerable<string> CandidateRoots(string projectPath)
    {
        var dir = Path.GetFullPath(projectPath);
        for (var i = 0; i < 6 && !string.IsNullOrEmpty(dir); i++)
        {
            yield return dir;
            dir = Path.GetDirectoryName(dir)!;
        }
    }

    private static List<string> FindScriptProjects(string projectPath)
    {
        var found = new List<string>();
        foreach (var marker in Directory.EnumerateFiles(
                     projectPath, XtsProjectManifest.FileName, SearchOption.AllDirectories))
        {
            found.Add(Path.GetDirectoryName(marker)!);
        }
        return found;
    }
}
