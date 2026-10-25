// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;

namespace XEngine.Runtime.RHI.Shaders;

/// <summary>
/// Process-based DXC wrapper: compiles HLSL to SPIR-V (Vulkan) or DXIL (D3D12).
/// Locates <c>dxc.exe</c> on PATH, Windows Kits, or VulkanSDK. Missing DXC yields a
/// clear <see cref="ShaderCompileResult"/> error — never throws for headless / CI.
/// </summary>
public sealed class DxcShaderCompiler : IShaderCompiler
{
    private static readonly object s_locateLock = new();
    private static string? s_cachedDxcPath;
    private static bool s_locateAttempted;

    private readonly string? _dxcPathOverride;

    public DxcShaderCompiler(string? dxcPathOverride = null)
    {
        _dxcPathOverride = dxcPathOverride;
    }

    /// <summary>Resolved path to dxc.exe, or null if not found.</summary>
    public string? ResolvedDxcPath => _dxcPathOverride ?? LocateDxc();

    private static bool? s_cachedSpirvCapability;
    private static readonly object s_spirvCheckLock = new();

    /// <summary>
    /// Probes whether the resolved DXC can emit SPIR-V (needed for the Vulkan backend). Runs a
    /// trivial HLSL compile with -spirv and caches the result so callers only pay the process
    /// spawn once. Hosts call this before committing to Vulkan so a Windows-SDK-only machine (DXC
    /// without -DENABLE_SPIRV_CODEGEN) can fall back to D3D12 / OpenGL instead of crashing on the
    /// first shader.
    /// </summary>
    public static bool CanCompileSpirv()
    {
        lock (s_spirvCheckLock)
        {
            if (s_cachedSpirvCapability.HasValue)
                return s_cachedSpirvCapability.Value;

            s_cachedSpirvCapability = ProbeSpirvCapability();
            return s_cachedSpirvCapability.Value;
        }
    }

    /// <summary>Clears the cached SPIR-V capability (tests).</summary>
    internal static void ResetSpirvCache() =>
        s_cachedSpirvCapability = null;

