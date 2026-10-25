// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Runtime.InteropServices;
using System.Text;

using FFmpeg.AutoGen;

namespace XEngine.Runtime.Video;

/// <summary>
/// Converts decoded audio frames to the engine's output format (packed float32,
/// engine sample rate, engine channel count) with optional pitch-preserving speed
/// change, built as a libavfilter graph:
/// <c>abuffer → [atempo] → aformat(flt) → buffersink</c>.
/// Owned by the decode worker alongside <see cref="FFmpegVideoDecoder"/>; rebuilt when
/// the playback speed changes (atempo only supports building fresh graphs).
/// </summary>
internal sealed unsafe class FFmpegAudioFilter : IDisposable
{
    private readonly int _sourceRate;
    private readonly string _sourceLayout;
    private readonly AVSampleFormat _sourceFormat;
    private readonly int _targetRate;
    private readonly string _targetLayout;

    private AVFilterGraph* _graph;
    private AVFilterContext* _src;
    private AVFilterContext* _sink;
    private float _speed = 1f;
    private bool _flushing;

    public FFmpegAudioFilter(
        int sourceRate, string sourceLayout, AVSampleFormat sourceFormat,
        int targetRate, string targetLayout)
    {
        _sourceRate = sourceRate;
        _sourceLayout = sourceLayout;
        _sourceFormat = sourceFormat;
        _targetRate = targetRate;
        _targetLayout = targetLayout;
        BuildGraph();
    }

    public double OutputSecondsPerInputSecond => 1.0 / _speed;

    /// <summary>Rebuilds the graph for a new playback speed; pending filtered audio is dropped.</summary>
    public void SetSpeed(float speed)
    {
        float clamped = Math.Clamp(speed, 0.5f, 2f);
        if (clamped == _speed && _graph is not null)
        {
            return;
        }

        _speed = clamped;
        _flushing = false;
        BuildGraph();
    }

    private void BuildGraph()
    {
        FreeGraph();

        _graph = ffmpeg.avfilter_graph_alloc();
        if (_graph is null)
        {
            throw new VideoException(VideoErrorCode.AudioOutputFailed, "avfilter_graph_alloc failed.");
        }

        string srcArgs =
            $"time_base=1/{_sourceRate}:sample_rate={_sourceRate}:sample_fmt={ffmpeg.av_get_sample_fmt_name(_sourceFormat)}:channel_layout={_sourceLayout}";

        AVFilter* buffersrc = ffmpeg.avfilter_get_by_name("abuffer");
        AVFilter* buffersink = ffmpeg.avfilter_get_by_name("abuffersink");
        AVFilter* aformat = ffmpeg.avfilter_get_by_name("aformat");
        if (buffersrc is null || buffersink is null || aformat is null)
        {
            throw new VideoException(VideoErrorCode.AudioOutputFailed, "FFmpeg abuffer/buffersink/aformat filters not available.");
        }

        AVFilterContext* src = null;
        AVFilterContext* sink = null;
        int error = ffmpeg.avfilter_graph_create_filter(&src, buffersrc, "in", srcArgs, null, _graph);
        if (error < 0)
        {
            throw new VideoException(
                VideoErrorCode.AudioOutputFailed,
                $"abuffer init failed: {DescribeError(error)} ({error}).");
        }

        error = ffmpeg.avfilter_graph_create_filter(&sink, buffersink, "out", null, null, _graph);
        if (error < 0)
        {
            throw new VideoException(
                VideoErrorCode.AudioOutputFailed,
                $"buffersink init failed: {DescribeError(error)} ({error}).");
        }

        _src = src;
        _sink = sink;

        // Manual chain: src → [atempo] → aformat → sink. FFmpeg 8's aformat uses the
        // "ch_layouts" option name (the pre-5.1 "channel_layouts" no longer parses).
        AVFilterContext* previous = _src;
        if (_speed != 1f)
        {
            AVFilter* atempo = ffmpeg.avfilter_get_by_name("atempo");
            string tempoArgs = $"tempo={_speed.ToString(System.Globalization.CultureInfo.InvariantCulture)}";
            AVFilterContext* tempoCtx = null;
            error = ffmpeg.avfilter_graph_create_filter(&tempoCtx, atempo, "tempo", tempoArgs, null, _graph);
            if (error < 0)
            {
                throw new VideoException(
                    VideoErrorCode.AudioOutputFailed,
                    $"atempo init failed: {DescribeError(error)} ({error}).");
            }

            error = ffmpeg.avfilter_link(previous, 0, tempoCtx, 0);
            if (error < 0)
            {
                throw new VideoException(
                    VideoErrorCode.AudioOutputFailed,
                    $"avfilter_link(atempo) failed: {DescribeError(error)} ({error}).");
            }

            previous = tempoCtx;
        }

        // aformat constraints are parsed at filter init — they MUST ride the args string.
        string formatArgs =
            $"sample_fmts=flt:sample_rates={_targetRate.ToString(System.Globalization.CultureInfo.InvariantCulture)}:channel_layouts={_targetLayout}";
        AVFilterContext* formatCtx = null;
        error = ffmpeg.avfilter_graph_create_filter(&formatCtx, aformat, "format", formatArgs, null, _graph);
        if (error < 0)
        {
            throw new VideoException(
                VideoErrorCode.AudioOutputFailed,
                $"aformat init failed: {DescribeError(error)} ({error}).");
        }

        error = ffmpeg.avfilter_link(previous, 0, formatCtx, 0);
        if (error < 0)
        {
            throw new VideoException(
                VideoErrorCode.AudioOutputFailed,
                $"avfilter_link(aformat) failed: {DescribeError(error)} ({error}).");
        }

        error = ffmpeg.avfilter_link(formatCtx, 0, _sink, 0);
        if (error < 0)
        {
            throw new VideoException(
                VideoErrorCode.AudioOutputFailed,
                $"avfilter_link(sink) failed: {DescribeError(error)} ({error}).");
        }
        error = ffmpeg.avfilter_graph_config(_graph, null);
        if (error < 0)
        {
            throw new VideoException(
                VideoErrorCode.AudioOutputFailed,
                $"avfilter_graph_config failed: {DescribeError(error)} ({error}).");
        }
    }

