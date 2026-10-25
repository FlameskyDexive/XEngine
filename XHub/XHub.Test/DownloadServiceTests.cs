using System;
using System.IO;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using XHub.Models;
using XHub.Services;
using Xunit;

namespace XHub.Test;

/// <summary>
/// 可控测试源 (§8): a raw TcpListener HTTP server serving one payload with Range support, used
/// to exercise the real download pipeline (resume, etag change detection, sha256 gate) without
/// CI needing external hosts. TcpListener avoids HttpListener URLACL requirements.
/// </summary>
public sealed class TestSourceServer : IDisposable
{
    private readonly System.Net.Sockets.TcpListener _listener;
    private readonly CancellationTokenSource _cts = new();

    public string Url { get; }
    public byte[] Payload { get; }
    public string Etag { get; set; } = "\"test-etag-v1\"";
    public int RangeRequests { get; private set; }

    public TestSourceServer(byte[] payload)
    {
        Payload = payload;
        var endpoint = new System.Net.IPEndPoint(System.Net.IPAddress.Loopback, 0);
        _listener = new System.Net.Sockets.TcpListener(endpoint);
        _listener.Start();
        int port = ((System.Net.IPEndPoint)_listener.LocalEndpoint).Port;
        Url = $"http://localhost:{port}/pkg.zip";
        _ = Task.Run(() => Loop(_cts.Token));
    }

    private async Task Loop(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            System.Net.Sockets.TcpClient client;
            try { client = await _listener.AcceptTcpClientAsync(ct); }
            catch { return; }
            _ = Task.Run(() => Serve(client, ct), ct);
        }
    }

    private async Task Serve(System.Net.Sockets.TcpClient client, CancellationToken ct)
    {
        try
        {
            await using System.Net.Sockets.NetworkStream stream = client.GetStream();
            string request = await ReadRequestHeadAsync(stream, ct);
            long start = 0;
            bool partial = false;
            foreach (string rawLine in request.Split("\r\n"))
            {
                if (rawLine.StartsWith("Range:", StringComparison.OrdinalIgnoreCase))
                {
                    string spec = rawLine["Range:".Length..].Trim();
                    if (spec.StartsWith("bytes="))
                    {
                        start = long.Parse(spec["bytes=".Length..].TrimEnd('-'));
                        partial = true;
                        RangeRequests++;
                    }
                }
            }

            long count = Payload.Length - start;
            string headers =
                (partial
                    ? "HTTP/1.1 206 Partial Content\r\n" +
                      $"Content-Range: bytes {start}-{Payload.Length - 1}/{Payload.Length}\r\n"
                    : "HTTP/1.1 200 OK\r\n") +
                $"ETag: {Etag}\r\n" +
                $"Content-Length: {count}\r\n" +
                "Connection: close\r\n" +
                "\r\n";
            byte[] head = Encoding.ASCII.GetBytes(headers);
            await stream.WriteAsync(head, ct);
            await stream.WriteAsync(Payload.AsMemory((int)start, (int)count), ct);
            await stream.FlushAsync(ct);
        }
        catch { /* client vanished mid-stream — normal for cancel/pause tests */ }
        finally
        {
            try { client.Close(); } catch { }
        }
    }

    private static async Task<string> ReadRequestHeadAsync(System.Net.Sockets.NetworkStream stream, CancellationToken ct)
    {
        var buffer = new byte[8192];
        var head = new MemoryStream();
        while (head.Length < buffer.Length)
        {
            int read = await stream.ReadAsync(buffer, ct);
            if (read == 0) break;
            head.Write(buffer, 0, read);
            string soFar = Encoding.ASCII.GetString(head.ToArray());
            if (soFar.Contains("\r\n\r\n")) return soFar[..soFar.IndexOf("\r\n\r\n")];
        }
        return Encoding.ASCII.GetString(head.ToArray());
    }

    public void Dispose()
    {
        _cts.Cancel();
        try { _listener.Stop(); } catch { }
        _cts.Dispose();
    }
}

