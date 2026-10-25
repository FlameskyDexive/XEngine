namespace XEngine.Ai.Gateway;

/// <summary>
/// ★ Gen-P1.1 — The full, replayable description of one generation request: which provider, what
/// kind of asset, and every parameter that shaped the output. The recipe is the pipeline's unit of
/// provenance (recorded to the journal and the task store) and of idempotency: two submissions with
/// the same <see cref="ComputeInputHash"/> are the same request and must not bill twice. It is also
/// what makes an asset "re-rollable" — tweak a field, resubmit, overwrite-import under the same
/// GUID, and every scene reference picks up the new art.
/// </summary>
public sealed record GenRecipe(
    /// <summary>Provider name as registered in <see cref="GenProviderRegistry"/> (e.g. "meshy", "mock").</summary>
    string Provider,
    /// <summary>Asset kind: model3d | motion | texture | sprite | skybox | sfx.</summary>
    string Kind,
    /// <summary>The natural-language prompt (or equivalent primary input).</summary>
    string Prompt,
    /// <summary>Provider-side model/version id (e.g. "meshy-5"), when selectable.</summary>
    string? Model = null,
    /// <summary>Deterministic seed, when the provider supports one.</summary>
    int? Seed = null,
    /// <summary>Project style anchor id (LoRA model id, palette name, ...) injected by gen_submit (P3.2).</summary>
    string? StyleAnchor = null,
    /// <summary>Provider-specific extras (topology=quad, artStyle=realistic, duration=2.5, ...).
    /// String-typed on purpose: values travel through CLI args and JSON untouched.</summary>
    IReadOnlyDictionary<string, string>? Extra = null)
{
    /// <summary>Canonical asset kinds (providers may support a subset).</summary>
    public static class Kinds
    {
        public const string Model3d = "model3d";
        public const string Motion = "motion";
        public const string Texture = "texture";
        public const string Sprite = "sprite";
        public const string Skybox = "skybox";
        public const string Sfx = "sfx";

        public static readonly IReadOnlyList<string> All = new[] { Model3d, Motion, Texture, Sprite, Skybox, Sfx };
    }

    /// <summary>
    /// Deterministic SHA-256 (lowercase hex) over every generation-shaping field. Field order is
    /// fixed and <see cref="Extra"/> is key-sorted (ordinal), so semantically identical recipes hash
    /// identically regardless of construction order. Used as the idempotency/billing dedupe key.
    /// </summary>
    public string ComputeInputHash()
    {
        var canonical = new StringBuilder()
            .Append(Provider).Append('\n')
            .Append(Kind).Append('\n')
            .Append(Prompt).Append('\n')
            .Append(Model ?? "").Append('\n')
            .Append(Seed?.ToString() ?? "").Append('\n')
            .Append(StyleAnchor ?? "").Append('\n');

        if (Extra is { Count: > 0 })
        {
            foreach (KeyValuePair<string, string> pair in Extra.OrderBy(p => p.Key, StringComparer.Ordinal))
            {
                canonical.Append(pair.Key).Append('=').Append(pair.Value).Append('\n');
            }
        }

        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(canonical.ToString()));
        return Convert.ToHexStringLower(hash);
    }
}
