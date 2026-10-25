// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

using FFmpeg.AutoGen;

namespace XEngine.Runtime.Video;

/// <summary>
/// Locates and loads the FFmpeg shared libraries vendored per-RID under
/// <c>Libraries/{rid}/native/</c> (copied into build output as <c>runtimes/{rid}/native/</c>).
/// Each library is preloaded with <see cref="NativeLibrary"/> so FFmpeg.AutoGen's DllImports
/// resolve against the already-loaded modules regardless of OS search-path rules.
/// Thread-safe, idempotent; loads once per process.
/// </summary>
public static unsafe class FFmpegNativeLoader
{
    /// <summary>Environment variable override pointing at a directory containing the FFmpeg shared libraries.</summary>
    public const string PathEnvironmentVariable = "XENGINE_FFMPEG_PATH";

    // FFmpeg n8.1 ABI majors: avutil 60, swresample 6, swscale 9, avcodec 62, avformat 62, avfilter 11.
    // Load order matters: dependencies first.
    private static readonly string[] LibraryStems =
    [
        "avutil-60",
        "swresample-6",
        "swscale-9",
        "avcodec-62",
        "avformat-62",
        "avfilter-11",
    ];

    private static readonly object LoadLock = new();
    private static bool _loaded;
    private static string? _rootPath;
    private static string? _versionInfo;
    private static readonly List<nint> LoadedHandles = [];

    public static bool IsLoaded => _loaded;

    /// <summary>Directory the libraries were loaded from, or null before a successful load.</summary>
    public static string? RootPath => _rootPath;

    /// <summary>FFmpeg version string (e.g. "8.1"), or null before a successful load.</summary>
    public static string? VersionInfo => _versionInfo;

    /// <summary>
    /// Loads FFmpeg once per process. Throws <see cref="VideoException"/> with
    /// <see cref="VideoErrorCode.NativeLoadFailed"/> when the libraries cannot be found or
    /// their ABI does not match the pinned majors.
    /// </summary>
    public static void EnsureLoaded()
    {
        if (_loaded)
        {
            return;
        }

        lock (LoadLock)
        {
            if (_loaded)
            {
                return;
            }

            try
            {
                LoadCore();
            }
            catch (VideoException)
            {
                UnloadHandles();
                throw;
            }
            catch (Exception ex)
            {
                UnloadHandles();
                throw new VideoException(
                    VideoErrorCode.NativeLoadFailed,
                    $"Failed to load FFmpeg shared libraries: {ex.Message}", ex);
            }
        }
    }

    /// <summary>
    /// Returns true when the FFmpeg libraries can be loaded. Never throws; caches success
    /// the same way <see cref="EnsureLoaded"/> does.
    /// </summary>
    public static bool TryEnsureLoaded()
    {
        try
        {
            EnsureLoaded();
            return true;
        }
        catch (VideoException)
        {
            return false;
        }
    }

    private static void LoadCore()
    {
        string? rootPath = ProbeRootPath();
#if XENGINE_HARMONYOS
        // On OpenHarmony the payload ships in the HAP's libs/<abi>/ folder (staged by the
        // build pipeline), which the OS app loader puts on the default dlopen search path.
        // AppContext.BaseDirectory usually points there, but when it does not, fall back to
        // loading each library by plain soname and let the OS resolve it — this never picks
        // up a foreign system FFmpeg because HarmonyOS ships none.
        if (rootPath == null)
        {
            LoadByDefaultSearch();
            rootPath = "(app default library search path)";
        }
#endif
        if (rootPath == null)
        {
            throw CreateNotFoundException();
        }

        foreach (string stem in LibraryStems)
        {
            string fileName = GetLibraryFileName(stem);
            string fullPath = Path.Combine(rootPath, fileName);
            if (!File.Exists(fullPath))
            {
                throw new VideoException(
                    VideoErrorCode.NativeLoadFailed,
                    $"FFmpeg library '{fileName}' was not found in '{rootPath}'.");
            }

            LoadedHandles.Add(NativeLibrary.Load(fullPath));
        }

        // Hint for FFmpeg.AutoGen's own resolver (it probes RootPath before the default search).
        ffmpeg.RootPath = Directory.Exists(rootPath) ? rootPath : AppContext.BaseDirectory;

        // Touch the core library through AutoGen so a broken ABI surfaces here, not at first use.
        _versionInfo = ffmpeg.av_version_info();
        if (string.IsNullOrEmpty(_versionInfo))
        {
            throw new VideoException(
                VideoErrorCode.NativeLoadFailed,
                "FFmpeg libraries loaded but av_version_info() returned nothing.");
        }

        _rootPath = rootPath;
        _loaded = true;
    }

#if XENGINE_HARMONYOS
    /// <summary>
    /// Loads each library by plain soname (dlopen search-path semantics). Throws
    /// <see cref="VideoException"/> naming the first library that cannot be resolved.
    /// </summary>
    private static void LoadByDefaultSearch()
    {
        foreach (string stem in LibraryStems)
        {
            string fileName = GetLibraryFileName(stem);
            try
            {
                LoadedHandles.Add(NativeLibrary.Load(fileName));
            }
            catch (Exception exception)
            {
                UnloadHandles();
                throw new VideoException(
                    VideoErrorCode.NativeLoadFailed,
                    $"Failed to load FFmpeg library '{fileName}' from the app library search path: {exception.Message}",
                    exception);
            }
        }

        ffmpeg.RootPath = AppContext.BaseDirectory;
    }
#endif

#if XENGINE_HARMONYOS
    /// <summary>
    /// Returns the directory of the first mapped file whose path ends with
    /// <paramref name="fileName"/>, or null. /proc/self/maps is the only portable way for a
    /// NativeAOT guest library to locate the app-private bundle directory OHOS extracted
    /// the HAP's native libraries into.
    /// </summary>
    private static string? GetLoadedModuleDirectory(string fileName)
    {
        try
        {
            using StreamReader reader = new("/proc/self/maps");
            string? line;
            while ((line = reader.ReadLine()) != null)
            {
                int space = line.LastIndexOf(' ');
                if (space < 0 || line.Length - space - 1 <= fileName.Length)
                    continue;
                string path = line[(space + 1)..];
                if (!path.EndsWith(fileName, StringComparison.Ordinal) || !path.StartsWith('/'))
                    continue;
                string? directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory))
                    return directory;
            }
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
        return null;
    }
