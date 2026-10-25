// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

using XEngine.Editor.Projects.Build;

namespace XEngine.Editor.Build.Web;

internal sealed class WebPlayerProjectOptions
{
    public string ProjectName { get; init; } = "XEngineGame";
    public string ApplicationId { get; init; } = "com.xengine.game";
    public string ApplicationLabel { get; init; } = "XEngine";
    public WebSubTarget SubTarget { get; init; } = WebSubTarget.Browser;
    public bool WasmEnableSIMD { get; init; } = true;
    public string CanvasSelector { get; init; } = "#canvas";
    public string RuntimeProjectPath { get; init; } = string.Empty;
}

internal sealed record WebPlayerProjectLayout(
    string RootDirectory,
    string ProjectPath,
    string OwnershipManifestPath,
    string AssemblyName,
    string RuntimeIdentifier,
    WebSubTarget SubTarget,
    bool WasmEnableSIMD);

/// <summary>
/// Creates a relocatable <c>net10.0</c> + <c>browser-wasm</c> player with Browser / WeChat / Douyin
/// canvas adapters. Publish still requires the <c>wasm-tools</c> workload.
/// </summary>
internal static class WebPlayerProjectGenerator
{
    private const int OwnershipSchemaVersion = 1;
    private static readonly UTF8Encoding s_utf8NoBom = new(false);
    private static readonly JsonSerializerOptions s_jsonOptions = new()
    {
        WriteIndented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    internal static async Task<WebPlayerProjectLayout> GenerateAsync(
        string outputDirectory,
        WebPlayerProjectOptions options,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(outputDirectory);
        ArgumentNullException.ThrowIfNull(options);

        string root = Path.GetFullPath(outputDirectory);
        if (Directory.Exists(root) && HasEntries(root))
            throw new IOException($"Web export directory must be empty: {root}");

        ValidateOptions(options);
        string assemblyName = GetAssemblyName(options.ProjectName);
        bool simd = options.SubTarget == WebSubTarget.Douyin ? false : options.WasmEnableSIMD;
        bool wasmEh = options.SubTarget == WebSubTarget.Browser;
        bool linkWebGpu = options.SubTarget != WebSubTarget.Douyin;
        string playerDirectory = Path.Combine(root, "Player");
        string wwwDirectory = Path.Combine(playerDirectory, "wwwroot");
        string wechatDirectory = Path.Combine(root, "wechat");
        string douyinDirectory = Path.Combine(root, "douyin");
        string projectPath = Path.Combine(playerDirectory, assemblyName + ".csproj");
        string ownershipManifestPath = Path.Combine(root, "xengine-web-project.json");

        Directory.CreateDirectory(playerDirectory);
        Directory.CreateDirectory(wwwDirectory);
        Directory.CreateDirectory(wechatDirectory);
        Directory.CreateDirectory(Path.Combine(wechatDirectory, "cdn"));
        Directory.CreateDirectory(Path.Combine(wechatDirectory, "dotnet"));
        Directory.CreateDirectory(Path.Combine(wechatDirectory, "wasm"));
        Directory.CreateDirectory(douyinDirectory);
        Directory.CreateDirectory(Path.Combine(douyinDirectory, "cdn"));
        Directory.CreateDirectory(Path.Combine(douyinDirectory, "wasm"));
        string nativeDir = Path.Combine(
            Path.GetDirectoryName(options.RuntimeProjectPath)!,
            "Platforms", "Browser", "native");
        string nativeDestDir = Path.Combine(playerDirectory, "native");
        CopyNativeIfExists(nativeDir, nativeDestDir, "xengine_webgl2.c");
        if (options.SubTarget != WebSubTarget.Douyin)
        {
            CopyNativeIfExists(nativeDir, nativeDestDir, "xengine_webgpu.c");
            CopyEmdawnwebgpuPackage(options.RuntimeProjectPath, playerDirectory);
        }

        await WriteTextAsync(
                projectPath,
                CreateProject(options, assemblyName, simd, wasmEh, linkWebGpu),
                cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(
                Path.Combine(playerDirectory, "BrowserPlayer.cs"),
                WebManagedPlayerSource.CreatePlayer(options),
                cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(
                Path.Combine(wwwDirectory, "index.html"),
                WebHostTemplates.CreateIndexHtml(options.ApplicationLabel),
                cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(
                Path.Combine(wwwDirectory, "main.js"),
                WebHostTemplates.CreateBrowserMainJs(),
                cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(
                Path.Combine(wechatDirectory, "game.json"),
                WebHostTemplates.CreateWeChatGameJson(),
                cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(
                Path.Combine(wechatDirectory, "game.js"),
                WebHostTemplates.CreateWeChatGameJs(),
                cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(
                Path.Combine(wechatDirectory, "project.config.json"),
                WebHostTemplates.CreateWeChatProjectConfig(options.ApplicationLabel),
                cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(
                Path.Combine(wechatDirectory, "cdn", ".keep"),
                string.Empty,
                cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(
                Path.Combine(wechatDirectory, "cdn", "game.js"),
                WebHostTemplates.CreateWeChatSubpackageGameJs(),
                cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(
                Path.Combine(wechatDirectory, "dotnet", ".keep"),
                string.Empty,
                cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(
                Path.Combine(wechatDirectory, "dotnet", "game.js"),
                WebHostTemplates.CreateWeChatSubpackageGameJs(),
                cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(
                Path.Combine(wechatDirectory, "wasm", ".keep"),
                string.Empty,
                cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(
                Path.Combine(douyinDirectory, "game.json"),
                WebHostTemplates.CreateDouyinGameJson(),
                cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(
                Path.Combine(douyinDirectory, "game.js"),
                WebHostTemplates.CreateDouyinGameJs(),
                cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(
                Path.Combine(douyinDirectory, "project.config.json"),
                WebHostTemplates.CreateDouyinProjectConfig(options.ApplicationLabel),
                cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(
                Path.Combine(douyinDirectory, "cdn", ".keep"),
                string.Empty,
                cancellationToken)
            .ConfigureAwait(false);
        await WriteTextAsync(
                Path.Combine(douyinDirectory, "wasm", ".keep"),
                string.Empty,
                cancellationToken)
            .ConfigureAwait(false);
        await WriteOwnershipManifestAsync(
                root,
                ownershipManifestPath,
                assemblyName,
                options.SubTarget,
                simd,
                wasmEh,
                linkWebGpu,
                cancellationToken)
            .ConfigureAwait(false);

        return new WebPlayerProjectLayout(
            root,
            projectPath,
            ownershipManifestPath,
            assemblyName,
            "browser-wasm",
            options.SubTarget,
            simd);
    }

    internal static string GetAssemblyName(string projectName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(projectName);
        return SanitizeIdentifier(projectName) + ".Web";
    }

    internal static string FindRuntimeProject(string searchRoot)
    {
        string? directory = Path.GetFullPath(searchRoot);
        if (File.Exists(directory))
            directory = Path.GetDirectoryName(directory);
        while (directory != null)
        {
            string candidate = Path.Combine(directory, "XEngine.Runtime", "XEngine.Runtime.csproj");
            if (File.Exists(candidate))
                return candidate;
            directory = Path.GetDirectoryName(directory);
        }
        throw new FileNotFoundException(
            "XEngine.Runtime.csproj was not found. The Web player must ProjectReference the engine Runtime with IsBrowserTarget.");
    }

    private static void ValidateOptions(WebPlayerProjectOptions options)
    {
        if (string.IsNullOrWhiteSpace(options.ProjectName))
            throw new ArgumentException("Web project name cannot be empty.", nameof(options));
        if (string.IsNullOrWhiteSpace(options.RuntimeProjectPath) || !File.Exists(options.RuntimeProjectPath))
            throw new FileNotFoundException("Web player requires XEngine.Runtime.csproj.", options.RuntimeProjectPath);
        if (string.IsNullOrWhiteSpace(options.CanvasSelector))
            throw new ArgumentException("Canvas selector cannot be empty.", nameof(options));
    }

    private static string CreateProject(
        WebPlayerProjectOptions options,
        string assemblyName,
        bool simd,
        bool wasmEh,
        bool linkWebGpu)
    {
        string runtimePath = options.RuntimeProjectPath.Replace('\\', '/');
        string simdValue = simd ? "true" : "false";
        string ehValue = wasmEh ? "true" : "false";
        string extraFlags = linkWebGpu
            ? """
                <EmccFlags>$(EmccFlags) --use-port=native/emdawnwebgpu_pkg/emdawnwebgpu.port.py:cpp_bindings=false</EmccFlags>
                <EmccExtraLDFlags>$(EmccExtraLDFlags) -sDEFAULT_TO_CXX=1 -sMIN_WEBGL_VERSION=2 -sMAX_WEBGL_VERSION=2 -sFULL_ES3=1 -Wl,--export=xengine_webgl2_create,--export=xengine_webgl2_make_current,--export=xengine_webgl2_get_proc_address,--export=xengine_webgl2_clear_rgba8,--export=xengine_webgl2_canvas_width,--export=xengine_webgl2_canvas_height,--export=xengine_webgl2_start_main_loop,--export=xengine_webgl2_on_native_frame,--export=xengine_webgl2_frame_count,--export=xengine_webgpu_create_canvas_surface</EmccExtraLDFlags>
            """
            : """
                <EmccExtraLDFlags>$(EmccExtraLDFlags) -sMIN_WEBGL_VERSION=2 -sMAX_WEBGL_VERSION=2 -sFULL_ES3=1 -lGL-getprocaddr -Wl,--export=xengine_webgl2_create,--export=xengine_webgl2_make_current,--export=xengine_webgl2_get_proc_address,--export=xengine_webgl2_clear_rgba8,--export=xengine_webgl2_canvas_width,--export=xengine_webgl2_canvas_height,--export=xengine_webgl2_start_main_loop,--export=xengine_webgl2_on_native_frame,--export=xengine_webgl2_frame_count</EmccExtraLDFlags>
            """;
        string nativeRefs = linkWebGpu
            ? """
                <NativeFileReference Include="native/xengine_webgl2.c" />
                <NativeFileReference Include="native/xengine_webgpu.c" />
            """
            : """
                <NativeFileReference Include="native/xengine_webgl2.c" />
            """;
        return $"""
            <Project Sdk="Microsoft.NET.Sdk.WebAssembly">
              <PropertyGroup>
                <TargetFramework>net10.0</TargetFramework>
                <RuntimeIdentifier>browser-wasm</RuntimeIdentifier>
                <OutputType>Exe</OutputType>
                <AssemblyName>{Xml(assemblyName)}</AssemblyName>
                <RootNamespace>XEngine.Generated.Web</RootNamespace>
                <Nullable>enable</Nullable>
                <ImplicitUsings>disable</ImplicitUsings>
                <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
                <InvariantGlobalization>true</InvariantGlobalization>
                <ServerGarbageCollection>false</ServerGarbageCollection>
                <PublishTrimmed>true</PublishTrimmed>
                <TrimMode>partial</TrimMode>
                <WasmEnableThreads>false</WasmEnableThreads>
                <WasmEnableSIMD>{simdValue}</WasmEnableSIMD>
                <WasmEnableExceptionHandling>{ehValue}</WasmEnableExceptionHandling>
                <WasmBuildNative>true</WasmBuildNative>
                <WasmMainJSPath>wwwroot/main.js</WasmMainJSPath>
                <!-- .NET wasm-tools freezes the SDK emcc cache. A writable cache is required to
                     compile extra native ports. On Windows it must share the SDK's drive (C:) because
                     Emscripten 3.1.56 os.path.relpath cannot cross drive letters. Do not use
                     $(TEMP): test/CI often redirect TEMP to D:, which then breaks libGL rebuilds. -->
                <WasmCachePath Condition="'$(OS)' == 'Windows_NT'">$(LocalAppData)\Temp\xengine-emcc-cache\</WasmCachePath>
                <WasmCachePath Condition="'$(OS)' != 'Windows_NT'">$(MSBuildThisFileDirectory)obj/emcc-cache/</WasmCachePath>
                {extraFlags}
                <IsBrowserTarget>true</IsBrowserTarget>
                <DefineConstants>$(DefineConstants);XENGINE_WEB;XENGINE_MOBILE</DefineConstants>
                <XEngineEnableAnalyzers>false</XEngineEnableAnalyzers>
                <OverrideHtmlAssetPlaceholders>true</OverrideHtmlAssetPlaceholders>
                <StaticWebAssetFingerprintingEnabled>false</StaticWebAssetFingerprintingEnabled>
                <StaticWebAssetsFingerprintContent>false</StaticWebAssetsFingerprintContent>
                <!-- Fingerprinted `_framework/dotnet.*.js` breaks `import './_framework/dotnet.js'`
                     and WeChat/Douyin path instantiate (they need a stable package-relative name). -->
              </PropertyGroup>
              <ItemGroup>
                <ProjectReference Include="{Xml(runtimePath)}">
                  <AdditionalProperties>IsBrowserTarget=true</AdditionalProperties>
                </ProjectReference>
                {nativeRefs}
              </ItemGroup>
              <!-- The WebAssembly SDK compiles runtime.c with wasm_m2n_invoke.g.h but does not
                   list that header as a dependency, so incremental publishes keep a stale
                   interp-to-native table (missing VFFFF etc.). -->
              <Target Name="_XEngineRebuildMonoRuntimeForInterpTrampolines" AfterTargets="_GenerateManagedToNative">
                <Delete Files="$(_WasmIntermediateOutputPath)runtime.o" />
              </Target>
            </Project>
            """;
    }

    private static async Task WriteOwnershipManifestAsync(
        string root,
        string manifestPath,
        string assemblyName,
        WebSubTarget subTarget,
        bool simd,
        bool wasmEh,
        bool linkWebGpu,
        CancellationToken cancellationToken)
    {
        string normalizedManifestPath = Path.GetFullPath(manifestPath);
        string[] files = Directory.GetFiles(root, "*", SearchOption.AllDirectories);
        Array.Sort(files, StringComparer.Ordinal);
        var entries = new List<object>(files.Length);
        for (int i = 0; i < files.Length; i++)
        {
            if (string.Equals(Path.GetFullPath(files[i]), normalizedManifestPath, StringComparison.OrdinalIgnoreCase))
                continue;
            string relativePath = Path.GetRelativePath(root, files[i]).Replace('\\', '/');
            entries.Add(new
            {
                path = relativePath,
                sha256 = await HashFileAsync(files[i], cancellationToken).ConfigureAwait(false),
            });
        }

        await WriteTextAsync(
                manifestPath,
                JsonSerializer.Serialize(new
                {
                    schemaVersion = OwnershipSchemaVersion,
                    platform = "web",
                    runtimeIdentifier = "browser-wasm",
                    subTarget = subTarget.ToString(),
                    wasmEnableSIMD = simd,
                    wasmEnableExceptionHandling = wasmEh,
                    wasmEnableThreads = false,
                    graphics = linkWebGpu ? "WebGPU+WebGL2" : "WebGL2",
                    assemblyName,
                    files = entries,
                }, s_jsonOptions),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private static void CopyEmdawnwebgpuPackage(string runtimeProjectPath, string playerDirectory)
    {
        string? runtimeDir = Path.GetDirectoryName(runtimeProjectPath);
        string? repoRoot = runtimeDir != null ? Path.GetDirectoryName(runtimeDir) : null;
        if (repoRoot == null)
            return;
        string source = Path.Combine(repoRoot, "ThirdParty", "emdawnwebgpu_pkg");
        if (!Directory.Exists(source))
            return;
        string dest = Path.Combine(playerDirectory, "native", "emdawnwebgpu_pkg");
        CopyDirectory(source, dest);
    }

    private static void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (string file in Directory.GetFiles(sourceDir, "*", SearchOption.AllDirectories))
        {
            string relative = Path.GetRelativePath(sourceDir, file);
            string target = Path.Combine(destDir, relative);
            Directory.CreateDirectory(Path.GetDirectoryName(target)!);
            File.Copy(file, target, overwrite: true);
        }
    }

    private static void CopyNativeIfExists(string sourceDir, string destDir, string fileName)
    {
        string source = Path.Combine(sourceDir, fileName);
        if (!File.Exists(source))
            return;
        Directory.CreateDirectory(destDir);
        File.Copy(source, Path.Combine(destDir, fileName), overwrite: true);
    }

    private static async Task<string> HashFileAsync(string path, CancellationToken cancellationToken)
    {
        await using FileStream stream = File.OpenRead(path);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static async Task WriteTextAsync(string path, string contents, CancellationToken cancellationToken)
    {
        await File.WriteAllTextAsync(path, contents.Replace("\r\n", "\n"), s_utf8NoBom, cancellationToken)
            .ConfigureAwait(false);
    }

    internal static string SanitizeIdentifier(string value)
    {
        var result = new StringBuilder(value.Length + 1);
        if (value.Length == 0 || !IsIdentifierStart(value[0]))
            result.Append('_');
        for (int i = 0; i < value.Length; i++)
            result.Append(IsIdentifierPart(value[i]) ? value[i] : '_');
        return result.ToString();
    }

    private static bool IsIdentifierStart(char value) => value == '_' || char.IsLetter(value);
    private static bool IsIdentifierPart(char value) => value == '_' || char.IsLetterOrDigit(value);
    private static string Xml(string value) => System.Security.SecurityElement.Escape(value) ?? string.Empty;

    private static bool HasEntries(string path)
    {
        using IEnumerator<string> entries = Directory.EnumerateFileSystemEntries(path).GetEnumerator();
        return entries.MoveNext();
    }
}
