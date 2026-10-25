// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Diagnostics;

using XEngine.Runtime;
using XEngine.Runtime.Audio;
using XEngine.Runtime.Resources;
using XEngine.Runtime.Video;

namespace XEngine.Editor.GUI;

/// <summary>
/// Editor-side preview playback: wraps a runtime <see cref="VideoPlaybackSession"/> whose
/// presentation is pumped from inspector GUI ticks (never the game SimulationStep), with
/// an optional dedicated (exclusive) <see cref="AudioSource"/> bridging the PCM ring to
/// the engine audio device. The preview owns its texture; teardown is explicit.
/// </summary>
public sealed class VideoPreviewSession : IDisposable
{
    private readonly VideoPlaybackSession _session;
    private readonly Stopwatch _wallClock = Stopwatch.StartNew();
    private bool _withAudio;
    private GameObject? _audioObject;
    private AudioSource? _audioSource;
    private Texture2D? _videoTexture;
    private byte[]? _flipBuffer;
    private double _lastTick;
    private bool _disposed;
    private bool _playRequested;

    private VideoPreviewSession(VideoPlaybackSession session)
    {
        _session = session;
        _lastTick = _wallClock.Elapsed.TotalSeconds;
    }

    /// <summary>
    /// Creates and prepares a preview. <paramref name="withAudio"/> also enables the audio
    /// master clock and the output source; pass false for silent previews (default).
    /// </summary>
    public static VideoPreviewSession Create(string uri, bool withAudio, bool loop, float speed)
    {
        var session = new VideoPlaybackSession();
        if (withAudio && AudioContext.IsInitialized)
        {
            session.EnableAudioClock(AudioContext.SampleRate, AudioContext.Channels);
        }

        session.IsLooping = loop;
        session.PlaybackSpeed = Math.Clamp(speed, 0.5f, 2f);

        var preview = new VideoPreviewSession(session) { _withAudio = withAudio && AudioContext.IsInitialized };
        session.FramePresented += preview.OnFramePresented;
        session.StateChanged += preview.OnStateChanged;
        session.StateChanged += preview.OnStateChangedPlayRequest;

        if (preview._withAudio)
        {
            preview._audioObject = new GameObject("Video Preview Audio");
            preview._audioSource = preview._audioObject.AddComponent<AudioSource>();
            preview._audioSource.Volume = 0f; // started muted by the preview controls
            preview._audioSource.PlayProcedural();
        }

        _ = session.PrepareAsync(uri);
        return preview;
    }

    /// <summary>Clip-based overload: resolves the media payload (editor or packaged).</summary>
    public static VideoPreviewSession CreateForClip(VideoClip clip, bool withAudio, bool loop, float speed)
    {
        if (!clip.TryGetPayloadPath(out string uri))
        {
            throw new VideoException(VideoErrorCode.OpenFailed, $"Media payload for '{clip.Name}' is unavailable.");
        }

        return Create(uri, withAudio, loop, speed);
    }

    public VideoPlaybackState State => _session.State;

    public bool IsPlaying => _session.State == VideoPlaybackState.Playing;

    public double Time => _session.CurrentTimeSeconds;

    public double Duration => _session.DurationSeconds;

    /// <summary>The texture the most recent presented frame was uploaded to.</summary>
    public Texture2D? Texture => _videoTexture;

    public bool IsLooping
    {
        get => _session.IsLooping;
        set => _session.IsLooping = value;
    }

    public float Speed
    {
        get => _session.PlaybackSpeed;
        set => _session.PlaybackSpeed = Math.Clamp(value, 0.5f, 2f);
    }

    /// <summary>Playback tick — call from the inspector GUI (runs without the game loop).</summary>
    public void Tick()
    {
        if (_disposed)
        {
            return;
        }

        _lastTick = _wallClock.Elapsed.TotalSeconds;
        _session.Pump();
    }

    public void Play()
    {
        _playRequested = true;
        if (_session.State is VideoPlaybackState.Ready or VideoPlaybackState.Paused
            or VideoPlaybackState.Buffering or VideoPlaybackState.Ended)
        {
            _session.Play();
        }
        // Preparing: OnStateChangedPlayRequest fires Play the moment Ready lands.

        _audioSource?.PlayProcedural();
    }

    private void OnStateChangedPlayRequest(VideoPlaybackState state)
    {
        if (state == VideoPlaybackState.Ready && _playRequested && !_disposed)
        {
            _session.Play();
        }
    }

    public void Pause()
    {
        _playRequested = false;
        _session.Pause();
        _audioSource?.Stop();
    }

    public void StopPlayback()
    {
        _playRequested = false;
        _session.Stop();
        _audioSource?.Stop();
    }

    public void Seek(double seconds)
    {
        _ = _session.SeekAsync(seconds);
    }

    public void SetMuted(bool muted)
    {
        if (_audioSource is not null)
        {
            _audioSource.Volume = muted ? 0f : 1f;
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _session.StateChanged -= OnStateChanged;
        _session.FramePresented -= OnFramePresented;
        _session.Dispose();

        _audioSource?.Stop();
        if (_audioObject is not null)
        {
            _audioObject.Dispose();
            _audioObject = null;
            _audioSource = null;
        }

        if (_videoTexture is not null)
        {
            _videoTexture.Dispose();
            _videoTexture = null;
        }
    }

    private void OnStateChanged(VideoPlaybackState state)
    {
        // Keep the device source aligned with the session; the ring only feeds while PCM
        // is decoded, and stopping it on pause prevents silent-clock consumption.
        if (state == VideoPlaybackState.Playing)
        {
            _audioSource?.PlayProcedural();
        }
        else if (state is VideoPlaybackState.Paused or VideoPlaybackState.Idle
            or VideoPlaybackState.Ended or VideoPlaybackState.Disposed)
        {
            _audioSource?.Stop();
        }
    }

    private void OnFramePresented(PooledVideoFrame frame)
    {
        uint width = (uint)frame.Width;
        uint height = (uint)frame.Height;
        if (_videoTexture is null)
        {
            _videoTexture = new Texture2D(width, height, generateMipmaps: false);
        }
        else if (_videoTexture.Width != width || _videoTexture.Height != height)
        {
            _videoTexture.RecreateImage(width, height);
        }

        int stride = frame.Width * 4;
        int rows = frame.Height;
        int bytes = stride * rows;
        if (_flipBuffer is null || _flipBuffer.Length < bytes)
        {
            _flipBuffer = new byte[bytes];
        }

        unsafe
        {
            fixed (byte* srcPtr = frame.Data)
            {
                fixed (byte* dstPtr = _flipBuffer)
                {
                    for (int y = 0; y < rows; y++)
                    {
                        Buffer.MemoryCopy(
                            srcPtr + (long)y * stride,
                            dstPtr + (long)(rows - 1 - y) * stride,
                            stride,
                            stride);
                    }
                }
            }
        }

        _videoTexture.SetData<byte>(_flipBuffer.AsMemory(0, bytes));
    }
}
