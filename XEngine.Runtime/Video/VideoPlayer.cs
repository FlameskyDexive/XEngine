// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Threading;

using XEngine.Echo;
using XEngine.Async;
using XEngine.Runtime.Audio;
using XEngine.Runtime.Audio.Native;
using XEngine.Runtime.Resources;

namespace XEngine.Runtime.Video;

/// <summary>How the decoded video frames are presented.</summary>
public enum VideoRenderMode
{
    /// <summary>Frames upload into an engine-created Texture2D (see <see cref="VideoPlayer.Texture"/>).</summary>
    TextureOnly = 0,

    /// <summary>Frames upload into <see cref="VideoPlayer.TargetRenderTexture"/>.MainTexture.</summary>
    RenderTexture = 1,

    /// <summary>Frames upload into an internal texture bound to the target renderer's material property.</summary>
    MaterialOverride = 2,
}

/// <summary>Where the decoded audio goes.</summary>
public enum VideoAudioOutputMode
{
    /// <summary>Silent playback (and no audio clock).</summary>
    None = 0,

    /// <summary>PCM routes through a dedicated AudioSource (the target one, or one created on this GameObject).</summary>
    AudioSource = 1,
}

/// <summary>Selects between the imported <see cref="VideoClip"/> asset and a raw URL source.</summary>
public enum VideoSource
{
    Clip = 0,
    Url = 1,
}

/// <summary>
/// Scene component wrapping a <see cref="VideoPlaybackSession"/>: plays a VideoClip or URL
/// and presents frames to textures, render textures, or a renderer's material property.
/// Playback runs on the session's decode worker; this component pumps presentation in
/// <see cref="Update"/> and uploads presented frames on the main thread. Each player owns
/// an exclusive session (no shared state between players).
/// </summary>
[AddComponentMenu("Video/Video Player")]
public sealed class VideoPlayer : MonoBehaviour
{
    [SerializeField] private VideoSource _source = VideoSource.Clip;
    [SerializeField] private VideoClip? _clip;
    [SerializeField] private string _url = string.Empty;
    [SerializeField] private bool _playOnAwake;
    [SerializeField] private bool _isLooping;
    [SerializeField] private float _playbackSpeed = 1f;
    [SerializeField] private float _volume = 1f;
    [SerializeField] private bool _mute;
    [SerializeField] private VideoRenderMode _renderMode = VideoRenderMode.TextureOnly;
    [SerializeField] private Texture2D? _targetTexture;
    [SerializeField] private RenderTexture? _targetRenderTexture;
    [SerializeField] private MeshRenderer? _targetRenderer;
    [SerializeField] private string _materialPropertyName = "_MainTex";
    [SerializeField] private VideoAudioOutputMode _audioOutputMode = VideoAudioOutputMode.AudioSource;
    [SerializeField] private AudioSource? _targetAudioSource;

    private VideoPlaybackSession? _session;
    private AudioSource? _boundAudioSource;
    private Texture2D? _videoTexture;
    private byte[]? _flipBuffer;
    private float[]? _pcmScratch;
    private bool _playRequested;
    private bool _proceduralBound;

    /// <summary>Imported media asset (when <see cref="Source"/> is Clip).</summary>
    public VideoClip? Clip
    {
        get => _clip;
        set { _clip = value; if (_source == VideoSource.Clip) InvalidateSource(); }
    }

    /// <summary>Raw media URL/file path (when <see cref="Source"/> is Url).</summary>
    public string Url
    {
        get => _url;
        set { _url = value ?? string.Empty; if (_source == VideoSource.Url) InvalidateSource(); }
    }

    public VideoSource Source
    {
        get => _source;
        set { _source = value; InvalidateSource(); }
    }

    public bool PlayOnAwake { get => _playOnAwake; set => _playOnAwake = value; }

    public bool IsLooping
    {
        get => _session?.IsLooping ?? _isLooping;
        set { _isLooping = value; if (_session is not null) _session.IsLooping = value; }
    }

    public float PlaybackSpeed
    {
        get => _session?.PlaybackSpeed ?? _playbackSpeed;
        set { _playbackSpeed = Math.Clamp(value, 0.5f, 2f); if (_session is not null) _session.PlaybackSpeed = _playbackSpeed; }
    }

