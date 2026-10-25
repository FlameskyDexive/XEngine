using System;
using System.Collections.Generic;

using System.Reflection;
using System.Reflection.Emit;

namespace XEngine.ILInterpreter.VM;

public enum Op : ushort
{
    Nop,
    Ldarg0, Ldarg1, Ldarg2, Ldarg3, LdargS, Ldarg, LdargaS, StargS, Starg,
    Ldloc0, Ldloc1, Ldloc2, Ldloc3, Stloc0, Stloc1, Stloc2, Stloc3,
    LdlocS, Ldloc, LdlocaS, StlocS, Stloc,
    LdcI4M1, LdcI40, LdcI41, LdcI42, LdcI43, LdcI44, LdcI45, LdcI46, LdcI47, LdcI48,
    LdcI4S, LdcI4, LdcI8, LdcR4, LdcR8, LdNull, LdStr,
    Dup, Pop,
    Add, Sub, Mul, Div, DivUn, Rem, RemUn, Neg,
    AddOvf, AddOvfUn, SubOvf, SubOvfUn, MulOvf, MulOvfUn,
    And, Or, Xor, Not, Shl, Shr, ShrUn,
    ConvI, ConvI1, ConvI2, ConvI4, ConvI8, ConvU1, ConvU2, ConvU4, ConvU8, ConvR4, ConvR8, ConvU,
    Ceq, Cgt, CgtUn, Clt, CltUn,
    Br, BrS, BrTrue, BrTrueS, BrFalse, BrFalseS,
    Beq, BeqS, Bge, BgeS, Bgt, BgtS, Ble, BleS, Blt, BltS,
    BneUn, BneUnS, BgeUn, BgeUnS, BgtUn, BgtUnS, BleUn, BleUnS, BltUn, BltUnS,
    Switch, Leave, LeaveS, Endfinally, Rethrow, Throw,
    Call, CallVirt, NewObj, Constrained,
    LdFld, LdFldA, StFld, LdSFld, StSFld, InitObj,
    NewArr, LdLen, LdElemA,
    LdElemI1, LdElemU1, LdElemI2, LdElemU2, LdElemI4, LdElemU4, LdElemI8, LdElemR4, LdElemR8, LdElemRef,
    StElemI, StElemI1, StElemI2, StElemI4, StElemI8, StElemR4, StElemR8, StElemRef,
    LdIndI1, LdIndU1, LdIndI2, LdIndU2, LdIndI4, LdIndU4, LdIndI8, LdIndR4, LdIndR8, LdIndRef,
    StIndI, StIndI1, StIndI2, StIndI4, StIndI8, StIndR4, StIndR8, StIndRef,
    Box, UnboxAny, IsInst, CastClass, LdFtn, Ret,
    Unsupported,
}

/// <summary>One pre-decoded instruction. Branch targets are resolved to instruction indexes.</summary>
public sealed class Instr
{
    public Op Op;
    public long I;
    public double F;
    public int[] SwitchTargets = Array.Empty<int>();
    public string? Reason;
}

/// <summary>
/// IL decoder. The opcode table is built from <see cref="OpCodes"/> static
/// metadata (name + operand kind) — no reflection emit, no codegen; the tables
/// are read-only data consulted at load time. This keeps ECMA-335 mnemonic and
/// operand-size facts from being hand-transcribed.
/// </summary>
public static class ILDecoder
{
    private sealed class Entry
    {
        public Op Op;
        public OperandType Operand;
        public string? Reject; // non-null → unsupported, carries the IL name
    }

    private static readonly Entry?[] OneByte = new Entry?[256];
    private static readonly Entry?[] TwoByte = new Entry?[256];

