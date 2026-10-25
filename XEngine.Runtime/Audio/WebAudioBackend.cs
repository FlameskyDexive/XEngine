// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using XEngine.Runtime.Audio.Native;

namespace XEngine.Runtime.Audio;

/// <summary>
/// Web Audio output. No miniaudio P/Invoke. The JS host attaches a ScriptProcessor/AudioWorklet
/// and calls <see cref="Pump"/> with the render quantum; B0 measured latency, not desktop quality.
/// </summary>
public sealed class WebAudioBackend : IAudioBackend
{
    private float _masterVolume = 1f;
    private bool _playbackActive;
    private bool _initialized;

    public string Name => "WebAudio";
    public bool IsInitialized => _initialized;
    public int SampleRate { get; private set; }
    public int Channels { get; private set; }
    public IntPtr NativeHandle => IntPtr.Zero;
    public bool IsPlaybackActive => _playbackActive;
    public AudioBackendCapabilities Capabilities => AudioBackendCapabilities.None;
    public int PumpCount { get; private set; }
    public uint LastPumpedFrames { get; private set; }

    public float MasterVolume
    {
        get => _masterVolume;
        set => _masterVolume = Math.Clamp(value, 0f, 1f);
    }

    public bool Initialize(uint sampleRate, uint channels, uint periodSizeInFrames, DeviceInfo? deviceInfo)
    {
        _ = periodSizeInFrames;
        _ = deviceInfo;
        if (_initialized)
            return true;
        SampleRate = (int)Math.Max(1, sampleRate);
        Channels = (int)Math.Max(1, channels);
        _initialized = true;
        _playbackActive = true;
        return true;
    }

    public void Shutdown()
    {
        _initialized = false;
        _playbackActive = false;
        PumpCount = 0;
        LastPumpedFrames = 0;
    }

    public bool SetPlaybackActive(bool active)
    {
        if (!_initialized)
            return false;
        _playbackActive = active;
        return true;
    }

    public void Update() { }

    /// <summary>Simulates one ScriptProcessor quantum without filling PCM.</summary>
    public bool Pump(uint frameCount)
    {
        if (!_initialized || !_playbackActive || frameCount == 0)
            return false;
        PumpCount++;
        LastPumpedFrames = frameCount;
        return true;
    }

    /// <summary>
    /// Simulates one ScriptProcessor quantum and forwards interleaved PCM through
    /// <see cref="AudioContext.HandleDeviceData"/>.
    /// </summary>
    public bool Pump(uint frameCount, NativeArray<float> interleaved)
    {
        if (!Pump(frameCount))
            return false;
        AudioContext.HandleDeviceData(interleaved, frameCount);
        return true;
    }

    public void Dispose() => Shutdown();
}
