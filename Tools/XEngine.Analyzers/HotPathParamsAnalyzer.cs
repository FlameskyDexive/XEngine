using System.Collections.Immutable;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace XEngine.Analyzers;

/// <summary>
/// PR0006: bans params methods marked [HotPath], and expanded-params invocations inside [HotPath].
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class HotPathParamsAnalyzer : DiagnosticAnalyzer
{
    private static readonly DiagnosticDescriptor s_rule = new(
        DiagnosticIds.HotPathParams,
        "Hot path params forbidden",
        "params usage in [HotPath] allocates an array. Pass explicit arrays or fixed arity overloads.",
        "Performance",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(s_rule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeMethod, SyntaxKind.MethodDeclaration);
        context.RegisterSyntaxNodeAction(AnalyzeInvocation, SyntaxKind.InvocationExpression);
        context.RegisterSyntaxNodeAction(AnalyzeObjectCreation, SyntaxKind.ObjectCreationExpression);
    }

    private static void AnalyzeMethod(SyntaxNodeAnalysisContext ctx)
    {
        var method = (MethodDeclarationSyntax)ctx.Node;
        if (!HotPathUtils.IsHotPath(ctx, method))
            return;

        var parameters = method.ParameterList.Parameters;
        for (int i = 0; i < parameters.Count; i++)
        {
            var p = parameters[i];
            for (int m = 0; m < p.Modifiers.Count; m++)
            {
                if (p.Modifiers[m].IsKind(SyntaxKind.ParamsKeyword))
                {
                    ctx.ReportDiagnostic(Diagnostic.Create(s_rule, p.GetLocation()));
                    return;
                }
            }
        }
    }

    private static void AnalyzeInvocation(SyntaxNodeAnalysisContext ctx)
    {
        if (!HotPathUtils.IsHotPath(ctx, ctx.Node))
            return;

        var inv = (InvocationExpressionSyntax)ctx.Node;
        if (ctx.SemanticModel.GetSymbolInfo(inv).Symbol is not IMethodSymbol method)
            return;

        if (!HasParamsParameter(method))
            return;

        // Only flag when the compiler synthesizes a params array (expanded form),
        // or when the caller explicitly allocates an array/collection for the args.
        // Passing a preallocated array as the params argument does NOT allocate.
        if (IsExpandedParamsCall(ctx.SemanticModel, inv, ctx.CancellationToken) ||
            HasArrayCreationArgument(inv.ArgumentList))
        {
            ctx.ReportDiagnostic(Diagnostic.Create(s_rule, inv.GetLocation()));
        }
    }

    private static void AnalyzeObjectCreation(SyntaxNodeAnalysisContext ctx)
    {
        if (!HotPathUtils.IsHotPath(ctx, ctx.Node))
            return;

        var creation = (ObjectCreationExpressionSyntax)ctx.Node;
        if (creation.ArgumentList is null)
            return;

        if (ctx.SemanticModel.GetSymbolInfo(creation).Symbol is not IMethodSymbol ctor)
            return;

        if (!HasParamsParameter(ctor))
            return;

        if (IsExpandedParamsCall(ctx.SemanticModel, creation, ctx.CancellationToken) ||
            HasArrayCreationArgument(creation.ArgumentList))
        {
            ctx.ReportDiagnostic(Diagnostic.Create(s_rule, creation.GetLocation()));
        }
    }

    private static bool HasParamsParameter(IMethodSymbol method)
    {
        if (method.Parameters.Length == 0)
            return false;

        return method.Parameters[method.Parameters.Length - 1].IsParams;
    }

    /// <summary>
    /// True when the call uses expanded params form (compiler creates the params array).
    /// False when a single array is passed as the params argument (normal form).
    /// </summary>
    private static bool IsExpandedParamsCall(SemanticModel model, SyntaxNode node, CancellationToken cancellationToken)
    {
        var operation = model.GetOperation(node, cancellationToken);
        ImmutableArray<IArgumentOperation> arguments = default;
        bool hasArgs = false;

        if (operation is IInvocationOperation inv)
        {
            arguments = inv.Arguments;
            hasArgs = true;
        }
        else if (operation is IObjectCreationOperation create)
        {
            arguments = create.Arguments;
            hasArgs = true;
        }

        if (!hasArgs)
            return false;

        for (int i = 0; i < arguments.Length; i++)
        {
            // ArgumentKind.ParamArray means the compiler packs expanded args into a synthesized array.
            if (arguments[i].ArgumentKind == ArgumentKind.ParamArray)
                return true;
        }

        return false;
    }

    private static bool HasArrayCreationArgument(ArgumentListSyntax args)
    {
        for (int i = 0; i < args.Arguments.Count; i++)
        {
            var expr = args.Arguments[i].Expression;
            if (expr is ArrayCreationExpressionSyntax ||
                expr is ImplicitArrayCreationExpressionSyntax)
            {
                return true;
            }

            // Collection expressions (C# 12+) used as the params array still allocate.
            if (expr.IsKind(SyntaxKind.CollectionExpression) ||
                expr.RawKind == 9058 /* SyntaxKind.CollectionExpression fallback */)
                return true;
        }

        return false;
    }
}
