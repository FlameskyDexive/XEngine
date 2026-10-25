// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Buffers;
using System.Collections.Generic;
using System.Diagnostics;
using System.Threading;

using XEngine.Async;

namespace XEngine.Runtime.Video;

/// <summary>
/// Playback engine for one video source: state machine, bounded frame queue, XTask-driven
/// decode worker, media clock, seek and loop. The session owns an <see cref="IVideoDecoder"/>
/// exclusively (created through the decoder factory on the decode worker) and presents
/// frames through <see cref="FramePresented"/> whenever <see cref="Pump"/> ticks — the owner
/// (VideoPlayer component / preview session) drives the pump on the main thread and consumes
/// frames inside the callback (the session disposes the frame right after).
///
/// Threading: control APIs (Prepare/Play/Pause/Stop/Seek/Pump) are main-thread; the decode
/// worker is a thread-pool fiber that owns the decoder. Late results are dropped by session
/// generation: Stop/Seek bump it and drain the queue so stale frames never present.
///
/// Media clock: when the audio master clock is enabled (<see cref="EnableAudioClock"/>) and
/// the media carries audio, the clock is the PCM ring's consumption position; otherwise a
/// monotonic realtime clock scaled by <see cref="PlaybackSpeed"/> (ignores TimeScale).
/// </summary>
public sealed class VideoPlaybackSession : IDisposable
{
    /// <summary>Bounded video frame queue size (module plan: 4 frames initially).</summary>
    public const int FrameQueueCapacity = 4;

    private readonly IVideoDecoderFactory _decoderFactory;
    private readonly Func<double> _nowSeconds;
    private readonly ArrayPool<byte> _framePool;

    // Lock guards all mutable session state below; events are raised outside it.
    private readonly object _lock = new();

    private VideoPlaybackState _state = VideoPlaybackState.Idle;

    /// <summary>State Seek returns to once the decoder lands on the target (Playing/Paused/Ready).</summary>
    private VideoPlaybackState _resumeStateAfterSeek = VideoPlaybackState.Playing;
    private double _mediaBaseSeconds;   // media time at the current clock anchor
    private double _clockBaseSeconds;   // clock value at the current clock anchor
    private float _playbackSpeed = 1f;
    private bool _loop;
    private bool _decoderEof;
    private long _generation;
    private bool _endedRaised;

    // Frame queue: fixed ring of pooled frames.
    private readonly PooledVideoFrame?[] _queue = new PooledVideoFrame[FrameQueueCapacity];
    private int _queueHead;
    private int _queueCount;

    // Decode worker plumbing (owned by the worker thread once started).
    private IVideoDecoder? _decoder;
    private CancellationTokenSource? _sessionCts = new();
    private bool _workerQuitRequested;
    private bool _workerRunning;
    private double _pendingSeekSeconds = double.NaN; // NaN = no pending seek command
    private float _pendingAudioSpeed = float.NaN;    // NaN = no pending speed command
    private bool _quitRequested;

    private VideoMediaInfo? _metadata;
    private double _startTimeSeconds;
    private double _durationSeconds = -1;

    // Audio master clock (N3): when enabled and the media has audio, the PCM ring's
    // playhead (audio consumption position) drives the media clock.
    private VideoPcmRing? _pcmRing;
    private bool _audioClockEnabled;
    private bool _hasAudio;

    // Diagnostics (N7). Longs are Interlocked-updated so snapshots can read them
    // without the state lock.
    private long _droppedFrames;
    private long _presentedFrames;
    private long _pcmUnderruns;
    private long _decodeErrors;

    private VideoException? _lastError;

    private readonly Queue<AutoResetXTaskCompletionSource> _seekWaiters = new();
    private volatile bool _workerBusy;

    /// <summary>Point-in-time counters for diagnostics overlays and regression checks.</summary>
    public readonly struct Diagnostics
    {
        public Diagnostics(long presented, long dropped, long underruns, double lastPumpMs)
        {
            PresentedFrames = presented;
            DroppedFrames = dropped;
            PcmUnderruns = underruns;
            LastPumpMs = lastPumpMs;
        }

        public long PresentedFrames { get; }
        public long DroppedFrames { get; }
        public long PcmUnderruns { get; }
        public double LastPumpMs { get; }
    }

