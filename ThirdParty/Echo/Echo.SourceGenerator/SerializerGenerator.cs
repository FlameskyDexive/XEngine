// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

// Echo serializer source generator.
//
// Emits an ISerializable implementation for [GenerateSerializer] types whose output is
// STRUCTURALLY IDENTICAL to what the reflection fallback (AnyObjectFormat + ReflectionUtils)
// produces, so generated and reflection-serialized data are interchangeable in both directions:
//
//   - Fields are collected over the FULL inheritance chain, most-derived first, with the same
//     shadowed-name qualification rule as ReflectionUtils.GetSerializableFields.
//   - Primitives / string / byte[] / char / enum / Guid / DateTime / TimeSpan are inlined with
//     the exact tag layout the corresponding formatter (or Serializer.SerializeFast) writes.
//   - Everything else goes through Serializer.Serialize/Deserialize with the declared field type,
//     which is literally what the reflection path does per field - identical output by construction
//     (including $id reference tracking, $type envelopes and collection wrapping).
//   - Deserialization is tag-type guarded: an unexpected tag (legacy $t/$v wrappers, widened
//     numeric types, ...) falls back to the full Serializer pipeline instead of failing.
//   - Each field block is wrapped in try/catch like the reflection path, so a single bad field
//     logs and continues instead of aborting the whole object.
//
// Fields that the generated code cannot access directly (private in a base class, readonly on
// write, compiler-generated backing fields) are reached through [UnsafeAccessor] when available
// (net8.0+), or a cached FieldInfo otherwise.

using System.Collections.Generic;
using System.Linq;
using System.Text;

using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Text;

namespace XEngine.Echo.SourceGenerator;

[Generator]
public class SerializerGenerator : IIncrementalGenerator
{
    // Fully-qualified display with tuples expanded to ValueTuple<...> so emitted type references are
    // valid in typeof()/cast/'is' positions ("(int, string)" is not a legal type pattern).
    private static readonly SymbolDisplayFormat EmitTypeFormat =
        SymbolDisplayFormat.FullyQualifiedFormat.AddMiscellaneousOptions(SymbolDisplayMiscellaneousOptions.ExpandValueTuple);

    private const string EchoNs = "global::XEngine.Echo";
    private const string EchoObj = EchoNs + ".EchoObject";
    private const string EchoTypeName = EchoNs + ".EchoType";
    private const string CtxType = EchoNs + ".SerializationContext";
    private const string SerializerType = EchoNs + ".Serializer";

    private static readonly DiagnosticDescriptor NotPartialDescriptor = new(
        id: "ECHO001",
        title: "Type with [GenerateSerializer] must be partial",
        messageFormat: "Type '{0}' has [GenerateSerializer] but is not declared 'partial'; no serializer is generated",
        category: "Echo.SourceGenerator",
        defaultSeverity: DiagnosticSeverity.Error,
        isEnabledByDefault: true);

    private static readonly DiagnosticDescriptor ConditionNotFoundDescriptor = new(
        id: "ECHO002",
        title: "SerializeIf condition member not found",
        messageFormat: "SerializeIf condition member '{0}' on field '{1}' of type '{2}' was not found or does not return bool; the field will always be serialized",
        category: "Echo.SourceGenerator",
        defaultSeverity: DiagnosticSeverity.Warning,
        isEnabledByDefault: true);

    public void Initialize(IncrementalGeneratorInitializationContext context)
    {
        // Find all types with the [GenerateSerializer] attribute. The attribute itself lives in the
        // Echo runtime assembly (XEngine.Echo.GenerateSerializerAttribute).
        var typesToGenerate = context.SyntaxProvider
            .ForAttributeWithMetadataName(
                "XEngine.Echo.GenerateSerializerAttribute",
                predicate: static (node, _) => node is TypeDeclarationSyntax,
                transform: static (ctx, _) => GetTypeToGenerate(ctx))
            .Where(static type => type is not null);

        context.RegisterSourceOutput(typesToGenerate, static (spc, typeInfo) =>
        {
            if (typeInfo is null) return;

            foreach (var diag in typeInfo.Diagnostics)
                spc.ReportDiagnostic(Diagnostic.Create(diag.Descriptor, Location.None, diag.Args));

            if (typeInfo.SkipGeneration) return;

            var source = GenerateSerializerSource(typeInfo);
            spc.AddSource($"{typeInfo.HintName}.g.cs", SourceText.From(source, Encoding.UTF8));
        });
    }

    #region Model

    private enum FieldTypeCategory
    {
        Fallback,
        Byte, SByte, Short, UShort, Int, UInt, Long, ULong,
        Float, Double, Decimal,
        Bool, Char,
        String, ByteArray,
        Enum,       // long-backed (everything except ulong)
        EnumULong,  // ulong-backed
        DateTime, Guid, TimeSpan,
    }

    private enum FieldAccess
    {
        Direct,         // this.{name}
        UnsafeAccessor, // ref-returning [UnsafeAccessor] method (read+write)
        FieldInfo,      // cached FieldInfo GetValue/SetValue (last resort)
    }

    private sealed record DiagnosticInfo(DiagnosticDescriptor Descriptor, object[] Args);

    private sealed record FieldToSerialize(
        string Name,               // CLR field name
        string SerializedName,     // compound key (shadow-qualified when colliding)
        string TypeName,           // fully-qualified (global::) field type
        bool IsValueType,
        bool IsNullableValueType,
        bool HasIgnoreOnNull,
        string? SerializeIfExpr,   // C# expression evaluating the condition, or null
        List<string> FormerlySerializedAs,
        FieldTypeCategory Category,
        FieldAccess Access,
        bool WriteNeedsAccessor,   // readonly field: reads direct, writes via accessor
        bool NeedsBaseCast,        // base field hidden by a nearer member: direct access via base cast
        string DeclaringTypeName,  // fully-qualified (global::) declaring type (for accessors)
        int AccessorIndex);

