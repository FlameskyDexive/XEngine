namespace XEngine.XTS;

/// <summary>
/// Creation options for a script environment. Zero values take the native
/// defaults. Limits exist to keep gameplay scripts bounded: the heap limit
/// raises <see cref="XtsStatus.Limit"/> on exhaustion, the stack limit bounds
/// recursion, and the per-call deadline (set on the runtime) bounds loops.
/// </summary>
public sealed class XtsRuntimeOptions
{
    public static XtsRuntimeOptions Default { get; } = new();

    /// <summary>QuickJS heap limit in bytes; 0 = engine default.</summary>
    public ulong MemoryLimitBytes { get; init; }

    /// <summary>JS stack limit in bytes; 0 = engine default.</summary>
    public ulong MaxStackBytes { get; init; }
}
