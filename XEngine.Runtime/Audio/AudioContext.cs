// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;

using XEngine.Runtime.Audio.Native;
using XEngine.Runtime.Resources;

namespace XEngine.Runtime.Audio;

public delegate void DeviceDataEvent(NativeArray<float> data, UInt32 frameCount);

/// <summary>
/// This class is responsible for managing the audio context.
/// </summary>
public static class AudioContext
{
    private static IAudioBackend s_backend = CreateDefaultBackend();
    private static Dictionary<UInt64, IntPtr> audioClipHandles = new Dictionary<UInt64, IntPtr>();
    // Ref-count per shared handle: clips with identical data share one native allocation, so it must
    // only be freed once the last clip using it is disposed (otherwise: double-free / use-after-free).
    private static Dictionary<UInt64, int> audioClipRefCounts = new Dictionary<UInt64, int>();
    // Guards the two dictionaries above. Add/AddRef/Remove are called from the main thread during
    // normal loading, but AudioClip getting a GC finalizer means Remove can now also run on the
    // dedicated finalizer thread concurrently - a plain Dictionary would corrupt under that race.
    private static readonly object clipTableLock = new();
    private static AudioBuffer outputBuffer = new AudioBuffer(8192);

    // Retired PCM buffers are freed on the MAIN thread after a grace period. Freeing from
    // Remove() (which can run on the GC finalizer thread while the device callback or a
    // one-shot voice is still reading the buffer) caused audio-thread access violations
    // (0xC0000005) once frequent one-shots mixed with long clips.
    private sealed class RetiredBuffer
    {
        public IntPtr Handle;
        public long RetiredAtTimestamp;
    }

    private static readonly List<RetiredBuffer> retiredBuffers = new();
    private static readonly object retiredLock = new();
    private static readonly long RetiredGraceTicks = 5L * Stopwatch.Frequency;

    private static UInt32 sampleRate = 44100;
    private static UInt32 channels = 2;
    private static DateTime lastUpdateTime;
    private static float deltaTime;
    private static long lastDeviceCallbackTimestamp;
    private static long underrunCount;

    public static event DeviceDataEvent DataProcess;

    [OnAssemblyUnload]
    internal static void ClearDataProcessEvent() => DataProcess = null;

    public static IAudioBackend Backend => s_backend;

    internal static void ClearDeviceCallbackTimestamp() =>
        Interlocked.Exchange(ref lastDeviceCallbackTimestamp, 0);

    internal static IntPtr NativeContext => s_backend.NativeHandle;

    /// <summary>
    /// Gets the chosen sample rate.
    /// </summary>
    /// <value></value>
    public static Int32 SampleRate
    {
        get
        {
            return (int)sampleRate;
        }
    }

    public static Int32 Channels
    {
        get
        {
            return (int)channels;
        }
    }

    public static bool IsInitialized => s_backend.IsInitialized;

    public static long UnderrunCount => Interlocked.Read(ref underrunCount);

    /// <summary>
    /// Controls the master volume.
    /// </summary>
    /// <value></value>
    public static float MasterVolume
    {
        get => s_backend.MasterVolume;
        set => s_backend.MasterVolume = value;
    }

    /// <summary>
    /// The elapsed time since last call to 'Update'.
    /// </summary>
    /// <value></value>
    public static float DeltaTime
    {
        get
        {
            return deltaTime;
        }
    }

    /// <summary>Replaces the process audio backend. Shuts down the previous backend if it was running.</summary>
    public static void Configure(IAudioBackend backend)
    {
        ArgumentNullException.ThrowIfNull(backend);
        if (ReferenceEquals(s_backend, backend))
            return;
        if (s_backend.IsInitialized)
            Deinitialize();
        s_backend = backend;
    }

    internal static void ResetBackendForTests()
    {
        if (s_backend.IsInitialized)
            Deinitialize();
        s_backend = CreateDefaultBackend();
    }

    private static IAudioBackend CreateDefaultBackend()
    {
#if XENGINE_WEB
        return new WebAudioBackend();
#else
        return new MiniAudioBackend();
#endif
    }

