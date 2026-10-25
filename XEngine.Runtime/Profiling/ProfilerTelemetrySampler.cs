// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Diagnostics;
using System.Net;
using XEngine.Runtime.RHI;

namespace XEngine.Runtime.Profiling;

/// <summary>
/// Captures portable player profiler frames. Metrics without a portable runtime query are retained
/// as explicit unavailable fields so device captures never invent values from unrelated counters.
/// </summary>
public static class ProfilerTelemetrySampler
{
    private static Process? s_process;
    private static long s_lastTimestamp;
    private static TimeSpan s_lastCpuTime;

    /// <summary>Captures one frame using the native target identity for the current player.</summary>
    public static ProfilerTelemetryFrame Capture(ulong frameIndex) => Capture(frameIndex, GetCurrentTarget());

    /// <summary>Captures one frame with an explicit target identity, primarily for host integrations.</summary>
    public static ProfilerTelemetryFrame Capture(ulong frameIndex, ProfilerTelemetryTarget target)
    {
        long timestamp = Stopwatch.GetTimestamp();
        TryGetProcessMetrics(timestamp, out long workingSetBytes, out float processCpuPercent,
            out ProfilerMetricSource workingSetSource, out ProfilerMetricSource processCpuSource);

        long managedHeapBytes = GC.GetTotalMemory(forceFullCollection: false);
        IGraphicsDevice? device = Graphics.Device;
        ProfilerTelemetryCapabilities capabilities = ProfilerTelemetryCapabilities.None;
        if (device?.Capabilities.HardwareSupportsTimestampQueries == true)
            capabilities |= ProfilerTelemetryCapabilities.GpuTimestampQueries;

        float gpuFrameTimeMs = 0f;
        if (Graphics.LastGpuTimestampFrame is GpuTimestampFrame gpuFrame)
        {
            gpuFrameTimeMs = gpuFrame.DurationMs;
            capabilities |= ProfilerTelemetryCapabilities.GpuFrameTimestampSample;
        }

        return new ProfilerTelemetryFrame(
            DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            frameIndex,
            processCpuPercent,
            gpuFrameTimeMs,
            workingSetBytes,
            managedHeapBytes,
            RhiMemoryBytes: 0,
            AdapterVramBudgetBytes: 0,
            device?.Backend ?? GraphicsBackend.Null,
            target,
            capabilities,
            processCpuSource,
            workingSetSource,
            ProfilerMetricSource.Runtime,
            ProfilerMetricSource.Unavailable,
            ProfilerMetricSource.Unavailable);
    }

    internal static void ResetForTests()
    {
        s_lastTimestamp = 0;
        s_lastCpuTime = default;
    }

    private static void TryGetProcessMetrics(long timestamp, out long workingSetBytes,
        out float processCpuPercent, out ProfilerMetricSource workingSetSource,
        out ProfilerMetricSource processCpuSource)
    {
        workingSetBytes = 0;
        processCpuPercent = 0f;
        workingSetSource = ProfilerMetricSource.Unavailable;
        processCpuSource = ProfilerMetricSource.Unavailable;
        try
        {
            Process process = s_process ??= Process.GetCurrentProcess();
            process.Refresh();
            workingSetBytes = Math.Max(0, process.WorkingSet64);
            workingSetSource = ProfilerMetricSource.OperatingSystem;

            TimeSpan cpu = process.TotalProcessorTime;
            if (s_lastTimestamp != 0)
            {
                double elapsedMs = (timestamp - s_lastTimestamp) * 1000.0 / Stopwatch.Frequency;
                double cpuMs = (cpu - s_lastCpuTime).TotalMilliseconds;
                if (elapsedMs > 0 && Environment.ProcessorCount > 0)
                {
                    processCpuPercent = (float)Math.Clamp(
                        cpuMs / elapsedMs / Environment.ProcessorCount * 100.0, 0.0, 100.0);
                    processCpuSource = ProfilerMetricSource.OperatingSystem;
                }
            }

            s_lastTimestamp = timestamp;
            s_lastCpuTime = cpu;
        }
        catch (Exception ex) when (ex is InvalidOperationException or PlatformNotSupportedException or NotSupportedException)
        {
            // Restricted player platforms keep the fields explicitly unavailable.
        }
    }

