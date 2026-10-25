using System;
using System.Collections.Generic;

using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using XEngine.ILInterpreter.Binding;
using XEngine.ILInterpreter.Loading;

namespace XEngine.ILInterpreter.VM;

/// <summary>One interpreted call frame.</summary>
public sealed class Frame
{
    public ILMethod M = default!;
    public Slot[] Args = Array.Empty<Slot>();
    public Slot[] Locals = Array.Empty<Slot>();
    public Slot[] Stack = new Slot[32];
    public int Esp;
    public int Ip;                   // instruction index (for EH + diagnostics)
    public ILThrow? PendingThrow;    // set while a finally runs during unwind
    public object? CurrentException; // exception object handled by the active catch
    public HashSet<ILEhClause> RanFinally = new();

    public void Push(Slot s)
    {
        if (Esp == Stack.Length)
        {
            Array.Resize(ref Stack, Stack.Length * 2);
        }
        Stack[Esp++] = s;
    }

    public Slot Pop()
    {
        if (Esp == 0) throw new ILError("eval stack underflow");
        return Stack[--Esp];
    }

    public Slot Peek() => Stack[Esp - 1];
}

public static class Interpreter
{
    public static Slot CallMethod(ILMethod m, Slot[] args, ILDomain d, int depth)
    {
        if (depth > d.MaxCallDepth)
        {
            throw new ILError($"call depth limit {d.MaxCallDepth} exceeded");
        }

        var f = new Frame
        {
            M = m,
            Args = args,
            Locals = new Slot[m.LocalCount],
        };

        int ip = 0;
        while (true)
        {
            try
            {
                return RunLoop(d, m, f, depth, ip, constrained: false);
            }
            catch (ILThrow t)
            {
                if (!TryUnwind(d, m, f, depth, t))
                {
                    throw;
                }
                ip = f.Ip;
            }
            catch (ILError e)
            {
                if (e.MethodName is null)
                {
                    e.At(m.DisplayName, OffsetOf(m, f.Ip));
                }
                throw;
            }
        }
    }

    private static int OffsetOf(ILMethod m, int index)
        => index >= 0 && index < m.IlOffsets.Length ? m.IlOffsets[index] : -1;

    /// <summary>
    /// Finds and runs the handler for an in-flight exception in this frame.
    /// Returns true when a catch handled it (execution continues at f.Ip);
    /// false means the exception leaves this frame.
    /// </summary>
    private static bool TryUnwind(ILDomain d, ILMethod m, Frame f, int depth, ILThrow t)
    {
        while (true)
        {
            ILEhClause? found = null;
            int bestLen = int.MaxValue;
            foreach (var c in m.Ehs)
            {
                if (f.RanFinally.Contains(c)) continue;
                if (c.TryStart > f.Ip || f.Ip >= c.TryEnd) continue;
                int len = c.TryEnd - c.TryStart;
                if (len < bestLen)
                {
                    bestLen = len;
                    found = c;
                }
            }

            if (found is null)
            {
                return false; // propagate to caller frame
            }

            var clause = found;
            if (clause.Flags == ILEhFlags.Exception)
            {
                if (CatchMatches(d, clause.ClassToken, t.Value))
                {
                    f.Esp = 0;
                    f.CurrentException = t.Value;
                    f.RanFinally.Clear();
                    f.Push(Slot.Ref(t.Value));
                    f.Ip = clause.HandlerStart;
                    return true;
                }

                // catch mismatch: keep searching outward from the region end
                f.Ip = clause.TryEnd;
                continue;
            }

            // finally / fault: run it, then keep unwinding outside the region.
            int throwIp = f.Ip;
            f.RanFinally.Add(clause);
            f.PendingThrow = t;
            RunLoop(d, m, f, depth, clause.HandlerStart, constrained: false);
            f.PendingThrow = null;
            f.Ip = throwIp;
        }
    }

    private static bool CatchMatches(ILDomain d, int classToken, object value)
    {
        var ctx = TypeResolutionContext.Empty;
        var t = d.ResolveTypeToken(classToken, ctx);

        if (value is ILObject vi)
        {
            if (ReferenceEquals(vi.Type, t) || vi.Type.IsAssignableTo(t)) return true;
            return t.HostType is not null && vi.Type.ChainHasHost(t.HostType);
        }

        if (value is Exception he)
        {
            return t.HostType is not null && t.HostType.IsInstanceOfType(he);
        }

        return false;
    }

    /// <summary>Runs enclosing finallys (innermost first) for a leave, then returns the target ip.</summary>
    private static int RunLeave(ILDomain d, ILMethod m, Frame f, int depth, int curIp, int target)
    {
        while (true)
        {
            ILEhClause? found = null;
            int bestLen = int.MaxValue;
            foreach (var c in m.Ehs)
            {
                if (c.Flags is not (ILEhFlags.Finally or ILEhFlags.Fault)) continue;
                if (f.RanFinally.Contains(c)) continue;
                if (c.TryStart > curIp || curIp >= c.TryEnd) continue;
                int len = c.TryEnd - c.TryStart;
                if (len < bestLen)
                {
                    bestLen = len;
                    found = c;
                }
            }

            if (found is null)
            {
                f.RanFinally.Clear();
                return target;
            }

            f.RanFinally.Add(found);
            RunLoop(d, m, f, depth, found.HandlerStart, constrained: false);
        }
    }