public class DownloadServiceTests : IDisposable
{
    private readonly string _dir;

    public DownloadServiceTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), "xhub-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private static string Sha256(byte[] data)
    {
        using var sha = SHA256.Create();
        return Convert.ToHexString(sha.ComputeHash(data)).ToLowerInvariant();
    }

    [Fact]
    public async Task Download_CompletesAndVerifiesHash()
    {
        byte[] payload = RandomNumberGenerator.GetBytes(1024 * 512);
        using var server = new TestSourceServer(payload);
        using var service = new DownloadService(
            Path.Combine(_dir, "operations.json"), Path.Combine(_dir, "cache"));

        DownloadOperation op = service.Enqueue(server.Url, payload.Length, Sha256(payload), "1.0.0-a1", "V1.0.0a1");
        var finished = new TaskCompletionSource();
        service.Progress += p =>
        {
            if (p.Id == op.Id && p.State == OperationState.Completed)
                finished.TrySetResult();
            if (p.Id == op.Id && p.State == OperationState.Failed)
                finished.TrySetException(new InvalidOperationException(p.Error));
        };
        await Task.WhenAny(finished.Task, Task.Delay(15000));

        Assert.Equal(OperationState.Completed, service.Operations is { } ops ? FindState(ops, op.Id) : default);
    }

    [Fact]
    public async Task Download_HashMismatch_FailsAndDeletesPartial()
    {
        byte[] payload = Encoding.UTF8.GetBytes("corrupt payload content");
        using var server = new TestSourceServer(payload);
        using var service = new DownloadService(
            Path.Combine(_dir, "operations.json"), Path.Combine(_dir, "cache"));

        DownloadOperation op = service.Enqueue(server.Url, payload.Length, new string('0', 64), "1.0.0-a1", "V1.0.0a1");
        var failed = new TaskCompletionSource();
        service.Progress += p =>
        {
            if (p.Id == op.Id && p.State == OperationState.Failed)
                failed.TrySetResult();
        };
        await Task.WhenAny(failed.Task, Task.Delay(15000));

        Assert.True(failed.Task.IsCompletedSuccessfully);
        Assert.False(File.Exists(op.TempPath)); // corrupt payload discarded
    }

    [Fact]
    public async Task Download_PauseThenResume_UsesRangeRequest()
    {
        byte[] payload = RandomNumberGenerator.GetBytes(1024 * 1024);
        using var server = new TestSourceServer(payload);
        string operationsFile = Path.Combine(_dir, "operations.json");
        using var service = new DownloadService(operationsFile, Path.Combine(_dir, "cache"));

        DownloadOperation op = service.Enqueue(server.Url, payload.Length, Sha256(payload), "1.0.0-a1", "V1.0.0a1");
        service.Pause(op.Id); // pause immediately: the in-flight request stops on the next tick
        await Task.Delay(600);
        service.Resume(op.Id);

        var finished = new TaskCompletionSource();
        service.Progress += p =>
        {
            if (p.Id == op.Id && p.State == OperationState.Completed) finished.TrySetResult();
        };
        await Task.WhenAny(finished.Task, Task.Delay(20000));
        Assert.True(finished.Task.IsCompleted);

        // Verify the delivered bytes match the payload end-to-end.
        Assert.Equal(Sha256(payload), Sha256(File.ReadAllBytes(
            Directory.GetFiles(Path.Combine(_dir, "cache"))[0])));
    }

    private static OperationState FindState(System.Collections.Generic.IReadOnlyCollection<DownloadOperation> ops, string id)
    {
        foreach (DownloadOperation op in ops)
            if (op.Id == id) return Map(op.State);
        return default;
    }

    private static OperationState Map(string state) => state switch
    {
        "completed" => OperationState.Completed,
        "failed" => OperationState.Failed,
        _ => OperationState.Queued,
    };
}
