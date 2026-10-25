using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

using XEngine.Editor.Projects.Build;
using XEngine.ILInterpreter.Compatibility;
using XEngine.ILInterpreter.Runtime;

namespace XEngine.ILInterpreter.Compatibility;

/// <summary>
/// One interpreted module's build entry: hash + compatibility verdict + the
/// binding requirements the host player must register.
/// </summary>
public sealed class ILModuleManifestEntry
{
    public string Name = "";
    public string Sha256 = "";
    public string RelativePath = "";
    public bool Compatible;
    public List<string> RequiredBindings = new();
    public List<string> Findings = new();
}

/// <summary>
/// Build post-processor: packages the interpreted module set as player content.
/// Copies every compiled module DLL from Library/ILInterpreter/Modules into
/// &lt;output&gt;/Content/ILInterpreter/, runs the compatibility scanner against
/// the project's binding list, and writes manifest.json (schema, module hashes,
/// required binding IDs, minimum host version). Modules that fail the scan are
/// reported in the manifest and fail the build — a broken hot-update module must
/// not ship silently (plan §7).
/// </summary>
public sealed class ILModuleManifestBuilder : IBuildPostProcessor
{
    public const string ManifestSchema = "xengine.ilinterpreter.manifest.v1";
    public const string ModulesFolderName = "Modules";

    public string Owner => "com.xengine.ilinterpreter";

    /// <summary>Project-relative binding declarations ("Type::Member/count" per line),
    /// merged with the editor defaults. File: &lt;project&gt;/ILBindings.txt.</summary>
    public static List<string> LoadProjectBindings(string projectPath)
    {
        var list = new List<string>();
        string path = Path.Combine(projectPath, "ILBindings.txt");
        if (!File.Exists(path)) return list;
        foreach (var raw in File.ReadAllLines(path))
        {
            var line = raw.Trim();
            if (line.Length == 0 || line.StartsWith('#')) continue;
            list.Add(line);
        }
        return list;
    }

    List<string> IBuildPostProcessor.Process(BuildPostContext context)
    {
        var errors = new List<string>();

        string projectPath = context.ProjectPath;
        string moduleRoot = Path.Combine(projectPath, "Library", "ILInterpreter", "Modules");
        string contentRoot = Path.Combine(context.OutputDirectory, "Content", "ILInterpreter");
        string modulesOut = Path.Combine(contentRoot, ModulesFolderName);

        // No interpreted modules at all: nothing to package (the feature is optional).
        if (!Directory.Exists(moduleRoot))
        {
            Directory.CreateDirectory(contentRoot);
            File.WriteAllText(Path.Combine(contentRoot, "manifest.json"), EmptyManifest());
            return errors;
        }

        Directory.CreateDirectory(modulesOut);

        var bindings = LoadProjectBindings(projectPath);
        var entries = new List<ILModuleManifestEntry>();

        // N1 convention: one sub-directory per module, <dir>/<dir>.dll. Copy-local
        // dependency DLLs that land next to the module are ignored by design.
        foreach (var moduleDir in Directory.EnumerateDirectories(moduleRoot))
        {
            string dirName = Path.GetFileName(moduleDir);
            string dll = Path.Combine(moduleDir, dirName + ".dll");
            if (!File.Exists(dll)) continue;
            string name = dirName;
            try
            {
                var report = ILModuleScanner.Scan(dll, bindings);

                // Copy-local engine DLLs in the module folder are not modules; the
                // scanner still parses them, so only keep entries that declare the
                // matching assembly name.
                if (!string.Equals(report.ModuleName, name, StringComparison.OrdinalIgnoreCase))
                    continue;

                string rel = Path.Combine(ModulesFolderName, name + ".dll");
                string dest = Path.Combine(modulesOut, name + ".dll");
                File.Copy(dll, dest, overwrite: true);

                var entry = new ILModuleManifestEntry
                {
                    Name = report.ModuleName,
                    Sha256 = report.ModuleSha256,
                    RelativePath = rel.Replace('\\', '/'),
                    Compatible = report.IsCompatible,
                    RequiredBindings = report.RequiredBindings,
                };
                foreach (var f in report.Findings)
                {
                    entry.Findings.Add($"{f.Type}.{f.Method}@{f.IlOffset}: {f.Kind} {f.Detail}");
                }

                string reportPath = Path.Combine(contentRoot, $"compat-{name}.json");
                File.WriteAllText(reportPath, report.ToJson());

                entries.Add(entry);
                if (!report.IsCompatible)
                {
                    errors.Add($"interpreted module '{name}' is incompatible: " +
                               string.Join("; ", entry.Findings.Take(3)) +
                               (entry.RequiredBindings.Count > 0
                                   ? "; missing bindings: " + string.Join(", ", entry.RequiredBindings)
                                   : string.Empty));
                }
            }
            catch (Exception ex)
            {
                // Non-CLI DLLs (copy-local dependencies) are skipped silently;
                // anything that scans but fails otherwise is a build error.
                if (string.Equals(report_NameIfScannable(dll), name, StringComparison.OrdinalIgnoreCase))
                    errors.Add($"interpreted module '{name}' could not be packaged: {ex.Message}");
            }
        }

        var manifest = new
        {
            schema = ManifestSchema,
            minimumHostVersion = ILInterpreterPackage.Version,
            modules = entries.ConvertAll(e => new
            {
                name = e.Name,
                sha256 = e.Sha256,
                path = e.RelativePath,
                compatible = e.Compatible,
                requiredBindings = e.RequiredBindings,
                bindingIds = e.RequiredBindings.ConvertAll(ILBindingIds.IdForRequirement),
            }),
        };
        File.WriteAllText(
            Path.Combine(contentRoot, "manifest.json"),
            JsonSerializer.Serialize(manifest, new JsonSerializerOptions { WriteIndented = true }));

        context.Data["ILInterpreter.Manifest"] = Path.Combine(contentRoot, "manifest.json");
        context.Data["ILInterpreter.ModuleCount"] = entries.Count.ToString();
        return errors;
    }

    private static string? report_NameIfScannable(string dll)
    {
        try
        {
            return ILModuleScanner.Scan(dll, Array.Empty<string>()).ModuleName;
        }
        catch
        {
            return null;
        }
    }

    private static string EmptyManifest() =>
        "{\n  \"schema\": \"" + ManifestSchema + "\",\n  \"modules\": []\n}";
}