    internal static ProfilerTelemetryTarget GetCurrentTarget()
    {
#if XENGINE_ANDROID
        return ProfilerTelemetryTarget.AndroidPlayer;
#elif XENGINE_HARMONYOS
        return ProfilerTelemetryTarget.HarmonyOSPlayer;
#else
        return ProfilerTelemetryTarget.DesktopPlayer;
#endif
    }
}

/// <summary>Rate-limited player publisher for direct-IP profiler capture.</summary>
public sealed class ProfilerTelemetryPublisher : IDisposable
{
    public const double MinimumPublishIntervalSeconds = 0.25;

    private readonly ProfilerTelemetrySender _sender;
    private readonly ProfilerTelemetryTarget _target;
    private long _lastPublishedTimestamp;
    private ulong _nextFrameIndex;

    public ProfilerTelemetryPublisher(IPEndPoint remoteEndpoint)
        : this(remoteEndpoint, ProfilerTelemetrySampler.GetCurrentTarget())
    {
    }

    public ProfilerTelemetryPublisher(IPEndPoint remoteEndpoint, ProfilerTelemetryTarget target)
    {
        _sender = new ProfilerTelemetrySender(remoteEndpoint);
        _target = target;
    }

    /// <summary>Publishes a sampled frame when the bounded capture cadence has elapsed.</summary>
    public bool TryPublish()
    {
        long now = Stopwatch.GetTimestamp();
        if (_lastPublishedTimestamp != 0
            && (now - _lastPublishedTimestamp) / (double)Stopwatch.Frequency < MinimumPublishIntervalSeconds)
            return false;

        _lastPublishedTimestamp = now;
        ProfilerTelemetryFrame frame = ProfilerTelemetrySampler.Capture(++_nextFrameIndex, _target);
        return _sender.TrySend(frame);
    }

    public void Dispose() => _sender.Dispose();
}

/// <summary>Parses the direct-IP player switch without resolving host names or opening a connection.</summary>
public static class ProfilerTelemetryCommandLine
{
    public const string EndpointArgument = "--profiler-udp";

    /// <summary>Finds <c>--profiler-udp=&lt;IP:port&gt;</c> or a separated switch value.</summary>
    public static bool TryGetEndpoint(string[] arguments, out IPEndPoint endpoint)
    {
        ArgumentNullException.ThrowIfNull(arguments);
        endpoint = null!;
        for (int i = 0; i < arguments.Length; i++)
        {
            string argument = arguments[i];
            if (argument.StartsWith(EndpointArgument + "=", StringComparison.Ordinal))
                return TryParseEndpoint(argument.Substring(EndpointArgument.Length + 1), out endpoint);
            if (argument == EndpointArgument && i + 1 < arguments.Length)
                return TryParseEndpoint(arguments[i + 1], out endpoint);
        }
        return false;
    }

    /// <summary>Parses IPv4 <c>address:port</c> and bracketed IPv6 <c>[address]:port</c> endpoints.</summary>
    public static bool TryParseEndpoint(string value, out IPEndPoint endpoint)
    {
        endpoint = null!;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        string host;
        string portText;
        if (value[0] == '[')
        {
            int close = value.IndexOf(']');
            if (close <= 1 || close + 1 >= value.Length || value[close + 1] != ':')
                return false;
            host = value.Substring(1, close - 1);
            portText = value.Substring(close + 2);
        }
        else
        {
            int separator = value.LastIndexOf(':');
            if (separator <= 0 || separator == value.Length - 1 || value.IndexOf(':') != separator)
                return false;
            host = value.Substring(0, separator);
            portText = value.Substring(separator + 1);
        }

        if (!IPAddress.TryParse(host, out IPAddress? address)
            || !int.TryParse(portText, out int port)
            || port is < 1 or > 65535)
            return false;

        endpoint = new IPEndPoint(address, port);
        return true;
    }
}
