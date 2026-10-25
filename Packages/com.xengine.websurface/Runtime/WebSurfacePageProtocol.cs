using System;
using System.Text.Json;

namespace XEngine.WebSurface;

/// <summary>
/// The control protocol between the host (engine process) and the hosted page bridge script.
/// Frame pixels never travel through these messages — the page writes them into the shared
/// buffer posted by the host and only signals their availability here. Kept pure (plain JSON
/// DTOs over strings) so the host and page sides can be tested without a browser.
///
/// Host → page messages:
///   {"type":"websurface","event":"produce","no":N}                    — one produce token
///   {"type":"websurface","event":"input", ...}                        — pointer forwarding (B6)
/// Page → host messages:
///   {"type":"websurface","event":"ready"}                             — bridge installed
///   {"type":"websurface","event":"frame","no":N,"pageMs":F}           — frame N is in the buffer
///   {"type":"websurface","event":"error","message":"..."}             — bridge-side failure
/// Messages of any other shape are ignored by both sides (forward compatibility).
/// </summary>
public static class WebSurfacePageProtocol
{
    public const string MessageType = "websurface";

    private const string EventType = "event";
    private const string FrameNoField = "no";
    private const string PageMsField = "pageMs";
    private const string MessageField = "message";
    private const string WidthField = "width";
    private const string HeightField = "height";

    private static readonly JsonSerializerOptions Options = new()
    {
        IncludeFields = true,
    };

    /// <summary>Produces a produce-token message; the page may capture exactly one frame.</summary>
    public static string Produce(ulong frameNumber) =>
        $"{{\"type\":\"{MessageType}\",\"event\":\"produce\",\"no\":{frameNumber}}}";

    /// <summary>
    /// Produces the shared-buffer hand-off payload posted alongside
    /// <c>PostSharedBufferToScript</c> so the page knows how to interpret the memory.
    /// </summary>
    public static string SharedBufferHandoff(int width, int height) =>
        $"{{\"type\":\"{MessageType}\",\"event\":\"sharedbuffer\","
        + $"\"{WidthField}\":{width},\"{HeightField}\":{height},"
        + $"\"format\":\"rgba8\",\"origin\":\"topLeft\",\"alpha\":\"straight\"}}";

    public static string? TryParseEvent(string json, out ulong frameNumber, out double pageMs, out string? message)
    {
        frameNumber = 0;
        pageMs = 0;
        message = null;

        string? eventType = null;
        try
        {
            using var document = JsonDocument.Parse(json);
            JsonElement root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !root.TryGetProperty("type", out JsonElement typeElement)
                || typeElement.ValueKind != JsonValueKind.String
                || !string.Equals(typeElement.GetString(), MessageType, StringComparison.Ordinal)
                || !root.TryGetProperty(EventType, out JsonElement eventElement)
                || eventElement.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            eventType = eventElement.GetString();
            if (root.TryGetProperty(FrameNoField, out JsonElement noElement)
                && noElement.ValueKind == JsonValueKind.Number
                && noElement.TryGetUInt64(out ulong parsed))
            {
                frameNumber = parsed;
            }
            if (root.TryGetProperty(PageMsField, out JsonElement msElement)
                && msElement.ValueKind == JsonValueKind.Number)
            {
                pageMs = msElement.GetDouble();
            }
            if (root.TryGetProperty(MessageField, out JsonElement messageElement)
                && messageElement.ValueKind == JsonValueKind.String)
            {
                message = messageElement.GetString();
            }
        }
        catch (JsonException)
        {
            return null;
        }

        return eventType;
    }

    public static bool IsReadyEvent(string? eventType) => eventType == "ready";

    public static bool IsFrameEvent(string? eventType) => eventType == "frame";

    public static bool IsErrorEvent(string? eventType) => eventType == "error";
}