    /// <summary>Snapshot of the playback diagnostics counters.</summary>
    public Diagnostics Snapshot()
    {
        lock (_lock)
        {
            return new Diagnostics(
                Interlocked.Read(ref _presentedFrames),
                Interlocked.Read(ref _droppedFrames),
                Interlocked.Read(ref _pcmUnderruns),
                _lastPumpMs);
        }
    }

    private double _lastPumpMs;

    public VideoPlaybackSession(Func<double>? nowSeconds = null, ArrayPool<byte>? framePool = null)
    {
        _decoderFactory = FFmpegDecoderFactory.Instance;
        _nowSeconds = nowSeconds ?? DefaultClock;
        _framePool = framePool ?? ArrayPool<byte>.Shared;
    }

    /// <summary>Test/preview seam: pluggable decoder (fake decoders for deterministic tests).</summary>
    public VideoPlaybackSession(
        IVideoDecoderFactory decoderFactory,
        Func<double>? nowSeconds = null,
        ArrayPool<byte>? framePool = null)
    {
        _decoderFactory = decoderFactory;
        _nowSeconds = nowSeconds ?? DefaultClock;
        _framePool = framePool ?? ArrayPool<byte>.Shared;
    }

    private static double DefaultClock() => Stopwatch.GetTimestamp() / (double)Stopwatch.Frequency;

    public VideoPlaybackState State { get { lock (_lock) return _state; } }

    /// <summary>Probe metadata; valid once preparation reached <see cref="VideoPlaybackState.Ready"/>.</summary>
    public VideoMediaInfo Metadata => _metadata
        ?? throw new VideoException(VideoErrorCode.InvalidData, "Session is not prepared.");

    /// <summary>True when the audio master clock is enabled and the media has audio.</summary>
    public bool HasAudioClock { get { lock (_lock) return _hasAudio && _pcmRing is not null; } }

    /// <summary>Current media timeline position in seconds (frozen while paused/buffering).</summary>
    public double CurrentTimeSeconds
    {
        get
        {
            lock (_lock)
            {
                if (_state is VideoPlaybackState.Playing && _hasAudio && _pcmRing is not null)
                {
                    // Audio master clock: the device consumption position IS media time.
                    // Speed is baked into the ring's chunk timestamps by the decoder.
                    return _pcmRing.PlayheadMediaSeconds;
                }

                if (_state is VideoPlaybackState.Playing)
                {
                    return _mediaBaseSeconds + (_nowSeconds() - _clockBaseSeconds) * _playbackSpeed;
                }

                return _mediaBaseSeconds;
            }
        }
    }

    /// <summary>Total media duration in seconds (0 while not prepared).</summary>
    public double DurationSeconds
    {
        get { lock (_lock) return _durationSeconds < 0 ? 0 : _durationSeconds; }
    }

    /// <summary>0.5–2.0 per the module plan; rebases the clock so changes never jump time.</summary>
    public float PlaybackSpeed
    {
        get { lock (_lock) return _playbackSpeed; }
        set
        {
            lock (_lock)
            {
                float clamped = Math.Clamp(value, 0.5f, 2f);
                if (clamped == _playbackSpeed)
                {
                    return;
                }

                double current = CurrentMediaTimeUnlocked();
                _mediaBaseSeconds = current;
                _clockBaseSeconds = _nowSeconds();
                _playbackSpeed = clamped;
                if (_hasAudio)
                {
                    // The decoder rebuilds its atempo graph and re-anchors chunk timestamps.
                    _pendingAudioSpeed = clamped;
                    Monitor.PulseAll(_lock);
                }
            }
        }
    }

    /// <summary>When true, reaching the end seeks back to the start and keeps playing.</summary>
    public bool IsLooping
    {
        get { lock (_lock) return _loop; }
        set { lock (_lock) _loop = value; }
    }

    /// <summary>Raised whenever the state changes (may fire on the decode worker).</summary>
    public event Action<VideoPlaybackState>? StateChanged;

