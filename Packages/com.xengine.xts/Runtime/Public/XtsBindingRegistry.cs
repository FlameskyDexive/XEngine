using System;
using System.Collections.Generic;
using System.Reflection;

namespace XEngine.XTS.Binding;

/// <summary>
/// The primitive kinds a binding parameter or return value may use. These map
/// one-to-one onto TS types and onto <see cref="XtsValue"/> conversions.
/// Structural/complex values cross as JSON (<see cref="XtsBindingType.Json"/>)
/// with by-value copy semantics — never as live references.
/// </summary>
public enum XtsBindingType
{
    Bool,
    Int32,
    Float64,
    BigInt,
    String,
    /// <summary>Any JSON-serializable value; crosses as a copied document.</summary>
    Json,
    /// <summary>The binding returns nothing meaningful (JS undefined).</summary>
    Undefined,
}

/// <summary>
/// One static host function exposed to TypeScript. Declared at build time by
/// the host assembly; the declaration generator turns these into `.d.ts` and
/// the JS glue module, and the capability manifest pins their stable ids.
/// </summary>
public sealed class XtsFunctionSpec
{
    /// <summary>
    /// Stable identity of the binding (e.g. "engine.time.deltaTime"). Content
    /// manifests reference this id; it must never be renamed casually.
    /// </summary>
    public required string BindingId { get; init; }

    /// <summary>Global function name the script calls (e.g. "engine_time_deltaTime").</summary>
    public required string GlobalName { get; init; }

    public IReadOnlyList<XtsBindingParameter> Parameters { get; init; } = Array.Empty<XtsBindingParameter>();

    public XtsBindingType ReturnType { get; init; } = XtsBindingType.Undefined;

    public XtsBindingHandler? Handler { get; init; }

    /// <summary>One-line description emitted into the declarations.</summary>
    public string Description { get; init; } = string.Empty;
}

public readonly struct XtsBindingParameter
{
    public XtsBindingParameter(string name, XtsBindingType type, Type? clrType = null)
    {
        Name = name;
        Type = type;
        ClrType = clrType;
    }

    public string Name { get; }
    public XtsBindingType Type { get; }
    /// <summary>
    /// CLR type carried for build-time validation (open generics, byref,
    /// pointers). Optional: specs without one skip CLR-side diagnostics.
    /// </summary>
    public Type? ClrType { get; }
}

public static class XtsBindingTypeExtensions
{
    public static XtsBindingType UndefinedType(this XtsBindingType _) => XtsBindingType.Json;
}

/// <summary>
/// Build-time registry of static host bindings. Validate() produces the
/// rejection diagnostics required before any build can ship: duplicate ids,
/// malformed names, unsupported CLR types (open generics, byref, pointers)
/// and unbound handlers all fail loudly here — never via runtime reflection.
/// </summary>
public sealed class XtsBindingRegistry
{
    private readonly Dictionary<string, XtsFunctionSpec> _specs = new(StringComparer.Ordinal);

    public IReadOnlyCollection<XtsFunctionSpec> Functions => _specs.Values;

    public void Add(XtsFunctionSpec spec) => _specs[spec.BindingId] = spec;

    public IReadOnlyList<string> Validate()
    {
        var errors = new List<string>();
        var globalNames = new Dictionary<string, string>(StringComparer.Ordinal);

        foreach (var spec in _specs.Values)
        {
            if (string.IsNullOrWhiteSpace(spec.BindingId))
                errors.Add("binding has an empty id");
            if (string.IsNullOrWhiteSpace(spec.GlobalName))
                errors.Add($"binding '{spec.BindingId}' has an empty global name");
            else if (!IsIdentifier(spec.GlobalName))
                errors.Add($"binding '{spec.BindingId}' global name '{spec.GlobalName}' is not a valid JS identifier");
            if (spec.Handler == null)
                errors.Add($"binding '{spec.BindingId}' has no handler; scripts would call into nothing");

            if (globalNames.TryGetValue(spec.GlobalName, out var other))
                errors.Add($"binding '{spec.BindingId}' global name '{spec.GlobalName}' collides with '{other}'");
            else
                globalNames[spec.GlobalName] = spec.BindingId;

            foreach (var parameter in spec.Parameters)
                ValidateType(spec.BindingId, parameter.Name, parameter.ClrType, errors);
        }

        return errors;
    }

    private static void ValidateType(string bindingId, string parameterName, Type? clrType, List<string> errors)
    {
        if (clrType == null)
            return;
        if (clrType.IsByRef)
        {
            errors.Add($"binding '{bindingId}' parameter '{parameterName}': ref/out parameters are not supported; " +
                       "return the new value instead");
            return;
        }
        if (clrType.IsPointer)
        {
            errors.Add($"binding '{bindingId}' parameter '{parameterName}': pointer types are not supported");
            return;
        }
        if (clrType.IsGenericType && !clrType.IsConstructedGenericType)
        {
            errors.Add($"binding '{bindingId}' parameter '{parameterName}': open generic type " +
                       $"{clrType.FullName ?? clrType.Name} is not supported; close it (e.g. List<int>)");
            return;
        }
        if (clrType.IsGenericParameter)
            errors.Add($"binding '{bindingId}' parameter '{parameterName}': generic parameter types are not supported");
    }

    private static bool IsIdentifier(string name)
    {
        if (name.Length == 0 || !(char.IsLetter(name[0]) || name[0] == '_'))
            return false;
        foreach (var c in name)
        {
            if (!(char.IsLetterOrDigit(c) || c == '_'))
                return false;
        }
        return true;
    }
}

/// <summary>Static TS-type mapping used by the declaration generator.</summary>
public static class XtsBindingTypeMap
{
    public static string ToTypeScript(XtsBindingType type, Type? clrType = null)
    {
        return type switch
        {
            XtsBindingType.Bool => "boolean",
            XtsBindingType.Int32 or XtsBindingType.Float64 => "number",
            XtsBindingType.BigInt => "bigint",
            XtsBindingType.String => "string",
            XtsBindingType.Json => JsonTypeScriptName(clrType),
            _ => "void",
        };
    }

    /// <summary>
    /// Closed generic collections map to TS array syntax; anything else JSON
    /// degrades to unknown with a leading underscore marker for review.
    /// </summary>
    private static string JsonTypeScriptName(Type? clrType)
    {
        if (clrType == null)
            return "unknown";
        if (clrType.IsArray)
            return ToTypeScriptElement(clrType.GetElementType()!) + "[]";
        if (clrType.IsGenericType && clrType.IsConstructedGenericType)
        {
            var definition = clrType.GetGenericTypeDefinition();
            var arguments = clrType.GetGenericArguments();
            if (definition == typeof(List<>) || definition == typeof(IList<>) ||
                definition == typeof(IReadOnlyList<>) || definition == typeof(HashSet<>))
                return ToTypeScriptElement(arguments[0]) + "[]";
            if (definition == typeof(Nullable<>))
                return ToTypeScriptElement(arguments[0]) + " | null";
        }
        if (clrType == typeof(double) || clrType == typeof(int) || clrType == typeof(long) || clrType == typeof(float))
            return "number";
        if (clrType == typeof(string))
            return "string";
        if (clrType == typeof(bool))
            return "boolean";
        return "unknown";
    }

    private static string ToTypeScriptElement(Type type)
    {
        if (type == typeof(double) || type == typeof(int) || type == typeof(long) || type == typeof(float))
            return "number";
        if (type == typeof(string))
            return "string";
        if (type == typeof(bool))
            return "boolean";
        return "unknown";
    }
}