    /// <summary>Pushes a decoded source frame into the graph (frame is left unmodified).</summary>
    public void Push(AVFrame* frame)
    {
        int error = ffmpeg.av_buffersrc_add_frame_flags(_src, frame, (int)AvBuffersrcFlag.AV_BUFFERSRC_FLAG_KEEP_REF);
        if (error < 0)
        {
            throw new VideoException(
                VideoErrorCode.AudioOutputFailed,
                $"av_buffersrc_add_frame_flags failed: {DescribeError(error)} ({error}).");
        }
    }

    /// <summary>Marks end-of-stream so the graph drains its internal pipeline.</summary>
    public void EndOfStream() => _flushing = true;

    /// <summary>
    /// Pulls one converted output frame. Returns false when the graph is empty
    /// (or fully drained after <see cref="EndOfStream"/>).
    /// </summary>
    /// <param name="samples">Interleaved float32 output samples (managed copy).</param>
    /// <param name="count">Sample count (frames × channels).</param>
    /// <param name="outputPtsSeconds">PTS of the frame on the OUTPUT timeline, in seconds.</param>
    public bool TryPull(out float[] samples, out int count, out double outputPtsSeconds)
    {
        samples = [];
        count = 0;
        outputPtsSeconds = 0;

        if (_graph is null)
        {
            return false;
        }

        AVFrame* frame = ffmpeg.av_frame_alloc();
        try
        {
            int error = ffmpeg.av_buffersink_get_frame(_sink, frame);
            if (error < 0)
            {
                return false;
            }

            int channels = frame->ch_layout.nb_channels;
            count = frame->nb_samples * channels;
            samples = new float[count];
            Marshal.Copy((nint)frame->data[0], samples, 0, count);
            outputPtsSeconds = frame->pts / (double)_targetRate;
            return true;
        }
        finally
        {
            ffmpeg.av_frame_free(&frame);
        }
    }

    private void FreeGraph()
    {
        if (_graph is not null)
        {
            AVFilterGraph* graph = _graph;
            _graph = null;
            _src = null;
            _sink = null;
            ffmpeg.avfilter_graph_free(&graph);
        }
    }

    public void Dispose()
    {
        FreeGraph();
    }

    internal static string DescribeError(int errorCode)
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


}
