using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Diagnostics;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using XEngine.XTS.Editor.Build;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;

namespace XEngine.XTS.Editor;

/// <summary>
/// Result of one toolchain step (install / type-check / bundle).
/// </summary>
public sealed record XtsToolchainResult
{
    public bool Success { get; init; }
    public string Step { get; init; } = string.Empty;
    public string Output { get; init; } = string.Empty;
    public IReadOnlyList<string> Diagnostics { get; init; } = Array.Empty<string>();
}

/// <summary>
/// Editor-side TypeScript toolchain: creates script projects from the
/// package template, installs their npm tool dependencies once (typescript +
/// esbuild are editor-only, never shipped to players), type-checks with
/// `tsc --noEmit`, and bundles with esbuild as an ES2020 module. Builds run
/// entirely from the project's local node_modules — no network is required
/// after the one-time install.
/// </summary>
public sealed class XtsToolchainService
{
    /// <summary>Creates a new script project from the package template.</summary>
    public static void CreateProject(string directory, string projectName)
    {
        var templateRoot = FindTemplateRoot();
        Directory.CreateDirectory(directory);
        Directory.CreateDirectory(Path.Combine(directory, "src"));
        foreach (var file in new[] { "package.json", "tsconfig.json", "src/main.ts", "src/xts-globals.d.ts" })
        {
            var text = File.ReadAllText(Path.Combine(templateRoot, file.Replace('/', Path.DirectorySeparatorChar)));
            text = text.Replace("{{PROJECT_NAME}}", projectName);
            File.WriteAllText(Path.Combine(directory, file.Replace('/', Path.DirectorySeparatorChar)), text);
        }
        File.WriteAllText(Path.Combine(directory, ".gitignore"), "node_modules/\ndist/\n");
        // player-build marker (see XtsBuildPostProcessor)
        File.WriteAllText(Path.Combine(directory, "xts.project.json"),
            new XtsProjectManifest { Name = projectName }.ToJson());
    }

    /// <summary>
    /// One-time (per project) npm install of the tool dependencies. Safe to
    /// re-run; skipped entirely when node_modules already exists so builds
    /// stay offline.
    /// </summary>
    public XtsToolchainResult InstallTools(string projectDir, IReadOnlyDictionary<string, string>? environment = null)
    {
        if (Directory.Exists(Path.Combine(projectDir, "node_modules", "typescript")) &&
            Directory.Exists(Path.Combine(projectDir, "node_modules", "esbuild")))
            return new XtsToolchainResult { Success = true, Step = "install", Output = "node_modules present; skipped" };

        return Run("install", projectDir, "npm", "install --no-audit --no-fund", environment);
    }

    /// <summary>Type-check the project with `tsc --noEmit`.</summary>
    public XtsToolchainResult TypeCheck(string projectDir, IReadOnlyDictionary<string, string>? environment = null) =>
        Run("typecheck", projectDir, "npx", "tsc --noEmit --pretty false", environment);

    /// <summary>Bundle src/main.ts into dist/main.js (ES2020 ESM).</summary>
    public XtsToolchainResult Bundle(string projectDir, IReadOnlyDictionary<string, string>? environment = null)
    {
        var result = Run("bundle", projectDir,
            "npx", "esbuild src/main.ts --bundle --format=iife --target=es2020 --platform=browser --outfile=dist/main.js", environment);
        if (!result.Success)
            result = result with { Diagnostics = ClassifyBuildFailure(result.Output).Concat(result.Diagnostics).ToArray() };
        else if (!File.Exists(Path.Combine(projectDir, "dist", "main.js")))
            result = result with { Success = false, Diagnostics = new[] { "esbuild reported success but dist/main.js is missing" } };
        return result;
    }

    /// <summary>install → typecheck → bundle, stopping at the first failure.</summary>
    public XtsToolchainResult Build(string projectDir) => Build(projectDir, null);

    /// <summary>
    /// Build with extra environment variables (used to prove offline builds
    /// by pointing npm at an unreachable registry).
    /// </summary>
    public XtsToolchainResult Build(string projectDir, IReadOnlyDictionary<string, string>? environment)
    {
        var install = InstallTools(projectDir, environment);
        if (!install.Success)
            return install;
        var check = TypeCheck(projectDir, environment);
        if (!check.Success)
            return check;
        return Bundle(projectDir, environment);
    }

    // ------------------------------------------------------------ internals

    private static XtsToolchainResult Run(string step, string workingDir, string command, string arguments,
        IReadOnlyDictionary<string, string>? environment = null)
    {
        var (exe, args) = ResolveCommand(command, arguments);
        var psi = new ProcessStartInfo
        {
            FileName = exe,
            Arguments = args,
            WorkingDirectory = workingDir,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
        };
        if (environment != null)
        {
            foreach (var (key, value) in environment)
                psi.Environment[key] = value;
        }
        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException($"failed to start '{command}'");
        var output = process.StandardOutput.ReadToEnd() + process.StandardError.ReadToEnd();
        process.WaitForExit(10 * 60 * 1000);
        var success = process.ExitCode == 0;
        if (!success && step != "install")
            output += ClassifyBuildFailure(output);
        return new XtsToolchainResult
        {
            Success = success,
            Step = step,
            Output = output,
            Diagnostics = success ? Array.Empty<string>() : ClassifyBuildFailure(output),
        };
    }

    /// <summary>
    /// Turns raw tool output into located diagnostics: node built-in imports
    /// and other player-illegal dependencies are named explicitly.
    /// </summary>
    public static string[] ClassifyBuildFailure(string output)
    {
        var diagnostics = new List<string>();
        foreach (var token in new[] { "node:fs", "node:path", "node:child_process", "node:http", "node:net",
                                      "node:os", "node:crypto", "node:worker_threads", "\"fs\"", "\"path\"",
                                      "\"child_process\"", "\"http\"" })
        {
            if (output.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                diagnostics.Add(
                    $"node api dependency detected ({token}): player scripts run inside the QuickJS sandbox and " +
                    "cannot use Node built-ins. Remove the import or replace the package with a pure-JS dependency.");
                break;
            }
        }
        return diagnostics.ToArray();
    }

    private static (string Exe, string Args) ResolveCommand(string command, string arguments)
    {
        return OperatingSystem.IsWindows()
            ? (Environment.ExpandEnvironmentVariables("%ComSpec%"), $"/d /s /c \"{command} {arguments}\"")
            : (command, arguments);
    }

    internal static string FindTemplateRoot()
    {
        // walk up from the executing assembly (editor test / editor context)
        var dir = AppContext.BaseDirectory;
        for (var i = 0; i < 10 && dir is not null; i++)
        {
            var candidate = Path.Combine(dir, "Packages", "com.xengine.xts", "Editor", "Templates~", "tsproject");
            if (Directory.Exists(candidate))
                return candidate;
            dir = Path.GetDirectoryName(dir);
        }
        throw new InvalidOperationException("XTS project template not found (Packages/com.xengine.xts/Editor/Templates~/tsproject)");
    }
}
