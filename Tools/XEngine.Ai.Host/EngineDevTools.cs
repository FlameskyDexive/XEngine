using System.ComponentModel;
using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using XEngine.Ai.Protocol;

namespace XEngine.Ai.Host;

/// <summary>
/// Tools that only make sense against the ENGINE REPOSITORY itself: the solution-wide Roslyn
/// symbol index and the engine test suite. A user game project has no XEngine.sln / test
/// projects, so these register only in the <see cref="AiEdition.EngineDev"/> edition (see
/// <see cref="ToolTypeCatalog"/>). New engine-internal tools belong in this class (or a sibling
/// carrying the same <see cref="AiToolEditionAttribute"/>), never in the shared tool classes.
/// </summary>
[McpServerToolType]
[AiToolEdition(AiEdition.EngineDev)]
public static class EngineDevTools
{
    // ---- B1 code tool (Roslyn symbol index over the solution) ----

    [McpServerTool(Name = "code_symbols", UseStructuredContent = true)]
    [Description("Search the solution's code symbol index (types/members) by name. Requires the solution file (XEngine.sln) at the project root and the .NET SDK on PATH.")]
    public static Task<CodeSymbolResult> FindCodeSymbols(
        IServiceProvider services,
        [Description("Symbol name (or substring) to search for, case-insensitive.")] string query,
        [Description("Page offset, >= 0.")] int offset = 0,
        [Description("Page size, 1-500.")] int limit = 100,
        CancellationToken cancellationToken = default)
    {
        var project = services.GetRequiredService<ProjectDescriptor>();
        if (string.IsNullOrEmpty(project.SolutionPath))
        {
            throw new FileNotFoundException("No solution file found at the project root (code tools need XEngine.sln).");
        }

        return services.GetRequiredService<CodeIndexService>()
            .FindSymbolsAsync(project.SolutionPath, query, offset, limit, cancellationToken);
    }

    [McpServerTool(Name = "run_tests", UseStructuredContent = true)]
    [Description("Run xUnit tests via 'dotnet test'. Target is a .csproj (one project) or .sln (whole solution); when omitted, auto-discovers XEngine.sln at the project root. Returns parsed passed/failed/skipped/total counts + the output tail. Supports a --filter (xUnit filter expression) and a timeout.")]
    public static Task<TestRunResult> RunTests(
        IServiceProvider services,
        [Description("Optional absolute path to a .csproj or .sln to test. Omit to auto-discover XEngine.sln.")] string? testProject = null,
        [Description("Optional xUnit filter expression (e.g. 'FullyQualifiedName~CameraTests').")] string? filter = null,
        [Description("Timeout in seconds (0 = no limit). Default 300 (5 min).")] int timeoutSeconds = 300,
        CancellationToken cancellationToken = default)
    {
        var project = services.GetRequiredService<ProjectDescriptor>();
        return TestRunner.RunAsync(testProject, project.SolutionPath, filter, timeoutSeconds, cancellationToken);
    }
}
