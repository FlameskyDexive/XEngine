using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using XHub.Models;

namespace XHub.Services;

/// <summary>
/// Probes a candidate installation root: locates the editor executable (flat install or the
/// source-repo <c>Build/Editor/&lt;Config&gt;</c> layout), reads its informational version, and
/// reads the PE machine type for the architecture. Pure probing — no writes, safe on any thread.
/// </summary>
public static class InstallationInspector
{
    public sealed record ProbeResult(
        bool Found,
        string EditorExePath,
        string VersionDisplay,
        string CanonicalVersion,
        string Channel,
        string Architecture);

    private static readonly string[] EditorExeNames =
        OperatingSystem.IsWindows() ? ["XEngine.Editor.exe"] : ["XEngine.Editor"];

    private static readonly string[] RepoEditorLayouts =
    [
        // Source-repo build outputs: classic layout and SDK-style TFM-nested layout (also used
        // when the record root already points at Build/Editor/<Config>).
        Path.Combine("Build", "Editor", "Release"),
        Path.Combine("Build", "Editor", "Debug"),
        Path.Combine("Build", "Editor", "Release", "net10.0"),
        Path.Combine("Build", "Editor", "Debug", "net10.0"),
        "net10.0",
        // Shipped release payload: the release zip wraps everything in an "XEngine/" folder,
        // so hub installs (and hand-extracted zips pointed at via Locate) sit one level down —
        // matches xengine-release.json's editorPath (XEngine/XEngine.Editor.exe).
        "XEngine",
    ];

    /// <summary>
    /// Locates the editor executable under <paramref name="root"/>. Accepts the exe path itself.
    /// Returns "" when nothing runnable exists (broken installation).
    /// </summary>
    public static string FindEditorExe(string root)
    {
        try
        {
            if (File.Exists(root) && IsEditorExeName(root))
                return root;

            if (!Directory.Exists(root))
                return "";

            foreach (string name in EditorExeNames)
            {
                string direct = Path.Combine(root, name);
                if (File.Exists(direct)) return direct;
            }

            foreach (string layout in RepoEditorLayouts)
            {
                foreach (string name in EditorExeNames)
                {
                    string candidate = Path.Combine(root, layout, name);
                    if (File.Exists(candidate)) return candidate;
                }
            }
        }
        catch
        {
            // Unreadable root reads as broken.
        }
        return "";
    }

    public static ProbeResult Probe(string root)
    {
        string exe = FindEditorExe(root);
        if (exe.Length == 0)
            return new ProbeResult(false, "", "", "", "unknown", "");

        string versionDisplay = ReadVersionFromExe(exe);
        string canonical = "";
        string channel = "unknown";
        if (EngineVersion.TryParse(versionDisplay, out EngineVersion? version))
        {
            canonical = version.Canonical;
            channel = version.Channel;
        }

        return new ProbeResult(true, exe, versionDisplay, canonical, channel, ReadArchitecture(exe));
    }

    private static bool IsEditorExeName(string path) =>
        Array.Exists(EditorExeNames, name => path.EndsWith(name, StringComparison.OrdinalIgnoreCase));

    private static string ReadVersionFromExe(string exe)
    {
        try
        {
            var info = System.Diagnostics.FileVersionInfo.GetVersionInfo(exe);
            // The csproj InformationalVersion lands in the Win32 ProductVersion resource.
            return info.ProductVersion?.Split('+')[0] ?? "";
        }
        catch
        {
            return "";
        }
    }

    /// <summary>Reads the PE machine field (x64 / x86 / arm64) without loading the image.</summary>
    private static string ReadArchitecture(string exe)
    {
        try
        {
            using FileStream stream = File.OpenRead(exe);
            using var reader = new BinaryReader(stream);
            if (stream.Length < 0x40) return "";
            stream.Seek(0x3C, SeekOrigin.Begin);
            int peOffset = reader.ReadInt32();
            if (peOffset <= 0 || peOffset + 6 > stream.Length) return "";
            stream.Seek(peOffset + 4, SeekOrigin.Begin);
            ushort machine = reader.ReadUInt16();
            return machine switch
            {
                0x8664 => "x64",
                0x014C => "x86",
                0xAA64 => "arm64",
                _ => RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant(),
            };
        }
        catch
        {
            return "";
        }
    }

    /// <summary>
    /// Well-known installation roots worth scanning (future installer default, machine-wide
    /// install dir, and the XENGINE_EDITOR_ROOT env override). Existing directories only.
    /// </summary>
    public static IReadOnlyList<string> DiscoverCandidateRoots()
    {
        var roots = new List<string>();

        void Add(string? candidate)
        {
            if (string.IsNullOrWhiteSpace(candidate)) return;
            candidate = Environment.ExpandEnvironmentVariables(candidate.Trim());
            if (Directory.Exists(candidate)) roots.Add(candidate);
        }

        string localAppData = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        Add(Path.Combine(localAppData, "Programs", "XEngine", "Editor"));
        Add(Path.Combine(localAppData, "XEngine", "Editor"));

        string programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        Add(Path.Combine(programFiles, "XEngine", "Editor"));

        // Explicit dev/CI seam: XENGINE_EDITOR_ROOT may list several roots (; on Windows, : elsewhere).
        string env = Environment.GetEnvironmentVariable("XENGINE_EDITOR_ROOT") ?? "";
        foreach (string part in env.Split(OperatingSystem.IsWindows() ? ';' : ':', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            Add(part);

        return roots;
    }
}
