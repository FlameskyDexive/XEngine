// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Buffers;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

using FFmpeg.AutoGen;

namespace XEngine.Runtime.Video;

/// <summary>
/// FFmpeg-backed <see cref="IVideoDecoder"/>. Owns the demuxer context, video/audio codec
/// contexts, and the RGBA converter. All access must stay on one thread (the session
/// worker serializes calls).
///
/// Cancellation and the no-progress timeout are enforced through the demuxer interrupt
/// callback, which fires inside every blocking avformat/avio call, plus cooperative checks
/// in the decode loop. The managed callback delegate is held in a field for the decoder's
/// lifetime — FFmpeg keeps invoking the thunk after the initial call returns.
/// </summary>
internal sealed unsafe class FFmpegVideoDecoder : IVideoDecoder
{
    /// <summary>Default deadline for open / find-stream-info / no-progress stalls, per the module plan.</summary>
    public const int DefaultTimeoutMs = 15_000;

    // AVERROR negates the platform errno value: Darwin EAGAIN is 35, while the
    // Windows/Linux/OpenHarmony payloads use 11. Cache the host check once.
    private static readonly int AvErrorEagain =
        OperatingSystem.IsMacOS() || OperatingSystem.IsIOS() || OperatingSystem.IsMacCatalyst()
            ? -35 : -11;
    private const long NoPtsValue = long.MinValue;
    private const int FixedPointOne = 1 << 16;

    private AVFormatContext* _format;
    private AVCodecContext* _videoCodec;
    private AVCodecContext* _audioCodec;
    private SwsContext* _sws;
    private AVFrame* _frame;
    private AVPacket* _packet;

    private int _videoStreamIndex = -1;
    private int _audioStreamIndex = -1;
    private bool _videoDraining;
    private VideoMediaInfo? _metadata;

    private readonly InterruptState _interrupt = new();

    // Session threads (prepare, worker, teardown) may touch the native context from
    // different threads; the API boundary serializes them. Monitor is reentrant.
    private readonly object _nativeLock = new();
    private readonly int _timeoutMs;

    // The interrupt callback thunk must stay rooted for as long as the demuxer may call it.
    private readonly AVIOInterruptCB_callback _interruptDelegate;
    private readonly AVIOInterruptCB _interruptCb;

    // Audio output pipeline (set via ConfigureAudio before Open).
    private IAudioPcmSink? _pcmSink;
    private int _targetAudioRate;
    private int _targetAudioChannels;
    private FFmpegAudioFilter? _audioFilter;
    private float _audioSpeed = 1f;
    private double _audioAnchorMedia;
    private double _audioAnchorOutPts;
    private bool _audioAnchorSet;
    private double _pendingAudioFrameMediaPts;

    private bool _audioOnly;
    private double _audioOnlyPts;
    private bool _audioOnlyEnded;

    private bool _disposed;

    public FFmpegVideoDecoder(int timeoutMs = DefaultTimeoutMs)
    {
        FFmpegNativeLoader.EnsureLoaded();
        _timeoutMs = timeoutMs > 0 ? timeoutMs : DefaultTimeoutMs;
        _interruptDelegate = _ => _interrupt.ShouldAbort() ? 1 : 0;
        _interruptCb = new AVIOInterruptCB { callback = _interruptDelegate };
    }

    public bool HasAudio => _pcmSink is not null && _audioStreamIndex >= 0 && _audioCodec is not null;

