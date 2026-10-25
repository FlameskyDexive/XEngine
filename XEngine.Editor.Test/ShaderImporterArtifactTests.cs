// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Editor.Build;
using XEngine.Editor.Importers;
using XEngine.Runtime;
using XEngine.Runtime.Resources;
using XEngine.Runtime.RHI;
using XEngine.Runtime.RHI.Shaders;

using Xunit;
using Xunit.Abstractions;

namespace XEngine.Editor.Test;

public sealed class ShaderImporterArtifactTests(ITestOutputHelper output) : EditorTestHarness
{
    [Fact]
    public void ImportCreatesBothBackendArtifactsAndWarmReimportsDoNotRewrite()
    {
        (Guid includeGuid, Guid shaderGuid) = ImportShader("include-v1");
        ShaderArtifactStore store = ShaderImporter.GetArtifactStore(Project);

        ShaderArtifactStoreStats cold = store.GetStats();
        Assert.Equal(2, cold.Misses);
        Assert.Equal(2, cold.Writes);
        Assert.Equal(2, Directory.GetFiles(store.RootDirectory, "*.xshader").Length);
        Assert.Contains(includeGuid, Assets.GetEntry(shaderGuid)!.Dependencies);
        Assert.True(File.Exists(ShaderArtifactBuildPackager.GetImportIndexPath(Project, shaderGuid)));

        Assets.Reimport(shaderGuid);
        ShaderArtifactStoreStats memoryWarm = store.GetStats();
        Assert.Equal(2, memoryWarm.MemoryHits);
        Assert.Equal(2, memoryWarm.Writes);

        store.ClearMemory();
        Assets.Reimport(shaderGuid);
        ShaderArtifactStoreStats diskWarm = store.GetStats();
        Assert.Equal(2, diskWarm.DiskHits);
        Assert.Equal(2, diskWarm.Writes);

        bool foundVulkan = false;
        bool foundDirect3D12 = false;
        string[] artifactPaths = Directory.GetFiles(store.RootDirectory, "*.xshader");
        long artifactBytes = 0;
        for (int i = 0; i < artifactPaths.Length; i++)
        {
            string path = artifactPaths[i];
            ShaderArtifactKey key = ShaderArtifactKey.Parse(Path.GetFileNameWithoutExtension(path));
            using var stream = File.OpenRead(path);
            ShaderArtifact artifact = ShaderArtifactCodec.Read(stream, key);
            Assert.Equal(ShaderCompilerKind.Slang, artifact.CompilerKind);
            Assert.Equal(RuntimeSlangShaderCompiler.CompilerIdentity, artifact.CompilerIdentity);
            Assert.Equal(RuntimeSlangShaderCompiler.MatrixLayout, artifact.MatrixLayout);
            foundVulkan |= artifact.Backend == GraphicsBackend.Vulkan && artifact.Bytecode.Format == ShaderBytecodeFormat.SpirV;
            foundDirect3D12 |= artifact.Backend == GraphicsBackend.Direct3D12 && artifact.Bytecode.Format == ShaderBytecodeFormat.Dxil;
            artifactBytes += stream.Length;
        }
        Assert.True(foundVulkan);
        Assert.True(foundDirect3D12);

        output.WriteLine($"cold_misses={cold.Misses}");
        output.WriteLine($"writes={diskWarm.Writes}");
        output.WriteLine($"memory_hits={memoryWarm.MemoryHits}");
        output.WriteLine($"disk_hits={diskWarm.DiskHits}");
        output.WriteLine($"artifact_files={artifactPaths.Length}");
        output.WriteLine($"artifact_bytes={artifactBytes}");
    }

    [Fact]
    public void IncludeAssetChangeReimportsDependentShaderAndCreatesNewContentKeys()
    {
        (Guid includeGuid, Guid shaderGuid) = ImportShader("include-v1");
        ShaderArtifactStore store = ShaderImporter.GetArtifactStore(Project);
        Shader first = Assert.IsType<Shader>(AssetDatabase.Get(shaderGuid));

        File.WriteAllText(AssetAbsolutePath("Includes/Common.hlsl"), IncludeSource("include-v2"));
        Assert.Equal(includeGuid, Assets.ImportFile("Includes/Common.hlsl"));

        Shader second = Assert.IsType<Shader>(AssetDatabase.Get(shaderGuid));
        ShaderArtifactStoreStats stats = store.GetStats();
        Assert.NotSame(first, second);
        Assert.True(first.IsDisposed);
        Assert.Equal(4, stats.Misses);
        Assert.Equal(4, stats.Writes);
        Assert.Equal(4, Directory.GetFiles(store.RootDirectory, "*.xshader").Length);
        Assert.Contains(includeGuid, Assets.GetEntry(shaderGuid)!.Dependencies);
    }

