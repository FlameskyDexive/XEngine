// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;

using XEngine.Editor.Theming;
using XEngine.Editor.Utils;

namespace XEngine.Editor.Core;

/// <summary>
/// Launches the user's code editor at a specific file, line and column (Console double-click,
/// stack-trace links, Project .cs double-click). VS Code and Rider take the caret position on
/// the command line; Visual Studio and the system default only open the file.
///
/// The choice lives in <see cref="EditorSettings.ExternalCodeEditor"/> as a string
/// ("auto" / "system" / "vscode" / "rider" / "vs" / "custom") so unknown future values
/// deserialize gracefully, same as PreferredGraphicsBackend.
/// </summary>
public static class ExternalCodeEditor
{
    public const string Auto = "auto";
    public const string SystemDefault = "system";
    public const string VsCode = "vscode";
    public const string Rider = "rider";
    public const string VisualStudio = "vs";
    public const string Custom = "custom";

    // Probing hits the filesystem (and vswhere runs a process), so resolved paths are cached.
    // null = probed and not found; missing key = not probed yet.
    private static readonly Dictionary<string, string?> s_resolved = new();

    /// <summary>Open a source file, jumping to a 1-based line/column when the editor supports it.
    /// Values &lt;= 0 mean "unknown" and fall back to just opening the file.</summary>
    public static void Open(string filePath, int line = 0, int column = 0)
    {
        if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath)) return;

        string kind = EditorSettings.Instance.ExternalCodeEditor;
        if (string.IsNullOrWhiteSpace(kind)) kind = Auto;

        try
        {
            switch (kind)
            {
                case VsCode: if (!TryOpenVsCode(filePath, line, column)) OpenShell(filePath); return;
                case Rider: if (!TryOpenRider(filePath, line)) OpenShell(filePath); return;
                case VisualStudio: if (!TryOpenVisualStudio(filePath)) OpenShell(filePath); return;
                case Custom: if (!TryOpenCustom(filePath, line, column)) OpenShell(filePath); return;
                case SystemDefault: OpenShell(filePath); return;
                default: // auto: first editor found wins, system default as the floor
                    if (TryOpenVsCode(filePath, line, column)) return;
                    if (TryOpenRider(filePath, line)) return;
                    if (TryOpenVisualStudio(filePath)) return;
                    OpenShell(filePath);
                    return;
            }
        }
        catch (Exception ex)
        {
            Runtime.Debug.LogWarning($"External code editor failed ({kind}): {ex.Message}");
            OpenShell(filePath);
        }
    }

    /// <summary>Human-readable resolution of the current setting, for the Preferences note row.</summary>
    public static string DescribeResolved()
    {
        string kind = EditorSettings.Instance.ExternalCodeEditor;
        if (string.IsNullOrWhiteSpace(kind)) kind = Auto;
        return kind switch
        {
            SystemDefault => "Opens files with the OS file association.",
            Custom => string.IsNullOrWhiteSpace(EditorSettings.Instance.ExternalCodeEditorCustomPath)
                ? "Custom: no executable set."
                : $"Custom: {EditorSettings.Instance.ExternalCodeEditorCustomPath}",
            VsCode => ResolveVsCode() is { } code ? $"VS Code: {code}" : "VS Code: not found (falls back to system default).",
            Rider => ResolveRider() is { } rider ? $"Rider: {rider}" : "Rider: not found (falls back to system default).",
            VisualStudio => ResolveVisualStudio() is { } vs ? $"Visual Studio: {vs}" : "Visual Studio: not found (falls back to system default).",
            _ => ResolveVsCode() is { } c ? $"Auto → VS Code: {c}"
                : ResolveRider() is { } r ? $"Auto → Rider: {r}"
                : ResolveVisualStudio() is { } v ? $"Auto → Visual Studio: {v}"
                : "Auto → system default (no known editor found).",
        };
    }

    /// <summary>Settings changed (editor kind or custom path) — drop cached probe results.</summary>
    public static void InvalidateCache() => s_resolved.Clear();

    // ── Script assets double-click straight into the code editor ─────────────
    // Text-ish assets with no dedicated XEngine editor; anything not listed here falls back to the
    // Project panel's shell-open path.
    [AssetDoubleClickHandler(".cs", ".shader", ".hlsl", ".glsl", ".txt", ".md", ".json", ".xml")]
    private static bool OpenScriptHandler(string relativePath, Guid guid)
    {
        string? assets = Projects.Project.Current?.AssetsPath;
        if (assets == null) return false;
        string abs = Path.Combine(assets, relativePath);
        if (!File.Exists(abs)) return false;
        Open(abs);
        return true;
    }

    // ── Per-editor launchers ─────────────────────────────────────────────────

    private static bool TryOpenVsCode(string file, int line, int column)
    {
        string? exe = ResolveVsCode();
        if (exe == null) return false;
        string target = line > 0 ? (column > 0 ? $"{file}:{line}:{column}" : $"{file}:{line}") : file;
        Launch(exe, $"--goto \"{target}\"");
        return true;
    }

    private static bool TryOpenRider(string file, int line)
    {
        string? exe = ResolveRider();
        if (exe == null) return false;
        Launch(exe, line > 0 ? $"--line {line} \"{file}\"" : $"\"{file}\"");
        return true;
    }

    private static bool TryOpenVisualStudio(string file)
    {
        string? exe = ResolveVisualStudio();
        if (exe == null) return false;
        // devenv /edit opens in an existing instance; line navigation needs DTE and is not attempted.
        Launch(exe, $"/edit \"{file}\"");
        return true;
    }

    private static bool TryOpenCustom(string file, int line, int column)
    {
        var s = EditorSettings.Instance;
        if (string.IsNullOrWhiteSpace(s.ExternalCodeEditorCustomPath) || !File.Exists(s.ExternalCodeEditorCustomPath))
            return false;
        string args = string.IsNullOrWhiteSpace(s.ExternalCodeEditorCustomArgs) ? "\"{file}\"" : s.ExternalCodeEditorCustomArgs;
        args = args.Replace("{file}", file)
                   .Replace("{line}", Math.Max(line, 1).ToString())
                   .Replace("{column}", Math.Max(column, 1).ToString());
        Launch(s.ExternalCodeEditorCustomPath, args);
        return true;
    }

    private static void OpenShell(string file) => EditorUtils.OpenUrl(file);

    private static void Launch(string exe, string args)
        => Process.Start(new ProcessStartInfo(exe, args) { UseShellExecute = false, CreateNoWindow = true });

    // ── Detection ────────────────────────────────────────────────────────────

    private static string? ResolveVsCode() => ResolveCached(VsCode, static () =>
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            string local = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs", "Microsoft VS Code", "Code.exe");
            if (File.Exists(local)) return local;
            string program = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles),
                "Microsoft VS Code", "Code.exe");
            if (File.Exists(program)) return program;
            return FindOnPath("code.cmd") ?? FindOnPath("code.exe");
        }
        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            const string mac = "/Applications/Visual Studio Code.app/Contents/Resources/app/bin/code";
            if (File.Exists(mac)) return mac;
        }
        return FindOnPath("code");
    });

    private static string? ResolveRider() => ResolveCached(Rider, static () =>
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            string toolbox = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "Programs", "Rider", "bin", "rider64.exe");
            if (File.Exists(toolbox)) return toolbox;

            // Direct installs land in Program Files\JetBrains\JetBrains Rider <version>\bin.
            string jetbrains = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "JetBrains");
            if (Directory.Exists(jetbrains))
            {
                string? best = null;
                foreach (string dir in Directory.GetDirectories(jetbrains, "JetBrains Rider*"))
                {
                    string exe = Path.Combine(dir, "bin", "rider64.exe");
                    if (File.Exists(exe) && (best == null || string.CompareOrdinal(exe, best) > 0))
                        best = exe;
                }
                if (best != null) return best;
            }
            return FindOnPath("rider64.exe") ?? FindOnPath("rider.cmd");
        }
        return FindOnPath("rider");
    });

    private static string? ResolveVisualStudio() => ResolveCached(VisualStudio, static () =>
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows)) return null;
        string vswhere = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "Microsoft Visual Studio", "Installer", "vswhere.exe");
        if (!File.Exists(vswhere)) return null;
        try
        {
            using var p = Process.Start(new ProcessStartInfo(vswhere, "-latest -property productPath")
            {
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true,
            });
            if (p == null) return null;
            string output = p.StandardOutput.ReadToEnd().Trim();
            p.WaitForExit(5000);
            return File.Exists(output) ? output : null;
        }
        catch
        {
            return null;
        }
    });

    private static string? ResolveCached(string key, Func<string?> probe)
    {
        if (!s_resolved.TryGetValue(key, out string? path))
            s_resolved[key] = path = probe();
        return path;
    }

    private static string? FindOnPath(string fileName)
    {
        string? pathVar = Environment.GetEnvironmentVariable("PATH");
        if (pathVar == null) return null;
        foreach (string dir in pathVar.Split(Path.PathSeparator))
        {
            if (string.IsNullOrWhiteSpace(dir)) continue;
            try
            {
                string candidate = Path.Combine(dir.Trim(), fileName);
                if (File.Exists(candidate)) return candidate;
            }
            catch { }
        }
        return null;
    }
}
