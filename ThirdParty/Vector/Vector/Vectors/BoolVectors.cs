// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Runtime.InteropServices;

namespace XEngine.Vector;

[StructLayout(LayoutKind.Sequential, Pack = 1, Size = 1)]
public readonly struct Bool2 : IEquatable<Bool2>
{
    private readonly byte _bits;
    public bool X => (_bits & 1) != 0;
    public bool Y => (_bits & 2) != 0;
    public bool Any => (_bits & 3) != 0;
    public bool All => (_bits & 3) == 3;
    public byte Bits => (byte)(_bits & 3);
    public Bool2(bool x, bool y) => _bits = (byte)((x ? 1 : 0) | (y ? 2 : 0));
    private Bool2(byte bits) => _bits = (byte)(bits & 3);
    public bool this[int index] => index switch { 0 => X, 1 => Y, _ => throw new IndexOutOfRangeException() };
    public static Bool2 operator &(Bool2 a, Bool2 b) => new((byte)(a._bits & b._bits));
    public static Bool2 operator |(Bool2 a, Bool2 b) => new((byte)(a._bits | b._bits));
    public static Bool2 operator ^(Bool2 a, Bool2 b) => new((byte)(a._bits ^ b._bits));
    public static Bool2 operator !(Bool2 value) => new((byte)~value._bits);
    public static bool operator ==(Bool2 a, Bool2 b) => a.Equals(b);
    public static bool operator !=(Bool2 a, Bool2 b) => !a.Equals(b);
    public bool Equals(Bool2 other) => Bits == other.Bits;
    public override bool Equals(object? obj) => obj is Bool2 other && Equals(other);
    public override int GetHashCode() => Bits;
    public override string ToString() => $"({X}, {Y})";
}

[StructLayout(LayoutKind.Sequential, Pack = 1, Size = 1)]
public readonly struct Bool3 : IEquatable<Bool3>
{
    private readonly byte _bits;
    public bool X => (_bits & 1) != 0;
    public bool Y => (_bits & 2) != 0;
    public bool Z => (_bits & 4) != 0;
    public bool Any => (_bits & 7) != 0;
    public bool All => (_bits & 7) == 7;
    public byte Bits => (byte)(_bits & 7);
    public Bool3(bool x, bool y, bool z) => _bits = (byte)((x ? 1 : 0) | (y ? 2 : 0) | (z ? 4 : 0));
    private Bool3(byte bits) => _bits = (byte)(bits & 7);
    public bool this[int index] => index switch { 0 => X, 1 => Y, 2 => Z, _ => throw new IndexOutOfRangeException() };
    public static Bool3 operator &(Bool3 a, Bool3 b) => new((byte)(a._bits & b._bits));
    public static Bool3 operator |(Bool3 a, Bool3 b) => new((byte)(a._bits | b._bits));
    public static Bool3 operator ^(Bool3 a, Bool3 b) => new((byte)(a._bits ^ b._bits));
    public static Bool3 operator !(Bool3 value) => new((byte)~value._bits);
    public static bool operator ==(Bool3 a, Bool3 b) => a.Equals(b);
    public static bool operator !=(Bool3 a, Bool3 b) => !a.Equals(b);
    public bool Equals(Bool3 other) => Bits == other.Bits;
    public override bool Equals(object? obj) => obj is Bool3 other && Equals(other);
    public override int GetHashCode() => Bits;
    public override string ToString() => $"({X}, {Y}, {Z})";
}

[StructLayout(LayoutKind.Sequential, Pack = 1, Size = 1)]
public readonly struct Bool4 : IEquatable<Bool4>
{
    private readonly byte _bits;
    public bool X => (_bits & 1) != 0;
    public bool Y => (_bits & 2) != 0;
    public bool Z => (_bits & 4) != 0;
    public bool W => (_bits & 8) != 0;
    public bool Any => (_bits & 15) != 0;
    public bool All => (_bits & 15) == 15;
    public byte Bits => (byte)(_bits & 15);
    public Bool4(bool x, bool y, bool z, bool w)
        => _bits = (byte)((x ? 1 : 0) | (y ? 2 : 0) | (z ? 4 : 0) | (w ? 8 : 0));
    private Bool4(byte bits) => _bits = (byte)(bits & 15);
    public bool this[int index] => index switch { 0 => X, 1 => Y, 2 => Z, 3 => W, _ => throw new IndexOutOfRangeException() };
    public static Bool4 operator &(Bool4 a, Bool4 b) => new((byte)(a._bits & b._bits));
    public static Bool4 operator |(Bool4 a, Bool4 b) => new((byte)(a._bits | b._bits));
    public static Bool4 operator ^(Bool4 a, Bool4 b) => new((byte)(a._bits ^ b._bits));
    public static Bool4 operator !(Bool4 value) => new((byte)~value._bits);
    public static bool operator ==(Bool4 a, Bool4 b) => a.Equals(b);
    public static bool operator !=(Bool4 a, Bool4 b) => !a.Equals(b);
    public bool Equals(Bool4 other) => Bits == other.Bits;
    public override bool Equals(object? obj) => obj is Bool4 other && Equals(other);
    public override int GetHashCode() => Bits;
    public override string ToString() => $"({X}, {Y}, {Z}, {W})";
}
