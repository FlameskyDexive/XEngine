// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Reflection;
using System.Threading;

using XEngine.Echo;

namespace XEngine.Runtime;

/// <summary>
/// Explicit type roots used by trimmed players. Desktop/editor builds may leave the registry open
/// and fall back to assembly discovery; generated players seal it before loading any assets.
/// </summary>
public static class RuntimeTypeRegistry
{
    private static readonly object s_gate = new();
    private static readonly Dictionary<string, Type> s_typesByName = new(StringComparer.OrdinalIgnoreCase);
    // Redirects are kept as names rather than Type instances. A package can be unloaded and
    // reloaded through a collectible ALC without the core registry pinning the old assembly.
    private static readonly Dictionary<string, string> s_typeAliasesByName = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, Assembly> s_assembliesByName = new(StringComparer.OrdinalIgnoreCase);
    private static readonly HashSet<string> s_ambiguousNames = new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<Type> s_types = [];
    private static bool s_authoritative;

    static RuntimeTypeRegistry()
    {
        TypeNameRegistry.TypeResolver = Resolve;
    }

    /// <summary>True when misses must not fall back to scanning loaded assemblies.</summary>
    public static bool IsAuthoritative => Volatile.Read(ref s_authoritative);

    /// <summary>Number of distinct registered runtime types.</summary>
    public static int Count
    {
        get
        {
            lock (s_gate)
                return s_types.Count;
        }
    }

    /// <summary>
    /// Registers a serialized type-name redirect without retaining a <see cref="Type"/> from a
    /// package load context. The alias is normalized to the type name plus simple assembly name,
    /// so old Echo data that includes version/culture metadata resolves identically.
    /// </summary>
    public static void RegisterAlias(string serializedName, string targetAssemblyQualifiedName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serializedName);
        ArgumentException.ThrowIfNullOrWhiteSpace(targetAssemblyQualifiedName);

        string alias = NormalizeLookupName(serializedName);
        string target = NormalizeLookupName(targetAssemblyQualifiedName);
        lock (s_gate)
        {
            if (s_typeAliasesByName.TryGetValue(alias, out string? existing)
                && !existing.Equals(target, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"Serialized type alias '{alias}' is already mapped to '{existing}'.");
            }

            s_typeAliasesByName[alias] = target;
        }

