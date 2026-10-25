using System;
using System.Diagnostics;
using System.IO;
using System.Text.Json;

namespace XHub.Services;

/// <summary>
/// Hub protocol constants and the <c>--hub-info</c> capability probe (§4.3). Parsing is pure
/// and unit-testable; <see cref="ProbeEditor"/> spawns the editor exe headless with a timeout.
/// </summary>
public static class HubProtocol
{
    /// <summary>The protocol version this XHub build speaks.</summary>
    public const int CurrentVersion = 1;

    public sealed record HubInfo(
        int ProtocolVersion,
        string EngineVersion,
        string DisplayVersion,
        string Architecture,
        string[] Capabilities);

    /// <summary>Parses stdout of <c>--hub-info</c>. Null when the payload is not hub-aware.</summary>
    public static HubInfo? ParseHubInfo(string stdout)
    {
        foreach (string rawLine in stdout.Split('\n'))
        {
            string line = rawLine.Trim();
            if (!line.StartsWith('{')) continue;
            try
            {
                using JsonDocument doc = JsonDocument.Parse(line);
                JsonElement root = doc.RootElement;
                if (!root.TryGetProperty("protocolVersion", out JsonElement versionEl)
                    || versionEl.ValueKind != JsonValueKind.Number)
                    continue;
                return new HubInfo(
                    versionEl.GetInt32(),
                    TryGetString(root, "engineVersion"),
                    TryGetString(root, "displayVersion"),
                    TryGetString(root, "architecture"),
                    root.TryGetProperty("capabilities", out JsonElement caps) && caps.ValueKind == JsonValueKind.Array
                        ? EnumerateStrings(caps)
                        : []);
            }
            catch (JsonException)
            {
                // not JSON — keep scanning (the editor may print unrelated lines first)
            }
        }
        return null;
    }

    /// <summary>
    /// Runs <c>&lt;editorExe&gt; --hub-info</c> and parses the reply. Null on any failure
    /// (missing exe, crash, timeout, legacy editor) — callers treat null as "no hub support".
    /// </summary>
    public static HubInfo? ProbeEditor(string editorExePath, int timeoutMs = 15000)
    {
        if (string.IsNullOrEmpty(editorExePath) || !File.Exists(editorExePath))
            return null;
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = editorExePath,
                    Arguments = "--hub-info",
                    UseShellExecute = false,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    CreateNoWindow = true,
                },
            };
            if (!process.Start()) return null;
            string stdout = process.StandardOutput.ReadToEnd();
            process.WaitForExit(timeoutMs);
            return process.HasExited ? ParseHubInfo(stdout) : null;
        }
        catch
        {
            return null;
        }
    }

    private static string TryGetString(JsonElement element, string name) =>
        element.TryGetProperty(name, out JsonElement value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? ""
            : "";

    private static string[] EnumerateStrings(JsonElement array)
    {
        var values = new string[array.GetArrayLength()];
        int i = 0;
        foreach (JsonElement item in array.EnumerateArray())
        {
            values[i++] = item.ValueKind == JsonValueKind.String ? item.GetString() ?? "" : "";
        }
        return values;
    }
}
