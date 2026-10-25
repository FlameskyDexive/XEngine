// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Collections.Concurrent;
using System.Diagnostics.CodeAnalysis;
using System.Linq.Expressions;
using System.Reflection;

namespace XEngine.Echo;

/// <summary>
/// Cached field metadata that avoids per-call attribute reflection. Also carries compiled
/// getter/setter delegates so the reflection serialization path skips FieldInfo.GetValue/SetValue
/// (an order of magnitude slower per access) for every object it touches.
/// </summary>
internal readonly struct CachedFieldInfo
{
    public readonly FieldInfo Field;
    // Key this field is (de)serialized under. Equals Field.Name except for a field shadowed by a
    // same-named field further down the hierarchy, which is qualified so the two don't collide.
    public readonly string SerializedName;
    public readonly string? SerializeIfCondition;
    public readonly bool HasIgnoreOnNull;
    public readonly string[]? FormerNames;

    /// <summary>Compiled field read; null falls back to Field.GetValue.</summary>
    public readonly Func<object, object?>? Getter;

    /// <summary>Compiled field write (SetValue semantics: null assigns default(T) to value-typed
    /// fields, boxed structs are mutated in place); null falls back to Field.SetValue.</summary>
    public readonly Action<object, object?>? Setter;

    public CachedFieldInfo(FieldInfo field, string serializedName)
    {
        Field = field;
        SerializedName = serializedName;

        var serializeIf = field.GetCustomAttribute<SerializeIfAttribute>();
        SerializeIfCondition = serializeIf?.ConditionMemberName;

        HasIgnoreOnNull = field.IsDefined(typeof(IgnoreOnNullAttribute), false);

        var formerAttrs = field.GetCustomAttributes<FormerlySerializedAsAttribute>();
        string[]? names = null;
        // Avoid LINQ allocation in the common case (no former names)
        foreach (var attr in formerAttrs)
        {
            names ??= CollectFormerNames(formerAttrs);
            break;
        }
        FormerNames = names;

        Getter = ReflectionUtils.CompileGetter(field);
        Setter = ReflectionUtils.CompileSetter(field);
    }

    public object? GetValue(object target) => Getter != null ? Getter(target) : Field.GetValue(target);

    public void SetValue(object target, object? value)
    {
        if (Setter != null) Setter(target, value);
        else Field.SetValue(target, value);
    }

    private static string[] CollectFormerNames(IEnumerable<FormerlySerializedAsAttribute> attrs)
    {
        var list = new List<string>();
        foreach (var attr in attrs)
            list.Add(attr.oldName);
        return list.ToArray();
    }
}

[RequiresUnreferencedCode("These methods use reflection and can't be statically analyzed.")]
public static class ReflectionUtils
{
    // Cache for type lookups
    private static readonly ConcurrentDictionary<string, Type?> TypeCache = new();
    // Cache for serializable fields (with pre-computed attribute data)
    private static readonly ConcurrentDictionary<RuntimeTypeHandle, CachedFieldInfo[]> SerializableFieldsCache = new();
    // Compiled parameterless constructors (incl. non-public); a null value means "not compilable,
    // use Activator" so misses aren't re-attempted per instantiation.
    private static readonly ConcurrentDictionary<RuntimeTypeHandle, Func<object>?> ActivatorCache = new();

    internal static void ClearCache()
    {
        TypeCache.Clear();
        SerializableFieldsCache.Clear();
        ActivatorCache.Clear();
    }

    /// <summary>
    /// Creates an instance of <paramref name="type"/> through a cached compiled constructor
    /// delegate, falling back to <see cref="Activator.CreateInstance(Type, bool)"/> (preserving its
    /// exception behavior, e.g. MissingMethodException for a missing parameterless constructor).
    /// </summary>
    internal static object CreateInstance(Type type)
    {
        Func<object>? factory = ActivatorCache.GetOrAdd(type.TypeHandle, static (_, t) =>
        {
            try
            {
                if (t.IsValueType)
                {
                    // default(T) boxed - what Activator does for constructorless structs.
                    return Expression.Lambda<Func<object>>(
                        Expression.Convert(Expression.New(t), typeof(object))).Compile();
                }

                if (t.IsAbstract || t.IsInterface || t.ContainsGenericParameters)
                    return null;

                ConstructorInfo? ctor = t.GetConstructor(
                    BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    binder: null, Type.EmptyTypes, modifiers: null);
                if (ctor == null)
                    return null; // Activator raises the canonical MissingMethodException

                return Expression.Lambda<Func<object>>(Expression.New(ctor)).Compile();
            }
            catch
            {
                return null;
            }
        }, type);

