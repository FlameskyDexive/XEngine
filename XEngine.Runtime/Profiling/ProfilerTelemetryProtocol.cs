// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using XEngine.Runtime.RHI;

namespace XEngine.Runtime.Profiling;

/// <summary>Identifies the producer of an individual profiler metric.</summary>
public enum ProfilerMetricSource : byte
{
    Unavailable = 0,
    OperatingSystem = 1,
    Runtime = 2,
    Rhi = 3,
    Platform = 4,
}

/// <summary>Identifies the runtime reporting a profiler frame.</summary>
public enum ProfilerTelemetryTarget : byte
{
    Unknown = 0,
    LocalEditor = 1,
    DesktopPlayer = 2,
    AndroidPlayer = 3,
    HarmonyOSPlayer = 4,
}

/// <summary>Capabilities advertised with every profiler frame.</summary>
[Flags]
public enum ProfilerTelemetryCapabilities : ushort
{
    None = 0,
    GpuTimestampQueries = 1 << 0,
    RhiMemory = 1 << 1,
    AdapterVramBudget = 1 << 2,
    GpuFrameTimestampSample = 1 << 3,
}

/// <summary>
/// One portable profiler sample. Values whose source is <see cref="ProfilerMetricSource.Unavailable"/>
/// are zero and must not be presented as a measured platform value.
/// </summary>
public readonly record struct ProfilerTelemetryFrame(
    long TimestampUnixMilliseconds,
    ulong FrameIndex,
    float ProcessCpuPercent,
    float GpuFrameTimeMs,
    long WorkingSetBytes,
    long ManagedHeapBytes,
    long RhiMemoryBytes,
    long AdapterVramBudgetBytes,
    GraphicsBackend Backend,
    ProfilerTelemetryTarget Target,
    ProfilerTelemetryCapabilities Capabilities,
    ProfilerMetricSource ProcessCpuSource,
    ProfilerMetricSource WorkingSetSource,
    ProfilerMetricSource ManagedHeapSource,
    ProfilerMetricSource RhiMemorySource,
    ProfilerMetricSource AdapterVramBudgetSource)
{
    public bool HasGpuFrameTime => (Capabilities & ProfilerTelemetryCapabilities.GpuFrameTimestampSample) != 0;
}

/// <summary>
/// Fixed-size versioned wire format for profiler frames. Datagram-sized frames bound both network
/// memory and parser work; a receiver accepts exactly one schema version at a time.
/// </summary>
public static class ProfilerTelemetryProtocol
{
    public const ushort Version = 1;
    public const int PacketSize = 72;

    private const uint Magic = 0x4D4C5058; // "XPLM" in little-endian byte order.
    private const ushort KnownCapabilityMask = (ushort)(ProfilerTelemetryCapabilities.GpuTimestampQueries
        | ProfilerTelemetryCapabilities.RhiMemory
        | ProfilerTelemetryCapabilities.AdapterVramBudget
        | ProfilerTelemetryCapabilities.GpuFrameTimestampSample);
    private const int HeaderSize = 8;
    private const int TimestampOffset = HeaderSize;
    private const int FrameIndexOffset = TimestampOffset + sizeof(long);
    private const int ProcessCpuOffset = FrameIndexOffset + sizeof(ulong);
    private const int GpuFrameOffset = ProcessCpuOffset + sizeof(float);
    private const int WorkingSetOffset = GpuFrameOffset + sizeof(float);
    private const int ManagedHeapOffset = WorkingSetOffset + sizeof(long);
    private const int RhiMemoryOffset = ManagedHeapOffset + sizeof(long);
    private const int AdapterVramBudgetOffset = RhiMemoryOffset + sizeof(long);
    private const int BackendOffset = AdapterVramBudgetOffset + sizeof(long);
    private const int TargetOffset = BackendOffset + sizeof(byte);
    private const int CapabilityOffset = TargetOffset + sizeof(byte);
    private const int SourceOffset = CapabilityOffset + sizeof(ushort);
    private const int ReservedOffset = SourceOffset + sizeof(ushort);

