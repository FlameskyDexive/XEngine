using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using XHub.Infrastructure;
using XHub.Models;

namespace XHub.Services;

public enum HubCommandPhase
{
    Progress,
    Success,
    Failure,
}

/// <summary>One NDJSON event from a <c>--hub-command</c> run (§4.3 envelope).</summary>
public sealed record HubCommandEvent(HubCommandPhase Phase, string RequestId, string Code, JsonElement Data);

/// <summary>
/// Client for the editor's headless <c>--hub-command</c> channel (§4.3): spawns the editor,
/// writes one JSON request to stdin, parses NDJSON events from stdout until the request's
/// terminal event, and surfaces progress along the way. Parsing is split into a pure method so
/// tests don't need a real editor.
/// </summary>
public static class EditorCommandClient
{
    public sealed record CommandOutcome(bool Success, string Code, JsonElement Data);

    /// <summary>
    /// Runs one command against an installation. Returns null when the payload can't run the
    /// hub command at all (broken install, crash, timeout — stderr carries the reason).
    /// </summary>
    public static async Task<CommandOutcome?> ExecuteAsync(
        InstallationRecord install, string type, object requestData,
        IProgress<HubCommandEvent>? progress = null, string? requestId = null, int timeoutMs = 120_000)
    {
        if (install.IsBroken || !File.Exists(install.EditorExePath))
            return null;

        requestId ??= Guid.NewGuid().ToString("N");
        var startInfo = new ProcessStartInfo
        {
            FileName = install.EditorExePath,
            Arguments = "--hub-command",
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };

        try
        {
            using var process = new Process { StartInfo = startInfo };
            if (!process.Start())
                return null;

            string request = JsonSerializer.Serialize(new { protocolVersion = HubProtocol.CurrentVersion, requestId, type, data = requestData });
            await process.StandardInput.WriteLineAsync(request);
            process.StandardInput.Close();

            var completion = new TaskCompletionSource<CommandOutcome?>(TaskCreationOptions.RunContinuationsAsynchronously);
            _ = Task.Run(() =>
            {
                string? line;
                while ((line = process.StandardOutput.ReadLine()) != null)
                {
                    HubCommandEvent? evt = ParseEvent(line);
                    if (evt == null || evt.RequestId != requestId) continue;
                    switch (evt.Phase)
                    {
                        case HubCommandPhase.Progress:
                            progress?.Report(evt);
                            break;
                        case HubCommandPhase.Success:
                            completion.TrySetResult(new CommandOutcome(true, "", evt.Data));
                            return;
                        case HubCommandPhase.Failure:
                            completion.TrySetResult(new CommandOutcome(false, evt.Code, evt.Data));
                            return;
                    }
                }
                completion.TrySetResult(null); // stream ended without a terminal event
            });

            var winner = await Task.WhenAny(completion.Task, Task.Delay(timeoutMs));
            if (winner != completion.Task)
            {
                try { process.Kill(true); } catch { }
                HubLog.Warning($"Hub command '{type}' timed out after {timeoutMs}ms.");
                return null;
            }

            CommandOutcome? outcome = await completion.Task;
            if (outcome == null)
            {
                string stderr = await process.StandardError.ReadToEndAsync();
                HubLog.Warning($"Hub command '{type}' ended without a terminal event: {stderr}");
            }
            return outcome;
        }
        catch (Exception ex)
        {
            HubLog.Error($"Hub command '{type}' failed to run.", ex);
            return null;
        }
    }

    /// <summary>Pure NDJSON parser for command events (unit-tested).</summary>
    public static HubCommandEvent? ParseEvent(string line)
    {
        if (!line.StartsWith('{')) return null;
        try
        {
            using JsonDocument doc = JsonDocument.Parse(line);
            JsonElement root = doc.RootElement;
            if (!root.TryGetProperty("type", out JsonElement typeEl)) return null;
            HubCommandPhase phase = typeEl.GetString() switch
            {
                "progress" => HubCommandPhase.Progress,
                "success" => HubCommandPhase.Success,
                "failure" => HubCommandPhase.Failure,
                _ => (HubCommandPhase)255,
            };
            if (phase == (HubCommandPhase)255) return null;
            return new HubCommandEvent(
                phase,
                root.TryGetProperty("requestId", out JsonElement r) && r.ValueKind == JsonValueKind.String ? r.GetString() ?? "" : "",
                root.TryGetProperty("code", out JsonElement c) && c.ValueKind == JsonValueKind.String ? c.GetString() ?? "" : "",
                root.TryGetProperty("data", out JsonElement d) ? d.Clone() : default);
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
