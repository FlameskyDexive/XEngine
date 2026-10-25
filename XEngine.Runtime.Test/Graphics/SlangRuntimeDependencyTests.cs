// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.IO;
using System.Runtime.InteropServices;

using XEngine.Runtime.RHI;
using XEngine.Runtime.RHI.Shaders;
using XEngine.Slang;

using Xunit;
using Xunit.Abstractions;

namespace XEngine.Runtime.Test;

public sealed class SlangRuntimeDependencyTests(ITestOutputHelper output)
{
    [Fact]
    public void RuntimeDependencyLoadsNativeCompilerForCurrentRid()
    {
        string nativeFileName = GetNativeCompilerFileName();
        string nativePath = Path.Combine(AppContext.BaseDirectory, nativeFileName);
        string buildTag = GlobalSession.GetBuildTagString();

        bool spirvSupported = GlobalSession.CheckCompileTargetSupport(
            CompileTarget.Spirv,
            out bool spirvNotImplemented,
            out bool spirvNotFound);
        bool dxilSupported = GlobalSession.CheckCompileTargetSupport(
            CompileTarget.Dxil,
            out bool dxilNotImplemented,
            out bool dxilNotFound);

        output.WriteLine($"Slang version: {buildTag}");
        output.WriteLine($"RID: {RuntimeInformation.RuntimeIdentifier}");
        output.WriteLine($"Native path: {nativePath}");
        output.WriteLine($"SPIR-V support: {spirvSupported} (notImplemented={spirvNotImplemented}, notFound={spirvNotFound})");
        output.WriteLine($"DXIL support: {dxilSupported} (notImplemented={dxilNotImplemented}, notFound={dxilNotFound})");

        Assert.False(string.IsNullOrWhiteSpace(buildTag));
        Assert.True(File.Exists(nativePath), $"Slang native compiler was not copied to '{nativePath}'.");
        Assert.True(spirvSupported, "This Slang build must support SPIR-V generation.");
        // DXIL emission routes through the DXC downstream compiler, vendored for Windows only.
        if (OperatingSystem.IsWindows())
            Assert.True(dxilSupported, "This Slang build must support DXIL generation.");
    }

    [Fact]
    public void RuntimeDependencyCreatesRepeatedTargetSessions()
    {
        SessionDescription description = new()
        {
            Targets =
            [
                new TargetDescription { Format = CompileTarget.Spirv },
                new TargetDescription { Format = CompileTarget.Dxil },
            ],
        };

        using Session first = GlobalSession.CreateSession(description);
        using Session second = GlobalSession.CreateSession(description);

        Assert.NotNull(first);
        Assert.NotNull(second);
        Assert.NotSame(first, second);

        first.Dispose();
        second.Dispose();
    }

    [Fact]
    public void RuntimeCompilerIdentityMatchesNativeBuildAndCanonicalConfiguration()
    {
        Assert.Equal($"Slang {GlobalSession.GetBuildTagString()}; XEngine adapter 8", RuntimeSlangShaderCompiler.CompilerIdentity);
        Assert.Equal("spirv_1_5", RuntimeSlangShaderCompiler.GetTargetProfile(GraphicsBackend.Vulkan));
        Assert.Equal("sm_6_0", RuntimeSlangShaderCompiler.GetTargetProfile(GraphicsBackend.Direct3D12));
        Assert.Equal("metal", RuntimeSlangShaderCompiler.GetTargetProfile(GraphicsBackend.Metal));
        Assert.Contains(
            RuntimeSlangShaderCompiler.GetTargetProfile(GraphicsBackend.WebGL2),
            RuntimeSlangShaderCompiler.GlslEsProfileCandidates);
        Assert.Equal("column-major", RuntimeSlangShaderCompiler.MatrixLayout);
        Assert.Equal(MatrixLayoutMode.ColumnMajor, RuntimeSlangShaderCompiler.GetMatrixLayoutMode());
        Assert.Equal(RuntimeSlangShaderCompiler.MatrixLayout, ShaderArtifact.DefaultMatrixLayout);
    }

    private static string GetNativeCompilerFileName()
    {
        if (OperatingSystem.IsWindows())
            return "slang-compiler.dll";

        if (OperatingSystem.IsLinux())
            return "libslang-compiler.so";

        if (OperatingSystem.IsMacOS())
            return "libslang-compiler.dylib";

        throw new PlatformNotSupportedException(
            $"Slang runtime dependency is not configured for {RuntimeInformation.OSDescription}.");
    }
}
