// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Reflection;
using System;
using System.IO;
using System.Runtime.InteropServices;

using XEngine.Runtime.Audio.Native;

namespace XEngine.Runtime.Audio;

/// <summary>Desktop / Android / iOS output via <c>miniaudioex</c>. Not used on WASM.</summary>
public sealed class MiniAudioBackend : IAudioBackend
{
    private IntPtr _context;
    private unsafe delegate* unmanaged[Cdecl]<ma_device_ptr, IntPtr, IntPtr, uint, void> _deviceDataProc;
    private float _masterVolume = 1f;
    private bool _playbackActive;

    public string Name => "MiniAudio";
    public bool IsInitialized => _context != IntPtr.Zero;
    public int SampleRate { get; private set; } = 44100;
    public int Channels { get; private set; } = 2;
    public IntPtr NativeHandle => _context;
    public bool IsPlaybackActive => _playbackActive;
    public AudioBackendCapabilities Capabilities =>
        AudioBackendCapabilities.BusRouting | AudioBackendCapabilities.BusEffects | AudioBackendCapabilities.Metering;

    public float MasterVolume
    {
        get
        {
            if (_context == IntPtr.Zero)
                return _masterVolume;
            return MiniAudioExNative.ma_ex_context_get_master_volume(_context);
        }
        set
        {
            _masterVolume = value;
            if (_context != IntPtr.Zero)
                MiniAudioExNative.ma_ex_context_set_master_volume(_context, value);
        }
    }

    public bool Initialize(uint sampleRate, uint channels, uint periodSizeInFrames, DeviceInfo? deviceInfo)
    {
        if (_context != IntPtr.Zero)
            return true;

        PreloadOhosNativeLibrary();

        try
        {
            SampleRate = (int)sampleRate;
            Channels = (int)channels;

            ma_ex_device_info pDeviceInfo = new ma_ex_device_info();
            pDeviceInfo.index = deviceInfo == null ? -1 : deviceInfo.Index;
            pDeviceInfo.pName = IntPtr.Zero;
            pDeviceInfo.nativeDataFormatCount = 0;
            pDeviceInfo.nativeDataFormats = IntPtr.Zero;

            ma_ex_context_config contextConfig = MiniAudioExNative.ma_ex_context_config_init(
                sampleRate,
                (byte)channels,
                periodSizeInFrames,
                ref pDeviceInfo);

            unsafe
            {
                _deviceDataProc = &OnDeviceDataProc;
                contextConfig.deviceDataProc = _deviceDataProc;
            }

            _context = MiniAudioExNative.ma_ex_context_init(ref contextConfig);
            if (_context == IntPtr.Zero)
            {
                if (IsOhos)
                {
                    // hilog/stderr are both invisible from the module namespace on OHOS;
                    // the native side reports its failure stage through the diag readback.
                    int stage = MiniAudioExNative.ma_ex_ohos_get_diag();
                    string detail = MiniAudioExNative.ma_ex_ohos_get_diag_detail();
                    XEngine.Runtime.Debug.LogWarning($"[MiniAudioBackend] context init failed: diag={stage} detail={detail}");
                }
                return false;
            }
        }
        catch (Exception e)
        {
            // Surface the real native-load/init reason: TryInitialize callers swallow these.
            XEngine.Runtime.Debug.LogWarning("[MiniAudioBackend] initialize failed: " + e.Message);
            throw;
        }

        _playbackActive = true;
        MiniAudioExNative.ma_ex_context_set_master_volume(_context, _masterVolume);
        return true;
    }

    /// <summary>True in OpenHarmony player builds. The ohos runtime RID is linux-musl-arm64, so
    /// OSPlatform checks identify as Linux — gate on the player compile define instead.</summary>
    private static bool IsOhos =>
#if XENGINE_HARMONYOS
        true;
#else
        OperatingSystem.IsOSPlatform("OHOS");
#endif

    /// <summary>OpenHarmony: the HAP libs directory is not on the default dlopen search path,
    /// so locate the player library's directory via /proc/self/maps and load
    /// libminiaudioex.so explicitly; later DllImports then resolve against it.</summary>
    private static IntPtr _ohosLibHandle;

