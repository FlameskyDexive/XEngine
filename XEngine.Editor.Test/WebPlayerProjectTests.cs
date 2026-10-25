// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Text.Json;
using System.Xml.Linq;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;

using XEngine.Editor.Build;
using XEngine.Editor.Build.Web;
using XEngine.Editor.Projects.Build;
using XEngine.Editor.Projects.Settings;
using XEngine.Runtime;

using Xunit;

namespace XEngine.Editor.Test;

public sealed class WebPlayerProjectTests : IDisposable
{
    private readonly string _testRoot = Path.Combine(
        Path.GetTempPath(),
        "XEngineWebPlayerProjectTests",
        Guid.NewGuid().ToString("N"));

    [Fact]
    public async Task GenerateAsync_WritesBrowserWasmPlayerAndMiniGameAdapters()
    {
        Directory.CreateDirectory(_testRoot);
        string firstRoot = Path.Combine(_testRoot, "first");
        string secondRoot = Path.Combine(_testRoot, "second");
        WebPlayerProjectOptions options = CreateOptions();

        WebPlayerProjectLayout first = await WebPlayerProjectGenerator.GenerateAsync(firstRoot, options);
        WebPlayerProjectLayout second = await WebPlayerProjectGenerator.GenerateAsync(secondRoot, options);

        Assert.Equal("browser-wasm", first.RuntimeIdentifier);
        Assert.Equal(WebPlayerProjectGenerator.GetAssemblyName("My Game"), first.AssemblyName);
        Assert.Equal(WebSubTarget.Browser, first.SubTarget);
        Assert.True(first.WasmEnableSIMD);
        Assert.True(File.Exists(first.ProjectPath));
        Assert.True(File.Exists(first.OwnershipManifestPath));
        Assert.Equal(EnumerateFileBytes(firstRoot), EnumerateFileBytes(secondRoot));

        XDocument project = XDocument.Load(first.ProjectPath);
        Assert.Equal("net10.0", Property(project, "TargetFramework"));
        Assert.Equal("browser-wasm", Property(project, "RuntimeIdentifier"));
        Assert.Equal("false", Property(project, "WasmEnableThreads"));
        Assert.Equal("true", Property(project, "WasmEnableSIMD"));
        Assert.Equal("true", Property(project, "WasmEnableExceptionHandling"));
        Assert.Equal("true", Property(project, "WasmBuildNative"));
        Assert.Equal("false", Property(project, "StaticWebAssetFingerprintingEnabled"));
        Assert.Equal("false", Property(project, "StaticWebAssetsFingerprintContent"));
        Assert.Contains("-sMIN_WEBGL_VERSION=2", Property(project, "EmccExtraLDFlags"), StringComparison.Ordinal);
        Assert.Contains("-sFULL_ES3=1", Property(project, "EmccExtraLDFlags"), StringComparison.Ordinal);
        Assert.Contains("--export=xengine_webgl2_create", Property(project, "EmccExtraLDFlags"), StringComparison.Ordinal);
        Assert.Contains("--export=xengine_webgl2_clear_rgba8", Property(project, "EmccExtraLDFlags"), StringComparison.Ordinal);
        Assert.Contains("--export=xengine_webgl2_canvas_width", Property(project, "EmccExtraLDFlags"), StringComparison.Ordinal);
        Assert.Contains("--export=xengine_webgl2_start_main_loop", Property(project, "EmccExtraLDFlags"), StringComparison.Ordinal);
        Assert.Contains("_XEngineRebuildMonoRuntimeForInterpTrampolines", File.ReadAllText(first.ProjectPath), StringComparison.Ordinal);
        Assert.Contains("XENGINE_WEB", Property(project, "DefineConstants"), StringComparison.Ordinal);
        Assert.Contains("XENGINE_MOBILE", Property(project, "DefineConstants"), StringComparison.Ordinal);
        string projectText = await File.ReadAllTextAsync(first.ProjectPath);
        Assert.Contains("Microsoft.NET.Sdk.WebAssembly", projectText, StringComparison.Ordinal);
        Assert.Contains("XEngine.Runtime.csproj", projectText, StringComparison.Ordinal);
        Assert.Contains("AdditionalProperties>IsBrowserTarget=true", projectText, StringComparison.Ordinal);
        Assert.DoesNotContain("GlobalPropertiesToRemove", projectText, StringComparison.Ordinal);
        Assert.Contains("NativeFileReference Include=\"native/xengine_webgl2.c\"", projectText, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(firstRoot, "Player", "native", "xengine_webgl2.c")));
        Assert.Contains("emscripten_webgl_get_proc_address", await File.ReadAllTextAsync(Path.Combine(firstRoot, "Player", "native", "xengine_webgl2.c")), StringComparison.Ordinal);
        Assert.Contains("xengine_wasm_seed_vffff", await File.ReadAllTextAsync(Path.Combine(firstRoot, "Player", "native", "xengine_webgl2.c")), StringComparison.Ordinal);
        Assert.Contains("xengine_wasm_seed_viffff", await File.ReadAllTextAsync(Path.Combine(firstRoot, "Player", "native", "xengine_webgl2.c")), StringComparison.Ordinal);
        Assert.Contains("xengine_wasm_seed_viii", await File.ReadAllTextAsync(Path.Combine(firstRoot, "Player", "native", "xengine_webgl2.c")), StringComparison.Ordinal);
        Assert.Contains("xengine_webgl2_clear_rgba8", await File.ReadAllTextAsync(Path.Combine(firstRoot, "Player", "native", "xengine_webgl2.c")), StringComparison.Ordinal);
        Assert.Contains("native/emdawnwebgpu_pkg/emdawnwebgpu.port.py", projectText, StringComparison.Ordinal);
        Assert.Contains("cpp_bindings=false", projectText, StringComparison.Ordinal);
        Assert.Contains("WasmCachePath", projectText, StringComparison.Ordinal);
        Assert.Contains("$(LocalAppData)\\Temp\\xengine-emcc-cache", projectText, StringComparison.Ordinal);
        Assert.DoesNotContain("$(TEMP)\\xengine-emcc-cache", projectText, StringComparison.Ordinal);
        Assert.Contains("NativeFileReference Include=\"native/xengine_webgpu.c\"", projectText, StringComparison.Ordinal);
        Assert.Contains("--export=xengine_webgpu_create_canvas_surface", projectText, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(firstRoot, "Player", "native", "emdawnwebgpu_pkg", "emdawnwebgpu.port.py")));
        Assert.True(File.Exists(Path.Combine(firstRoot, "Player", "native", "emdawnwebgpu_pkg", "webgpu", "include", "webgpu", "webgpu.h")));
        Assert.Contains("xengine_webgpu_create_canvas_surface", await File.ReadAllTextAsync(Path.Combine(firstRoot, "Player", "native", "xengine_webgpu.c")), StringComparison.Ordinal);
        Assert.DoesNotContain("fetch(", await File.ReadAllTextAsync(Path.Combine(firstRoot, "wechat", "game.js")), StringComparison.Ordinal);
        Assert.Contains("WXWebAssembly.instantiate", await File.ReadAllTextAsync(Path.Combine(firstRoot, "wechat", "game.js")), StringComparison.Ordinal);
        Assert.Contains("wx.onTouchStart", await File.ReadAllTextAsync(Path.Combine(firstRoot, "wechat", "game.js")), StringComparison.Ordinal);
        Assert.DoesNotContain("window.addEventListener", await File.ReadAllTextAsync(Path.Combine(firstRoot, "wechat", "game.js")), StringComparison.Ordinal);
        Assert.Contains("tt.onTouchStart", await File.ReadAllTextAsync(Path.Combine(firstRoot, "douyin", "game.js")), StringComparison.Ordinal);
        Assert.DoesNotContain("window.addEventListener", await File.ReadAllTextAsync(Path.Combine(firstRoot, "douyin", "game.js")), StringComparison.Ordinal);
        string wechatJs = await File.ReadAllTextAsync(Path.Combine(firstRoot, "wechat", "game.js"));
        int typeofGpu = wechatJs.IndexOf("typeof wx.getGPU === 'function'", StringComparison.Ordinal);
        int callGpu = wechatJs.IndexOf("const gpu = wx.getGPU()", StringComparison.Ordinal);
        Assert.True(typeofGpu >= 0 && callGpu > typeofGpu, "wx.getGPU() may run only after a typeof function check.");
        Assert.DoesNotContain("getContext('webgl2')", wechatJs, StringComparison.Ordinal);
        Assert.Contains("Module.canvas = canvas", wechatJs, StringComparison.Ordinal);
        Assert.Contains("querySelector", wechatJs, StringComparison.Ordinal);
        Assert.Contains("xengine_webgl2_canvas_width", await File.ReadAllTextAsync(Path.Combine(firstRoot, "Player", "native", "xengine_webgl2.c")), StringComparison.Ordinal);
        Assert.Contains("xengine_webgl2_start_main_loop", await File.ReadAllTextAsync(Path.Combine(firstRoot, "Player", "native", "xengine_webgl2.c")), StringComparison.Ordinal);
        Assert.Contains("xengine_webgl2_on_native_frame", await File.ReadAllTextAsync(Path.Combine(firstRoot, "Player", "native", "xengine_webgl2.c")), StringComparison.Ordinal);
        Assert.Contains("TTWebAssembly.instantiate", await File.ReadAllTextAsync(Path.Combine(firstRoot, "douyin", "game.js")), StringComparison.Ordinal);
        Assert.Contains("tt.createCanvas", await File.ReadAllTextAsync(Path.Combine(firstRoot, "douyin", "game.js")), StringComparison.Ordinal);
        Assert.DoesNotContain("getGPU", await File.ReadAllTextAsync(Path.Combine(firstRoot, "douyin", "game.js")), StringComparison.Ordinal);
        Assert.Contains("id=\"canvas\"", await File.ReadAllTextAsync(Path.Combine(firstRoot, "Player", "wwwroot", "index.html")), StringComparison.Ordinal);
        Assert.Contains("dotnet.js", await File.ReadAllTextAsync(Path.Combine(firstRoot, "Player", "wwwroot", "main.js")), StringComparison.Ordinal);
        Assert.Contains("getConfig", await File.ReadAllTextAsync(Path.Combine(firstRoot, "Player", "wwwroot", "main.js")), StringComparison.Ordinal);
        Assert.DoesNotContain("dotnet.getConfig", await File.ReadAllTextAsync(Path.Combine(firstRoot, "Player", "wwwroot", "main.js")), StringComparison.Ordinal);
        Assert.Contains("AttachCanvas", await File.ReadAllTextAsync(Path.Combine(firstRoot, "Player", "wwwroot", "main.js")), StringComparison.Ordinal);
        Assert.Contains("navigator.gpu", await File.ReadAllTextAsync(Path.Combine(firstRoot, "Player", "wwwroot", "main.js")), StringComparison.Ordinal);
        Assert.Contains("SetAssetBase", await File.ReadAllTextAsync(Path.Combine(firstRoot, "Player", "wwwroot", "main.js")), StringComparison.Ordinal);
        Assert.Contains("OnPointer", await File.ReadAllTextAsync(Path.Combine(firstRoot, "Player", "wwwroot", "main.js")), StringComparison.Ordinal);
        Assert.Contains("ConfigurePlatform", await File.ReadAllTextAsync(Path.Combine(firstRoot, "Player", "wwwroot", "main.js")), StringComparison.Ordinal);
        Assert.Contains("pointerdown", await File.ReadAllTextAsync(Path.Combine(firstRoot, "Player", "wwwroot", "main.js")), StringComparison.Ordinal);
        Assert.Contains("InitializeAudio", await File.ReadAllTextAsync(Path.Combine(firstRoot, "Player", "wwwroot", "main.js")), StringComparison.Ordinal);
        Assert.Contains("createScriptProcessor", await File.ReadAllTextAsync(Path.Combine(firstRoot, "Player", "wwwroot", "main.js")), StringComparison.Ordinal);
        Assert.Contains("GetMasterVolume", await File.ReadAllTextAsync(Path.Combine(firstRoot, "Player", "wwwroot", "main.js")), StringComparison.Ordinal);
        Assert.Contains("wx.createInnerAudioContext", await File.ReadAllTextAsync(Path.Combine(firstRoot, "wechat", "game.js")), StringComparison.Ordinal);
        Assert.Contains("tt.createInnerAudioContext", await File.ReadAllTextAsync(Path.Combine(firstRoot, "douyin", "game.js")), StringComparison.Ordinal);
        Assert.DoesNotContain("miniaudioex", await File.ReadAllTextAsync(Path.Combine(firstRoot, "wechat", "game.js")), StringComparison.Ordinal);
        Assert.DoesNotContain("miniaudioex", await File.ReadAllTextAsync(Path.Combine(firstRoot, "douyin", "game.js")), StringComparison.Ordinal);
        Assert.Contains("xengine-boot-error", await File.ReadAllTextAsync(Path.Combine(firstRoot, "Player", "wwwroot", "main.js")), StringComparison.Ordinal);

        string playerSource = await File.ReadAllTextAsync(Path.Combine(firstRoot, "Player", "BrowserPlayer.cs"));
        AssertNoSyntaxErrors(playerSource);
        Assert.Contains("BrowserPlatformHost", playerSource, StringComparison.Ordinal);
        Assert.Contains("BrowserAssetHost.Install", playerSource, StringComparison.Ordinal);
        Assert.Contains("OnPointer", playerSource, StringComparison.Ordinal);
        Assert.Contains("BrowserInputRuntime", playerSource, StringComparison.Ordinal);
        Assert.Contains("BrowserAudioRuntime", playerSource, StringComparison.Ordinal);
        Assert.Contains("InitializeAudio", playerSource, StringComparison.Ordinal);
        Assert.Contains("webaudio", playerSource, StringComparison.Ordinal);
        Assert.Contains("SetAssetBase", playerSource, StringComparison.Ordinal);
        Assert.Contains("GraphicsBackend.Auto", playerSource, StringComparison.Ordinal);
        Assert.Contains("preferWebGpu", playerSource, StringComparison.Ordinal);
        Assert.DoesNotContain("GraphicsBackend.WebGL2", playerSource, StringComparison.Ordinal);
        Assert.Contains("JSExport", playerSource, StringComparison.Ordinal);
        Assert.Contains("OnAnimationFrame", playerSource, StringComparison.Ordinal);
        Assert.DoesNotContain("ForceJsExportRegister", playerSource, StringComparison.Ordinal);
        Assert.DoesNotContain("SelfHostFromMain", playerSource, StringComparison.Ordinal);
        Assert.DoesNotContain("StartSelfHostFramePump", playerSource, StringComparison.Ordinal);
        Assert.DoesNotContain("TryLoadSelfHostCubeScene", playerSource, StringComparison.Ordinal);
        Assert.DoesNotContain("wgpu_native", playerSource, StringComparison.Ordinal);

        using JsonDocument ownership = JsonDocument.Parse(await File.ReadAllTextAsync(first.OwnershipManifestPath));
        Assert.Equal(1, ownership.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal("web", ownership.RootElement.GetProperty("platform").GetString());
        Assert.Equal("browser-wasm", ownership.RootElement.GetProperty("runtimeIdentifier").GetString());
        Assert.Equal("WebGPU+WebGL2", ownership.RootElement.GetProperty("graphics").GetString());
        Assert.False(ownership.RootElement.GetProperty("wasmEnableThreads").GetBoolean());
    }

    [Fact]
    public async Task GenerateAsync_DouyinDisablesSimd()
    {
        Directory.CreateDirectory(_testRoot);
        WebPlayerProjectOptions options = CreateOptions(WebSubTarget.Douyin, wasmEnableSIMD: true);
        WebPlayerProjectLayout layout = await WebPlayerProjectGenerator.GenerateAsync(
            Path.Combine(_testRoot, "douyin"), options);

        Assert.False(layout.WasmEnableSIMD);
        Assert.Equal(WebSubTarget.Douyin, layout.SubTarget);
        XDocument project = XDocument.Load(layout.ProjectPath);
        Assert.Equal("false", Property(project, "WasmEnableSIMD"));
        Assert.Equal("false", Property(project, "WasmEnableExceptionHandling"));
        string projectText = await File.ReadAllTextAsync(layout.ProjectPath);
        Assert.DoesNotContain("emdawnwebgpu.port.py", projectText, StringComparison.Ordinal);
        Assert.DoesNotContain("xengine_webgpu", projectText, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(layout.RootDirectory, "Player", "native", "xengine_webgpu.c")));
        Assert.False(Directory.Exists(Path.Combine(layout.RootDirectory, "Player", "native", "emdawnwebgpu_pkg")));
        Assert.True(File.Exists(Path.Combine(layout.RootDirectory, "Player", "native", "xengine_webgl2.c")));
        string playerSource = await File.ReadAllTextAsync(Path.Combine(layout.RootDirectory, "Player", "BrowserPlayer.cs"));
        Assert.Contains("BrowserAudioRuntime.Ensure(\"inner\")", playerSource, StringComparison.Ordinal);
        Assert.Contains("GraphicsBackend.WebGL2", playerSource, StringComparison.Ordinal);
        Assert.Contains("preferWebGpu != 0 && false", playerSource, StringComparison.Ordinal);
        Assert.DoesNotContain("GraphicsBackend.Auto", playerSource, StringComparison.Ordinal);
        string douyinRoot = Path.Combine(layout.RootDirectory, "douyin");
        Assert.True(File.Exists(Path.Combine(douyinRoot, "game.js")));
        Assert.True(File.Exists(Path.Combine(douyinRoot, "game.json")));
        Assert.True(File.Exists(Path.Combine(douyinRoot, "project.config.json")));
        Assert.True(File.Exists(Path.Combine(douyinRoot, "cdn", ".keep")));
        Assert.True(File.Exists(Path.Combine(douyinRoot, "wasm", ".keep")));

        string gameJs = await File.ReadAllTextAsync(Path.Combine(douyinRoot, "game.js"));
        Assert.DoesNotContain("fetch(", gameJs, StringComparison.Ordinal);
        Assert.DoesNotContain("./_framework/", gameJs, StringComparison.Ordinal);
        Assert.DoesNotContain("instantiateStreaming", gameJs, StringComparison.Ordinal);
        Assert.DoesNotContain("window.addEventListener", gameJs, StringComparison.Ordinal);
        Assert.DoesNotContain("getGPU", gameJs, StringComparison.Ordinal);
        Assert.DoesNotContain("navigator.gpu", gameJs, StringComparison.Ordinal);
        Assert.DoesNotContain("WXWebAssembly", gameJs, StringComparison.Ordinal);
        Assert.DoesNotContain("emdawnwebgpu", gameJs, StringComparison.Ordinal);
        Assert.Contains("TTWebAssembly.instantiate", gameJs, StringComparison.Ordinal);
        Assert.Contains("wasm/dotnet.native.wasm.br", gameJs, StringComparison.Ordinal);
        Assert.Contains("getFileSystemManager", gameJs, StringComparison.Ordinal);
        Assert.Contains("tt.setStorageSync", gameJs, StringComparison.Ordinal);
        Assert.Contains("xengine.settings.", gameJs, StringComparison.Ordinal);
        Assert.Contains("tt.loadSubpackage", gameJs, StringComparison.Ordinal);
        Assert.Contains("tt.downloadFile", gameJs, StringComparison.Ordinal);
        Assert.Contains("loopRunning", gameJs, StringComparison.Ordinal);
        Assert.Contains("function stopLoop", gameJs, StringComparison.Ordinal);
        Assert.Contains("innerAudio.pause()", gameJs, StringComparison.Ordinal);
        Assert.Contains("tt.onHide", gameJs, StringComparison.Ordinal);
        Assert.Contains("tt.onShow", gameJs, StringComparison.Ordinal);
        Assert.Contains("bootNativeWasm()", gameJs, StringComparison.Ordinal);
        Assert.Contains("AttachCanvas(canvas.width, canvas.height, 0)", gameJs, StringComparison.Ordinal);
        Assert.Contains("https?:", gameJs, StringComparison.Ordinal);

        string gameJson = await File.ReadAllTextAsync(Path.Combine(douyinRoot, "game.json"));
        Assert.Contains("\"name\": \"cdn\"", gameJson, StringComparison.Ordinal);
        string projectConfig = await File.ReadAllTextAsync(Path.Combine(douyinRoot, "project.config.json"));
        Assert.Contains("\"compileType\": \"game\"", projectConfig, StringComparison.Ordinal);
        Assert.Contains("testAppId", projectConfig, StringComparison.Ordinal);

        Assert.Contains("BrowserAssetHost.InstallPackage();", playerSource, StringComparison.Ordinal);
        Assert.Contains("InstallPackageFile", playerSource, StringComparison.Ordinal);
        Assert.Contains("TrySetSetting", playerSource, StringComparison.Ordinal);
        using JsonDocument ownership = JsonDocument.Parse(await File.ReadAllTextAsync(layout.OwnershipManifestPath));
        Assert.Equal("Douyin", ownership.RootElement.GetProperty("subTarget").GetString());
        Assert.Equal("WebGL2", ownership.RootElement.GetProperty("graphics").GetString());
        Assert.False(ownership.RootElement.GetProperty("wasmEnableSIMD").GetBoolean());
    }

    [Fact]
    public async Task GenerateAsync_WeChatWritesPathLoaderStorageAndLifecycle()
    {
        Directory.CreateDirectory(_testRoot);
        WebPlayerProjectOptions options = CreateOptions(WebSubTarget.WeChat);
        WebPlayerProjectLayout layout = await WebPlayerProjectGenerator.GenerateAsync(
            Path.Combine(_testRoot, "wechat"), options);

        Assert.Equal(WebSubTarget.WeChat, layout.SubTarget);
        Assert.True(layout.WasmEnableSIMD);
        Assert.Equal("false", Property(XDocument.Load(layout.ProjectPath), "WasmEnableExceptionHandling"));
        string wechatRoot = Path.Combine(layout.RootDirectory, "wechat");
        Assert.True(File.Exists(Path.Combine(wechatRoot, "game.js")));
        Assert.True(File.Exists(Path.Combine(wechatRoot, "game.json")));
        Assert.True(File.Exists(Path.Combine(wechatRoot, "project.config.json")));
        Assert.True(File.Exists(Path.Combine(wechatRoot, "cdn", ".keep")));
        Assert.True(File.Exists(Path.Combine(wechatRoot, "cdn", "game.js")));
        Assert.True(File.Exists(Path.Combine(wechatRoot, "dotnet", ".keep")));
        Assert.True(File.Exists(Path.Combine(wechatRoot, "dotnet", "game.js")));
        Assert.True(File.Exists(Path.Combine(wechatRoot, "wasm", ".keep")));

        string gameJs = await File.ReadAllTextAsync(Path.Combine(wechatRoot, "game.js"));
        Assert.DoesNotContain("fetch(", gameJs, StringComparison.Ordinal);
        Assert.DoesNotContain("./_framework/", gameJs, StringComparison.Ordinal);
        Assert.DoesNotContain("instantiateStreaming", gameJs, StringComparison.Ordinal);
        Assert.DoesNotContain("window.addEventListener", gameJs, StringComparison.Ordinal);
        Assert.Contains("WXWebAssembly.instantiate", gameJs, StringComparison.Ordinal);
        Assert.Contains("wasm/dotnet.native.wasm", gameJs, StringComparison.Ordinal);
        Assert.Contains("require('./dotnet/dotnet.js')", gameJs, StringComparison.Ordinal);
        Assert.Contains("instantiateWasm", gameJs, StringComparison.Ordinal);
        Assert.Contains("loadCodeSubpackages", gameJs, StringComparison.Ordinal);
        Assert.Contains("loadOneSubpackage", gameJs, StringComparison.Ordinal);
        Assert.Contains("[XEngine] WeChat game.js start", gameJs, StringComparison.Ordinal);
        Assert.Contains("[XEngine] WeChat boot start", gameJs, StringComparison.Ordinal);
        Assert.Contains("finish('timeout')", gameJs, StringComparison.Ordinal);
        Assert.Contains("assembly-map.json", gameJs, StringComparison.Ordinal);
        Assert.Contains("globalThis.WebAssembly", gameJs, StringComparison.Ordinal);
        Assert.Contains("installWebAssemblyShim", gameJs, StringComparison.Ordinal);
        Assert.Contains("compileWasm", gameJs, StringComparison.Ordinal);
        Assert.Contains("__xengineWasmPath", gameJs, StringComparison.Ordinal);
        Assert.DoesNotContain("WXWebAssembly.compile", gameJs, StringComparison.Ordinal);
        Assert.Contains("readFileSync(rel, 'utf8')", gameJs, StringComparison.Ordinal);
        Assert.DoesNotContain("{ encoding: 'utf8' }", gameJs, StringComparison.Ordinal);
        Assert.Contains("https://xengine.local/", gameJs, StringComparison.Ordinal);
        Assert.Contains("dotnet/dotnet.native.js", gameJs, StringComparison.Ordinal);
        Assert.Contains("dotnet/dotnet.js", gameJs, StringComparison.Ordinal);
        Assert.Contains("getFileSystemManager", gameJs, StringComparison.Ordinal);
        Assert.Contains("wx.setStorageSync", gameJs, StringComparison.Ordinal);
        Assert.Contains("xengine.settings.", gameJs, StringComparison.Ordinal);
        Assert.Contains("wx.loadSubpackage", gameJs, StringComparison.Ordinal);
        Assert.Contains("wx.downloadFile", gameJs, StringComparison.Ordinal);
        Assert.Contains("loopRunning", gameJs, StringComparison.Ordinal);
        Assert.Contains("function stopLoop", gameJs, StringComparison.Ordinal);
        Assert.Contains("innerAudio.pause()", gameJs, StringComparison.Ordinal);
        Assert.Contains("wx.onHide", gameJs, StringComparison.Ordinal);
        Assert.Contains("wx.onShow", gameJs, StringComparison.Ordinal);
        Assert.Contains("bootNativeWasm()", gameJs, StringComparison.Ordinal);
        Assert.Contains("https?:", gameJs, StringComparison.Ordinal);
        Assert.Contains("function findBrowserPlayer", gameJs, StringComparison.Ordinal);
        Assert.Contains("function loadPlayerExports", gameJs, StringComparison.Ordinal);
        Assert.Contains("WeChat assembly exports", gameJs, StringComparison.Ordinal);
        Assert.Contains("setModuleImports('xengine'", gameJs, StringComparison.Ordinal);
        Assert.Contains("WeChat C#", gameJs, StringComparison.Ordinal);
        Assert.Contains("WeChat JSExport missing; self-host attach", gameJs, StringComparison.Ordinal);
        Assert.Contains("WeChat Mono path boot ok", gameJs, StringComparison.Ordinal);
        Assert.Contains("sizeCanvasToWindow", gameJs, StringComparison.Ordinal);
        Assert.Contains("windowWidth", gameJs, StringComparison.Ordinal);
        Assert.Contains("pixelRatio", gameJs, StringComparison.Ordinal);
        Assert.Contains("XENGINE_CANVAS_WIDTH", gameJs, StringComparison.Ordinal);
        Assert.Contains("XENGINE_CANVAS_HEIGHT", gameJs, StringComparison.Ordinal);
        Assert.Contains("function scheduleFrame", gameJs, StringComparison.Ordinal);
        Assert.Contains("canvas.requestAnimationFrame", gameJs, StringComparison.Ordinal);
        Assert.Contains("function presentFallback", gameJs, StringComparison.Ordinal);
        Assert.Contains("WeChat wasm abort", gameJs, StringComparison.Ordinal);
        Assert.Contains("if (globalThis.__xengineMonoReady)", gameJs, StringComparison.Ordinal);
        Assert.Contains("WeChat native magenta pump silenced", gameJs, StringComparison.Ordinal);
        Assert.Contains("_xengine_webgl2_clear_rgba8", gameJs, StringComparison.Ordinal);
        Assert.Contains("WeChat present fallback", gameJs, StringComparison.Ordinal);
        Assert.DoesNotContain("const player = exports.XEngine.Generated.Web.BrowserPlayer", gameJs, StringComparison.Ordinal);
        string wechatPlayer = await File.ReadAllTextAsync(Path.Combine(layout.RootDirectory, "Player", "BrowserPlayer.cs"));
        Assert.Contains("ForceJsExportRegister", wechatPlayer, StringComparison.Ordinal);
        Assert.Contains("SelfHostFromMain", wechatPlayer, StringComparison.Ordinal);
        Assert.Contains("AttachDefaultCanvas", wechatPlayer, StringComparison.Ordinal);
        Assert.Contains("StartSelfHostFramePump", wechatPlayer, StringComparison.Ordinal);
        Assert.Contains("TryLoadSelfHostCubeScene", wechatPlayer, StringComparison.Ordinal);
        Assert.Contains("WeChat C# cube begin", wechatPlayer, StringComparison.Ordinal);
        Assert.Contains("WeChat C# cube ok", wechatPlayer, StringComparison.Ordinal);
        Assert.Contains("WeChat C# self-host pump", wechatPlayer, StringComparison.Ordinal);
        Assert.Contains("WeChat C# canvas", wechatPlayer, StringComparison.Ordinal);
        Assert.Contains("__GeneratedInitializer", wechatPlayer, StringComparison.Ordinal);
        Assert.Contains("[JSImport(\"log\", \"xengine\")]", wechatPlayer, StringComparison.Ordinal);
        int typeofGpu = gameJs.IndexOf("typeof wx.getGPU === 'function'", StringComparison.Ordinal);
        int callGpu = gameJs.IndexOf("const gpu = wx.getGPU()", StringComparison.Ordinal);
        Assert.True(typeofGpu >= 0 && callGpu > typeofGpu);

        string gameJson = await File.ReadAllTextAsync(Path.Combine(wechatRoot, "game.json"));
        Assert.Contains("\"name\": \"dotnet\"", gameJson, StringComparison.Ordinal);
        Assert.Contains("\"root\": \"dotnet/\"", gameJson, StringComparison.Ordinal);
        Assert.Contains("\"name\": \"cdn\"", gameJson, StringComparison.Ordinal);
        Assert.Contains("\"root\": \"cdn/\"", gameJson, StringComparison.Ordinal);
        Assert.DoesNotContain("\"workers\"", gameJson, StringComparison.Ordinal);
        string projectConfig = await File.ReadAllTextAsync(Path.Combine(wechatRoot, "project.config.json"));
        Assert.Contains("\"compileType\": \"game\"", projectConfig, StringComparison.Ordinal);
        Assert.Contains("\"libVersion\": \"widelyUsed\"", projectConfig, StringComparison.Ordinal);
        Assert.DoesNotContain("\"libVersion\": \"game\"", projectConfig, StringComparison.Ordinal);
        Assert.Contains("\"isGameTourist\": true", projectConfig, StringComparison.Ordinal);
        Assert.Contains(WebHostTemplates.WeChatGameTouristAppId, projectConfig, StringComparison.Ordinal);
        Assert.DoesNotContain("touristappid", projectConfig, StringComparison.Ordinal);

        string playerSource = await File.ReadAllTextAsync(Path.Combine(layout.RootDirectory, "Player", "BrowserPlayer.cs"));
        AssertNoSyntaxErrors(playerSource);
        Assert.Contains("BrowserAssetHost.InstallPackage();", playerSource, StringComparison.Ordinal);
        Assert.Contains("InstallPackageFile", playerSource, StringComparison.Ordinal);
        Assert.Contains("TrySetSetting", playerSource, StringComparison.Ordinal);
        Assert.Contains("TakePendingStorageWrite", playerSource, StringComparison.Ordinal);
        Assert.Contains("GraphicsBackend.Auto", playerSource, StringComparison.Ordinal);
        using JsonDocument ownership = JsonDocument.Parse(await File.ReadAllTextAsync(layout.OwnershipManifestPath));
        Assert.Equal("WeChat", ownership.RootElement.GetProperty("subTarget").GetString());
        Assert.Equal("WebGPU+WebGL2", ownership.RootElement.GetProperty("graphics").GetString());
    }

    [Fact]
    public void GetAssemblyName_SanitizesProjectName()
    {
        Assert.Equal("My_Game.Web", WebPlayerProjectGenerator.GetAssemblyName("My Game"));
        Assert.Equal("_1Title.Web", WebPlayerProjectGenerator.GetAssemblyName("1Title"));
    }

    [Fact]
    public void PublishPlanner_RequiresGlobalIsBrowserTarget()
    {
        WebPublishPlan plan = WebPublishPlanner.Create(
            Path.Combine(_testRoot, "Player", "Game.Web.csproj"),
            _testRoot,
            BuildConfiguration.Release);

        Assert.Equal("dotnet", plan.PublishArgv[0]);
        Assert.Contains("publish", plan.PublishArgv);
        Assert.Contains("-p:IsBrowserTarget=true", plan.PublishArgv);
        Assert.Contains("-o", plan.PublishArgv);
        Assert.Equal(Path.Combine(_testRoot, "publish"), plan.PublishDirectory);
        Assert.Equal(Path.Combine(_testRoot, "publish", "wwwroot"), plan.WwwRootDirectory);
    }

    [Fact]
    public void PublishPlanner_SizeReportRejectsDesktopGraphicsPackages()
    {
        Directory.CreateDirectory(_testRoot);
        string framework = Path.Combine(_testRoot, "_framework");
        Directory.CreateDirectory(framework);
        File.WriteAllBytes(Path.Combine(framework, "XEngine.Runtime.wasm"), new byte[100]);
        File.WriteAllBytes(Path.Combine(framework, "XEngine.Runtime.wasm.br"), new byte[40]);
        File.WriteAllBytes(Path.Combine(framework, "dotnet.native.wasm.br"), new byte[20]);

        Directory.CreateDirectory(Path.Combine(_testRoot, "wechat", "cdn"));
        File.WriteAllBytes(Path.Combine(_testRoot, "wechat", "game.js"), new byte[8]);
        File.WriteAllBytes(Path.Combine(_testRoot, "wechat", "cdn", "tex.bin"), new byte[7]);

        WebPackageSizeSnapshot snapshot = WebPublishPlanner.WriteSizeReport(_testRoot, framework);
        snapshot.Enforce(Path.Combine(_testRoot, "verification", WebPublishPlanner.SizeReportFileName));

        using JsonDocument report = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(_testRoot, "verification", WebPublishPlanner.SizeReportFileName)));
        Assert.Equal(2, report.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(100, report.RootElement.GetProperty("uniqueBytes").GetInt64());
        Assert.Equal(60, report.RootElement.GetProperty("brotliBytes").GetInt64());
        Assert.Equal(8, report.RootElement.GetProperty("buckets").GetProperty("weChatMain").GetInt64());
        Assert.Equal(7, report.RootElement.GetProperty("buckets").GetProperty("weChatCdn").GetInt64());
        Assert.True(report.RootElement.GetProperty("largestEntries").GetArrayLength() <= 20);
        Assert.True(report.RootElement.GetProperty("largestEntries").GetArrayLength() >= 3);
        Assert.Equal(4L * 1024 * 1024, report.RootElement.GetProperty("weChatMainPackageLimitBytes").GetInt64());
        Assert.Equal(20L * 1024 * 1024, report.RootElement.GetProperty("weChatTotalPackageLimitBytes").GetInt64());
        Assert.Equal(30L * 1024 * 1024, report.RootElement.GetProperty("weChatTotalPackageLimitWithVirtualPayBytes").GetInt64());
        Assert.True(report.RootElement.GetProperty("weChatMainWithinBudget").GetBoolean());
        Assert.True(report.RootElement.GetProperty("withinBudget").GetBoolean());
        Assert.False(report.RootElement.GetProperty("containsMagick").GetBoolean());
        Assert.False(report.RootElement.GetProperty("containsVulkan").GetBoolean());
        Assert.False(report.RootElement.GetProperty("containsSlang").GetBoolean());
        Assert.True(snapshot.WithinBudget);
    }

    [Fact]
    public void PublishPlanner_SizeReportFailsOnForbiddenDesktopPackages()
    {
        Directory.CreateDirectory(_testRoot);
        string framework = Path.Combine(_testRoot, "_framework");
        Directory.CreateDirectory(framework);
        File.WriteAllBytes(Path.Combine(framework, "Magick.NET.wasm"), new byte[12]);

        WebPackageSizeSnapshot snapshot = WebPublishPlanner.WriteSizeReport(_testRoot, framework);
        Assert.True(snapshot.ContainsMagick);
        Assert.False(snapshot.WithinBudget);
        Assert.Throws<WebPackageSizeBudgetExceededException>(() =>
            snapshot.Enforce(Path.Combine(_testRoot, "verification", WebPublishPlanner.SizeReportFileName)));
    }

    [Fact]
    public void PublishPlanner_SizeReportFailsWhenWeChatMainExceeds4MiB()
    {
        Directory.CreateDirectory(_testRoot);
        string framework = Path.Combine(_testRoot, "_framework");
        Directory.CreateDirectory(framework);
        File.WriteAllBytes(Path.Combine(framework, "XEngine.Runtime.wasm"), new byte[4]);
        Directory.CreateDirectory(Path.Combine(_testRoot, "wechat", "wasm"));
        File.WriteAllBytes(
            Path.Combine(_testRoot, "wechat", "wasm", "dotnet.native.wasm"),
            new byte[4 * 1024 * 1024 + 1]);

        WebPackageSizeSnapshot snapshot = WebPublishPlanner.WriteSizeReport(_testRoot, framework);
        Assert.False(snapshot.WeChatMainWithinBudget);
        Assert.Throws<WebPackageSizeBudgetExceededException>(() =>
            snapshot.Enforce(Path.Combine(_testRoot, "verification", WebPublishPlanner.SizeReportFileName)));
    }

    [Fact]
    public void PublishPlanner_WritesHonestRuntimeBaselineWithoutInventedTimings()
    {
        Directory.CreateDirectory(_testRoot);
        WebPublishPlanner.WriteRuntimeBaseline(_testRoot);

        using JsonDocument baseline = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(_testRoot, "verification", WebPublishPlanner.RuntimeBaselineFileName)));
        Assert.Equal(1, baseline.RootElement.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(JsonValueKind.Null, baseline.RootElement.GetProperty("browserColdStartToFirstPresentMs").ValueKind);
        Assert.Equal(JsonValueKind.Null, baseline.RootElement.GetProperty("browserEmptySceneFps").ValueKind);
        Assert.Equal(JsonValueKind.Null, baseline.RootElement.GetProperty("weChatColdStartToFirstPresentMs").ValueKind);
        Assert.False(baseline.RootElement.GetProperty("douyinFramerateTracked").GetBoolean());
    }

    [Fact]
    public void PublishPlanner_CopiesFingerprintedDotnetJsToStableName()
    {
        Directory.CreateDirectory(_testRoot);
        string framework = Path.Combine(_testRoot, "_framework");
        Directory.CreateDirectory(framework);
        File.WriteAllText(Path.Combine(framework, "dotnet.iqj6jn9j0v.js"), "boot");
        File.WriteAllText(Path.Combine(framework, "dotnet.native.551abh86rf.js"), "native");
        File.WriteAllText(Path.Combine(framework, "dotnet.runtime.zbexyp8zrs.js"), "runtime");
        File.WriteAllText(Path.Combine(framework, "dotnet.iqj6jn9j0v.js.br"), "compressed");

        Assert.Equal(1, WebPublishPlanner.WriteStableFrameworkAliases(framework));
        Assert.Equal("boot", File.ReadAllText(Path.Combine(framework, "dotnet.js")));
        Assert.False(File.Exists(Path.Combine(framework, "dotnet.native.js")));
        Assert.Equal(1, WebPublishPlanner.WriteStableFrameworkAliases(framework));
    }

    [Fact]
    public void WeChatEsmCommonJs_RewritesExportImportMetaAndDynamicImport()
    {
        const string esm =
            "const loc = import.meta.url;\n" +
            "const other = import('./dotnet.native.abc.js');\n" +
            "const ft = 1, gt = 2, mt = 3;\n" +
            "export { gt as default, ft as dotnet, mt as exit };\n";

        string cjs = WeChatEsmCommonJs.ToCommonJs(esm, "https://xengine.local/dotnet/dotnet.js");
        Assert.Contains("var __xengineImportMetaUrl = \"https://xengine.local/dotnet/dotnet.js\"", cjs, StringComparison.Ordinal);
        Assert.Contains("__xengineImportMetaUrl", cjs, StringComparison.Ordinal);
        Assert.DoesNotContain("import.meta", cjs, StringComparison.Ordinal);
        Assert.Contains("__xengineImport('./dotnet.native.abc.js')", cjs, StringComparison.Ordinal);
        Assert.DoesNotContain("import(", cjs, StringComparison.Ordinal);
        Assert.Contains("module.exports[\"default\"] = gt", cjs, StringComparison.Ordinal);
        Assert.Contains("module.exports[\"dotnet\"] = ft", cjs, StringComparison.Ordinal);
        Assert.DoesNotContain("export {", cjs, StringComparison.Ordinal);
        Assert.DoesNotContain("sourceMappingURL", cjs, StringComparison.Ordinal);
        Assert.Equal(cjs, WeChatEsmCommonJs.ToCommonJs(cjs, "https://xengine.local/dotnet/dotnet.js"));
    }

    [Fact]
    public void WeChatEsmCommonJs_StripsSourceMappingUrl()
    {
        string cjs = WeChatEsmCommonJs.ToCommonJs(
            "export default runtime;\n//# sourceMappingURL=dotnet.runtime.js.map\n",
            "https://xengine.local/dotnet/dotnet.runtime.js");
        Assert.DoesNotContain("sourceMappingURL", cjs, StringComparison.Ordinal);
        Assert.Contains("module.exports = runtime", cjs, StringComparison.Ordinal);
    }

    [Fact]
    public void WeChatEsmCommonJs_PatchesEmscriptenCanvasLookup()
    {
        const string glue =
            "var findEventTarget = (target) => {\n" +
            "      target = maybeCStringToJsString(target);\n" +
            "      var domElement = specialHTMLTargets[target] || (typeof document != 'undefined' ? document.querySelector(target) : undefined);\n" +
            "      return domElement;\n" +
            "    };\n";
        string patched = WeChatEsmCommonJs.PatchEmscriptenCanvasLookup(glue);
        Assert.Contains("globalThis.__xengineCanvas", patched, StringComparison.Ordinal);
        Assert.Contains("Module.canvas", patched, StringComparison.Ordinal);
        Assert.Equal(patched, WeChatEsmCommonJs.PatchEmscriptenCanvasLookup(patched));
    }

    [Fact]
    public void WeChatEsmCommonJs_RewritesExportDefault()
    {
        string cjs = WeChatEsmCommonJs.ToCommonJs(
            "export default createDotnetRuntime;\n",
            "https://xengine.local/dotnet/dotnet.native.js");
        Assert.Contains("module.exports = createDotnetRuntime", cjs, StringComparison.Ordinal);
        Assert.Contains("module.exports.default = createDotnetRuntime", cjs, StringComparison.Ordinal);
        Assert.DoesNotContain("export default", cjs, StringComparison.Ordinal);
    }

    [Fact]
    public void WeChatMiniGamePackager_PacksCommonJsAndSplitsAssembliesOutOfMain()
    {
        Directory.CreateDirectory(_testRoot);
        string framework = Path.Combine(_testRoot, "_framework");
        string wechat = Path.Combine(_testRoot, "wechat");
        Directory.CreateDirectory(framework);
        Directory.CreateDirectory(wechat);
        File.WriteAllText(Path.Combine(wechat, "game.json"), WebHostTemplates.CreateWeChatGameJson());
        File.WriteAllBytes(Path.Combine(framework, "dotnet.native.abc123xyz.wasm"), [1, 2, 3, 4]);
        File.WriteAllBytes(Path.Combine(framework, "dotnet.native.abc123xyz.wasm.br"), [9]);
        File.WriteAllText(
            Path.Combine(framework, "dotnet.iqj6jn9j0v.js"),
            "const loc = import.meta.url; export { loc as default, loc as dotnet };");
        File.WriteAllText(
            Path.Combine(framework, "dotnet.native.551abh86rf.js"),
            "export default createDotnetRuntime;");
        File.WriteAllText(
            Path.Combine(framework, "dotnet.runtime.zbexyp8zrs.js"),
            "export default runtime;\n//# sourceMappingURL=dotnet.runtime.js.map\n");
        File.WriteAllBytes(Path.Combine(framework, "System.Private.CoreLib.6tftr9mr8s.wasm"), new byte[16]);
        File.WriteAllBytes(Path.Combine(framework, "XEngine.Runtime.wasm"), new byte[32]);
        File.WriteAllBytes(Path.Combine(framework, "XEngine.Runtime.wasm.br"), new byte[8]);
        File.WriteAllBytes(Path.Combine(framework, "Magick.NET.wasm"), new byte[12]);
        File.WriteAllBytes(Path.Combine(framework, "icudt.dat"), new byte[20]);

        WeChatPackageLayout packed = WeChatMiniGamePackager.Pack(framework, wechat);
        Assert.Equal(2, packed.NativeWasmCopied);
        Assert.True(packed.JsModulesTransformed >= 3);
        Assert.Equal(2, packed.AssembliesCopied);
        Assert.Contains("dotnet/", packed.CodeSubpackageRoots);

        Assert.Equal([1, 2, 3, 4], File.ReadAllBytes(Path.Combine(wechat, "wasm", "dotnet.native.wasm")));
        Assert.False(File.Exists(Path.Combine(wechat, "dotnet", "dotnet.native.abc123xyz.wasm")));
        Assert.False(File.Exists(Path.Combine(wechat, "dotnet", "Magick.NET.wasm")));
        Assert.False(File.Exists(Path.Combine(wechat, "dotnet", "XEngine.Runtime.wasm.br")));
        Assert.False(File.Exists(Path.Combine(wechat, "dotnet", "icudt.dat")));
        Assert.True(File.Exists(Path.Combine(wechat, "dotnet", "dotnet.js")));
        Assert.True(File.Exists(Path.Combine(wechat, "dotnet", "dotnet.native.js")));
        Assert.True(File.Exists(Path.Combine(wechat, "dotnet", "dotnet.runtime.js")));
        Assert.False(File.Exists(Path.Combine(wechat, "dotnet", "dotnet.iqj6jn9j0v.js")));
        Assert.False(File.Exists(Path.Combine(wechat, "dotnet", "dotnet.native.551abh86rf.js")));
        Assert.True(File.Exists(Path.Combine(wechat, "dotnet", "System.Private.CoreLib.6tftr9mr8s.wasm")));
        Assert.True(File.Exists(Path.Combine(wechat, "dotnet", "XEngine.Runtime.wasm")));
        Assert.True(File.Exists(Path.Combine(wechat, "dotnet", WeChatMiniGamePackager.BootManifestFileName)));
        Assert.True(File.Exists(Path.Combine(wechat, WeChatMiniGamePackager.AssemblyMapFileName)));
        using (JsonDocument map = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(wechat, WeChatMiniGamePackager.AssemblyMapFileName))))
        {
            JsonElement files = map.RootElement.GetProperty("files");
            Assert.Equal(
                "dotnet/System.Private.CoreLib.6tftr9mr8s.wasm",
                files.GetProperty("System.Private.CoreLib.6tftr9mr8s.wasm").GetString());
            Assert.Equal(
                "dotnet/System.Private.CoreLib.6tftr9mr8s.wasm",
                files.GetProperty("System.Private.CoreLib.wasm").GetString());
        }
        string packedGameJs = File.ReadAllText(Path.Combine(wechat, "game.js"));
        Assert.Contains("globalThis.WebAssembly", packedGameJs, StringComparison.Ordinal);
        Assert.Contains("compileWasm", packedGameJs, StringComparison.Ordinal);
        Assert.DoesNotContain("WXWebAssembly.compile", packedGameJs, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(wechat, "dotnet", "game.js")));
        Assert.True(File.Exists(Path.Combine(wechat, "cdn", "game.js")));

        string loader = File.ReadAllText(Path.Combine(wechat, "dotnet", "dotnet.js"));
        Assert.Contains("module.exports", loader, StringComparison.Ordinal);
        Assert.DoesNotContain("export {", loader, StringComparison.Ordinal);
        Assert.DoesNotContain("import.meta", loader, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "sourceMappingURL",
            File.ReadAllText(Path.Combine(wechat, "dotnet", "dotnet.runtime.js")),
            StringComparison.Ordinal);

        string gameJson = File.ReadAllText(Path.Combine(wechat, "game.json"));
        Assert.Contains("\"name\": \"dotnet\"", gameJson, StringComparison.Ordinal);
        Assert.Contains("\"name\": \"cdn\"", gameJson, StringComparison.Ordinal);
        Assert.DoesNotContain("\"workers\"", gameJson, StringComparison.Ordinal);
    }

    [Fact]
    public void WeChatMiniGamePackager_SplitsAssembliesAcrossDotnetSubpackages()
    {
        Directory.CreateDirectory(_testRoot);
        string framework = Path.Combine(_testRoot, "_framework");
        string wechat = Path.Combine(_testRoot, "wechat");
        Directory.CreateDirectory(framework);
        Directory.CreateDirectory(wechat);
        File.WriteAllText(
            Path.Combine(framework, "dotnet.aaaaaaa.js"),
            "export default 1;");
        File.WriteAllBytes(
            Path.Combine(framework, "Big.One.wasm"),
            new byte[WeChatMiniGamePackager.SubpackagePackBudgetBytes - 32]);
        File.WriteAllBytes(
            Path.Combine(framework, "Big.Two.wasm"),
            new byte[WeChatMiniGamePackager.SubpackagePackBudgetBytes - 32]);

        WeChatPackageLayout packed = WeChatMiniGamePackager.Pack(framework, wechat);
        Assert.Equal(2, packed.AssembliesCopied);
        Assert.Contains("dotnet/", packed.CodeSubpackageRoots);
        Assert.Contains("dotnet2/", packed.CodeSubpackageRoots);
        Assert.True(File.Exists(Path.Combine(wechat, "dotnet", "game.js")));
        Assert.True(File.Exists(Path.Combine(wechat, "dotnet2", "game.js")));
        Assert.True(
            File.Exists(Path.Combine(wechat, "dotnet", "Big.One.wasm")) ||
            File.Exists(Path.Combine(wechat, "dotnet2", "Big.One.wasm")));
        Assert.True(
            File.Exists(Path.Combine(wechat, "dotnet", "Big.Two.wasm")) ||
            File.Exists(Path.Combine(wechat, "dotnet2", "Big.Two.wasm")));
        Assert.False(
            File.Exists(Path.Combine(wechat, "dotnet", "Big.One.wasm")) &&
            File.Exists(Path.Combine(wechat, "dotnet", "Big.Two.wasm")));
        using JsonDocument map = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(wechat, WeChatMiniGamePackager.AssemblyMapFileName)));
        JsonElement files = map.RootElement.GetProperty("files");
        string one = files.GetProperty("Big.One.wasm").GetString()!;
        string two = files.GetProperty("Big.Two.wasm").GetString()!;
        Assert.NotEqual(one, two);
        Assert.StartsWith("dotnet", one, StringComparison.Ordinal);
        Assert.StartsWith("dotnet", two, StringComparison.Ordinal);
    }

    [Fact]
    public void WeChatMiniGamePackager_StripsAssemblyFingerprint()
    {
        Assert.Equal(
            "System.Private.CoreLib.wasm",
            WeChatMiniGamePackager.StripAssemblyFingerprint("System.Private.CoreLib.6tftr9mr8s.wasm"));
        Assert.Equal(
            "Silk.NET.OpenGL.wasm",
            WeChatMiniGamePackager.StripAssemblyFingerprint("Silk.NET.OpenGL.owqp48dt56.wasm"));
        Assert.Null(WeChatMiniGamePackager.StripAssemblyFingerprint("dotnet.native.wasm"));
        Assert.Null(WeChatMiniGamePackager.StripAssemblyFingerprint("XEngine.Runtime.wasm"));
    }

    [Fact]
    public void PublishPlanner_SizeReportCountsDotnetSubpackageOutsideWeChatMain()
    {
        Directory.CreateDirectory(_testRoot);
        string framework = Path.Combine(_testRoot, "_framework");
        Directory.CreateDirectory(framework);
        File.WriteAllBytes(Path.Combine(framework, "XEngine.Runtime.wasm"), new byte[100]);
        File.WriteAllBytes(Path.Combine(framework, "XEngine.Runtime.wasm.br"), new byte[40]);
        Directory.CreateDirectory(Path.Combine(_testRoot, "wechat", "cdn"));
        Directory.CreateDirectory(Path.Combine(_testRoot, "wechat", "dotnet"));
        File.WriteAllBytes(Path.Combine(_testRoot, "wechat", "game.js"), new byte[8]);
        File.WriteAllBytes(Path.Combine(_testRoot, "wechat", "cdn", "tex.bin"), new byte[7]);
        File.WriteAllBytes(Path.Combine(_testRoot, "wechat", "dotnet", "CoreLib.wasm"), new byte[64]);

        WebPackageSizeSnapshot snapshot = WebPublishPlanner.WriteSizeReport(_testRoot, framework);
        snapshot.Enforce(Path.Combine(_testRoot, "verification", WebPublishPlanner.SizeReportFileName));

        using JsonDocument report = JsonDocument.Parse(
            File.ReadAllText(Path.Combine(_testRoot, "verification", WebPublishPlanner.SizeReportFileName)));
        Assert.Equal(8, report.RootElement.GetProperty("buckets").GetProperty("weChatMain").GetInt64());
        Assert.Equal(71, report.RootElement.GetProperty("buckets").GetProperty("weChatCdn").GetInt64());
        Assert.True(snapshot.WeChatMainWithinBudget);
    }

    [Fact]
    public void PublishPlanner_CopiesWeChatNativeWasmAliasesOnly()
    {
        Directory.CreateDirectory(_testRoot);
        string framework = Path.Combine(_testRoot, "_framework");
        string wechat = Path.Combine(_testRoot, "wechat");
        Directory.CreateDirectory(framework);
        Directory.CreateDirectory(wechat);
        File.WriteAllBytes(Path.Combine(framework, "dotnet.native.abc123xyz.wasm"), [1, 2, 3, 4]);
        File.WriteAllBytes(Path.Combine(framework, "dotnet.native.abc123xyz.wasm.br"), [9]);
        File.WriteAllText(Path.Combine(framework, "dotnet.iqj6jn9j0v.js"), "boot");
        File.WriteAllBytes(Path.Combine(framework, "XEngine.Runtime.wasm"), [7, 7]);

        Assert.Equal(2, WebPublishPlanner.CopyWeChatRuntimeAliases(framework, wechat));
        Assert.Equal([1, 2, 3, 4], File.ReadAllBytes(Path.Combine(wechat, "wasm", "dotnet.native.wasm")));
        Assert.Equal([9], File.ReadAllBytes(Path.Combine(wechat, "wasm", "dotnet.native.wasm.br")));
        Assert.False(File.Exists(Path.Combine(wechat, "wasm", "dotnet.js")));
        Assert.False(File.Exists(Path.Combine(wechat, "wasm", "XEngine.Runtime.wasm")));
        Assert.Equal(2, WebPublishPlanner.CopyWeChatRuntimeAliases(framework, wechat));

        string douyin = Path.Combine(_testRoot, "douyin");
        Assert.Equal(2, WebPublishPlanner.CopyDouyinRuntimeAliases(framework, douyin));
        Assert.Equal([9], File.ReadAllBytes(Path.Combine(douyin, "wasm", "dotnet.native.wasm.br")));
        Assert.False(File.Exists(Path.Combine(douyin, "wasm", "XEngine.Runtime.wasm")));
    }

    [Fact]
    public async Task CiSnapshot_WeChatAndDouyinExportsHaveTemplatesSimdAndNoDouyinWgpu()
    {
        Directory.CreateDirectory(_testRoot);
        WebPlayerProjectLayout wechat = await WebPlayerProjectGenerator.GenerateAsync(
            Path.Combine(_testRoot, "ci-wechat"), CreateOptions(WebSubTarget.WeChat));
        WebPlayerProjectLayout douyin = await WebPlayerProjectGenerator.GenerateAsync(
            Path.Combine(_testRoot, "ci-douyin"), CreateOptions(WebSubTarget.Douyin, wasmEnableSIMD: true));

        Assert.True(File.Exists(Path.Combine(wechat.RootDirectory, "wechat", "game.js")));
        Assert.True(File.Exists(Path.Combine(wechat.RootDirectory, "wechat", "game.json")));
        Assert.True(File.Exists(Path.Combine(wechat.RootDirectory, "wechat", "project.config.json")));
        Assert.True(wechat.WasmEnableSIMD);
        Assert.Equal("false", Property(XDocument.Load(wechat.ProjectPath), "WasmEnableExceptionHandling"));
        Assert.Contains(
            "WXWebAssembly.instantiate",
            await File.ReadAllTextAsync(Path.Combine(wechat.RootDirectory, "wechat", "game.js")),
            StringComparison.Ordinal);

        Assert.False(douyin.WasmEnableSIMD);
        Assert.Equal("false", Property(XDocument.Load(douyin.ProjectPath), "WasmEnableSIMD"));
        Assert.Equal("false", Property(XDocument.Load(douyin.ProjectPath), "WasmEnableExceptionHandling"));
        string douyinProject = await File.ReadAllTextAsync(douyin.ProjectPath);
        Assert.DoesNotContain("emdawnwebgpu.port.py", douyinProject, StringComparison.Ordinal);
        Assert.DoesNotContain("xengine_webgpu", douyinProject, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(douyin.RootDirectory, "Player", "native", "xengine_webgpu.c")));
        string douyinJs = await File.ReadAllTextAsync(Path.Combine(douyin.RootDirectory, "douyin", "game.js"));
        Assert.DoesNotContain("getGPU", douyinJs, StringComparison.Ordinal);
        Assert.DoesNotContain("wgpu", douyinJs, StringComparison.Ordinal);
        Assert.Contains("TTWebAssembly.instantiate", douyinJs, StringComparison.Ordinal);
        Assert.True(File.Exists(Path.Combine(douyin.RootDirectory, "douyin", "game.json")));
        Assert.True(File.Exists(Path.Combine(douyin.RootDirectory, "douyin", "project.config.json")));
    }

    [Fact]
    public void Pipeline_RegistersWebId()
    {
        var pipeline = new WebBuildPipeline();
        Assert.Equal("xengine.web", pipeline.Id);
        Assert.Equal("Web", pipeline.DisplayName);
        Assert.Contains("browser-wasm", pipeline.SupportedRuntimeIdentifiers);
    }

    public void Dispose()
    {
        if (Directory.Exists(_testRoot))
            Directory.Delete(_testRoot, recursive: true);
    }

    private static WebPlayerProjectOptions CreateOptions(
        WebSubTarget subTarget = WebSubTarget.Browser,
        bool wasmEnableSIMD = true) => new()
    {
        ProjectName = "My Game",
        ApplicationId = "com.xengine.testgame",
        ApplicationLabel = "My Game",
        SubTarget = subTarget,
        WasmEnableSIMD = wasmEnableSIMD,
        CanvasSelector = "#canvas",
        RuntimeProjectPath = WebPlayerProjectGenerator.FindRuntimeProject(
            typeof(EngineObject).Assembly.Location),
    };

    private static string Property(XDocument project, string name) =>
        project.Descendants(name).Single().Value;

    private static void AssertNoSyntaxErrors(string source)
    {
        SyntaxTree tree = CSharpSyntaxTree.ParseText(source, new CSharpParseOptions(LanguageVersion.Preview));
        foreach (Diagnostic diagnostic in tree.GetDiagnostics())
            Assert.NotEqual(DiagnosticSeverity.Error, diagnostic.Severity);
    }

    private static IReadOnlyList<string> EnumerateFileBytes(string root) =>
        Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .OrderBy(path => Path.GetRelativePath(root, path), StringComparer.Ordinal)
            .Select(path => Path.GetRelativePath(root, path).Replace('\\', '/') + ":" + Convert.ToBase64String(File.ReadAllBytes(path)))
            .ToList();
}