    /// <summary>
    /// Raised from <see cref="Pump"/> with a frame due for presentation. The callback must
    /// consume (upload/copy) the frame before returning — the session disposes it afterwards.
    /// </summary>
    public event Action<PooledVideoFrame>? FramePresented;

    /// <summary>Raised once when non-looping playback reaches the end of the media.</summary>
    public event Action? PlaybackEnded;

    /// <summary>Raised on terminal failures; the session transitions to <see cref="VideoPlaybackState.Error"/>.</summary>
    public event Action<VideoErrorCode, string>? PlaybackFailed;

    // ---------------------------------------------------------------- controls

    /// <summary>
    /// Enables the audio master clock: decoded PCM lands in a session-owned ring and the
    /// media clock follows the audio consumption position. Call with the engine audio
    /// device's rate/channels (AudioContext.SampleRate/Channels) before PrepareAsync.
    /// </summary>
    public void EnableAudioClock(int sampleRate, int channels)
    {
        lock (_lock)
        {
            if (_audioClockEnabled)
            {
                return;
            }

            _audioClockEnabled = true;
            _pcmRing = new VideoPcmRing(sampleRate, channels);
        }
    }

    /// <summary>
    /// Audio clock fallback (device failure): abandons the PCM ring; the session runs on
    /// the realtime clock and stays silent.
    /// </summary>
    public void DisableAudioClock()
    {
        lock (_lock)
        {
            _audioClockEnabled = false;
            _pcmRing = null;
            _hasAudio = false;
        }
    }

    /// <summary>Device callback helper: drains delivered PCM from the session ring.</summary>
    public int ReadPcm(float[] destination, int frames)
    {
        VideoPcmRing? ring = _pcmRing;
        return ring is null ? 0 : ring.Read(destination, frames);
    }

    /// <summary>
    /// Opens the source and primes the frame queue on the decode worker. Preparing while a
    /// different source is already prepared performs a full source switch (the previous
    /// decoder is closed after the worker drains). A duplicate prepare of the in-flight
    /// source merges into the running one.
    /// </summary>
    public XTask PrepareAsync(string uri, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(uri);
        PooledVideoFrame[] drained;
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_quitRequested, this);
            if (_state == VideoPlaybackState.Preparing)
            {
                return XTask.CompletedTask; // duplicate prepare merges into the running one
            }

