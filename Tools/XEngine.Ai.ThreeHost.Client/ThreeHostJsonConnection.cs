using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using XEngine.Ai.Protocol;

namespace XEngine.Ai.ThreeHost.Client;

/// <summary>
/// Line-framed JSON connection to the Windows three.js authoring host. The editor and the CLI
/// share this class so both speak the identical <c>three-authoring/1</c> protocol: one JSON line
/// in (request), one JSON line out (response), matched by <see cref="ThreeAuthoringRequest.RequestId"/>.
/// Streams are injectable so unit tests can drive the framing without a real process.
/// </summary>
public sealed class ThreeHostJsonConnection : IDisposable
{
    public const string TimeoutCode = "operation_timeout";
    public const string CancelledCode = "operation_cancelled";
    public const string DisconnectedCode = "host_disconnected";

    private readonly TextReader _reader;
    private readonly TextWriter _writer;
    private readonly object _writeLock = new();
    private readonly Dictionary<string, PendingCall> _pending = new(StringComparer.Ordinal);
    private readonly object _pendingLock = new();
    private Thread? _readLoop;
    private volatile bool _disposed;
    private int _started;

    /// <summary>Raised for host stderr/log lines; never raised on the caller's thread.</summary>
    public event Action<string>? HostLog;

    public ThreeHostJsonConnection(TextReader reader, TextWriter writer)
    {
        _reader = reader ?? throw new ArgumentNullException(nameof(reader));
        _writer = writer ?? throw new ArgumentNullException(nameof(writer));
    }

    /// <summary>Starts the background read loop. Safe to call once; subsequent calls are no-ops.</summary>
    public void Start()
    {
        if (Interlocked.Exchange(ref _started, 1) != 0)
            return;
        _readLoop = new Thread(ReadLoop)
        {
            IsBackground = true,
            Name = "ThreeHostJsonConnection.ReadLoop",
        };
        _readLoop.Start();
    }

    /// <summary>
    /// Sends one request and blocks until the matching response, the timeout, a cancellation or
    /// host death. Only one call may be in flight per connection instance at a time — the host
    /// process enforces the same rule and answers a second concurrent request with
    /// <c>host_busy</c>.
    /// </summary>
    public ThreeAuthoringResponse RoundTrip(
        ThreeAuthoringRequest request,
        int timeoutMs,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        ObjectDisposedException.ThrowIf(_disposed, this);
        Start();

        var pending = new PendingCall(request.RequestId);
        lock (_pendingLock)
        {
            foreach (KeyValuePair<string, PendingCall> entry in _pending)
                throw new InvalidOperationException(
                    $"Another request is in flight on this connection: {entry.Key}.");
            _pending[request.RequestId] = pending;
        }

        string line = JsonSerializer.Serialize(request, ThreeAuthoringProtocol.JsonOptions);
        try
        {
            lock (_writeLock)
            {
                _writer.WriteLine(line);
                _writer.Flush();
            }
        }
        catch (Exception ex)
        {
            RemovePending(request.RequestId);
            return Fail(request, DisconnectedCode, $"Failed to write to the authoring host: {ex.Message}");
        }

        using (cancellationToken.Register(static state => ((PendingCall)state!).Cancel(), pending))
        {
            bool completed;
            try
            {
                completed = pending.Completed.Wait(timeoutMs, cancellationToken);
            }
            catch (OperationCanceledException)
            {
                completed = false;
            }

            // A response may have raced with the cancellation/timeout wakeup; a real answer
            // always wins over a synthetic failure.
            if (pending.Response is not null)
                return pending.Response;

            if (RemovePending(request.RequestId))
            {
                string code = cancellationToken.IsCancellationRequested ? CancelledCode : TimeoutCode;
                string message = cancellationToken.IsCancellationRequested
                    ? "The request was cancelled before the authoring host answered."
                    : $"The authoring host did not answer within {timeoutMs} ms.";
                return Fail(request, code, message);
            }

            if (!completed)
                return Fail(request, DisconnectedCode, "The connection closed while waiting for the authoring host.");
        }

        return Fail(request, DisconnectedCode, "The connection closed before a response arrived.");
    }

