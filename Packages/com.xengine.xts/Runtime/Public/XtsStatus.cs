namespace XEngine.XTS;

/// <summary>Outcome of an XTS native call (mirrors xts_status in the ABI).</summary>
public enum XtsStatus : int
{
    Ok = 0,
    /// <summary>JS parse/link failure (syntax error, unresolved import).</summary>
    Compile = 1,
    /// <summary>A JS exception escaped to the boundary.</summary>
    Exception = 2,
    /// <summary>Resource limit hit: execution deadline, memory limit, or interrupt.</summary>
    Limit = 3,
    BadHandle = 4,
    BadArg = 5,
}