            _generation++;
            _pendingSeekSeconds = double.NaN;
            _pendingAudioSpeed = float.NaN;
            drained = DrainQueueUnlocked();
            _decoderEof = false;
            _endedRaised = false;
            _hasAudio = false;
            _lastError = null;
            _mediaBaseSeconds = 0;
            _clockBaseSeconds = _nowSeconds();
            TransitionUnlocked(VideoPlaybackState.Preparing);
            if (_workerRunning)
            {
                PulseWorkerUnlocked(); // worker sees Preparing and parks after its current fill
            }
        }

        ReturnFrames(drained);
        return PrepareCore(uri, cancellationToken);
    }

    private async XTask PrepareCore(string uri, CancellationToken cancellationToken)
    {
        CancellationTokenSource? cts = _sessionCts;
        if (cts is null)
        {
            return;
        }

        CancellationToken linked = CancellationTokenSource.CreateLinkedTokenSource(cts.Token, cancellationToken).Token;
        await XTask.SwitchToThreadPool();

        try
        {
            // A source switch closes the previous decoder once the worker finished its
            // in-flight fill (it parks while Preparing, so this wait is short).
            for (int spins = 0; spins < 5000 && Volatile.Read(ref _workerBusy); spins++)
            {
                Thread.Sleep(1);
            }

            IVideoDecoder? previous;
            lock (_lock)
            {
                previous = _decoder;
                _decoder = null;
            }

            previous?.Dispose();

            VideoPcmRing? ring;
            lock (_lock)
            {
                ring = _audioClockEnabled ? _pcmRing : null;
            }

            IVideoDecoder decoder = _decoderFactory.Create(ring, ring?.SampleRate ?? 48000, ring?.Channels ?? 2);
            decoder.Open(uri, linked);

            VideoMediaInfo info = decoder.Metadata;
            bool hasAudio = ring is not null && decoder.HasAudio;
            lock (_lock)
            {
                if (_quitRequested)
                {
                    decoder.Dispose();
                    return;
                }

                _hasAudio = hasAudio;
                _decoder = decoder;
                _metadata = info;
                _durationSeconds = info.DurationSeconds;
                _startTimeSeconds = info.StartTimeSeconds;
                _mediaBaseSeconds = info.StartTimeSeconds;
                _clockBaseSeconds = _nowSeconds();
                _decoderEof = false;
                _endedRaised = false;
            }

            if (hasAudio)
            {
                ring!.Clear(info.StartTimeSeconds);
            }

            PrimeQueue(linked);

            bool prepared;
            lock (_lock)
            {
                prepared = !_quitRequested && _state == VideoPlaybackState.Preparing;
                if (prepared)
                {
                    TransitionUnlocked(VideoPlaybackState.Ready);
                }
            }

            if (prepared)
            {
                StartDecodeWorker();
            }
        }
        catch (OperationCanceledException)
        {
            SetIdleAfterAbort();
        }
        catch (VideoException ex) when (ex.ErrorCode == VideoErrorCode.Canceled)
        {
            SetIdleAfterAbort();
        }
        catch (VideoException ex)
        {
            Fail(ex.ErrorCode, ex.Message);
        }
        catch (Exception ex)
        {
            Fail(VideoErrorCode.OpenFailed, ex.Message);
        }
    }

    private void SetIdleAfterAbort()
    {
        lock (_lock)
        {
            if (!_quitRequested && _state == VideoPlaybackState.Preparing)
            {
                TransitionUnlocked(VideoPlaybackState.Idle);
            }
        }
    }

    private void Fail(VideoErrorCode code, string message)
    {
        lock (_lock)
        {
            _lastError = new VideoException(code, message);
            if (!_quitRequested)
            {
                TransitionUnlocked(VideoPlaybackState.Error);
            }
        }

        PlaybackFailed?.Invoke(code, message);
    }

    /// <summary>
    /// Starts (or resumes) presentation. Repeated calls while playing merge; play after
    /// Ended rewinds the decoder and restarts from the beginning.
    /// </summary>
    public void Play()
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_quitRequested, this);
            switch (_state)
            {
                case VideoPlaybackState.Ready:
                case VideoPlaybackState.Paused:
                case VideoPlaybackState.Buffering:
                    _endedRaised = false;
                    RebaseClockUnlocked(CurrentMediaTimeUnlocked());
                    TransitionUnlocked(VideoPlaybackState.Playing);
                    PulseWorkerUnlocked();
                    break;
                case VideoPlaybackState.Ended:
                    // Replaying needs a real decoder rewind: route through the seek path.
                    _endedRaised = false;
                    _resumeStateAfterSeek = VideoPlaybackState.Playing;
                    _pendingSeekSeconds = _startTimeSeconds;
                    TransitionUnlocked(VideoPlaybackState.Seeking);
                    PulseWorkerUnlocked();
                    break;
                case VideoPlaybackState.Idle:
                case VideoPlaybackState.Error:
                    throw new VideoException(
                        VideoErrorCode.InvalidData,
                        "Play() requires a prepared session. Call PrepareAsync first.");
            }
        }
    }

    public void Pause()
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_quitRequested, this);
            if (_state == VideoPlaybackState.Playing)
            {
                double frozen = CurrentMediaTimeUnlocked();
                _mediaBaseSeconds = frozen;
                TransitionUnlocked(VideoPlaybackState.Paused);
            }
        }
    }

    /// <summary>
    /// Stops playback and resets to the start position. The media stays prepared: the
    /// next <see cref="Play"/> starts again without re-opening (Unity VideoPlayer parity).
    /// </summary>
    public void Stop()
    {
        PooledVideoFrame[] drained;
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_quitRequested, this);
            _generation++;
            _pendingSeekSeconds = double.NaN;
            _pendingAudioSpeed = float.NaN;
            drained = DrainQueueUnlocked();
            _decoderEof = false;
            _endedRaised = false;
            _mediaBaseSeconds = _startTimeSeconds;
            _clockBaseSeconds = _nowSeconds();
            if (_hasAudio && _pcmRing is not null)
            {
                _pcmRing.Clear(_startTimeSeconds);
            }

            if (_state is VideoPlaybackState.Playing or VideoPlaybackState.Paused
                or VideoPlaybackState.Buffering or VideoPlaybackState.Seeking
                or VideoPlaybackState.Ended)
            {
                TransitionUnlocked(VideoPlaybackState.Ready);
            }

            if (_workerRunning)
            {
                PulseWorkerUnlocked(); // worker notices stopped state and parks
            }
        }

        ReturnFrames(drained);
    }

    /// <summary>
    /// Seeks to <paramref name="seconds"/> on the media timeline. Consecutive seeks replace
    /// each other: only the newest request runs and earlier awaiting tasks complete as
    /// canceled (generation drop).
    /// </summary>
    public XTask SeekAsync(double seconds, CancellationToken cancellationToken = default)
    {
        AutoResetXTaskCompletionSource completion = AutoResetXTaskCompletionSource.Create();
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_quitRequested, this);
            if (_state is VideoPlaybackState.Idle or VideoPlaybackState.Preparing or VideoPlaybackState.Error)
            {
                completion.TrySetResult();
                return completion.Task;
            }

            double clamped = Math.Clamp(seconds, 0, Math.Max(0, _durationSeconds - 0.05));
            _generation++;
            _pendingSeekSeconds = clamped;
            _endedRaised = false;
            _resumeStateAfterSeek = _state switch
            {
                VideoPlaybackState.Playing => VideoPlaybackState.Playing,
                VideoPlaybackState.Ended => VideoPlaybackState.Ready,
                _ => _state,
            };

            TransitionUnlocked(VideoPlaybackState.Seeking);
            _seekWaiters.Enqueue(completion);
            Monitor.PulseAll(_lock);
        }

        return AwaitSeek(completion, cancellationToken);
    }

    private async XTask AwaitSeek(AutoResetXTaskCompletionSource completion, CancellationToken cancellationToken)
    {
        using CancellationTokenRegistration registration = cancellationToken.Register(
            static state => ((AutoResetXTaskCompletionSource)state!).TrySetCanceled(), completion);
        await completion.Task;
    }

    // ---------------------------------------------------------------- presentation

    /// <summary>
    /// One presentation tick: presents frames whose PTS is due against the media clock,
    /// drops late frames, handles buffering starvation and end-of-stream/loop. Called from
    /// the owner's main-thread loop (or tests with an injected clock).
    /// </summary>
    public void Pump()
    {
        PooledVideoFrame? toPresent = null;
        int dropped = 0;
        bool raiseEnded = false;
        bool loopRequested = false;
        bool consumedFrames = false;
        var pumpWatch = Stopwatch.StartNew();

        lock (_lock)
        {
            if (_state is not (VideoPlaybackState.Playing or VideoPlaybackState.Ready))
            {
                return;
            }

            double target = CurrentMediaTimeUnlocked();

            // Present the newest frame whose PTS is due; older ones are dropped latecomers.
            // Ready presents exactly the poster frame.
            while (_queueCount > 0)
            {
                PooledVideoFrame head = PeekHeadUnlocked();
                bool due = _state == VideoPlaybackState.Ready || head.PtsSeconds <= target + FrameLateTolerance;
                if (!due)
                {
                    break;
                }

                DequeueHeadUnlocked();
                consumedFrames = true;
                if (toPresent is not null)
                {
                    dropped++;
                    toPresent.Dispose();
                }

                toPresent = head;
            }

            if (toPresent is null && _queueCount == 0)
            {
                if (_decoderEof)
                {
                    double anchor = _mediaBaseSeconds;
                    bool atEnd = _state == VideoPlaybackState.Playing
                        && (target >= _durationSeconds - EndEpsilon || anchor >= _durationSeconds - EndEpsilon);
                    if (atEnd)
                    {
                        if (_loop)
                        {
                            loopRequested = true;
                        }
                        else
                        {
                            _mediaBaseSeconds = Math.Max(0, _durationSeconds);
                            TransitionUnlocked(VideoPlaybackState.Ended);
                            if (!_endedRaised)
                            {
                                _endedRaised = true;
                                raiseEnded = true;
                            }
                        }
                    }
                }
                else if (_state == VideoPlaybackState.Playing)
                {
                    bool audioStarved = !_hasAudio || _pcmRing is null || _pcmRing.BufferedFrames == 0;
                    if (audioStarved)
                    {
                        // Starved: freeze the clock until the worker refills the queue.
                        _mediaBaseSeconds = target;
                        _clockBaseSeconds = _nowSeconds();
                        TransitionUnlocked(VideoPlaybackState.Buffering);
                        Interlocked.Increment(ref _pcmUnderruns);
                        PulseWorkerUnlocked();
                    }
                }
            }

            if (consumedFrames)
            {
                // The worker may be parked waiting for queue space; wake it to refill.
                PulseWorkerUnlocked();
            }

            Interlocked.Add(ref _droppedFrames, dropped);
            if (toPresent is not null)
            {
                Interlocked.Increment(ref _presentedFrames);
            }

            _lastPumpMs = pumpWatch.Elapsed.TotalMilliseconds;
        }

        if (loopRequested)
        {
            // Fire-and-forget loop seek; playback continuity is restored by the worker.
            _ = SeekAsync(_startTimeSeconds);
        }

        if (toPresent is not null)
        {
            try
            {
                FramePresented?.Invoke(toPresent);
            }
            finally
            {
                toPresent.Dispose();
            }
        }

        if (raiseEnded)
        {
            PlaybackEnded?.Invoke();
        }
    }

    /// <summary>Frames within this window of the target time count as presentable.</summary>
    internal static readonly double FrameLateTolerance = 1.0 / 120.0;

    private const double EndEpsilon = 0.02;

    /// <summary>Number of frames currently queued (test/diagnostics surface).</summary>
    public int QueuedFrameCount { get { lock (_lock) return _queueCount; } }

    // ---------------------------------------------------------------- decode worker

    private void StartDecodeWorker()
    {
        lock (_lock)
        {
            if (_workerRunning)
            {
                return;
            }

            _workerRunning = true;
        }

        DecodeWorker().Forget();
    }

    private async XTaskVoid DecodeWorker()
    {
        // Hop off the caller's flow: the worker parks on the queue lock for the session's
        // lifetime, and this first suspension lets StartDecodeWorker return immediately.
        await XTask.SwitchToThreadPool();

        try
        {
            while (true)
            {
                double seekTarget;
                float audioSpeed;
                bool hasSeek;
                bool hasAudioSpeed;
                CancellationToken token;
                lock (_lock)
                {
                    while (!_quitRequested
                        && double.IsNaN(_pendingSeekSeconds)
                        && float.IsNaN(_pendingAudioSpeed)
                        && !ShouldFillQueueUnlocked())
                    {
                        Monitor.Wait(_lock);
                    }

                    if (_quitRequested)
                    {
                        return;
                    }

                    hasSeek = !double.IsNaN(_pendingSeekSeconds);
                    seekTarget = _pendingSeekSeconds;
                    if (hasSeek)
                    {
                        _pendingSeekSeconds = double.NaN;
                    }

                    hasAudioSpeed = !float.IsNaN(_pendingAudioSpeed);
                    audioSpeed = _pendingAudioSpeed;
                    _pendingAudioSpeed = float.NaN;

                    token = _sessionCts?.Token ?? new CancellationToken(true);
                }

                if (hasSeek)
                {
                    PerformSeek(seekTarget, token);
                }
                else if (hasAudioSpeed)
                {
                    // Graph rebuild drops buffered audio; the ring keeps playing through.
                    _decoder?.SetAudioSpeed(audioSpeed);
                }

                FillQueue(token);
            }
        }
        catch (OperationCanceledException)
        {
            // Session stopped/disposed while decoding: normal exit path.
        }
        catch (VideoException ex)
        {
            Fail(ex.ErrorCode, ex.Message);
        }
        catch (Exception ex)
        {
            Fail(VideoErrorCode.DecodeFailed, ex.Message);
        }
        finally
        {
            lock (_lock)
            {
                _workerRunning = false;
            }
        }
    }

    private bool ShouldFillQueueUnlocked()
    {
        // Seeking is included: the post-seek refill is what completes the seek.
        return (_state == VideoPlaybackState.Playing
                || _state == VideoPlaybackState.Buffering
                || _state == VideoPlaybackState.Seeking)
            && !_decoderEof
            && _queueCount < FrameQueueCapacity;
    }

    private void PerformSeek(double seconds, CancellationToken token)
    {
        IVideoDecoder? decoder = _decoder;
        if (decoder is null)
        {
            return;
        }

        decoder.Seek(seconds, token);

        PooledVideoFrame[] drained;
        lock (_lock)
        {
            drained = DrainQueueUnlocked();
            _decoderEof = false;
            _mediaBaseSeconds = seconds;
            _clockBaseSeconds = _nowSeconds();
            if (_hasAudio && _pcmRing is not null)
            {
                _pcmRing.Clear(seconds);
            }
        }

        ReturnFrames(drained);
    }

    /// <summary>
    /// Fills the queue while playing/buffering/seeking and no newer command arrived. Runs
    /// on the decode worker; returns when parked (queue full, idle, or a seek superseded).
    /// </summary>
    private void FillQueue(CancellationToken token)
    {
        _workerBusy = true;
        try
        {
            FillQueueInner(token);
        }
        finally
        {
            _workerBusy = false;
        }
    }

    private void FillQueueInner(CancellationToken token)
    {
        IVideoDecoder? decoder = _decoder;
        if (decoder is null)
        {
            return;
        }

        while (true)
        {
            bool wantFill;
            lock (_lock)
            {
                wantFill = ShouldFillQueueUnlocked()
                    && double.IsNaN(_pendingSeekSeconds)
                    && float.IsNaN(_pendingAudioSpeed);
                if (!wantFill)
                {
                    return;
                }
            }

            PooledVideoFrame? frame = decoder.DecodeNextFrame(token, _framePool);
            lock (_lock)
            {
                if (frame is null)
                {
                    _decoderEof = true;
                    // EOF while buffering: nothing will refill after us, so restore the
                    // presentation state here or the session stays stuck in Buffering.
                    if (_state == VideoPlaybackState.Buffering)
                    {
                        RebaseClockUnlocked(_mediaBaseSeconds);
                        TransitionUnlocked(VideoPlaybackState.Playing);
                    }

                    ResumeAfterSeekUnlocked();
                    CompleteSeekWaitersUnlocked();
                    return;
                }

                if (_queueCount >= FrameQueueCapacity || !double.IsNaN(_pendingSeekSeconds))
                {
                    // Queue filled (or a seek arrived while decoding): drop this latecomer.
                    frame.Dispose();
                    ResumeAfterSeekUnlocked();
                    CompleteSeekWaitersUnlocked();
                    return;
                }

                EnqueueUnlocked(frame);
                if (_state == VideoPlaybackState.Buffering && _queueCount >= 1)
                {
                    RebaseClockUnlocked(_mediaBaseSeconds);
                    TransitionUnlocked(VideoPlaybackState.Playing);
                }

                if (_queueCount >= FrameQueueCapacity)
                {
                    ResumeAfterSeekUnlocked();
                    CompleteSeekWaitersUnlocked();
                }
            }
        }
    }

    private void PrimeQueue(CancellationToken token)
    {
        IVideoDecoder? decoder;
        lock (_lock)
        {
            decoder = _decoder;
        }

        if (decoder is null)
        {
            return;
        }

        while (true)
        {
            bool want;
            lock (_lock)
            {
                want = !_quitRequested && !_decoderEof && _queueCount < FrameQueueCapacity;
            }

            if (!want)
            {
                return;
            }

            PooledVideoFrame? frame = decoder.DecodeNextFrame(token, _framePool);
            lock (_lock)
            {
                if (frame is null)
                {
                    _decoderEof = true;
                    return;
                }

                if (_queueCount >= FrameQueueCapacity)
                {
                    frame.Dispose();
                    return;
                }

                EnqueueUnlocked(frame);
            }
        }
    }

    // ---------------------------------------------------------------- queue helpers

    private PooledVideoFrame PeekHeadUnlocked() => _queue[_queueHead]!;

    private void DequeueHeadUnlocked()
    {
        _queue[_queueHead] = null;
        _queueHead = (_queueHead + 1) % FrameQueueCapacity;
        _queueCount--;
    }

    private void EnqueueUnlocked(PooledVideoFrame frame)
    {
        int tail = (_queueHead + _queueCount) % FrameQueueCapacity;
        _queue[tail] = frame;
        _queueCount++;
    }

    private PooledVideoFrame[] DrainQueueUnlocked()
    {
        var drained = new PooledVideoFrame[_queueCount];
        for (int i = 0; i < drained.Length; i++)
        {
            drained[i] = PeekHeadUnlocked();
            DequeueHeadUnlocked();
        }

        return drained;
    }

    private static void ReturnFrames(PooledVideoFrame[] frames)
    {
        foreach (PooledVideoFrame frame in frames)
        {
            frame.Dispose();
        }
    }

    private void PulseWorkerUnlocked() => Monitor.PulseAll(_lock);

    /// <summary>Leaves <see cref="VideoPlaybackState.Seeking"/> once the seek landed.</summary>
    private void ResumeAfterSeekUnlocked()
    {
        if (_state == VideoPlaybackState.Seeking)
        {
            TransitionUnlocked(_resumeStateAfterSeek);
        }
    }

    private void CompleteSeekWaitersUnlocked()
    {
        while (_seekWaiters.TryDequeue(out AutoResetXTaskCompletionSource? waiter))
        {
            waiter.TrySetResult();
        }
    }

    // ---------------------------------------------------------------- clock / state

    private double CurrentMediaTimeUnlocked()
    {
        if (_state == VideoPlaybackState.Playing)
        {
            if (_hasAudio && _pcmRing is not null)
            {
                // Audio master clock: consumption position drives media time. When the
                // ring starves the playhead freezes (Buffering semantics without drift).
                return _pcmRing.PlayheadMediaSeconds;
            }

            return _mediaBaseSeconds + (_nowSeconds() - _clockBaseSeconds) * _playbackSpeed;
        }

        return _mediaBaseSeconds;
    }

    private void RebaseClockUnlocked(double mediaTime)
    {
        _mediaBaseSeconds = mediaTime;
        _clockBaseSeconds = _nowSeconds();
    }

    private void TransitionUnlocked(VideoPlaybackState next)
    {
        if (_state == next)
        {
            return;
        }

        _state = next;
        StateChanged?.Invoke(next);
    }

    // ---------------------------------------------------------------- dispose

    public void Dispose()
    {
        CancellationTokenSource? cts;
        lock (_lock)
        {
            if (_quitRequested)
            {
                return;
            }

            _quitRequested = true;
            _generation++;
            Monitor.PulseAll(_lock);
            cts = _sessionCts;
            _sessionCts = null;
            TransitionUnlocked(VideoPlaybackState.Disposed);
        }

        try
        {
            cts?.Cancel();
        }
        finally
        {
            cts?.Dispose();
        }

        // The decoder is disposed by the worker's exit path ownership; give it a
        // cooperative handshake, then drain anything still queued.
        for (int spins = 0; spins < 200 && Volatile.Read(ref _workerRunning); spins++)
        {
            Thread.Sleep(5);
        }

        PooledVideoFrame[] drained;
        lock (_lock)
        {
            drained = DrainQueueUnlocked();
            _seekWaiters.Clear();
        }

        ReturnFrames(drained);
        _decoder?.Dispose();
        _decoder = null;
    }
}

/// <summary>
/// Creates the session's decoder on the decode worker thread. The session's PCM sink
/// and engine audio format ride along so audio decode is wired before Open.
/// </summary>
public interface IVideoDecoderFactory
{
    IVideoDecoder Create(IAudioPcmSink? audioSink, int audioSampleRate, int audioChannels);
}

internal sealed class FFmpegDecoderFactory : IVideoDecoderFactory
{
    public static readonly FFmpegDecoderFactory Instance = new();

    public IVideoDecoder Create(IAudioPcmSink? audioSink, int audioSampleRate, int audioChannels)
    {
        var decoder = new FFmpegVideoDecoder();
        if (audioSink is not null)
        {
            decoder.ConfigureAudio(audioSink, audioSampleRate, audioChannels);
        }

        return decoder;
    }
}
