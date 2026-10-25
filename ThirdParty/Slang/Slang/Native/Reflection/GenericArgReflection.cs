// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Runtime.InteropServices;

namespace XEngine.Slang.Native;


[StructLayout(LayoutKind.Explicit)]
internal unsafe struct GenericArgReflection
{
    [FieldOffset(0)]
    public TypeReflection* typeVal;

    [FieldOffset(0)]
    public long intVal;

    [FieldOffset(0)]
    public CBool boolVal;
}