    /// <summary>
    /// Initializes the current <see cref="IAudioBackend"/>. Desktop uses miniaudio; WASM uses Web Audio
    /// or InnerAudio and must not P/Invoke <c>miniaudioex</c>.
    /// </summary>
    /// <param name="sampleRate">The sample rate to use. Typical sampling rates are 44100 and 48000.</param>
    /// <param name="channels">The number of channels to use. For most purposes 2 is the best choice (stereo audio).</param>
    /// <param name="periodSizeInFrames">Buffer size for audio processing. This value is a 'hint' so in practice it may be different than what you passed.</param>
    /// <param name="deviceInfo">If left null, a default device is used.</param>
    public static void Initialize(UInt32 sampleRate, UInt32 channels, UInt32 periodSizeInFrames = 2048, DeviceInfo deviceInfo = null)
    {
        if (s_backend.IsInitialized)
            return;

        AudioContext.sampleRate = sampleRate;
        AudioContext.channels = channels;

        if (!s_backend.Initialize(sampleRate, channels, periodSizeInFrames, deviceInfo))
            Console.WriteLine("Failed to initialize audio backend " + s_backend.Name);

        // Native mixer bus graphs rebuild lazily against the fresh engine (N2 device recovery).
        AudioMixerInstance.OnBackendInitialized();

        lastUpdateTime = DateTime.Now;
    }

    /// <summary>Initializes audio when the native backend is present for the process ABI.</summary>
    public static bool TryInitialize(
        UInt32 sampleRate,
        UInt32 channels,
        UInt32 periodSizeInFrames = 2048,
        DeviceInfo deviceInfo = null)
    {
        try
        {
            Initialize(sampleRate, channels, periodSizeInFrames, deviceInfo);
            return s_backend.IsInitialized;
        }
        catch (DllNotFoundException)
        {
            return false;
        }
        catch (EntryPointNotFoundException)
        {
            return false;
        }
        catch (BadImageFormatException)
        {
            return false;
        }
    }

    /// <summary>Starts or stops playback without destroying clip or device state.</summary>
    internal static bool SetPlaybackActive(bool active) => s_backend.SetPlaybackActive(active);

    /// <summary>
    /// Deinitializes the current audio backend. Call this before closing the application.
    /// </summary>
    public static void Deinitialize()
    {
        if (!s_backend.IsInitialized)
            return;

        lock (clipTableLock)
        {
            foreach (var audioClipHandle in audioClipHandles.Values)
            {
                if (audioClipHandle != IntPtr.Zero)
                    Marshal.FreeHGlobal(audioClipHandle);
            }

            audioClipHandles.Clear();
            audioClipRefCounts.Clear();
        }

        lock (retiredLock)
        {
            foreach (var retired in retiredBuffers)
                if (retired.Handle != IntPtr.Zero)
                    Marshal.FreeHGlobal(retired.Handle);
            retiredBuffers.Clear();
        }

        // Teardown native mixer bus graphs while the engine can still uninit nodes.
        AudioMixerInstance.OnBackendShutdown();

        s_backend.Shutdown();
        Interlocked.Exchange(ref lastDeviceCallbackTimestamp, 0);
    }

    /// <summary>
    /// Used to calculate delta time and move messages from the audio thread to the main thread. Call this method from within your main thread loop.
    /// </summary>
    public static void Update()
    {
        // N1: pick up legacy direct mutations of AudioMixer assets (editor inspector edits,
        // hand-written code) once per frame and propagate them to registered sources. Runs even
        // without an initialized backend so editor tooling and headless flows stay live.
        AudioMixerInstance.TickAll();

        if (!s_backend.IsInitialized)
            return;

        DateTime currentTime = DateTime.Now;
        TimeSpan dt = currentTime - lastUpdateTime;
        deltaTime = (float)dt.TotalSeconds;

        lastUpdateTime = currentTime;
        s_backend.Update();

        DrainRetiredBuffers();
    }

    private static void DrainRetiredBuffers()
    {
        lock (retiredLock)
        {
            if (retiredBuffers.Count == 0)
                return;
            long now = Stopwatch.GetTimestamp();
            for (int i = retiredBuffers.Count - 1; i >= 0; i--)
            {
                var entry = retiredBuffers[i];
                if (now - entry.RetiredAtTimestamp < RetiredGraceTicks)
                    continue; // still inside the grace window, keep order
                if (entry.Handle != IntPtr.Zero)
                    Marshal.FreeHGlobal(entry.Handle);
                retiredBuffers.RemoveAt(i);
            }
        }
    }

