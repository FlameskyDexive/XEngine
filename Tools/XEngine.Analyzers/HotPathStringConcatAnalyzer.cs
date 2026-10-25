using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace XEngine.Analyzers;

/// <summary>PR0004: bans string + and interpolated strings inside [HotPath].</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class HotPathStringConcatAnalyzer : DiagnosticAnalyzer
{
    private static readonly DiagnosticDescriptor s_rule = new(
        DiagnosticIds.HotPathStringConcat,
        "Hot path string concat forbidden",
        "String concatenation in [HotPath] allocates. Use a non-allocating string builder.",
        "Performance",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(s_rule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeBinary, SyntaxKind.AddExpression);
        context.RegisterSyntaxNodeAction(AnalyzeInterpolation, SyntaxKind.InterpolatedStringExpression);
    }

    private static void AnalyzeBinary(SyntaxNodeAnalysisContext ctx)
    {
        if (!HotPathUtils.IsHotPath(ctx, ctx.Node))
            return;

        var bin = (BinaryExpressionSyntax)ctx.Node;
        var left = ctx.SemanticModel.GetTypeInfo(bin.Left).Type;
        var right = ctx.SemanticModel.GetTypeInfo(bin.Right).Type;
        if (left?.SpecialType == SpecialType.System_String ||
            right?.SpecialType == SpecialType.System_String)
        {
            ctx.ReportDiagnostic(Diagnostic.Create(s_rule, bin.GetLocation()));
        }
    }

    private static void AnalyzeInterpolation(SyntaxNodeAnalysisContext ctx)
    {
        if (!HotPathUtils.IsHotPath(ctx, ctx.Node))
            return;

        ctx.ReportDiagnostic(Diagnostic.Create(s_rule, ctx.Node.GetLocation()));
    }
}