    [Fact]
    public void IncludeChangeWhileEditorIsClosedInvalidatesPersistedDependency()
    {
        (Guid includeGuid, Guid shaderGuid) = ImportShader("include-v1");
        ShaderArtifactStore store = ShaderImporter.GetArtifactStore(Project);

        File.WriteAllText(AssetAbsolutePath("Includes/Common.hlsl"), IncludeSource("include-v2"));
        ReopenDatabase();

        ShaderArtifactStoreStats stats = store.GetStats();
        Assert.Equal(4, stats.Misses);
        Assert.Equal(4, stats.Writes);
        Assert.Equal(4, Directory.GetFiles(store.RootDirectory, "*.xshader").Length);
        Assert.Contains(includeGuid, Assets.GetEntry(shaderGuid)!.Dependencies);
        Assert.IsType<Shader>(AssetDatabase.Get(shaderGuid));
    }

    [Fact]
    public void CorruptArtifactIsRecoveredDuringReimport()
    {
        (_, Guid shaderGuid) = ImportShader("include-v1");
        ShaderArtifactStore store = ShaderImporter.GetArtifactStore(Project);
        string[] artifactPaths = Directory.GetFiles(store.RootDirectory, "*.xshader");
        Assert.Equal(2, artifactPaths.Length);

        byte[] bytes = File.ReadAllBytes(artifactPaths[0]);
        bytes[^1] ^= 0x7F;
        File.WriteAllBytes(artifactPaths[0], bytes);
        store.ClearMemory();

        Assets.Reimport(shaderGuid);

        ShaderArtifactStoreStats stats = store.GetStats();
        Assert.Equal(1, stats.Corruptions);
        Assert.Equal(3, stats.Misses);
        Assert.Equal(1, stats.DiskHits);
        Assert.Equal(3, stats.Writes);
        Assert.Equal(2, Directory.GetFiles(store.RootDirectory, "*.xshader").Length);
    }

    [Fact]
    public void DeclaredVariantsCompileForBothModernBackends()
    {
        string shaderPath = AssetAbsolutePath("Shaders/Variants.shader");
        Directory.CreateDirectory(Path.GetDirectoryName(shaderPath)!);
        File.WriteAllText(shaderPath, VariantShaderSource);
        Guid shaderGuid = Assets.ImportFile("Shaders/Variants.shader");
        Assert.NotEqual(Guid.Empty, shaderGuid);

        ShaderArtifactStore store = ShaderImporter.GetArtifactStore(Project);
        string[] artifactPaths = Directory.GetFiles(store.RootDirectory, "*.xshader");
        Assert.Equal(6, artifactPaths.Length);
        var variants = new Dictionary<string, int>(StringComparer.Ordinal);
        for (int i = 0; i < artifactPaths.Length; i++)
        {
            ShaderArtifactKey key = ShaderArtifactKey.Parse(Path.GetFileNameWithoutExtension(artifactPaths[i]));
            using var stream = File.OpenRead(artifactPaths[i]);
            ShaderArtifact artifact = ShaderArtifactCodec.Read(stream, key);
            Assert.Equal("Tests/OfflineVariants", artifact.ShaderName);
            variants[artifact.VariantKey] = variants.GetValueOrDefault(artifact.VariantKey) + 1;
        }

        Assert.Equal(2, variants[""]);
        Assert.Equal(2, variants["FEATURE_A;"]);
        Assert.Equal(2, variants["FEATURE_A;FEATURE_B;"]);

        string packagePath = Path.Combine(Project.RootPath, "VariantPackage");
        ShaderArtifactBuildPackage package = ShaderArtifactBuildPackager.Prepare(
            Project,
            [shaderGuid],
            packagePath,
            includeBuiltInShaders: false);
        try
        {
            ShaderArtifactRuntimeLibrary.ConfigureFromPackage(OpenPackageFile, package.DirectoryPath);
            ReopenDatabase();
            Shader restored = Assert.IsType<Shader>(AssetDatabase.Get(shaderGuid));
            using var material = new Material(restored);
            material.SetKeyword("IGNORED", true);
            material.SetKeyword("FEATURE_A", true);

            CompiledShaderBytecode bytecode = restored.GetPass(0).ResolveHlslBytecode(
                GraphicsBackend.Vulkan,
                material._localKeywords,
                material.KeywordKey);
            Assert.Equal(ShaderBytecodeFormat.SpirV, bytecode.Format);
            Assert.Equal("FEATURE_A;", material.KeywordKey);

            material.SetKeyword("FEATURE_A", false);
            material.SetKeyword("FEATURE_B", true);
            ShaderArtifactUnavailableException exception = Assert.Throws<ShaderArtifactUnavailableException>(() =>
                restored.GetPass(0).ResolveHlslBytecode(
                    GraphicsBackend.Vulkan,
                    material._localKeywords,
                    material.KeywordKey));
            Assert.Contains("variant=FEATURE_B;", exception.Message, StringComparison.Ordinal);

            output.WriteLine("declared_variants=3");
            output.WriteLine("modern_backends=2");
            output.WriteLine("offline_artifacts=6");
            output.WriteLine("irrelevant_keyword_filtered=true");
            output.WriteLine("undeclared_variant_rejected=true");
        }
        finally
        {
            ShaderArtifactRuntimeLibrary.ResetForTests();
        }

        Stream? OpenPackageFile(string fileName)
        {
            string path = Path.Combine(package.DirectoryPath, fileName);
            return File.Exists(path) ? File.OpenRead(path) : null;
        }
    }

