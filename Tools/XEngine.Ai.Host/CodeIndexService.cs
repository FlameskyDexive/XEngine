using Microsoft.Build.Locator;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.FindSymbols;
using Microsoft.CodeAnalysis.MSBuild;
using XEngine.Ai.Protocol;

namespace XEngine.Ai.Host;

/// <summary>
/// ★ Layer 1 (B1) — Roslyn-backed code symbol index over XEngine.sln. Lets an AI find symbols
/// (types/members) by name across the whole solution without loading the engine runtime. Ported from
/// XMcp.CodeIndexService, trimmed to the highest-value query (symbol search); references/callers/
/// diagnostics are follow-ups. Uses MSBuildWorkspace + MSBuildLocator (same as XMcp).
/// </summary>
internal sealed class CodeIndexService : IDisposable
{
    private const int MaxResults = 500;
    private static int s_registered; // MSBuildLocator is process-global; register once.
    private readonly SemaphoreSlim _gate = new(1, 1);
    private MSBuildWorkspace? _workspace;
    private Solution? _solution;
    private string? _lastError;

    public async Task<CodeSymbolResult> FindSymbolsAsync(
        string solutionPath, string query, int offset, int limit, CancellationToken cancellationToken)
    {
        Solution solution = await EnsureSolutionAsync(solutionPath, cancellationToken).ConfigureAwait(false);

        IEnumerable<ISymbol> declarations = await SymbolFinder.FindSourceDeclarationsAsync(
            solution, query, ignoreCase: true, SymbolFilter.TypeAndMember, cancellationToken);

        ISymbol[] matches = declarations
            .Where(s => s.Locations.Any(l => l.IsInSource))
            .DistinctBy(s => s.ToDisplayString(), StringComparer.Ordinal)
            .OrderBy(s => s.ToDisplayString(), StringComparer.Ordinal)
            .ToArray();

        CodeSymbol[] page = matches
            .Skip(offset)
            .Take(Math.Clamp(limit, 1, MaxResults))
            .Select(s => ToSymbol(s, solution))
            .ToArray();

        return new CodeSymbolResult(CreateStatus(solutionPath, solution), offset, limit, matches.Length, page);
    }

    private async Task<Solution> EnsureSolutionAsync(string solutionPath, CancellationToken cancellationToken)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            RegisterMsbuild();

            if (_solution is null || _workspace is null)
            {
                if (string.IsNullOrEmpty(solutionPath) || !File.Exists(solutionPath))
                {
                    throw new FileNotFoundException("Solution file not found: " + solutionPath);
                }

                _workspace = MSBuildWorkspace.Create();
                _workspace.WorkspaceFailed += (_, e) => _lastError = e.Diagnostic.Message;
                _solution = await _workspace.OpenSolutionAsync(solutionPath, cancellationToken: cancellationToken);
            }

            return _solution;
        }
        finally
        {
            _gate.Release();
        }
    }

    private static void RegisterMsbuild()
    {
        if (Interlocked.Exchange(ref s_registered, 1) == 0 && !MSBuildLocator.IsRegistered)
        {
            // Use the SDK that ships with dotnet; VisualStudio instances are optional.
            VisualStudioInstance[] instances = MSBuildLocator.QueryVisualStudioInstances().ToArray();
            if (instances.Length > 0)
            {
                MSBuildLocator.RegisterInstance(instances[0]);
            }
            else
            {
                MSBuildLocator.RegisterDefaults();
            }
        }
    }

    private CodeIndexStatus CreateStatus(string solutionPath, Solution solution)
    {
        int projectCount = solution.Projects.Count();
        int docCount = solution.Projects.Sum(p => p.DocumentIds.Count);
        return new CodeIndexStatus(true, solutionPath, projectCount, docCount, _lastError);
    }

    private static CodeSymbol ToSymbol(ISymbol symbol, Solution solution)
    {
        Location loc = symbol.Locations.First(l => l.IsInSource);
        FileLinePositionSpan span = loc.GetLineSpan();
        string filePath = span.Path ?? "";
        Microsoft.CodeAnalysis.Project? project = solution.Projects.FirstOrDefault(p => p.Documents.Any(d => d.FilePath == filePath));
        return new CodeSymbol(
            Name: symbol.Name,
            Kind: symbol.Kind.ToString(),
            ContainingType: symbol.ContainingType?.ToDisplayString() ?? symbol.ContainingNamespace?.ToDisplayString() ?? "",
            Project: project?.Name ?? "",
            FilePath: filePath,
            Line: span.StartLinePosition.Line + 1,
            Column: span.StartLinePosition.Character + 1,
            DisplayString: symbol.ToDisplayString());
    }

    public void Dispose()
    {
        _workspace?.Dispose();
        _gate.Dispose();
    }
}