    /// <summary>Writes one complete fixed-size packet without allocating.</summary>
    public static bool TryWrite(in ProfilerTelemetryFrame frame, Span<byte> destination, out int written)
    {
        written = 0;
        if (destination.Length < PacketSize || !IsValid(frame))
            return false;

        Span<byte> packet = destination.Slice(0, PacketSize);
        packet.Clear();
        BinaryPrimitives.WriteUInt32LittleEndian(packet, Magic);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.Slice(4), Version);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.Slice(6), PacketSize);
        BinaryPrimitives.WriteInt64LittleEndian(packet.Slice(TimestampOffset), frame.TimestampUnixMilliseconds);
        BinaryPrimitives.WriteUInt64LittleEndian(packet.Slice(FrameIndexOffset), frame.FrameIndex);
        WriteSingle(packet.Slice(ProcessCpuOffset), frame.ProcessCpuPercent);
        WriteSingle(packet.Slice(GpuFrameOffset), frame.GpuFrameTimeMs);
        BinaryPrimitives.WriteInt64LittleEndian(packet.Slice(WorkingSetOffset), frame.WorkingSetBytes);
        BinaryPrimitives.WriteInt64LittleEndian(packet.Slice(ManagedHeapOffset), frame.ManagedHeapBytes);
        BinaryPrimitives.WriteInt64LittleEndian(packet.Slice(RhiMemoryOffset), frame.RhiMemoryBytes);
        BinaryPrimitives.WriteInt64LittleEndian(packet.Slice(AdapterVramBudgetOffset), frame.AdapterVramBudgetBytes);
        packet[BackendOffset] = (byte)frame.Backend;
        packet[TargetOffset] = (byte)frame.Target;
        BinaryPrimitives.WriteUInt16LittleEndian(packet.Slice(CapabilityOffset), (ushort)frame.Capabilities);
        BinaryPrimitives.WriteUInt16LittleEndian(packet.Slice(SourceOffset), PackSources(frame));
        BinaryPrimitives.WriteUInt16LittleEndian(packet.Slice(ReservedOffset), 0);
        written = PacketSize;
        return true;
    }

    /// <summary>Validates and reads a complete packet. Short, oversized, and unknown-version data is rejected.</summary>
    public static bool TryRead(ReadOnlySpan<byte> packet, out ProfilerTelemetryFrame frame)
    {
        frame = default;
        if (packet.Length != PacketSize
            || BinaryPrimitives.ReadUInt32LittleEndian(packet) != Magic
            || BinaryPrimitives.ReadUInt16LittleEndian(packet.Slice(4)) != Version
            || BinaryPrimitives.ReadUInt16LittleEndian(packet.Slice(6)) != PacketSize
            || BinaryPrimitives.ReadUInt16LittleEndian(packet.Slice(ReservedOffset)) != 0)
            return false;

        ushort capabilities = BinaryPrimitives.ReadUInt16LittleEndian(packet.Slice(CapabilityOffset));
        if ((capabilities & ~KnownCapabilityMask) != 0)
            return false;

        ushort sources = BinaryPrimitives.ReadUInt16LittleEndian(packet.Slice(SourceOffset));
        ProfilerMetricSource processCpuSource = UnpackSource(sources, 0);
        ProfilerMetricSource workingSetSource = UnpackSource(sources, 1);
        ProfilerMetricSource managedHeapSource = UnpackSource(sources, 2);
        ProfilerMetricSource rhiMemorySource = UnpackSource(sources, 3);
        ProfilerMetricSource adapterVramBudgetSource = UnpackSource(sources, 4);
        if (!IsKnownSource(processCpuSource)
            || !IsKnownSource(workingSetSource)
            || !IsKnownSource(managedHeapSource)
            || !IsKnownSource(rhiMemorySource)
            || !IsKnownSource(adapterVramBudgetSource))
            return false;

        frame = new ProfilerTelemetryFrame(
            BinaryPrimitives.ReadInt64LittleEndian(packet.Slice(TimestampOffset)),
            BinaryPrimitives.ReadUInt64LittleEndian(packet.Slice(FrameIndexOffset)),
            ReadSingle(packet.Slice(ProcessCpuOffset)),
            ReadSingle(packet.Slice(GpuFrameOffset)),
            BinaryPrimitives.ReadInt64LittleEndian(packet.Slice(WorkingSetOffset)),
            BinaryPrimitives.ReadInt64LittleEndian(packet.Slice(ManagedHeapOffset)),
            BinaryPrimitives.ReadInt64LittleEndian(packet.Slice(RhiMemoryOffset)),
            BinaryPrimitives.ReadInt64LittleEndian(packet.Slice(AdapterVramBudgetOffset)),
            (GraphicsBackend)packet[BackendOffset],
            (ProfilerTelemetryTarget)packet[TargetOffset],
            (ProfilerTelemetryCapabilities)capabilities,
            processCpuSource,
            workingSetSource,
            managedHeapSource,
            rhiMemorySource,
            adapterVramBudgetSource);
        return IsValid(frame);
    }

    private static ushort PackSources(in ProfilerTelemetryFrame frame) => (ushort)
        ((ushort)frame.ProcessCpuSource
        | ((ushort)frame.WorkingSetSource << 3)
        | ((ushort)frame.ManagedHeapSource << 6)
        | ((ushort)frame.RhiMemorySource << 9)
        | ((ushort)frame.AdapterVramBudgetSource << 12));

    private static ProfilerMetricSource UnpackSource(ushort sources, int index) =>
        (ProfilerMetricSource)((sources >> (index * 3)) & 0x7);

    private static bool IsValid(in ProfilerTelemetryFrame frame)
    {
        if (!float.IsFinite(frame.ProcessCpuPercent)
            || !float.IsFinite(frame.GpuFrameTimeMs)
            || frame.ProcessCpuPercent < 0f
            || frame.ProcessCpuPercent > 100f
            || frame.GpuFrameTimeMs < 0f
            || !IsKnownBackend(frame.Backend)
            || !IsKnownTarget(frame.Target)
            || ((ushort)frame.Capabilities & ~KnownCapabilityMask) != 0
            || !IsKnownSource(frame.ProcessCpuSource)
            || !IsKnownSource(frame.WorkingSetSource)
            || !IsKnownSource(frame.ManagedHeapSource)
            || !IsKnownSource(frame.RhiMemorySource)
            || !IsKnownSource(frame.AdapterVramBudgetSource))
            return false;

        return IsValidMetric(frame.ProcessCpuPercent, frame.ProcessCpuSource)
            && IsValidMetric(frame.WorkingSetBytes, frame.WorkingSetSource)
            && IsValidMetric(frame.ManagedHeapBytes, frame.ManagedHeapSource)
            && IsValidMetric(frame.RhiMemoryBytes, frame.RhiMemorySource)
            && IsValidMetric(frame.AdapterVramBudgetBytes, frame.AdapterVramBudgetSource)
            && (frame.HasGpuFrameTime || frame.GpuFrameTimeMs == 0f);
    }

    private static bool IsValidMetric(float value, ProfilerMetricSource source) =>
        source == ProfilerMetricSource.Unavailable ? value == 0f : value >= 0f;

    private static bool IsValidMetric(long value, ProfilerMetricSource source) =>
        source == ProfilerMetricSource.Unavailable ? value == 0 : value >= 0;

    private static bool IsKnownSource(ProfilerMetricSource source) => source <= ProfilerMetricSource.Platform;
    private static bool IsKnownBackend(GraphicsBackend backend) => backend >= GraphicsBackend.Auto && backend <= GraphicsBackend.WebGL2;
    private static bool IsKnownTarget(ProfilerTelemetryTarget target) => target <= ProfilerTelemetryTarget.HarmonyOSPlayer;

    private static void WriteSingle(Span<byte> destination, float value) =>
        BinaryPrimitives.WriteInt32LittleEndian(destination, BitConverter.SingleToInt32Bits(value));

    private static float ReadSingle(ReadOnlySpan<byte> source) =>
        BitConverter.Int32BitsToSingle(BinaryPrimitives.ReadInt32LittleEndian(source));
}

