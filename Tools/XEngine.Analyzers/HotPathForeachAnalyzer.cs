using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace XEngine.Analyzers;

/// <summary>PR0003: bans foreach when GetEnumerator() returns a class (boxing/allocation) in [HotPath].</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class HotPathForeachAnalyzer : DiagnosticAnalyzer
{
    private static readonly DiagnosticDescriptor s_rule = new(
        DiagnosticIds.HotPathForeach,
        "Hot path class-enumerator foreach forbidden",
        "foreach over a class enumerator in [HotPath] may allocate. Prefer indexed for-loops or struct enumerators.",
        "Performance",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(s_rule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeForEach, SyntaxKind.ForEachStatement);
        // foreach ((var a, var b) in pairs) — deconstruction form
        context.RegisterSyntaxNodeAction(AnalyzeForEachVariable, SyntaxKind.ForEachVariableStatement);
    }

    private static void AnalyzeForEach(SyntaxNodeAnalysisContext ctx)
    {
        if (!HotPathUtils.IsHotPath(ctx, ctx.Node))
            return;

        var foreachStmt = (ForEachStatementSyntax)ctx.Node;
        AnalyzeCore(ctx, foreachStmt.Expression, foreachStmt.ForEachKeyword.GetLocation(),
            ctx.SemanticModel.GetForEachStatementInfo(foreachStmt));
    }

    private static void AnalyzeForEachVariable(SyntaxNodeAnalysisContext ctx)
    {
        if (!HotPathUtils.IsHotPath(ctx, ctx.Node))
            return;

        var foreachStmt = (ForEachVariableStatementSyntax)ctx.Node;
        AnalyzeCore(ctx, foreachStmt.Expression, foreachStmt.ForEachKeyword.GetLocation(),
            ctx.SemanticModel.GetForEachStatementInfo(foreachStmt));
    }

    private static void AnalyzeCore(
        SyntaxNodeAnalysisContext ctx,
        ExpressionSyntax collectionExpression,
        Location reportLocation,
        ForEachStatementInfo info)
    {
        var collectionType = ctx.SemanticModel.GetTypeInfo(collectionExpression).Type;
        if (collectionType is null)
            return;

        // Arrays and spans use indexed loops under the hood (no allocation). Always OK.
        if (collectionType.TypeKind == TypeKind.Array)
            return;
        if (collectionType.Name == "Span`1" || collectionType.Name == "ReadOnlySpan`1")
            return;

        ITypeSymbol? enumeratorType = info.GetEnumeratorMethod?.ReturnType;
        if (enumeratorType is null)
        {
            enumeratorType = FindGetEnumeratorReturnType(collectionType);
            if (enumeratorType is null)
                return;
        }

        // Struct enumerators (List<T>.Enumerator, arrays, spans) are OK.
        if (enumeratorType.IsValueType)
            return;

        // IEnumerator / class enumerator → report.
        ctx.ReportDiagnostic(Diagnostic.Create(s_rule, reportLocation));
    }

    private static ITypeSymbol? FindGetEnumeratorReturnType(ITypeSymbol collectionType)
    {
        foreach (var member in collectionType.GetMembers("GetEnumerator"))
        {
            if (member is IMethodSymbol method && method.Parameters.Length == 0)
                return method.ReturnType;
        }

        foreach (var iface in collectionType.AllInterfaces)
        {
            foreach (var member in iface.GetMembers("GetEnumerator"))
            {
                if (member is IMethodSymbol method && method.Parameters.Length == 0)
                    return method.ReturnType;
            }
        }

        return null;
    }
}
