using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace XEngine.Analyzers;

/// <summary>PR0002: bans capturing lambdas/anonymous methods inside [HotPath].</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class HotPathClosureAnalyzer : DiagnosticAnalyzer
{
    private static readonly DiagnosticDescriptor s_rule = new(
        DiagnosticIds.HotPathClosure,
        "Hot path closure forbidden",
        "Capturing lambda/anonymous method in [HotPath] allocates a closure. Use static delegates or explicit locals.",
        "Performance",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(s_rule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(
            Analyze,
            SyntaxKind.SimpleLambdaExpression,
            SyntaxKind.ParenthesizedLambdaExpression,
            SyntaxKind.AnonymousMethodExpression);
    }

    private static void Analyze(SyntaxNodeAnalysisContext ctx)
    {
        if (!HotPathUtils.IsHotPath(ctx, ctx.Node))
            return;

        var flow = ctx.SemanticModel.AnalyzeDataFlow(ctx.Node);
        if (flow is null || !flow.Succeeded)
            return;

        if (!flow.Captured.IsDefaultOrEmpty && flow.Captured.Length > 0)
            ctx.ReportDiagnostic(Diagnostic.Create(s_rule, ctx.Node.GetLocation()));
    }
}
