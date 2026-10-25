// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Globalization;
using System.Text;

namespace XEngine.Ads.HarmonyOS;

/// <summary>A parsed event from the ArkTS glue (Native/glue/*.ets protocol, v1).</summary>
internal readonly struct HarmonyOSAdsEvent
{
    public int Id { get; init; }
    /// <summary><c>init-result | load-result | show-result | close-result | error | state</c></summary>
    public string Type { get; init; }
    public bool Ok { get; init; }
    public bool Reward { get; init; }
    public string Error { get; init; }
    /// <summary>For <c>state</c> events: <c>show-start | show-end</c>.</summary>
    public string State { get; init; }
    /// <summary><c>reward | interstitial</c> — named to avoid shadowing the <see cref="AdType"/> enum.</summary>
    public string AdTypeToken { get; init; }

    public bool IsAdType(AdType type) =>
        type == XEngine.Ads.AdType.RewardVideo
            ? string.Equals(AdTypeToken, "reward", StringComparison.Ordinal)
            : string.Equals(AdTypeToken, "interstitial", StringComparison.Ordinal);
}

/// <summary>
/// Wire protocol between the C# provider and the ArkTS glue. Requests are built as plain
/// strings (no serializer dependency on the hot paths of the NativeAOT player); events are
/// parsed with <c>System.Text.Json.JsonDocument</c>, which is AOT-safe.
/// </summary>
internal static class HarmonyOSAdsProtocol
{
    public const int Version = 1;

    public static string BuildInitialize(int id) => $"{{\"v\":{Version},\"id\":{id},\"op\":\"initialize\"}}";

    public static string BuildClose(int id) => $"{{\"v\":{Version},\"id\":{id},\"op\":\"close\"}}";

    public static string BuildLoad(int id, AdType type, string slotId, bool testAds)
    {
        var json = new StringBuilder(128);
        json.Append("{\"v\":").Append(Version)
            .Append(",\"id\":").Append(id)
            .Append(",\"op\":\"load\"")
            .Append(",\"adType\":\"").Append(AdTypeToken(type)).Append('"')
            .Append(",\"slotId\":\"").Append(Escape(slotId)).Append('"')
            .Append(",\"test\":").Append(testAds ? "true" : "false")
            .Append('}');
        return json.ToString();
    }

    public static string BuildShow(int id, AdType type)
    {
        var json = new StringBuilder(96);
        json.Append("{\"v\":").Append(Version)
            .Append(",\"id\":").Append(id)
            .Append(",\"op\":\"show\"")
            .Append(",\"adType\":\"").Append(AdTypeToken(type)).Append('"')
            .Append('}');
        return json.ToString();
    }

    public static string AdTypeToken(AdType type) => type switch
    {
        AdType.RewardVideo => "reward",
        AdType.Interstitial => "interstitial",
        _ => throw new ArgumentOutOfRangeException(nameof(type), type, "Unknown ad type."),
    };

    /// <summary>Parses a glue event; returns false for malformed payloads (never throws).</summary>
    public static bool TryParseEvent(string json, out HarmonyOSAdsEvent parsed)
    {
        parsed = default;
        if (string.IsNullOrWhiteSpace(json))
            return false;
        try
        {
            using var document = System.Text.Json.JsonDocument.Parse(json);
            System.Text.Json.JsonElement root = document.RootElement;
            if (root.ValueKind != System.Text.Json.JsonValueKind.Object)
                return false;
            if (!root.TryGetProperty("type", out System.Text.Json.JsonElement typeElement) ||
                typeElement.ValueKind != System.Text.Json.JsonValueKind.String)
            {
                return false;
            }

            parsed = new HarmonyOSAdsEvent
            {
                Type = typeElement.GetString() ?? string.Empty,
                Id = root.TryGetProperty("id", out System.Text.Json.JsonElement idElement) &&
                     idElement.ValueKind == System.Text.Json.JsonValueKind.Number
                    ? idElement.GetInt32()
                    : -1,
                Ok = root.TryGetProperty("ok", out System.Text.Json.JsonElement okElement) &&
                     okElement.ValueKind == System.Text.Json.JsonValueKind.True,
                Reward = root.TryGetProperty("reward", out System.Text.Json.JsonElement rewardElement) &&
                         rewardElement.ValueKind == System.Text.Json.JsonValueKind.True,
                Error = root.TryGetProperty("error", out System.Text.Json.JsonElement errorElement) &&
                        errorElement.ValueKind == System.Text.Json.JsonValueKind.String
                    ? errorElement.GetString() ?? string.Empty
                    : string.Empty,
                State = root.TryGetProperty("state", out System.Text.Json.JsonElement stateElement) &&
                        stateElement.ValueKind == System.Text.Json.JsonValueKind.String
                    ? stateElement.GetString() ?? string.Empty
                    : string.Empty,
                AdTypeToken = root.TryGetProperty("adType", out System.Text.Json.JsonElement adTypeElement) &&
                         adTypeElement.ValueKind == System.Text.Json.JsonValueKind.String
                    ? adTypeElement.GetString() ?? string.Empty
                    : string.Empty,
            };
            return true;
        }
        catch (System.Text.Json.JsonException)
        {
            return false;
        }
    }

    private static string Escape(string value)
    {
        if (string.IsNullOrEmpty(value))
            return value;
        var escaped = new StringBuilder(value.Length + 8);
        foreach (char c in value)
        {
            switch (c)
            {
                case '"': escaped.Append("\\\""); break;
                case '\\': escaped.Append("\\\\"); break;
                case '\n': escaped.Append("\\n"); break;
                case '\r': escaped.Append("\\r"); break;
                case '\t': escaped.Append("\\t"); break;
                default:
                    if (c < ' ')
                        escaped.Append("\\u").Append(((int)c).ToString("x4", CultureInfo.InvariantCulture));
                    else
                        escaped.Append(c);
                    break;
            }
        }
        return escaped.ToString();
    }
}
