// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Text;

using XEngine.Editor.Build.Ios;

using Xunit;

namespace XEngine.Editor.Test;

public sealed class IosNativeAudioContractTests : IDisposable
{
    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "XEngineIosNativeAudioTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public void RequireIosArm64Library_FailsWhenMissing()
    {
        string path = Path.Combine(_root, "missing.dylib");
        FileNotFoundException ex = Assert.Throws<FileNotFoundException>(
            () => IosNativeAudioContract.RequireIosArm64Library(path));
        Assert.Equal(IosNativeAudioContract.MissingLibraryError, ex.Message);
    }

    [Fact]
    public void RequireIosArm64Library_RefusesPeDesktopBinary()
    {
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, "miniaudioex.dll");
        File.WriteAllBytes(path, Encoding.ASCII.GetBytes("MZ this is a PE stub".PadRight(32, '\0')));
        InvalidDataException ex = Assert.Throws<InvalidDataException>(
            () => IosNativeAudioContract.RequireIosArm64Library(path));
        Assert.Contains(IosNativeAudioContract.DesktopDylibRefusedError, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RequireIosArm64Library_RefusesElf()
    {
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, "libminiaudioex.so");
        File.WriteAllBytes(path, [0x7F, (byte)'E', (byte)'L', (byte)'F', 2, 1, 1, 0, 0, 0, 0, 0, 0, 0, 0, 0,
            0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0]);
        InvalidDataException ex = Assert.Throws<InvalidDataException>(
            () => IosNativeAudioContract.RequireIosArm64Library(path));
        Assert.Contains(IosNativeAudioContract.WrongFormatError, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RequireIosArm64Library_RefusesX64MachO()
    {
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, "libminiaudioex.dylib");
        File.WriteAllBytes(path, CreateThinDylib(IosMachOInspector.CpuTypeX86_64, IosMachOInspector.RequiredAudioSymbols));
        InvalidDataException ex = Assert.Throws<InvalidDataException>(
            () => IosNativeAudioContract.RequireIosArm64Library(path));
        Assert.Contains(IosNativeAudioContract.WrongArchError, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RequireIosArm64Library_AcceptsArm64DylibWithExports()
    {
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, "libminiaudioex.dylib");
        File.WriteAllBytes(path, CreateThinDylib(IosMachOInspector.CpuTypeArm64, IosMachOInspector.RequiredAudioSymbols));
        IosNativeAudioContract.RequireIosArm64Library(path);
    }

    [Fact]
    public void RequireIosSimulatorLibrary_RejectsDevicePlatformBinary()
    {
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, "libminiaudioex.dylib");
        File.WriteAllBytes(path, CreateThinDylib(
            IosMachOInspector.CpuTypeArm64,
            IosMachOInspector.RequiredAudioSymbols,
            IosMachOInspector.PlatformIos));

        InvalidDataException ex = Assert.Throws<InvalidDataException>(
            () => IosNativeAudioContract.RequireIosLibrary(path, "iossimulator-arm64"));
        Assert.Contains(IosNativeAudioContract.WrongPlatformError, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RequireIosDeviceLibrary_RejectsSimulatorPlatformBinary()
    {
        Directory.CreateDirectory(_root);
        string path = Path.Combine(_root, "libminiaudioex.dylib");
        File.WriteAllBytes(path, CreateThinDylib(
            IosMachOInspector.CpuTypeArm64,
            IosMachOInspector.RequiredAudioSymbols,
            IosMachOInspector.PlatformIosSimulator));

        InvalidDataException ex = Assert.Throws<InvalidDataException>(
            () => IosNativeAudioContract.RequireIosLibrary(path, "ios-arm64"));
        Assert.Contains(IosNativeAudioContract.WrongPlatformError, ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TryCopy_DoesNotCopyDesktopDylibIntoPlayer()
    {
        string nativeRoot = Path.Combine(_root, "Libraries");
        string source = IosNativeAudioContract.GetSourcePath(nativeRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.WriteAllBytes(source, Encoding.ASCII.GetBytes("MZ desktop".PadRight(32, '\0')));
        string player = Path.Combine(_root, "Player");

        Assert.Throws<InvalidDataException>(() => IosNativeAudioContract.TryCopy(nativeRoot, player));
        Assert.False(File.Exists(IosNativeAudioContract.GetPackagedPath(player)));
    }

    [Fact]
    public void TryCopy_CopiesValidIosArm64Dylib()
    {
        string nativeRoot = Path.Combine(_root, "Libraries");
        string source = IosNativeAudioContract.GetSourcePath(nativeRoot);
        Directory.CreateDirectory(Path.GetDirectoryName(source)!);
        File.WriteAllBytes(source, CreateThinDylib(IosMachOInspector.CpuTypeArm64, IosMachOInspector.RequiredAudioSymbols));
        string player = Path.Combine(_root, "Player");

        Assert.True(IosNativeAudioContract.TryCopy(nativeRoot, player));
        Assert.True(File.Exists(IosNativeAudioContract.GetPackagedPath(player)));
    }

    [Fact]
    public void TryCopy_UsesSeparateSimulatorNativeLibrary()
    {
        string nativeRoot = Path.Combine(_root, "Libraries");
        string deviceSource = IosNativeAudioContract.GetSourcePath(nativeRoot);
        string simulatorSource = IosNativeAudioContract.GetSourcePath(nativeRoot, "iossimulator-arm64");
        Directory.CreateDirectory(Path.GetDirectoryName(deviceSource)!);
        Directory.CreateDirectory(Path.GetDirectoryName(simulatorSource)!);
        File.WriteAllBytes(deviceSource, CreateThinDylib(
            IosMachOInspector.CpuTypeArm64,
            IosMachOInspector.RequiredAudioSymbols,
            IosMachOInspector.PlatformIos));
        byte[] simulatorBytes = CreateThinDylib(
            IosMachOInspector.CpuTypeArm64,
            IosMachOInspector.RequiredAudioSymbols,
            IosMachOInspector.PlatformIosSimulator);
        File.WriteAllBytes(simulatorSource, simulatorBytes);
        string player = Path.Combine(_root, "SimulatorPlayer");

        Assert.True(IosNativeAudioContract.TryCopy(nativeRoot, player, "iossimulator-arm64"));
        Assert.Equal(simulatorBytes, File.ReadAllBytes(IosNativeAudioContract.GetPackagedPath(player)));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    internal static byte[] CreateThinDylib(
        int cpuType,
        IReadOnlyList<string> symbols,
        uint platform = IosMachOInspector.PlatformIos)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true);
        writer.Write(IosMachOInspector.Magic64LittleEndian);
        writer.Write(cpuType);
        writer.Write(0);
        writer.Write(IosMachOInspector.FileTypeDylib);
        writer.Write(1u); // ncmds
        writer.Write(24u); // sizeofcmds
        writer.Write(0u);
        writer.Write(0u);
        writer.Write(IosMachOInspector.LoadCommandBuildVersion);
        writer.Write(24u);
        writer.Write(platform);
        writer.Write(0u); // minos
        writer.Write(0u); // sdk
        writer.Write(0u); // ntools
        for (int i = 0; i < symbols.Count; i++)
        {
            writer.Write(Encoding.ASCII.GetBytes(symbols[i]));
            writer.Write((byte)0);
        }
        return stream.ToArray();
    }
}