    /// <summary>Runs until ret, or the end of a finally region (returns default).</summary>
    private static Slot RunLoop(ILDomain d, ILMethod m, Frame f, int depth, int ip, bool constrained)
    {
        var code = m.Code;

        while (ip >= 0 && ip < code.Length)
        {
            var ins = code[ip];
            f.Ip = ip;

            if (++d.InstructionCount > d.MaxInstructions)
            {
                throw new ILError($"instruction budget {d.MaxInstructions} exhausted");
            }

            switch (ins.Op)
            {
                // ---- loads / stores ------------------------------------------------
                case Op.Nop: ip++; break;
                case Op.Ldarg0: f.Push(f.Args[0]); ip++; break;
                case Op.Ldarg1: f.Push(f.Args[1]); ip++; break;
                case Op.Ldarg2: f.Push(f.Args[2]); ip++; break;
                case Op.Ldarg3: f.Push(f.Args[3]); ip++; break;
                case Op.LdargS:
                case Op.Ldarg: f.Push(f.Args[(int)ins.I]); ip++; break;
                case Op.LdargaS: f.Push(Slot.ByRef(ILRef.ToSlots(f.Args, (int)ins.I))); ip++; break;
                case Op.StargS:
                case Op.Starg: f.Args[(int)ins.I] = CopyValue(f.Pop()); ip++; break;
                case Op.Ldloc0: f.Push(f.Locals[0]); ip++; break;
                case Op.Ldloc1: f.Push(f.Locals[1]); ip++; break;
                case Op.Ldloc2: f.Push(f.Locals[2]); ip++; break;
                case Op.Ldloc3: f.Push(f.Locals[3]); ip++; break;
                case Op.LdlocS:
                case Op.Ldloc: f.Push(f.Locals[(int)ins.I]); ip++; break;
                case Op.LdlocaS: f.Push(Slot.ByRef(ILRef.ToSlots(f.Locals, (int)ins.I))); ip++; break;
                case Op.Stloc0: f.Locals[0] = CopyValue(f.Pop()); ip++; break;
                case Op.Stloc1: f.Locals[1] = CopyValue(f.Pop()); ip++; break;
                case Op.Stloc2: f.Locals[2] = CopyValue(f.Pop()); ip++; break;
                case Op.Stloc3: f.Locals[3] = CopyValue(f.Pop()); ip++; break;
                case Op.StlocS:
                case Op.Stloc: f.Locals[(int)ins.I] = CopyValue(f.Pop()); ip++; break;

                case Op.LdcI4M1: f.Push(Slot.I4(-1)); ip++; break;
                case Op.LdcI40: f.Push(Slot.I4(0)); ip++; break;
                case Op.LdcI41: f.Push(Slot.I4(1)); ip++; break;
                case Op.LdcI42: f.Push(Slot.I4(2)); ip++; break;
                case Op.LdcI43: f.Push(Slot.I4(3)); ip++; break;
                case Op.LdcI44: f.Push(Slot.I4(4)); ip++; break;
                case Op.LdcI45: f.Push(Slot.I4(5)); ip++; break;
                case Op.LdcI46: f.Push(Slot.I4(6)); ip++; break;
                case Op.LdcI47: f.Push(Slot.I4(7)); ip++; break;
                case Op.LdcI48: f.Push(Slot.I4(8)); ip++; break;
                case Op.LdcI4S:
                case Op.LdcI4: f.Push(Slot.I4((int)ins.I)); ip++; break;
                case Op.LdcI8: f.Push(Slot.I8(ins.I)); ip++; break;
                case Op.LdcR4: f.Push(Slot.R4((float)ins.F)); ip++; break;
                case Op.LdcR8: f.Push(Slot.R8(ins.F)); ip++; break;
                case Op.LdNull: f.Push(Slot.Null); ip++; break;
                case Op.LdStr: f.Push(Slot.Ref(ResolveString(d, m, ip, (int)ins.I))); ip++; break;

                case Op.Dup:
                {
                    var v = f.Peek();
                    f.Push(CopyValue(v));
                    ip++;
                    break;
                }
                case Op.Pop: _ = f.Pop(); ip++; break;

                // ---- arithmetic ------------------------------------------------------
                case Op.Add: { var b = f.Pop(); var a = f.Pop(); f.Push(NumAdd(a, b)); ip++; break; }
                case Op.Sub: { var b = f.Pop(); var a = f.Pop(); f.Push(NumSub(a, b)); ip++; break; }
                case Op.Mul: { var b = f.Pop(); var a = f.Pop(); f.Push(NumMul(a, b)); ip++; break; }
                case Op.Div: { var b = f.Pop(); var a = f.Pop(); f.Push(NumDiv(a, b)); ip++; break; }
                case Op.DivUn: { var b = f.Pop(); var a = f.Pop(); f.Push(Slot.I4((int)((uint)a.AsI4() / (uint)b.AsI4()))); ip++; break; }
                case Op.Rem: { var b = f.Pop(); var a = f.Pop(); f.Push(NumRem(a, b)); ip++; break; }
                case Op.RemUn: { var b = f.Pop(); var a = f.Pop(); f.Push(Slot.I4((int)((uint)a.AsI4() % (uint)b.AsI4()))); ip++; break; }
                case Op.Neg:
                {
                    var a = f.Pop();
                    f.Push(a.Kind switch
                    {
                        ILSlotKind.I4 => Slot.I4(unchecked(-(int)a.I)),
                        ILSlotKind.R4 => Slot.R4(-(float)a.F),
                        ILSlotKind.R8 => Slot.R8(-a.F),
                        _ => Slot.I8(-a.I),
                    });
                    ip++;
                    break;
                }
                case Op.AddOvf:
                {
                    var b = f.Pop(); var a = f.Pop();
                    try
                    {
                        if (a.Kind == ILSlotKind.I4 && b.Kind == ILSlotKind.I4)
                        {
                            f.Push(Slot.I4(checked((int)a.I + (int)b.I)));
                        }
                        else
                        {
                            f.Push(Slot.I8(checked(a.I + b.I)));
                        }
                    }
                    catch (OverflowException ex)
                    {
                        throw new ILThrow(ex);
                    }
                    ip++;
                    break;
                }
                case Op.And: { var b = f.Pop(); var a = f.Pop(); f.Push(Int2(a.I & b.I, a.Kind)); ip++; break; }
                case Op.Or: { var b = f.Pop(); var a = f.Pop(); f.Push(Int2(a.I | b.I, a.Kind)); ip++; break; }
                case Op.Xor: { var b = f.Pop(); var a = f.Pop(); f.Push(Int2(a.I ^ b.I, a.Kind)); ip++; break; }
                case Op.Not: { var a = f.Pop(); f.Push(Int2(~a.I, a.Kind)); ip++; break; }
                case Op.Shl: { var b = f.Pop(); var a = f.Pop(); f.Push(Int2(a.I << ((int)b.I & 63), a.Kind)); ip++; break; }
                case Op.Shr: { var b = f.Pop(); var a = f.Pop(); f.Push(Int2(a.I >> ((int)b.I & 63), a.Kind)); ip++; break; }
                case Op.ShrUn: { var b = f.Pop(); var a = f.Pop(); f.Push(Int2((long)((ulong)a.I >> ((int)b.I & 63)), a.Kind)); ip++; break; }

                case Op.ConvI1: { var a = f.Pop(); f.Push(Slot.I4((sbyte)a.AsI4())); ip++; break; }
                case Op.ConvI2: { var a = f.Pop(); f.Push(Slot.I4((short)a.AsI4())); ip++; break; }
                case Op.ConvI4: { var a = f.Pop(); f.Push(ToInt(a)); ip++; break; }
                case Op.ConvU1: { var a = f.Pop(); f.Push(Slot.I4((byte)a.AsI4())); ip++; break; }
                case Op.ConvU2: { var a = f.Pop(); f.Push(Slot.I4((ushort)a.AsI4())); ip++; break; }
                case Op.ConvU4: { var a = f.Pop(); f.Push(Slot.I8(a.AsI4() & 0xFFFFFFFFL)); ip++; break; }
                case Op.ConvI8:
                case Op.ConvU8:
                case Op.ConvI:
                {
                    var a = f.Pop();
                    f.Push(a.Kind is ILSlotKind.R4 or ILSlotKind.R8 ? Slot.I8((long)a.AsF8()) : Slot.I8(a.I8v()));
                    ip++;
                    break;
                }
                case Op.ConvR4: { var a = f.Pop(); f.Push(Slot.R4((float)a.AsF8())); ip++; break; }
                case Op.ConvR8: { var a = f.Pop(); f.Push(Slot.R8(a.AsF8())); ip++; break; }
                case Op.ConvU: { var a = f.Pop(); f.Push(Slot.I8(a.I8v())); ip++; break; }

                case Op.Ceq: { var b = f.Pop(); var a = f.Pop(); f.Push(Slot.I4(ValEq(a, b) ? 1 : 0)); ip++; break; }
                case Op.Cgt: { var b = f.Pop(); var a = f.Pop(); f.Push(Slot.I4(NumCmp(a, b) > 0 ? 1 : 0)); ip++; break; }
                case Op.CgtUn: { var b = f.Pop(); var a = f.Pop(); f.Push(Slot.I4(UnCmp(a, b) > 0 ? 1 : 0)); ip++; break; }
                case Op.Clt: { var b = f.Pop(); var a = f.Pop(); f.Push(Slot.I4(NumCmp(a, b) < 0 ? 1 : 0)); ip++; break; }
                case Op.CltUn: { var b = f.Pop(); var a = f.Pop(); f.Push(Slot.I4(UnCmp(a, b) < 0 ? 1 : 0)); ip++; break; }

                // ---- branches ---------------------------------------------------------
                case Op.Br:
                case Op.BrS: ip = (int)ins.I; break;
                case Op.BrTrue:
                case Op.BrTrueS: ip = Truthy(f.Pop()) ? (int)ins.I : ip + 1; break;
                case Op.BrFalse:
                case Op.BrFalseS: ip = Truthy(f.Pop()) ? ip + 1 : (int)ins.I; break;
                case Op.Beq:
                case Op.BeqS: { var b = f.Pop(); var a = f.Pop(); ip = ValEq(a, b) ? (int)ins.I : ip + 1; break; }
                case Op.BneUn:
                case Op.BneUnS: { var b = f.Pop(); var a = f.Pop(); ip = !ValEq(a, b) ? (int)ins.I : ip + 1; break; }
                case Op.Bge:
                case Op.BgeS: { var b = f.Pop(); var a = f.Pop(); ip = NumCmp(a, b) >= 0 ? (int)ins.I : ip + 1; break; }
                case Op.Bgt:
                case Op.BgtS: { var b = f.Pop(); var a = f.Pop(); ip = NumCmp(a, b) > 0 ? (int)ins.I : ip + 1; break; }
                case Op.Ble:
                case Op.BleS: { var b = f.Pop(); var a = f.Pop(); ip = NumCmp(a, b) <= 0 ? (int)ins.I : ip + 1; break; }
                case Op.Blt:
                case Op.BltS: { var b = f.Pop(); var a = f.Pop(); ip = NumCmp(a, b) < 0 ? (int)ins.I : ip + 1; break; }
                case Op.BgeUn:
                case Op.BgeUnS: { var b = f.Pop(); var a = f.Pop(); ip = UnCmp(a, b) >= 0 ? (int)ins.I : ip + 1; break; }
                case Op.BgtUn:
                case Op.BgtUnS: { var b = f.Pop(); var a = f.Pop(); ip = UnCmp(a, b) > 0 ? (int)ins.I : ip + 1; break; }
                case Op.BleUn:
                case Op.BleUnS: { var b = f.Pop(); var a = f.Pop(); ip = UnCmp(a, b) <= 0 ? (int)ins.I : ip + 1; break; }
                case Op.BltUn:
                case Op.BltUnS: { var b = f.Pop(); var a = f.Pop(); ip = UnCmp(a, b) < 0 ? (int)ins.I : ip + 1; break; }
                case Op.Switch:
                {
                    int idx = f.Pop().AsI4();
                    int target = (uint)idx < (uint)ins.SwitchTargets.Length
                        ? ins.SwitchTargets[idx]
                        : ip + 1;
                    ip = target;
                    break;
                }

                case Op.Leave:
                case Op.LeaveS:
                {
                    f.CurrentException = null;
                    ip = RunLeave(d, m, f, depth, ip, (int)ins.I);
                    break;
                }
                case Op.Endfinally:
                {
                    if (f.PendingThrow is not null)
                    {
                        // During exception unwinding the caller of RunLoop
                        // (TryUnwind) owns the in-flight throw.  Returning from
                        // the handler lets it continue searching outer clauses;
                        // throwing here would escape the active catch block in
                        // CallMethod before that search can resume.
                        return default;
                    }
                    return default; // normal leave-flow: finally region complete
                }
                case Op.Throw:
                {
                    var v = f.Pop();
                    throw new ILThrow(ThrowValue(v));
                }
                case Op.Rethrow:
                {
                    if (f.CurrentException is ILThrow t1) throw t1;
                    if (f.PendingThrow is ILThrow t2) throw t2;
                    if (f.CurrentException is not null) throw new ILThrow(f.CurrentException);
                    throw new ILError("rethrow outside handler").At(m.DisplayName, OffsetOf(m, ip));
                }
                case Op.Constrained:
                    constrained = true;
                    ip++;
                    continue;

                // ---- calls -------------------------------------------------------------
                case Op.Call:
                case Op.CallVirt:
                {
                    var result = InvokeCallSite(d, m, f, ip, ins.Op == Op.CallVirt, constrained, depth);
                    constrained = false;
                    if (result.returned)
                    {
                        f.Push(result.value);
                    }
                    ip++;
                    break;
                }
                case Op.NewObj:
                {
                    var obj = NewObject(d, m, f, ip, depth);
                    f.Push(obj);
                    ip++;
                    break;
                }

                // ---- fields ------------------------------------------------------------
                case Op.LdFld: f.Push(LoadField(d, m, f, ip)); ip++; break;
                case Op.LdFldA: f.Push(LoadFieldAddress(d, m, f, ip)); ip++; break;
                case Op.StFld: StoreField(d, m, f, ip); ip++; break;
                case Op.LdSFld: f.Push(LoadStatic(d, m, f, ip)); ip++; break;
                case Op.StSFld: StoreStatic(d, m, f, ip); ip++; break;
                case Op.InitObj:
                {
                    var address = f.Pop();
                    if (address.Kind != ILSlotKind.ByRef || address.R is null)
                    {
                        throw new ILError("initobj requires a managed pointer")
                            .At(m.DisplayName, OffsetOf(m, ip));
                    }

                    var type = ResolveTypeCached(d, m, ip, (int)ins.I);
                    address.R.Write(DefaultValue(type));
                    ip++;
                    break;
                }

                // ---- arrays --------------------------------------------------------------
                case Op.NewArr:
                {
                    int len = f.Pop().AsI4();
                    if (len < 0) throw new ILThrow(new OverflowException("array length negative"));
                    var elemType = ResolveTypeCached(d, m, ip, (int)ins.I);
                    f.Push(Slot.Ref(new ILArray(elemType, len)));
                    ip++;
                    break;
                }
                case Op.LdLen:
                {
                    var a = f.Pop();
                    if (a.O is not ILArray arr) throw ThrowNre();
                    f.Push(Slot.I4(arr.Length));
                    ip++;
                    break;
                }
                case Op.LdElemA:
                {
                    int idx = f.Pop().AsI4();
                    var arrSlot = f.Pop();
                    if (arrSlot.O is not ILArray arr) throw ThrowNre();
                    arr.CheckIndex(idx);
                    f.Push(Slot.ByRef(ILRef.ToArrayElem(arr, idx)));
                    ip++;
                    break;
                }
                case Op.LdElemI1: case Op.LdElemU1: case Op.LdElemI2: case Op.LdElemU2:
                case Op.LdElemI4: case Op.LdElemU4: case Op.LdElemI8:
                case Op.LdElemR4: case Op.LdElemR8: case Op.LdElemRef:
                {
                    int idx = f.Pop().AsI4();
                    var arrSlot = f.Pop();
                    if (arrSlot.O is not ILArray arr) throw ThrowNre();
                    f.Push(arr.LoadElement(idx));
                    ip++;
                    break;
                }
                case Op.StElemI: case Op.StElemI1: case Op.StElemI2: case Op.StElemI4:
                case Op.StElemI8: case Op.StElemR4: case Op.StElemR8: case Op.StElemRef:
                {
                    var v = f.Pop();
                    int idx = f.Pop().AsI4();
                    var arrSlot = f.Pop();
                    if (arrSlot.O is not ILArray arr) throw ThrowNre();
                    arr.StoreElement(idx, v);
                    ip++;
                    break;
                }

                case Op.LdIndI1: case Op.LdIndU1: case Op.LdIndI2: case Op.LdIndU2:
                case Op.LdIndI4: case Op.LdIndU4: case Op.LdIndI8:
                case Op.LdIndR4: case Op.LdIndR8: case Op.LdIndRef:
                {
                    var r = f.Pop().R ?? throw new ILError("ldind expects managed pointer");
                    f.Push(r.Read());
                    ip++;
                    break;
                }
                case Op.StIndI: case Op.StIndI1: case Op.StIndI2: case Op.StIndI4:
                case Op.StIndI8: case Op.StIndR4: case Op.StIndR8: case Op.StIndRef:
                {
                    var value = f.Pop();
                    var r = f.Pop().R ?? throw new ILError("stind expects managed pointer");
                    r.Write(value);
                    ip++;
                    break;
                }

                // ---- objects -----------------------------------------------------------
                case Op.Box:
                {
                    var v = f.Pop();
                    var t = ResolveTypeCached(d, m, ip, (int)ins.I);
                    f.Push(BoxValue(v, t));
                    ip++;
                    break;
                }
                case Op.UnboxAny:
                {
                    var v = f.Pop();
                    var t = ResolveTypeCached(d, m, ip, (int)ins.I);
                    f.Push(UnboxAny(v, t));
                    ip++;
                    break;
                }
                case Op.IsInst:
                {
                    var v = f.Pop();
                    var t = ResolveTypeCached(d, m, ip, (int)ins.I);
                    f.Push(TypeCheck(v, t, throwOnFail: false));
                    ip++;
                    break;
                }
                case Op.CastClass:
                {
                    var v = f.Pop();
                    var t = ResolveTypeCached(d, m, ip, (int)ins.I);
                    f.Push(TypeCheck(v, t, throwOnFail: true));
                    ip++;
                    break;
                }
                case Op.LdFtn:
                {
                    var site = CallSiteResolver.Resolve(d, m, ip);
                    var mi = site.Method ?? throw new ILError("ldftn on non-method").At(m.DisplayName, OffsetOf(m, ip));
                    f.Push(Slot.Ref(new ILDelegate { Method = mi }));
                    ip++;
                    break;
                }

                case Op.Ret:
                    return f.Esp > 0 ? f.Pop() : default;

                case Op.Unsupported:
                    throw new ILError($"unsupported IL: {ins.Reason}").At(m.DisplayName, OffsetOf(m, ip));

                default:
                    throw new ILError($"unhandled opcode {ins.Op}").At(m.DisplayName, OffsetOf(m, ip));
            }
        }

        return default; // fell off the end (void return)
    }

