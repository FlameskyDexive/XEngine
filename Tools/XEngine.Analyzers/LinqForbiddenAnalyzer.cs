using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace XEngine.Analyzers;

/// <summary>PR0001: reviews LINQ invocations against the benchmark-backed allowlist.</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class LinqForbiddenAnalyzer : DiagnosticAnalyzer
{
    private static readonly DiagnosticDescriptor s_rule = new(
        DiagnosticIds.LinqForbidden,
        "LINQ form is not benchmark-allowlisted",
        "LINQ form is not benchmark-allowlisted in XEngine. Use an explicit loop or add benchmark evidence. Affected: {0}.",
        "Performance",
        DiagnosticSeverity.Warning,
        isEnabledByDefault: true,
        description: "PR0001 permits only operator/source shapes proven competitive by the Runtime LINQ benchmark suite.");

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(s_rule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeInvocation, SyntaxKind.InvocationExpression);
        context.RegisterSyntaxNodeAction(AnalyzeQuery, SyntaxKind.QueryExpression);
    }

    private static void AnalyzeInvocation(SyntaxNodeAnalysisContext ctx)
    {
        var inv = (InvocationExpressionSyntax)ctx.Node;
        if (ctx.SemanticModel.GetSymbolInfo(inv).Symbol is not IMethodSymbol m)
            return;

        var ns = m.ContainingNamespace?.ToDisplayString();
        if (ns != null &&
            (ns == "System.Linq" || ns.StartsWith("System.Linq.", System.StringComparison.Ordinal)))
        {
            if (IsBenchmarkAllowlisted(ctx, inv, m))
                return;

            ctx.ReportDiagnostic(Diagnostic.Create(s_rule, inv.GetLocation(), m.Name));
        }
    }

    private static bool IsBenchmarkAllowlisted(
        SyntaxNodeAnalysisContext ctx,
        InvocationExpressionSyntax invocation,
        IMethodSymbol method)
    {
        ITypeSymbol? sourceType = GetSourceType(ctx, invocation, method);

        // Direct ToList on a statically known array/List uses Count + bulk-copy paths and was
        // 1.9-4x faster with the same result allocation. Materialization is still forbidden in
        // [HotPath], and iterator/IEnumerable sources remain warnings.
        if (method.Name == "ToList")
            return !HotPathUtils.IsHotPath(ctx, invocation) && IsArrayOrList(sourceType);

        // Predicate terminals require an explicit static lambda so the delegate is cached.
        switch (method.Name)
        {
            case "Any":
            case "FirstOrDefault":
                break;
            case "All":
            case "Count":
                // Array results crossed over by size; List<T> won consistently.
                if (!IsList(sourceType))
                    return false;
                break;
            default:
                return false;
        }

        // Parameterless Count()/Any()/FirstOrDefault() are not allowlisted — only Func/predicate
        // overloads were measured. Prefer an explicit static lambda so the delegate is cached.
        if (!HasExplicitStaticPredicateArgument(ctx, invocation))
            return false;

        return IsArrayOrList(sourceType);
    }

    private static ITypeSymbol? GetSourceType(
        SyntaxNodeAnalysisContext ctx,
        InvocationExpressionSyntax invocation,
        IMethodSymbol method)
    {
        if (method.ReducedFrom != null &&
            invocation.Expression is MemberAccessExpressionSyntax memberAccess)
            return ctx.SemanticModel.GetTypeInfo(memberAccess.Expression).Type;

        return invocation.ArgumentList.Arguments.Count > 0
            ? ctx.SemanticModel.GetTypeInfo(invocation.ArgumentList.Arguments[0].Expression).Type
            : null;
    }

    /// <summary>
    /// True when an invocation contains a Func argument expressed as an explicit static lambda.
    /// Excludes parameterless overloads, capturing lambdas, method groups, and delegate variables.
    /// </summary>
    private static bool HasExplicitStaticPredicateArgument(
        SyntaxNodeAnalysisContext ctx,
        InvocationExpressionSyntax invocation)
    {
        foreach (ArgumentSyntax argument in invocation.ArgumentList.Arguments)
        {
            ITypeSymbol? convertedType = ctx.SemanticModel.GetTypeInfo(argument.Expression).ConvertedType;
            if (convertedType is not INamedTypeSymbol named ||
                !named.OriginalDefinition.MetadataName.StartsWith("Func`", System.StringComparison.Ordinal) ||
                named.OriginalDefinition.ContainingNamespace.ToDisplayString() != "System")
                continue;

            if (argument.Expression is not LambdaExpressionSyntax lambda)
                return false;

            foreach (SyntaxToken modifier in lambda.Modifiers)
                if (modifier.IsKind(SyntaxKind.StaticKeyword))
                    return true;

            return false;
        }

        return false;
    }

    private static bool IsArrayOrList(ITypeSymbol? type)
    {
        if (type is IArrayTypeSymbol)
            return true;

        return IsList(type);
    }

    private static bool IsList(ITypeSymbol? type) =>
        type is INamedTypeSymbol named &&
        named.OriginalDefinition.MetadataName == "List`1" &&
        named.OriginalDefinition.ContainingNamespace.ToDisplayString() == "System.Collections.Generic";

    private static void AnalyzeQuery(SyntaxNodeAnalysisContext ctx)
    {
        ctx.ReportDiagnostic(Diagnostic.Create(s_rule, ctx.Node.GetLocation(), "query expression"));
    }
}
