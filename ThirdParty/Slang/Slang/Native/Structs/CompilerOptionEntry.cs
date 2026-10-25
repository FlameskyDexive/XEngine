// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Runtime.InteropServices;


namespace XEngine.Slang.Native;


[StructLayout(LayoutKind.Sequential)]
internal unsafe struct CompilerOptionEntry
{
    public CompilerOptionName Name;
    // Keep the nested C++ value: its pointer alignment inserts padding after
    // Name on 64-bit hosts. Flattening the fields changes offsets and array stride.
    public CompilerOptionValue Value;


    public CompilerOptionEntry Allocate(Slang.CompilerOptionEntry src)
    {
        Name = src.Name;
        Value = new CompilerOptionValue
        {
            Kind = src.Value.Kind,
            IntValue0 = src.Value.IntValue0,
            IntValue1 = src.Value.IntValue1,
        };

        if (src.Value.StringValue0 != null)
            Value.StringValue0 = U8Str.Alloc(src.Value.StringValue0);

        if (src.Value.StringValue1 != null)
            Value.StringValue1 = U8Str.Alloc(src.Value.StringValue1);

        return this;
    }


    public void Free()
    {
        if (Value.StringValue0.Data != null)
            NativeMemory.Free(Value.StringValue0.Data);

        if (Value.StringValue1.Data != null)
            NativeMemory.Free(Value.StringValue1.Data);

        Value.StringValue0.Data = null;
        Value.StringValue1.Data = null;
    }


    public Slang.CompilerOptionEntry Read()
    {
        return new()
        {
            Name = Name,
            Value = new()
            {
                Kind = Value.Kind,
                IntValue0 = Value.IntValue0,
                IntValue1 = Value.IntValue1,
                StringValue0 = Value.StringValue0.String,
                StringValue1 = Value.StringValue1.String
            }
        };
    }
}

[StructLayout(LayoutKind.Sequential)]
internal unsafe struct CompilerOptionValue
{
    public CompilerOptionValueKind Kind;
    public int IntValue0;
    public int IntValue1;
    public ConstU8Str StringValue0;
    public ConstU8Str StringValue1;
}