    // ---------------------------------------------------------------- calls

    private static (bool returned, Slot value) InvokeCallSite(
        ILDomain d, ILMethod m, Frame f, int ip, bool virt, bool constrained, int depth)
    {
        ResolvedCallSite site;
        try
        {
            site = CallSiteResolver.Resolve(d, m, ip);
        }
        catch (Exception ex) when (ex is IndexOutOfRangeException or ArgumentOutOfRangeException)
        {
            throw new ILError($"call-site metadata index error at {m.DisplayName} ip={ip} code={m.Code.Length} cache={m.TokenCache.Length}: {ex.Message}")
                .At(m.DisplayName, OffsetOf(m, ip));
        }
        int ilOffset = OffsetOf(m, ip);

        switch (site.Kind)
        {
            case CallSiteKind.HostMethod:
            {
                int ilOffset2 = ilOffset;

                // async builder interception — the builder is a host type
                if (site.HostType is not null && d.Host.BuilderTypeNames.Contains(site.HostType))
                {
                    var key = site.ClosedDecl?.Args is { Length: > 0 } ba
                        ? ba[0].FullName
                        : throw new ILError("builder TypeSpec missing result type argument").At(m.DisplayName, ilOffset2);
                    // Create and get_Task are value-returning builder members.  The
                    // other builder methods (Start, AwaitUnsafeOnCompleted,
                    // SetResult, SetException, SetStateMachine) are void and must
                    // leave no value on the evaluation stack.  Treating Create as
                    // void loses the builder value immediately before its stfld;
                    // the following stfld then pops an empty stack and reports an
                    // underflow in every async entry point.
                    bool returnsValue = site.HostName is "Create" or "get_Task";
                    return (returnsValue, InvokeBuilder(d, m, f, ip, site, key, depth));
                }

                int argc = site.HostParamCount;
                var args = PopArgs(f, argc + (site.HostHasThis ? 1 : 0));
                var selfSlot = site.HostHasThis && args.Length > 0 && args[0].Kind == ILSlotKind.ByRef
                    ? args[0].R!.Read()
                    : site.HostHasThis && args.Length > 0 ? args[0] : default;
                var self = site.HostHasThis
                    ? selfSlot.Kind is ILSlotKind.I4 or ILSlotKind.I8 or ILSlotKind.R4 or ILSlotKind.R8
                        ? selfSlot.Boxed()
                        : selfSlot.O
                    : null;
                var call = new HostBindingCall(d, site.HostHasThis ? Rest(args) : args, self);

                if (site.HostType is "System.Delegate" or "System.MulticastDelegate")
                {
                return (true, site.HostName switch
                    {
                        "Combine" => Slot.Ref(ILDelegate.Combine(AsDel(args[0]), AsDel(args[1]))),
                        "Remove" => Slot.RefOrNull(ILDelegate.Remove(AsDel(args[0]), AsDel(args[1]))),
                        _ => throw new ILError($"unsupported delegate op {site.HostName}").At(m.DisplayName, ilOffset),
                    });
                }

                // closed-generic task/awaiter surface (ProbeTask<T>, Awaiter<T>, Source<T>)
                if (site.ClosedDecl is { Def: null } hc && hc.Args.Length == 1)
                {
                    var openName = hc.OpenType?.FullName ?? hc.FullName;
                    if (d.Host.TaskFamilyTypeNames.Contains(openName ?? ""))
                    {
                        var sup = d.Host.AsyncSupport[hc.Args[0].FullName];
                        if (site.HostName == "GetResult")
                        {
                            try
                            {
                                return (true, sup.GetResult(self!));
                            }
                            catch (Exception ex)
                            {
                                // Awaiter.GetResult rethrows the task fault on
                                // the interpreted state-machine path.  Convert
                                // the host exception to VM control flow so the
                                // state machine's generated EH clauses can
                                // catch it or route it to SetException.
                                throw new ILThrow(ex);
                            }
                        }
                        return (true, site.HostName switch
                        {
                            "GetAwaiter" => Slot.Ref(sup.MakeAwaiter(self!)),
                            "get_IsCompleted" => Slot.I4(sup.IsCompleted(self!) ? 1 : 0),
                            _ => throw new ILError($"unsupported async member {openName}.{site.HostName}")
                                .At(m.DisplayName, ilOffset),
                        });
                    }
                }

                if (!d.Host.TryInvoke(site.HostType!, site.HostName!, site.HostParamCount, call, out var result))
                {
                    throw new ILError(HostBindingRegistry.MissingSignatureError(site.HostType!, site.HostName!, site.HostParamCount))
                        .At(m.DisplayName, ilOffset);
                }
                return (site.HostReturnsVoid ? false : true, result);
            }

            case CallSiteKind.HostCtor:
            {
                int argc = site.HostParamCount + (site.HostHasThis ? 1 : 0);
                var args = PopArgs(f, argc);
                if (site.HostHasThis && args.Length > 0 && args[0].Kind == ILSlotKind.ByRef)
                {
                    // Value-type host constructors are invoked as call on an address.
                    // The explicit binding owns construction and writes the value back.
                    var raw = new Slot[args.Length - 1];
                    Array.Copy(args, 1, raw, 0, raw.Length);
                    var call = new HostBindingCall(d, raw, null);
                    if (d.Host.TryCtor(site.HostType!, call, out var value))
                    {
                        args[0].R!.Write(value);
                        return (false, default);
                    }
                }
                else if (site.HostHasThis)
                {
                    // Base constructors for interpreted objects initialise no VM fields.
                    return (false, default);
                }
                throw new ILError(HostBindingRegistry.MissingSignatureError(site.HostType!, ".ctor", site.HostParamCount))
                    .At(m.DisplayName, ilOffset);
            }

            case CallSiteKind.InterpretedCtor:
            {
                var ctor = site.Method!;
                var closed = site.ClosedDecl ?? d.OpenTypeOf(ctor.Decl);
                closed.EnsureLayout();
                int argc = ctor.ParamCount + 1;
                var args = PopArgs(f, argc);
                var receiver = args[0];
                if (receiver.Kind == ILSlotKind.ByRef && receiver.R!.Read().Kind == ILSlotKind.Null)
                {
                    receiver.R.Write(Slot.Ref(new ILObject(closed, closed.TotalFields)));
                }
                var callArgs = new Slot[argc];
                callArgs[0] = receiver;
                for (int i = 1; i < argc; i++) callArgs[i] = CopyValue(args[i]);
                CallMethod(ctor.Instantiate(closed.Args, Array.Empty<ILType>()), callArgs, d, depth + 1);
                return (false, default);
            }

            case CallSiteKind.InterpretedMethod:
            {
                var mi = site.Method!;

                // delegate Invoke
                if (mi.Decl.IsDelegate && mi.Name == "Invoke")
                {
                    var args = PopArgs(f, mi.ParamCount + 1);
                    return (true, InvokeDelegate(d, args[0].O, Rest(args), depth));
                }

                int argc = mi.ParamCount + (mi.IsStatic ? 0 : 1);
                var callArgs = PopArgs(f, argc);
                var callee = mi;

                if (!mi.IsStatic)
                {
                    var receiver = callArgs[0];
                    var recvObj = ResolveSlotValue(receiver);

                    if (recvObj is null)
                    {
                        throw ThrowNre();
                    }

                    var recvType = recvObj switch
                    {
                        ILObject vi => vi.Type,
                        _ => null,
                    };

                    if (virt && recvType is not null)
                    {
                        var callee2 = FindDispatchTarget(recvType, mi)
                            ?? throw new ILError($"no dispatch target for {mi.Decl.FullName}.{mi.Name}/{mi.ParamCount}")
                                .At(m.DisplayName, ilOffset);
                        callee = callee2;
                    }
                }

                EnsureCctor(d, OpenTypeForArgs(d, callee, site));

                var finalArgs = new Slot[callArgs.Length];
                if (!mi.IsStatic)
                {
                    finalArgs[0] = callArgs[0];
                }
                for (int i = mi.IsStatic ? 0 : 1; i < callArgs.Length; i++)
                {
                    finalArgs[i] = CopyValue(callArgs[i]);
                }
                var callResult = CallMethod(callee, finalArgs, d, depth + 1);
                return (mi.ReturnsVoid ? false : true, callResult);
            }

            default:
                throw new ILError("unresolvable call site").At(m.DisplayName, ilOffset);
        }
    }

