// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;

using XEngine.Runtime.RHI;
using XEngine.Runtime.RHI.Shaders;

using Xunit;
using Xunit.Abstractions;

namespace XEngine.Runtime.Test;

public sealed class ShaderArtifactStoreTests : IDisposable
{
    private readonly ITestOutputHelper _output;
    private readonly string _directory = Path.Combine(
        Path.GetTempPath(),
        "XEngine-ShaderArtifactTests",
        Guid.NewGuid().ToString("N"));

    public ShaderArtifactStoreTests(ITestOutputHelper output) => _output = output;

    public void Dispose()
    {
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Fact]
    public void ColdMissWriteWarmHitAndDiskReloadPreserveArtifact()
    {
        ShaderCompileRequest request = CreateRequest();
        ShaderArtifactKey key = ShaderArtifactKey.Create(
            request,
            "Slang 2026.12",
            "sm_6_0",
            new Dictionary<string, string> { ["Common.hlsl"] = "include-v1" });
        ShaderArtifact artifact = CreateArtifact();
        var store = new ShaderArtifactStore(_directory, memoryCapacity: 2);

        Assert.False(store.TryGet(key, out _));
        store.Put(key, artifact);
        Assert.True(store.TryGet(key, out ShaderArtifact warm));
        Assert.Same(artifact, warm);
        Assert.Same(artifact.Bytecode.VertexBytecode, warm.Bytecode.VertexBytecode);

        var reloadedStore = new ShaderArtifactStore(_directory);
        Assert.True(reloadedStore.TryGet(key, out ShaderArtifact reloaded));
        AssertArtifact(reloaded);
        Assert.True(reloadedStore.TryGet(key, out ShaderArtifact secondWarm));
        Assert.Same(reloaded, secondWarm);
        Assert.Same(reloaded.Bytecode.FragmentBytecode, secondWarm.Bytecode.FragmentBytecode);

        ShaderArtifactStoreStats stats = reloadedStore.GetStats();
        Assert.Equal(1, stats.DiskHits);
        Assert.Equal(1, stats.MemoryHits);
        Assert.Equal(0, stats.Misses);
    }

    [Fact]
    public void Direct3D12ArtifactRoundTripPreservesDxilMetadata()
    {
        ShaderCompileRequest request = CreateRequest(targetBackend: GraphicsBackend.Direct3D12);
        ShaderArtifactKey key = ShaderArtifactKey.Create(request, "Slang 2026.12", "sm_6_0");
        var store = new ShaderArtifactStore(_directory);
        store.Put(key, CreateArtifact(backend: GraphicsBackend.Direct3D12));
        store.ClearMemory();

        Assert.True(store.TryGet(key, out ShaderArtifact artifact));
        Assert.Equal(GraphicsBackend.Direct3D12, artifact.Backend);
        Assert.Equal(ShaderBytecodeFormat.Dxil, artifact.Bytecode.Format);
        Assert.Equal("sm_6_0", artifact.TargetProfile);
        Assert.Equal(ShaderArtifact.DefaultMatrixLayout, artifact.MatrixLayout);
        Assert.Equal("validation-clean", artifact.Diagnostics);
    }

    [Fact]
    public void MetalArtifactRoundTripPreservesMetalLibMetadata()
    {
        ShaderCompileRequest request = CreateRequest(targetBackend: GraphicsBackend.Metal);
        ShaderArtifactKey key = ShaderArtifactKey.Create(request, "Slang 2026.12", "metal");
        var store = new ShaderArtifactStore(_directory);
        store.Put(key, CreateArtifact(backend: GraphicsBackend.Metal));
        store.ClearMemory();

        Assert.True(store.TryGet(key, out ShaderArtifact artifact));
        Assert.Equal(GraphicsBackend.Metal, artifact.Backend);
        Assert.Equal(ShaderBytecodeFormat.MetalLib, artifact.Bytecode.Format);
        Assert.Equal("metal", artifact.TargetProfile);
        Assert.Equal(ShaderArtifact.DefaultMatrixLayout, artifact.MatrixLayout);
        Assert.Equal("validation-clean", artifact.Diagnostics);
    }

