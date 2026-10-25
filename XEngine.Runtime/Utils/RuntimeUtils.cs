// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

using XEngine.Echo;

namespace XEngine.Runtime;


// From https://github.com/dotnet/runtime/blob/main/src/libraries/Microsoft.NETCore.Platforms/src/PortableRuntimeIdentifierGraph.json
public enum Platform
{
    Android,
    Browser,
    FreeBSD,
    Haiku,
    Illumos,
    iOS,
    iOSSimulator,
    Linux,
    MacCatalyst,
    MacOS,
    Solaris,
    tvOS,
    tvOSSimulator,
    Unix,
    Wasi,
    Windows,
    HarmonyOS,
}


[RequiresUnreferencedCode("These methods use reflection and can't be statically analyzed.")]
public static class RuntimeUtils
{
    private static readonly Dictionary<TypeInfo, bool> s_deepCopyByAssignmentCache = [];
    private static readonly Dictionary<Type, int> s_executionOrderCache = [];

    // Concurrent because assets (and therefore their type names) are resolved on background
    // load threads as well as the main thread. Only successful lookups are stored, so a name
    // that isn't resolvable yet stays resolvable once its assembly finishes loading.
    private static readonly ConcurrentDictionary<string, Type> s_resolvedTypeCache = new(StringComparer.Ordinal);

    // Set between the unload-scrub and the post-reload load callback. Background threads (asset
    // loading) can resolve type names DURING that window; caching a type that belongs to the dying
    // script ALC would pin it and fail the unload verification nondeterministically.
    private static volatile bool s_scriptReloadInProgress;

    [OnAssemblyUnload]
    public static void ClearCache()
    {
        s_deepCopyByAssignmentCache.Clear();
        s_executionOrderCache.Clear();
        // Must be cleared before the script ALC unloads - a cached Type would pin it alive.
        s_resolvedTypeCache.Clear();
        s_scriptReloadInProgress = true;
    }

    [OnAssemblyLoad]
    public static void EndScriptReloadWindow() => s_scriptReloadInProgress = false;

    public static bool IsARM() =>
        RuntimeInformation.OSArchitecture == Architecture.Arm ||
        RuntimeInformation.OSArchitecture == Architecture.Arm64;

    public static bool IsWindows() => RuntimeInformation.IsOSPlatform(OSPlatform.Windows);
    public static bool IsLinux() => RuntimeInformation.IsOSPlatform(OSPlatform.Linux);
    public static bool IsFreeBSD() => RuntimeInformation.IsOSPlatform(OSPlatform.FreeBSD);
    public static bool IsMac() => RuntimeInformation.IsOSPlatform(OSPlatform.OSX);
    public static bool IsBrowser() => RuntimeInformation.IsOSPlatform(OSPlatform.Create("BROWSER"));

    /// <summary>
    /// True when running on Android. Uses the <c>XENGINE_ANDROID</c> compile-time constant when set
    /// (the player build injects it via the Android build profile's <c>ModifyDefines</c>), and falls
    /// back to <see cref="OperatingSystem.IsAndroid"/> so editor/desktop runs report false without
    /// relying on environment variables or file existence heuristics.
    /// </summary>
    public static bool IsAndroid()
    {
#if XENGINE_ANDROID
        return true;
#else
        return OperatingSystem.IsAndroid();
#endif
    }

    /// <summary>
    /// True when running on HarmonyOS/OpenHarmony. The OpenHarmony NativeAOT provider uses a
    /// Linux-compatible runtime identifier, so this check must precede the generic Linux fallback.
    /// </summary>
    public static bool IsHarmonyOS()
    {
#if XENGINE_HARMONYOS
        return true;
#else
        return RuntimeInformation.IsOSPlatform(OSPlatform.Create("OHOS")) ||
            RuntimeInformation.IsOSPlatform(OSPlatform.Create("OPENHARMONY"));
#endif
    }