    private static ILType OpenTypeForArgs(ILDomain d, ILMethod callee, ResolvedCallSite site)
    {
        if (site.ClosedDecl is not null) return site.ClosedDecl;
        return d.OpenTypeOf(callee.Decl);
    }

    private static ILMethod FindDispatchTarget(ILType recvType, ILMethod mi)
    {
        for (ILType? t = recvType; t is not null && t.Def is not null; t = t.Base)
        {
            if (t.Def.Methods.TryGetValue((mi.Name, mi.ParamCount), out var found))
            {
                return found.Instantiate(t.Args, Array.Empty<ILType>());
            }
        }

        var queue = new Queue<ILType>();
        for (ILType? t = recvType; t is not null; t = t.Base)
        {
            foreach (var i in t.Interfaces)
            {
                queue.Enqueue(i);
            }
        }
        while (queue.Count > 0)
        {
            var iface = queue.Dequeue();
            if (iface.Def is not null && iface.Def.Methods.TryGetValue((mi.Name, mi.ParamCount), out var found))
            {
                return found.Instantiate(iface.Args, Array.Empty<ILType>());
            }
            foreach (var i in iface.Interfaces)
            {
                queue.Enqueue(i);
            }
        }

        return mi;
    }

    private static Slot InvokeDelegate(ILDomain d, object? delObj, Slot[] args, int depth)
    {
        if (delObj is not ILDelegate del)
        {
            throw ThrowNre();
        }

        if (del.Multi is not null)
        {
            Slot last = default;
            foreach (var part in del.Multi)
            {
                last = InvokeDelegate(d, part, args, depth);
            }
            return last;
        }

        if (del.Method is null)
        {
            throw new ILError("delegate without method");
        }

        var mi = del.Method;
        int receiverCount = mi.IsStatic ? 0 : 1;
        if (receiverCount != 0 && del.Target is null)
        {
            throw ThrowNre();
        }
        var callArgs = new Slot[args.Length + receiverCount];
        if (receiverCount != 0)
        {
            callArgs[0] = Slot.Ref(del.Target);
        }
        for (int i = 0; i < args.Length; i++)
        {
            callArgs[i + receiverCount] = CopyValue(args[i]);
        }
        var result = CallMethod(mi, callArgs, d, depth + 1);
        return result;
    }

