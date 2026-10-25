// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Runtime.InteropServices;
using System.Buffers.Binary;
using XEngine.Slang;
using Xunit;
using Xunit.Abstractions;

namespace XEngine.Runtime.Test;

public sealed class SlangCompilerOptionMarshallingTests(ITestOutputHelper output)
{
    [Fact]
    public void NativeOptionEntry_PreservesNestedValueAlignmentAndArrayStride()
    {
        Type entry = typeof(GlobalSession).Assembly.GetType("XEngine.Slang.Native.CompilerOptionEntry", throwOnError: true)!;
        int expectedSize = IntPtr.Size == 8 ? 40 : 24;
        int expectedValueOffset = IntPtr.Size == 8 ? 8 : 4;
        output.WriteLine($"Native CompilerOptionEntry: size={Marshal.SizeOf(entry)}, expected={expectedSize}");
        Assert.Equal(expectedSize, Marshal.SizeOf(entry));
        Assert.Equal(0, Marshal.OffsetOf(entry, "Name").ToInt32());
        Assert.Equal(expectedValueOffset, Marshal.OffsetOf(entry, "Value").ToInt32());

        Type value = entry.GetField("Value")!.FieldType;
        Assert.Equal(IntPtr.Size == 8 ? 32 : 20, Marshal.SizeOf(value));
        Assert.Equal(0, Marshal.OffsetOf(value, "Kind").ToInt32());
        Assert.Equal(4, Marshal.OffsetOf(value, "IntValue0").ToInt32());
        Assert.Equal(8, Marshal.OffsetOf(value, "IntValue1").ToInt32());
        Assert.Equal(IntPtr.Size == 8 ? 16 : 12, Marshal.OffsetOf(value, "StringValue0").ToInt32());
        Assert.Equal(IntPtr.Size == 8 ? 24 : 16, Marshal.OffsetOf(value, "StringValue1").ToInt32());
    }

    [Fact]
    public void NativeOptions_KeepStringMacrosAndIntegerBindingShiftsAcrossEntries()
    {
        var description = new SessionDescription
        {
            CompilerOptionEntries =
            [
                new() { Name = CompilerOptionName.MacroDefine, Value = new()
                    { Kind = CompilerOptionValueKind.String, StringValue0 = "TEST_FIRST", StringValue1 = "3" } },
                new() { Name = CompilerOptionName.MacroDefine, Value = new()
                    { Kind = CompilerOptionValueKind.String, StringValue0 = "TEST_SECOND", StringValue1 = "7" } },
            ],
            Targets =
            [
                new() { Format = CompileTarget.Spirv, Profile = GlobalSession.FindProfile("spirv_1_5"),
                    CompilerOptionEntries =
                    [
                        new() { Name = CompilerOptionName.VulkanBindShiftAll, Value = new()
                            { Kind = CompilerOptionValueKind.Int, IntValue0 = 3, IntValue1 = 3 } },
                        new() { Name = CompilerOptionName.VulkanBindShiftAll, Value = new()
                            { Kind = CompilerOptionValueKind.Int, IntValue0 = 2, IntValue1 = 64 } },
                    ] },
            ],
        };
        for (int iteration = 0; iteration < 3; iteration++)
        {
            using Session session = GlobalSession.CreateSession(description);
            using Module module = session.LoadModuleFromSourceString("options", "options.slang", """
                #if TEST_FIRST != 3 || TEST_SECOND != 7
                #error Compiler option strings were lost
                #endif
                cbuffer Parameters : register(b1) { uint offset; };
                Texture2D<uint> InputTex : register(t2);
                RWStructuredBuffer<uint> Output : register(u0);
                [numthreads(1, 1, 1)]
                void main() { Output[0] = offset + InputTex.Load(int3(0, 0, 0)) + TEST_FIRST + TEST_SECOND; }
                """, out _);
            using EntryPoint entry = module.FindAndCheckEntryPoint("main", ShaderStage.Compute, out _);
            using ComponentType program = session.CreateCompositeComponentType([module, entry], out _);
            byte[] code = program.GetEntryPointCode(0, 0, out _).ToArray();
            Assert.Equal(0x07230203u, BinaryPrimitives.ReadUInt32LittleEndian(code));
            Assert.Equal(new uint[] { 0, 4, 66 }, ReadSpirvBindings(code));
            output.WriteLine($"Session {iteration}: native Slang {GlobalSession.GetBuildTagString()}, SPIR-V {code.Length} bytes, bindings=0/4/66");
        }
    }

    internal static SortedSet<uint> ReadSpirvBindings(byte[] code)
    {
        var bindings = new SortedSet<uint>();
        for (int word = 5; word < code.Length / 4;)
        {
            uint instruction = BinaryPrimitives.ReadUInt32LittleEndian(code.AsSpan(word * 4));
            int count = (int)(instruction >> 16);
            Assert.True(count > 0);
            Assert.True(word + count <= code.Length / 4);
            if ((instruction & 0xffff) == 71 && count == 4
                && BinaryPrimitives.ReadUInt32LittleEndian(code.AsSpan((word + 2) * 4)) == 33)
                bindings.Add(BinaryPrimitives.ReadUInt32LittleEndian(code.AsSpan((word + 3) * 4)));
            word += count;
        }
        return bindings;
    }
}