    /// <summary>Resolves the "miniaudioex" DllImports to the preloaded handle: OHOS
    /// namespace-isolated dlopen does not match absolute-path-loaded libraries by soname.</summary>
    private static IntPtr OhosImportResolver(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        XEngine.Runtime.Debug.LogWarning($"[MiniAudioBackend] resolver: {libraryName} bound={_ohosLibHandle != IntPtr.Zero}");
        if (libraryName == "miniaudioex" && _ohosLibHandle != IntPtr.Zero)
            return _ohosLibHandle;
        return IntPtr.Zero;
    }

    private static void PreloadOhosNativeLibrary()
    {
        if (!IsOhos)
            return;
        try
        {
            string? libDir = null;
            foreach (string line in File.ReadLines("/proc/self/maps"))
            {
                int idx = line.IndexOf("libxengine_player.so", StringComparison.Ordinal);
                if (idx < 0)
                    continue;
                string path = line.Substring(line.LastIndexOf(' ') + 1).Trim();
                if (File.Exists(path))
                {
                    libDir = Path.GetDirectoryName(path);
                    break;
                }
            }

            if (libDir == null)
            {
                XEngine.Runtime.Debug.LogWarning("[MiniAudioBackend] player library dir not found in /proc/self/maps");
                return;
            }

            bool loaded = NativeLibrary.TryLoad(Path.Combine(libDir, "libminiaudioex.so"), out IntPtr handle);
            _ohosLibHandle = loaded ? handle : IntPtr.Zero;
            XEngine.Runtime.Debug.Log($"[MiniAudioBackend] preload libminiaudioex.so from {libDir}: {loaded}");

            if (loaded)
            {
                // Bind the "miniaudioex" DllImports to the preloaded handle. OHOS namespace-
                // isolated dlopen never matches an absolute-path-loaded library by soname, so
                // without this every P/Invoke probes /system/bin and fails.
                NativeLibrary.SetDllImportResolver(typeof(MiniAudioBackend).Assembly, OhosImportResolver);
                XEngine.Runtime.Debug.Log("[MiniAudioBackend] import resolver registered");
            }
        }
        catch (Exception e)
        {
            XEngine.Runtime.Debug.LogWarning("[MiniAudioBackend] preload failed: " + e.Message);
        }
    }

    public void Shutdown()
    {
        if (_context == IntPtr.Zero)
            return;
        MiniAudioExNative.ma_ex_context_uninit(_context);
        _context = IntPtr.Zero;
        _playbackActive = false;
        AudioContext.ClearDeviceCallbackTimestamp();
    }

    public bool SetPlaybackActive(bool active)
    {
        IntPtr context = _context;
        if (context == IntPtr.Zero)
            return false;
        IntPtr engine = MiniAudioExNative.ma_ex_context_get_engine(context);
        if (engine == IntPtr.Zero)
            return false;
        ma_result result;
        if (IsOhos)
        {
            // OHOS: the OHAudio renderer owns the output path; ma_engine start/stop would
            // only drive the inert null device. The flag gates the renderer write callback.
            MiniAudioExNative.ma_ex_ohos_set_playback_active(active ? 1 : 0);
            result = ma_result.success;
        }
        else        {
            result = active
                ? MiniAudioExNative.ma_engine_start(engine)
                : MiniAudioExNative.ma_engine_stop(engine);
        }
        AudioContext.ClearDeviceCallbackTimestamp();
        if (result != ma_result.success)
            return false;
        _playbackActive = active;
        return true;
    }

    public void Update() { }

    public void Dispose() => Shutdown();

    [UnmanagedCallersOnly(CallConvs = [typeof(System.Runtime.CompilerServices.CallConvCdecl)])]
    private static void OnDeviceDataProc(ma_device_ptr pDevice, IntPtr pOutput, IntPtr pInput, uint frameCount)
    {
        _ = pInput;
        IntPtr pEngine = MiniAudioExNative.ma_ex_device_get_user_data(pDevice.pointer);
        MiniAudioExNative.ma_engine_read_pcm_frames(pEngine, pOutput, frameCount, out _);
        NativeArray<float> buffer = new NativeArray<float>(pOutput, (int)(frameCount * (uint)AudioContext.Channels));
        AudioContext.HandleDeviceData(buffer, frameCount);
    }
}