    private static ILDelegate? AsDel(Slot s) => s.O as ILDelegate;

    /// <summary>Intercepts ProbeTaskBuilder method calls — the async carrier.</summary>
    private static Slot InvokeBuilder(ILDomain d, ILMethod caller, Frame f, int ip, ResolvedCallSite site, string resultType, int depth)
    {
        int ilOffset = OffsetOf(caller, ip);
        var support = d.Host.AsyncSupport.TryGetValue(resultType, out var s)
            ? s
            : throw new ILError($"async result type not bound: {resultType} — register the closed task type in the host")
                .At(caller.DisplayName, ilOffset);

        switch (site.HostName)
        {
            case "Create":
            {
                var inst = new ILObject(site.ClosedDecl!, 2);
                inst.F[0] = Slot.Ref(support.CreateSource());
                return Slot.Ref(inst);
            }
            case "Start":
            {
                var args = PopArgs(f, 2); // (ref builder, ref state machine) via this+arg
                var smSlot = args[^1];
                var smRef = smSlot.R ?? throw new ILError("Start: state machine byref expected").At(caller.DisplayName, ilOffset);
                var box = smRef.Read();
                if (box.O is not ILObject sm) throw new ILError("Start: state machine struct expected").At(caller.DisplayName, ilOffset);
                var moveNext = sm.Type.Def!.Methods[("MoveNext", 0)].Instantiate(sm.Type.Args, Array.Empty<ILType>());
                CallMethod(moveNext, new Slot[] { Slot.ByRef(smRef) }, d, depth + 1);
                return default;
            }
            case "AwaitUnsafeOnCompleted":
            case "AwaitOnCompleted":
            {
                // (ref builder, ref awaiter, ref state machine).  The C#
                // compiler selects AwaitOnCompleted when the awaiter only
                // implements INotifyCompletion (as ProbeTaskAwaiter<T> does)
                // and AwaitUnsafeOnCompleted for ICriticalNotifyCompletion;
                // both paths have the same VM continuation semantics.
                var args = PopArgs(f, 3);
                var awaiterObj = ResolveSlotValue(args[1]);
                var smSlot = args[2];
                var smRef = smSlot.R ?? throw new ILError("AwaitUnsafeOnCompleted: machine byref expected").At(caller.DisplayName, ilOffset);
                var moveNext = MoveNextOf(smRef);

                var continuation = MakeContinuation(d, smRef, moveNext);

                if (awaiterObj is null) throw ThrowNre();
                ((System.Runtime.CompilerServices.INotifyCompletion)awaiterObj).OnCompleted(continuation);
                return default;
            }
            case "SetResult":
            {
                var args = PopArgs(f, 2);
                var source = BuilderSource(args[0]);
                support.Complete(source, args[1]);
                return default;
            }
            case "SetException":
            {
                var args = PopArgs(f, 2);
                var source = BuilderSource(args[0]);
                support.Fail(source, ToHostException(args[1]));
                return default;
            }
            case "get_Task":
            {
                var args = PopArgs(f, 1);
                var inst = (ILObject)ResolveSlotValue(args[0])!;
                if (inst.F[1].O is not null)
                {
                    return Slot.Ref(inst.F[1].O);
                }
                var task = support.CreateTask(BuilderSource(args[0]));
                inst.F[1] = Slot.Ref(task);
                return Slot.Ref(task);
            }
            case "SetStateMachine":
                return default;
            default:
                throw new ILError($"unsupported builder method {site.HostName}").At(caller.DisplayName, ilOffset);
        }
    }

    private static ILMethod MoveNextOf(ILRef smRef)
    {
        var box = smRef.Read();
        if (box.O is not ILObject sm) throw new ILError("state machine struct expected");
        return sm.Type.Def!.Methods[("MoveNext", 0)].Instantiate(sm.Type.Args, Array.Empty<ILType>());
    }

    private static Action MakeContinuation(ILDomain d, ILRef smRef, ILMethod moveNext)
    {
        int gen = d.Generation;
        return () =>
        {
            if (!d.Active || gen != d.Generation)
            {
                d.NoteDroppedContinuation();
                return; // late continuation after domain switch — dropped by design
            }
            CallMethod(moveNext, new Slot[] { Slot.ByRef(smRef) }, d, 0);
        };
    }

    private static object BuilderSource(Slot builderSlot)
    {
        var inst = (ILObject)(ResolveSlotValue(builderSlot)
            ?? throw new ILError("builder struct expected"));
        return inst.F[0].O!;
    }

    // ---------------------------------------------------------------- newobj