    /// <summary>
    /// Gets an array of available playback devices. Retrieving devices is a relatively slow operation, so don't call it continuously.
    /// </summary>
    /// <returns>An array with playback devices</returns>
    public static DeviceInfo[] GetDevices()
    {
        if (s_backend is not MiniAudioBackend)
            return Array.Empty<DeviceInfo>();

        IntPtr pDevices = MiniAudioExNative.ma_ex_playback_devices_get(out UInt32 count);

        if (pDevices == IntPtr.Zero)
            return null;

        if (count == 0)
        {
            MiniAudioExNative.ma_ex_playback_devices_free(pDevices, count);
            return null;
        }

        DeviceInfo[] devices = new DeviceInfo[count];

        for (UInt32 i = 0; i < count; i++)
        {
            IntPtr elementPtr = IntPtr.Add(pDevices, (int)i * Marshal.SizeOf<ma_ex_device_info>());
            ma_ex_device_info deviceInfo = Marshal.PtrToStructure<ma_ex_device_info>(elementPtr);
            devices[i] = new DeviceInfo(deviceInfo.pName, deviceInfo.index, deviceInfo.isDefault > 0 ? true : false, deviceInfo.nativeDataFormats, deviceInfo.nativeDataFormatCount);
        }

        MiniAudioExNative.ma_ex_playback_devices_free(pDevices, count);

        return devices;
    }

    internal static void Add(AudioClip clip)
    {
        if (clip.Hash == 0)
            return;

        if (clip.Handle == IntPtr.Zero)
            return;

        lock (clipTableLock)
        {
            if (audioClipHandles.ContainsKey(clip.Hash))
                return;

            audioClipHandles.Add(clip.Hash, clip.Handle);
            audioClipRefCounts[clip.Hash] = 1;
        }
    }

    /// <summary>Register another clip sharing an already-cached handle (increments its ref-count).</summary>
    internal static void AddRef(UInt64 hash)
    {
        lock (clipTableLock)
        {
            if (audioClipRefCounts.TryGetValue(hash, out int count))
                audioClipRefCounts[hash] = count + 1;
        }
    }

    internal static void Remove(AudioClip clip)
    {
        // Raw accessors: this runs from OnDispose(), where IsDisposed is already true, so the
        // checked public Hash/Handle properties would themselves throw here.
        UInt64 hash = clip.RawHash;
        if (hash == 0)
            return;

        lock (clipTableLock)
        {
            if (!audioClipRefCounts.TryGetValue(hash, out int count))
                return;

            // Only release the shared native allocation when the last user drops it — and
            // even then defer the actual free to the main-thread drain (mixer may still
            // be reading the buffer).
            if (count <= 1)
            {
                if (audioClipHandles.TryGetValue(hash, out IntPtr handle) && handle != IntPtr.Zero)
                {
                    lock (retiredLock)
                        retiredBuffers.Add(new RetiredBuffer { Handle = handle, RetiredAtTimestamp = Stopwatch.GetTimestamp() });
                }
                audioClipHandles.Remove(hash);
                audioClipRefCounts.Remove(hash);
            }
            else
            {
                audioClipRefCounts[hash] = count - 1;
            }
        }
    }

    internal static bool GetAudioClipHandle(UInt64 hashcode, out IntPtr handle)
    {
        lock (clipTableLock)
        {
            return audioClipHandles.TryGetValue(hashcode, out handle);
        }
    }

    /// <summary>Engine callback from miniaudio, Web Audio ScriptProcessor, or tests.</summary>
    internal static void HandleDeviceData(NativeArray<float> buffer, uint frameCount)
    {
        long now = Stopwatch.GetTimestamp();
        long previous = Interlocked.Exchange(ref lastDeviceCallbackTimestamp, now);
        if (previous != 0 && sampleRate > 0)
        {
            double expectedSeconds = (double)frameCount / sampleRate;
            double elapsedSeconds = (double)(now - previous) / Stopwatch.Frequency;
            if (elapsedSeconds > expectedSeconds * 2.5d)
                Interlocked.Increment(ref underrunCount);
        }

        DataProcess?.Invoke(buffer, frameCount);
        outputBuffer.Write(buffer);
    }

    public static bool GetOutputBuffer(ref float[] buffer, out int length)
    {
        length = outputBuffer.Read(ref buffer);
        return length > 0;
    }
}

public enum AttenuationModel
{
    None,
    Inverse,
    Linear,
    Exponential
}

public enum PanMode
{
    Balance,
    Pan
}

