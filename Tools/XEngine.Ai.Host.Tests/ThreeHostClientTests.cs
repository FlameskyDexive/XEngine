using System;
using System.Collections.Generic;
using System.Collections.Concurrent;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using XEngine.Ai.Protocol;
using XEngine.Ai.ThreeHost.Client;
using Xunit;

namespace XEngine.Ai.Host.Tests;

/// <summary>
/// A1b — framing/behaviour tests for the shared authoring-host connection. A real WebView2
/// process is exercised by Tools/capture_three_host.py; these tests pin the protocol semantics:
/// request/response routing by RequestId, timeout, cancellation, disconnect diagnostics and
/// late-response dropping.
/// </summary>
public sealed class ThreeHostClientTests
{
    private static ThreeAuthoringRequest ValidRequest(string id = "req-1", int timeoutMs = 60_000) =>
        new(
            ProtocolVersion: ThreeAuthoringProtocol.Version,
            RequestId: id,
            Operation: "Preview",
            SourcePath: "Three/sample.threefx",
            OutputDirectory: null,
            PreviewId: null,
            TimeSeconds: 1.5,
            Width: 512,
            Height: 512,
            TimeoutMs: timeoutMs,
            Intent: "unit test",
            Options: null);

    private static ThreeAuthoringResponse OkResponse(string requestId) =>
        new(
            ThreeAuthoringProtocol.Version,
            requestId,
            "Preview",
            Succeeded: true,
            ErrorCode: null,
            Message: "ok",
            Manifest: null,
            Diagnostics: []);

    [Fact]
    public async Task RoundTrip_RoutesResponseByRequestId()
    {
        QueueTextReader reader = new();
        CaptureTextWriter writer = new();
        using ThreeHostJsonConnection connection = new(reader, writer);

        writer.OnLine += line =>
        {
            ThreeAuthoringRequest? request =
                JsonSerializer.Deserialize<ThreeAuthoringRequest>(line, ThreeAuthoringProtocol.JsonOptions);
            Assert.NotNull(request);
            reader.AddLine(JsonSerializer.Serialize(OkResponse(request!.RequestId),
                ThreeAuthoringProtocol.JsonOptions));
        };
        connection.Start();

        ThreeAuthoringResponse response = connection.RoundTrip(ValidRequest("route-1"), 5_000, CancellationToken.None);

        Assert.True(response.Succeeded);
        Assert.Equal("route-1", response.RequestId);
        Assert.Single(writer.Lines);
        Assert.Contains("\"route-1\"", writer.Lines[0]);
        await Task.CompletedTask;
    }

    [Fact]
    public void RoundTrip_TimesOutWithDiagnostic()
    {
        QueueTextReader reader = new();
        CaptureTextWriter writer = new();
        using ThreeHostJsonConnection connection = new(reader, writer);
        connection.Start();

        ThreeAuthoringResponse response = connection.RoundTrip(
            ValidRequest("slow-1"), 100, CancellationToken.None);

        Assert.False(response.Succeeded);
        Assert.Equal(ThreeHostJsonConnection.TimeoutCode, response.ErrorCode);
    }

    [Fact]
    public void RoundTrip_CancellationYieldsCancelledDiagnostic()
    {
        QueueTextReader reader = new();
        CaptureTextWriter writer = new();
        using ThreeHostJsonConnection connection = new(reader, writer);
        connection.Start();

        using var cts = new CancellationTokenSource(50);
        ThreeAuthoringResponse response = connection.RoundTrip(
            ValidRequest("cancel-1", timeoutMs: 60_000), 60_000, cts.Token);

        Assert.False(response.Succeeded);
        Assert.Equal(ThreeHostJsonConnection.CancelledCode, response.ErrorCode);
    }

    [Fact]
    public async Task RoundTrip_HostDeathFailsPendingWithDisconnect()
    {
        QueueTextReader reader = new();
        CaptureTextWriter writer = new();
        using ThreeHostJsonConnection connection = new(reader, writer);
        connection.Start();

        Task<ThreeAuthoringResponse> pending = Task.Run(
            () => connection.RoundTrip(ValidRequest("dead-1"), 30_000, CancellationToken.None));

        await Task.Delay(50);
        reader.Complete();

        ThreeAuthoringResponse response = await pending;
        Assert.False(response.Succeeded);
        Assert.Equal(ThreeHostJsonConnection.DisconnectedCode, response.ErrorCode);
    }