/// <summary>
/// Non-blocking direct-IP sender for the bounded profiler datagram protocol. A failed send drops
/// only the current sample; profiling must never stall gameplay or rendering.
/// </summary>
public sealed class ProfilerTelemetrySender : IDisposable
{
    private readonly Socket _socket;
    private readonly EndPoint _remoteEndpoint;
    private readonly byte[] _buffer = new byte[ProfilerTelemetryProtocol.PacketSize];

    public ProfilerTelemetrySender(IPEndPoint remoteEndpoint)
    {
        ArgumentNullException.ThrowIfNull(remoteEndpoint);
        _remoteEndpoint = remoteEndpoint;
        _socket = new Socket(remoteEndpoint.AddressFamily, SocketType.Dgram, ProtocolType.Udp)
        {
            Blocking = false,
        };
    }

    public bool TrySend(in ProfilerTelemetryFrame frame)
    {
        if (!ProfilerTelemetryProtocol.TryWrite(frame, _buffer, out int written))
            return false;

        try
        {
            return _socket.SendTo(_buffer, 0, written, SocketFlags.None, _remoteEndpoint) == written;
        }
        catch (SocketException)
        {
            return false;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    public void Dispose() => _socket.Dispose();
}

/// <summary>Non-blocking direct-IP receiver for fixed-size profiler datagrams.</summary>
public sealed class ProfilerTelemetryReceiver : IDisposable
{
    private readonly Socket _socket;
    private readonly byte[] _buffer = new byte[ProfilerTelemetryProtocol.PacketSize];
    private EndPoint _remoteEndpoint;

    public ProfilerTelemetryReceiver(IPEndPoint localEndpoint)
    {
        ArgumentNullException.ThrowIfNull(localEndpoint);
        _socket = new Socket(localEndpoint.AddressFamily, SocketType.Dgram, ProtocolType.Udp)
        {
            Blocking = false,
        };
        _socket.Bind(localEndpoint);
        _remoteEndpoint = localEndpoint.AddressFamily == AddressFamily.InterNetworkV6
            ? new IPEndPoint(IPAddress.IPv6Any, 0)
            : new IPEndPoint(IPAddress.Any, 0);
    }

    public IPEndPoint LocalEndpoint => (IPEndPoint)_socket.LocalEndPoint!;

    public bool TryReceive(out ProfilerTelemetryFrame frame)
    {
        frame = default;
        try
        {
            int received = _socket.ReceiveFrom(_buffer, 0, _buffer.Length, SocketFlags.None, ref _remoteEndpoint);
            return received == ProfilerTelemetryProtocol.PacketSize
                && ProfilerTelemetryProtocol.TryRead(_buffer, out frame);
        }
        catch (SocketException)
        {
            return false;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }

    public void Dispose() => _socket.Dispose();
}
