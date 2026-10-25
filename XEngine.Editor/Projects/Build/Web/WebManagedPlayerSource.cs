// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Text.Json;

using XEngine.Editor.Projects.Build;

namespace XEngine.Editor.Build.Web;

/// <summary>Generated <c>net10.0</c> + <c>browser-wasm</c> host sources and JS adapters.</summary>
internal static class WebManagedPlayerSource
{
    internal static string CreatePlayer(WebPlayerProjectOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);
        string label = JsonSerializer.Serialize(options.ApplicationLabel);
        string selector = JsonSerializer.Serialize(options.CanvasSelector);
        string audioKind = options.SubTarget == WebSubTarget.Browser ? "webaudio" : "inner";
        string preferredBackend = options.SubTarget == WebSubTarget.Douyin
            ? "GraphicsBackend.WebGL2"
            : "GraphicsBackend.Auto";
        string allowWebGpu = options.SubTarget == WebSubTarget.Douyin ? "false" : "true";
        string packageInstall = options.SubTarget == WebSubTarget.Browser
            ? string.Empty
            : "BrowserAssetHost.InstallPackage();\n                    ";
        bool miniGame = options.SubTarget != WebSubTarget.Browser;
        string jsHostUsings = miniGame
            ? """
            using System.Reflection;
            using System.Runtime.InteropServices;

            """
            : string.Empty;
        string forceRegisterCall = miniGame
            ? "Console.WriteLine(\"[XEngine] WeChat C# Main\");\n                    ForceJsExportRegister();\n                    SelfHostFromMain();\n                    "
            : string.Empty;
        string forceRegisterMethods = miniGame
            ? """
                [JSImport("log", "xengine")]
                private static partial void HostLog(string message);

                private static void ForceJsExportRegister()
                {
                    void Trace(string message)
                    {
                        string line = "[XEngine] WeChat C# " + message;
                        Console.WriteLine(line);
                        try { HostLog(message); } catch { }
                    }

                    try
                    {
                        Trace("arch=" + RuntimeInformation.OSArchitecture + " wasm=" + (RuntimeInformation.OSArchitecture == Architecture.Wasm));
                        Type? type = typeof(BrowserPlayer).Assembly.GetType("System.Runtime.InteropServices.JavaScript.__GeneratedInitializer");
                        Trace("initType=" + (type != null ? type.FullName : "null"));
                        MethodInfo? register = type?.GetMethod("__Register_", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic);
                        Trace("register=" + (register != null ? "ok" : "null"));
                        register?.Invoke(null, null);
                        Trace("register-invoked");
                    }
                    catch (Exception ex)
                    {
                        Exception inner = ex.InnerException ?? ex;
                        Trace("register-fail " + inner.GetType().Name + ": " + inner.Message);
                    }
                }

                private static void SelfHostFromMain()
                {
                    try
                    {
                        Console.WriteLine("[XEngine] WeChat C# self-host attach");
                        s_host!.AttachDefaultCanvas(preferWebGpu: false);
                        var attach = s_host.LastCanvasAttach;
                        Console.WriteLine("[XEngine] WeChat C# canvas " + attach.Width + "x" + attach.Height + " via " + attach.Source);
                        s_host.Resume();
                        s_host.StartSelfHostFramePump();
                        Console.WriteLine("[XEngine] WeChat C# self-host pump");
                        try
                        {
                            Console.WriteLine("[XEngine] WeChat C# cube begin");
                            s_host.TryLoadSelfHostCubeScene();
                            Console.WriteLine("[XEngine] WeChat C# cube ok");
                        }
                        catch (Exception cubeEx)
                        {
                            Exception cubeInner = cubeEx.InnerException ?? cubeEx;
                            Console.WriteLine("[XEngine] WeChat C# cube fail " + cubeInner.GetType().Name + ": " + cubeInner.Message);
                            if (!string.IsNullOrEmpty(cubeInner.StackTrace))
                                Console.WriteLine("[XEngine] WeChat C# cube stack " + cubeInner.StackTrace);
                        }
                        Console.WriteLine("[XEngine] WeChat C# self-host attached");
                    }
                    catch (Exception ex)
                    {
                        Exception inner = ex.InnerException ?? ex;
                        Console.WriteLine("[XEngine] WeChat C# self-host fail " + inner.GetType().Name + ": " + inner.Message);
                    }
                }

            """
            : string.Empty;
        return $$"""
            using System;
            using System.Runtime.InteropServices.JavaScript;
            {{jsHostUsings}}
            using XEngine.Runtime;
            using XEngine.Runtime.Audio;
            using XEngine.Runtime.Platforms.Browser;
            using XEngine.Runtime.RHI;

            namespace XEngine.Generated.Web;

            public static partial class BrowserPlayer
            {
                private static BrowserPlatformHost? s_host;
                private static GeneratedWebGame? s_game;
                private static double s_lastTimeMs;
                private static bool s_started;

                public static void Main(string[] args)
                {
                    _ = args;
                    Application.ConfigureRuntimePlatform(ApplicationRuntimePlatform.Browser);
                    Application.IsEditor = false;
                    Application.IsPlaying = true;
                    GraphicsBackendSelection.Preferred = {{preferredBackend}};

                    s_host = new BrowserPlatformHost();
                    s_game = new GeneratedWebGame();
                    s_game.InitializePlatformHost({{label}}, 1, 1);
                    BrowserInputRuntime.Ensure();
                    BrowserAudioRuntime.Ensure({{JsonSerializer.Serialize(audioKind)}});
                    {{packageInstall}}s_started = true;
                    {{forceRegisterCall}}
                }

                {{forceRegisterMethods}}

                [JSExport]
                public static void AttachCanvas(int width, int height, int preferWebGpu)
                {
                    EnsureStarted();
                    s_host!.AttachCanvas({{selector}}, width, height, preferWebGpu != 0 && {{allowWebGpu}});
                    s_host.Resume();
                }

                [JSExport]
                public static void SetAssetBase(string href)
                {
                    EnsureStarted();
                    if (string.IsNullOrEmpty(href) || href == "package:")
                        BrowserAssetHost.InstallPackage();
                    else
                        BrowserAssetHost.Install(href);
                }

                [JSExport]
                public static void InstallPackageFile(string path, byte[] data)
                {
                    EnsureStarted();
                    BrowserAssetHost.AddPackageFile(path, data);
                }

                [JSExport]
                public static bool TrySetSetting(string key, string value) =>
                    MiniGameSettingsStore.TrySet(key, value);

                [JSExport]
                public static string GetSetting(string key) => MiniGameSettingsStore.Get(key);

                [JSExport]
                public static void HydrateSetting(string key, string value) =>
                    MiniGameSettingsStore.Hydrate(key, value);

                [JSExport]
                public static string? TakePendingStorageWrite() => MiniGameSettingsStore.TakePendingWrite();

                [JSExport]
                public static void ConfigurePlatform(string locale, bool online)
                {
                    EnsureStarted();
                    BrowserInputRuntime.Configure(locale, online);
                }

                [JSExport]
                public static void OnPointer(int kind, int pointerId, float cssX, float cssY, float cssW, float cssH, int buttons, float pressure, int isTouch)
                {
                    BrowserInputRuntime.NoteUserGesture();
                    BrowserInputRuntime.Handler.HandlePointer(kind, pointerId, cssX, cssY, cssW, cssH, buttons, pressure, isTouch != 0);
                }

                [JSExport]
                public static void OnWheel(float cssX, float cssY, float cssW, float cssH, float deltaY)
                {
                    BrowserInputRuntime.Handler.HandleWheel(cssX, cssY, cssW, cssH, deltaY);
                }

                [JSExport]
                public static void OnKey(string code, int pressed, string key)
                {
                    BrowserInputRuntime.Handler.HandleKey(code, pressed != 0, key);
                }

                [JSExport]
                public static void OnGamepad(int index, int buttonMask, float lx, float ly, float rx, float ry, float lt, float rt)
                {
                    BrowserInputRuntime.Handler.HandleGamepad(index, buttonMask, lx, ly, rx, ry, lt, rt);
                }

                [JSExport]
                public static string GetLastPointer()
                {
                    var p = BrowserInputRuntime.Handler.LastMappedPointer;
                    return ((int)p.X) + "," + ((int)p.Y);
                }

                [JSExport]
                public static string? TakePendingOpenUrl() => BrowserInputRuntime.TakePendingOpenUrl();

                [JSExport]
                public static string? TakePendingClipboardWrite() => BrowserInputRuntime.TakePendingClipboardWrite();

                [JSExport]
                public static string? TakePendingOrientationLock() => BrowserInputRuntime.TakePendingOrientationLock();

                [JSExport]
                public static void NoteScreenOrientationUnsupported() => BrowserInputRuntime.NoteScreenOrientationUnsupported();

                [JSExport]
                public static void ReportScreenOrientation(string orientationType) =>
                    BrowserInputRuntime.ReportScreenOrientation(orientationType);

                [JSExport]
                public static void SetClipboardFromHost(string text) => BrowserInputRuntime.SetClipboardFromHost(text);

                [JSExport]
                public static void InitializeAudio(string kind)
                {
                    EnsureStarted();
                    BrowserAudioRuntime.Ensure(kind);
                }

                [JSExport]
                public static void OnAudioProcess(int frameCount) => BrowserAudioRuntime.Pump((uint)Math.Max(0, frameCount));

                [JSExport]
                public static string? TakePendingInnerAudio() => BrowserAudioRuntime.TakePendingInnerAudio();

                [JSExport]
                public static float GetMasterVolume() => AudioContext.MasterVolume;

                [JSExport]
                public static void SetMasterVolume(float volume) => AudioContext.MasterVolume = volume;

                [JSExport]
                public static void OnAnimationFrame(double timeMs)
                {
                    if (s_host == null)
                        return;
                    float dt = 1f / 60f;
                    if (s_lastTimeMs > 0)
                        dt = (float)Math.Clamp((timeMs - s_lastTimeMs) / 1000.0, 0.0, 0.25);
                    s_lastTimeMs = timeMs;
                    s_host.Tick(dt);
                }

                [JSExport]
                public static void OnResize(int width, int height) => s_host?.ResizeCanvas(width, height);

                [JSExport]
                public static void OnVisibility(bool visible)
                {
                    if (s_host == null)
                        return;
                    if (visible)
                        s_host.Resume();
                    else
                        s_host.Suspend();
                }

                private static void EnsureStarted()
                {
                    if (!s_started)
                        Main([]);
                }
            }

            internal sealed class GeneratedWebGame : Game
            {
            }
            """;
    }
}