    private static readonly Dictionary<string, Op> NameMap = new()
    {
        ["nop"] = Op.Nop,
        ["ldarg.0"] = Op.Ldarg0, ["ldarg.1"] = Op.Ldarg1, ["ldarg.2"] = Op.Ldarg2, ["ldarg.3"] = Op.Ldarg3,
        ["ldloc.0"] = Op.Ldloc0, ["ldloc.1"] = Op.Ldloc1, ["ldloc.2"] = Op.Ldloc2, ["ldloc.3"] = Op.Ldloc3,
        ["stloc.0"] = Op.Stloc0, ["stloc.1"] = Op.Stloc1, ["stloc.2"] = Op.Stloc2, ["stloc.3"] = Op.Stloc3,
        ["ldarg.s"] = Op.LdargS, ["ldarga.s"] = Op.LdargaS, ["starg.s"] = Op.StargS,
        ["ldloc.s"] = Op.LdlocS, ["ldloca.s"] = Op.LdlocaS, ["stloc.s"] = Op.StlocS,
        ["ldnull"] = Op.LdNull,
        ["ldc.i4.m1"] = Op.LdcI4M1,
        ["ldc.i4.0"] = Op.LdcI40, ["ldc.i4.1"] = Op.LdcI41, ["ldc.i4.2"] = Op.LdcI42, ["ldc.i4.3"] = Op.LdcI43,
        ["ldc.i4.4"] = Op.LdcI44, ["ldc.i4.5"] = Op.LdcI45, ["ldc.i4.6"] = Op.LdcI46, ["ldc.i4.7"] = Op.LdcI47,
        ["ldc.i4.8"] = Op.LdcI48,
        ["ldc.i4.s"] = Op.LdcI4S, ["ldc.i4"] = Op.LdcI4, ["ldc.i8"] = Op.LdcI8,
        ["ldc.r4"] = Op.LdcR4, ["ldc.r8"] = Op.LdcR8,
        ["dup"] = Op.Dup, ["pop"] = Op.Pop,
        ["ldarg"] = Op.Ldarg, ["starg"] = Op.Starg, ["ldloc"] = Op.Ldloc, ["stloc"] = Op.Stloc,
        ["add"] = Op.Add, ["sub"] = Op.Sub, ["mul"] = Op.Mul,
        ["div"] = Op.Div, ["div.un"] = Op.DivUn, ["rem"] = Op.Rem, ["rem.un"] = Op.RemUn,
        ["and"] = Op.And, ["or"] = Op.Or, ["xor"] = Op.Xor,
        ["shl"] = Op.Shl, ["shr"] = Op.Shr, ["shr.un"] = Op.ShrUn, ["neg"] = Op.Neg, ["not"] = Op.Not,
        ["add.ovf"] = Op.AddOvf, ["add.ovf.un"] = Op.AddOvfUn,
        ["sub.ovf"] = Op.SubOvf, ["sub.ovf.un"] = Op.SubOvfUn,
        ["mul.ovf"] = Op.MulOvf, ["mul.ovf.un"] = Op.MulOvfUn,
        ["conv.i"] = Op.ConvI, ["conv.i1"] = Op.ConvI1, ["conv.i2"] = Op.ConvI2, ["conv.i4"] = Op.ConvI4,
        ["conv.i8"] = Op.ConvI8, ["conv.u1"] = Op.ConvU1, ["conv.u2"] = Op.ConvU2, ["conv.u4"] = Op.ConvU4,
        ["conv.u8"] = Op.ConvU8, ["conv.r4"] = Op.ConvR4, ["conv.r8"] = Op.ConvR8, ["conv.u"] = Op.ConvU,
        ["ceq"] = Op.Ceq, ["cgt"] = Op.Cgt, ["cgt.un"] = Op.CgtUn, ["clt"] = Op.Clt, ["clt.un"] = Op.CltUn,
        ["br"] = Op.Br, ["br.s"] = Op.BrS,
        ["brfalse"] = Op.BrFalse, ["brfalse.s"] = Op.BrFalseS,
        ["brtrue"] = Op.BrTrue, ["brtrue.s"] = Op.BrTrueS,
        ["beq"] = Op.Beq, ["beq.s"] = Op.BeqS,
        ["bge"] = Op.Bge, ["bge.s"] = Op.BgeS,
        ["bgt"] = Op.Bgt, ["bgt.s"] = Op.BgtS,
        ["ble"] = Op.Ble, ["ble.s"] = Op.BleS,
        ["blt"] = Op.Blt, ["blt.s"] = Op.BltS,
        ["bne.un"] = Op.BneUn, ["bne.un.s"] = Op.BneUnS,
        ["bge.un"] = Op.BgeUn, ["bge.un.s"] = Op.BgeUnS,
        ["bgt.un"] = Op.BgtUn, ["bgt.un.s"] = Op.BgtUnS,
        ["ble.un"] = Op.BleUn, ["ble.un.s"] = Op.BleUnS,
        ["blt.un"] = Op.BltUn, ["blt.un.s"] = Op.BltUnS,
        ["switch"] = Op.Switch,
        ["leave"] = Op.Leave, ["leave.s"] = Op.LeaveS,
        ["endfinally"] = Op.Endfinally,
        ["rethrow"] = Op.Rethrow, ["throw"] = Op.Throw,
        ["call"] = Op.Call, ["callvirt"] = Op.CallVirt, ["newobj"] = Op.NewObj,
        ["constrained."] = Op.Constrained,
        ["ldfld"] = Op.LdFld, ["ldflda"] = Op.LdFldA, ["stfld"] = Op.StFld, ["ldsfld"] = Op.LdSFld, ["stsfld"] = Op.StSFld,
        ["initobj"] = Op.InitObj,
        ["newarr"] = Op.NewArr, ["ldlen"] = Op.LdLen, ["ldelema"] = Op.LdElemA,
        ["ldelem"] = Op.LdElemRef,
        ["ldelem.i1"] = Op.LdElemI1, ["ldelem.u1"] = Op.LdElemU1,
        ["ldelem.i2"] = Op.LdElemI2, ["ldelem.u2"] = Op.LdElemU2,
        ["ldelem.i4"] = Op.LdElemI4, ["ldelem.u4"] = Op.LdElemU4,
        ["ldelem.i8"] = Op.LdElemI8, ["ldelem.r4"] = Op.LdElemR4, ["ldelem.r8"] = Op.LdElemR8,
        ["ldelem.ref"] = Op.LdElemRef,
        ["stelem.i"] = Op.StElemI, ["stelem.i1"] = Op.StElemI1, ["stelem.i2"] = Op.StElemI2,
        ["stelem.i4"] = Op.StElemI4, ["stelem.i8"] = Op.StElemI8,
        ["stelem.r4"] = Op.StElemR4, ["stelem.r8"] = Op.StElemR8, ["stelem.ref"] = Op.StElemRef,
        ["stelem"] = Op.StElemRef,
        ["ldind.i1"] = Op.LdIndI1, ["ldind.u1"] = Op.LdIndU1, ["ldind.i2"] = Op.LdIndI2,
        ["ldind.u2"] = Op.LdIndU2, ["ldind.i4"] = Op.LdIndI4, ["ldind.u4"] = Op.LdIndU4,
        ["ldind.i8"] = Op.LdIndI8, ["ldind.r4"] = Op.LdIndR4, ["ldind.r8"] = Op.LdIndR8,
        ["ldind.ref"] = Op.LdIndRef, ["ldind.i"] = Op.LdIndI8,
        ["stind.i"] = Op.StIndI, ["stind.i1"] = Op.StIndI1, ["stind.i2"] = Op.StIndI2,
        ["stind.i4"] = Op.StIndI4, ["stind.i8"] = Op.StIndI8, ["stind.r4"] = Op.StIndR4,
        ["stind.r8"] = Op.StIndR8, ["stind.ref"] = Op.StIndRef,
        ["box"] = Op.Box, ["unbox.any"] = Op.UnboxAny,
        ["isinst"] = Op.IsInst, ["castclass"] = Op.CastClass, ["ldstr"] = Op.LdStr,
        ["ldftn"] = Op.LdFtn, ["ret"] = Op.Ret,
    };

