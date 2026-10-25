using System.Collections.Immutable;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace XEngine.Analyzers;

/// <summary>PR0007: bans new of reference types without [Pool] inside [HotPath].</summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class HotPathNewAnalyzer : DiagnosticAnalyzer
{
    private static readonly DiagnosticDescriptor s_rule = new(
        DiagnosticIds.HotPathNew,
        "Hot path new reference type forbidden",
        "new of reference type '{0}' in [HotPath] allocates. Mark type with [Pool] and use Rent(), or avoid allocation.",
        "Performance",
        DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics =>
        ImmutableArray.Create(s_rule);

    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();
        context.RegisterSyntaxNodeAction(AnalyzeObjectCreation, SyntaxKind.ObjectCreationExpression);
        context.RegisterSyntaxNodeAction(AnalyzeImplicitObjectCreation, SyntaxKind.ImplicitObjectCreationExpression);
        context.RegisterSyntaxNodeAction(AnalyzeArrayCreation, SyntaxKind.ArrayCreationExpression);
        context.RegisterSyntaxNodeAction(AnalyzeImplicitArrayCreation, SyntaxKind.ImplicitArrayCreationExpression);
        // C# 12 collection expressions: int[] xs = [1, 2, 3]; List<int> ys = [1, 2];
        context.RegisterSyntaxNodeAction(AnalyzeCollectionExpression, SyntaxKind.CollectionExpression);
    }

    private static void AnalyzeObjectCreation(SyntaxNodeAnalysisContext ctx)
    {
        if (!HotPathUtils.IsHotPath(ctx, ctx.Node))
            return;

        var creation = (ObjectCreationExpressionSyntax)ctx.Node;
        var type = ctx.SemanticModel.GetTypeInfo(creation).Type;
        ReportIfForbidden(ctx, creation.GetLocation(), type);
    }

    private static void AnalyzeImplicitObjectCreation(SyntaxNodeAnalysisContext ctx)
    {
        if (!HotPathUtils.IsHotPath(ctx, ctx.Node))
            return;

        var creation = (ImplicitObjectCreationExpressionSyntax)ctx.Node;
        var type = ctx.SemanticModel.GetTypeInfo(creation).Type;
        ReportIfForbidden(ctx, creation.GetLocation(), type);
    }

    private static void AnalyzeArrayCreation(SyntaxNodeAnalysisContext ctx)
    {
        if (!HotPathUtils.IsHotPath(ctx, ctx.Node))
            return;

        // Arrays are reference types and always allocate (unless stackalloc).
        ctx.ReportDiagnostic(Diagnostic.Create(s_rule, ctx.Node.GetLocation(), "array"));
    }

    private static void AnalyzeImplicitArrayCreation(SyntaxNodeAnalysisContext ctx)
    {
        if (!HotPathUtils.IsHotPath(ctx, ctx.Node))
            return;

        ctx.ReportDiagnostic(Diagnostic.Create(s_rule, ctx.Node.GetLocation(), "array"));
    }

    private static void AnalyzeCollectionExpression(SyntaxNodeAnalysisContext ctx)
    {
        if (!HotPathUtils.IsHotPath(ctx, ctx.Node))
            return;

        // Collection expressions often have Type == null; the target lives in ConvertedType.
        var typeInfo = ctx.SemanticModel.GetTypeInfo(ctx.Node);
        var type = typeInfo.Type ?? typeInfo.ConvertedType;
        if (type is null)
            return;

        // Only flag when the target is a reference type or array (heap allocation).
        // Span/ReadOnlySpan targets are value types and are left alone.
        if (type.TypeKind == TypeKind.Array)
        {
            ctx.ReportDiagnostic(Diagnostic.Create(s_rule, ctx.Node.GetLocation(), "array"));
            return;
        }

        if (type.IsValueType)
            return;

        ReportIfForbidden(ctx, ctx.Node.GetLocation(), type);
    }

    private static void ReportIfForbidden(SyntaxNodeAnalysisContext ctx, Location location, ITypeSymbol? type)
    {
        if (type is null)
            return;

        // Value types / primitives are fine.
        if (type.IsValueType)
            return;

        // string literals are not object creation expressions; `new string(...)` allocates and is flagged.
        // [Pool] types are exempt (must use Rent in practice; new is still discouraged but plan says exempt).
        if (HotPathUtils.HasAttributeNamed(type, HotPathUtils.PoolAttributeMetadataName, "Pool"))
            return;

        ctx.ReportDiagnostic(Diagnostic.Create(s_rule, location, type.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat)));
    }
}
