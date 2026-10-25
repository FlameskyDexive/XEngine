using System;
using System.IO;
using XEngine.Runtime.Rendering.Shaders;
using XEngine.Runtime.Resources;
using XEngine.Runtime.RHI;
using XEngine.Runtime.RHI.Shaders;
using Xunit;

namespace XEngine.Runtime.Test;

[Collection(ShaderCompilerEnvironmentCollection.Name)]
public sealed class ShaderEditorCompilerCacheTests : IDisposable
{
    private readonly string? _previousMode = Environment.GetEnvironmentVariable("XENGINE_SHADER_COMPILER");
    private readonly ShaderArtifactStore? _previousStore = ShaderPass.EditorArtifactStore;
    private readonly string _directory = Path.Combine(Path.GetTempPath(), "XEngine shader compiler cache", Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("XENGINE_SHADER_COMPILER", _previousMode);
        ShaderPass.EditorArtifactStore = _previousStore;
        if (Directory.Exists(_directory))
            Directory.Delete(_directory, recursive: true);
    }

    [Theory]
    [InlineData(GraphicsBackend.Vulkan)]
    [InlineData(GraphicsBackend.Direct3D12)]
    public void ForcedDxc_DoesNotConsumeOrOverwriteTheSlangCache(GraphicsBackend backend)
    {
        // Forced-DXC cases require the DXC toolchain, which is vendored for Windows only.
        if (!OperatingSystem.IsWindows())
            return;

        var store = new ShaderArtifactStore(_directory);
        ShaderPass.EditorArtifactStore = store;
        ShaderPass pass = Assert.Single(Shader.LoadDefault(DefaultShader.Blit).Passes);
        ShaderCompilerFactory.ResetTelemetryForTests();
        Environment.SetEnvironmentVariable("XENGINE_SHADER_COMPILER", "slang");
        CompiledShaderBytecode slang = pass.ResolveHlslBytecode(backend, null, "");
        Assert.Equal(1, store.GetStats().Writes);

        Environment.SetEnvironmentVariable("XENGINE_SHADER_COMPILER", "dxc");
        _ = pass.ResolveHlslBytecode(backend, null, "");
        _ = pass.ResolveHlslBytecode(backend, null, "");
        Assert.Equal(2, ShaderCompilerFactory.GetTelemetry().DxcCompileAttempts);
        Assert.Equal(1, store.GetStats().Writes);
        Assert.Equal(0, store.GetStats().MemoryHits);

        Environment.SetEnvironmentVariable("XENGINE_SHADER_COMPILER", "slang");
        Assert.Same(slang, pass.ResolveHlslBytecode(backend, null, ""));
        Assert.Equal(1, ShaderCompilerFactory.GetTelemetry().SlangCompileAttempts);
        Assert.Equal(1, store.GetStats().MemoryHits);
    }

    [Theory]
    [InlineData(GraphicsBackend.Vulkan)]
    [InlineData(GraphicsBackend.Direct3D12)]
    public void SlangCache_ReplacesLegacyDxcArtifactsStoredUnderASlangKey(GraphicsBackend backend)
    {
        // Compiles through DXC to produce the legacy artifact; DXC is Windows-only.
        if (!OperatingSystem.IsWindows())
            return;

        var store = new ShaderArtifactStore(_directory);
        ShaderPass.EditorArtifactStore = store;
        ShaderPass pass = Assert.Single(Shader.LoadDefault(DefaultShader.Blit).Passes);
        ShaderCompileRequest request = pass.CreateHlslCompileRequest(backend, null, "");
        string identity = RuntimeSlangShaderCompiler.CompilerIdentity;
        string profile = RuntimeSlangShaderCompiler.GetTargetProfile(backend);
        ShaderArtifactKey key = ShaderArtifactKey.Create(request, identity, profile, null, RuntimeSlangShaderCompiler.MatrixLayout);
        ShaderCompileResult dxc = new DxcShaderCompiler().Compile(request);
        Assert.True(dxc.Success, dxc.ErrorMessage);
        var bytecode = new CompiledShaderBytecode(ShaderLanguage.Hlsl, dxc.Format, dxc.VertexBytecode!, dxc.FragmentBytecode!,
            dxc.BindingLayout, dxc.VertexInputLayout);
        store.Put(key, new ShaderArtifact(backend, ShaderCompilerKind.Dxc, identity,
            ShaderArtifactKey.ComputeSourceHash(request), profile, RuntimeSlangShaderCompiler.MatrixLayout,
            request.ShaderName!, request.PassName!, "", request.VertexEntryPoint, request.FragmentEntryPoint, null, null, bytecode));
        store.ClearMemory();

        ShaderCompilerFactory.ResetTelemetryForTests();
        Environment.SetEnvironmentVariable("XENGINE_SHADER_COMPILER", "slang");
        CompiledShaderBytecode repaired = pass.ResolveHlslBytecode(backend, null, "");
        Assert.Equal(1, ShaderCompilerFactory.GetTelemetry().SlangCompileAttempts);
        Assert.Equal(0, ShaderCompilerFactory.GetTelemetry().DxcCompileAttempts);
        Assert.Equal(2, store.GetStats().Writes);
        Assert.True(store.TryGet(key, out ShaderArtifact replacement));
        Assert.Equal(ShaderCompilerKind.Slang, replacement.CompilerKind);
        Assert.Same(repaired, replacement.Bytecode);
        Assert.Same(repaired, pass.ResolveHlslBytecode(backend, null, ""));
        Assert.Equal(1, ShaderCompilerFactory.GetTelemetry().SlangCompileAttempts);
    }
}
