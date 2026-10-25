// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Net;
using System.Net.Sockets;
using XEngine.Runtime.Profiling;

namespace XEngine.Editor.Core;

/// <summary>Bounded Editor-side receiver for direct-IP player profiler frames.</summary>
public sealed class EditorProfilerCapture : IDisposable
{
    private const int MaximumFramesPerPoll = 16;

    private ProfilerTelemetryReceiver? _receiver;

    public bool IsListening => _receiver != null;
    public int Port => _receiver?.LocalEndpoint.Port ?? 0;
    public bool HasFrame { get; private set; }
    public ProfilerTelemetryFrame LastFrame { get; private set; }

    /// <summary>Starts a new IPv4 listener. A zero port is allowed for test/local host allocation.</summary>
    public bool TryStart(int port)
    {
        if (port is < 0 or > 65535)
            return false;

        Stop();
        try
        {
            _receiver = new ProfilerTelemetryReceiver(new IPEndPoint(IPAddress.Any, port));
            HasFrame = false;
            LastFrame = default;
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    /// <summary>Drains at most a fixed number of received frames and retains the newest valid sample.</summary>
    public bool Poll()
    {
        bool received = false;
        for (int i = 0; i < MaximumFramesPerPoll; i++)
        {
            if (_receiver == null || !_receiver.TryReceive(out ProfilerTelemetryFrame frame))
                break;
            LastFrame = frame;
            HasFrame = true;
            received = true;
        }
        return received;
    }

    public void Stop()
    {
        _receiver?.Dispose();
        _receiver = null;
        HasFrame = false;
        LastFrame = default;
    }

    public void Dispose() => Stop();
}