public struct DeviceDataFormat
{
    public ma_format format;       /* Sample format. If set to ma_format_unknown, all sample formats are supported. */
    public UInt32 channels;     /* If set to 0, all channels are supported. */
    public UInt32 sampleRate;   /* If set to 0, all sample rates are supported. */
    public UInt32 flags;        /* A combination of MA_DATA_FORMAT_FLAG_* flags. */
}

public sealed class DeviceInfo
{
    private string name;
    private Int32 index;
    private bool isDefault;
    private DeviceDataFormat[] formats;

    public string Name
    {
        get => name;
    }

    public Int32 Index
    {
        get => index;
    }

    public bool IsDefault
    {
        get => isDefault;
    }

    public DeviceDataFormat[] Formats
    {
        get => formats;
    }

    public DeviceInfo(IntPtr pName, Int32 index, bool isDefault, IntPtr pFormats, UInt32 formatCount)
    {
        if (pName != IntPtr.Zero)
            name = Marshal.PtrToStringAnsi(pName);
        else
            name = string.Empty;

        this.index = index;
        this.isDefault = isDefault;

        formats = (formatCount > 0 && pFormats != IntPtr.Zero) ? new DeviceDataFormat[formatCount] : null;

        if (formats != null)
        {
            for (int i = 0; i < formats.Length; i++)
            {
                IntPtr elementPtr = IntPtr.Add(pFormats, i * Marshal.SizeOf<ma_ex_native_data_format>());
                ma_ex_native_data_format f = Marshal.PtrToStructure<ma_ex_native_data_format>(elementPtr);
                formats[i] = new DeviceDataFormat();
                formats[i].channels = f.channels;
                formats[i].flags = f.flags;
                formats[i].format = f.format;
                formats[i].sampleRate = f.sampleRate;
            }
        }
    }
}

public sealed class ConcurrentList<T>
{
    private readonly List<T> items;
    private readonly object syncRoot = new object();

    public int Count
    {
        get
        {
            lock (syncRoot)
            {
                return items.Count;
            }
        }
    }

    public T this[int index]
    {
        get
        {
            lock (syncRoot)
            {
                return items[index];
            }
        }
        set
        {
            lock (syncRoot)
            {
                items[index] = value;
            }
        }
    }

    public ConcurrentList()
    {
        this.items = new List<T>();
    }

    public void Clear()
    {
        lock (syncRoot)
        {
            items.Clear();
        }
    }

    public void Add(T item)
    {
        lock (syncRoot)
        {
            items.Add(item);
        }
    }

    public void Remove(T item)
    {
        lock (syncRoot)
        {
            items.Remove(item);
        }
    }

    public void Remove(List<T> items)
    {
        lock (syncRoot)
        {
            for (int i = 0; i < items.Count; i++)
            {
                this.items.Remove(items[i]);
            }
        }
    }

    public void RemoveAt(int index)
    {
        lock (syncRoot)
        {
            items.RemoveAt(index);
        }
    }
}

/// <summary>
/// A thread safe class storing audio data.
/// </summary>
public sealed class AudioBuffer
{
    private readonly float[] buffer;
    private readonly object sync = new();
    private int currentLength = 0;

    public AudioBuffer(int capacityPowerOfTwo)
    {
        if (capacityPowerOfTwo <= 0 || (capacityPowerOfTwo & (capacityPowerOfTwo - 1)) != 0)
            throw new ArgumentException("capacityPowerOfTwo must be power of two");
        int capacity = capacityPowerOfTwo;
        buffer = new float[capacity];
    }

    public int Write(NativeArray<float> src)
    {
        lock (sync)
        {
            unsafe
            {
                fixed (float* pBuffer = &buffer[0])
                {
                    NativeArray<float> b = new NativeArray<float>(pBuffer, src.Length);
                    src.CopyTo(b);
                    currentLength = src.Length;
                }

            }
            return src.Length;
        }
    }

    public int Read(ref float[] output)
    {
        lock (sync)
        {
            unsafe
            {
                if (output == null || output.Length < buffer.Length)
                    output = new float[buffer.Length];

                fixed (float* pSrc = &buffer[0], pDst = &output[0])
                {
                    NativeArray<float> src = new NativeArray<float>(pSrc, buffer.Length);
                    NativeArray<float> dst = new NativeArray<float>(pDst, buffer.Length);
                    src.CopyTo(dst);
                    return currentLength;
                }
            }
        }
    }
}