    private sealed record TypeToGenerate(
        string HintName,
        string? Namespace,
        List<(string Keyword, string Name)> TypeChain, // containing types outermost-first, target last
        bool NeedsNewModifier,
        bool IsFixedStructure,
        bool HasUnsafeAccessor,
        bool SkipGeneration,
        List<FieldToSerialize> Fields,
        List<DiagnosticInfo> Diagnostics);

    #endregion

    #region Transform

    private static TypeToGenerate? GetTypeToGenerate(GeneratorAttributeSyntaxContext context)
    {
        if (context.TargetSymbol is not INamedTypeSymbol typeSymbol) return null;
        if (context.TargetNode is not TypeDeclarationSyntax typeDeclaration) return null;

        var compilation = context.SemanticModel.Compilation;
        var diagnostics = new List<DiagnosticInfo>();

        string displayName = typeSymbol.ToDisplayString(SymbolDisplayFormat.MinimallyQualifiedFormat);

        // The generated members live in a partial declaration - the target (and any containing types)
        // must be partial.
        bool isPartial = typeDeclaration.Modifiers.Any(m => m.IsKind(SyntaxKind.PartialKeyword));
        if (!isPartial)
        {
            diagnostics.Add(new DiagnosticInfo(NotPartialDescriptor, new object[] { displayName }));
            return new TypeToGenerate("invalid", null, new(), false, false, false, SkipGeneration: true, new(), diagnostics);
        }

        var fixedStructureAttr = compilation.GetTypeByMetadataName("XEngine.Echo.FixedEchoStructureAttribute");
        bool isFixedStructure = HasAttribute(typeSymbol, fixedStructureAttr);

        bool hasUnsafeAccessor = compilation.GetTypeByMetadataName("System.Runtime.CompilerServices.UnsafeAccessorAttribute") != null;

        var fields = GetSerializableFields(typeSymbol, compilation, hasUnsafeAccessor, diagnostics);

        // Chain of containing types (for nested targets), outermost first; target last.
        var typeChain = new List<(string Keyword, string Name)>();
        var chainWalk = new List<INamedTypeSymbol>();
        for (INamedTypeSymbol? t = typeSymbol; t != null; t = t.ContainingType)
            chainWalk.Add(t);
        chainWalk.Reverse();
        foreach (var t in chainWalk)
        {
            string keyword = t.TypeKind == TypeKind.Struct
                ? (t.IsRecord ? "record struct" : "struct")
                : (t.IsRecord ? "record" : "class");
            string name = t.Name;
            if (t.TypeParameters.Length > 0)
                name += "<" + string.Join(", ", t.TypeParameters.Select(p => p.Name)) + ">";
            typeChain.Add((keyword, name));
        }

        // If any base type already exposes Serialize/Deserialize (a manual or generated ISerializable
        // implementation), the generated members hide them - emit 'new' to be explicit.
        bool needsNew = false;
        var iserializable = compilation.GetTypeByMetadataName("XEngine.Echo.ISerializable");
        for (var b = typeSymbol.BaseType; b != null && b.SpecialType != SpecialType.System_Object; b = b.BaseType)
        {
            if (iserializable != null && b.AllInterfaces.Any(i => SymbolEqualityComparer.Default.Equals(i, iserializable)))
            {
                needsNew = true;
                break;
            }
        }

        string ns = typeSymbol.ContainingNamespace is { IsGlobalNamespace: false } cns
            ? cns.ToDisplayString()
            : "";

        string hintName = (string.IsNullOrEmpty(ns) ? "" : ns + ".")
            + string.Join(".", chainWalk.Select(t => t.TypeParameters.Length > 0 ? $"{t.Name}_{t.TypeParameters.Length}" : t.Name));

        return new TypeToGenerate(
            HintName: hintName,
            Namespace: string.IsNullOrEmpty(ns) ? null : ns,
            TypeChain: typeChain,
            NeedsNewModifier: needsNew,
            IsFixedStructure: isFixedStructure,
            HasUnsafeAccessor: hasUnsafeAccessor,
            SkipGeneration: false,
            Fields: fields,
            Diagnostics: diagnostics);
    }

