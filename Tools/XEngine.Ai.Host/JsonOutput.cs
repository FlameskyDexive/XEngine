namespace XEngine.Ai.Host;

/// <summary>
/// Shared JSON serializer options for all CLI/structured output. Web defaults (camelCase),
/// enums-as-strings for readability, and a Unicode-safe encoder so Chinese intent text and
/// asset names round-trip unescaped (XMcp pattern — important because provenance intent is
/// often natural-language Chinese).
/// </summary>
internal static class JsonOutput
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web)
    {
        Encoder = JavaScriptEncoder.Create(UnicodeRanges.All),
        WriteIndented = true,
    };

    static JsonOutput()
    {
        Options.Converters.Add(new JsonStringEnumConverter());
    }

    /// <summary>Serialize to stdout. Kept centralized so every CLI command shares one shape.</summary>
    public static void Write(object value)
    {
        string json = JsonSerializer.Serialize(value, value.GetType(), Options);
        Console.WriteLine(json);
    }
}