    /// <summary>Audio volume (0–1) applied to the audio output source.</summary>
    public float Volume
    {
        get => _volume;
        set { _volume = Math.Clamp(value, 0f, 1f); ApplyAudioVolume(); }
    }

    /// <summary>Mutes the audio output without touching <see cref="Volume"/>.</summary>
    public bool Mute
    {
        get => _mute;
        set { _mute = value; ApplyAudioVolume(); }
    }

    public VideoRenderMode RenderMode
    {
        get => _renderMode;
        set => _renderMode = value;
    }

    public VideoAudioOutputMode AudioOutputMode
    {
        get => _audioOutputMode;
        set => _audioOutputMode = value;
    }

    /// <summary>External texture receiving frames in <see cref="VideoRenderMode.TextureOnly"/>.</summary>
    public Texture2D? TargetTexture { get => _targetTexture; set => _targetTexture = value; }

    /// <summary>Render texture receiving frames in <see cref="VideoRenderMode.RenderTexture"/>.</summary>
    public RenderTexture? TargetRenderTexture { get => _targetRenderTexture; set => _targetRenderTexture = value; }

    /// <summary>Renderer whose material property receives frames in <see cref="VideoRenderMode.MaterialOverride"/>.</summary>
    public MeshRenderer? TargetRenderer { get => _targetRenderer; set => _targetRenderer = value; }

    public string MaterialPropertyName { get => _materialPropertyName; set => _materialPropertyName = value; }

    /// <summary>Audio source used in <see cref="VideoAudioOutputMode.AudioSource"/>; one is created when unset.</summary>
    public AudioSource? TargetAudioSource { get => _targetAudioSource; set => _targetAudioSource = value; }

    /// <summary>The texture the most recent frame was uploaded to (null until the first frame).</summary>
    public Texture2D? Texture => _videoTexture;

    public VideoPlaybackState State => _session?.State ?? VideoPlaybackState.Idle;

    public bool IsPlaying => _session?.State == VideoPlaybackState.Playing;

    public bool IsPreparing => _session?.State == VideoPlaybackState.Preparing;

    public bool IsReady => _session is not null &&
        _session.State is VideoPlaybackState.Ready or VideoPlaybackState.Playing
            or VideoPlaybackState.Paused or VideoPlaybackState.Buffering or VideoPlaybackState.Seeking;

    /// <summary>Current media time in seconds.</summary>
    public double Time => _session?.CurrentTimeSeconds ?? 0;

    /// <summary>Media duration in seconds (0 while not prepared).</summary>
    public double Length => _session?.DurationSeconds ?? 0;

    /// <summary>Raised whenever the playback state changes.</summary>
    public event Action<VideoPlaybackState>? StateChanged;

    /// <summary>Raised on terminal playback failures with the error code and diagnostic message.</summary>
    public event Action<VideoErrorCode, string>? PlaybackFailed;

    /// <summary>Raised once when non-looping playback reaches the end.</summary>
    public event Action? Ended;

    // ---------------------------------------------------------------- control

    /// <summary>Prepares the current source (opens media, primes queues). Play also prepares.</summary>
    public XTask PrepareAsync(CancellationToken cancellationToken = default)
    {
        VideoPlaybackSession? session = EnsureSession();
        if (session is null || !TryResolveSourceUri(out string uri))
        {
            return XTask.CompletedTask;
        }

        return session.PrepareAsync(uri, cancellationToken);
    }

    /// <summary>Starts playback; prepares the session automatically when idle.</summary>
    public void Play()
    {
        VideoPlaybackSession? session = EnsureSession();
        if (session is null)
        {
            return;
        }

        _playRequested = true;
        switch (session.State)
        {
            case VideoPlaybackState.Idle:
            case VideoPlaybackState.Error:
                session.Stop();
                _ = PrepareAndPlay();
                break;
            case VideoPlaybackState.Ready:
                session.Play();
                StartProceduralAudio();
                break;
        }
    }

    private async XTaskVoid PrepareAndPlay()
    {
        VideoPlaybackSession? session = _session;
        if (session is null)
        {
            return;
        }

        if (TryResolveSourceUri(out string uri))
        {
            await session.PrepareAsync(uri);
        }

        if (_playRequested && session.State == VideoPlaybackState.Ready)
        {
            session.Play();
            StartProceduralAudio();
        }
    }

