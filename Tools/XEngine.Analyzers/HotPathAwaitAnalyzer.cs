using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.Diagnostics;

namespace XEngine.Analyzers;

/// <summary>PR0005: bans await inside [HotPath].</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class HotPathAwaitAnalyzer : DiagnosticAnalyzer
{
    private static readonly DiagnosticDescriptor s_rule = new(
        DiagnosticIds.HotPathAwait,
        "Hot path await forbidden",
        "await in [HotPath] is forbidden (async state machine allocates). Keep hot paths synchronous.",
        "Performance",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(s_rule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(Analyze, SyntaxKind.AwaitExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext ctx)
    {
        if (!HotPathUtils.IsHotPath(ctx, ctx.Node))
            return;

        ctx.ReportDiagnostic(Diagnostic.Create(s_rule, ctx.Node.GetLocation()));
    }
}