    [Fact]
    public async Task RoundTrip_LateResponseAfterTimeoutIsDropped()
    {
        QueueTextReader reader = new();
        CaptureTextWriter writer = new();
        using ThreeHostJsonConnection connection = new(reader, writer);
        connection.Start();

        // No responder yet → late-1 deterministically times out.
        ThreeAuthoringResponse timedOut = connection.RoundTrip(
            ValidRequest("late-1"), 80, CancellationToken.None);
        Assert.Equal(ThreeHostJsonConnection.TimeoutCode, timedOut.ErrorCode);

        // From now on the host answers every request with a matching response…
        writer.OnLine += line =>
        {
            ThreeAuthoringRequest? request =
                JsonSerializer.Deserialize<ThreeAuthoringRequest>(line, ThreeAuthoringProtocol.JsonOptions);
            reader.AddLine(JsonSerializer.Serialize(OkResponse(request!.RequestId),
                ThreeAuthoringProtocol.JsonOptions));
        };

        // …but its answer to the abandoned late-1 arrives first. The connection must drop it
        // (no pending entry) and still answer late-2 correctly.
        reader.AddLine(JsonSerializer.Serialize(OkResponse("late-1"), ThreeAuthoringProtocol.JsonOptions));

        ThreeAuthoringResponse second = connection.RoundTrip(
            ValidRequest("late-2"), 5_000, CancellationToken.None);
        Assert.True(second.Succeeded);
        Assert.Equal("late-2", second.RequestId);
        await Task.CompletedTask;
    }

    [Fact]
    public async Task RoundTrip_SecondConcurrentRequestIsRefused()
    {
        QueueTextReader reader = new();
        CaptureTextWriter writer = new();
        using ThreeHostJsonConnection connection = new(reader, writer);
        connection.Start();

        Task<ThreeAuthoringResponse> first = Task.Run(
            () => connection.RoundTrip(ValidRequest("busy-1"), 30_000, CancellationToken.None));
        await Task.Delay(50);

        Assert.Throws<InvalidOperationException>(
            () => connection.RoundTrip(ValidRequest("busy-2"), 30_000, CancellationToken.None));

        reader.Complete();
        await first;
    }

    [Fact]
    public async Task FailAllPending_ProducesDisconnectEnvelope()
    {
        QueueTextReader reader = new();
        CaptureTextWriter writer = new();
        ThreeHostJsonConnection connection = new(reader, writer);
        connection.Start();

        Task<ThreeAuthoringResponse> pending = Task.Run(
            () => connection.RoundTrip(ValidRequest("fail-all"), 30_000, CancellationToken.None));
        await Task.Delay(50);
        connection.FailAllPending(
            ThreeHostJsonConnection.DisconnectedCode, "forced");
        connection.Dispose();

        ThreeAuthoringResponse response = await pending;
        Assert.False(response.Succeeded);
        Assert.Equal(ThreeHostJsonConnection.DisconnectedCode, response.ErrorCode);
    }

    /// <summary>Thread-safe line source; <see cref="Complete"/> makes ReadLine return null (EOF).</summary>
    private sealed class QueueTextReader : TextReader
    {
        private readonly BlockingCollection<string> _lines = new();

        public void AddLine(string line) => _lines.Add(line);

        public void Complete() => _lines.CompleteAdding();

        public override string? ReadLine()
        {
            try
            {
                return _lines.Take();
            }
            catch (InvalidOperationException)
            {
                return null;
            }
        }
    }

    private sealed class CaptureTextWriter : TextWriter
    {
        public List<string> Lines { get; } = new();
        public Action<string>? OnLine { get; set; }
        private readonly object _lock = new();

        public override Encoding Encoding => Encoding.UTF8;

        public override void WriteLine(string? value)
        {
            lock (_lock)
            {
                Lines.Add(value ?? string.Empty);
            }
            OnLine?.Invoke(value ?? string.Empty);
        }
    }
}