    [Fact]
    public void KeyChangesForIncludeCompilerProfileAndVariantInputs()
    {
        ShaderCompileRequest request = CreateRequest();
        ShaderArtifactKey baseline = ShaderArtifactKey.Create(
            request,
            "Slang 2026.12",
            "sm_6_0",
            new Dictionary<string, string> { ["Common.hlsl"] = "include-v1" });

        Assert.NotEqual(baseline, ShaderArtifactKey.Create(
            request,
            "Slang 2026.12",
            "sm_6_0",
            new Dictionary<string, string> { ["Common.hlsl"] = "include-v2" }));
        Assert.NotEqual(baseline, ShaderArtifactKey.Create(
            request,
            "Slang 2026.13",
            "sm_6_0",
            new Dictionary<string, string> { ["Common.hlsl"] = "include-v1" }));
        Assert.NotEqual(baseline, ShaderArtifactKey.Create(
            request,
            "Slang 2026.12",
            "sm_6_6",
            new Dictionary<string, string> { ["Common.hlsl"] = "include-v1" }));

        ShaderCompileRequest variant = CreateRequest("FOG_HIGH;GPU_INSTANCING;");
        Assert.NotEqual(baseline, ShaderArtifactKey.Create(
            variant,
            "Slang 2026.12",
            "sm_6_0",
            new Dictionary<string, string> { ["Common.hlsl"] = "include-v1" }));
    }

    [Fact]
    public void DependencyOrderingDoesNotChangeContentAddress()
    {
        ShaderCompileRequest request = CreateRequest();
        var first = new Dictionary<string, string>
        {
            ["Lighting.hlsl"] = "lighting",
            ["Common.hlsl"] = "common",
        };
        var second = new Dictionary<string, string>
        {
            ["Common.hlsl"] = "common",
            ["Lighting.hlsl"] = "lighting",
        };

        Assert.Equal(
            ShaderArtifactKey.Create(request, "Slang 2026.12", "sm_6_0", first),
            ShaderArtifactKey.Create(request, "Slang 2026.12", "sm_6_0", second));
    }

    [Fact]
    public void DefineOrderingAndDependencySeparatorsDoNotChangeContentAddress()
    {
        ShaderCompileRequest firstRequest = CreateRequest(extraDefines: ["USE_FOG", "XENGINE_VULKAN"]);
        ShaderCompileRequest secondRequest = CreateRequest(extraDefines: ["XENGINE_VULKAN", "USE_FOG"]);

        ShaderArtifactKey first = ShaderArtifactKey.Create(
            firstRequest,
            "Slang 2026.12",
            "sm_6_0",
            new Dictionary<string, string> { ["Includes\\Common.hlsl"] = "common" });
        ShaderArtifactKey second = ShaderArtifactKey.Create(
            secondRequest,
            "Slang 2026.12",
            "sm_6_0",
            new Dictionary<string, string> { ["Includes/Common.hlsl"] = "common" });

        Assert.Equal(first, second);
    }

    [Fact]
    public void SourceLineEndingsAreNormalizedAndMatrixLayoutInvalidatesKey()
    {
        ShaderCompileRequest windowsSource = CreateRequest(vertexSource: "line1\r\nline2\rline3");
        ShaderCompileRequest unixSource = CreateRequest(vertexSource: "line1\nline2\nline3");

        Assert.Equal(
            ShaderArtifactKey.ComputeSourceHash(windowsSource),
            ShaderArtifactKey.ComputeSourceHash(unixSource));
        ShaderArtifactKey columnMajor = ShaderArtifactKey.Create(windowsSource, "Slang 2026.12", "spirv_1_5");
        ShaderArtifactKey normalizedColumnMajor = ShaderArtifactKey.Create(unixSource, "Slang 2026.12", "spirv_1_5");
        ShaderArtifactKey rowMajor = ShaderArtifactKey.Create(
            unixSource,
            "Slang 2026.12",
            "spirv_1_5",
            matrixLayout: "row-major");

        Assert.Equal(columnMajor, normalizedColumnMajor);
        Assert.NotEqual(columnMajor, rowMajor);
    }

    [Fact]
    public void DefaultKeyIsRejectedBeforeFileAccess()
    {
        var store = new ShaderArtifactStore(_directory);

        Assert.Throws<ArgumentException>(() => store.GetArtifactPath(default));
        Assert.Throws<ArgumentException>(() => store.TryGet(default, out _));
        Assert.Throws<ArgumentException>(() => store.Put(default, CreateArtifact()));
        Assert.False(Directory.Exists(_directory));
    }

    [Fact]
    public void CorruptArtifactIsDeletedAndReportedAsMiss()
    {
        ShaderArtifactKey key = ShaderArtifactKey.Create(CreateRequest(), "Slang 2026.12", "sm_6_0");
        var store = new ShaderArtifactStore(_directory);
        store.Put(key, CreateArtifact());
        store.ClearMemory();
        string path = store.GetArtifactPath(key);
        byte[] bytes = File.ReadAllBytes(path);
        bytes[^1] ^= 0x7F;
        File.WriteAllBytes(path, bytes);

        Assert.False(store.Contains(key));
        Assert.False(File.Exists(path));
        ShaderArtifactStoreStats stats = store.GetStats();
        Assert.Equal(1, stats.Corruptions);
        Assert.Equal(1, stats.Misses);
    }