    [Fact]
    public void VariantReimportCanonicalizesOrderAndReplacesImportIndex()
    {
        string shaderPath = AssetAbsolutePath("Shaders/Variants.shader");
        Directory.CreateDirectory(Path.GetDirectoryName(shaderPath)!);
        File.WriteAllText(shaderPath, VariantShaderSource);
        Guid shaderGuid = Assets.ImportFile("Shaders/Variants.shader");
        Assert.NotEqual(Guid.Empty, shaderGuid);

        ShaderArtifactStore store = ShaderImporter.GetArtifactStore(Project);
        ShaderArtifactStoreStats initial = store.GetStats();
        ShaderArtifactCatalog initialIndex = ReadImportIndex(shaderGuid);
        Assert.Equal(6, initialIndex.Entries[shaderGuid].Count);

        string reordered = VariantShaderSource
            .Replace("Variant(\"FEATURE_A\")", "Variant(\"TEMP\")", StringComparison.Ordinal)
            .Replace("Variant(\"FEATURE_B\", \"FEATURE_A\")", "Variant(\"FEATURE_A\")", StringComparison.Ordinal)
            .Replace("Variant(\"TEMP\")", "Variant(\"FEATURE_B\", \"FEATURE_A\")", StringComparison.Ordinal);
        File.WriteAllText(shaderPath, reordered);
        Assert.Equal(shaderGuid, Assets.ImportFile("Shaders/Variants.shader"));

        ShaderArtifactStoreStats canonical = store.GetStats();
        ShaderArtifactCatalog canonicalIndex = ReadImportIndex(shaderGuid);
        Assert.Equal(initialIndex.Entries[shaderGuid], canonicalIndex.Entries[shaderGuid]);
        Assert.Equal(initial.Writes, canonical.Writes);

        string reduced = reordered.Replace(
            "Variant(\"FEATURE_B\", \"FEATURE_A\")",
            "",
            StringComparison.Ordinal);
        File.WriteAllText(shaderPath, reduced);
        Assert.Equal(shaderGuid, Assets.ImportFile("Shaders/Variants.shader"));

        ShaderArtifactCatalog reducedIndex = ReadImportIndex(shaderGuid);
        Assert.Equal(4, reducedIndex.Entries[shaderGuid].Count);
    }

    private ShaderArtifactCatalog ReadImportIndex(Guid shaderGuid)
    {
        using var stream = File.OpenRead(ShaderArtifactBuildPackager.GetImportIndexPath(Project, shaderGuid));
        return ShaderArtifactCatalog.Read(stream);
    }

    private (Guid includeGuid, Guid shaderGuid) ImportShader(string includeVersion)
    {
        string includePath = AssetAbsolutePath("Includes/Common.hlsl");
        Directory.CreateDirectory(Path.GetDirectoryName(includePath)!);
        File.WriteAllText(includePath, IncludeSource(includeVersion));
        Guid includeGuid = Assets.ImportFile("Includes/Common.hlsl");
        Assert.NotEqual(Guid.Empty, includeGuid);

        string shaderPath = AssetAbsolutePath("Shaders/Offline.shader");
        Directory.CreateDirectory(Path.GetDirectoryName(shaderPath)!);
        File.WriteAllText(shaderPath, ShaderSource);
        Guid shaderGuid = Assets.ImportFile("Shaders/Offline.shader");
        Assert.NotEqual(Guid.Empty, shaderGuid);
        Assert.IsType<Shader>(AssetDatabase.Get(shaderGuid));
        return (includeGuid, shaderGuid);
    }

    private static string IncludeSource(string version) => $$"""
        static const float INCLUDED_VERSION = {{(version == "include-v1" ? "1.0" : "2.0")}};
        float4 IncludedColor() { return float4(INCLUDED_VERSION, 0.0, 0.0, 1.0); }
        """;

    private const string ShaderSource = """
        Shader "Tests/OfflineArtifact"

        Pass "Main"
        {
            HLSLPROGRAM
                Vertex
                {
                    float4 main(float3 position : POSITION) : SV_Position
                    {
                        return float4(position, 1.0);
                    }
                }

                Fragment
                {
                    #include "Includes/Common"

                    float4 main() : SV_Target
                    {
                        return IncludedColor();
                    }
                }
            ENDHLSL
        }
        """;

    private const string VariantShaderSource = """
        Shader "Tests/OfflineVariants"

        Variants
        {
            Variant("FEATURE_A")
            Variant("FEATURE_B", "FEATURE_A")
        }

        Pass "Main"
        {
            HLSLPROGRAM
                Vertex
                {
                    float4 main(float3 position : POSITION) : SV_Position
                    {
                        return float4(position, 1.0);
                    }
                }
                Fragment
                {
                    float4 main() : SV_Target
                    {
                        #ifdef FEATURE_B
                        return float4(0.0, 1.0, 0.0, 1.0);
                        #else
                        return float4(1.0, 0.0, 0.0, 1.0);
                        #endif
                    }
                }
            ENDHLSL
        }
        """;
}