        // TypeNameRegistry caches both successful and failed lookups. A package may register its
        // aliases after an editor first inspected a missing component, so invalidate that cache.
        TypeNameRegistry.ClearCache();
    }

    /// <summary>
    /// Registers a type for dynamic lookup. The annotation transfers the reflection requirements
    /// to callers that register types directly in source.
    /// </summary>
    public static void Register(
        [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.All)] Type type)
    {
        ArgumentNullException.ThrowIfNull(type);
        lock (s_gate)
            RegisterCore(type);
    }

    /// <summary>
    /// Installs the deterministic registry emitted by a player build and makes it authoritative.
    /// Every name has a matching generated <see cref="DynamicDependencyAttribute"/> root.
    /// </summary>
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2057:Unrecognized value passed to the parameter 'typeName' of method 'System.Type.GetType'",
        Justification = "Generated player source emits a matching DynamicDependency(All, type, assembly) for every registered name.")]
    public static void ConfigureGenerated(params string[] assemblyQualifiedTypeNames)
    {
        ArgumentNullException.ThrowIfNull(assemblyQualifiedTypeNames);

        lock (s_gate)
        {
            Volatile.Write(ref s_authoritative, false);
            s_typesByName.Clear();
            s_assembliesByName.Clear();
            s_ambiguousNames.Clear();
            s_types.Clear();

            for (int i = 0; i < assemblyQualifiedTypeNames.Length; i++)
            {
                string typeName = assemblyQualifiedTypeNames[i];
                if (string.IsNullOrWhiteSpace(typeName))
                    throw new ArgumentException("Generated runtime type names cannot be empty.", nameof(assemblyQualifiedTypeNames));

                Type type = Type.GetType(typeName, throwOnError: false)
                    ?? throw new TypeLoadException($"Generated runtime type root could not be loaded: {typeName}");
                RegisterCore(type);
            }

            TypeNameRegistry.TypeResolver = Resolve;
            TypeNameRegistry.TypeResolverIsAuthoritative = true;
            Volatile.Write(ref s_authoritative, true);
        }

        // A generated registry is installed before asset load, but clearing here also makes
        // reconfiguration deterministic for hosts that initialize more than once in tests.
        Serializer.ClearCache();
    }

    /// <summary>Hot-reload: clear cached Types so script-ALC type references are released.</summary>
    [OnAssemblyUnload]
    internal static void ClearForReload()
    {
        lock (s_gate)
        {
            s_typesByName.Clear();
            s_assembliesByName.Clear();
            s_types.Clear();
        }
    }

    /// <summary>Resolves a registered full, assembly-qualified, compact, array, or generic name.</summary>
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2057:Unrecognized value passed to the parameter 'typeName' of method 'System.Type.GetType'",
        Justification = "The resolver callbacks only expose Type instances already present in the explicit registry and rooted by its callers.")]
    [UnconditionalSuppressMessage(
        "Trimming",
        "IL2026:RequiresUnreferencedCode",
        Justification = "Alias targets are package types rooted by the package/player build; loaded-assembly lookup is only a fallback for editor hot reload.")]
    public static Type? Resolve(string typeName)
    {
        if (string.IsNullOrWhiteSpace(typeName))
            return null;

        string? aliasTarget = null;
        lock (s_gate)
        {
            if (s_typeAliasesByName.TryGetValue(NormalizeLookupName(typeName), out string? target))
                aliasTarget = target;
            if (s_typesByName.TryGetValue(typeName, out Type? direct))
                return direct;
        }

        if (aliasTarget != null)
        {
            lock (s_gate)
            {
                // Generated players are authoritative: only explicitly rooted target types may
                // satisfy an alias. Desktop/editor builds can bind a loaded package by its AQN.
                if (s_typesByName.TryGetValue(aliasTarget, out Type? registeredTarget))
                    return registeredTarget;
                if (s_authoritative)
                    return null;
            }

            try
            {
                Type? resolved = Type.GetType(aliasTarget, throwOnError: false);
                if (resolved != null)
                    return resolved;

                // Package assemblies can live in a collectible editor load context. CLR's
                // Type.GetType only probes the default context, so inspect currently loaded
                // assemblies by simple name without retaining any of them in this registry.
                int comma = aliasTarget.IndexOf(',');
                string targetTypeName = comma >= 0 ? aliasTarget[..comma].Trim() : aliasTarget;
                string? targetAssemblyName = comma >= 0 ? aliasTarget[(comma + 1)..].Trim() : null;
                Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
                for (int i = 0; i < assemblies.Length; i++)
                {
                    Assembly assembly = assemblies[i];
                    if (targetAssemblyName != null
                        && !string.Equals(assembly.GetName().Name, targetAssemblyName, StringComparison.OrdinalIgnoreCase))
                        continue;
                    Type? candidate = assembly.GetType(targetTypeName, throwOnError: false, ignoreCase: false);
                    if (candidate != null)
                        return candidate;
                }
                return null;
            }
            catch (Exception exception) when (
                exception is ArgumentException or FileLoadException or TypeLoadException)
            {
                return null;
            }
        }

        try
        {
            // The callbacks only expose registered assemblies and types. Type.GetType still handles
            // CLR array/generic syntax without turning a miss into an assembly-wide type scan.
            return Type.GetType(typeName, ResolveRegisteredAssembly, ResolveRegisteredType, throwOnError: false);
        }
        catch (Exception exception) when (
            exception is ArgumentException or FileLoadException or TypeLoadException)
        {
            return null;
        }
    }

    /// <summary>Returns registered concrete implementations without scanning assemblies.</summary>
    public static List<Type> FindTypesImplementing(Type baseType, bool ignoreGenerics = false)
    {
        ArgumentNullException.ThrowIfNull(baseType);
        var result = new List<Type>();
        lock (s_gate)
        {
            for (int i = 0; i < s_types.Count; i++)
            {
                Type type = s_types[i];
                if (!baseType.IsAssignableFrom(type) || type.IsAbstract || type.IsInterface)
                    continue;
                if (ignoreGenerics && type.IsGenericType)
                    continue;
                result.Add(type);
            }
        }
        return result;
    }

    /// <summary>Returns registered types carrying the requested attribute.</summary>
    public static List<Type> GetTypesWithAttribute(Type attributeType)
    {
        ArgumentNullException.ThrowIfNull(attributeType);
        var result = new List<Type>();
        lock (s_gate)
        {
            for (int i = 0; i < s_types.Count; i++)
            {
                Type type = s_types[i];
                if (type.IsDefined(attributeType, inherit: true))
                    result.Add(type);
            }
        }
        return result;
    }

    private static void RegisterCore(Type type)
    {
        if (!s_types.Contains(type))
            s_types.Add(type);

        Assembly assembly = type.Assembly;
        string? assemblyName = assembly.GetName().Name;
        if (!string.IsNullOrWhiteSpace(assemblyName))
            s_assembliesByName[assemblyName] = assembly;

        AddName(type.AssemblyQualifiedName, type);
        AddName(type.FullName, type);
        AddName(type.Name, type);
        if (!string.IsNullOrWhiteSpace(type.FullName) && !string.IsNullOrWhiteSpace(assemblyName))
            AddName(type.FullName + ", " + assemblyName, type);
    }

    private static string NormalizeLookupName(string name)
    {
        string trimmed = name.Trim();
        int comma = trimmed.IndexOf(',');
        if (comma < 0)
            return trimmed;

        string typeName = trimmed[..comma].Trim();
        string assembly = trimmed[(comma + 1)..].Trim();
        int assemblyComma = assembly.IndexOf(',');
        if (assemblyComma >= 0)
            assembly = assembly[..assemblyComma].Trim();
        return assembly.Length == 0 ? typeName : typeName + ", " + assembly;
    }

    private static void AddName(string? name, Type type)
    {
        if (string.IsNullOrWhiteSpace(name) || s_ambiguousNames.Contains(name))
            return;
        if (s_typesByName.TryGetValue(name, out Type? existing) && existing != type)
        {
            s_typesByName.Remove(name);
            s_ambiguousNames.Add(name);
            return;
        }
        s_typesByName[name] = type;
    }

    private static Assembly? ResolveRegisteredAssembly(AssemblyName assemblyName)
    {
        lock (s_gate)
            return assemblyName.Name != null && s_assembliesByName.TryGetValue(assemblyName.Name, out Assembly? assembly)
                ? assembly
                : null;
    }

    private static Type? ResolveRegisteredType(Assembly? assembly, string typeName, bool ignoreCase)
    {
        lock (s_gate)
        {
            if (assembly != null)
            {
                string? assemblyName = assembly.GetName().Name;
                if (assemblyName != null && s_typesByName.TryGetValue(typeName + ", " + assemblyName, out Type? qualified))
                    return qualified;
            }
            return s_typesByName.TryGetValue(typeName, out Type? type) ? type : null;
        }
    }
}