    private static Slot NewObject(ILDomain d, ILMethod m, Frame f, int ip, int depth)
    {
        var site = CallSiteResolver.Resolve(d, m, ip);
        int ilOffset = OffsetOf(m, ip);

        if (site.Kind == CallSiteKind.HostCtor)
        {
            var raw = PopArgs(f, site.HostParamCount);
            var call = new HostBindingCall(d, raw, null);
            if (d.Host.TryCtor(site.HostType!, call, out var slot))
            {
                return slot;
            }
            throw new ILError(HostBindingRegistry.MissingSignatureError(site.HostType!, ".ctor", site.HostParamCount))
                .At(m.DisplayName, ilOffset);
        }

        if (site.Kind == CallSiteKind.InterpretedCtor)
        {
            var ctor = site.Method!;
            var closed = site.ClosedDecl ?? d.OpenTypeOf(ctor.Decl);
            closed.EnsureLayout();

            if (ctor.Decl.IsDelegate)
            {
                var args = PopArgs(f, 2);
                var del = (ILDelegate)args[1].O!;
                del.Target = args[0].O;
                return Slot.Ref(del);
            }

            var inst = new ILObject(closed, closed.TotalFields);
            var callArgs = new Slot[ctor.ParamCount + 1];
            callArgs[0] = Slot.Ref(inst);
            var raw = PopArgs(f, ctor.ParamCount);
            for (int i = 0; i < raw.Length; i++)
            {
                callArgs[i + 1] = CopyValue(raw[i]);
            }
            // Constructor MethodDef still carries the open declaring type.  A
            // closed generic allocation must execute an instantiated method so
            // field signatures such as !0 resolve against the allocated type.
            CallMethod(ctor.Instantiate(closed.Args, Array.Empty<ILType>()), callArgs, d, depth + 1);
            return Slot.Ref(inst);
        }

        if (site.Kind == CallSiteKind.InterpretedMethod)
        {
            // newobj on a non-ctor method def (e.g. struct state machine ctors in
            // optimized builds appear as real ctors; anything else is a probe gap).
            throw new ILError($"newobj on {site.Method!.DisplayName} unsupported").At(m.DisplayName, ilOffset);
        }

        throw new ILError("unresolvable newobj").At(m.DisplayName, ilOffset);
    }

    // ---------------------------------------------------------------- fields

    private static ILObject InstanceOf(Slot s)
    {
        var slot = s.Kind == ILSlotKind.ByRef ? s.R!.Read() : s;
        if (slot.O is ILObject vi) return vi;
        if (slot.Kind == ILSlotKind.Null) throw ThrowNre();
        throw new ILError($"field access on non-instance ({slot.Kind})");
    }

    private static Slot LoadField(ILDomain d, ILMethod m, Frame f, int ip)
    {
        var (typeName, fieldName, closedType) = ResolveFieldSite(d, m, ip);
        var recv = f.Pop();
        var slot = recv.Kind == ILSlotKind.ByRef ? recv.R!.Read() : recv;

        // host object field
        if (slot.O is not ILObject)
        {
            if (slot.Kind == ILSlotKind.Null) throw ThrowNre();
            if (typeName is not null && d.Host.TryFieldGet(typeName, fieldName, slot.O, out var hv))
            {
                return hv;
            }
            throw new ILError(HostBindingRegistry.MissingSignatureError(typeName ?? slot.O?.GetType().FullName ?? "?", "field " + fieldName, 0))
                .At(m.DisplayName, OffsetOf(m, ip));
        }

        var inst = (ILObject)slot.O!;
        var layoutType = closedType ?? inst.Type;
        layoutType.EnsureLayout();
        if (!layoutType.Layout!.TryGetValue(fieldName, out var idx))
        {
            // walk to base layouts (inheritance)
            idx = FindFieldIndex(layoutType, fieldName)
                ?? throw new ILError($"field {fieldName} not found on {inst.Type.FullName}").At(m.DisplayName, OffsetOf(m, ip));
        }
        var value = inst.F[idx];
        if (value.O is ILObject st && st.Type.IsValueType)
        {
            value = Slot.Ref(st.CloneStruct()); // field load of struct value = copy
        }
        return value;
    }

    private static Slot LoadFieldAddress(ILDomain d, ILMethod m, Frame f, int ip)
    {
        var (_, fieldName, closedType) = ResolveFieldSite(d, m, ip);
        var recv = f.Pop();
        var slot = recv.Kind == ILSlotKind.ByRef ? recv.R!.Read() : recv;
        if (slot.O is not ILObject inst)
        {
            throw slot.Kind == ILSlotKind.Null ? ThrowNre() : new ILError("ldflda on non-instance")
                .At(m.DisplayName, OffsetOf(m, ip));
        }

        var layoutType = closedType ?? inst.Type;
        layoutType.EnsureLayout();
        int index = layoutType.Layout!.TryGetValue(fieldName, out var own)
            ? own
            : FindFieldIndex(layoutType, fieldName)
                ?? throw new ILError($"field {fieldName} not found on {inst.Type.FullName}")
                    .At(m.DisplayName, OffsetOf(m, ip));
        return Slot.ByRef(recv.Kind == ILSlotKind.ByRef
            ? ILRef.ToNestedField(recv.R!, index)
            : ILRef.ToField(inst, index));
    }

    private static void StoreField(ILDomain d, ILMethod m, Frame f, int ip)
    {
        var (typeName, fieldName, closedType) = ResolveFieldSite(d, m, ip);
        var value = f.Pop();
        var recv = f.Pop();
        var slot = recv.Kind == ILSlotKind.ByRef ? recv.R!.Read() : recv;

        // A compiler-generated state-machine local is a value-type local.  Its
        // first ldloca/stfld sequence addresses the zero-initialized local, so
        // the slot is still Null until the first field is written.  Materialize
        // the VM value-type record at that point, matching CLR default(T)
        // storage while preserving the managed pointer held by the IL stack.
        if (slot.Kind == ILSlotKind.Null && recv.Kind == ILSlotKind.ByRef && closedType?.Def is not null)
        {
            closedType.EnsureLayout();
            var fresh = new ILObject(closedType, closedType.TotalFields);
            recv.R!.Write(Slot.Ref(fresh));
            slot = Slot.Ref(fresh);
        }

        if (slot.O is not ILObject)
        {
            if (slot.Kind == ILSlotKind.Null) throw ThrowNre();
            if (typeName is not null && d.Host.TryFieldSet(typeName, fieldName, slot.O, value))
            {
                return;
            }
            throw new ILError(HostBindingRegistry.MissingSignatureError(typeName ?? "?", "field " + fieldName, 0))
                .At(m.DisplayName, OffsetOf(m, ip));
        }

        var inst = (ILObject)slot.O!;
        var layoutType = closedType ?? inst.Type;
        layoutType.EnsureLayout();
        var idx = layoutType.Layout!.TryGetValue(fieldName, out var i2)
            ? i2
            : FindFieldIndex(layoutType, fieldName)
            ?? throw new ILError($"field {fieldName} not found on {inst.Type.FullName}").At(m.DisplayName, OffsetOf(m, ip));
        inst.F[idx] = CopyValue(value);
        if (recv.Kind == ILSlotKind.ByRef)
        {
            recv.R!.Write(Slot.Ref(inst));
        }
    }

    private static int? FindFieldIndex(ILType start, string field)
    {
        for (ILType? t = start; t is not null && t.Def is not null; t = t.Base)
        {
            t.EnsureLayout();
            if (t.Layout!.TryGetValue(field, out var idx)) return idx;
        }
        return null;
    }

    private static Slot LoadStatic(ILDomain d, ILMethod m, Frame f, int ip)
    {
        var closed = ResolveStaticType(d, m, ip);
        var fieldName = LastFieldName(d, m, ip);
        EnsureCctor(d, closed);
        var def = closed.Def!;
        if (!def.FieldNames.Contains(fieldName))
        {
            throw new ILError($"static field {fieldName} not on {closed.FullName}").At(m.DisplayName, OffsetOf(m, ip));
        }
        int index = def.FieldNames.IndexOf(fieldName);
        if (closed.Statics is null || (uint)index >= (uint)closed.Statics.Length)
        {
            throw new ILError($"static storage missing for {closed.FullName}.{fieldName} index={index} length={closed.Statics?.Length ?? -1}")
                .At(m.DisplayName, OffsetOf(m, ip));
        }
        return closed.Statics[index];
    }

