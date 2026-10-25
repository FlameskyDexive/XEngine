using System;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;
using XEngine.Ai.Protocol;

namespace XEngine.Ai.ThreeHost;

/// <summary>
/// Reads <see cref="ThreeAuthoringRequest"/> JSON lines from stdin, executes exactly one
/// operation at a time on the UI thread, and writes <see cref="ThreeAuthoringResponse"/> JSON
/// lines to stdout. Timeouts, host-busy rejection and WebView2 process death are all surfaced
/// as typed diagnostics; a failed operation never takes the process down (crash diagnosis is
/// observable from the client side as a process exit instead).
/// </summary>
internal sealed class ThreeHostServer : IDisposable
{
    private readonly ThreeHostOptions _options;
    private ThreeAuthoringBridge? _bridge;
    private volatile bool _inFlight;
    private int _exitCode;

    public ThreeHostServer(ThreeHostOptions options) => _options = options;

    public int Run()
    {
        using ManualResetEventSlim stdinClosed = new(false);
        ThreeHostWindow form = new(_options);
        form.FormClosed += (_, _) => stdinClosed.Set();

        Thread reader = new(() => StdinLoop(form, stdinClosed))
        {
            IsBackground = true,
            Name = "ThreeHostServer.Stdin",
        };
        reader.Start();

        form.Shown += (_, _) => form.SetStatus("waiting-requests");
        Application.Run(form);
        return _exitCode;
    }

    private void StdinLoop(ThreeHostWindow form, ManualResetEventSlim stdinClosed)
    {
        try
        {
            TextReader stdin = Console.In;
            while (true)
            {
                string? line = stdin.ReadLine();
                if (line is null)
                {
                    Console.Error.WriteLine("host-trace: stdin EOF");
                    break; // Parent died or disposed the pipes → graceful shutdown.
                }
                if (line.Length == 0)
                    continue;

                if (form.IsDisposed)
                    break;

                Dispatch(form, line);
            }
        }
        catch (Exception ex)
        {
            // Torn pipes during client dispose land here; treat like EOF.
            Console.Error.WriteLine("stdin_loop_error: " + ex.Message);
        }

        try
        {
            if (!form.IsDisposed)
                form.BeginInvoke(() => form.Close());
        }
        catch (ObjectDisposedException)
        {
        }
        stdinClosed.Set();
    }

    private void Dispatch(ThreeHostWindow form, string line)
    {
        ThreeAuthoringRequest request;
        try
        {
            request = JsonSerialization.Deserialize<ThreeAuthoringRequest>(line)
                ?? throw new JsonSerializationException("null request");
        }
        catch (Exception ex)
        {
            Write(new ThreeAuthoringResponse(
                ThreeAuthoringProtocol.Version,
                string.Empty,
                string.Empty,
                Succeeded: false,
                ErrorCode: "malformed_request",
                Message: ex.Message,
                Manifest: null,
                Diagnostics: [new ThreeAuthoringDiagnostic("malformed_request", "error", ex.Message)]));
            return;
        }

        IReadOnlyList<ThreeAuthoringDiagnostic> issues = ThreeAuthoringRequestValidator.Validate(request);
        if (HasError(issues))
        {
            Write(new ThreeAuthoringResponse(
                request.ProtocolVersion,
                request.RequestId,
                request.Operation,
                Succeeded: false,
                ErrorCode: "invalid_request",
                Message: "Request failed protocol validation.",
                Manifest: null,
                Diagnostics: issues));
            return;
        }

        if (_inFlight)
        {
            Write(new ThreeAuthoringResponse(
                request.ProtocolVersion,
                request.RequestId,
                request.Operation,
                Succeeded: false,
                ErrorCode: "host_busy",
                Message: "The authoring host is already executing another request.",
                Manifest: null,
                Diagnostics: [new ThreeAuthoringDiagnostic("host_busy", "error",
                    "The authoring host is already executing another request.")]));
            return;
        }

        _inFlight = true;
        try
        {
            form.BeginInvoke(() => _ = ExecuteAsync(form, request));
        }
        catch (ObjectDisposedException)
        {
            _inFlight = false;
            Write(Fail(request, "host_shutdown", "The authoring host window closed before the request ran."));
        }
    }

    private async Task ExecuteAsync(ThreeHostWindow form, ThreeAuthoringRequest request)
    {
        try
        {
            form.SetStatus("executing:" + request.Operation.ToLowerInvariant());
            _bridge ??= new ThreeAuthoringBridge(_options, form);

            int timeoutMs = request.TimeoutMs;
            Task<ThreeAuthoringResponse> operation = form.RunOnUIThreadAsync(
                () => _bridge.ExecuteAsync(request));
            Task completed = await Task.WhenAny(operation, Task.Delay(timeoutMs)).ConfigureAwait(true);

            if (completed != operation)
            {
                // The abandoned operation keeps running on the UI thread; its late response is
                // dropped because the in-flight flag is already cleared and the client has
                // stopped listening for this RequestId.
                Write(Fail(request, "operation_timeout", $"The operation exceeded {timeoutMs} ms."));
                return;
            }

            Write(await operation.ConfigureAwait(true));
        }
        catch (Exception ex)
        {
            Write(Fail(request, "host_error", ex.Message));
        }
        finally
        {
            _inFlight = false;
            form.SetStatus("ready");
        }
    }

    private static bool HasError(IReadOnlyList<ThreeAuthoringDiagnostic> diagnostics)
    {
        foreach (ThreeAuthoringDiagnostic diagnostic in diagnostics)
        {
            if (string.Equals(diagnostic.Severity, "error", StringComparison.Ordinal))
                return true;
        }
        return false;
    }

    private static ThreeAuthoringResponse Fail(ThreeAuthoringRequest request, string code, string message) =>
        new(
            request.ProtocolVersion,
            request.RequestId,
            request.Operation,
            Succeeded: false,
            ErrorCode: code,
            Message: message,
            Manifest: null,
            Diagnostics: [new ThreeAuthoringDiagnostic(code, "error", message)]);

    private void Write(ThreeAuthoringResponse response)
    {
        try
        {
            string json = JsonSerialization.Serialize(response);
            lock (Console.Out)
            {
                Console.Out.WriteLine(json);
                Console.Out.Flush();
            }
        }
        catch (IOException)
        {
            // Parent is gone; keep draining stdin until EOF so the process exits cleanly.
        }
        catch (ObjectDisposedException)
        {
        }
    }

    public void Dispose()
    {
        _bridge?.Dispose();
        _exitCode = 0;
    }
}

/// <summary>Thrown for unreadable request payloads (kept local; the protocol layer stays DTO-only).</summary>
internal sealed class JsonSerializationException(string message) : Exception(message);
