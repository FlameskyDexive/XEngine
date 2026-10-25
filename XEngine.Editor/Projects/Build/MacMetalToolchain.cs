using System;
using System.Diagnostics;
using System.IO;

namespace XEngine.Editor.Build;

/// <summary>Finds and verifies Xcode's offline Metal compiler without shell configuration.</summary>
internal static class MacMetalToolchain
{
    private const string SmokeSource = "#include <metal_stdlib>\nusing namespace metal;\n"
        + "kernel void xengine_toolchain_smoke(device uint* output [[buffer(0)]], "
        + "uint id [[thread_position_in_grid]]) { output[id] = id; }\n";

    internal static bool TryPrepare(out string diagnostic)
    {
        if (!OperatingSystem.IsMacOS())
        {
            diagnostic = "Metal compilation requires macOS.";
            return false;
        }

        if (!TryFindTools(out string metal, out string metallib))
        {
            diagnostic = "Xcode Metal toolchain was not found. Select Xcode with xcode-select "
                + "and install its Metal Toolchain component.";
            return false;
        }

        if (!CompileSmoke(metal, metallib, out diagnostic))
            return false;

        // Slang's downstream MetalLib compiler launches `metal` by name. Keep the resolved
        // toolchain on this process's PATH for the following shader-artifact compilation.
        string toolDirectory = Path.GetDirectoryName(metal)!;
        string path = Environment.GetEnvironmentVariable("PATH") ?? string.Empty;
        if (!PathContains(path, toolDirectory))
            Environment.SetEnvironmentVariable("PATH", toolDirectory + Path.PathSeparator + path);
        diagnostic = $"Metal compiler verified: {metal}; metallib: {metallib}";
        return true;
    }

    private static bool TryFindTools(out string metal, out string metallib)
    {
        metal = string.Empty;
        metallib = string.Empty;

        string? developerDirectory = Environment.GetEnvironmentVariable("DEVELOPER_DIR");
        if (string.IsNullOrWhiteSpace(developerDirectory))
            developerDirectory = RunFind("/usr/bin/xcode-select", "-p");
        if (!string.IsNullOrWhiteSpace(developerDirectory))
        {
            string defaultToolchain = Path.Combine(developerDirectory,
                "Toolchains", "XcodeDefault.xctoolchain", "usr", "bin");
            if (IsToolPair(Path.Combine(defaultToolchain, "metal"),
                    Path.Combine(defaultToolchain, "metallib"), out metal, out metallib))
                return true;
        }

        // Xcode's downloadable Metal Toolchain is mounted by MobileAsset. Its version and
        // mount suffix change, so enumerate the stable mount parent rather than pinning a path.
        // Probe this before xcrun: a stale DEVELOPER_DIR can make xcrun open the install-tools
        // prompt even though the current Metal Toolchain is already mounted and usable.
        const string mountRoot = "/private/var/run/com.apple.security.cryptexd/mnt";
        try
        {
            foreach (string mount in Directory.EnumerateDirectories(mountRoot,
                         "com.apple.MobileAsset.MetalToolchain-*"))
            {
                string bin = Path.Combine(mount, "Metal.xctoolchain", "usr", "bin");
                if (IsToolPair(Path.Combine(bin, "metal"), Path.Combine(bin, "metallib"),
                        out metal, out metallib))
                    return true;
            }
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }

        string? xcrunMetal = FindWithXcrun("metal");
        string? xcrunMetallib = FindWithXcrun("metallib");
        if (IsToolPair(xcrunMetal, xcrunMetallib, out metal, out metallib))
            return true;
        return false;
    }

    private static bool IsToolPair(string? metalCandidate, string? metallibCandidate,
        out string metal, out string metallib)
    {
        metal = string.Empty;
        metallib = string.Empty;
        if (string.IsNullOrWhiteSpace(metalCandidate) ||
            string.IsNullOrWhiteSpace(metallibCandidate) ||
            !File.Exists(metalCandidate) || !File.Exists(metallibCandidate))
            return false;
        metal = metalCandidate;
        metallib = metallibCandidate;
        return true;
    }

    private static string? FindWithXcrun(string tool) =>
        RunFind("/usr/bin/xcrun", "--sdk", "macosx", "--find", tool);

    private static string? RunFind(string executable, params string[] arguments)
    {
        try
        {
            var info = new ProcessStartInfo(executable)
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (string argument in arguments)
                info.ArgumentList.Add(argument);
            using Process? process = Process.Start(info);
            if (process == null)
                return null;
            string output = process.StandardOutput.ReadToEnd().Trim();
            if (!process.WaitForExit(10_000))
            {
                process.Kill(entireProcessTree: true);
                return null;
            }
            return process.ExitCode == 0 ? output : null;
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception)
        {
            return null;
        }
    }

    private static bool CompileSmoke(string metal, string metallib, out string diagnostic)
    {
        string directory = Path.Combine(Path.GetTempPath(), "xengine-metal-toolchain-" + Guid.NewGuid().ToString("N"));
        try
        {
            Directory.CreateDirectory(directory);
            string source = Path.Combine(directory, "smoke.metal");
            string air = Path.Combine(directory, "smoke.air");
            string library = Path.Combine(directory, "smoke.metallib");
            File.WriteAllText(source, SmokeSource);
            if (!RunCompiler(metal, ["-c", source, "-o", air], out diagnostic) ||
                !File.Exists(air) || new FileInfo(air).Length == 0)
                return false;
            if (!RunCompiler(metallib, [air, "-o", library], out diagnostic) ||
                !File.Exists(library) || new FileInfo(library).Length == 0)
                return false;
            diagnostic = string.Empty;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            diagnostic = $"Metal compiler smoke failed: {ex.Message}";
            return false;
        }
        finally
        {
            try
            {
                if (Directory.Exists(directory))
                    Directory.Delete(directory, recursive: true);
            }
            catch (IOException) { }
            catch (UnauthorizedAccessException) { }
        }
    }

    private static bool RunCompiler(string executable, string[] arguments, out string diagnostic)
    {
        try
        {
            var info = new ProcessStartInfo(executable)
            {
                RedirectStandardError = true,
                UseShellExecute = false,
            };
            foreach (string argument in arguments)
                info.ArgumentList.Add(argument);
            using Process? process = Process.Start(info);
            if (process == null)
            {
                diagnostic = $"Failed to start {executable}.";
                return false;
            }
            string error = process.StandardError.ReadToEnd();
            if (!process.WaitForExit(30_000))
            {
                process.Kill(entireProcessTree: true);
                diagnostic = $"{executable} timed out.";
                return false;
            }
            diagnostic = process.ExitCode == 0 ? string.Empty
                : $"{executable} exited {process.ExitCode}: {error.Trim()}";
            return process.ExitCode == 0;
        }
        catch (Exception ex) when (ex is IOException or System.ComponentModel.Win32Exception)
        {
            diagnostic = $"Failed to run {executable}: {ex.Message}";
            return false;
        }
    }

    private static bool PathContains(string path, string directory)
    {
        foreach (string entry in path.Split(Path.PathSeparator))
            if (string.Equals(entry, directory, StringComparison.Ordinal))
                return true;
        return false;
    }
}