    public void Pause()
    {
        _session?.Pause();
        StopProceduralAudio();
    }

    public void Stop()
    {
        _playRequested = false;
        _session?.Stop();
        StopProceduralAudio();
    }

    /// <summary>Seeks to <paramref name="seconds"/> (media time). Playback state is preserved.</summary>
    public XTask SeekAsync(double seconds, CancellationToken cancellationToken = default)
    {
        if (_session is null)
        {
            return XTask.CompletedTask;
        }

        return _session.SeekAsync(seconds, cancellationToken);
    }

    // ---------------------------------------------------------------- lifecycle

    public override void OnEnable()
    {
        if (_playOnAwake)
        {
            Play();
        }
    }

    public override void OnDisable()
    {
        _playRequested = false;
        _session?.Stop();
        ReleaseSession();
    }

    public override void OnDispose()
    {
        ReleaseSession();
        if (_videoTexture is not null)
        {
            _videoTexture.Dispose();
            _videoTexture = null;
        }
    }

    public override void Update()
    {
        _session?.Pump();
    }

    // ---------------------------------------------------------------- internals

    private bool TryResolveSourceUri(out string uri)
    {
        if (_source == VideoSource.Clip)
        {
            if (_clip is not null && _clip.TryGetPayloadPath(out uri!))
            {
                return true;
            }

            uri = string.Empty;
            return false;
        }

        uri = _url ?? string.Empty;
        return uri.Length > 0;
    }

    private VideoPlaybackSession? EnsureSession()
    {
        if (_session is not null)
        {
            return _session;
        }

        var session = new VideoPlaybackSession();
        if (_audioOutputMode != VideoAudioOutputMode.None && AudioContext.IsInitialized)
        {
            session.EnableAudioClock(AudioContext.SampleRate, AudioContext.Channels);
        }

        session.IsLooping = _isLooping;
        session.PlaybackSpeed = Math.Clamp(_playbackSpeed, 0.5f, 2f);
        session.StateChanged += OnSessionStateChanged;
        session.PlaybackEnded += OnSessionEnded;
        session.FramePresented += OnFramePresented;
        session.PlaybackFailed += OnSessionPlaybackFailed;
        EnsureAudioOutput();
        _session = session;
        return session;
    }

    private void OnSessionPlaybackFailed(VideoErrorCode code, string message) =>
        PlaybackFailed?.Invoke(code, message);

    /// <summary>
    /// Binds the exclusive audio output source: the explicitly assigned one, else one on
    /// this GameObject (created on demand).
    /// </summary>
    private void EnsureAudioOutput()
    {
        if (_audioOutputMode != VideoAudioOutputMode.AudioSource)
        {
            return;
        }

        _boundAudioSource = _targetAudioSource ?? GetComponent<AudioSource>() ?? GameObject.AddComponent<AudioSource>();
        _boundAudioSource.Spatial = false;
        ApplyAudioVolume();
    }

    private void StartProceduralAudio()
    {
        if (_audioOutputMode != VideoAudioOutputMode.AudioSource)
        {
            return;
        }

        EnsureAudioOutput();
        AudioSource? source = _targetAudioSource ?? _boundAudioSource;
        if (source is null)
        {
            return;
        }

        if (!_proceduralBound)
        {
            source.Read += OnProceduralRead;
            _proceduralBound = true;
        }

        ApplyAudioVolume();
        if (!source.IsPlaying)
        {
            source.PlayProcedural();
        }
    }

    private void StopProceduralAudio()
    {
        AudioSource? source = _targetAudioSource ?? _boundAudioSource;
        if (source is null)
        {
            return;
        }

        if (_proceduralBound)
        {
            source.Read -= OnProceduralRead;
            _proceduralBound = false;
        }

        source.Stop();
    }

