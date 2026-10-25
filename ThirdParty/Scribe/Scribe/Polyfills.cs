// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

#if NETSTANDARD2_1
// Polyfills for net6+/net8+ APIs that the netstandard2.1 reference pack lacks
// (same approach as ThirdParty/Echo/Echo/Polyfills.cs).

namespace System.Runtime.CompilerServices
{
    // System.Runtime.CompilerServices.Unsafe ships in-box on net5+ but is missing from the
    // netstandard2.1 reference pack. Only SizeOf is needed, and only to weight LRU cache
    // entries: ref-containing structs report IntPtr.Size (a lower bound), blittable structs
    // report their exact marshaled size.
    internal static class Unsafe
    {
        public static int SizeOf<T>() =>
            RuntimeHelpers.IsReferenceOrContainsReferences<T>()
                ? IntPtr.Size
                : System.Runtime.InteropServices.Marshal.SizeOf<T>();
    }
}
#endif