    public void ConfigureAudio(IAudioPcmSink sink, int targetSampleRate, int targetChannels)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _pcmSink = sink;
        _targetAudioRate = targetSampleRate;
        _targetAudioChannels = targetChannels;
    }

    public void SetAudioSpeed(float speed)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _audioSpeed = Math.Clamp(speed, 0.5f, 2f);
        _audioFilter?.SetSpeed(_audioSpeed);
        _audioAnchorSet = false;
    }

    public VideoMediaInfo Metadata => _metadata
        ?? throw new VideoException(VideoErrorCode.InvalidData, "Decoder is not open; no metadata available.");

    public void Open(string uri, CancellationToken cancellationToken)
    {
        lock (_nativeLock)
        {
            OpenCore(uri, cancellationToken);
        }
    }

    private void OpenCore(string uri, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (string.IsNullOrWhiteSpace(uri))
        {
            throw new VideoException(VideoErrorCode.OpenFailed, "Video URI is empty.");
        }

        _interrupt.Arm(_timeoutMs, cancellationToken);

        // Allocate the context up front so the interrupt callback is active while the
        // demuxer itself opens the input (network connects, header reads).
        AVFormatContext* context = ffmpeg.avformat_alloc_context();
        context->interrupt_callback = _interruptCb;

        try
        {
            int error = ffmpeg.avformat_open_input(&context, uri, null, null);
            if (error < 0)
            {
                // avformat_open_input frees the context on failure.
                throw MapOpenError(error, uri);
            }

            _format = context;

            _interrupt.Arm(_timeoutMs, cancellationToken);
            error = ffmpeg.avformat_find_stream_info(_format, null);
            if (error < 0)
            {
                throw MapOpenError(error, uri);
            }

            _videoStreamIndex = ffmpeg.av_find_best_stream(
                _format, AVMediaType.AVMEDIA_TYPE_VIDEO, -1, -1, null, 0);
            _audioStreamIndex = ffmpeg.av_find_best_stream(
                _format, AVMediaType.AVMEDIA_TYPE_AUDIO, -1,
                _videoStreamIndex >= 0 ? _videoStreamIndex : -1, null, 0);

            if (_videoStreamIndex < 0 && _audioStreamIndex < 0)
            {
                throw new VideoException(
                    VideoErrorCode.VideoStreamNotFound,
                    $"No decodable video or audio stream in '{uri}'.");
            }

            _audioOnly = _videoStreamIndex < 0;
            _audioOnlyPts = 0;
            _audioOnlyEnded = false;
            _videoDraining = false;

            if (_videoStreamIndex >= 0)
            {
                OpenVideoCodec();
            }

            if (_audioStreamIndex >= 0)
            {
                OpenAudioCodec();
            }

            _metadata = BuildMetadata();
            _audioOnlyPts = _metadata.StartTimeSeconds;
        }
        finally
        {
            if (_format is null && context is not null)
            {
                // Open failed after allocation but outside avformat_open_input.
                ffmpeg.avformat_free_context(context);
            }
        }
    }

    public VideoFrame DecodeFirstFrame(CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Probes use exact-size arrays and never see end-of-stream as null. The wrapper's
        // Dispose is a no-op on this path (null pool), so the exact array just moves on.
        using PooledVideoFrame first = DecodeNext(cancellationToken, pool: null)
            ?? throw new VideoException(
                VideoErrorCode.DecodeFailed, "Stream ended before the first frame was decoded.");
        return new VideoFrame
        {
            Data = first.Data,
            Width = first.Width,
            Height = first.Height,
            PtsSeconds = first.PtsSeconds,
        };
    }

    public PooledVideoFrame? DecodeNextFrame(CancellationToken cancellationToken, ArrayPool<byte> pool)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(pool);

        return DecodeNext(cancellationToken, pool);
    }

    public void Seek(double seconds, CancellationToken cancellationToken)
    {
        lock (_nativeLock)
        {
            SeekCore(seconds, cancellationToken);
        }
    }

    private void SeekCore(double seconds, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_format is null || (_videoStreamIndex < 0 && _audioStreamIndex < 0))
        {
            throw new VideoException(VideoErrorCode.InvalidData, "Decoder is not open.");
        }

        _interrupt.Arm(_timeoutMs, cancellationToken);

        int seekStream = _videoStreamIndex >= 0 ? _videoStreamIndex : _audioStreamIndex;
        AVStream* stream = _format->streams[seekStream];
        double timeBase = StreamTimeBaseSeconds(stream);
        long target = timeBase > 0 ? (long)(seconds / timeBase) : (long)seconds;

        int error = ffmpeg.avformat_seek_file(
            _format,
            seekStream,
            long.MinValue,
            target,
            long.MaxValue,
            0 /* flags: default backward seek to a keyframe */);
        if (error < 0)
        {
            throw new VideoException(
                VideoErrorCode.InvalidData,
                $"avformat_seek_file failed: {GetErrorMessage(error)} ({error}).");
        }

        if (_videoCodec is not null)
        {
            ffmpeg.avcodec_flush_buffers(_videoCodec);
        }

        if (_audioCodec is not null)
        {
            ffmpeg.avcodec_flush_buffers(_audioCodec);
        }

        _audioOnlyPts = seconds;
        _audioOnlyEnded = false;
        _videoDraining = false;

        // Audio: drop graph-buffered PCM and restart the media mapping at the seek target.
        if (_audioFilter is not null)
        {
            _audioFilter.SetSpeed(_audioSpeed); // forced graph rebuild, drops buffered audio
            _audioAnchorSet = false;
        }
    }

    // ---------------------------------------------------------------- audio pipeline

    /// <summary>
    /// Feeds one audio packet through the codec and pushes resulting frames into the
    /// filter graph, draining converted PCM into the sink. Called from the demux loop so
    /// audio decode interleaves with video fill.
    /// </summary>
    private void DecodeAudioPacket(AVPacket* packet)
    {
        AVCodecContext* codec = _audioCodec;
        FFmpegAudioFilter? filter = _audioFilter;
        if (codec is null || filter is null)
        {
            return;
        }

        int error = ffmpeg.avcodec_send_packet(codec, packet);
        if (error < 0 && error != AvErrorEagain)
        {
            throw new VideoException(
                VideoErrorCode.DecodeFailed,
                $"audio avcodec_send_packet failed: {GetErrorMessage(error)} ({error}).");
        }

        AVFrame* audioFrame = ffmpeg.av_frame_alloc();
        try
        {
            while (true)
            {
                error = ffmpeg.avcodec_receive_frame(codec, audioFrame);
                if (error == AvErrorEagain || error == ffmpeg.AVERROR_EOF)
                {
                    break;
                }

                if (error < 0)
                {
                    throw new VideoException(
                        VideoErrorCode.DecodeFailed,
                        $"audio avcodec_receive_frame failed: {GetErrorMessage(error)} ({error}).");
                }

                try
                {
                    DrainAudio(filter);

                    AVStream* stream = _format->streams[_audioStreamIndex];
                    _pendingAudioFrameMediaPts = audioFrame->pts != NoPtsValue
                        ? audioFrame->pts * StreamTimeBaseSeconds(stream)
                        : _pendingAudioFrameMediaPts;
                    filter.Push(audioFrame);
                    DrainAudio(filter);
                }
                finally
                {
                    ffmpeg.av_frame_unref(audioFrame);
                }
            }
        }
        finally
        {
            ffmpeg.av_frame_free(&audioFrame);
        }
    }

    /// <summary>Pulls converted chunks from the graph and writes them to the sink.</summary>
    private void DrainAudio(FFmpegAudioFilter filter)
    {
        if (_pcmSink is null)
        {
            return;
        }

        while (filter.TryPull(out float[] samples, out int count, out double outputPts))
        {
            if (!_audioAnchorSet)
            {
                _audioAnchorMedia = _pendingAudioFrameMediaPts;
                _audioAnchorOutPts = outputPts;
                _audioAnchorSet = true;
            }

            double media = _audioAnchorMedia + (outputPts - _audioAnchorOutPts) * _audioSpeed;
            if (!_pcmSink.TryWrite(samples, count, media, _audioSpeed))
            {
                return; // sink full: device stalled, stop producing audio
            }
        }
    }

    /// <summary>Demuxer EOF: signal the graph to drain its internal pipeline.</summary>
    private void FlushAudioAtEndOfStream()
    {
        if (_audioFilter is not null)
        {
            _audioFilter.EndOfStream();
            DrainAudio(_audioFilter);
        }
    }

    /// <summary>
    /// Core read/decode loop. Returns the next presentable frame (pooled when
    /// <paramref name="pool"/> is provided, exact-size otherwise), or null at end of stream.
    /// </summary>
    private PooledVideoFrame? DecodeNext(CancellationToken cancellationToken, ArrayPool<byte>? pool)
    {
        lock (_nativeLock)
        {
            return DecodeNextCore(cancellationToken, pool);
        }
    }

    private PooledVideoFrame? DecodeNextCore(CancellationToken cancellationToken, ArrayPool<byte>? pool)
    {
        if (_format is null)
        {
            throw new VideoException(VideoErrorCode.InvalidData, "Decoder is not open.");
        }

        if (_audioOnly || _videoCodec is null)
        {
            return DecodeAudioOnly(cancellationToken, pool);
        }

        _interrupt.Arm(_timeoutMs, cancellationToken);

        if (_frame is null)
        {
            _frame = ffmpeg.av_frame_alloc();
            _packet = ffmpeg.av_packet_alloc();
        }

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();

            // Drain all available output before submitting another packet. A codec
            // can emit more than one frame per packet and buffer frames until EOF.
            int error = ffmpeg.avcodec_receive_frame(_videoCodec, _frame);
            if (error >= 0)
            {
                try
                {
                    if (_frame->width > 0 && _frame->height > 0)
                    {
                        if (pool is not null)
                        {
                            return ConvertFrameToRgbaPooled(pool);
                        }

                        VideoFrame exact = ConvertFrameToRgba();
                        return new PooledVideoFrame(null, exact.Data, exact.Data.Length, exact.Width, exact.Height, exact.PtsSeconds);
                    }
                }
                finally
                {
                    ffmpeg.av_frame_unref(_frame);
                }

                continue;
            }

            if (error == ffmpeg.AVERROR_EOF)
            {
                return null;
            }

            if (error != AvErrorEagain || _videoDraining)
            {
                throw new VideoException(
                    VideoErrorCode.DecodeFailed,
                    $"avcodec_receive_frame failed: {GetErrorMessage(error)} ({error}).");
            }

            error = ffmpeg.av_read_frame(_format, _packet);
            if (error < 0)
            {
                if (error == ffmpeg.AVERROR_EOF)
                {
                    error = ffmpeg.avcodec_send_packet(_videoCodec, null);
                    if (error < 0 && error != ffmpeg.AVERROR_EOF)
                    {
                        throw new VideoException(
                            VideoErrorCode.DecodeFailed,
                            $"avcodec_send_packet(drain) failed: {GetErrorMessage(error)} ({error}).");
                    }

                    _videoDraining = true;
                    FlushAudioAtEndOfStream();
                    continue;
                }

                ThrowReadError(error);
            }

            // Any successful demuxer read counts as progress and extends the stall deadline.
            _interrupt.Arm(_timeoutMs, cancellationToken);

            AVPacket* packet = _packet;
            if (packet->stream_index != _videoStreamIndex)
            {
                if (packet->stream_index == _audioStreamIndex && _audioFilter is not null)
                {
                    DecodeAudioPacket(packet);
                }

                ffmpeg.av_packet_unref(packet);
                continue;
            }

            error = ffmpeg.avcodec_send_packet(_videoCodec, packet);
            ffmpeg.av_packet_unref(packet);
            if (error < 0)
            {
                throw new VideoException(
                    VideoErrorCode.DecodeFailed,
                    $"avcodec_send_packet failed: {GetErrorMessage(error)} ({error}).");
            }

        }
    }

    public void Dispose()
    {
        lock (_nativeLock)
        {
            DisposeCore();
        }
    }

    private void DisposeCore()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_sws is not null)
        {
            ffmpeg.sws_freeContext(_sws);
            _sws = null;
        }

        if (_frame is not null)
        {
            AVFrame* frame = _frame;
            _frame = null;
            ffmpeg.av_frame_free(&frame);
        }

        if (_packet is not null)
        {
            AVPacket* packet = _packet;
            _packet = null;
            ffmpeg.av_packet_free(&packet);
        }

        if (_videoCodec is not null)
        {
            AVCodecContext* codec = _videoCodec;
            _videoCodec = null;
            ffmpeg.avcodec_free_context(&codec);
        }

        if (_audioCodec is not null)
        {
            AVCodecContext* codec = _audioCodec;
            _audioCodec = null;
            ffmpeg.avcodec_free_context(&codec);
        }

        if (_audioFilter is not null)
        {
            _audioFilter.Dispose();
            _audioFilter = null;
        }

        if (_format is not null)
        {
            AVFormatContext* format = _format;
            _format = null;
            ffmpeg.avformat_close_input(&format);
        }

        _videoStreamIndex = -1;
        _audioStreamIndex = -1;
        _videoDraining = false;
        _metadata = null;
    }

    // ---------------------------------------------------------------- interrupt state

    /// <summary>
    /// Abort state for the demuxer interrupt callback: cancellation plus a rolling
    /// no-progress deadline that is re-armed before each blocking phase and on every
    /// successful read. Written by the owner thread, read from the interrupt callback
    /// which FFmpeg invokes on that same thread; the cancellation token is the only
    /// cross-thread input and is thread-safe.
    /// </summary>
    private sealed class InterruptState
    {
        private CancellationToken _cancellationToken;
        private long _deadlineTicks;
        private bool _armed;

        public bool IsCanceled => _cancellationToken.IsCancellationRequested;

        public bool TimedOut => _armed && Stopwatch.GetTimestamp() > _deadlineTicks;

        public void Arm(int timeoutMs, CancellationToken cancellationToken)
        {
            _cancellationToken = cancellationToken;
            _deadlineTicks = Stopwatch.GetTimestamp() + (long)(timeoutMs * (double)Stopwatch.Frequency / 1000.0);
            _armed = true;
        }

        public bool ShouldAbort()
        {
            // Before the first Arm the callback must never block progress.
            return IsCanceled || TimedOut;
        }
    }

    // ---------------------------------------------------------------- codec open / metadata

    private void OpenVideoCodec()
    {
        AVStream* stream = _format->streams[_videoStreamIndex];
        AVCodecParameters* parameters = stream->codecpar;
        AVCodec* codec = ffmpeg.avcodec_find_decoder(parameters->codec_id);
        if (codec is null)
        {
            throw new VideoException(
                VideoErrorCode.UnsupportedStream,
                $"No FFmpeg decoder for codec id {parameters->codec_id}.");
        }

        _videoCodec = ffmpeg.avcodec_alloc_context3(codec);
        if (_videoCodec is null)
        {
            throw new VideoException(VideoErrorCode.OpenFailed, "avcodec_alloc_context3 failed for video.");
        }

        int error = ffmpeg.avcodec_parameters_to_context(_videoCodec, parameters);
        if (error < 0)
        {
            throw new VideoException(
                VideoErrorCode.OpenFailed,
                $"avcodec_parameters_to_context failed: {GetErrorMessage(error)} ({error}).");
        }

        _videoCodec->thread_count = 0; // auto: use available cores for software decode
        error = ffmpeg.avcodec_open2(_videoCodec, codec, null);
        if (error < 0)
        {
            throw new VideoException(
                VideoErrorCode.OpenFailed,
                $"avcodec_open2 failed for video: {GetErrorMessage(error)} ({error}).");
        }
    }

    private void OpenAudioCodec()
    {
        AVStream* stream = _format->streams[_audioStreamIndex];
        AVCodecParameters* parameters = stream->codecpar;
        AVCodec* codec = ffmpeg.avcodec_find_decoder(parameters->codec_id);
        if (codec is null)
        {
            // A silent result is acceptable; the session runs without audio.
            _audioStreamIndex = -1;
            return;
        }

        _audioCodec = ffmpeg.avcodec_alloc_context3(codec);
        int error = ffmpeg.avcodec_parameters_to_context(_audioCodec, parameters);
        if (error < 0 || ffmpeg.avcodec_open2(_audioCodec, codec, null) < 0)
        {
            AVCodecContext* failed = _audioCodec;
            ffmpeg.avcodec_free_context(&failed);
            _audioCodec = null;
            _audioStreamIndex = -1;
            return;
        }

        // Audio output pipeline: convert decoded frames to the engine's float32 format
        // with optional atempo speed change. Failure to build it degrades to silent.
        if (_pcmSink is not null && _targetAudioRate > 0 && _targetAudioChannels > 0)
        {
            try
            {
                Span<byte> layoutBuffer = stackalloc byte[256];
                fixed (byte* p = layoutBuffer)
                {
                    ffmpeg.av_channel_layout_describe(&_audioCodec->ch_layout, p, (ulong)layoutBuffer.Length);
                    int end = 0;
                    while (end < layoutBuffer.Length && p[end] != 0)
                    {
                        end++;
                    }

                    string layout = Encoding.UTF8.GetString(p, end);
                    _audioFilter = new FFmpegAudioFilter(
                        _audioCodec->sample_rate, layout, _audioCodec->sample_fmt,
                        _targetAudioRate, _targetAudioChannels == 1 ? "mono" : "stereo");
                    _audioFilter.SetSpeed(_audioSpeed);
                }
            }
            catch (VideoException ex)
            {
                Debug.LogWarning($"[Video] Audio filter unavailable, playing silent: {ex.Message}");
                _audioFilter?.Dispose();
                _audioFilter = null;
            }
        }
    }

    private const int AudioOnlyWidth = 2;
    private const int AudioOnlyHeight = 2;
    private const double AudioOnlyFps = 30;

    private PooledVideoFrame? DecodeAudioOnly(CancellationToken cancellationToken, ArrayPool<byte>? pool)
    {
        _interrupt.Arm(_timeoutMs, cancellationToken);
        if (_packet is null)
        {
            _packet = ffmpeg.av_packet_alloc();
        }

        int packets = 0;
        bool eof = false;
        while (packets < 64)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int error = ffmpeg.av_read_frame(_format, _packet);
            if (error < 0)
            {
                if (error == ffmpeg.AVERROR_EOF)
                {
                    FlushAudioAtEndOfStream();
                    eof = true;
                    break;
                }

                ThrowReadError(error);
            }

            _interrupt.Arm(_timeoutMs, cancellationToken);
            if (_packet->stream_index == _audioStreamIndex && _audioFilter is not null)
            {
                DecodeAudioPacket(_packet);
            }

            ffmpeg.av_packet_unref(_packet);
            packets++;
        }

        if (eof)
        {
            if (_audioOnlyEnded)
            {
                return null;
            }

            _audioOnlyEnded = true;
        }

        double pts = _audioOnlyPts;
        _audioOnlyPts += 1.0 / AudioOnlyFps;
        return AllocDummyFrame(pool, pts);
    }

    private static PooledVideoFrame AllocDummyFrame(ArrayPool<byte>? pool, double pts)
    {
        const int bytes = AudioOnlyWidth * AudioOnlyHeight * 4;
        byte[] data = pool is null ? new byte[bytes] : pool.Rent(bytes);
        Array.Clear(data, 0, bytes);
        return new PooledVideoFrame(pool, data, bytes, AudioOnlyWidth, AudioOnlyHeight, pts);
    }

    private VideoMediaInfo BuildMetadata()
    {
        AVFormatContext* format = _format;
        if (_audioOnly || _videoStreamIndex < 0)
        {
            AVStream* audioStream = _audioStreamIndex >= 0 ? format->streams[_audioStreamIndex] : null;
            double audioDuration = format->duration > 0
                ? format->duration / (double)ffmpeg.AV_TIME_BASE
                : audioStream is not null && audioStream->duration > 0
                    ? audioStream->duration * StreamTimeBaseSeconds(audioStream)
                    : 0;
            double audioStart = format->start_time != NoPtsValue
                ? format->start_time / (double)ffmpeg.AV_TIME_BASE
                : 0;
            VideoStreamAudioInfo? audioOnly = null;
            if (_audioStreamIndex >= 0 && _audioCodec is not null)
            {
                AVCodecParameters* audioParameters = format->streams[_audioStreamIndex]->codecpar;
                audioOnly = new VideoStreamAudioInfo
                {
                    Codec = ffmpeg.avcodec_get_name(audioParameters->codec_id),
                    SampleRate = audioParameters->sample_rate,
                    Channels = audioParameters->ch_layout.nb_channels,
                    Profile = ffmpeg.avcodec_profile_name(audioParameters->codec_id, audioParameters->profile),
                };
            }

            return new VideoMediaInfo
            {
                Container = PtrToString(format->iformat->name) ?? "unknown",
                DurationSeconds = audioDuration,
                StartTimeSeconds = audioStart,
                BitRate = format->bit_rate > 0 ? format->bit_rate : 0,
                Video = new VideoStreamInfo
                {
                    Width = AudioOnlyWidth,
                    Height = AudioOnlyHeight,
                    CodedWidth = AudioOnlyWidth,
                    CodedHeight = AudioOnlyHeight,
                    Codec = "none",
                    Profile = null,
                    Fps = AudioOnlyFps,
                    RotationDegrees = 0,
                    SampleAspectNum = 1,
                    SampleAspectDen = 1,
                },
                Audio = audioOnly,
                CanSeek = format->pb is not null && ffmpeg.avio_seek(format->pb, 0, 1) >= 0,
            };
        }

        AVStream* videoStream = format->streams[_videoStreamIndex];
        AVCodecParameters* videoParameters = videoStream->codecpar;

        double duration = format->duration > 0
            ? format->duration / (double)ffmpeg.AV_TIME_BASE
            : videoStream->duration > 0
                ? videoStream->duration * StreamTimeBaseSeconds(videoStream)
                : 0;

        double startTime = format->start_time != NoPtsValue
            ? format->start_time / (double)ffmpeg.AV_TIME_BASE
            : 0;

        AVRational fps = ffmpeg.av_guess_frame_rate(format, videoStream, null);
        double fpsValue = fps.num > 0 && fps.den > 0 ? fps.num / (double)fps.den : 0;

        // Sample aspect ratio: the stream value wins when present, else the codec one.
        AVRational sar = videoStream->sample_aspect_ratio.num > 0
            ? videoStream->sample_aspect_ratio
            : videoParameters->sample_aspect_ratio;
        int sarNum = sar.num > 0 && sar.den > 0 ? sar.num : 1;
        int sarDen = sar.num > 0 && sar.den > 0 ? sar.den : 1;

        int displayWidth = sarNum == sarDen
            ? videoParameters->width
            : (int)Math.Round(videoParameters->width * sarNum / (double)sarDen);

        int rotation = ReadRotationDegrees(videoParameters);
        string? profile = ffmpeg.avcodec_profile_name(videoParameters->codec_id, videoParameters->profile);

        VideoStreamAudioInfo? audio = null;
        if (_audioStreamIndex >= 0 && _audioCodec is not null)
        {
            AVCodecParameters* audioParameters = _format->streams[_audioStreamIndex]->codecpar;
            audio = new VideoStreamAudioInfo
            {
                Codec = ffmpeg.avcodec_get_name(audioParameters->codec_id),
                SampleRate = audioParameters->sample_rate,
                Channels = audioParameters->ch_layout.nb_channels,
                Profile = ffmpeg.avcodec_profile_name(audioParameters->codec_id, audioParameters->profile),
            };
        }

        bool canSeek = false;
        if (format->pb is not null)
        {
            // Do NOT read pb->seekable through the generated AVIOContext: its unsigned-long
            // checksum field is generated as CULong (8 bytes) while Windows C 'unsigned long'
            // is 4 bytes, misaligning every field after it by 8 on Windows. A zero-offset
            // SEEK_CUR goes through the real function and succeeds exactly when seekable.
            canSeek = ffmpeg.avio_seek(format->pb, 0, 1 /* SEEK_CUR */) >= 0;
        }

        return new VideoMediaInfo
        {
            Container = PtrToString(format->iformat->name) ?? "unknown",
            DurationSeconds = duration,
            StartTimeSeconds = startTime,
            BitRate = format->bit_rate > 0 ? format->bit_rate : 0,
            Video = new VideoStreamInfo
            {
                Width = displayWidth > 0 ? displayWidth : videoParameters->width,
                Height = videoParameters->height,
                CodedWidth = videoParameters->width,
                CodedHeight = videoParameters->height,
                Codec = ffmpeg.avcodec_get_name(videoParameters->codec_id),
                Profile = profile,
                Fps = fpsValue,
                RotationDegrees = rotation,
                SampleAspectNum = sarNum,
                SampleAspectDen = sarDen,
            },
            Audio = audio,
            CanSeek = canSeek,
        };
    }

    /// <summary>
    /// Reads the display-matrix side data (phone-recorded portrait video) and normalizes
    /// the stored rotation to 0/90/180/270. Mirrors libavutil av_display_rotation_get.
    /// </summary>
    private static int ReadRotationDegrees(AVCodecParameters* parameters)
    {
        if (parameters->coded_side_data is null || parameters->nb_coded_side_data <= 0)
        {
            return 0;
        }

        AVPacketSideData* sideData = ffmpeg.av_packet_side_data_get(
            parameters->coded_side_data,
            parameters->nb_coded_side_data,
            AVPacketSideDataType.AV_PKT_DATA_DISPLAYMATRIX);
        if (sideData is null || sideData->size < 9 * sizeof(int))
        {
            return 0;
        }

        var matrix = new int[9];
        Marshal.Copy((nint)sideData->data, matrix, 0, 9);

        double m0 = matrix[0] / (double)FixedPointOne;
        double m1 = matrix[1] / (double)FixedPointOne;
        double m3 = matrix[3] / (double)FixedPointOne;
        double m4 = matrix[4] / (double)FixedPointOne;
        double scale0 = Math.Sqrt(m0 * m0 + m3 * m3);
        double scale1 = Math.Sqrt(m1 * m1 + m4 * m4);
        if (scale0 == 0 || scale1 == 0)
        {
            return 0;
        }

        double rotation = -Math.Atan2(m1 / scale1, m0 / scale0) * 180.0 / Math.PI;
        return ((int)Math.Round(rotation / 90.0) % 4 + 4) % 4 * 90;
    }

    private static double StreamTimeBaseSeconds(AVStream* stream)
    {
        AVRational timeBase = stream->time_base;
        return timeBase.den > 0 ? timeBase.num / (double)timeBase.den : 0;
    }

    // ---------------------------------------------------------------- frame conversion

    private VideoFrame ConvertFrameToRgba()
    {
        AVFrame* frame = _frame;
        int width = frame->width;
        int height = frame->height;
        var sourceFormat = (AVPixelFormat)frame->format;

        _sws = ffmpeg.sws_getCachedContext(
            _sws,
            width,
            height,
            sourceFormat,
            width,
            height,
            AVPixelFormat.AV_PIX_FMT_RGBA,
            (int)SwsFlags.SWS_BILINEAR,
            null,
            null,
            null);
        if (_sws is null)
        {
            throw new VideoException(VideoErrorCode.DecodeFailed, "sws_getCachedContext failed.");
        }

        int byteCount = ffmpeg.av_image_get_buffer_size(AVPixelFormat.AV_PIX_FMT_RGBA, width, height, 1);
        if (byteCount <= 0)
        {
            throw new VideoException(VideoErrorCode.DecodeFailed, "av_image_get_buffer_size failed.");
        }

        var pixels = new byte[byteCount];
        fixed (byte* dstPtr = pixels)
        {
            byte*[] dstData = [(byte*)dstPtr];
            int[] dstLinesize = [width * 4];
            int error = ffmpeg.sws_scale(_sws, frame->data, frame->linesize, 0, height, dstData, dstLinesize);
            if (error < 0)
            {
                throw new VideoException(
                    VideoErrorCode.DecodeFailed,
                    $"sws_scale failed: {GetErrorMessage(error)} ({error}).");
            }
        }

        // PTS is expressed in the video stream's time_base.
        double pts = frame->pts != NoPtsValue
            ? frame->pts * StreamTimeBaseSeconds(_format->streams[_videoStreamIndex])
            : 0;

        return new VideoFrame
        {
            Data = pixels,
            Width = width,
            Height = height,
            PtsSeconds = pts,
        };
    }

    /// <summary>Pooled variant used by the playback streaming path.</summary>
    private PooledVideoFrame ConvertFrameToRgbaPooled(ArrayPool<byte> pool)
    {
        AVFrame* frame = _frame;
        int width = frame->width;
        int height = frame->height;
        var sourceFormat = (AVPixelFormat)frame->format;

        _sws = ffmpeg.sws_getCachedContext(
            _sws,
            width,
            height,
            sourceFormat,
            width,
            height,
            AVPixelFormat.AV_PIX_FMT_RGBA,
            (int)SwsFlags.SWS_BILINEAR,
            null,
            null,
            null);
        if (_sws is null)
        {
            throw new VideoException(VideoErrorCode.DecodeFailed, "sws_getCachedContext failed.");
        }

        int byteCount = ffmpeg.av_image_get_buffer_size(AVPixelFormat.AV_PIX_FMT_RGBA, width, height, 1);
        if (byteCount <= 0)
        {
            throw new VideoException(VideoErrorCode.DecodeFailed, "av_image_get_buffer_size failed.");
        }

        byte[] pixels = pool.Rent(byteCount);
        bool success = false;
        try
        {
            fixed (byte* dstPtr = pixels)
            {
                byte*[] dstData = [(byte*)dstPtr];
                int[] dstLinesize = [width * 4];
                int error = ffmpeg.sws_scale(_sws, frame->data, frame->linesize, 0, height, dstData, dstLinesize);
                if (error < 0)
                {
                    throw new VideoException(
                        VideoErrorCode.DecodeFailed,
                        $"sws_scale failed: {GetErrorMessage(error)} ({error}).");
                }
            }

            success = true;
        }
        finally
        {
            if (!success)
            {
                pool.Return(pixels);
            }
        }

        double pts = frame->pts != NoPtsValue
            ? frame->pts * StreamTimeBaseSeconds(_format->streams[_videoStreamIndex])
            : 0;

        return new PooledVideoFrame(pool, pixels, byteCount, width, height, pts);
    }

    // ---------------------------------------------------------------- error helpers

    private VideoException MapOpenError(int error, string uri)
    {
        if (_interrupt.IsCanceled)
        {
            return new VideoException(VideoErrorCode.Canceled, $"Opening '{uri}' was canceled.");
        }

        if (_interrupt.TimedOut)
        {
            return new VideoException(VideoErrorCode.Timeout, $"Opening '{uri}' timed out.");
        }

        return new VideoException(
            VideoErrorCode.OpenFailed,
            $"Opening '{uri}' failed: {GetErrorMessage(error)} ({error}).");
    }

    private void ThrowReadError(int error)
    {
        if (error == ffmpeg.AVERROR_EOF)
        {
            throw new VideoException(VideoErrorCode.DecodeFailed, "Stream ended before the first frame was decoded.");
        }

        if (_interrupt.IsCanceled)
        {
            throw new VideoException(VideoErrorCode.Canceled, "Decoding was canceled while reading.");
        }

        if (_interrupt.TimedOut)
        {
            throw new VideoException(VideoErrorCode.Timeout, "Decoding made no progress within the timeout.");
        }

        throw new VideoException(
            VideoErrorCode.InvalidData,
            $"av_read_frame failed: {GetErrorMessage(error)} ({error}).");
    }

    private static string GetErrorMessage(int errorCode)
    {
        Span<byte> buffer = stackalloc byte[1024];
        fixed (byte* p = buffer)
        {
            ffmpeg.av_strerror(errorCode, p, (ulong)buffer.Length);
            int end = 0;
            while (end < buffer.Length && p[end] != 0)
            {
                end++;
            }

            return Encoding.UTF8.GetString(p, end);
        }
    }

    private static string? PtrToString(byte* pointer)
    {
        return pointer is null ? null : Marshal.PtrToStringAnsi((nint)pointer);
    }
}
