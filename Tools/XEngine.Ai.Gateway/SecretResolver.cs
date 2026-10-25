namespace XEngine.Ai.Gateway;

/// <summary>
/// ★ Gen-P1.1 — API-key resolution: <c>{PROVIDER}_API_KEY</c> environment variable first (CI,
/// per-session overrides), then the project's <c>.xengine/secrets.json</c> (developer machines;
/// the whole <c>.xengine/</c> directory is gitignored). Secrets never travel through MCP arguments
/// and are never echoed into results or the journal.
/// </summary>
public static class SecretResolver
{
    /// <summary>Resolve the API key for a provider, or null when neither source has one.
    /// secrets.json accepts either <c>{"meshy": "sk-..."}</c> or <c>{"meshy": {"apiKey": "sk-..."}}</c>.</summary>
    public static string? Resolve(string providerName, string projectRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(providerName);

        string? fromEnv = Environment.GetEnvironmentVariable(EnvVarName(providerName));
        if (!string.IsNullOrEmpty(fromEnv))
        {
            return fromEnv;
        }

        string path = Path.Combine(projectRoot, ".xengine", "secrets.json");
        if (!File.Exists(path))
        {
            return null;
        }

        try
        {
            using JsonDocument doc = JsonDocument.Parse(File.ReadAllText(path));
            if (!doc.RootElement.TryGetProperty(providerName, out JsonElement entry))
            {
                return null;
            }

            return entry.ValueKind switch
            {
                JsonValueKind.String => entry.GetString(),
                JsonValueKind.Object when entry.TryGetProperty("apiKey", out JsonElement key)
                    && key.ValueKind == JsonValueKind.String => key.GetString(),
                _ => null,
            };
        }
        catch (JsonException)
        {
            throw new InvalidOperationException($"'{path}' is not valid JSON — fix or delete it.");
        }
    }

    /// <summary>"meshy" → "MESHY_API_KEY"; non-alphanumerics become underscores ("pixel-lab" → "PIXEL_LAB_API_KEY").</summary>
    public static string EnvVarName(string providerName)
    {
        var sb = new StringBuilder(providerName.Length + 8);
        foreach (char c in providerName)
        {
            sb.Append(char.IsLetterOrDigit(c) ? char.ToUpperInvariant(c) : '_');
        }

        return sb.Append("_API_KEY").ToString();
    }
}
