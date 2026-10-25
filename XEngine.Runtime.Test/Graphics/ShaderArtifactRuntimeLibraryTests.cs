// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Text.Json;

using XEngine.Runtime.AssetStreaming;
using XEngine.Runtime.Rendering.Shaders;
using XEngine.Runtime.RHI;
using XEngine.Runtime.RHI.Shaders;

using Xunit;
using Xunit.Abstractions;

namespace XEngine.Runtime.Test;

[Collection(ShaderCompilerEnvironmentCollection.Name)]
public sealed class ShaderArtifactRuntimeLibraryTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string? _previousCompilerMode =
        Environment.GetEnvironmentVariable("XENGINE_SHADER_COMPILER");
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "XEngine-ShaderArtifactRuntimeTests",
        Guid.NewGuid().ToString("N"));

    public ShaderArtifactRuntimeLibraryTests(ITestOutputHelper output)
    {
        _output = output;
        ShaderArtifactRuntimeLibrary.ResetForTests();
    }

    public void Dispose()
    {
        ShaderArtifactRuntimeLibrary.ResetForTests();
        Environment.SetEnvironmentVariable("XENGINE_SHADER_COMPILER", _previousCompilerMode);
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Theory]
    [InlineData(GraphicsBackend.Vulkan, ShaderBytecodeFormat.SpirV)]
    [InlineData(GraphicsBackend.Direct3D12, ShaderBytecodeFormat.Dxil)]
    [InlineData(GraphicsBackend.WebGPU, ShaderBytecodeFormat.Wgsl)]
    [InlineData(GraphicsBackend.Metal, ShaderBytecodeFormat.MetalLib)]
    [InlineData(GraphicsBackend.WebGL2, ShaderBytecodeFormat.GlslEs300)]
    public void PackageStreamsLoadModernBackendMetadata(
        GraphicsBackend backend,
        ShaderBytecodeFormat expectedFormat)
    {
        ShaderArtifactEntry entry = CreateEntry(backend, "Tests/Offline", "Main", "");
        Dictionary<string, byte[]> package = CreatePackage(entry);

        Configure(package, "memory package");

        ShaderArtifact loaded = ShaderArtifactRuntimeLibrary.GetRequiredArtifact(
            "Tests/Offline",
            "Main",
            backend,
            "");
        Assert.Equal(expectedFormat, loaded.Bytecode.Format);
        Assert.Equal(backend, loaded.Backend);
        Assert.Equal(ShaderCompilerKind.Slang, loaded.CompilerKind);
        Assert.Equal("sm9-runtime-test", loaded.Diagnostics);
        ShaderArtifactRuntimeStats stats = ShaderArtifactRuntimeLibrary.GetStats();
        Assert.Equal(1, stats.ArtifactCount);
        Assert.Equal(1, stats.LookupHits);
        Assert.Equal(0, stats.LookupMisses);
    }

    [Fact]
    public void StableCatalogDefersArtifactDecodeUntilLookup()
    {
        ShaderArtifactEntry first = CreateEntry(GraphicsBackend.Vulkan, "Tests/Lazy/First", "Main", "");
        ShaderArtifactEntry second = CreateEntry(GraphicsBackend.Vulkan, "Tests/Lazy/Second", "Other", "");
        Dictionary<string, byte[]> package = CreateStablePackage(first, second);
        int artifactOpens = 0;

        ShaderArtifactRuntimeLibrary.ConfigureFromPackage(
            fileName =>
            {
                if (fileName.EndsWith(".xshader", StringComparison.Ordinal))
                    artifactOpens++;
                return package.TryGetValue(fileName, out byte[]? bytes)
                    ? new MemoryStream(bytes, writable: false)
                    : null;
            },
            "lazy package");

        ShaderArtifactRuntimeStats configured = ShaderArtifactRuntimeLibrary.GetStats();
        Assert.Equal(2, configured.ArtifactCount);
        Assert.Equal(0, configured.LoadedArtifactCount);
        Assert.Equal(0, configured.LoadedBytecodeBytes);
        Assert.Equal(0, artifactOpens);

        _ = ShaderArtifactRuntimeLibrary.GetRequiredArtifact(
            "Tests/Lazy/First", "Main", GraphicsBackend.Vulkan, "");
        Assert.Equal(1, artifactOpens);
        Assert.Equal(1, ShaderArtifactRuntimeLibrary.GetStats().LoadedArtifactCount);

        _ = ShaderArtifactRuntimeLibrary.GetRequiredArtifact(
            "Tests/Lazy/First", "Main", GraphicsBackend.Vulkan, "");
        Assert.Equal(1, artifactOpens);
        _ = ShaderArtifactRuntimeLibrary.GetRequiredArtifact(
            "Tests/Lazy/Second", "Other", GraphicsBackend.Vulkan, "");
        Assert.Equal(2, artifactOpens);
        Assert.True(ShaderArtifactRuntimeLibrary.GetStats().LoadedBytecodeBytes > 0);
    }

    [Theory]
    [InlineData(AssetPackagingMode.LooseFiles)]
    [InlineData(AssetPackagingMode.XEnginePak)]
    [InlineData(AssetPackagingMode.AssetBundle)]
    public void NonEmbeddedModesLoadContentShaderDirectory(AssetPackagingMode packagingMode)
    {
        ShaderArtifactEntry entry = CreateEntry(GraphicsBackend.Vulkan, "Tests/Files", "Main", "");
        Dictionary<string, byte[]> package = CreatePackage(entry);
        string shaderDirectory = Path.Combine(_directory, "Content", "Shaders");
        Directory.CreateDirectory(shaderDirectory);
        foreach (KeyValuePair<string, byte[]> file in package)
            File.WriteAllBytes(Path.Combine(shaderDirectory, file.Key), file.Value);

        ShaderArtifactRuntimeLibrary.Configure(
            packagingMode,
            Path.Combine(_directory, "Content"));

        Assert.Contains(shaderDirectory, ShaderArtifactRuntimeLibrary.SourceDescription, StringComparison.Ordinal);
        Assert.Equal(1, ShaderArtifactRuntimeLibrary.GetStats().ArtifactCount);
    }

    [Fact]
    public void ZipStreamProviderLoadsAndroidShaderPackage()
    {
        ShaderArtifactEntry entry = CreateEntry(GraphicsBackend.Vulkan, "Tests/AndroidVfs", "Main", "");
        Dictionary<string, byte[]> package = CreatePackage(entry);
        using var archiveBytes = new MemoryStream();
        using (var archive = new ZipArchive(archiveBytes, ZipArchiveMode.Create, leaveOpen: true))
        {
            string[] names = [.. package.Keys];
            Array.Sort(names, StringComparer.Ordinal);
            for (int i = 0; i < names.Length; i++)
            {
                ZipArchiveEntry zipEntry = archive.CreateEntry(names[i], CompressionLevel.NoCompression);
                using Stream stream = zipEntry.Open();
                stream.Write(package[names[i]]);
            }
        }
        archiveBytes.Position = 0;
        using var provider = new ZipAssetStreamProvider(archiveBytes, "res/raw/android_vulkan.xshaderpack");

        ShaderArtifactRuntimeLibrary.Configure(provider);

        ShaderArtifact loaded = ShaderArtifactRuntimeLibrary.GetRequiredArtifact(
            "Tests/AndroidVfs",
            "Main",
            GraphicsBackend.Vulkan,
            "");
        Assert.Equal(ShaderBytecodeFormat.SpirV, loaded.Bytecode.Format);
        Assert.Contains("android_vulkan.xshaderpack", ShaderArtifactRuntimeLibrary.SourceDescription);
    }

    [Fact]
    public void EmbeddedResourceNamesMatchPlayerPackager()
    {
        Assert.Equal(
            "Shaders.catalog.xshadercatalog",
            ShaderArtifactRuntimeLibrary.GetEmbeddedResourceName("catalog.xshadercatalog"));
        Assert.Equal(
            "Shaders.ABC.xshader",
            ShaderArtifactRuntimeLibrary.GetEmbeddedResourceName("ABC.xshader"));
    }

    [Fact]
    public void MissingAndCorruptPackageInputsFailConfiguration()
    {
        FileNotFoundException missingCatalog = Assert.Throws<FileNotFoundException>(() =>
            ShaderArtifactRuntimeLibrary.ConfigureFromPackage(_ => null, "missing package"));
        Assert.Contains("catalog", missingCatalog.Message, StringComparison.OrdinalIgnoreCase);

        ShaderArtifactEntry entry = CreateEntry(GraphicsBackend.Vulkan, "Tests/Broken", "Main", "");
        Dictionary<string, byte[]> missingArtifact = CreatePackage(entry);
        missingArtifact.Remove(entry.Key.Value + ".xshader");
        Assert.Throws<FileNotFoundException>(() => Configure(missingArtifact, "missing artifact"));

        Dictionary<string, byte[]> corruptCatalog = CreatePackage(entry);
        corruptCatalog["catalog.xshadercatalog"][^1] ^= 0x7F;
        Assert.Throws<InvalidDataException>(() => Configure(corruptCatalog, "corrupt catalog"));

        Dictionary<string, byte[]> corruptArtifact = CreatePackage(entry);
        corruptArtifact[entry.Key.Value + ".xshader"][^1] ^= 0x7F;
        Assert.Throws<InvalidDataException>(() => Configure(corruptArtifact, "corrupt artifact"));
        Assert.False(ShaderArtifactRuntimeLibrary.IsConfigured);
    }

    [Fact]
    public void DuplicateLogicalIdentityFailsConfiguration()
    {
        ShaderArtifactEntry first = CreateEntry(
            GraphicsBackend.Vulkan,
            "Tests/Duplicate",
            "Main",
            "",
            sourceSuffix: "first");
        ShaderArtifactEntry second = CreateEntry(
            GraphicsBackend.Vulkan,
            "Tests/Duplicate",
            "Main",
            "",
            sourceSuffix: "second");

        InvalidDataException exception = Assert.Throws<InvalidDataException>(() =>
            Configure(CreatePackage(first, second), "duplicate package"));
        Assert.Contains("duplicate", exception.Message, StringComparison.OrdinalIgnoreCase);
        Assert.False(ShaderArtifactRuntimeLibrary.IsConfigured);
    }

    [Fact]
    public void ShaderPassUsesOfflineBytecodeWithoutCompilerFallback()
    {
        ShaderArtifactEntry entry = CreateEntry(GraphicsBackend.Vulkan, "Tests/OfflinePass", "Main", "");
        Configure(CreatePackage(entry), "offline pass package");
        Environment.SetEnvironmentVariable("XENGINE_SHADER_COMPILER", "must-not-be-read");
        ShaderPass pass = CreatePass("Tests/OfflinePass", "Main");

        CompiledShaderBytecode bytecode = pass.ResolveHlslBytecode(
            GraphicsBackend.Vulkan,
            null,
            "");

        Assert.Equal(ShaderBytecodeFormat.SpirV, bytecode.Format);
        Assert.Equal(entry.Artifact.Bytecode.VertexBytecode, bytecode.VertexBytecode);
        ShaderArtifactUnavailableException keywordException = Assert.Throws<ShaderArtifactUnavailableException>(() =>
            pass.ResolveHlslBytecode(
                GraphicsBackend.Vulkan,
                new Dictionary<string, bool> { ["FOG"] = true },
                "FOG;"));
        Assert.Contains("variant=FOG;", keywordException.Message, StringComparison.Ordinal);
        ShaderArtifactUnavailableException backendException = Assert.Throws<ShaderArtifactUnavailableException>(() =>
            pass.ResolveHlslBytecode(GraphicsBackend.Direct3D12, null, ""));
        Assert.Contains("variant=<default>", backendException.Message, StringComparison.Ordinal);

        ShaderArtifactRuntimeStats stats = ShaderArtifactRuntimeLibrary.GetStats();
        Assert.Equal(1, stats.LookupHits);
        Assert.Equal(2, stats.LookupMisses);
        _output.WriteLine($"artifact_count={stats.ArtifactCount}");
        _output.WriteLine($"offline_lookup_hits={stats.LookupHits}");
        _output.WriteLine($"offline_lookup_misses={stats.LookupMisses}");
        _output.WriteLine("runtime_compiler_invocations=0");
        _output.WriteLine("compiler_environment_read=false");
    }

    [Fact]
    public void DevelopmentTraceObservesStableLookupDescriptors()
    {
        ShaderArtifactEntry entry = CreateEntry(GraphicsBackend.Vulkan, "Tests/Trace", "Main", "FEATURE_A;");
        Configure(CreatePackage(entry), "trace package");
        var lookups = new List<ShaderArtifactRuntimeLookup>();
        ShaderArtifactRuntimeLibrary.SetUsageTraceSink(lookups.Add);

        ShaderArtifactRuntimeLibrary.GetRequiredArtifact(
            "Tests/Trace",
            "Main",
            GraphicsBackend.Vulkan,
            "FEATURE_A;");
        Assert.Throws<ShaderArtifactUnavailableException>(() =>
            ShaderArtifactRuntimeLibrary.GetRequiredArtifact(
                "Tests/Trace",
                "Main",
                GraphicsBackend.Vulkan,
                "FEATURE_B;"));

        Assert.Equal(
        [
            new ShaderArtifactRuntimeLookup("Tests/Trace", "Main", GraphicsBackend.Vulkan, "FEATURE_A;", true),
            new ShaderArtifactRuntimeLookup("Tests/Trace", "Main", GraphicsBackend.Vulkan, "FEATURE_B;", false),
        ],
        lookups);
    }

    [Fact]
    public void DevelopmentUsageTracePersistsUniqueJsonlDescriptors()
    {
        ShaderArtifactEntry entry = CreateEntry(GraphicsBackend.Vulkan, "Tests/PersistedTrace", "Main", "FEATURE_A;");
        Configure(CreatePackage(entry), "persisted trace package");
        string tracePath = Path.Combine(_directory, "Diagnostics", ShaderArtifactUsageTrace.FileName);

        using (ShaderArtifactRuntimeLibrary.EnableDevelopmentUsageTrace(tracePath))
        {
            _ = ShaderArtifactRuntimeLibrary.GetRequiredArtifact(
                "Tests/PersistedTrace",
                "Main",
                GraphicsBackend.Vulkan,
                "FEATURE_A;");
            _ = ShaderArtifactRuntimeLibrary.GetRequiredArtifact(
                "Tests/PersistedTrace",
                "Main",
                GraphicsBackend.Vulkan,
                "FEATURE_A;");
            _ = Assert.Throws<ShaderArtifactUnavailableException>(() =>
                ShaderArtifactRuntimeLibrary.GetRequiredArtifact(
                    "Tests/PersistedTrace",
                    "Main",
                    GraphicsBackend.Vulkan,
                    "FEATURE_B;"));
        }

        string[] lines = File.ReadAllLines(tracePath);
        Assert.Equal(2, lines.Length);
        using JsonDocument hit = JsonDocument.Parse(lines[0]);
        Assert.Equal(ShaderArtifactUsageTrace.SchemaVersion, hit.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("Tests/PersistedTrace", hit.RootElement.GetProperty("shaderName").GetString());
        Assert.Equal("Main", hit.RootElement.GetProperty("passName").GetString());
        Assert.Equal("Vulkan", hit.RootElement.GetProperty("backend").GetString());
        Assert.Equal("FEATURE_A;", hit.RootElement.GetProperty("variantKey").GetString());
        Assert.True(hit.RootElement.GetProperty("found").GetBoolean());
        using JsonDocument miss = JsonDocument.Parse(lines[1]);
        Assert.Equal("FEATURE_B;", miss.RootElement.GetProperty("variantKey").GetString());
        Assert.False(miss.RootElement.GetProperty("found").GetBoolean());

        using (ShaderArtifactRuntimeLibrary.EnableDevelopmentUsageTrace(tracePath))
        {
            _ = ShaderArtifactRuntimeLibrary.GetRequiredArtifact(
                "Tests/PersistedTrace",
                "Main",
                GraphicsBackend.Vulkan,
                "FEATURE_A;");
        }

        Assert.Equal(2, File.ReadAllLines(tracePath).Length);
    }

    [Fact]
    public void WarmArtifactLookupHasZeroSteadyStateAllocation()
    {
        ShaderArtifactEntry entry = CreateEntry(GraphicsBackend.Vulkan, "Tests/WarmLookup", "Main", "");
        Configure(CreatePackage(entry), "warm lookup package");
        ShaderArtifact warm = ShaderArtifactRuntimeLibrary.GetRequiredArtifact(
            "Tests/WarmLookup",
            "Main",
            GraphicsBackend.Vulkan,
            "");

        ShaderArtifact last = warm;
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 256; i++)
        {
            last = ShaderArtifactRuntimeLibrary.GetRequiredArtifact(
                "Tests/WarmLookup",
                "Main",
                GraphicsBackend.Vulkan,
                "");
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Same(warm, last);
        Assert.Equal(0, allocated);
    }

    private static ShaderPass CreatePass(string shaderName, string passName)
    {
        var pass = new ShaderPass(
            passName,
            null,
            null,
            new RasterizerState(),
            "",
            "",
            "",
            hlslVertexSource: "float4 main(float3 p : POSITION) : SV_Position { return float4(p, 1); }",
            hlslFragmentSource: "float4 main() : SV_Target { return 1; }");
        pass.SetShaderName(shaderName);
        return pass;
    }

    private static ShaderArtifactEntry CreateEntry(
        GraphicsBackend backend,
        string shaderName,
        string passName,
        string variantKey,
        string sourceSuffix = "default")
    {
        var request = new ShaderCompileRequest
        {
            TargetBackend = backend,
            Language = ShaderLanguage.Hlsl,
            VertexSource = $"float4 main(float3 p : POSITION) : SV_Position {{ return float4(p, 1); }} // {sourceSuffix}",
            FragmentSource = "float4 main() : SV_Target { return 1; }",
            ShaderName = shaderName,
            PassName = passName,
            VariantKey = variantKey,
        };
        string targetProfile = RuntimeSlangShaderCompiler.GetTargetProfile(backend);
        ShaderArtifactKey key = ShaderArtifactKey.Create(request, "Slang 2026.12", targetProfile);
        ShaderBytecodeFormat format = ShaderBytecodeFormats.GetExpected(backend);
        var bytecode = new CompiledShaderBytecode(
            ShaderLanguage.Hlsl,
            format,
            [1, 2, 3, (byte)backend],
            [4, 5, 6, (byte)backend]);
        var artifact = new ShaderArtifact(
            backend,
            ShaderCompilerKind.Slang,
            "Slang 2026.12",
            ShaderArtifactKey.ComputeSourceHash(request),
            targetProfile,
            ShaderArtifact.DefaultMatrixLayout,
            shaderName,
            passName,
            variantKey,
            "main",
            "main",
            [],
            "sm9-runtime-test",
            bytecode);
        return new ShaderArtifactEntry(key, artifact);
    }

    private static Dictionary<string, byte[]> CreatePackage(params ShaderArtifactEntry[] entries) =>
        CreatePackage(false, entries);

    private static Dictionary<string, byte[]> CreateStablePackage(params ShaderArtifactEntry[] entries) =>
        CreatePackage(true, entries);

    private static Dictionary<string, byte[]> CreatePackage(
        bool stableCatalog,
        params ShaderArtifactEntry[] entries)
    {
        var keys = new ShaderArtifactKey[entries.Length];
        var package = new Dictionary<string, byte[]>(StringComparer.Ordinal);
        for (int i = 0; i < entries.Length; i++)
        {
            keys[i] = entries[i].Key;
            using var artifactStream = new MemoryStream();
            ShaderArtifactCodec.Write(artifactStream, entries[i].Key, entries[i].Artifact);
            package.Add(entries[i].Key.Value + ".xshader", artifactStream.ToArray());
        }

        using var catalogStream = new MemoryStream();
        Guid assetGuid = Guid.Parse("9d6124d1-ea69-491e-b1c4-b87eaa746c88");
        if (stableCatalog)
        {
            var stableEntries = new ShaderArtifactCatalogEntry[entries.Length];
            for (int i = 0; i < entries.Length; i++)
            {
                ShaderArtifact artifact = entries[i].Artifact;
                stableEntries[i] = new ShaderArtifactCatalogEntry(
                    assetGuid,
                    artifact.ShaderName,
                    artifact.PassName,
                    artifact.Backend,
                    artifact.TargetProfile,
                    artifact.VariantKey,
                    entries[i].Key);
            }
            new ShaderArtifactCatalog(stableEntries).Write(catalogStream);
        }
        else
        {
            new ShaderArtifactCatalog(new Dictionary<Guid, IReadOnlyList<ShaderArtifactKey>>
            {
                [assetGuid] = keys,
            }).Write(catalogStream);
        }
        package.Add("catalog.xshadercatalog", catalogStream.ToArray());
        return package;
    }

    private static void Configure(Dictionary<string, byte[]> package, string sourceDescription) =>
        ShaderArtifactRuntimeLibrary.ConfigureFromPackage(
            fileName => package.TryGetValue(fileName, out byte[]? bytes)
                ? new MemoryStream(bytes, writable: false)
                : null,
            sourceDescription);

    private readonly record struct ShaderArtifactEntry(
        ShaderArtifactKey Key,
        ShaderArtifact Artifact);
}
