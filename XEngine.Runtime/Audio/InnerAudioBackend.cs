// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

namespace XEngine.Runtime.Audio;

/// <summary>
/// WeChat / Douyin InnerAudio adapter. No GPU required — B7 can play audio without WebGL.
/// Hosts supply <c>wx.createInnerAudioContext</c> / <c>tt.createInnerAudioContext</c> callbacks.
/// </summary>
public sealed class InnerAudioBackend : IAudioBackend
{
    private readonly Func<string, bool> _play;
    private readonly Func<bool> _pause;
    private readonly Func<bool> _stop;
    private readonly Action<float>? _setVolume;
    private float _masterVolume = 1f;
    private bool _initialized;
    private bool _playbackActive;

    public InnerAudioBackend(
        Func<string, bool>? play = null,
        Func<bool>? pause = null,
        Func<bool>? stop = null,
        Action<float>? setVolume = null)
    {
        _play = play ?? (static _ => false);
        _pause = pause ?? (static () => false);
        _stop = stop ?? (static () => false);
        _setVolume = setVolume;
    }

    public string Name => "InnerAudio";
    public bool IsInitialized => _initialized;
    public int SampleRate { get; private set; } = 44100;
    public int Channels { get; private set; } = 2;
    public IntPtr NativeHandle => IntPtr.Zero;
    public bool IsPlaybackActive => _playbackActive;
    public AudioBackendCapabilities Capabilities => AudioBackendCapabilities.None;
    public string? LastSrc { get; private set; }
    public int PlayCount { get; private set; }
    public int PauseCount { get; private set; }

    public float MasterVolume
    {
        get => _masterVolume;
        set
        {
            _masterVolume = Math.Clamp(value, 0f, 1f);
            _setVolume?.Invoke(_masterVolume);
        }
    }

    public bool Initialize(uint sampleRate, uint channels, uint periodSizeInFrames, DeviceInfo? deviceInfo)
    {
        _ = periodSizeInFrames;
        _ = deviceInfo;
        SampleRate = (int)Math.Max(1, sampleRate);
        Channels = (int)Math.Max(1, channels);
        _initialized = true;
        _playbackActive = true;
        return true;
    }

    public void Shutdown()
    {
        _stop();
        _initialized = false;
        _playbackActive = false;
        LastSrc = null;
    }

    public bool SetPlaybackActive(bool active)
    {
        if (!_initialized)
            return false;
        _playbackActive = active;
        if (active)
            return LastSrc == null || _play(LastSrc);
        PauseCount++;
        return _pause();
    }

    public bool Play(string src)
    {
        if (!_initialized || string.IsNullOrWhiteSpace(src))
            return false;
        LastSrc = src;
        PlayCount++;
        _playbackActive = true;
        return _play(src);
    }

    public bool Stop()
    {
        LastSrc = null;
        _playbackActive = false;
        return _stop();
    }

    public void Update() { }

    public void Dispose() => Shutdown();
}