    /// <summary>
    /// Collects serializable fields over the full inheritance chain with EXACTLY the same rules and
    /// ordering as ReflectionUtils.GetSerializableFields: most-derived type first, public or
    /// [SerializeField] instance fields, minus [SerializeIgnore]/[NonSerialized]; a shadowed name is
    /// qualified with its declaring type.
    /// </summary>
    private static List<FieldToSerialize> GetSerializableFields(
        INamedTypeSymbol typeSymbol,
        Compilation compilation,
        bool hasUnsafeAccessor,
        List<DiagnosticInfo> diagnostics)
    {
        var serializeIgnoreAttr = compilation.GetTypeByMetadataName("XEngine.Echo.SerializeIgnoreAttribute");
        var nonSerializedAttr = compilation.GetTypeByMetadataName("System.NonSerializedAttribute");
        var serializeFieldAttr = compilation.GetTypeByMetadataName("XEngine.Echo.SerializeFieldAttribute");
        var ignoreOnNullAttr = compilation.GetTypeByMetadataName("XEngine.Echo.IgnoreOnNullAttribute");
        var serializeIfAttr = compilation.GetTypeByMetadataName("XEngine.Echo.SerializeIfAttribute");
        var formerlySerializedAsAttr = compilation.GetTypeByMetadataName("XEngine.Echo.FormerlySerializedAsAttribute");

        var fields = new List<FieldToSerialize>();
        var seenNames = new HashSet<string>();
        int accessorIndex = 0;

        for (INamedTypeSymbol? currentType = typeSymbol;
             currentType != null && currentType.SpecialType != SpecialType.System_Object;
             currentType = currentType.BaseType)
        {
            foreach (var member in currentType.GetMembers())
            {
                if (member is not IFieldSymbol field) continue;
                if (field.IsConst || field.IsStatic) continue;
                // Reflection's GetFields never returns these
                if (field.IsFixedSizeBuffer) continue;

                bool isPublic = field.DeclaredAccessibility == Accessibility.Public;
                bool hasSerializeField = HasAttribute(field, serializeFieldAttr);
                if (!isPublic && !hasSerializeField) continue;

                if (HasAttribute(field, serializeIgnoreAttr) || HasAttribute(field, nonSerializedAttr)) continue;

                string serializedName = seenNames.Add(field.Name)
                    ? field.Name
                    : $"{field.Name}@{GetReflectionFullName(field.ContainingType)}";

                bool hasIgnoreOnNull = HasAttribute(field, ignoreOnNullAttr);

                string? serializeIfExpr = null;
                var serializeIfData = field.GetAttributes().FirstOrDefault(a => IsAttribute(a, serializeIfAttr));
                if (serializeIfData != null &&
                    serializeIfData.ConstructorArguments.Length > 0 &&
                    serializeIfData.ConstructorArguments[0].Value is string conditionName &&
                    !string.IsNullOrEmpty(conditionName))
                {
                    serializeIfExpr = ResolveConditionExpression(typeSymbol, conditionName);
                    if (serializeIfExpr == null)
                        diagnostics.Add(new DiagnosticInfo(ConditionNotFoundDescriptor,
                            new object[] { conditionName, field.Name, typeSymbol.Name }));
                }

                var formerNames = field.GetAttributes()
                    .Where(a => IsAttribute(a, formerlySerializedAsAttr))
                    .Select(a => a.ConstructorArguments.FirstOrDefault().Value as string)
                    .Where(s => !string.IsNullOrEmpty(s))
                    .Cast<string>()
                    .ToList();

                var category = ClassifyFieldType(field.Type);

                // A base field whose name is hidden by a nearer member (field, property, method - any
                // hiding member redirects 'this.name') must be accessed through a base-typed receiver.
                bool hidden = false;
                if (!SymbolEqualityComparer.Default.Equals(currentType, typeSymbol))
                {
                    for (INamedTypeSymbol? t = typeSymbol;
                         t != null && !SymbolEqualityComparer.Default.Equals(t, currentType);
                         t = t.BaseType)
                    {
                        if (t.GetMembers(field.Name).Length > 0) { hidden = true; break; }
                    }
                }

                // Accessibility: generated code sits inside the annotated type, so it sees its own
                // privates but not a base type's. A hidden field additionally needs to be accessible
                // through a base-typed receiver (rules out protected: CS1540).
                bool accessible = !field.IsImplicitlyDeclared &&
                                  (hidden
                                      ? compilation.IsSymbolAccessibleWithin(field, typeSymbol, currentType)
                                      : compilation.IsSymbolAccessibleWithin(field, typeSymbol));

                // UnsafeAccessor cannot target fields on generic declaring types or of type-parameter
                // types on all supported TFMs; use FieldInfo for those.
                bool accessorViable = hasUnsafeAccessor &&
                                      !currentType.IsGenericType &&
                                      !ContainsTypeParameter(field.Type);

                FieldAccess access;
                bool writeNeedsAccessor = false;
                if (accessible)
                {
                    access = FieldAccess.Direct;
                    if (field.IsReadOnly)
                    {
                        // Readonly instance fields still roundtrip through reflection (SetValue), so
                        // the generated writer needs an accessor for the write side only.
                        writeNeedsAccessor = true;
                        if (!accessorViable)
                            access = FieldAccess.FieldInfo;
                    }
                }
                else
                {
                    access = accessorViable ? FieldAccess.UnsafeAccessor : FieldAccess.FieldInfo;
                }

                // FieldInfo access always funnels through the object-typed Serializer path.
                if (access == FieldAccess.FieldInfo)
                    category = FieldTypeCategory.Fallback;

                var fieldTypeName = field.Type.ToDisplayString(EmitTypeFormat);

                fields.Add(new FieldToSerialize(
                    Name: field.Name,
                    SerializedName: serializedName,
                    TypeName: fieldTypeName,
                    IsValueType: field.Type.IsValueType,
                    IsNullableValueType: field.Type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T },
                    HasIgnoreOnNull: hasIgnoreOnNull,
                    SerializeIfExpr: serializeIfExpr,
                    FormerlySerializedAs: formerNames,
                    Category: category,
                    Access: access,
                    WriteNeedsAccessor: writeNeedsAccessor,
                    NeedsBaseCast: hidden,
                    DeclaringTypeName: currentType.ToDisplayString(EmitTypeFormat),
                    AccessorIndex: (access != FieldAccess.Direct || writeNeedsAccessor) ? accessorIndex++ : -1));
            }
        }

