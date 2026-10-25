namespace XEngine.Analyzers;

internal static class DiagnosticIds
{
    public const string LinqForbidden = "PR0001";
    public const string HotPathClosure = "PR0002";
    public const string HotPathForeach = "PR0003";
    public const string HotPathStringConcat = "PR0004";
    public const string HotPathAwait = "PR0005";
    public const string HotPathParams = "PR0006";
    public const string HotPathNew = "PR0007";
    public const string EngineObjectNullConditional = "PR0008";
    public const string EngineObjectNullCoalescing = "PR0009";
    public const string MonoBehaviourCtorMissing = "PR0011";
    public const string MonoBehaviourCtor = "PR0010";
}