    private static void StoreStatic(ILDomain d, ILMethod m, Frame f, int ip)
    {
        var closed = ResolveStaticType(d, m, ip);
        var fieldName = LastFieldName(d, m, ip);
        var value = f.Pop();
        EnsureCctor(d, closed);
        var def = closed.Def!;
        if (!def.FieldNames.Contains(fieldName))
        {
            throw new ILError($"static field {fieldName} not on {closed.FullName}").At(m.DisplayName, OffsetOf(m, ip));
        }
        int index = def.FieldNames.IndexOf(fieldName);
        if (closed.Statics is null || (uint)index >= (uint)closed.Statics.Length)
        {
            throw new ILError($"static storage missing for {closed.FullName}.{fieldName} index={index} length={closed.Statics?.Length ?? -1}")
                .At(m.DisplayName, OffsetOf(m, ip));
        }
        closed.Statics[index] = value;
    }

    private static string LastFieldName(ILDomain d, ILMethod m, int ip)
    {
        int token = (int)m.Code[ip].I;
        var handle = MetadataTokens.EntityHandle(token);
        if (handle.Kind == HandleKind.FieldDefinition)
        {
            return d.Module.NameOf(d.Module.Md.GetFieldDefinition((FieldDefinitionHandle)handle).Name);
        }
        if (handle.Kind == HandleKind.MemberReference)
        {
            return d.Module.NameOf(d.Module.Md.GetMemberReference((MemberReferenceHandle)handle).Name);
        }
        throw new ILError($"bad field token 0x{token:X8}").At(m.DisplayName, OffsetOf(m, ip));
    }

    private static ILType ResolveStaticType(ILDomain d, ILMethod m, int ip)
    {
        int token = (int)m.Code[ip].I;
        var handle = MetadataTokens.EntityHandle(token);
        var ctx = new TypeResolutionContext { ClassArgs = m.ClassArgs, MethodArgs = m.MethodArgs };
        ILType closed;
        if (handle.Kind == HandleKind.MemberReference)
        {
            var parent = d.Module.Md.GetMemberReference((MemberReferenceHandle)handle).Parent;
            closed = parent.Kind switch
            {
                HandleKind.TypeDefinition => d.GetModuleType(MetadataTokens.GetRowNumber((TypeDefinitionHandle)parent)),
                HandleKind.TypeSpecification => d.ResolveTypeSpec((TypeSpecificationHandle)parent, ctx),
                HandleKind.TypeReference => CallSiteResolver.ResolveToken(d, MetadataTokens.GetToken(parent), ctx).ClosedDecl
                    ?? d.GetHostType(CallSiteResolver.TypeRefFullName(d, (TypeReferenceHandle)parent)),
                _ => throw new ILError("bad static field parent"),
            };
        }
        else if (handle.Kind == HandleKind.FieldDefinition)
        {
            var fd = d.Module.Md.GetFieldDefinition((FieldDefinitionHandle)handle);
            var decl = fd.GetDeclaringType();
            closed = d.GetModuleType(MetadataTokens.GetRowNumber(decl));
        }
        else
        {
            throw new ILError("bad static field token kind");
        }
        return closed;
    }

    private static (string? typeName, string fieldName, ILType? closedType) ResolveFieldSite(ILDomain d, ILMethod m, int ip)
    {
        int token = (int)m.Code[ip].I;
        var handle = MetadataTokens.EntityHandle(token);
        var ctx = new TypeResolutionContext { ClassArgs = m.ClassArgs, MethodArgs = m.MethodArgs };

        if (handle.Kind == HandleKind.FieldDefinition)
        {
            var fd = d.Module.Md.GetFieldDefinition((FieldDefinitionHandle)handle);
            var decl = fd.GetDeclaringType();
            var closed = d.GetModuleType(MetadataTokens.GetRowNumber(decl));
            return (null, d.Module.NameOf(fd.Name), closed);
        }

        if (handle.Kind == HandleKind.MemberReference)
        {
            var mr = d.Module.Md.GetMemberReference((MemberReferenceHandle)handle);
            var name = d.Module.NameOf(mr.Name);
            var parent = mr.Parent;
            switch (parent.Kind)
            {
                case HandleKind.TypeDefinition:
                    return (null, name, d.GetModuleType(MetadataTokens.GetRowNumber((TypeDefinitionHandle)parent)));
                case HandleKind.TypeSpecification:
                    return (null, name, d.ResolveTypeSpec((TypeSpecificationHandle)parent, ctx));
                case HandleKind.TypeReference:
                    return (CallSiteResolver.TypeRefFullName(d, (TypeReferenceHandle)parent), name, null);
            }
        }

        throw new ILError($"bad field token 0x{token:X8}").At(m.DisplayName, OffsetOf(m, ip));
    }

    private static void EnsureCctor(ILDomain d, ILType closed)
    {
        if (closed.CctorDone || closed.Def is null) return;
        closed.EnsureLayout();
        closed.CctorDone = true;
        closed.Statics = new Slot[closed.TotalFields];
        if (closed.Def.Methods.TryGetValue((".cctor", 0), out var cctor))
        {
            CallMethod(cctor.Instantiate(closed.Args, Array.Empty<ILType>()), Array.Empty<Slot>(), d, 0);
        }
    }

    // ---------------------------------------------------------------- helpers

    private static ILType ResolveTypeCached(ILDomain d, ILMethod m, int ip, int token)
    {
        if (m.TokenCache[ip] is ILType t) return t;
        t = d.ResolveTypeToken(token, new TypeResolutionContext { ClassArgs = m.ClassArgs, MethodArgs = m.MethodArgs });
        m.TokenCache[ip] = t;
        return t;
    }

    private static string ResolveString(ILDomain d, ILMethod m, int ip, int token)
        => d.Module.UserString(MetadataTokens.UserStringHandle(token));

    public static object? ResolveSlotValue(Slot s)
    {
        var slot = s.Kind == ILSlotKind.ByRef ? s.R!.Read() : s;
        return slot.Kind == ILSlotKind.Null ? null : slot.O;
    }

    private static Slot CopyValue(Slot s)
    {
        if (s.Kind == ILSlotKind.Obj && s.O is ILObject vi && vi.Type.IsValueType)
        {
            return Slot.Ref(vi.CloneStruct());
        }
        return s;
    }

    private static Slot DefaultValue(ILType type)
    {
        return type.PrimKind switch
        {
            ILPrim.I4 => Slot.I4(0),
            ILPrim.I8 => Slot.I8(0),
            ILPrim.R4 => Slot.R4(0),
            ILPrim.R8 => Slot.R8(0),
            _ when type.IsValueType && type.Def is not null =>
                CreateDefaultStruct(type),
            _ => Slot.Null,
        };
    }

    private static Slot CreateDefaultStruct(ILType type)
    {
        type.EnsureLayout();
        return Slot.Ref(new ILObject(type, type.TotalFields));
    }

    private static bool Truthy(Slot s) => s.Kind switch
    {
        ILSlotKind.Null => false,
        ILSlotKind.I4 or ILSlotKind.I8 => s.I != 0,
        ILSlotKind.Obj => s.O is not null,
        _ => throw new ILError($"cannot branch on {s.Kind}"),
    };

    private static Slot ToInt(Slot a)
    {
        if (a.Kind is ILSlotKind.R4 or ILSlotKind.R8) return Slot.I4((int)a.AsF8());
        return Slot.I4((int)a.I8v());
    }

    private static bool ValEq(Slot a, Slot b)
    {
        if (a.Kind is ILSlotKind.Null || b.Kind is ILSlotKind.Null) return a.Kind == b.Kind;
        if (a.Kind is ILSlotKind.Obj || b.Kind is ILSlotKind.Obj) return ReferenceEquals(a.O, b.O);
        if (a.Kind is ILSlotKind.R4 or ILSlotKind.R8 || b.Kind is ILSlotKind.R4 or ILSlotKind.R8) return a.AsF8() == b.AsF8();
        return a.I8v() == b.I8v();
    }

    private static int NumCmp(Slot a, Slot b)
    {
        if (a.Kind is ILSlotKind.R4 or ILSlotKind.R8 || b.Kind is ILSlotKind.R4 or ILSlotKind.R8)
        {
            double fx = a.AsF8();
            double fy = b.AsF8();
            return fx < fy ? -1 : fx > fy ? 1 : 0;
        }
        long ix = a.I8v();
        long iy = b.I8v();
        return ix < iy ? -1 : ix > iy ? 1 : 0;
    }

