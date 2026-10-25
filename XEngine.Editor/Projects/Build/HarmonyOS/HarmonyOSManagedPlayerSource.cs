// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Text.Json;

namespace XEngine.Editor.Build.HarmonyOS;

/// <summary>Generates the NativeAOT-managed implementation of the stable HarmonyOS player ABI.</summary>
internal static class HarmonyOSManagedPlayerSource
{
    internal static string Create(
        HarmonyOSPlayerProjectOptions options,
        IReadOnlyList<HarmonyOSVerifiedInputFile> managedAssemblies)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(managedAssemblies);
        string label = JsonSerializer.Serialize(options.ApplicationLabel);
        string packagingMode = options.PackagingMode.ToString();
        string defaultSceneGuid = options.DefaultSceneGuid.ToString("D");
        string managedAssemblyNames = CreateManagedAssemblyNames(managedAssemblies);
        return $$"""
            using System;
            using System.Collections.Generic;
            using System.IO;
            using System.Reflection;
            using System.Runtime.CompilerServices;
            using System.Runtime.InteropServices;
            using System.Text;

            using XEngine.Runtime;
            using XEngine.Runtime.AssetBundles;
            using XEngine.Runtime.AssetStreaming;
            using XEngine.Runtime.Platforms.HarmonyOS;
            using XEngine.Runtime.RHI.Shaders;

            namespace XEngine.Generated.HarmonyOS;

            internal static unsafe class HarmonyOSPlayerExports
            {
                private const uint AbiVersion = 8;
                private const ulong MaximumContentRootBytes = 32 * 1024;

                [StructLayout(LayoutKind.Sequential)]
                internal struct PlayerCreateArgs
                {
                    internal uint StructSize;
                    internal uint AbiVersion;
                    internal byte* ContentRoot;
                    internal ulong ContentRootLength;
                    internal delegate* unmanaged[Cdecl]<int, byte*, void*, void> Log;
                    internal void* Context;
                    internal delegate* unmanaged[Cdecl]<ulong, void*, void*, ulong> CreateVulkanSurface;
                    internal delegate* unmanaged[Cdecl]<int, void*, int> SetPreferredOrientation;
                    internal delegate* unmanaged[Cdecl]<int, void*, int> SetKeepScreenOn;
                }

                [UnmanagedCallersOnly(EntryPoint = "xengine_player_abi_version", CallConvs = [typeof(CallConvCdecl)])]
                internal static uint GetAbiVersion() => AbiVersion;

                [UnmanagedCallersOnly(EntryPoint = "xengine_player_create", CallConvs = [typeof(CallConvCdecl)])]
                internal static void* Create(PlayerCreateArgs* args)
                {
                    global::XEngine.Generated.HarmonyOS.XEnginePlayerRoots.Preserve();
                    if (args == null ||
                        args->StructSize != sizeof(PlayerCreateArgs) ||
                        args->AbiVersion != AbiVersion ||
                        args->ContentRoot == null ||
                        args->ContentRootLength == 0 ||
                        args->ContentRootLength > MaximumContentRootBytes ||
                        args->CreateVulkanSurface == null ||
                        args->SetPreferredOrientation == null || args->SetKeepScreenOn == null)
                    {
                        Report(args, 8, "HarmonyOS player create arguments do not match ABI v8.");
                        return null;
                    }

                    try
                    {
                        string contentRoot = Encoding.UTF8.GetString(
                            new ReadOnlySpan<byte>(args->ContentRoot, checked((int)args->ContentRootLength)));
                        if (contentRoot.IndexOf('\0', StringComparison.Ordinal) >= 0)
                            throw new InvalidDataException("Content root contains an embedded null.");
                        contentRoot = Path.GetFullPath(contentRoot);
                        if (!Directory.Exists(contentRoot) ||
                            (File.GetAttributes(contentRoot) & FileAttributes.ReparsePoint) != 0)
                        {
                            throw new DirectoryNotFoundException("Packaged content root is unavailable.");
                        }

                        var state = new PlayerState(args, contentRoot);
                        GCHandle handle = GCHandle.Alloc(state, GCHandleType.Normal);
                        return (void*)GCHandle.ToIntPtr(handle);
                    }
                    catch (Exception exception)
                    {
                        Report(args, 8, "HarmonyOS player creation failed: " + exception.GetType().Name);
                        return null;
                    }
                }

                [UnmanagedCallersOnly(EntryPoint = "xengine_player_attach_surface", CallConvs = [typeof(CallConvCdecl)])]
                internal static int AttachSurface(void* player, void* nativeWindow, int width, int height)
                {
                    if (!TryGetState(player, out PlayerState? state) || nativeWindow == null || width <= 0 || height <= 0)
                        return -1;
                    try
                    {
                        state.Host.AttachSurface((nint)nativeWindow, width, height);
                        return 0;
                    }
                    catch (Exception exception)
                    {
                        state.ReportFailure("surface attach", exception);
                        return -2;
                    }
                }

                [UnmanagedCallersOnly(EntryPoint = "xengine_player_resize_surface", CallConvs = [typeof(CallConvCdecl)])]
                internal static void ResizeSurface(void* player, int width, int height)
                {
                    if (!TryGetState(player, out PlayerState? state) || width <= 0 || height <= 0)
                        return;
                    try { state.Host.ResizeSurface(width, height); }
                    catch (Exception exception) { state.ReportFailure("surface resize", exception); }
                }

                [UnmanagedCallersOnly(EntryPoint = "xengine_player_detach_surface", CallConvs = [typeof(CallConvCdecl)])]
                internal static void DetachSurface(void* player)
                {
                    if (!TryGetState(player, out PlayerState? state))
                        return;
                    try { state.Host.DetachSurface(); }
                    catch (Exception exception) { state.ReportFailure("surface detach", exception); }
                }

                [UnmanagedCallersOnly(EntryPoint = "xengine_player_set_resumed", CallConvs = [typeof(CallConvCdecl)])]
                internal static void SetResumed(void* player, int resumed)
                {
                    if (!TryGetState(player, out PlayerState? state))
                        return;
                    try
                    {
                        if (resumed != 0) state.Host.Resume();
                        else state.Host.Pause();
                    }
                    catch (Exception exception) { state.ReportFailure("lifecycle transition", exception); }
                }

                [UnmanagedCallersOnly(EntryPoint = "xengine_player_touch", CallConvs = [typeof(CallConvCdecl)])]
                internal static void Touch(
                    void* player,
                    int pointerId,
                    int action,
                    float x,
                    float y,
                    float pressure)
                {
                    if (!TryGetState(player, out PlayerState? state))
                        return;
                    try
                    {
                        state.Host.DispatchTouch(
                            pointerId,
                            (HarmonyOSTouchAction)action,
                            x,
                            y,
                            pressure);
                    }
                    catch (Exception exception) { state.ReportFailure("touch input", exception); }
                }

                [UnmanagedCallersOnly(EntryPoint = "xengine_player_key", CallConvs = [typeof(CallConvCdecl)])]
                internal static void Key(void* player, int harmonyOsKeyCode, int pressed)
                {
                    if (!TryGetState(player, out PlayerState? state))
                        return;
                    try { state.Host.HandleKey(harmonyOsKeyCode, pressed != 0); }
                    catch (Exception exception) { state.ReportFailure("physical key input", exception); }
                }

                [UnmanagedCallersOnly(EntryPoint = "xengine_player_mouse", CallConvs = [typeof(CallConvCdecl)])]
                internal static void Mouse(void* player, int action, int button, float x, float y)
                {
                    if (!TryGetState(player, out PlayerState? state))
                        return;
                    try { state.Host.HandleMouse(action, button, x, y); }
                    catch (Exception exception) { state.ReportFailure("physical mouse input", exception); }
                }

                [UnmanagedCallersOnly(EntryPoint = "xengine_player_mouse_wheel", CallConvs = [typeof(CallConvCdecl)])]
                internal static void MouseWheel(void* player, float delta)
                {
                    if (!TryGetState(player, out PlayerState? state))
                        return;
                    try { state.Host.HandleMouseWheel(delta); }
                    catch (Exception exception) { state.ReportFailure("mouse wheel input", exception); }
                }

                [UnmanagedCallersOnly(EntryPoint = "xengine_player_focus", CallConvs = [typeof(CallConvCdecl)])]
                internal static void Focus(void* player, int focused)
                {
                    if (!TryGetState(player, out PlayerState? state))
                        return;
                    try { state.Host.FocusChanged(focused != 0); }
                    catch (Exception exception) { state.ReportFailure("focus transition", exception); }
                }

                [UnmanagedCallersOnly(EntryPoint = "xengine_player_tick", CallConvs = [typeof(CallConvCdecl)])]
                internal static void Tick(void* player, long frameTimeNanos)
                {
                    if (!TryGetState(player, out PlayerState? state))
                        return;
                    try { state.Host.Tick(frameTimeNanos); }
                    catch (Exception exception) { state.ReportFailure("frame tick", exception); }
                }

                [UnmanagedCallersOnly(EntryPoint = "xengine_player_text_input_active", CallConvs = [typeof(CallConvCdecl)])]
                internal static int TextInputActive(void* player)
                {
                    if (!TryGetState(player, out PlayerState? state))
                        return 0;
                    try { return state.Host.InputHandler.LastTextInputActive ? 1 : 0; }
                    catch (Exception exception)
                    {
                        state.ReportFailure("text input query", exception);
                        return 0;
                    }
                }

                [UnmanagedCallersOnly(EntryPoint = "xengine_player_text_input_type", CallConvs = [typeof(CallConvCdecl)])]
                internal static int TextInputType(void* player)
                {
                    if (!TryGetState(player, out PlayerState? state))
                        return 0;
                    try { return state.Host.InputHandler.LastHarmonyOSInputType; }
                    catch (Exception exception)
                    {
                        state.ReportFailure("text input type query", exception);
                        return 0;
                    }
                }

                [UnmanagedCallersOnly(EntryPoint = "xengine_player_set_display_orientation", CallConvs = [typeof(CallConvCdecl)])]
                internal static void SetDisplayOrientation(void* player, int orientation)
                {
                    if (orientation is < 1 or > 4)
                        return;
                    try { XEngine.Runtime.Screen.ReportDeviceOrientation((ScreenOrientation)orientation); }
                    catch
                    {
                        // Orientation reports must never take the player down.
                    }
                }

                [UnmanagedCallersOnly(EntryPoint = "xengine_player_destroy", CallConvs = [typeof(CallConvCdecl)])]
                internal static void Destroy(void* player)
                {
                    if (player == null)
                        return;
                    GCHandle handle;
                    try { handle = GCHandle.FromIntPtr((nint)player); }
                    catch { return; }
                    try
                    {
                        if (handle.Target is PlayerState state)
                            state.Dispose();
                    }
                    catch { }
                    finally
                    {
                        if (handle.IsAllocated)
                            handle.Free();
                    }
                }

                private static bool TryGetState(void* player, out PlayerState? state)
                {
                    state = null;
                    if (player == null)
                        return false;
                    try
                    {
                        state = GCHandle.FromIntPtr((nint)player).Target as PlayerState;
                        return state != null;
                    }
                    catch
                    {
                        return false;
                    }
                }

                private static void Report(PlayerCreateArgs* args, int severity, string message)
                {
                    if (args == null || args->Log == null)
                        return;
                    Report(args->Log, args->Context, severity, message);
                }

                private static void Report(
                    delegate* unmanaged[Cdecl]<int, byte*, void*, void> callback,
                    void* context,
                    int severity,
                    string message)
                {
                    if (callback == null)
                        return;
                    string bounded = message.Length <= 4096 ? message : message[..4096];
                    byte[] utf8 = new byte[Encoding.UTF8.GetByteCount(bounded) + 1];
                    Encoding.UTF8.GetBytes(bounded.AsSpan(), utf8.AsSpan());
                    fixed (byte* text = utf8)
                        callback(severity, text, context);
                }

                private sealed class PlayerState : IDisposable
                {
                    private readonly delegate* unmanaged[Cdecl]<int, byte*, void*, void> _log;
                    private readonly void* _context;
                    private readonly delegate* unmanaged[Cdecl]<ulong, void*, void*, ulong> _createVulkanSurface;
                    private readonly delegate* unmanaged[Cdecl]<int, void*, int> _setPreferredOrientation;
                    private readonly delegate* unmanaged[Cdecl]<int, void*, int> _setKeepScreenOn;
                    private ContentStreamProvider? _contentProvider;
                    private PlayerAssetBackendBase? _assetBackend;
                    private HarmonyOSPlatformHost? _host;
                    private IDisposable? _platformServicesRegistration;
                    private bool _disposed;

                    internal PlayerState(PlayerCreateArgs* args, string contentRoot)
                    {
                        _log = args->Log;
                        _context = args->Context;
                        _createVulkanSurface = args->CreateVulkanSurface;
                        _setPreferredOrientation = args->SetPreferredOrientation;
                        _setKeepScreenOn = args->SetKeepScreenOn;
                        try
                        {
                            // HarmonyOS ships no libcrypto; System.Security.Cryptography.SHA256
                            // statically depends on it and aborts on first use. Catalog checksums
                            // are verified at build time and the HAP is install-signed, so skip
                            // the in-process checksum here.
                            ShaderArtifactCatalog.VerifyChecksumOnRead = false;
                            Application.ConfigureRuntimePlatform(ApplicationRuntimePlatform.HarmonyOS);
                            XEngine.Runtime.Application.DataPath = contentRoot;
                            // resfile maps into the read-only el1 bundle; derive the writable
                            // el2 sandbox roots for persistent/cache data (content updates).
                            string persistentRoot = contentRoot.Replace("/el1/bundle/", "/el2/base/haps/").Replace("/resources/resfile", "/files");
                            string cacheRoot = contentRoot.Replace("/el1/bundle/", "/el2/base/haps/").Replace("/resources/resfile", "/cache");
                            try { System.IO.Directory.CreateDirectory(persistentRoot); } catch { persistentRoot = contentRoot; }
                            try { System.IO.Directory.CreateDirectory(cacheRoot); } catch { cacheRoot = persistentRoot; }
                            Application.ConfigurePlatformPaths(
                                contentRoot, contentRoot, persistentRoot, cacheRoot, System.IO.Path.Combine(persistentRoot, "NoBackup"));
                            _contentProvider = new ContentStreamProvider(contentRoot);
                            _assetBackend = CreateAssetBackend(_contentProvider, contentRoot);
                            ConfigureShaders(_contentProvider, contentRoot);
                            _host = new HarmonyOSPlatformHost(
                                new HarmonyOSGraphicsHost(),
                                CreateVulkanSurface,
                                deferOwnerThreadBinding: true);
                            _platformServicesRegistration = ConfigurePlatformServices();
                            // Apply orientation before the first surface is attached. Settings live
                            // in the HAP VFS, so the filesystem-only Apply(...) call in Initialize
                            // is intentionally kept as the full post-scene settings pass.
                            PlayerSettingsLoader.ApplyOrientation(_contentProvider!);
                            var game = new HarmonyOSGame(
                                _assetBackend,
                                new Guid("{{defaultSceneGuid}}"),
                                [{{managedAssemblyNames}}]);
                            game.InitializePlatformHost({{label}}, 1, 1);
                            XEngine.Runtime.Debug.OnLog += ForwardLog;
                        }
                        catch
                        {
                            Dispose();
                            throw;
                        }
                    }

                    /// <summary>
                    /// Registers platform services; the ABI currently reaches the host for screen
                    /// orientation only, so the other members carry managed fallbacks (the window
                    /// glue gains native callbacks as the ABI grows).
                    /// </summary>
                    private IDisposable ConfigurePlatformServices() => PlatformServices.Configure(
                        new HarmonyOSPlatformServices(
                            localeName: static () => System.Globalization.CultureInfo.CurrentCulture.Name,
                            timeZoneId: static () => System.TimeZoneInfo.Local.Id,
                            networkReachability: static () => PlatformNetworkReachability.Unknown,
                            openUrl: static _ => false,
                            setKeepScreenOn: enabled => _setKeepScreenOn(enabled ? 1 : 0, _context) == 0,
                            vibrate: static _ => false,
                            getPermissionStatus: static _ => PlatformPermissionStatus.Unsupported,
                            shouldShowPermissionRationale: static _ => false,
                            requestPermissionAsync: static (_, _) =>
                                System.Threading.Tasks.ValueTask.FromResult(PlatformPermissionStatus.Unsupported),
                            openApplicationSettings: static () => false,
                            trySetScreenOrientation: (requested, policy) =>
                            {
                                // Pack request + allowed-orientation bits for the ArkTS poller
                                // (see xengine_player_abi.h).
                                int packed = (int)requested;
                                if (policy.AutoRotateToPortrait) packed |= 1 << 8;
                                if (policy.AutoRotateToPortraitUpsideDown) packed |= 1 << 9;
                                if (policy.AutoRotateToLandscapeLeft) packed |= 1 << 10;
                                if (policy.AutoRotateToLandscapeRight) packed |= 1 << 11;
                                _setPreferredOrientation(packed, _context);
                                return true;
                            }));

                    internal HarmonyOSPlatformHost Host => _host
                        ?? throw new ObjectDisposedException(nameof(PlayerState));

                    internal void ReportFailure(string operation, Exception exception) =>
                        Report(_log, _context, 8, operation + " failed: " + exception.GetType().Name);

                    private nint CreateVulkanSurface(nint instance, nint nativeWindow)
                    {
                        ulong result = _createVulkanSurface(
                            (ulong)(nuint)instance,
                            (void*)nativeWindow,
                            _context);
                        return (nint)(nuint)result;
                    }

                    private void ForwardLog(string message, DebugStackTrace? _, LogSeverity severity) =>
                        Report(_log, _context, (int)severity, message);

                    public void Dispose()
                    {
                        if (_disposed)
                            return;
                        _disposed = true;
                        XEngine.Runtime.Debug.OnLog -= ForwardLog;
                        try
                        {
                            _platformServicesRegistration?.Dispose();
                        }
                        finally
                        {
                            try
                            {
                                _host?.Dispose();
                            }
                            finally
                            {
                                try { _assetBackend?.Dispose(); }
                                finally { _contentProvider?.Dispose(); }
                                _host = null;
                                _assetBackend = null;
                                _contentProvider = null;
                            }
                        }
                    }

                    private static PlayerAssetBackendBase CreateAssetBackend(
                        ContentStreamProvider provider,
                        string contentRoot)
                    {
                        const AssetPackagingMode mode = AssetPackagingMode.{{packagingMode}};
                        if (mode != AssetPackagingMode.AssetBundle)
                            return new PlayerAssetBackend(mode, provider, "Content");

                        IReadOnlyList<string> packageRoots = FindAssetBundleRoots(contentRoot);
                        return BundleAssetBackend.CreateFromProvider(
                            provider,
                            packageRoots,
                            new Guid("{{defaultSceneGuid}}"),
                            AssetBundlePlatform.HarmonyOS)
                            ?? throw new InvalidDataException("HarmonyOS AssetBundle payload has no packages.");
                    }

                    private static IReadOnlyList<string> FindAssetBundleRoots(string contentRoot)
                    {
                        string directory = Path.Combine(contentRoot, "Content");
                        string[] candidates = Directory.Exists(directory)
                            ? Directory.GetDirectories(directory, "*", SearchOption.TopDirectoryOnly)
                            : [];
                        Array.Sort(candidates, StringComparer.Ordinal);
                        var roots = new List<string>(candidates.Length);
                        for (int i = 0; i < candidates.Length; i++)
                        {
                            if (Directory.GetFiles(candidates[i], "*.bytes", SearchOption.TopDirectoryOnly).Length != 1)
                                continue;
                            roots.Add("Content/" + Path.GetFileName(candidates[i]));
                        }
                        return roots.AsReadOnly();
                    }

                    private static void ConfigureShaders(ContentStreamProvider provider, string contentRoot)
                    {
                        string catalog = Path.Combine(contentRoot, "Shaders", "catalog.xshadercatalog");
                        if (File.Exists(catalog))
                            ShaderArtifactRuntimeLibrary.Configure(provider, "Shaders");
                    }
                }

                private sealed class ContentStreamProvider : IAssetStreamProvider
                {
                    private readonly DirectoryAssetStreamProvider _inner;

                    internal ContentStreamProvider(string contentRoot) =>
                        _inner = new DirectoryAssetStreamProvider(contentRoot);

                    public string Description => "HarmonyOS packaged content";
                    public Stream? OpenRead(string path) => _inner.OpenRead(path);
                    public IReadOnlyList<string> GetFiles(string directory, string extension) =>
                        _inner.GetFiles(directory, extension);
                    public void Dispose() => _inner.Dispose();
                }

                private sealed class HarmonyOSGame : Game
                {
                    private readonly PlayerAssetBackendBase _assetBackend;
                    private readonly Guid _expectedSceneGuid;
                    private readonly string[] _managedAssemblyNames;

                    internal HarmonyOSGame(
                        PlayerAssetBackendBase assetBackend,
                        Guid expectedSceneGuid,
                        string[] managedAssemblyNames)
                    {
                        _assetBackend = assetBackend;
                        _expectedSceneGuid = expectedSceneGuid;
                        _managedAssemblyNames = managedAssemblyNames;
                    }

                    public override void Initialize()
                    {
                        Application.IsPlaying = true;
                        Application.IsEditor = false;
                        Application.IsPlayerBuild = true;
                        ShowFpsOverlay = {{(options.ShowFpsOverlay ? "true" : "false")}};
                        for (int i = 0; i < _managedAssemblyNames.Length; i++)
                        {
                            try { Assembly.Load(new AssemblyName(_managedAssemblyNames[i])); }
                            catch (Exception exception)
                            {
                                XEngine.Runtime.Debug.LogWarning(
                                    "[HarmonyOS] Static managed assembly activation failed: " + exception.GetType().Name);
                            }
                        }

                        AssetDatabase.Current = _assetBackend;
                        GameResources.Initialize(_assetBackend.ResourcesMap);
                        string settingsDirectory = Path.Combine(Application.DataPath, "Settings");
                        PlayerSettingsLoader.ApplyAssetConfig(settingsDirectory);

                        Guid sceneGuid = _assetBackend.DefaultSceneGuid;
                        if (_expectedSceneGuid != Guid.Empty && sceneGuid != _expectedSceneGuid)
                            sceneGuid = _expectedSceneGuid;
                        if (sceneGuid != Guid.Empty)
                        {
                            XEngine.Runtime.Resources.Scene? scene = _assetBackend.LoadScene(sceneGuid);
                            if (scene != null)
                                XEngine.Runtime.Resources.Scene.Load(scene);
                            else
                                XEngine.Runtime.Debug.LogError("[HarmonyOS] Default scene could not be loaded.");
                        }
                        PlayerSettingsLoader.Apply(settingsDirectory);
                    }

                    public override void OnUpdate(XEngine.Runtime.Resources.Scene? scene)
                    {
                        _assetBackend.TickIdleSweep();
                        base.OnUpdate(scene);
                    }
                }
            }
            """;
    }

    private static string CreateManagedAssemblyNames(IReadOnlyList<HarmonyOSVerifiedInputFile> assemblies)
    {
        var result = new StringBuilder();
        for (int i = 0; i < assemblies.Count; i++)
        {
            if (i != 0)
                result.Append(", ");
            result.Append(JsonSerializer.Serialize(Path.GetFileNameWithoutExtension(assemblies[i].RelativePath)));
        }
        return result.ToString();
    }
}