        return fields;
    }

    /// <summary>
    /// Resolves a [SerializeIf] member name to a C# boolean expression, mirroring the reflection
    /// evaluator's lookup order (property, then field, then parameterless method), restricted to
    /// members the generated code can actually access. Returns null when unresolvable.
    /// </summary>
    private static string? ResolveConditionExpression(INamedTypeSymbol typeSymbol, string name)
    {
        for (INamedTypeSymbol? t = typeSymbol; t != null && t.SpecialType != SpecialType.System_Object; t = t.BaseType)
        {
            bool inSelf = SymbolEqualityComparer.Default.Equals(t, typeSymbol);
            foreach (var member in t.GetMembers(name))
            {
                if (member.IsStatic) continue;
                // Base-private members are invisible to both reflection's Type.GetProperty walk and
                // the generated code.
                if (!inSelf && member.DeclaredAccessibility == Accessibility.Private) continue;

                switch (member)
                {
                    case IPropertySymbol { GetMethod: not null } p when p.Type.SpecialType == SpecialType.System_Boolean:
                        return $"this.{name}";
                    case IFieldSymbol f when f.Type.SpecialType == SpecialType.System_Boolean:
                        return $"this.{name}";
                    case IMethodSymbol { Parameters.Length: 0 } m when m.ReturnType.SpecialType == SpecialType.System_Boolean:
                        return $"this.{name}()";
                }
            }
        }
        return null;
    }

    private static bool HasAttribute(ISymbol symbol, INamedTypeSymbol? attributeType)
    {
        if (attributeType == null) return false;
        foreach (var a in symbol.GetAttributes())
            if (IsAttribute(a, attributeType))
                return true;
        return false;
    }

    // Matches like reflection's GetCustomAttribute<T>: the attribute class or any subclass of it.
    private static bool IsAttribute(AttributeData a, INamedTypeSymbol? attributeType)
    {
        if (attributeType == null) return false;
        for (var t = a.AttributeClass; t != null; t = t.BaseType)
            if (SymbolEqualityComparer.Default.Equals(t, attributeType))
                return true;
        return false;
    }

    private static bool ContainsTypeParameter(ITypeSymbol type) => type switch
    {
        ITypeParameterSymbol => true,
        IArrayTypeSymbol arr => ContainsTypeParameter(arr.ElementType),
        INamedTypeSymbol named => named.TypeArguments.Any(ContainsTypeParameter),
        _ => false,
    };

    /// <summary>Reflection-style full name (namespace with '.', nested types with '+').</summary>
    private static string GetReflectionFullName(INamedTypeSymbol type)
    {
        var parts = new List<string>();
        for (INamedTypeSymbol? t = type; t != null; t = t.ContainingType)
            parts.Add(t.MetadataName);
        parts.Reverse();
        string name = string.Join("+", parts);
        if (type.ContainingNamespace is { IsGlobalNamespace: false } ns)
            name = ns.ToDisplayString() + "." + name;
        return name;
    }

    #endregion

    #region Type Classification

    private static FieldTypeCategory ClassifyFieldType(ITypeSymbol type)
    {
        // Nullable<T> is handled by NullableFormat - full pipeline
        if (type is INamedTypeSymbol { OriginalDefinition.SpecialType: SpecialType.System_Nullable_T })
            return FieldTypeCategory.Fallback;

        switch (type.SpecialType)
        {
            case SpecialType.System_Byte: return FieldTypeCategory.Byte;
            case SpecialType.System_SByte: return FieldTypeCategory.SByte;
            case SpecialType.System_Int16: return FieldTypeCategory.Short;
            case SpecialType.System_UInt16: return FieldTypeCategory.UShort;
            case SpecialType.System_Int32: return FieldTypeCategory.Int;
            case SpecialType.System_UInt32: return FieldTypeCategory.UInt;
            case SpecialType.System_Int64: return FieldTypeCategory.Long;
            case SpecialType.System_UInt64: return FieldTypeCategory.ULong;
            case SpecialType.System_Single: return FieldTypeCategory.Float;
            case SpecialType.System_Double: return FieldTypeCategory.Double;
            case SpecialType.System_Decimal: return FieldTypeCategory.Decimal;
            case SpecialType.System_Boolean: return FieldTypeCategory.Bool;
            case SpecialType.System_Char: return FieldTypeCategory.Char;
            case SpecialType.System_String: return FieldTypeCategory.String;
        }

        // byte[] - matches Serializer.SerializeFast's byte[] case
        if (type is IArrayTypeSymbol { Rank: 1 } byteArr && byteArr.ElementType.SpecialType == SpecialType.System_Byte)
            return FieldTypeCategory.ByteArray;

        if (type.TypeKind == TypeKind.Enum)
        {
            var underlying = (type as INamedTypeSymbol)?.EnumUnderlyingType;
            return underlying?.SpecialType == SpecialType.System_UInt64
                ? FieldTypeCategory.EnumULong
                : FieldTypeCategory.Enum;
        }

        if (type.ContainingNamespace?.ToDisplayString() == "System")
        {
            switch (type.Name)
            {
                case "DateTime": return FieldTypeCategory.DateTime;
                case "Guid": return FieldTypeCategory.Guid;
                case "TimeSpan": return FieldTypeCategory.TimeSpan;
            }
        }

        // Everything else (collections, dictionaries, nested objects, Nullable, DateTimeOffset, Uri,
        // Version, tuples, ...) goes through the Serializer pipeline with the declared field type -
        // structurally identical to the reflection path by construction.
        return FieldTypeCategory.Fallback;
    }

    #endregion

    #region Code Generation

    private static string GenerateSerializerSource(TypeToGenerate typeInfo)
    {
        var sb = new StringBuilder();

        sb.AppendLine("// <auto-generated/>");
        sb.AppendLine("#nullable enable");
        sb.AppendLine("#pragma warning disable CS0612, CS0618 // serialized fields may be [Obsolete]");
        sb.AppendLine("#pragma warning disable CS8600, CS8601, CS8602, CS8603, CS8604 // tag lookups are guarded at runtime");
        sb.AppendLine();

        string indent = "";
        if (typeInfo.Namespace != null)
        {
            sb.AppendLine($"namespace {typeInfo.Namespace}");
            sb.AppendLine("{");
            indent = "    ";
        }

        // Open containing-type chain; the last entry is the target type itself.
        for (int i = 0; i < typeInfo.TypeChain.Count; i++)
        {
            var (keyword, name) = typeInfo.TypeChain[i];
            bool isTarget = i == typeInfo.TypeChain.Count - 1;
            sb.AppendLine($"{indent}partial {keyword} {name}{(isTarget ? $" : {EchoNs}.ISerializable" : "")}");
            sb.AppendLine($"{indent}{{");
            indent += "    ";
        }

        GenerateAccessors(sb, typeInfo, indent);

        string newModifier = typeInfo.NeedsNewModifier ? "new " : "";

        if (typeInfo.IsFixedStructure)
            GenerateFixedStructureSerializeMethod(sb, typeInfo, indent, newModifier);
        else
            GenerateSerializeMethod(sb, typeInfo, indent, newModifier);

        sb.AppendLine();

        if (typeInfo.IsFixedStructure)
            GenerateFixedStructureDeserializeMethod(sb, typeInfo, indent, newModifier);
        else
            GenerateDeserializeMethod(sb, typeInfo, indent, newModifier);

        for (int i = 0; i < typeInfo.TypeChain.Count; i++)
        {
            indent = indent.Substring(4);
            sb.AppendLine($"{indent}}}");
        }

        if (typeInfo.Namespace != null)
            sb.AppendLine("}");

        return sb.ToString();
    }

    private static void GenerateAccessors(StringBuilder sb, TypeToGenerate typeInfo, string indent)
    {
        foreach (var field in typeInfo.Fields)
        {
            if (field.AccessorIndex < 0) continue;

            if (field.Access == FieldAccess.FieldInfo)
            {
                sb.AppendLine($"{indent}private static readonly global::System.Reflection.FieldInfo __echoFi_{field.AccessorIndex} =");
                sb.AppendLine($"{indent}    typeof({field.DeclaringTypeName}).GetField(\"{field.Name}\",");
                sb.AppendLine($"{indent}        global::System.Reflection.BindingFlags.Instance | global::System.Reflection.BindingFlags.Public |");
                sb.AppendLine($"{indent}        global::System.Reflection.BindingFlags.NonPublic | global::System.Reflection.BindingFlags.DeclaredOnly)!;");
            }
            else // UnsafeAccessor (either full access, or write-side for a readonly field)
            {
                sb.AppendLine($"{indent}[global::System.Runtime.CompilerServices.UnsafeAccessor(global::System.Runtime.CompilerServices.UnsafeAccessorKind.Field, Name = \"{field.Name}\")]");
                sb.AppendLine($"{indent}private static extern ref {field.TypeName} __echoField_{field.AccessorIndex}({field.DeclaringTypeName} @this);");
            }
            sb.AppendLine();
        }
    }

    // A hidden base field must be read through a base-typed receiver so the nearer same-named member
    // doesn't hijack the access.
    private static string DirectReceiver(FieldToSerialize f)
        => f.NeedsBaseCast ? $"(({f.DeclaringTypeName})this)" : "this";

    /// <summary>Expression that reads the field's current value.</summary>
    private static string ReadExpr(FieldToSerialize f) => f.Access switch
    {
        FieldAccess.Direct => $"{DirectReceiver(f)}.{f.Name}",
        FieldAccess.UnsafeAccessor => $"__echoField_{f.AccessorIndex}(this)",
        _ => $"__echoFi_{f.AccessorIndex}.GetValue(this)", // object-typed; Fallback category only
    };

    /// <summary>Assignable lvalue for the field (not used for FieldInfo access).</summary>
    private static string WriteLValue(FieldToSerialize f)
    {
        if (f.Access == FieldAccess.UnsafeAccessor || (f.Access == FieldAccess.Direct && f.WriteNeedsAccessor))
            return $"__echoField_{f.AccessorIndex}(this)";
        return $"{DirectReceiver(f)}.{f.Name}";
    }

    private static void GenerateSerializeMethod(StringBuilder sb, TypeToGenerate typeInfo, string indent, string newModifier)
    {
        sb.AppendLine($"{indent}public {newModifier}void Serialize(ref {EchoObj} compound, {CtxType} ctx)");
        sb.AppendLine($"{indent}{{");

        foreach (var field in typeInfo.Fields)
        {
            string ind = indent + "    ";
            sb.AppendLine($"{ind}try");
            sb.AppendLine($"{ind}{{");
            string bodyInd = ind + "    ";

            if (field.SerializeIfExpr != null)
            {
                sb.AppendLine($"{bodyInd}if ({field.SerializeIfExpr})");
                sb.AppendLine($"{bodyInd}{{");
                EmitSerializeField(sb, field, bodyInd + "    ");
                sb.AppendLine($"{bodyInd}}}");
            }
            else
            {
                EmitSerializeField(sb, field, bodyInd);
            }

            sb.AppendLine($"{ind}}}");
            sb.AppendLine($"{ind}catch (global::System.Exception __ex)");
            sb.AppendLine($"{ind}{{");
            sb.AppendLine($"{ind}    {SerializerType}.Logger.Error(\"Failed to serialize field {field.Name}\", __ex);");
            sb.AppendLine($"{ind}}}");
        }

        sb.AppendLine($"{indent}}}");
    }

    private static void EmitSerializeField(StringBuilder sb, FieldToSerialize field, string indent)
    {
        string key = field.SerializedName;
        string read = ReadExpr(field);

        switch (field.Category)
        {
            // Numeric/bool: exact EchoObject ctor overload => same tag as Serializer.SerializeFast
            case FieldTypeCategory.Byte:
            case FieldTypeCategory.SByte:
            case FieldTypeCategory.Short:
            case FieldTypeCategory.UShort:
            case FieldTypeCategory.Int:
            case FieldTypeCategory.UInt:
            case FieldTypeCategory.Long:
            case FieldTypeCategory.ULong:
            case FieldTypeCategory.Float:
            case FieldTypeCategory.Double:
            case FieldTypeCategory.Decimal:
            case FieldTypeCategory.Bool:
                sb.AppendLine($"{indent}compound.Add(\"{key}\", new {EchoObj}({read}));");
                break;

            // char stores as ushort (SerializeFast: TypeCode.Char => new EchoObject((ushort)(char)value))
            case FieldTypeCategory.Char:
                sb.AppendLine($"{indent}compound.Add(\"{key}\", new {EchoObj}((ushort){read}));");
                break;

            // Enum stores at full underlying width (EnumFormat.SerializeEnum)
            case FieldTypeCategory.Enum:
                sb.AppendLine($"{indent}compound.Add(\"{key}\", new {EchoObj}((long){read}));");
                break;
            case FieldTypeCategory.EnumULong:
                sb.AppendLine($"{indent}compound.Add(\"{key}\", new {EchoObj}((ulong){read}));");
                break;

            case FieldTypeCategory.String:
            case FieldTypeCategory.ByteArray:
                {
                    sb.AppendLine($"{indent}var __v = {read};");
                    if (field.HasIgnoreOnNull)
                    {
                        sb.AppendLine($"{indent}if (__v != null)");
                        sb.AppendLine($"{indent}    compound.Add(\"{key}\", new {EchoObj}(__v));");
                    }
                    else
                    {
                        sb.AppendLine($"{indent}compound.Add(\"{key}\", __v == null ? new {EchoObj}({EchoTypeName}.Null, null) : new {EchoObj}(__v));");
                    }
                    break;
                }

            // Guid/DateTime/TimeSpan: whether they get a type envelope depends on the context's
            // TypeMode at runtime (Serializer's fast path doesn't cover them), so writing must go
            // through the pipeline; only their deserialize side has an inline fast path.
            case FieldTypeCategory.Guid:
            case FieldTypeCategory.DateTime:
            case FieldTypeCategory.TimeSpan:
            // Everything else: same call the reflection path makes per field
            case FieldTypeCategory.Fallback:
            default:
                {
                    if (field.Access == FieldAccess.FieldInfo)
                    {
                        sb.AppendLine($"{indent}object? __v = {read};");
                    }
                    else if (field.IsValueType && !field.IsNullableValueType)
                    {
                        sb.AppendLine($"{indent}compound.Add(\"{key}\", {SerializerType}.Serialize(typeof({field.TypeName}), {read}, ctx));");
                        break;
                    }
                    else
                    {
                        sb.AppendLine($"{indent}var __v = {read};");
                    }

                    sb.AppendLine($"{indent}if (__v == null)");
                    sb.AppendLine($"{indent}{{");
                    if (!field.HasIgnoreOnNull)
                        sb.AppendLine($"{indent}    compound.Add(\"{key}\", new {EchoObj}({EchoTypeName}.Null, null));");
                    sb.AppendLine($"{indent}}}");
                    sb.AppendLine($"{indent}else");
                    sb.AppendLine($"{indent}{{");
                    sb.AppendLine($"{indent}    compound.Add(\"{key}\", {SerializerType}.Serialize(typeof({field.TypeName}), __v, ctx));");
                    sb.AppendLine($"{indent}}}");
                    break;
                }
        }
    }

    private static void GenerateDeserializeMethod(StringBuilder sb, TypeToGenerate typeInfo, string indent, string newModifier)
    {
        sb.AppendLine($"{indent}public {newModifier}void Deserialize({EchoObj} value, {CtxType} ctx)");
        sb.AppendLine($"{indent}{{");

        int i = 0;
        foreach (var field in typeInfo.Fields)
        {
            string ind = indent + "    ";
            string tagVar = $"__t{i++}";

            // Exact name first, then FormerlySerializedAs fallbacks (reflection does the same; its
            // case-insensitive last resort only matters for hand-edited data and is intentionally
            // not replicated here).
            var lookup = new StringBuilder($"value.TryGet(\"{field.SerializedName}\", out var {tagVar})");
            foreach (var oldName in field.FormerlySerializedAs)
                lookup.Append($" || value.TryGet(\"{oldName}\", out {tagVar})");

            sb.AppendLine($"{ind}if ({lookup})");
            sb.AppendLine($"{ind}{{");
            sb.AppendLine($"{ind}    try");
            sb.AppendLine($"{ind}    {{");
            EmitDeserializeField(sb, field, tagVar, ind + "        ");
            sb.AppendLine($"{ind}    }}");
            sb.AppendLine($"{ind}    catch (global::System.Exception __ex)");
            sb.AppendLine($"{ind}    {{");
            sb.AppendLine($"{ind}        {SerializerType}.Logger.Error(\"Failed to deserialize field {field.Name}\", __ex);");
            sb.AppendLine($"{ind}    }}");
            sb.AppendLine($"{ind}}}");
        }

        sb.AppendLine($"{indent}}}");
    }

    private static void EmitDeserializeField(StringBuilder sb, FieldToSerialize field, string tagVar, string indent)
    {
        string lvalue = WriteLValue(field);

        switch (field.Category)
        {
            case FieldTypeCategory.Byte: EmitNumericRead(sb, field, tagVar, indent, $"{tagVar}.ByteValue"); break;
            case FieldTypeCategory.SByte: EmitNumericRead(sb, field, tagVar, indent, $"{tagVar}.sByteValue"); break;
            case FieldTypeCategory.Short: EmitNumericRead(sb, field, tagVar, indent, $"{tagVar}.ShortValue"); break;
            case FieldTypeCategory.UShort: EmitNumericRead(sb, field, tagVar, indent, $"{tagVar}.UShortValue"); break;
            case FieldTypeCategory.Int: EmitNumericRead(sb, field, tagVar, indent, $"{tagVar}.IntValue"); break;
            case FieldTypeCategory.UInt: EmitNumericRead(sb, field, tagVar, indent, $"{tagVar}.UIntValue"); break;
            case FieldTypeCategory.Long: EmitNumericRead(sb, field, tagVar, indent, $"{tagVar}.LongValue"); break;
            case FieldTypeCategory.ULong: EmitNumericRead(sb, field, tagVar, indent, $"{tagVar}.ULongValue"); break;
            case FieldTypeCategory.Float: EmitNumericRead(sb, field, tagVar, indent, $"{tagVar}.FloatValue"); break;
            case FieldTypeCategory.Double: EmitNumericRead(sb, field, tagVar, indent, $"{tagVar}.DoubleValue"); break;
            case FieldTypeCategory.Decimal: EmitNumericRead(sb, field, tagVar, indent, $"{tagVar}.DecimalValue"); break;
            case FieldTypeCategory.Char: EmitNumericRead(sb, field, tagVar, indent, $"(char){tagVar}.UShortValue"); break;
            case FieldTypeCategory.Enum: EmitNumericRead(sb, field, tagVar, indent, $"({field.TypeName}){tagVar}.LongValue"); break;
            case FieldTypeCategory.EnumULong: EmitNumericRead(sb, field, tagVar, indent, $"({field.TypeName}){tagVar}.ULongValue"); break;

            case FieldTypeCategory.Bool:
                sb.AppendLine($"{indent}if ({tagVar}.TagType == {EchoTypeName}.Bool)");
                sb.AppendLine($"{indent}    {lvalue} = {tagVar}.BoolValue;");
                EmitDefaultAndFallback(sb, field, tagVar, indent);
                break;

            case FieldTypeCategory.String:
                sb.AppendLine($"{indent}if ({tagVar}.TagType == {EchoTypeName}.String)");
                sb.AppendLine($"{indent}    {lvalue} = {tagVar}.StringValue;");
                EmitDefaultAndFallback(sb, field, tagVar, indent);
                break;

            case FieldTypeCategory.ByteArray:
                sb.AppendLine($"{indent}if ({tagVar}.TagType == {EchoTypeName}.ByteArray)");
                sb.AppendLine($"{indent}    {lvalue} = {tagVar}.ByteArrayValue;");
                EmitDefaultAndFallback(sb, field, tagVar, indent);
                break;

            case FieldTypeCategory.Guid:
                sb.AppendLine($"{indent}if ({tagVar}.TagType == {EchoTypeName}.String)");
                sb.AppendLine($"{indent}    {lvalue} = global::System.Guid.Parse({tagVar}.StringValue);");
                EmitDefaultAndFallback(sb, field, tagVar, indent);
                break;

            case FieldTypeCategory.DateTime:
                sb.AppendLine($"{indent}if ({tagVar}.TagType == {EchoTypeName}.Compound && {tagVar}.TryGet(\"date\", out var __d) && __d.TagType == {EchoTypeName}.Long)");
                sb.AppendLine($"{indent}    {lvalue} = global::System.DateTime.FromBinary(__d.LongValue);");
                EmitDefaultAndFallback(sb, field, tagVar, indent);
                break;

            case FieldTypeCategory.TimeSpan:
                sb.AppendLine($"{indent}if ({tagVar}.TagType == {EchoTypeName}.Compound && {tagVar}.TryGet(\"ticks\", out var __k) && __k.TagType == {EchoTypeName}.Long)");
                sb.AppendLine($"{indent}    {lvalue} = new global::System.TimeSpan(__k.LongValue);");
                EmitDefaultAndFallback(sb, field, tagVar, indent);
                break;

            case FieldTypeCategory.Fallback:
            default:
                EmitFallbackRead(sb, field, tagVar, indent);
                break;
        }
    }

    /// <summary>
    /// Numeric family fast path: any numeric tag reads through the (Convert-based, width-tolerant)
    /// accessor; Null yields default; anything else (legacy $t/$v wrappers, ...) falls back to the
    /// full pipeline - the exact tolerance Convert.ChangeType gave the reflection path.
    /// </summary>
    private static void EmitNumericRead(StringBuilder sb, FieldToSerialize field, string tagVar, string indent, string readExpr)
    {
        string lvalue = WriteLValue(field);
        sb.AppendLine($"{indent}if ({tagVar}.TagType >= {EchoTypeName}.Byte && {tagVar}.TagType <= {EchoTypeName}.Decimal)");
        sb.AppendLine($"{indent}    {lvalue} = {readExpr};");
        EmitDefaultAndFallback(sb, field, tagVar, indent);
    }

    /// <summary>Shared tail: Null tag assigns default (SetValue(null) semantics), else full pipeline.</summary>
    private static void EmitDefaultAndFallback(StringBuilder sb, FieldToSerialize field, string tagVar, string indent)
    {
        string lvalue = WriteLValue(field);
        sb.AppendLine($"{indent}else if ({tagVar}.TagType == {EchoTypeName}.Null)");
        sb.AppendLine($"{indent}    {lvalue} = default!;");
        sb.AppendLine($"{indent}else");
        sb.AppendLine($"{indent}{{");
        EmitPipelineAssign(sb, field, tagVar, indent + "    ");
        sb.AppendLine($"{indent}}}");
    }

    private static void EmitFallbackRead(StringBuilder sb, FieldToSerialize field, string tagVar, string indent)
    {
        EmitPipelineAssign(sb, field, tagVar, indent);
    }

    /// <summary>Deserialize through Serializer.Deserialize and assign with SetValue-equivalent semantics.</summary>
    private static void EmitPipelineAssign(StringBuilder sb, FieldToSerialize field, string tagVar, string indent)
    {
        sb.AppendLine($"{indent}object? __obj = {SerializerType}.Deserialize({tagVar}, typeof({field.TypeName}), ctx);");

        if (field.Access == FieldAccess.FieldInfo)
        {
            sb.AppendLine($"{indent}__echoFi_{field.AccessorIndex}.SetValue(this, __obj);");
            return;
        }

        string lvalue = WriteLValue(field);
        if (field.IsValueType && !field.IsNullableValueType)
        {
            // Mirror FieldInfo.SetValue: null assigns default, a wrong type throws (caught above and
            // leaves the field unchanged).
            sb.AppendLine($"{indent}if (__obj is {field.TypeName} __v)");
            sb.AppendLine($"{indent}    {lvalue} = __v;");
            sb.AppendLine($"{indent}else if (__obj is null)");
            sb.AppendLine($"{indent}    {lvalue} = default;");
            sb.AppendLine($"{indent}else");
            sb.AppendLine($"{indent}    throw new global::System.InvalidCastException($\"Cannot assign {{__obj.GetType()}} to field of type {field.TypeName.Replace("global::", "")}\");");
        }
        else
        {
            // Reference types and Nullable<T>: cast (null flows through, wrong type throws => caught).
            sb.AppendLine($"{indent}{lvalue} = ({field.TypeName})__obj!;");
        }
    }

    #endregion

    #region Fixed Structure Generation

    private static void GenerateFixedStructureSerializeMethod(StringBuilder sb, TypeToGenerate typeInfo, string indent, string newModifier)
    {
        sb.AppendLine($"{indent}public {newModifier}void Serialize(ref {EchoObj} compound, {CtxType} ctx)");
        sb.AppendLine($"{indent}{{");
        sb.AppendLine($"{indent}    var __list = {EchoObj}.NewList();");

        foreach (var field in typeInfo.Fields)
        {
            string ind = indent + "    ";
            sb.AppendLine($"{ind}try");
            sb.AppendLine($"{ind}{{");
            EmitFixedSerializeElement(sb, field, ind + "    ");
            sb.AppendLine($"{ind}}}");
            sb.AppendLine($"{ind}catch (global::System.Exception __ex)");
            sb.AppendLine($"{ind}{{");
            sb.AppendLine($"{ind}    {SerializerType}.Logger.Error(\"Failed to serialize field {field.Name} in fixed structure\", __ex);");
            sb.AppendLine($"{ind}    __list.ListAdd(new {EchoObj}({EchoTypeName}.Null, null));");
            sb.AppendLine($"{ind}}}");
        }

        sb.AppendLine($"{indent}    compound = __list;");
        sb.AppendLine($"{indent}}}");
    }

    private static void EmitFixedSerializeElement(StringBuilder sb, FieldToSerialize field, string indent)
    {
        string read = ReadExpr(field);
        switch (field.Category)
        {
            case FieldTypeCategory.Byte:
            case FieldTypeCategory.SByte:
            case FieldTypeCategory.Short:
            case FieldTypeCategory.UShort:
            case FieldTypeCategory.Int:
            case FieldTypeCategory.UInt:
            case FieldTypeCategory.Long:
            case FieldTypeCategory.ULong:
            case FieldTypeCategory.Float:
            case FieldTypeCategory.Double:
            case FieldTypeCategory.Decimal:
            case FieldTypeCategory.Bool:
                sb.AppendLine($"{indent}__list.ListAdd(new {EchoObj}({read}));");
                break;
            case FieldTypeCategory.Char:
                sb.AppendLine($"{indent}__list.ListAdd(new {EchoObj}((ushort){read}));");
                break;
            case FieldTypeCategory.Enum:
                sb.AppendLine($"{indent}__list.ListAdd(new {EchoObj}((long){read}));");
                break;
            case FieldTypeCategory.EnumULong:
                sb.AppendLine($"{indent}__list.ListAdd(new {EchoObj}((ulong){read}));");
                break;
            case FieldTypeCategory.String:
            case FieldTypeCategory.ByteArray:
                sb.AppendLine($"{indent}var __v = {read};");
                sb.AppendLine($"{indent}__list.ListAdd(__v == null ? new {EchoObj}({EchoTypeName}.Null, null) : new {EchoObj}(__v));");
                break;
            case FieldTypeCategory.Guid:
            case FieldTypeCategory.DateTime:
            case FieldTypeCategory.TimeSpan:
            case FieldTypeCategory.Fallback:
            default:
                if (field.Access == FieldAccess.FieldInfo)
                    sb.AppendLine($"{indent}object? __v = {read};");
                else
                    sb.AppendLine($"{indent}object? __v = (object?){read};");
                sb.AppendLine($"{indent}__list.ListAdd({SerializerType}.Serialize(typeof({field.TypeName}), __v, ctx));");
                break;
        }
    }

    private static void GenerateFixedStructureDeserializeMethod(StringBuilder sb, TypeToGenerate typeInfo, string indent, string newModifier)
    {
        sb.AppendLine($"{indent}public {newModifier}void Deserialize({EchoObj} value, {CtxType} ctx)");
        sb.AppendLine($"{indent}{{");
        sb.AppendLine($"{indent}    if (value.TagType != {EchoTypeName}.List)");
        sb.AppendLine($"{indent}        throw new global::System.InvalidOperationException(\"Expected list for fixed structure deserialization\");");
        sb.AppendLine();
        sb.AppendLine($"{indent}    var __listValue = (global::System.Collections.Generic.List<{EchoObj}>)value.Value!;");
        sb.AppendLine();
        sb.AppendLine($"{indent}    if (__listValue.Count != {typeInfo.Fields.Count})");
        sb.AppendLine($"{indent}        throw new global::System.InvalidOperationException($\"Field count mismatch during fixed structure deserialization. Expected {typeInfo.Fields.Count} fields but got {{__listValue.Count}} values.\");");
        sb.AppendLine();

        for (int i = 0; i < typeInfo.Fields.Count; i++)
        {
            var field = typeInfo.Fields[i];
            string ind = indent + "    ";
            string tagVar = $"__t{i}";
            sb.AppendLine($"{ind}{{");
            sb.AppendLine($"{ind}    var {tagVar} = __listValue[{i}];");
            sb.AppendLine($"{ind}    try");
            sb.AppendLine($"{ind}    {{");
            EmitDeserializeField(sb, field, tagVar, ind + "        ");
            sb.AppendLine($"{ind}    }}");
            sb.AppendLine($"{ind}    catch (global::System.Exception __ex)");
            sb.AppendLine($"{ind}    {{");
            sb.AppendLine($"{ind}        {SerializerType}.Logger.Error(\"Failed to deserialize field {field.Name} in fixed structure\", __ex);");
            sb.AppendLine($"{ind}    }}");
            sb.AppendLine($"{ind}}}");
        }

        sb.AppendLine($"{indent}}}");
    }

    #endregion
}