    private static int UnCmp(Slot a, Slot b)
    {
        if (a.Kind is ILSlotKind.R4 or ILSlotKind.R8 || b.Kind is ILSlotKind.R4 or ILSlotKind.R8)
        {
            double fx = a.AsF8();
            double fy = b.AsF8();
            if (double.IsNaN(fx) || double.IsNaN(fy)) return 1; // cgt.un-style: NaN is maximal
            return fx < fy ? -1 : fx > fy ? 1 : 0;
        }
        ulong ux = (ulong)a.I8v();
        ulong uy = (ulong)b.I8v();
        return ux < uy ? -1 : ux > uy ? 1 : 0;
    }

    private static Slot NumAdd(Slot a, Slot b)
    {
        if (a.Kind is ILSlotKind.R4 or ILSlotKind.R8 || b.Kind is ILSlotKind.R4 or ILSlotKind.R8)
        {
            if (a.Kind == ILSlotKind.R4 || b.Kind == ILSlotKind.R4)
            {
                return Slot.R4((float)a.AsF8() + (float)b.AsF8());
            }
            return Slot.R8(a.AsF8() + b.AsF8());
        }
        if (a.Kind == ILSlotKind.I4 && b.Kind == ILSlotKind.I4) return Slot.I4(unchecked((int)a.I + (int)b.I));
        return Slot.I8(unchecked(a.I + b.I));
    }

    private static Slot NumSub(Slot a, Slot b)
    {
        if (a.Kind is ILSlotKind.R4 or ILSlotKind.R8 || b.Kind is ILSlotKind.R4 or ILSlotKind.R8)
        {
            if (a.Kind == ILSlotKind.R4 || b.Kind == ILSlotKind.R4)
            {
                return Slot.R4((float)a.AsF8() - (float)b.AsF8());
            }
            return Slot.R8(a.AsF8() - b.AsF8());
        }
        if (a.Kind == ILSlotKind.I4 && b.Kind == ILSlotKind.I4) return Slot.I4(unchecked((int)a.I - (int)b.I));
        return Slot.I8(unchecked(a.I - b.I));
    }

    private static Slot NumMul(Slot a, Slot b)
    {
        if (a.Kind is ILSlotKind.R4 or ILSlotKind.R8 || b.Kind is ILSlotKind.R4 or ILSlotKind.R8)
        {
            if (a.Kind == ILSlotKind.R4 || b.Kind == ILSlotKind.R4)
            {
                return Slot.R4((float)a.AsF8() * (float)b.AsF8());
            }
            return Slot.R8(a.AsF8() * b.AsF8());
        }
        if (a.Kind == ILSlotKind.I4 && b.Kind == ILSlotKind.I4) return Slot.I4(unchecked((int)a.I * (int)b.I));
        return Slot.I8(unchecked(a.I * b.I));
    }

    private static Slot NumDiv(Slot a, Slot b)
    {
        if (a.Kind is ILSlotKind.R4 or ILSlotKind.R8 || b.Kind is ILSlotKind.R4 or ILSlotKind.R8)
        {
            if (a.Kind == ILSlotKind.R4 || b.Kind == ILSlotKind.R4)
            {
                return Slot.R4((float)a.AsF8() / (float)b.AsF8());
            }
            return Slot.R8(a.AsF8() / b.AsF8());
        }
        if (b.I8v() == 0)
        {
            throw new ILThrow(new DivideByZeroException());
        }
        if (a.Kind == ILSlotKind.I4 && b.Kind == ILSlotKind.I4) return Slot.I4((int)a.I / (int)b.I);
        return Slot.I8(a.I / b.I);
    }

    private static Slot NumRem(Slot a, Slot b)
    {
        if (a.Kind is ILSlotKind.R4 or ILSlotKind.R8 || b.Kind is ILSlotKind.R4 or ILSlotKind.R8)
        {
            if (a.Kind == ILSlotKind.R4 || b.Kind == ILSlotKind.R4)
            {
                return Slot.R4((float)a.AsF8() % (float)b.AsF8());
            }
            return Slot.R8(a.AsF8() % b.AsF8());
        }
        if (b.I8v() == 0)
        {
            throw new ILThrow(new DivideByZeroException());
        }
        if (a.Kind == ILSlotKind.I4 && b.Kind == ILSlotKind.I4) return Slot.I4((int)a.I % (int)b.I);
        return Slot.I8(a.I % b.I);
    }

    private static Slot Int2(long v, ILSlotKind kind) => kind == ILSlotKind.I4 ? Slot.I4((int)v) : Slot.I8(v);

    private static ILThrow ThrowNre() => new(new NullReferenceException());

    private static object ThrowValue(Slot s)
    {
        if (s.Kind != ILSlotKind.Obj || s.O is null)
        {
            throw new ILError("throw of non-object");
        }
        return s.O;
    }

    public static Exception ToHostException(Slot s)
    {
        var o = ThrowValue(s);
        if (o is Exception ex) return ex;
        if (o is ILObject vi)
        {
            return new ILHostException($"interpreted exception {vi.Type.FullName}", vi);
        }
        throw new ILError("bad exception value");
    }

    private static Slot BoxValue(Slot v, ILType t)
    {
        if (t.Def is not null)
        {
            return t.IsValueType ? CopyValue(v) : v;
        }
        if (v.Kind is ILSlotKind.I4 or ILSlotKind.I8 or ILSlotKind.R4 or ILSlotKind.R8)
        {
            return Slot.Ref(v.Boxed());
        }
        return v;
    }

    private static Slot UnboxAny(Slot v, ILType t)
    {
        if (v.Kind == ILSlotKind.Null) throw ThrowNre();

        if (t.Def is not null)
        {
            if (v.O is ILObject vi && vi.Type.IsAssignableTo(t))
            {
                return vi.Type.IsValueType ? Slot.Ref(vi.CloneStruct()) : Slot.Ref(vi);
            }
            throw new ILThrow(new InvalidCastException($"cannot unbox {v.O?.GetType().FullName} to {t.FullName}"));
        }

        var boxed = v.O;
        if (t.PrimKind == ILPrim.I4 && boxed is int i) return Slot.I4(i);
        if (t.PrimKind == ILPrim.I8 && boxed is long l) return Slot.I8(l);
        if (t.PrimKind == ILPrim.R4 && boxed is float f) return Slot.R4(f);
        if (t.PrimKind == ILPrim.R8 && boxed is double dd) return Slot.R8(dd);
        if (boxed is not null && t.HostType!.IsInstanceOfType(boxed)) return Slot.Ref(boxed);
        throw new ILThrow(new InvalidCastException($"cannot unbox to {t.FullName}"));
    }

    private static Slot TypeCheck(Slot v, ILType t, bool throwOnFail)
    {
        if (v.Kind == ILSlotKind.Null) return Slot.Null;

        bool ok;
        if (v.O is ILDelegate && t.IsDelegate)
        {
            ok = true;
        }
        else if (v.O is ILObject vi)
        {
            ok = ReferenceEquals(vi.Type, t) || vi.Type.IsAssignableTo(t) ||
                 (t.HostType is not null && vi.Type.ChainHasHost(t.HostType));
        }
        else
        {
            ok = t.HostType is not null && t.HostType.IsInstanceOfType(v.O);
        }

        if (!ok && throwOnFail)
        {
            throw new ILThrow(new InvalidCastException($"cannot cast to {t.FullName}"));
        }
        return ok ? v : Slot.Null;
    }

    private static Slot[] PopArgs(Frame f, int n)
    {
        if (f.Esp < n) throw new ILError("stack underflow on call");
        var arr = new Slot[n];
        int baseIdx = f.Esp - n;
        Array.Copy(f.Stack, baseIdx, arr, 0, n);
        f.Esp = baseIdx;
        return arr;
    }

    private static Slot[] Rest(Slot[] withThis)
    {
        var r = new Slot[withThis.Length - 1];
        Array.Copy(withThis, 1, r, 0, r.Length);
        return r;
    }
}
