using System.Threading.Tasks;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Testing;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Testing;

namespace XEngine.Analyzers.Tests;

internal static class AnalyzerTestHelper
{
    public static async Task VerifyAsync<TAnalyzer>(string source, params DiagnosticResult[] expected)
        where TAnalyzer : DiagnosticAnalyzer, new()
    {
        var test = new CSharpAnalyzerTest<TAnalyzer, DefaultVerifier>
        {
            TestCode = source,
            ReferenceAssemblies = ReferenceAssemblies.Net.Net80,
            CompilerDiagnostics = CompilerDiagnostics.None,
        };

        // Ensure C# 12+ features (collection expressions, etc.) parse in tests.
        test.SolutionTransforms.Add((solution, projectId) =>
        {
            var project = solution.GetProject(projectId);
            if (project?.ParseOptions is not CSharpParseOptions parseOptions)
                return solution;

            return solution.WithProjectParseOptions(
                projectId,
                parseOptions.WithLanguageVersion(LanguageVersion.CSharp12));
        });

        for (int i = 0; i < expected.Length; i++)
            test.ExpectedDiagnostics.Add(expected[i]);

        await test.RunAsync();
    }
}