        return factory != null ? factory() : Activator.CreateInstance(type, nonPublic: true)!;
    }

    /// <summary>Compiles an allocation-free field read; null when not compilable.</summary>
    internal static Func<object, object?>? CompileGetter(FieldInfo field)
    {
        try
        {
            Type declaring = field.DeclaringType!;
            if (declaring.ContainsGenericParameters || field.FieldType.IsByRefLike || field.FieldType.IsPointer)
                return null;

            var obj = Expression.Parameter(typeof(object), "obj");
            Expression typed = declaring.IsValueType
                ? Expression.Unbox(obj, declaring)
                : Expression.Convert(obj, declaring);
            return Expression.Lambda<Func<object, object?>>(
                Expression.Convert(Expression.Field(typed, field), typeof(object)), obj).Compile();
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// Compiles a field write with FieldInfo.SetValue semantics: null assigns default(T) to a
    /// value-typed field, boxed struct targets are mutated in place (Unbox gives a ref into the
    /// box), wrong-typed values throw (callers catch per field). Init-only fields stay on
    /// FieldInfo.SetValue.
    /// </summary>
    internal static Action<object, object?>? CompileSetter(FieldInfo field)
    {
        try
        {
            Type declaring = field.DeclaringType!;
            if (field.IsInitOnly || declaring.ContainsGenericParameters ||
                field.FieldType.IsByRefLike || field.FieldType.IsPointer)
                return null;

            var obj = Expression.Parameter(typeof(object), "obj");
            var value = Expression.Parameter(typeof(object), "value");

            Expression typed = declaring.IsValueType
                ? Expression.Unbox(obj, declaring)
                : Expression.Convert(obj, declaring);

            Type ft = field.FieldType;
            Expression converted;
            if (ft.IsValueType && Nullable.GetUnderlyingType(ft) == null)
            {
                // SetValue(null) semantics: null writes default(T)
                converted = Expression.Condition(
                    Expression.Equal(value, Expression.Constant(null)),
                    Expression.Default(ft),
                    Expression.Convert(value, ft));
            }
            else
            {
                converted = Expression.Convert(value, ft);
            }

            return Expression.Lambda<Action<object, object?>>(
                Expression.Assign(Expression.Field(typed, field), converted), obj, value).Compile();
        }
        catch
        {
            return null;
        }
    }

    internal static Type? FindTypeByName(string qualifiedTypeName)
    {
        return TypeCache.GetOrAdd(qualifiedTypeName, typeName =>
        {
            if (TypeNameRegistry.TryResolveOverride(typeName, out Type? overridden))
                return overridden;

            // First try direct type lookup (works for types in the default AssemblyLoadContext)
            Type? t = Type.GetType(typeName);
            if (t != null)
                return t;

            // Strip assembly qualifier to get just the namespace-qualified type name.
            // Assembly-qualified names look like "Namespace.Type, AssemblyName, Version=..."
            // asm.GetType() needs just "Namespace.Type" to search within a specific assembly.
            string typeNameOnly = typeName;
            string? assemblyName = null;
            int commaIdx = typeName.IndexOf(',');
            if (commaIdx >= 0)
            {
                typeNameOnly = typeName.Substring(0, commaIdx).Trim();

                int nextComma = typeName.IndexOf(',', commaIdx + 1);
                assemblyName = (nextComma >= 0
                    ? typeName.Substring(commaIdx + 1, nextComma - commaIdx - 1)
                    : typeName.Substring(commaIdx + 1)).Trim();
            }

            Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();

            // Search the assembly the name records before any loose simple-name match, so a short name that
            // also exists in another assembly binds to the right one. Matched on simple name only, since an
            // assembly can keep its name while its version or load context changes.
            if (!string.IsNullOrEmpty(assemblyName))
            {
                foreach (Assembly asm in assemblies)
                {
                    if (!string.Equals(asm.GetName().Name, assemblyName, StringComparison.OrdinalIgnoreCase))
                        continue;

                    t = asm.GetType(typeNameOnly);
                    if (t == null)
                    {
                        Type?[] declared;
                        try { declared = asm.GetTypes(); }
                        catch (ReflectionTypeLoadException ex) { declared = ex.Types; }
                        t = declared.FirstOrDefault(type => type != null && type.Name.Equals(typeNameOnly, StringComparison.OrdinalIgnoreCase));
                    }
                    if (t != null)
                        return t;
                }
            }

            foreach (Assembly asm in assemblies)
            {
                // Try full name lookup (handles non-assembly-qualified names)
                t = asm.GetType(typeName);
                if (t != null)
                    return t;

                // Try with stripped assembly qualifier (finds types in custom AssemblyLoadContexts
                // where the assembly-qualified lookup fails)
                if (typeNameOnly != typeName)
                {
                    t = asm.GetType(typeNameOnly);
                    if (t != null)
                        return t;
                }

                // Try name-only lookup (case insensitive) while ignoring load failures
                Type[] types;
                try
                {
                    types = asm.GetTypes();
                }
                catch (ReflectionTypeLoadException ex)
                {
                    types = ex.Types.Where(type => type != null).Cast<Type>().ToArray();
                }

                t = types.FirstOrDefault(type => type.Name.Equals(typeNameOnly, StringComparison.OrdinalIgnoreCase));
                if (t != null)
                    return t;
            }

            // Former-name fallback: the serialized type may have been renamed. Look for a loaded type
            // carrying [FormerlySerializedAs(oldName)] whose old name matches the (now-missing) requested
            // name - either the old namespace-qualified name or the old short name.
            return FindTypeByFormerName(typeNameOnly);
        });
    }

    private static Type? FindTypeByFormerName(string typeNameOnly)
    {
        string shortName = typeNameOnly;
        int dot = typeNameOnly.LastIndexOf('.');
        if (dot >= 0) shortName = typeNameOnly[(dot + 1)..];

        foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type[] types;
            try { types = asm.GetTypes(); }
            catch (ReflectionTypeLoadException ex) { types = ex.Types.Where(type => type != null).Cast<Type>().ToArray(); }

            foreach (Type type in types)
            {
                foreach (var attr in type.GetCustomAttributes<FormerlySerializedAsAttribute>(false))
                {
                    if (attr.oldName == typeNameOnly || attr.oldName == shortName)
                        return type;
                }
            }
        }
        return null;
    }

    internal static CachedFieldInfo[] GetSerializableFields(this object target)
    {
        Type targetType = target.GetType();
        return SerializableFieldsCache.GetOrAdd(targetType.TypeHandle, _ =>
        {
            const BindingFlags flags = BindingFlags.Public |
                                     BindingFlags.NonPublic |
                                     BindingFlags.Instance |
                                     BindingFlags.DeclaredOnly;

            // Start with the current type
            List<CachedFieldInfo> fields = new List<CachedFieldInfo>();
            HashSet<string> seenNames = new();
            Type? currentType = targetType;

            // Walk up the inheritance hierarchy to collect fields from all base types. The most-derived
            // field of a shadowed name is seen first and keeps the plain name; any same-named base field
            // is qualified by its declaring type so both survive instead of colliding on one key.
            while (currentType != null && currentType != typeof(object))
            {
                foreach (var field in currentType.GetFields(flags))
                {
                    if (!IsFieldSerializable(field))
                        continue;

                    string serializedName = seenNames.Add(field.Name)
                        ? field.Name
                        : $"{field.Name}@{field.DeclaringType!.FullName}";
                    fields.Add(new CachedFieldInfo(field, serializedName));
                }

                currentType = currentType.BaseType;
            }

            return fields.ToArray();
        });
    }

    private static bool IsFieldSerializable(FieldInfo field)
    {
        // Check if field should be serialized
        bool shouldSerialize = field.IsPublic ||
                               field.GetCustomAttribute<SerializeFieldAttribute>() != null ||
                               field.GetCustomAttribute<System.Runtime.Serialization.DataMemberAttribute>() != null;
        if (!shouldSerialize)
            return false;
        // Check if field should be ignored
        bool shouldIgnore = field.GetCustomAttribute<SerializeIgnoreAttribute>() != null ||
                            field.GetCustomAttribute<NonSerializedAttribute>() != null;
        if (shouldIgnore)
            return false;
        return true;
    }
}