#endif

    private static VideoException CreateNotFoundException()
    {
        string rid = GetRuntimeIdentifier();
        string primary = GetLibraryFileName(LibraryStems[0]);
        return new VideoException(
            VideoErrorCode.NativeLoadFailed,
            $"FFmpeg shared libraries were not found (looked for '{primary}' in the " +
            $"runtimes/{rid}/native directory next to the app, the app directory, and " +
            $"{PathEnvironmentVariable}).");
    }

    private static void UnloadHandles()
    {
        foreach (nint handle in LoadedHandles)
        {
            NativeLibrary.Free(handle);
        }

        LoadedHandles.Clear();
    }

    /// <summary>
    /// Candidate directories, in probe order: the environment override, then the standard
    /// engine layout (<c>runtimes/{rid}/native</c> next to the app), then the app directory.
    /// The first directory containing the primary library wins.
    /// </summary>
    public static string? ProbeRootPath()
    {
        string? primary = GetLibraryFileName(LibraryStems[0]);
        var candidates = new List<string>();
        string? envPath = Environment.GetEnvironmentVariable(PathEnvironmentVariable);
        if (!string.IsNullOrEmpty(envPath))
        {
            candidates.Add(envPath);
        }

        string baseDir = AppContext.BaseDirectory;
        string rid = GetRuntimeIdentifier();
        candidates.Add(Path.Combine(baseDir, "runtimes", rid, "native"));
        candidates.Add(Path.Combine(baseDir, "runtimes", rid, "native", "ffmpeg"));
        candidates.Add(baseDir);
        candidates.Add(Path.Combine(baseDir, "ffmpeg"));
#if XENGINE_HARMONYOS
        // The OHOS app loader extracts the HAP's libs/<abi>/ into the app's private bundle
        // directory, which is neither AppContext.BaseDirectory (a NativeAOT .so has no app
        // base dir) nor the plain-name dlopen search path. Our own module, however, was
        // dlopened from exactly that directory — resolve it through /proc/self/maps.
        string? moduleDirectory = GetLoadedModuleDirectory("libxengine_player.so")
            ?? GetLoadedModuleDirectory("libxengine_host.so");
        if (moduleDirectory != null)
        {
            candidates.Insert(0, moduleDirectory);
        }
#endif

        foreach (string candidate in candidates)
        {
            if (candidate != null && File.Exists(Path.Combine(candidate, primary)))
            {
                return candidate;
            }
        }

        return null;
    }

    /// <summary>
    /// Microsoft-style RID for the current platform, e.g. "win-x64", "linux-x64", "osx-arm64".
    /// OpenHarmony player builds report "openharmony-&lt;arch&gt;" so the per-RID payload
    /// directory matches the <c>Libraries/openharmony-{arch}/native</c> layout the build
    /// pipeline stages, instead of the generic "linux-arm64" a musl-based host would report.
    /// </summary>
    public static string GetRuntimeIdentifier()
    {
#if XENGINE_HARMONYOS
        string os = "openharmony";
#else
        string os = RuntimeInformation.IsOSPlatform(OSPlatform.Windows) ? "win"
            : RuntimeInformation.IsOSPlatform(OSPlatform.OSX) ? "osx"
            : RuntimeInformation.IsOSPlatform(OSPlatform.Linux) ? "linux"
            : "unknown";
#endif
        string arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X86 => "x86",
            Architecture.X64 => "x64",
            Architecture.Arm => "arm",
            Architecture.Arm64 => "arm64",
            _ => "unknown",
        };
        return $"{os}-{arch}";
    }

    private static string GetLibraryFileName(string stem)
    {
        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            return stem + ".dll";
        }

        if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            // BtbN/evermeet shared layouts use libavutil.60.dylib style sonames.
            int dash = stem.IndexOf('-');
            return dash < 0 ? $"lib{stem}.dylib" : $"lib{stem[..dash]}.{stem[(dash + 1)..]}.dylib";
        }

        // Linux and OpenHarmony (musl): ABI-major-only sonames, libavcodec.so.62.
        int dashLinux = stem.IndexOf('-');
        return dashLinux < 0 ? $"lib{stem}.so" : $"lib{stem[..dashLinux]}.so.{stem[(dashLinux + 1)..]}";
    }
}
