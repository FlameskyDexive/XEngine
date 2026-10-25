// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

namespace XEngine.Runtime.RHI.Shaders;

public sealed class ShaderArtifact
{
    public const int CurrentSchemaVersion = 2;
    public const string DefaultMatrixLayout = "column-major";

    public GraphicsBackend Backend { get; }
    public ShaderCompilerKind CompilerKind { get; }
    public string CompilerIdentity { get; }
    public string SourceHash { get; }
    public string TargetProfile { get; }
    public string MatrixLayout { get; }
    public string ShaderName { get; }
    public string PassName { get; }
    public string VariantKey { get; }
    public string VertexEntryPoint { get; }
    public string FragmentEntryPoint { get; }
    public IReadOnlyList<string> Defines { get; }
    public string Diagnostics { get; }
    public CompiledShaderBytecode Bytecode { get; }

    public ShaderArtifact(
        GraphicsBackend backend,
        ShaderCompilerKind compilerKind,
        string compilerIdentity,
        string sourceHash,
        string targetProfile,
        string matrixLayout,
        string shaderName,
        string passName,
        string variantKey,
        string vertexEntryPoint,
        string fragmentEntryPoint,
        IReadOnlyList<string>? defines,
        string? diagnostics,
        CompiledShaderBytecode bytecode)
    {
        if (!ShaderBytecodeFormats.IsArtifactBackend(backend))
            throw new ArgumentOutOfRangeException(nameof(backend), backend, "Shader artifacts require a modern backend.");
        if (compilerKind == ShaderCompilerKind.Unknown)
            throw new ArgumentOutOfRangeException(nameof(compilerKind), compilerKind, "Compiler identity is required.");

        Backend = backend;
        CompilerKind = compilerKind;
        ArgumentException.ThrowIfNullOrWhiteSpace(compilerIdentity);
        CompilerIdentity = compilerIdentity;
        SourceHash = ShaderArtifactKey.Parse(sourceHash).Value
            ?? throw new InvalidOperationException("Parsed shader source hashes must be valid.");
        ArgumentException.ThrowIfNullOrWhiteSpace(targetProfile);
        TargetProfile = targetProfile;
        ArgumentException.ThrowIfNullOrWhiteSpace(matrixLayout);
        MatrixLayout = matrixLayout;
        ShaderName = shaderName ?? throw new ArgumentNullException(nameof(shaderName));
        PassName = passName ?? throw new ArgumentNullException(nameof(passName));
        VariantKey = variantKey ?? throw new ArgumentNullException(nameof(variantKey));
        ArgumentException.ThrowIfNullOrWhiteSpace(vertexEntryPoint);
        VertexEntryPoint = vertexEntryPoint;
        ArgumentException.ThrowIfNullOrWhiteSpace(fragmentEntryPoint);
        FragmentEntryPoint = fragmentEntryPoint;
        Defines = CopySortedDefines(defines);
        Diagnostics = diagnostics ?? string.Empty;
        Bytecode = bytecode ?? throw new ArgumentNullException(nameof(bytecode));

        ShaderBytecodeFormat expected = ShaderBytecodeFormats.GetExpected(backend);
        if (bytecode.Format != expected)
            throw new ArgumentException($"{backend} artifacts require {expected} bytecode.", nameof(bytecode));
    }

    private static IReadOnlyList<string> CopySortedDefines(IReadOnlyList<string>? defines)
    {
        if (defines == null || defines.Count == 0)
            return [];

        var sorted = new string[defines.Count];
        for (int i = 0; i < defines.Count; i++)
            sorted[i] = defines[i] ?? throw new ArgumentException("Shader artifact defines cannot contain null.", nameof(defines));
        Array.Sort(sorted, StringComparer.Ordinal);
        return Array.AsReadOnly(sorted);
    }
}