    static ILDecoder()
    {
        var fields = typeof(OpCodes).GetFields(BindingFlags.Public | BindingFlags.Static);
        foreach (var f in fields)
        {
            if (f.GetValue(null) is not OpCode oc) continue;
            int code = oc.Value & 0xFF;
            var table = (oc.Value & 0xFF00) != 0 ? TwoByte : OneByte;
            string name = oc.Name!;
            if (NameMap.TryGetValue(name, out var op))
            {
                table[code] = new Entry { Op = op, Operand = oc.OperandType };
            }
            else
            {
                table[code] = new Entry { Op = Op.Unsupported, Operand = oc.OperandType, Reject = name };
            }
        }
    }

    public static (Instr[] code, int[] offsets) Decode(byte[] il)
    {
        var offsets = new int[il.Length + 1];
        for (int i = 0; i < offsets.Length; i++) offsets[i] = -1;

        var pass1 = new List<(int offset, Entry e, int operandPos, byte[]? switchHeader)>();
        int pos = 0;

        while (pos < il.Length)
        {
            int offset = pos;
            int b = il[pos++];
            Entry? e;
            int operandPos;
            byte[]? switchHeader = null;

            if (b == 0xFE)
            {
                if (pos >= il.Length) throw new ILError("truncated 0xFE opcode");
                int b2 = il[pos++];
                e = TwoByte[b2] ?? new Entry { Op = Op.Unsupported, Operand = OperandType.InlineNone, Reject = $"0xFE 0x{b2:X2}" };
                operandPos = pos;
            }
            else
            {
                e = OneByte[b] ?? new Entry { Op = Op.Unsupported, Operand = OperandType.InlineNone, Reject = $"0x{b:X2}" };
                operandPos = pos;
            }

            int operandLen = 0;
            if (e.Operand == OperandType.InlineSwitch)
            {
                if (operandPos + 4 > il.Length) throw new ILError($"truncated switch at 0x{offset:X}");
                uint n = BitConverter.ToUInt32(il, operandPos);
                if (n > 4096) throw new ILError($"switch target count {n} excessive at 0x{offset:X}");
                operandLen = (int)(n * 4 + 4);
                switchHeader = new byte[operandLen];
                Buffer.BlockCopy(il, operandPos, switchHeader, 0, operandLen);
            }
            else
            {
                operandLen = e.Operand switch
                {
                    OperandType.InlineNone => 0,
                    OperandType.ShortInlineBrTarget => 1,
                    OperandType.ShortInlineI => 1,
                    OperandType.ShortInlineVar => 1,
                    OperandType.InlineVar => 2,
                    OperandType.InlineBrTarget => 4,
                    OperandType.InlineField => 4,
                    OperandType.InlineMethod => 4,
                    OperandType.InlineTok => 4,
                    OperandType.InlineType => 4,
                    OperandType.InlineSig => 4,
                    OperandType.InlineString => 4,
                    OperandType.InlineI => 4,
                    OperandType.InlineI8 => 8,
                    OperandType.ShortInlineR => 4,
                    OperandType.InlineR => 8,
                    OperandType.InlineSwitch => 0,
                    _ => throw new ILError($"unknown operand kind {e.Operand}"),
                };
            }

            pass1.Add((offset, e, operandPos, switchHeader));
            offsets[offset] = pass1.Count - 1;
            pos += operandLen;
        }

        var code = new Instr[pass1.Count];
        for (int i = 0; i < pass1.Count; i++)
        {
            var (offset, e, operandPos, switchHeader) = pass1[i];
            var ins = new Instr { Op = e.Op, Reason = e.Reject };

            if (e.Op == Op.Unsupported)
            {
                code[i] = ins;
                continue;
            }

            switch (e.Operand)
            {
                case OperandType.InlineSwitch:
                {
                    int n = switchHeader!.Length / 4 - 1;
                    var targets = new int[n];
                    int baseOffset = operandPos + switchHeader.Length;
                    for (int t = 0; t < n; t++)
                    {
                        int delta = BitConverter.ToInt32(switchHeader, 4 + t * 4);
                        targets[t] = Resolve(offsets, baseOffset + delta);
                    }
                    ins.SwitchTargets = targets;
                    break;
                }
                case OperandType.ShortInlineBrTarget:
                    // delta is relative to the end of the instruction (opcode + operand)
                    ins.I = Resolve(offsets, offset + 2 + (sbyte)il[operandPos]);
                    break;
                case OperandType.InlineBrTarget:
                    ins.I = Resolve(offsets, offset + 5 + BitConverter.ToInt32(il, operandPos));
                    break;
                default:
                    switch (e.Operand)
                    {
                        case OperandType.ShortInlineI: ins.I = (sbyte)il[operandPos]; break;
                        case OperandType.ShortInlineVar: ins.I = il[operandPos]; break;
                        case OperandType.InlineVar: ins.I = BitConverter.ToUInt16(il, operandPos); break;
                        case OperandType.InlineI: ins.I = BitConverter.ToInt32(il, operandPos); break;
                        case OperandType.InlineI8: ins.I = BitConverter.ToInt64(il, operandPos); break;
                        case OperandType.ShortInlineR: ins.F = BitConverter.ToSingle(il, operandPos); break;
                        case OperandType.InlineR: ins.F = BitConverter.ToDouble(il, operandPos); break;
                        case OperandType.InlineString:
                        case OperandType.InlineMethod:
                        case OperandType.InlineField:
                        case OperandType.InlineType:
                        case OperandType.InlineTok:
                        case OperandType.InlineSig:
                            ins.I = BitConverter.ToInt32(il, operandPos);
                            break;
                    }
                    break;
            }

            code[i] = ins;
        }

        return (code, offsets);
    }

    private static int Resolve(int[] offsets, int targetOffset)
    {
        if (targetOffset < 0 || targetOffset >= offsets.Length || offsets[targetOffset] < 0)
        {
            throw new ILError($"branch target 0x{targetOffset:X} outside method body");
        }
        return offsets[targetOffset];
    }
}