    /// <summary>
    /// Drops every pending call with a disconnect diagnostic. Used when the host process exits;
    /// late responses after this point find no pending entry and are discarded.
    /// </summary>
    public void FailAllPending(string code, string message)
    {
        PendingCall[] pending;
        lock (_pendingLock)
        {
            pending = new PendingCall[_pending.Count];
            _pending.Values.CopyTo(pending, 0);
            _pending.Clear();
        }

        foreach (PendingCall call in pending)
            call.Complete(Fail(call.RequestId, code, message));
    }

    public void Dispose()
    {
        if (_disposed)
            return;
        _disposed = true;
        FailAllPending(DisconnectedCode, "The authoring host connection was disposed.");
    }

    private void ReadLoop()
    {
        try
        {
            while (!_disposed)
            {
                string? line = _reader.ReadLine();
                if (line is null)
                    break;

                if (line.Length == 0)
                    continue;

                if (line.StartsWith("{\"log\"", StringComparison.Ordinal))
                {
                    HostLog?.Invoke(line);
                    continue;
                }

                ThreeAuthoringResponse? response;
                try
                {
                    response = JsonSerializer.Deserialize<ThreeAuthoringResponse>(
                        line, ThreeAuthoringProtocol.JsonOptions);
                }
                catch (JsonException)
                {
                    HostLog?.Invoke(line);
                    continue;
                }

                if (response is null || string.IsNullOrEmpty(response.RequestId))
                    continue;

                if (TryTakePending(response.RequestId, out PendingCall? pending))
                    pending.Complete(response);
            }
        }
        catch (Exception ex) when (ex is ObjectDisposedException or IOException)
        {
            // Stream torn down during dispose/shutdown — Fall through to FailAllPending.
        }
        finally
        {
            FailAllPending(DisconnectedCode, "The authoring host closed the connection.");
        }
    }

    private bool TryTakePending(string requestId, out PendingCall pending)
    {
        lock (_pendingLock)
        {
            if (_pending.TryGetValue(requestId, out pending!))
            {
                _pending.Remove(requestId);
                return true;
            }
            return false;
        }
    }

    private bool RemovePending(string requestId)
    {
        lock (_pendingLock)
        {
            return _pending.Remove(requestId);
        }
    }

    private static ThreeAuthoringResponse Fail(ThreeAuthoringRequest request, string code, string message) =>
        new(
            ThreeAuthoringProtocol.Version,
            request.RequestId,
            request.Operation,
            Succeeded: false,
            ErrorCode: code,
            Message: message,
            Manifest: null,
            Diagnostics: [new ThreeAuthoringDiagnostic(code, "error", message)]);

    private ThreeAuthoringResponse Fail(string requestId, string code, string message) =>
        new(
            ThreeAuthoringProtocol.Version,
            requestId,
            string.Empty,
            Succeeded: false,
            ErrorCode: code,
            Message: message,
            Manifest: null,
            Diagnostics: [new ThreeAuthoringDiagnostic(code, "error", message)]);

    private sealed class PendingCall
    {
        public PendingCall(string requestId) => RequestId = requestId;

        public string RequestId { get; }
        public ThreeAuthoringResponse? Response { get; private set; }
        public ManualResetEventSlim Completed { get; } = new(false, spinCount: 0);

        public void Complete(ThreeAuthoringResponse response)
        {
            Response = response;
            Completed.Set();
        }

        public void Cancel()
        {
            // The timeout/cancel path in RoundTrip owns the diagnostic; this only wakes the
            // blocked caller so it can observe IsCancellationRequested promptly.
            Completed.Set();
        }
    }
}