    [Fact]
    public void ContainsValidatesDiskArtifactWithoutMaterializingBytecode()
    {
        ShaderArtifactKey key = ShaderArtifactKey.Create(CreateRequest(), "Slang 2026.12", "spirv_1_5");
        var store = new ShaderArtifactStore(_directory);
        store.Put(key, CreateArtifact());
        store.ClearMemory();

        Assert.True(store.Contains(key));
        Assert.True(store.TryGet(key, out ShaderArtifact artifact));
        AssertArtifact(artifact);

        string path = store.GetArtifactPath(key);
        byte[] bytes = File.ReadAllBytes(path);
        bytes[^1] ^= 0x7F;
        File.WriteAllBytes(path, bytes);
        store.ClearMemory();

        Assert.False(store.Contains(key));
        Assert.False(File.Exists(path));
        ShaderArtifactStoreStats stats = store.GetStats();
        Assert.Equal(2, stats.DiskHits);
        Assert.Equal(0, stats.MemoryHits);
        Assert.Equal(1, stats.Corruptions);
        Assert.Equal(1, stats.Misses);
    }

    [Fact]
    public void InvalidMetadataWithValidChecksumIsDeletedAndReportedAsMiss()
    {
        ShaderArtifactKey key = ShaderArtifactKey.Create(CreateRequest(), "Slang 2026.12", "spirv_1_5");
        var store = new ShaderArtifactStore(_directory);
        store.Put(key, CreateArtifact());
        store.ClearMemory();
        string path = store.GetArtifactPath(key);
        byte[] bytes = File.ReadAllBytes(path);

        const int payloadLengthOffset = sizeof(uint) + sizeof(int) + ShaderArtifactKey.ByteLength;
        const int payloadOffset = payloadLengthOffset + sizeof(int);
        int payloadLength = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(payloadLengthOffset, sizeof(int)));
        int metadataOffset = payloadOffset + sizeof(int) + sizeof(int);
        int compilerIdentityLength = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(metadataOffset, sizeof(int)));
        metadataOffset += sizeof(int) + compilerIdentityLength;
        int sourceHashLength = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(metadataOffset, sizeof(int)));
        Assert.Equal(ShaderArtifactKey.ByteLength * 2, sourceHashLength);
        metadataOffset += sizeof(int);
        bytes[metadataOffset] = (byte)'Z';

        byte[] digest = SHA256.HashData(bytes.AsSpan(payloadOffset, payloadLength));
        digest.CopyTo(bytes.AsSpan(payloadOffset + payloadLength, digest.Length));
        File.WriteAllBytes(path, bytes);

        Assert.False(store.TryGet(key, out _));
        Assert.False(File.Exists(path));
        ShaderArtifactStoreStats stats = store.GetStats();
        Assert.Equal(1, stats.Corruptions);
        Assert.Equal(1, stats.Misses);
    }

    [Fact]
    public void WritesUseFinalContentAddressWithoutLeavingTemporaryFiles()
    {
        ShaderArtifactKey key = ShaderArtifactKey.Create(CreateRequest(), "Slang 2026.12", "spirv_1_5");
        var store = new ShaderArtifactStore(_directory);

        store.Put(key, CreateArtifact());

        Assert.True(File.Exists(store.GetArtifactPath(key)));
        Assert.Empty(Directory.GetFiles(_directory, "*.tmp"));
        ShaderArtifactStoreStats stats = store.GetStats();
        Assert.Equal(1, stats.Writes);
        Assert.True(stats.BytesWritten > 0);
    }

    [Fact]
    public void MemoryCacheEvictsOldestArtifactAtCapacity()
    {
        ShaderArtifactKey firstKey = ShaderArtifactKey.Create(CreateRequest("FIRST;"), "Slang 2026.12", "spirv_1_5");
        ShaderArtifactKey secondKey = ShaderArtifactKey.Create(CreateRequest("SECOND;"), "Slang 2026.12", "spirv_1_5");
        var store = new ShaderArtifactStore(_directory, memoryCapacity: 1);

        store.Put(firstKey, CreateArtifact("FIRST;"));
        store.Put(secondKey, CreateArtifact("SECOND;"));

        Assert.True(store.TryGet(firstKey, out ShaderArtifact reloaded));
        Assert.Equal("FIRST;", reloaded.VariantKey);
        ShaderArtifactStoreStats stats = store.GetStats();
        Assert.Equal(1, stats.DiskHits);
        Assert.Equal(0, stats.MemoryHits);
    }

    [Fact]
    public void TransientReadFailureIsNotReportedAsCorruption()
    {
        ShaderArtifactKey key = ShaderArtifactKey.Create(CreateRequest(), "Slang 2026.12", "spirv_1_5");
        var store = new ShaderArtifactStore(_directory);
        store.Put(key, CreateArtifact());
        store.ClearMemory();
        string path = store.GetArtifactPath(key);

        using (new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None))
            Assert.Throws<IOException>(() => store.TryGet(key, out _));

        Assert.True(File.Exists(path));
        ShaderArtifactStoreStats stats = store.GetStats();
        Assert.Equal(0, stats.Corruptions);
        Assert.Equal(0, stats.Misses);
    }

    [Fact]
    public void AcceptanceMetricsCoverCompleteStoreLifecycle()
    {
        ShaderArtifactKey key = ShaderArtifactKey.Create(
            CreateRequest(),
            "Slang 2026.12",
            "spirv_1_5",
            new Dictionary<string, string> { ["Includes/Common.hlsl"] = "common-v1" });
        var store = new ShaderArtifactStore(_directory);

        Assert.False(store.TryGet(key, out _));
        store.Put(key, CreateArtifact());
        long artifactBytes = new FileInfo(store.GetArtifactPath(key)).Length;
        Assert.True(store.TryGet(key, out _));
        store.ClearMemory();
        Assert.True(store.TryGet(key, out _));
        store.ClearMemory();

        string path = store.GetArtifactPath(key);
        byte[] bytes = File.ReadAllBytes(path);
        bytes[^1] ^= 0x7F;
        File.WriteAllBytes(path, bytes);
        Assert.False(store.TryGet(key, out _));

        int temporaryFiles = Directory.GetFiles(_directory, "*.tmp").Length;
        ShaderArtifactStoreStats stats = store.GetStats();
        Assert.Equal(1, stats.MemoryHits);
        Assert.Equal(1, stats.DiskHits);
        Assert.Equal(2, stats.Misses);
        Assert.Equal(1, stats.Writes);
        Assert.Equal(1, stats.Corruptions);
        Assert.Equal(0, temporaryFiles);

        _output.WriteLine($"schema_version={ShaderArtifactCodec.SchemaVersion}");
        _output.WriteLine($"artifact_key={key}");
        _output.WriteLine($"artifact_bytes={artifactBytes}");
        _output.WriteLine($"cold_misses={stats.Misses}");
        _output.WriteLine($"disk_hits={stats.DiskHits}");
        _output.WriteLine($"memory_hits={stats.MemoryHits}");
        _output.WriteLine($"corruptions_recovered={stats.Corruptions}");
        _output.WriteLine($"temporary_files={temporaryFiles}");
    }

    private static ShaderCompileRequest CreateRequest(
        string variantKey = "FOG_HIGH;",
        IReadOnlyList<string>? extraDefines = null,
        string? vertexSource = null,
        GraphicsBackend targetBackend = GraphicsBackend.Vulkan) => new()
    {
        TargetBackend = targetBackend,
        Language = ShaderLanguage.Hlsl,
        VertexSource = vertexSource ?? "float4 main(float3 p : POSITION) : SV_Position { return float4(p, 1); }",
        FragmentSource = "float4 main() : SV_Target { return 1; }",
        ShaderName = "Default/VolumetricFog",
        PassName = "FogMarch",
        VariantKey = variantKey,
        Keywords = new Dictionary<string, bool>
        {
            ["GPU_INSTANCING"] = false,
            ["FOG_HIGH"] = true,
        },
        ExtraDefines = extraDefines ?? ["XENGINE_VULKAN"],
    };

    private static ShaderArtifact CreateArtifact(
        string variantKey = "FOG_HIGH;",
        GraphicsBackend backend = GraphicsBackend.Vulkan)
    {
        ShaderCompileRequest request = CreateRequest(variantKey, targetBackend: backend);
        var layout = new ShaderBindingLayout
        {
            Textures =
            [
                new ShaderBindingSlot(
                    ShaderBindingKind.Texture,
                    0,
                    "_MainTex",
                    stages: ShaderStageMask.Fragment,
                    resourceShape: ShaderResourceShape.Texture2D,
                    physicalBinding: 2,
                    physicalSpace: 0),
            ],
            Buffers =
            [
                new ShaderBindingSlot(
                    ShaderBindingKind.Buffer,
                    2,
                    "FogPS",
                    stages: ShaderStageMask.Vertex | ShaderStageMask.Fragment,
                    byteSize: 16,
                    uniformFields:
                    [
                        new ShaderUniformField(
                            "_Density",
                            0,
                            4,
                            ShaderScalarType.Float32,
                            1,
                            1,
                            1,
                            0,
                            0),
                    ],
                    physicalBindings:
                    [
                        new ShaderPhysicalBinding(0, 0, ShaderStageMask.Vertex),
                        new ShaderPhysicalBinding(1, 0, ShaderStageMask.Fragment),
                    ]),
            ],
        };
        var vertexInputs = new ShaderVertexInputLayout
        {
            Inputs =
            [
                new ShaderVertexInputSlot(
                    0,
                    3,
                    "POSITION",
                    0,
                    new ShaderVertexInputFormat(ShaderScalarType.Float32, 3)),
            ],
        };
        var bytecode = new CompiledShaderBytecode(
            ShaderLanguage.Hlsl,
            ShaderBytecodeFormats.GetExpected(backend),
            [3, 2, 35, 7],
            [7, 35, 2, 3],
            layout,
            vertexInputs);
        return new ShaderArtifact(
            backend,
            ShaderCompilerKind.Slang,
            "Slang 2026.12",
            ShaderArtifactKey.ComputeSourceHash(request),
            RuntimeSlangShaderCompiler.GetTargetProfile(backend),
            ShaderArtifact.DefaultMatrixLayout,
            "Default/VolumetricFog",
            "FogMarch",
            variantKey,
            "main",
            "main",
            ["XENGINE_VULKAN", "FOG_HIGH"],
            "validation-clean",
            bytecode);
    }

    private static void AssertArtifact(ShaderArtifact artifact)
    {
        Assert.Equal(GraphicsBackend.Vulkan, artifact.Backend);
        Assert.Equal(ShaderCompilerKind.Slang, artifact.CompilerKind);
        Assert.Equal("Slang 2026.12", artifact.CompilerIdentity);
        Assert.Equal(ShaderArtifactKey.ComputeSourceHash(CreateRequest()), artifact.SourceHash);
        Assert.Equal("spirv_1_5", artifact.TargetProfile);
        Assert.Equal("column-major", artifact.MatrixLayout);
        Assert.Equal("Default/VolumetricFog", artifact.ShaderName);
        Assert.Equal("FogMarch", artifact.PassName);
        Assert.Equal("FOG_HIGH;", artifact.VariantKey);
        Assert.Equal("main", artifact.VertexEntryPoint);
        Assert.Equal("main", artifact.FragmentEntryPoint);
        Assert.Collection(
            artifact.Defines,
            define => Assert.Equal("FOG_HIGH", define),
            define => Assert.Equal("XENGINE_VULKAN", define));
        Assert.Equal("validation-clean", artifact.Diagnostics);
        Assert.Equal([3, 2, 35, 7], artifact.Bytecode.VertexBytecode);
        Assert.Equal([7, 35, 2, 3], artifact.Bytecode.FragmentBytecode);

        ShaderBindingLayout layout = Assert.IsType<ShaderBindingLayout>(artifact.Bytecode.BindingLayout);
        ShaderBindingSlot texture = Assert.Single(layout.Textures);
        Assert.Equal("_MainTex", texture.Name);
        Assert.Equal(ShaderResourceShape.Texture2D, texture.ResourceShape);
        Assert.Equal(2, texture.PhysicalBinding);
        Assert.Equal(0, texture.PhysicalSpace);
        ShaderBindingSlot buffer = Assert.Single(layout.Buffers);
        Assert.Equal(16, buffer.ByteSize);
        Assert.Collection(
            buffer.PhysicalBindings,
            physical => Assert.Equal(new ShaderPhysicalBinding(0, 0, ShaderStageMask.Vertex), physical),
            physical => Assert.Equal(new ShaderPhysicalBinding(1, 0, ShaderStageMask.Fragment), physical));
        ShaderUniformField field = Assert.Single(buffer.UniformFields);
        Assert.Equal("_Density", field.Name);
        Assert.Equal(ShaderScalarType.Float32, field.ScalarType);

        ShaderVertexInputLayout vertexInputs = Assert.IsType<ShaderVertexInputLayout>(artifact.Bytecode.VertexInputLayout);
        ShaderVertexInputSlot input = Assert.Single(vertexInputs.Inputs);
        Assert.Equal("POSITION", input.SemanticName);
        Assert.Equal((uint)3, input.Location);
        Assert.Equal(new ShaderVertexInputFormat(ShaderScalarType.Float32, 3), input.Format);
    }
}
