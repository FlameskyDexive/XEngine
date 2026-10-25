// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.IO;
using System.Text.Json;

namespace XEngine.Editor.Build.Ios;

/// <summary>
/// iOS audio pack contract. The library must be <c>libminiaudioex.dylib</c> built
/// for the selected iOS device or simulator runtime identifier. Missing, desktop,
/// and cross-platform binaries fail closed.
/// </summary>
internal static class IosNativeAudioContract
{
    public const string RuntimeIdentifier = "ios-arm64";
    public const string LibraryFileName = "libminiaudioex.dylib";
    public const string VerificationFileName = "ios-native-audio.json";

    public const string MissingLibraryError =
        "iOS audio requires libminiaudioex for ios-arm64 (Core Audio/AUHAL). This host has no such binary; refusing to pack a desktop dylib.";

    public const string MissingSimulatorLibraryError =
        "iOS Simulator audio requires libminiaudioex for iossimulator-arm64. Build the simulator variant with Tools/IOS/build-miniaudio.sh iossimulator-arm64.";

    public const string DesktopDylibRefusedError =
        "iOS audio refused a PE/desktop miniaudio binary. Rebuild libminiaudioex for ios-arm64 on macOS+Xcode.";

    public const string WrongFormatError =
        "iOS audio requires a thin Mach-O 64-bit dylib. ELF/PE/static archives are not packed.";

    public const string WrongArchError =
        "iOS audio requires CPU_TYPE_ARM64. x86_64 or other Mach-O slices are not packed.";

    public const string WrongPlatformError =
        "iOS audio dylib platform does not match the requested device/simulator target.";

    public const string MissingSymbolError =
        "iOS audio dylib is missing required miniaudioex exports (ma_ex_context_init / ma_engine_start).";

    public static string GetSourcePath(string nativeLibraryRoot, string runtimeIdentifier = RuntimeIdentifier) =>
        Path.Combine(nativeLibraryRoot, runtimeIdentifier, "native", LibraryFileName);

    public static string GetPackagedPath(string playerDirectory) =>
        Path.Combine(playerDirectory, "NativeLibraries", LibraryFileName);

    public static bool TryGetSourcePath(string? nativeLibraryRoot, out string sourcePath)
        => TryGetSourcePath(nativeLibraryRoot, RuntimeIdentifier, out sourcePath);

    public static bool TryGetSourcePath(string? nativeLibraryRoot, string runtimeIdentifier, out string sourcePath)
    {
        sourcePath = string.Empty;
        if (string.IsNullOrWhiteSpace(nativeLibraryRoot))
            return false;
        sourcePath = GetSourcePath(nativeLibraryRoot, runtimeIdentifier);
        return File.Exists(sourcePath);
    }

    public static void RequireIosArm64Library(string path)
        => RequireIosLibrary(path, RuntimeIdentifier);

    public static void RequireIosLibrary(string path, string runtimeIdentifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ValidateRuntimeIdentifier(runtimeIdentifier);
        if (!File.Exists(path))
        {
            string message = runtimeIdentifier == "iossimulator-arm64"
                ? MissingSimulatorLibraryError
                : MissingLibraryError;
            throw new FileNotFoundException(message, path);
        }
        IosMachOInspector.RequireIosAudioDylib(path, runtimeIdentifier);
    }

    public static bool TryCopy(string? nativeLibraryRoot, string playerDirectory)
        => TryCopy(nativeLibraryRoot, playerDirectory, RuntimeIdentifier);

    public static bool TryCopy(string? nativeLibraryRoot, string playerDirectory, string runtimeIdentifier)
    {
        ValidateRuntimeIdentifier(runtimeIdentifier);
        if (!TryGetSourcePath(nativeLibraryRoot, runtimeIdentifier, out string source))
            return false;

        RequireIosLibrary(source, runtimeIdentifier);
        string destination = GetPackagedPath(playerDirectory);
        Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
        File.Copy(source, destination, overwrite: true);
        return true;
    }

    public static void WriteVerification(
        string exportRoot,
        bool packaged,
        string runtimeIdentifier = RuntimeIdentifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(exportRoot);
        ValidateRuntimeIdentifier(runtimeIdentifier);
        string directory = Path.Combine(exportRoot, "verification");
        Directory.CreateDirectory(directory);
        File.WriteAllText(
            Path.Combine(directory, VerificationFileName),
            JsonSerializer.Serialize(
                new
                {
                    schemaVersion = 1,
                    runtimeIdentifier,
                    library = LibraryFileName,
                    backend = runtimeIdentifier == "iossimulator-arm64" ? "CoreAudio Simulator" : "CoreAudio/AUHAL",
                    sessionCategory = "AVAudioSessionCategoryPlayback",
                    backgroundAudioMode = false,
                    packaged,
                },
                new JsonSerializerOptions { WriteIndented = true }));
    }

    private static void ValidateRuntimeIdentifier(string runtimeIdentifier)
    {
        if (runtimeIdentifier is not ("ios-arm64" or "iossimulator-arm64"))
        {
            throw new ArgumentException(
                "iOS native audio supports ios-arm64 and iossimulator-arm64.",
                nameof(runtimeIdentifier));
        }
    }
}
