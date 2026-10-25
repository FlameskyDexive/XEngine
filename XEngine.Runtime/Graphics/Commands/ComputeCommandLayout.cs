// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace XEngine.Runtime;

/// <summary>
/// Shared payload sizes for compute / indirect opcodes. Every backend skip table and
/// the Frame Debugger must consume these exactly or the command stream desyncs.
/// </summary>
internal static class ComputeCommandLayout
{
    public static bool TrySkip(
        CommandOpcode op,
        ReadOnlySpan<byte> stream,
        ref int pos,
        IReadOnlyList<object?> objects)
    {
        switch (op)
        {
            case CommandOpcode.SetComputeShader:
                _ = objects[ReadU16(stream, ref pos)];
                return true;
            case CommandOpcode.DispatchCompute:
                pos += sizeof(uint) * 3;
                return true;
            case CommandOpcode.DispatchIndirect:
                _ = objects[ReadU16(stream, ref pos)];
                pos += sizeof(uint);
                return true;
            case CommandOpcode.DrawIndexedIndirect:
                _ = objects[ReadU16(stream, ref pos)];
                pos += 1;
                _ = objects[ReadU16(stream, ref pos)];
                pos += sizeof(uint);
                pos += 1;
                return true;
            case CommandOpcode.ResourceBarrier:
                pos += 1;
                _ = objects[ReadU16(stream, ref pos)];
                return true;
            default:
                return false;
        }
    }

    private static ushort ReadU16(ReadOnlySpan<byte> s, ref int pos)
    {
        ushort v = MemoryMarshal.Read<ushort>(s.Slice(pos));
        pos += sizeof(ushort);
        return v;
    }
}
