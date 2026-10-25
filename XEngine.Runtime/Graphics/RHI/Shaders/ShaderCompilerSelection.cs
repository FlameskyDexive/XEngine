// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

namespace XEngine.Runtime.RHI.Shaders;

public enum ShaderCompilerMode
{
    Auto = 0,
    Dxc = 1,
    Slang = 2,
}

public enum ShaderCompilerKind
{
    Unknown = 0,
    Glsl = 1,
    Dxc = 2,
    Slang = 3,
}

public readonly record struct ShaderCompilerTelemetry(
    long SlangCompileAttempts,
    long DxcCompileAttempts,
    long SuccessfulRequests,
    long FailedRequests,
    long FallbackCount);