    private static bool ProbeSpirvCapability()
    {
        // Prefer a Vulkan-SDK DXC; fall back to whatever LocateDxc found.
        string? dxc = LocateDxcPreferringSpirv() ?? LocateDxc();
        if (string.IsNullOrEmpty(dxc) || !File.Exists(dxc))
            return false;

        // Compile a trivial vertex shader to SPIR-V. If DXC lacks SPIR-V CodeGen the output file
        // won't exist and stderr will contain "SPIR-V CodeGen not available".
        string tempDir = Path.Combine(Path.GetTempPath(), "XEngineDxc", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            string src = Path.Combine(tempDir, "probe.hlsl");
            string dst = Path.Combine(tempDir, "probe.spv");
            File.WriteAllText(src, "float4 main(float4 pos : POSITION) : SV_Position { return pos; }", Encoding.UTF8);

            string? err = RunDxcSpirvProbe(dxc, src, dst);
            return err == null && File.Exists(dst);
        }
        catch
        {
            return false;
        }
        finally
        {
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, true); } catch { /* ignore */ }
        }
    }

    // Minimal DXC invocation for the SPIR-V probe (no descriptor-plan shifts — the trivial shader
    // declares no resources). Separated from RunDxc to avoid constructing a full binding layout.
    private static string? RunDxcSpirvProbe(string dxcPath, string inputPath, string outputPath)
    {
        string args = $"-T vs_6_0 -E main -Fo \"{outputPath}\" -spirv \"{inputPath}\"";
        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = dxcPath,
                Arguments = args,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };
            process.Start();
            var stderrTask = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(30_000))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* ignore */ }
                return "DXC timed out during SPIR-V probe.";
            }
            string stderr = stderrTask.GetAwaiter().GetResult();
            if (process.ExitCode != 0 || !File.Exists(outputPath))
                return string.IsNullOrEmpty(stderr) ? $"exit code {process.ExitCode}" : stderr.Trim();
            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    public ShaderCompileResult Compile(ShaderCompileRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        if (request.Language != ShaderLanguage.Hlsl)
        {
            return ShaderCompileResult.Fail(
                $"DxcShaderCompiler only accepts HLSL source (got {request.Language}).");
        }

        if (!string.IsNullOrWhiteSpace(request.ComputeSource) &&
            string.IsNullOrWhiteSpace(request.VertexSource) &&
            string.IsNullOrWhiteSpace(request.FragmentSource))
        {
            return ShaderCompileResult.Fail(
                "DxcShaderCompiler has no compute pipeline path; compute passes compile through Slang " +
                "(unset XENGINE_SHADER_COMPILER or use auto/slang).");
        }

        if (string.IsNullOrWhiteSpace(request.VertexSource))
            return ShaderCompileResult.Fail("Vertex source is null or empty.");
        if (string.IsNullOrWhiteSpace(request.FragmentSource))
            return ShaderCompileResult.Fail("Fragment source is null or empty.");

        string? dxc = ResolvedDxcPath;
        if (string.IsNullOrEmpty(dxc))
        {
            return ShaderCompileResult.Fail(
                "DXC (dxc.exe) was not found. Install the Windows SDK, VulkanSDK, or add dxc to PATH.");
        }

        bool spirv = request.TargetBackend == GraphicsBackend.Vulkan;
        // Prefer a SPIR-V-capable DXC (Vulkan SDK) when targeting Vulkan; Windows Kits DXC often
        // lacks -DENABLE_SPIRV_CODEGEN.
        if (spirv)
        {
            string? spirvDxc = LocateDxcPreferringSpirv();
            if (!string.IsNullOrEmpty(spirvDxc))
                dxc = spirvDxc;
        }

        ShaderBytecodeFormat format = spirv ? ShaderBytecodeFormat.SpirV : ShaderBytecodeFormat.Dxil;

        string vertSource = InjectDefines(request.VertexSource, request.Keywords, request.ExtraDefines);
        string fragSource = InjectDefines(request.FragmentSource, request.Keywords, request.ExtraDefines);

        string tempDir = Path.Combine(Path.GetTempPath(), "XEngineDxc", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);

        try
        {
            string vsIn = Path.Combine(tempDir, "vs.hlsl");
            string psIn = Path.Combine(tempDir, "ps.hlsl");
            string vsOut = Path.Combine(tempDir, spirv ? "vs.spv" : "vs.dxil");
            string psOut = Path.Combine(tempDir, spirv ? "ps.spv" : "ps.dxil");

            File.WriteAllText(vsIn, vertSource, Encoding.UTF8);
            File.WriteAllText(psIn, fragSource, Encoding.UTF8);
            string vsPreprocessed = Path.Combine(tempDir, "vs.preprocessed.hlsl");
            string psPreprocessed = Path.Combine(tempDir, "ps.preprocessed.hlsl");
            string? preprocessError = RunDxcPreprocess(dxc, vsIn, vsPreprocessed, "vs_6_0", spirv);
            if (preprocessError != null)
                return ShaderCompileResult.Fail($"DXC vertex preprocessing failed: {preprocessError}");
            preprocessError = RunDxcPreprocess(dxc, psIn, psPreprocessed, "ps_6_0", spirv);
            if (preprocessError != null)
                return ShaderCompileResult.Fail($"DXC fragment preprocessing failed: {preprocessError}");

            // Binding and vertex-layout discovery must see the same active branches as DXC.
            // Otherwise an inactive non-instanced COLOR input changes the instanced input ABI,
            // and resources behind disabled keywords consume descriptor slots that do not exist.
            vertSource = File.ReadAllText(vsPreprocessed);
            fragSource = File.ReadAllText(psPreprocessed);

            ShaderBindingLayout layout = ParseBindingLayout(vertSource, fragSource);
            ShaderVertexInputLayout vertexInputLayout = ParseVertexInputLayout(vertSource);
            ShaderDescriptorLayoutPlan descriptorPlan = ShaderDescriptorLayoutPlan.Create(layout);

            if (spirv)
            {
                vertSource = RemoveExplicitVulkanBindings(vertSource);
                vertSource = ApplyVulkanVertexInputLocations(vertSource, vertexInputLayout);
                fragSource = RemoveExplicitVulkanBindings(fragSource);
            }

            File.WriteAllText(vsIn, vertSource, Encoding.UTF8);
            File.WriteAllText(psIn, fragSource, Encoding.UTF8);

            string? vsErr = RunDxc(dxc, vsIn, vsOut, "vs_6_0", request.VertexEntryPoint, spirv, descriptorPlan);
            if (vsErr != null)
                return ShaderCompileResult.Fail($"DXC vertex compile failed: {vsErr}");

            string? psErr = RunDxc(dxc, psIn, psOut, "ps_6_0", request.FragmentEntryPoint, spirv, descriptorPlan);
            if (psErr != null)
                return ShaderCompileResult.Fail($"DXC fragment compile failed: {psErr}");

            byte[] vsBytes = File.ReadAllBytes(vsOut);
            byte[] psBytes = File.ReadAllBytes(psOut);

            return new ShaderCompileResult
            {
                Success = true,
                Format = format,
                VertexBytecode = vsBytes,
                FragmentBytecode = psBytes,
                BindingLayout = layout,
                VertexInputLayout = vertexInputLayout,
            };
        }
        catch (Exception ex)
        {
            return ShaderCompileResult.Fail($"DXC compile exception: {ex.Message}");
        }
        finally
        {
            try
            {
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, recursive: true);
            }
            catch
            {
                // Best-effort cleanup of temp compile artifacts.
            }
        }
    }

    /// <summary>
    /// Search PATH, Windows Kits, and VulkanSDK for <c>dxc.exe</c>. Result is cached.
    /// </summary>
    public static string? LocateDxc()
    {
        lock (s_locateLock)
        {
            if (s_locateAttempted)
                return s_cachedDxcPath;

            s_locateAttempted = true;
            s_cachedDxcPath = LocateDxcUncached();
            return s_cachedDxcPath;
        }
    }

    /// <summary>Clears the cached DXC path (tests).</summary>
    internal static void ResetLocateCache()
    {
        lock (s_locateLock)
        {
            s_locateAttempted = false;
            s_cachedDxcPath = null;
        }
    }

    private static string? LocateDxcUncached()
    {
        // Prefer the vendored DXC (always has SPIR-V support), then Vulkan SDK, then PATH, then
        // Windows Kits. The vendored binary ships under ThirdParty/DXC so users do not need to
        // install the Vulkan SDK to use the Vulkan backend (same approach as Unity's bundled DXC).
        string? fromVendored = FindVendoredDxc();
        if (fromVendored != null)
            return fromVendored;

        string? fromVulkan = FindInVulkanSdk();
        if (fromVulkan != null)
            return fromVulkan;

        string? fromPath = FindOnPath("dxc.exe") ?? FindOnPath("dxc");
        if (fromPath != null)
            return fromPath;

        return FindInWindowsKits();
    }

    private static string? LocateDxcPreferringSpirv()
    {
        // Vendored DXC always has SPIR-V; prefer it over a PATH/Windows Kits DXC that may not.
        string? fromVendored = FindVendoredDxc();
        if (fromVendored != null)
            return fromVendored;

        string? fromVulkan = FindInVulkanSdk();
        if (fromVulkan != null)
            return fromVulkan;

        // Already-cached path may be Vulkan SDK from LocateDxcUncached.
        string? cached = LocateDxc();
        return cached;
    }

    /// <summary>
    /// Finds the vendored SPIR-V-capable DXC binary under <c>ThirdParty/DXC</c>. The binary ships
    /// with the engine so users do not need the Vulkan SDK to use the Vulkan backend (matching how
    /// Unity bundles its own DXC). Walks from the assembly's base directory up to the repo root.
    /// </summary>
    private static string? FindVendoredDxc()
    {
        string? fromBaseDirectory = FindVendoredDxc(AppContext.BaseDirectory);
        if (fromBaseDirectory != null)
            return fromBaseDirectory;

        // Also check relative to the assembly itself (published single-directory apps).
        string? assemblyDir = Path.GetDirectoryName(typeof(DxcShaderCompiler).Assembly.Location);
        if (assemblyDir != null)
        {
            string fileName = OperatingSystem.IsWindows() ? "dxc.exe" : "dxc";
            string candidate = Path.Combine(assemblyDir, "dxc", fileName);
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    internal static string? FindVendoredDxc(string startDirectory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(startDirectory);

        // AppContext.BaseDirectory is the bin/.../net10.0 output folder. Walk up to find the
        // ThirdParty/DXC directory at the repo root, then select the architecture-appropriate binary.
        string arch = RuntimeInformation.ProcessArchitecture switch
        {
            Architecture.X64 => "x64",
            Architecture.Arm64 => "arm64",
            _ => "x64",
        };

        string os = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsLinux() ? "linux" : "macos";
        string fileName = OperatingSystem.IsWindows() ? "dxc.exe" : "dxc";

        string? baseDir = Path.TrimEndingDirectorySeparator(Path.GetFullPath(startDirectory));
        for (int depth = 0; depth < 8 && baseDir != null; depth++)
        {
            string candidate = Path.Combine(baseDir, "ThirdParty", "DXC", $"{os}-{arch}", "bin", arch, fileName);
            if (File.Exists(candidate))
                return candidate;
            // Fallback: some layouts put the binary directly under bin/
            candidate = Path.Combine(baseDir, "ThirdParty", "DXC", $"{os}-{arch}", "bin", fileName);
            if (File.Exists(candidate))
                return candidate;
            baseDir = Path.GetDirectoryName(baseDir);
        }

        return null;
    }

    private static string? FindInVulkanSdk()
    {
        string? vulkanSdk = Environment.GetEnvironmentVariable("VULKAN_SDK");
        if (string.IsNullOrEmpty(vulkanSdk))
            return null;

        string[] vulkanCandidates =
        [
            Path.Combine(vulkanSdk, "Bin", "dxc.exe"),
            Path.Combine(vulkanSdk, "bin", "dxc.exe"),
            Path.Combine(vulkanSdk, "Bin32", "dxc.exe"),
        ];
        foreach (string candidate in vulkanCandidates)
        {
            if (File.Exists(candidate))
                return candidate;
        }

        return null;
    }

    private static string? FindInWindowsKits()
    {
        string[] kitRoots =
        [
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86), "Windows Kits", "10", "bin"),
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Windows Kits", "10", "bin"),
        ];

        foreach (string kitRoot in kitRoots)
        {
            if (!Directory.Exists(kitRoot))
                continue;

            string? best = null;
            foreach (string versionDir in Directory.EnumerateDirectories(kitRoot))
            {
                string candidate = Path.Combine(versionDir, "x64", "dxc.exe");
                if (!File.Exists(candidate))
                    candidate = Path.Combine(versionDir, "x86", "dxc.exe");
                if (!File.Exists(candidate))
                    continue;

                if (best == null ||
                    string.Compare(versionDir, Path.GetDirectoryName(Path.GetDirectoryName(best)), StringComparison.OrdinalIgnoreCase) > 0)
                {
                    best = candidate;
                }
            }

            if (best != null)
                return best;
        }

        return null;
    }

    private static string? FindOnPath(string fileName)
    {
        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathEnv))
            return null;

        foreach (string dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries))
        {
            try
            {
                string candidate = Path.Combine(dir.Trim(), fileName);
                if (File.Exists(candidate))
                    return candidate;
            }
            catch
            {
                // Ignore malformed PATH entries.
            }
        }

        return null;
    }

    private static string InjectDefines(
        string source,
        IReadOnlyDictionary<string, bool>? keywords,
        IReadOnlyList<string>? extraDefines)
    {
        var sb = new StringBuilder(source.Length + 128);

        if (extraDefines != null)
        {
            for (int i = 0; i < extraDefines.Count; i++)
            {
                string define = extraDefines[i];
                if (!string.IsNullOrWhiteSpace(define))
                    sb.Append("#define ").Append(define).Append('\n');
            }
        }

        if (keywords != null)
        {
            foreach (KeyValuePair<string, bool> kvp in keywords)
            {
                if (kvp.Value)
                    sb.Append("#define ").Append(kvp.Key).Append('\n');
            }
        }

        sb.Append(source);
        return sb.ToString();
    }

    internal static string RemoveExplicitVulkanBindings(string source)
    {
        ArgumentNullException.ThrowIfNull(source);
        return s_vulkanBindingAttributeRegex.Replace(source, string.Empty);
    }

    internal static string ApplyVulkanResourceBindings(
        string source,
        ShaderBindingLayout layout,
        ShaderDescriptorLayoutPlan descriptorPlan)
    {
        ArgumentNullException.ThrowIfNull(source);
        ArgumentNullException.ThrowIfNull(layout);
        ArgumentNullException.ThrowIfNull(descriptorPlan);

        source = ExpandRegisterAliases(RemoveExplicitVulkanBindings(source));
        source = ApplyVulkanResourceBindings(source, s_cbufferRegex, layout.Buffers, descriptorPlan);
        source = ApplyVulkanResourceBindings(source, s_textureRegex, layout.Textures, descriptorPlan);
        source = ApplyVulkanResourceBindings(source, s_samplerRegex, layout.Samplers, descriptorPlan);
        return source;
    }

    private static string ApplyVulkanResourceBindings(
        string source,
        Regex declarationRegex,
        ShaderBindingSlot[] slots,
        ShaderDescriptorLayoutPlan descriptorPlan)
    {
        return declarationRegex.Replace(source, match =>
        {
            string name = match.Groups["name"].Value;
            for (int i = 0; i < slots.Length; i++)
            {
                ShaderBindingSlot slot = slots[i];
                if (!string.Equals(slot.Name, name, StringComparison.Ordinal))
                    continue;

                int physicalBinding = descriptorPlan.GetPhysicalBinding(slot);
                return $"[[vk::binding({physicalBinding}, {slot.Space})]] {match.Value}";
            }

            return match.Value;
        });
    }

    internal static ShaderVertexInputLayout ParseVertexInputLayout(string vertexSource)
    {
        ArgumentNullException.ThrowIfNull(vertexSource);
        Match inputStruct = s_vertexInputStructRegex.Match(vertexSource);
        if (!inputStruct.Success)
            return new ShaderVertexInputLayout();

        MatchCollection fields = s_vertexInputFieldRegex.Matches(inputStruct.Groups[1].Value);
        var occupiedLocations = new HashSet<uint>();
        for (int i = 0; i < fields.Count; i++)
        {
            Group explicitLocation = fields[i].Groups[1];
            if (explicitLocation.Success)
                occupiedLocations.Add(uint.Parse(explicitLocation.Value));
        }

        var inputs = new List<ShaderVertexInputSlot>(fields.Count);
        uint nextLocation = 0;
        for (int i = 0; i < fields.Count; i++)
        {
            Match field = fields[i];
            string semanticName = field.Groups[3].Value.ToUpperInvariant();
            uint semanticIndex = ParseSemanticIndex(field.Groups[4].Value);
            if (!ShaderReflectionUtilities.TryMapVertexSemantic(semanticName, semanticIndex, out uint semantic))
                continue;

            ShaderReflectionUtilities.TryParseHlslVertexFormat(
                field.Groups[2].Value,
                out ShaderVertexInputFormat format);

            uint location;
            if (field.Groups[1].Success)
            {
                location = uint.Parse(field.Groups[1].Value);
            }
            else
            {
                while (occupiedLocations.Contains(nextLocation))
                    nextLocation++;
                location = nextLocation++;
                occupiedLocations.Add(location);
            }

            inputs.Add(new ShaderVertexInputSlot(
                semantic,
                location,
                semanticName,
                semanticIndex,
                format));
        }

        return new ShaderVertexInputLayout { Inputs = inputs.ToArray() };
    }

    internal static string ApplyVulkanVertexInputLocations(
        string vertexSource,
        ShaderVertexInputLayout inputLayout)
    {
        ArgumentNullException.ThrowIfNull(vertexSource);
        ArgumentNullException.ThrowIfNull(inputLayout);
        return s_vertexInputStructRegex.Replace(vertexSource, match =>
        {
            string body = s_vulkanLocationAttributeRegex.Replace(match.Groups[1].Value, string.Empty);
            body = s_vertexInputDeclarationRegex.Replace(body, field =>
            {
                if (!TryMapVertexSemantic(field.Groups[1].Value, field.Groups[2].Value, out uint semantic) ||
                    !inputLayout.TryGetLocation(semantic, out uint location))
                {
                    return field.Value;
                }

                return $"[[vk::location({location})]] {field.Value}";
            });
            return match.Value.Replace(match.Groups[1].Value, body, StringComparison.Ordinal);
        });
    }

    private static bool TryMapVertexSemantic(string name, string indexText, out uint semantic)
    {
        return ShaderReflectionUtilities.TryMapVertexSemantic(
            name,
            ParseSemanticIndex(indexText),
            out semantic);
    }

    private static uint ParseSemanticIndex(string indexText) =>
        string.IsNullOrEmpty(indexText) ? 0u : uint.Parse(indexText);

    private static string? RunDxcPreprocess(string dxcPath, string inputPath, string outputPath, string profile, bool spirv)
    {
        using var process = new Process();
        process.StartInfo = new ProcessStartInfo
        {
            FileName = dxcPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        process.StartInfo.ArgumentList.Add("-T");
        process.StartInfo.ArgumentList.Add(profile);
        if (spirv)
        {
            process.StartInfo.ArgumentList.Add("-spirv");
            process.StartInfo.ArgumentList.Add("-fspv-target-env=vulkan1.2");
        }
        process.StartInfo.ArgumentList.Add("-P");
        process.StartInfo.ArgumentList.Add("-Fi");
        process.StartInfo.ArgumentList.Add(outputPath);
        process.StartInfo.ArgumentList.Add(inputPath);
        process.Start();
        var stdout = process.StandardOutput.ReadToEndAsync();
        var stderr = process.StandardError.ReadToEndAsync();
        if (!process.WaitForExit(30_000))
        {
            try { process.Kill(entireProcessTree: true); } catch { /* best effort */ }
            return "DXC timed out during preprocessing.";
        }
        string errors = stderr.GetAwaiter().GetResult();
        _ = stdout.GetAwaiter().GetResult();
        return process.ExitCode == 0 && File.Exists(outputPath)
            ? null : string.IsNullOrWhiteSpace(errors) ? $"exit code {process.ExitCode}" : errors.Trim();
    }

    private static string? RunDxc(
        string dxcPath,
        string inputPath,
        string outputPath,
        string profile,
        string entryPoint,
        bool spirv,
        ShaderDescriptorLayoutPlan descriptorPlan)
    {
        var args = new StringBuilder();
        args.Append("-T ").Append(profile);
        args.Append(" -E ").Append(entryPoint);
        args.Append(" -Fo \"").Append(outputPath).Append('"');
        if (spirv)
        {
            args.Append(" -spirv -fspv-target-env=vulkan1.2");
            args.Append(" -fvk-b-shift ").Append(descriptorPlan.BufferBindingBase).Append(" 0");
            args.Append(" -fvk-t-shift ").Append(descriptorPlan.TextureBindingBase).Append(" 0");
            args.Append(" -fvk-s-shift ").Append(descriptorPlan.SamplerBindingBase).Append(" 0");
        }
        args.Append(" \"").Append(inputPath).Append('"');

        try
        {
            using var process = new Process();
            process.StartInfo = new ProcessStartInfo
            {
                FileName = dxcPath,
                Arguments = args.ToString(),
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            process.Start();
            var stdoutTask = process.StandardOutput.ReadToEndAsync();
            var stderrTask = process.StandardError.ReadToEndAsync();
            if (!process.WaitForExit(120_000))
            {
                try { process.Kill(entireProcessTree: true); } catch { /* ignore */ }
                try { process.WaitForExit(); } catch { /* ignore */ }
                return "DXC timed out after 120s.";
            }

            string stdout = stdoutTask.GetAwaiter().GetResult();
            string stderr = stderrTask.GetAwaiter().GetResult();

            if (process.ExitCode != 0 || !File.Exists(outputPath))
            {
                string combined = (stderr + "\n" + stdout).Trim();
                return string.IsNullOrEmpty(combined)
                    ? $"exit code {process.ExitCode}"
                    : combined;
            }

            return null;
        }
        catch (Exception ex)
        {
            return ex.Message;
        }
    }

    private static readonly Regex s_vulkanBindingAttributeRegex = new(
        @"\[\[\s*vk::binding\s*\(\s*\d+\s*(?:,\s*\d+\s*)?\)\s*\]\]\s*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex s_vulkanLocationAttributeRegex = new(
        @"\[\[\s*vk::location\s*\(\s*\d+\s*\)\s*\]\]\s*",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex s_vertexInputStructRegex = new(
        @"\bstruct\s+VSInput\s*\{(.*?)\}\s*;",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Singleline);

    private static readonly Regex s_vertexInputFieldRegex = new(
        @"(?:\[\[\s*vk::location\s*\(\s*(\d+)\s*\)\s*\]\]\s*)?(?:\b(?:linear|centroid|nointerpolation|noperspective|sample)\s+)*([A-Za-z_]\w*)\s+\w+\s*:\s*([A-Za-z_]+?)(\d*)\s*;",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex s_vertexInputDeclarationRegex = new(
        @"\b\w+\s+\w+\s*:\s*([A-Za-z_]+?)(\d*)\s*;",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex s_textureRegex = new(
        @"\bTexture(?:1D|2D|3D|Cube)(?:Array)?(?:\s*<[^>]+>)?\s+(?<name>\w+)\s*(?:\[\s*(?<count>\d+)\s*\])?\s*(?::\s*register\s*\(\s*t(?<slot>\d+)\s*(?:,\s*space(?<space>\d+)\s*)?\))?\s*;",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex s_cbufferRegex = new(
        @"\bcbuffer\s+(?<name>\w+)\s*(?::\s*register\s*\(\s*b(?<slot>\d+)\s*(?:,\s*space(?<space>\d+)\s*)?\))?",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex s_samplerRegex = new(
        @"\bSampler(?:Comparison)?State\s+(?<name>\w+)\s*(?:\[\s*(?<count>\d+)\s*\])?\s*(?::\s*register\s*\(\s*s(?<slot>\d+)\s*(?:,\s*space(?<space>\d+)\s*)?\))?\s*;",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    private static readonly Regex s_registerAliasDefinitionRegex = new(
        @"^\s*#\s*define\s+(\w+)\s+([bts]\d+)\b",
        RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.Multiline);

    private static readonly Regex s_registerAliasUseRegex = new(
        @"\bregister\s*\(\s*(\w+)\s*\)",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    /// <summary>
    /// Best-effort reflection from HLSL source patterns (avoids fragile -dumpbin parsing).
    /// </summary>
    internal static ShaderBindingLayout ParseBindingLayout(string vertexSource, string fragmentSource)
    {
        ArgumentNullException.ThrowIfNull(vertexSource);
        ArgumentNullException.ThrowIfNull(fragmentSource);

        var textures = new Dictionary<string, ShaderBindingSlot>(StringComparer.Ordinal);
        var buffers = new Dictionary<string, ShaderBindingSlot>(StringComparer.Ordinal);
        var samplers = new Dictionary<string, ShaderBindingSlot>(StringComparer.Ordinal);
        int nextTextureSlot = 0;
        int nextBufferSlot = 0;
        int nextSamplerSlot = 0;

        ParseStage(vertexSource, ShaderStageMask.Vertex, textures, buffers, samplers, ref nextTextureSlot, ref nextBufferSlot, ref nextSamplerSlot);
        ParseStage(fragmentSource, ShaderStageMask.Fragment, textures, buffers, samplers, ref nextTextureSlot, ref nextBufferSlot, ref nextSamplerSlot);

        ShaderBindingSlot[] textureSlots = ToSortedArray(textures);
        ShaderBindingSlot[] bufferSlots = ToSortedArray(buffers);
        ShaderBindingSlot[] samplerSlots = ToSortedArray(samplers);
        ValidateUniqueSlots(textureSlots);
        ValidateUniqueSlots(bufferSlots);
        ValidateUniqueSlots(samplerSlots);

        return new ShaderBindingLayout
        {
            Textures = textureSlots,
            Buffers = bufferSlots,
            Samplers = samplerSlots,
        };
    }

    private static void ParseStage(
        string source,
        ShaderStageMask stage,
        Dictionary<string, ShaderBindingSlot> textures,
        Dictionary<string, ShaderBindingSlot> buffers,
        Dictionary<string, ShaderBindingSlot> samplers,
        ref int nextTextureSlot,
        ref int nextBufferSlot,
        ref int nextSamplerSlot)
    {
        source = ExpandRegisterAliases(source);
        ParseMatches(source, s_textureRegex, ShaderBindingKind.Texture, stage, textures, ref nextTextureSlot);
        ParseMatches(source, s_cbufferRegex, ShaderBindingKind.Buffer, stage, buffers, ref nextBufferSlot);
        ParseMatches(source, s_samplerRegex, ShaderBindingKind.Sampler, stage, samplers, ref nextSamplerSlot);
    }

    private static string ExpandRegisterAliases(string source)
    {
        Dictionary<string, string>? aliases = null;
        foreach (Match match in s_registerAliasDefinitionRegex.Matches(source))
        {
            aliases ??= new Dictionary<string, string>(StringComparer.Ordinal);
            aliases.TryAdd(match.Groups[1].Value, match.Groups[2].Value);
        }

        if (aliases is null)
            return source;

        return s_registerAliasUseRegex.Replace(source, match =>
            aliases.TryGetValue(match.Groups[1].Value, out string? register)
                ? $"register({register})"
                : match.Value);
    }

    private static void ParseMatches(
        string source,
        Regex regex,
        ShaderBindingKind kind,
        ShaderStageMask stage,
        Dictionary<string, ShaderBindingSlot> slots,
        ref int nextSlot)
    {
        foreach (Match match in regex.Matches(source))
        {
            string name = match.Groups["name"].Value;
            int count = match.Groups["count"].Success ? int.Parse(match.Groups["count"].Value) : 1;
            int slot = match.Groups["slot"].Success ? int.Parse(match.Groups["slot"].Value) : nextSlot;
            int space = match.Groups["space"].Success ? int.Parse(match.Groups["space"].Value) : 0;
            nextSlot = Math.Max(nextSlot, slot + count);

            if (!slots.TryGetValue(name, out ShaderBindingSlot existing))
            {
                slots.Add(name, new ShaderBindingSlot(kind, slot, name, space, count, stage));
            }
            else if (existing.Slot != slot || existing.Space != space || existing.Count != count)
            {
                throw new InvalidOperationException(
                    $"Shader binding '{name}' uses conflicting {kind} ranges " +
                    $"space{existing.Space}:{existing.Slot}[{existing.Count}] and space{space}:{slot}[{count}] across stages.");
            }
            else
            {
                slots[name] = existing.WithStages(existing.Stages | stage);
            }
        }
    }

    private static ShaderBindingSlot[] ToSortedArray(Dictionary<string, ShaderBindingSlot> slots)
    {
        ShaderBindingSlot[] result = new ShaderBindingSlot[slots.Count];
        int index = 0;
        foreach (ShaderBindingSlot slot in slots.Values)
            result[index++] = slot;

        Array.Sort(result, static (left, right) =>
        {
            int bySpace = left.Space.CompareTo(right.Space);
            if (bySpace != 0)
                return bySpace;
            int bySlot = left.Slot.CompareTo(right.Slot);
            if (bySlot != 0)
                return bySlot;
            int byKind = left.Kind.CompareTo(right.Kind);
            return byKind != 0 ? byKind : string.CompareOrdinal(left.Name, right.Name);
        });
        return result;
    }

    private static void ValidateUniqueSlots(ShaderBindingSlot[] slots)
    {
        for (int i = 1; i < slots.Length; i++)
        {
            ShaderBindingSlot previous = slots[i - 1];
            ShaderBindingSlot current = slots[i];
            if (previous.Space == current.Space
                && current.Slot < previous.Slot + previous.Count)
            {
                throw new InvalidOperationException(
                    $"Shader {current.Kind} range space{current.Space}:{current.Slot}[{current.Count}] is assigned to both " +
                    $"'{previous.Name}' and '{current.Name}' across stages.");
            }
        }
    }
}