    public static Platform GetOSPlatform()
        => IsWindows() ? Platform.Windows :
            IsAndroid() ? Platform.Android :
            IsHarmonyOS() ? Platform.HarmonyOS :
            IsLinux() ? Platform.Linux :
            IsMac() ? Platform.MacOS :
            throw new Exception("Unable to determine OS Platform");


    public static Type? FindType(string qualifiedTypeName)
    {
        Type? registered = RuntimeTypeRegistry.Resolve(qualifiedTypeName);
        if (registered != null || RuntimeTypeRegistry.IsAuthoritative)
            return registered;

        Type? t = Type.GetType(qualifiedTypeName);
        if (t != null)
            return t;

        // Strip assembly qualifier to get just the namespace-qualified type name.
        // Assembly-qualified names look like "Namespace.Type, AssemblyName, Version=..."
        // asm.GetType() needs just "Namespace.Type" to search within a specific assembly.
        string typeNameOnly = qualifiedTypeName;
        int commaIdx = qualifiedTypeName.IndexOf(',');
        if (commaIdx >= 0)
            typeNameOnly = qualifiedTypeName.Substring(0, commaIdx).Trim();

        foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            t = asm.GetType(qualifiedTypeName);
            if (t != null)
                return t;

            // Try with stripped assembly qualifier
            if (typeNameOnly != qualifiedTypeName)
            {
                t = asm.GetType(typeNameOnly);
                if (t != null)
                    return t;
            }

            // If not found, try to find by name without namespace. A desktop editor can have
            // optional native/API assemblies loaded whose metadata contains types that the current
            // runtime cannot reflect (for example an overlapping interop union). One such assembly
            // must not abort resolution for every later user/script assembly.
            Type[] candidates;
            try
            {
                candidates = asm.GetTypes();
            }
            catch (ReflectionTypeLoadException ex)
            {
                var loadedTypes = new List<Type>();
                foreach (Type? type in ex.Types)
                    if (type != null)
                        loadedTypes.Add(type);
                candidates = loadedTypes.ToArray();
            }
            catch (TypeLoadException)
            {
                continue;
            }

            foreach (Type candidate in candidates)
                if (candidate.Name.Equals(typeNameOnly, StringComparison.OrdinalIgnoreCase))
                    return candidate;
        }
        return null;
    }

    /// <summary>
    /// Resolves a type from an assembly-qualified name across ALL loaded assembly load contexts,
    /// including the collectible context user scripts are loaded into. <see cref="Type.GetType(string)"/>
    /// can only bind names into the default context by CLR rule, so it returns null for any type
    /// declared in a user assembly - which is why persisted type names (asset entries, and anything
    /// else round-tripped through <see cref="Type.AssemblyQualifiedName"/>) need this instead.
    /// </summary>
    /// <remarks>
    /// Unlike <see cref="FindType"/>, this honors the assembly recorded in the name and never falls
    /// back to a loose simple-name match, so it cannot return the wrong type when two assemblies
    /// declare types that share a name. Assemblies are matched on simple name only: a script assembly
    /// keeps its name but gets a fresh version and load context on every hot-reload, so matching full
    /// identity would miss it. Returns null rather than throwing for malformed or unloadable names.
    /// </remarks>
    public static Type? ResolveType(string assemblyQualifiedName)
    {
        if (string.IsNullOrWhiteSpace(assemblyQualifiedName))
            return null;

        Type? registered = RuntimeTypeRegistry.Resolve(assemblyQualifiedName);
        if (registered != null || RuntimeTypeRegistry.IsAuthoritative)
            return registered;

        if (s_resolvedTypeCache.TryGetValue(assemblyQualifiedName, out Type? cached))
            return cached;

        Type? resolved;
        try
        {
            // Fast path: already resolvable in the default context.
            resolved = Type.GetType(assemblyQualifiedName, throwOnError: false);

            // Slow path: bind against every loaded assembly. The resolver overload is applied
            // recursively to generic arguments and array element types, so it also handles names
            // like List<UserType> whose inner assembly qualifier is a collectible-context one.
            resolved ??= Type.GetType(assemblyQualifiedName, ResolveLoadedAssembly, ResolveTypeInAssembly, throwOnError: false);
        }
        catch (Exception ex) when (ex is FileLoadException or BadImageFormatException or ArgumentException or TypeLoadException)
        {
            // A malformed or unloadable name persisted in the asset database must not tear down the
            // caller - these getters run inside editor draw loops. Treat it as simply unresolved.
            return null;
        }

        // Only successful resolutions are cached; a miss may just mean the assembly isn't loaded yet.
        // During a script reload, never cache types touching the collectible script context (directly
        // or via generic arguments) - the context is being torn down and a cached handle pins it.
        if (resolved != null && !(s_scriptReloadInProgress && TouchesCollectibleAssembly(resolved)))
            s_resolvedTypeCache[assemblyQualifiedName] = resolved;

        return resolved;
    }

    /// <summary>True when the type (or any generic argument / array element, recursively) is declared
    /// by a collectible (hot-reloadable) assembly.</summary>
    private static bool TouchesCollectibleAssembly(Type type)
    {
        for (Type? current = type; current != null; current = current.DeclaringType)
        {
            if (current.Assembly.IsCollectible) return true;
            if (current.IsArray)
            {
                Type? element = current.GetElementType();
                if (element != null && TouchesCollectibleAssembly(element)) return true;
                continue;
            }
            if (current.IsGenericType)
            {
                foreach (Type argument in current.GetGenericArguments())
                {
                    if (TouchesCollectibleAssembly(argument)) return true;
                }
            }
        }
        return false;
    }

    private static Assembly? ResolveLoadedAssembly(AssemblyName name)
    {
        foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
            if (string.Equals(asm.GetName().Name, name.Name, StringComparison.OrdinalIgnoreCase))
                return asm;

        return null;
    }

    private static Type? ResolveTypeInAssembly(Assembly? asm, string typeName, bool ignoreCase)
    {
        if (asm != null)
            return asm.GetType(typeName, throwOnError: false, ignoreCase);

        // No assembly qualifier on this portion of the name - search every loaded assembly.
        foreach (Assembly candidate in AppDomain.CurrentDomain.GetAssemblies())
        {
            Type? t = candidate.GetType(typeName, throwOnError: false, ignoreCase);
            if (t != null)
                return t;
        }

        return null;
    }

    public static PropertyInfo GetInstanceProperty(this Type type, string name)
    {
        PropertyInfo result = null;
        foreach (PropertyInfo property in type.GetRuntimeProperties())
        {
            if (!property.IsStatic() && property.Name == name)
            {
                result = property;
                break;
            }
        }
        if (result == null)
            Debug.LogError(string.Format("Unable to retrieve property '{0}' of type '{1}'.", name, type));
        return result;
    }

    public static FieldInfo GetInstanceField(this Type type, string name)
    {
        FieldInfo result = null;
        foreach (FieldInfo field in type.GetRuntimeFields())
        {
            if (!field.IsStatic && field.Name == name)
            {
                result = field;
                break;
            }
        }
        if (result == null)
            Debug.LogError(string.Format("Unable to retrieve field '{0}' of type '{1}'.", name, type));
        return result;
    }

    public static bool IsPublic(this PropertyInfo property)
    {
        return
            (property.CanRead && property.GetMethod.IsPublic) ||
            (property.CanWrite && property.SetMethod.IsPublic);
    }
    public static bool IsStatic(this PropertyInfo property)
    {
        return
            (property.CanRead && property.GetMethod.IsStatic) ||
            (property.CanWrite && property.SetMethod.IsStatic);
    }

    /// <summary>
    /// Determines whether two <see cref="MemberInfo"/> instances refer to the same member,
    /// regardless of the context in which each instance was obtained.
    /// </summary>
    public static bool IsEquivalent(this MemberInfo first, MemberInfo second)
    {
        if (first == second)
            return true;

        // Early-out the basic stuff
        if (first.DeclaringType != second.DeclaringType)
            return false;
        if (first.Module != second.Module)
            return false;
        if (first.Name != second.Name)
            return false;

        // Check if they're both the same kind of member
        if (first is FieldInfo)
        {
            if (second is not FieldInfo)
                return false;
        }
        else if (first is PropertyInfo)
        {
            if (second is not PropertyInfo)
                return false;
        }
        else if (first is MethodInfo)
        {
            if (second is not MethodInfo)
                return false;
        }
        else if (first is ConstructorInfo)
        {
            if (second is not ConstructorInfo)
                return false;
        }
        else if (first is EventInfo)
        {
            if (second is not EventInfo)
                return false;
        }
        else if (first is TypeInfo)
        {
            if (second is not TypeInfo)
                return false;
        }

        if (first is MethodBase)
        {
            MethodBase firstMethod = first as MethodBase;
            MethodBase secondMethod = second as MethodBase;

            // If its a generic method, check its generic Type parameters
            if (firstMethod.IsGenericMethod)
            {
                if (!secondMethod.IsGenericMethod)
                    return false;

                Type[] firstGenArgs = firstMethod.GetGenericArguments();
                Type[] secondGenArgs = secondMethod.GetGenericArguments();
                for (int i = 0; i < secondGenArgs.Length; i++)
                {
                    if (firstGenArgs[i] != secondGenArgs[i])
                        return false;
                }
            }

            // Check its parameters
            ParameterInfo[] firstParams = firstMethod.GetParameters();
            ParameterInfo[] secondParams = secondMethod.GetParameters();
            if (firstParams.Length != secondParams.Length)
                return false;

            for (int i = 0; i < firstParams.Length; i++)
            {
                if (firstParams[i].ParameterType != secondParams[i].ParameterType)
                    return false;
                if (firstParams[i].IsOut != secondParams[i].IsOut)
                    return false;
                if (firstParams[i].IsIn != secondParams[i].IsIn)
                    return false;
                if (firstParams[i].IsRetval != secondParams[i].IsRetval)
                    return false;
            }
        }

        return true;
    }

    public static FieldInfo[] GetSerializableFields(this object target)
    {
        List<FieldInfo> fields = [];
        foreach (FieldInfo field in GetAllFields(target.GetType()))
        {
            bool isSerialized = field.IsPublic || field.GetCustomAttribute<SerializeFieldAttribute>() != null;
            bool isIgnored = field.GetCustomAttribute<SerializeIgnoreAttribute>() != null;
            bool isPublicNonSerialized = field.IsPublic && field.GetCustomAttribute<NonSerializedAttribute>() != null;
            if (isSerialized && !isIgnored && !isPublicNonSerialized)
                fields.Add(field);
        }
        return fields.ToArray();
    }

    public static IEnumerable<FieldInfo> GetAllFields(Type? t)
    {
        if (t == null)
            yield break;

        BindingFlags flags = BindingFlags.Public | BindingFlags.NonPublic |
                             BindingFlags.Instance | BindingFlags.DeclaredOnly;

        foreach (FieldInfo field in t.GetFields(flags))
            yield return field;
        foreach (FieldInfo field in GetAllFields(t.BaseType))
            yield return field;
    }

    public static object? GetValue(this MemberInfo member, object? target)
    {
        if (member is PropertyInfo prop)
            return prop.GetValue(target);
        else if (member is FieldInfo field)
            return field.GetValue(target);
        else
            return null;
    }

    public static void SetValue(this MemberInfo member, object? target, object? value)
    {
        if (member is PropertyInfo prop)
            prop.SetValue(target, value);
        else if (member is FieldInfo field)
            field.SetValue(target, value);
    }

    public static string Prettify(string label)
    {
        if (label.StartsWith('_'))
            label = label.Substring(1);

        // Use a StringBuilder to avoid modifying the original string in the loop
        StringBuilder result = new(label.Length * 2);
        result.Append(char.ToUpper(label[0]));

        // Add space before each Capital letter (except the first)
        for (int i = 1; i < label.Length; i++)
        {
            if (char.IsUpper(label[i]) && !char.IsUpper(label[i - 1]))
            {
                result.Append(' ');      // Add space
                result.Append(label[i]); // Append the current uppercase character
            }
            else if (label[i] == '_')
            {
                continue;
            }
            else
            {
                result.Append(label[i]);  // Append the current character
            }
        }

        return Thread.CurrentThread.CurrentCulture.TextInfo.ToTitleCase(result.ToString());
    }

    public static IEnumerable<Type> GetTypesWithAttribute<T>()
    {
        if (RuntimeTypeRegistry.IsAuthoritative)
            return RuntimeTypeRegistry.GetTypesWithAttribute(typeof(T));

        return GetTypesWithAttributeFromAssemblies<T>();
    }

    private static IEnumerable<Type> GetTypesWithAttributeFromAssemblies<T>()
    {
        Assembly[] assemblies = AppDomain.CurrentDomain.GetAssemblies();
        foreach (Assembly assembly in assemblies)
            foreach (Type type in assembly.GetTypes())
                if (type.GetCustomAttributes(typeof(T), true).Length > 0)
                    yield return type;
    }

    public static List<Type> FindTypesImplementing(Type propertyType, bool ignoreGenerics = false)
    {
        if (RuntimeTypeRegistry.IsAuthoritative)
            return RuntimeTypeRegistry.FindTypesImplementing(propertyType, ignoreGenerics);

        List<Type> types = [];
        foreach (Assembly asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            foreach (Type type in asm.GetTypes())
            {
                if (!propertyType.IsAssignableFrom(type) || type.IsAbstract || type.IsInterface)
                    continue;

                if (ignoreGenerics && type.IsGenericType)
                    continue;

                types.Add(type);
            }
        }
        return types;
    }

    public static string GetUniquePath(string target)
    {
        if (!System.IO.File.Exists(target))
            return target;

        string path = System.IO.Path.GetDirectoryName(target);
        string name = System.IO.Path.GetFileNameWithoutExtension(target);
        string ext = System.IO.Path.GetExtension(target);

        for (int i = 1; ; i++)
        {
            string temp = System.IO.Path.Combine(path, $"{name} ({i}){ext}");
            if (!System.IO.File.Exists(temp))
                return temp;
        }
    }

    public static bool HasAttribute<T>(this MemberInfo member) where T : Attribute => member.GetCustomAttributes(typeof(T), true).Length > 0;
    public static IEnumerable<T> GetAttributes<T>(this MemberInfo member) where T : Attribute
    {
        foreach (object attribute in member.GetCustomAttributes(typeof(T), true))
            yield return (T)attribute;
    }

    /// <summary>
    /// Returns whether the specified type is a primitive, enum, string, decimal, or struct that
    /// consists only of those types, allowing to do a deep-copy by simply assigning it.
    /// </summary>
    /// Source: https://github.com/AdamsLair/duality/blob/4156e696b381e508df409ca4741d1eae88223287/Source/Core/Duality/Utility/ReflectionHelper.cs#L470
    public static bool IsDeepCopyByAssignment(this TypeInfo typeInfo)
    {
        // Early-out for some obvious cases
        if (typeInfo.IsArray) return false;
        if (typeInfo.IsPrimitive) return true;
        if (typeInfo.IsEnum) return true;

        // Special cases for some well-known classes
        Type type = typeInfo.AsType();
        if (type == typeof(string)) return true;
        if (type == typeof(decimal)) return true;

        // Otherwise, any class is not plain old data
        if (typeInfo.IsClass) return false;
        if (typeInfo.IsInterface) return false;

        lock (s_deepCopyByAssignmentCache)
        {
            // If we have no evidence so far, check the cache and iterate fields
            if (s_deepCopyByAssignmentCache.TryGetValue(typeInfo, out bool isPlainOldData))
            {
                return isPlainOldData;
            }
            else
            {
                isPlainOldData = true;
                foreach (FieldInfo field in typeInfo.DeclaredFieldsDeep())
                {
                    if (field.IsStatic) continue;
                    TypeInfo fieldTypeInfo = field.FieldType.GetTypeInfo();
                    if (!IsDeepCopyByAssignment(fieldTypeInfo))
                    {
                        isPlainOldData = false;
                        break;
                    }
                }

                s_deepCopyByAssignmentCache[typeInfo] = isPlainOldData;
                return isPlainOldData;
            }
        }
    }

    /// <summary>
    /// Returns a TypeInfos BaseType as a TypeInfo, or null if it was null.
    /// </summary>
    /// <param name="type"></param>
    /// Source: https://github.com/AdamsLair/duality/blob/4156e696b381e508df409ca4741d1eae88223287/Source/Core/Duality/Utility/Extensions/ExtMethodsTypeInfo.cs#L45
    public static TypeInfo GetBaseTypeInfo(this TypeInfo type)
    {
        return type.BaseType != null ? type.BaseType.GetTypeInfo() : null;
    }

    /// <summary>
    /// Returns a Types inheritance level. The <c>object</c>-Type has an inheritance level of
    /// zero, each subsequent inheritance increases it by one.
    /// </summary>
    /// Source: https://github.com/AdamsLair/duality/blob/4156e696b381e508df409ca4741d1eae88223287/Source/Core/Duality/Utility/Extensions/ExtMethodsTypeInfo.cs#L45
    public static int GetInheritanceDepth(this TypeInfo type)
    {
        int level = 0;
        while (type.BaseType != null)
        {
            type = type.BaseType.GetTypeInfo();
            level++;
        }
        return level;
    }

    /// <summary>
    /// Returns all fields that are declared within this Type, or any of its base Types.
    /// Includes public, non-public, static and instance fields.
    /// </summary>
    /// Source: https://github.com/AdamsLair/duality/blob/4156e696b381e508df409ca4741d1eae88223287/Source/Core/Duality/Utility/Extensions/ExtMethodsTypeInfo.cs#L45
    public static IEnumerable<FieldInfo> DeclaredFieldsDeep(this TypeInfo type)
    {
        while (type != null)
        {
            foreach (FieldInfo field in type.DeclaredFields)
                yield return field;
            type = type.GetBaseTypeInfo();
        }
    }

    /// <summary>
    /// Returns all properties that are declared within this Type, or any of its base Types.
    /// Includes public, non-public, static and instance properties.
    /// </summary>
    /// Source: https://github.com/AdamsLair/duality/blob/4156e696b381e508df409ca4741d1eae88223287/Source/Core/Duality/Utility/Extensions/ExtMethodsTypeInfo.cs#L45
    public static IEnumerable<PropertyInfo> DeclaredPropertiesDeep(this TypeInfo type)
    {
        while (type != null)
        {
            foreach (PropertyInfo property in type.DeclaredProperties)
                yield return property;
            type = type.GetBaseTypeInfo();
        }
    }

    /// <summary>
    /// Returns all members that are declared within this Type, or any of its base Types.
    /// Includes public, non-public, static and instance fields.
    /// </summary>
    /// Source: https://github.com/AdamsLair/duality/blob/4156e696b381e508df409ca4741d1eae88223287/Source/Core/Duality/Utility/Extensions/ExtMethodsTypeInfo.cs#L45
    public static IEnumerable<MemberInfo> DeclaredMembersDeep(this TypeInfo type)
    {
        while (type != null)
        {
            foreach (MemberInfo member in type.DeclaredMembers)
                yield return member;
            type = type.GetBaseTypeInfo();
        }
    }

    /// <summary>
    /// An ultra-fast way to convert a bool to an int.
    /// </summary>
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static unsafe int AsInt(this bool b) =>
        NonNormalizedAsInt((*(byte*)&b) != 0);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    private static unsafe int NonNormalizedAsInt(bool b) =>
        *(byte*)(&b);

    internal static int? GetExecutionOrder(MonoBehaviour a)
    {
        Type type = a.GetType();
        if (s_executionOrderCache.TryGetValue(type, out int order))
            return order;
        ExecutionOrderAttribute? attr = type.GetCustomAttribute<ExecutionOrderAttribute>();
        if (attr != null)
        {
            s_executionOrderCache[type] = attr.Order;
            return attr.Order;
        }
        return null;
    }
}
