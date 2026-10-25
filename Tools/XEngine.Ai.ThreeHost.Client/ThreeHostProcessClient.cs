using System;
using System.Diagnostics;
using System.IO;
using XEngine.Ai.Protocol;

namespace XEngine.Ai.ThreeHost.Client;

/// <summary>
/// Spawns the STA WebView2 authoring host process and speaks the shared line protocol over its
/// stdio. Both the editor panel and <c>xengine-cli</c> go through this one implementation, which
/// is what makes panel and CLI exports byte-identical for the same request.
/// </summary>
public sealed class ThreeHostProcessClient : IDisposable
{
    private readonly Process _process;
    private readonly ThreeHostJsonConnection _connection;

    public ThreeHostProcessClient(ProcessStartInfo startInfo)
    {
        ArgumentNullException.ThrowIfNull(startInfo);

        startInfo.RedirectStandardInput = true;
        startInfo.RedirectStandardOutput = true;
        startInfo.RedirectStandardError = true;
        startInfo.UseShellExecute = false;
        startInfo.CreateNoWindow = true;

        _process = Process.Start(startInfo)
            ?? throw new InvalidOperationException($"Failed to start authoring host '{startInfo.FileName}'.");
        _connection = new ThreeHostJsonConnection(_process.StandardOutput, _process.StandardInput);
        _connection.HostLog += line => HostLog?.Invoke(line);
        _process.ErrorDataReceived += (_, e) =>
        {
            if (!string.IsNullOrEmpty(e.Data))
                HostLog?.Invoke(e.Data);
        };
        _process.BeginErrorReadLine();
        _process.Exited += OnProcessExited;
        if (!_process.HasExited)
            _process.EnableRaisingEvents = true;
        else
            OnProcessExited(_process, EventArgs.Empty);
        _connection.Start();
    }

    /// <summary>Host stderr/log lines and unexpected stdout lines.</summary>
    public event Action<string>? HostLog;

    public int ProcessId => _process.Id;

    /// <summary>True once the host process has exited (crash, user close, or client teardown).</summary>
    public bool HasExited => _process.HasExited;

    public ThreeAuthoringResponse RoundTrip(
        ThreeAuthoringRequest request,
        CancellationToken cancellationToken)
    {
        IReadOnlyList<ThreeAuthoringDiagnostic> issues = ThreeAuthoringRequestValidator.Validate(request);
        foreach (ThreeAuthoringDiagnostic diagnostic in issues)
        {
            if (string.Equals(diagnostic.Severity, "error", StringComparison.Ordinal))
                return new ThreeAuthoringResponse(
                    ThreeAuthoringProtocol.Version,
                    request.RequestId,
                    request.Operation,
                    Succeeded: false,
                    ErrorCode: "invalid_request",
                    Message: diagnostic.Message,
                    Manifest: null,
                    Diagnostics: issues);
        }

        int timeoutMs = request.TimeoutMs > 0 ? request.TimeoutMs : ThreeAuthoringProtocol.DefaultTimeoutMs;
        return _connection.RoundTrip(request, timeoutMs, cancellationToken);
    }

    public void Dispose()
    {
        _connection.Dispose();
        try
        {
            if (!_process.HasExited)
                _process.Kill(entireProcessTree: true);
        }
        catch (InvalidOperationException)
        {
            // Already exited between HasExited and Kill.
        }
        _process.Dispose();
    }

    private void OnProcessExited(object? sender, EventArgs e) =>
        _connection.FailAllPending(
            ThreeHostJsonConnection.DisconnectedCode,
            $"The authoring host process exited (exit code {_process.ExitCode}).");
}
