namespace XEngine.Ai.Gateway;

/// <summary>Name → provider lookup. One instance per Host; providers self-describe via
/// <see cref="IGenProvider.Name"/>.</summary>
public sealed class GenProviderRegistry
{
    private readonly Dictionary<string, IGenProvider> _providers = new(StringComparer.OrdinalIgnoreCase);

    public void Register(IGenProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _providers[provider.Name] = provider;
    }

    public IGenProvider Get(string name)
    {
        if (_providers.TryGetValue(name, out IGenProvider? provider))
        {
            return provider;
        }

        throw new InvalidOperationException(
            $"Unknown generation provider '{name}'. Registered: {string.Join(", ", _providers.Keys.OrderBy(k => k, StringComparer.Ordinal))}.");
    }

    public bool TryGet(string name, out IGenProvider provider)
    {
        if (_providers.TryGetValue(name, out IGenProvider? found))
        {
            provider = found;
            return true;
        }

        provider = null!;
        return false;
    }

    public IReadOnlyCollection<IGenProvider> All => _providers.Values;

    /// <summary>The default Host registry: mock always available (offline dev + tests + workflow
    /// smoke runs); real providers are registered here as they land (P1.3/P1.4/P3.3). Real providers
    /// resolve keys lazily — registration never fails on a keyless machine.</summary>
    /// <param name="projectRoot">Enables .xengine/secrets.json lookup and hosts the synchronous
    /// providers' result cache; null → environment-only keys + temp-dir cache.</param>
    public static GenProviderRegistry CreateDefault(string? projectRoot = null)
    {
        Func<string?> KeyFor(string provider) => () => projectRoot is null
            ? Environment.GetEnvironmentVariable(SecretResolver.EnvVarName(provider))
            : SecretResolver.Resolve(provider, projectRoot);

        string cacheRoot = projectRoot is null
            ? Path.Combine(Path.GetTempPath(), "xengine-gen-cache")
            : Path.Combine(projectRoot, ".xengine", "gen-cache");

        var registry = new GenProviderRegistry();
        registry.Register(new MockGenProvider());
        registry.Register(new MeshyGenProvider(KeyFor(MeshyGenProvider.ProviderName)));
        registry.Register(new PixelLabGenProvider(KeyFor(PixelLabGenProvider.ProviderName), cacheRoot));
        registry.Register(new BlockadeGenProvider(KeyFor(BlockadeGenProvider.ProviderName)));
        registry.Register(new ElevenLabsGenProvider(KeyFor(ElevenLabsGenProvider.ProviderName), cacheRoot));
        registry.Register(new ComfyUiGenProvider(KeyFor(ComfyUiGenProvider.ProviderName), projectRoot));
        registry.Register(new OpenAiImageGenProvider(KeyFor(OpenAiImageGenProvider.ProviderName), cacheRoot, projectRoot));
        return registry;
    }
}
