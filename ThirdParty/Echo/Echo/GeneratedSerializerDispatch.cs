// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Collections.Concurrent;
using System.Reflection;

namespace XEngine.Echo;

/// <summary>
/// Decides whether an object's <see cref="ISerializable"/> implementation may be used for its exact
/// runtime type.
///
/// A source-generated implementation ([GenerateSerializer]) only covers the fields visible on the
/// annotated type at compile time. A subclass that is not itself annotated would inherit that
/// implementation and silently lose its own fields, so for such types the formatters fall back to
/// the reflection path (which walks the full runtime hierarchy). Manual implementations keep their
/// historical behavior: they apply to subclasses too.
/// </summary>
internal static class GeneratedSerializerDispatch
{
    private static readonly ConcurrentDictionary<Type, bool> _cache = new();

    internal static void ClearCache() => _cache.Clear();

    /// <summary>
    /// True when <paramref name="type"/> (which must implement <see cref="ISerializable"/>) should be
    /// serialized through its ISerializable implementation; false when the implementation is a
    /// source-generated one inherited from a base type, in which case the reflection path must be
    /// used to cover the subtype's own fields.
    /// </summary>
    public static bool ShouldUseISerializable(Type type)
    {
        return _cache.GetOrAdd(type, static t =>
        {
            Type? declaringType = null;
            try
            {
                InterfaceMapping map = t.GetInterfaceMap(typeof(ISerializable));
                for (int i = 0; i < map.InterfaceMethods.Length; i++)
                {
                    if (map.InterfaceMethods[i].Name == nameof(ISerializable.Serialize))
                    {
                        declaringType = map.TargetMethods[i].DeclaringType;
                        break;
                    }
                }
            }
            catch (Exception)
            {
                return true; // be conservative: keep the historical dispatch
            }

            if (declaringType == null || declaringType == t)
                return true;

            // Inherited implementation: only refuse it when it came from the source generator, whose
            // field list is fixed to the annotated (base) type.
            return !declaringType.IsDefined(typeof(GenerateSerializerAttribute), inherit: false);
        });
    }
}
