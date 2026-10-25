// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.IO;
using System.Text;

namespace XEngine.Editor.Build.Ios;

/// <summary>
/// Thin Mach-O identity checks for the iOS miniaudio dylib. Windows cannot compile
/// ios-arm64; this gate refuses PE/ELF/x86_64 binaries instead of packing a desktop lib.
/// </summary>
internal static class IosMachOInspector
{
    internal const uint Magic64LittleEndian = 0xFEEDFACF;
    internal const int CpuTypeArm64 = 0x0100000C;
    internal const int CpuTypeX86_64 = 0x01000007;
    internal const uint FileTypeDylib = 6;
    internal const uint LoadCommandBuildVersion = 0x32;
    internal const uint PlatformIos = 2;
    internal const uint PlatformIosSimulator = 7;

    internal static readonly string[] RequiredAudioSymbols =
    [
        "ma_ex_context_init",
        "ma_ex_context_get_engine",
        "ma_engine_start",
        "ma_engine_stop",
    ];

    internal static void RequireIosArm64AudioDylib(string path)
        => RequireIosAudioDylib(path, IosNativeAudioContract.RuntimeIdentifier);

    internal static void RequireIosAudioDylib(string path, string runtimeIdentifier)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        using FileStream stream = File.OpenRead(path);
        RequireIosAudioDylib(stream, path, runtimeIdentifier);
    }

    internal static void RequireIosArm64AudioDylib(Stream stream, string description)
        => RequireIosAudioDylib(stream, description, IosNativeAudioContract.RuntimeIdentifier);

    internal static void RequireIosAudioDylib(Stream stream, string description, string runtimeIdentifier)
    {
        ArgumentNullException.ThrowIfNull(stream);
        ArgumentException.ThrowIfNullOrWhiteSpace(description);
        uint expectedPlatform = runtimeIdentifier switch
        {
            "ios-arm64" => PlatformIos,
            "iossimulator-arm64" => PlatformIosSimulator,
            _ => throw new ArgumentException("Unsupported iOS Mach-O runtime identifier.", nameof(runtimeIdentifier)),
        };
        if (stream.Length < 32)
        {
            throw new InvalidDataException(
                IosNativeAudioContract.WrongFormatError + ": " + description);
        }

        long start = stream.Position;
        using var reader = new BinaryReader(stream, Encoding.UTF8, leaveOpen: true);
        uint magic = reader.ReadUInt32();
        if (magic == 0x5A4D || (magic & 0xFFFF) == 0x5A4D)
        {
            throw new InvalidDataException(
                IosNativeAudioContract.DesktopDylibRefusedError + ": " + description);
        }
        if (magic == 0x464C457F)
        {
            throw new InvalidDataException(
                IosNativeAudioContract.WrongFormatError + ": " + description);
        }
        if (magic != Magic64LittleEndian)
        {
            throw new InvalidDataException(
                IosNativeAudioContract.WrongFormatError + ": " + description);
        }

        int cpuType = reader.ReadInt32();
        if (cpuType != CpuTypeArm64)
        {
            throw new InvalidDataException(
                IosNativeAudioContract.WrongArchError + ": " + description);
        }

        reader.ReadInt32();
        uint fileType = reader.ReadUInt32();
        if (fileType != FileTypeDylib)
        {
            throw new InvalidDataException(
                IosNativeAudioContract.WrongFormatError + ": " + description);
        }

        uint loadCommandCount = reader.ReadUInt32();
        uint loadCommandBytes = reader.ReadUInt32();
        reader.ReadUInt32(); // flags
        reader.ReadUInt32(); // reserved
        long loadCommandsEnd = stream.Position + loadCommandBytes;
        if (loadCommandsEnd > stream.Length)
            throw new InvalidDataException(IosNativeAudioContract.WrongFormatError + ": " + description);

        bool foundBuildVersion = false;
        uint parsedCommands = 0;
        for (; parsedCommands < loadCommandCount && stream.Position + 8 <= loadCommandsEnd; parsedCommands++)
        {
            long commandStart = stream.Position;
            uint command = reader.ReadUInt32();
            uint commandSize = reader.ReadUInt32();
            if (commandSize < 8 || commandStart + commandSize > loadCommandsEnd)
                throw new InvalidDataException(IosNativeAudioContract.WrongFormatError + ": " + description);

            if (command == LoadCommandBuildVersion)
            {
                if (commandSize < 24)
                    throw new InvalidDataException(IosNativeAudioContract.WrongFormatError + ": " + description);
                uint platform = reader.ReadUInt32();
                foundBuildVersion = true;
                if (platform != expectedPlatform)
                {
                    throw new InvalidDataException(
                        IosNativeAudioContract.WrongPlatformError + ": " + description);
                }
            }

            stream.Position = commandStart + commandSize;
        }

        if (parsedCommands != loadCommandCount || stream.Position != loadCommandsEnd || !foundBuildVersion)
        {
            throw new InvalidDataException(
                IosNativeAudioContract.WrongPlatformError + " Missing or invalid LC_BUILD_VERSION: " + description);
        }

        stream.Position = start;
        byte[] payload = new byte[checked((int)(stream.Length - start))];
        int read = stream.Read(payload, 0, payload.Length);
        if (read != payload.Length)
            throw new EndOfStreamException(description);

        for (int i = 0; i < RequiredAudioSymbols.Length; i++)
        {
            if (!ContainsAscii(payload, RequiredAudioSymbols[i]))
            {
                throw new InvalidDataException(
                    IosNativeAudioContract.MissingSymbolError + " (" +
                    RequiredAudioSymbols[i] + "): " + description);
            }
        }
    }

    private static bool ContainsAscii(byte[] haystack, string needle)
    {
        byte[] needleBytes = Encoding.ASCII.GetBytes(needle);
        int last = haystack.Length - needleBytes.Length;
        for (int i = 0; i <= last; i++)
        {
            int n = 0;
            while (n < needleBytes.Length && haystack[i + n] == needleBytes[n])
                n++;
            if (n == needleBytes.Length)
                return true;
        }
        return false;
    }
}