    private void OnProceduralRead(NativeArray<float> framesOut, ulong frameCount, int channels)
    {
        int frames = (int)frameCount;
        int samples = frames * channels;
        if (samples <= 0 || framesOut.Length <= 0)
        {
            return;
        }

        if (_pcmScratch is null || _pcmScratch.Length < samples)
        {
            _pcmScratch = new float[samples];
        }

        int got = _session?.ReadPcm(_pcmScratch, frames) ?? 0;
        int copied = got * channels;
        if (copied > framesOut.Length)
        {
            copied = framesOut.Length;
        }

        for (int i = 0; i < copied; i++)
        {
            framesOut[i] = _pcmScratch[i];
        }

        for (int i = copied; i < framesOut.Length && i < samples; i++)
        {
            framesOut[i] = 0f;
        }
    }

    private void ApplyAudioVolume()
    {
        AudioSource source = _targetAudioSource ?? _boundAudioSource;
        if (source is not null)
        {
            source.Volume = _mute ? 0f : _volume;
        }
    }

    private void OnSessionStateChanged(VideoPlaybackState state)
    {
        if (state == VideoPlaybackState.Ready && _playRequested)
        {
            _session?.Play();
        }

        if (state == VideoPlaybackState.Playing)
        {
            StartProceduralAudio();
        }
        else if (state is VideoPlaybackState.Paused or VideoPlaybackState.Ended
                 or VideoPlaybackState.Idle or VideoPlaybackState.Error or VideoPlaybackState.Disposed)
        {
            StopProceduralAudio();
        }

        StateChanged?.Invoke(state);
    }

    private void OnSessionEnded() => Ended?.Invoke();

    /// <summary>Main-thread frame upload: TextureOnly/RenderTexture/MaterialOverride paths.</summary>
    private void OnFramePresented(PooledVideoFrame frame)
    {
        Texture2D? uploadTarget;
        switch (_renderMode)
        {
            case VideoRenderMode.RenderTexture:
                uploadTarget = _targetRenderTexture?.MainTexture;
                if (uploadTarget is null)
                {
                    return;
                }

                break;
            case VideoRenderMode.MaterialOverride:
                uploadTarget = EnsureInternalTexture((uint)frame.Width, (uint)frame.Height);
                break;
            default:
                uploadTarget = _targetTexture ?? EnsureInternalTexture((uint)frame.Width, (uint)frame.Height);
                break;
        }

        if (uploadTarget.Width != (uint)frame.Width || uploadTarget.Height != (uint)frame.Height)
        {
            if (uploadTarget == _videoTexture)
            {
                uploadTarget.RecreateImage((uint)frame.Width, (uint)frame.Height);
            }
            else
            {
                return; // external target of a different size cannot take the frame
            }
        }

        UploadFlipped(uploadTarget, frame);

        if (_renderMode == VideoRenderMode.MaterialOverride && _targetRenderer is not null && _videoTexture is not null)
        {
            _targetRenderer.SetTextureOverride(
                string.IsNullOrEmpty(_materialPropertyName) ? "_MainTex" : _materialPropertyName,
                _videoTexture);
        }
    }

    private Texture2D EnsureInternalTexture(uint width, uint height)
    {
        if (_videoTexture is null)
        {
            _videoTexture = new Texture2D(width, height, generateMipmaps: false);
        }

        return _videoTexture;
    }


    /// <summary>
    /// Decoded rows are top-down; engine textures expect row 0 at the bottom, so uploads
    /// flip rows through a reused scratch buffer (allocation-free once steady).
    /// </summary>
    private void UploadFlipped(Texture2D target, PooledVideoFrame frame)
    {
        int stride = frame.Width * 4;
        int height = frame.Height;
        int bytes = stride * height;
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
                    for (int y = 0; y < height; y++)
                    {
                        Buffer.MemoryCopy(
                            srcPtr + (long)y * stride,
                            dstPtr + (long)(height - 1 - y) * stride,
                            stride,
                            stride);
                    }
                }
            }
        }

        target.SetData<byte>(_flipBuffer.AsMemory(0, bytes));
    }

    private void InvalidateSource()
    {
        if (_session is not null)
        {
            _session.Stop();
        }
    }

    private void ReleaseSession()
    {
        StopProceduralAudio();
        if (_session is not null)
        {
            _session.StateChanged -= OnSessionStateChanged;
            _session.PlaybackEnded -= OnSessionEnded;
            _session.FramePresented -= OnFramePresented;
            _session.Dispose();
            _session = null;
            _playRequested = false;
        }

        _boundAudioSource = null;
        _pcmScratch = null;
    }
}
