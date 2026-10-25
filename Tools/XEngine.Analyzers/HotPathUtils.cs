using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;

namespace XEngine.Analyzers;

internal static class HotPathUtils
{
    public const string HotPathAttributeMetadataName = "XEngine.HotPathAttribute";
    public const string PoolAttributeMetadataName = "XEngine.PoolAttribute";

    /// <summary>True when the node is inside a member or type marked [HotPath].</summary>
    public static bool IsHotPath(SyntaxNodeAnalysisContext ctx, SyntaxNode node)
    {
        for (SyntaxNode? n = node; n != null; n = n.Parent)
        {
            if (n is AccessorDeclarationSyntax)
                continue;

            if (n is MemberDeclarationSyntax m && HasHotPathAttribute(m.AttributeLists, ctx))
                return true;

            if (n is BaseTypeDeclarationSyntax t && HasHotPathAttribute(t.AttributeLists, ctx))
                return true;
        }

        return false;
    }

    /// <summary>
    /// Matches attribute by full metadata name first, then short-name fallback for incomplete test compilations.
    /// </summary>
    public static bool HasAttributeNamed(ISymbol? symbol, string fullMetadataName, string shortName)
    {
        if (symbol is null)
            return false;

        var attrs = symbol.GetAttributes();
        for (int i = 0; i < attrs.Length; i++)
        {
            var attrClass = attrs[i].AttributeClass;
            if (attrClass is null)
                continue;

            if (IsAttributeType(attrClass, fullMetadataName, shortName))
                return true;
        }

        return false;
    }

    public static bool IsAttributeType(INamedTypeSymbol attrClass, string fullMetadataName, string shortName)
    {
        // Prefer full metadata name when available.
        var metadataName = attrClass.ToDisplayString(SymbolDisplayFormat.FullyQualifiedFormat);
        // FullyQualifiedFormat uses "global::XEngine.HotPathAttribute"
        if (metadataName == "global::" + fullMetadataName || metadataName == fullMetadataName)
            return true;

        // Also accept namespace-qualified without global::
        if (attrClass.ContainingNamespace is { IsGlobalNamespace: false } ns)
        {
            var constructed = ns.ToDisplayString() + "." + attrClass.Name;
            if (constructed == fullMetadataName)
                return true;
        }

        // Short-name fallback (incomplete compilations / missing namespace resolution).
        if (attrClass.Name == shortName || attrClass.Name == shortName + "Attribute")
            return true;

        return false;
    }

    private static bool HasHotPathAttribute(SyntaxList<AttributeListSyntax> lists, SyntaxNodeAnalysisContext ctx)
    {
        for (int i = 0; i < lists.Count; i++)
        {
            var attrs = lists[i].Attributes;
            for (int j = 0; j < attrs.Count; j++)
            {
                var attr = attrs[j];
                var sym = ctx.SemanticModel.GetSymbolInfo(attr).Symbol;
                if (sym is IMethodSymbol ctor && ctor.ContainingType is { } containing)
                {
                    if (IsAttributeType(containing, HotPathAttributeMetadataName, "HotPath"))
                        return true;
                }
                else
                {
                    // Fallback for incomplete compilations in tests: match by simple name.
                    var name = attr.Name.ToString();
                    if (name == "HotPath" || name == "HotPathAttribute" ||
                        name.EndsWith(".HotPath", System.StringComparison.Ordinal) ||
                        name.EndsWith(".HotPathAttribute", System.StringComparison.Ordinal))
                        return true;
                }
            }
        }

        return false;
    }
}
