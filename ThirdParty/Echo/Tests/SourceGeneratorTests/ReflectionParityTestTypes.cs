// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

// Mirror-type pairs for ReflectionParityTests: for every Gen* type ([GenerateSerializer]) there is a
// structurally identical Ref* type WITHOUT the attribute (serialized by the reflection path).
// Serializing equal instances of both must produce deeply equal EchoObject graphs - the format
// red line the generator is held to.

using XEngine.Echo;

namespace Tests.SourceGeneratorTests;

public enum ParityEnum { Zero = 0, One = 1, Big = 1000 }
public enum ParityULongEnum : ulong { Zero = 0, Huge = 18446744073709551000UL }

/// <summary>Shared plain nested type (never annotated) so nested-object output carries no $type.</summary>
public class ParityNested
{
    public int N = 7;
    public string? S = "nested";
}

#region Flat mirror pair

[GenerateSerializer]
public partial class GenParityFlat
{
    public byte B = 200;
    public sbyte Sb = -100;
    public short Sh = -30000;
    public ushort Ush = 60000;
    public int I = -123456;
    public uint Ui = 3123456789;
    public long L = -9123456789012345678;
    public ulong Ul = 18123456789012345678;
    public float F = 3.5f;
    public double D = 2.25;
    public decimal Dec = 123.456m;
    public bool Bo = true;
    public char C = 'X';
    public string? S = "hello";
    public string? SNull = null;
    public byte[]? Bytes = new byte[] { 1, 2, 3 };
    public byte[]? BytesNull = null;
    public ParityEnum E = ParityEnum.Big;
    public ParityULongEnum EUl = ParityULongEnum.Huge;
    public System.Guid G = System.Guid.Parse("11111111-2222-3333-4444-555555555555");
    public System.DateTime Dt = new(2026, 8, 18, 12, 0, 0, System.DateTimeKind.Utc);
    public System.TimeSpan Ts = System.TimeSpan.FromMinutes(90);
    public System.DateTimeOffset Dto = new(2026, 8, 18, 12, 0, 0, System.TimeSpan.FromHours(8));
    public int? NullableSet = 42;
    public int? NullableNull = null;
    public List<int>? Ints = new() { 1, 2, 3 };
    public int[]? Arr = new[] { 4, 5, 6 };
    public Dictionary<string, int>? Map = new() { { "a", 1 }, { "b", 2 } };
    public ParityNested? Nested = new();
    public ParityNested? NestedNull = null;
    public object? Boxed = 99;

    [SerializeField] private int privateField = 1234;
    [SerializeIgnore] public int Ignored = 555;

    [IgnoreOnNull] public string? OptionalNull = null;
    [IgnoreOnNull] public string? OptionalSet = "present";

    public bool Cond = false;
    [SerializeIf(nameof(Cond))] public int Conditional = 77;

    public int PrivateFieldValue => privateField;
    public void SetPrivateField(int v) => privateField = v;
}

public class RefParityFlat
{
    public byte B = 200;
    public sbyte Sb = -100;
    public short Sh = -30000;
    public ushort Ush = 60000;
    public int I = -123456;
    public uint Ui = 3123456789;
    public long L = -9123456789012345678;
    public ulong Ul = 18123456789012345678;
    public float F = 3.5f;
    public double D = 2.25;
    public decimal Dec = 123.456m;
    public bool Bo = true;
    public char C = 'X';
    public string? S = "hello";
    public string? SNull = null;
    public byte[]? Bytes = new byte[] { 1, 2, 3 };
    public byte[]? BytesNull = null;
    public ParityEnum E = ParityEnum.Big;
    public ParityULongEnum EUl = ParityULongEnum.Huge;
    public System.Guid G = System.Guid.Parse("11111111-2222-3333-4444-555555555555");
    public System.DateTime Dt = new(2026, 8, 18, 12, 0, 0, System.DateTimeKind.Utc);
    public System.TimeSpan Ts = System.TimeSpan.FromMinutes(90);
    public System.DateTimeOffset Dto = new(2026, 8, 18, 12, 0, 0, System.TimeSpan.FromHours(8));
    public int? NullableSet = 42;
    public int? NullableNull = null;
    public List<int>? Ints = new() { 1, 2, 3 };
    public int[]? Arr = new[] { 4, 5, 6 };
    public Dictionary<string, int>? Map = new() { { "a", 1 }, { "b", 2 } };
    public ParityNested? Nested = new();
    public ParityNested? NestedNull = null;
    public object? Boxed = 99;

    [SerializeField] private int privateField = 1234;
    [SerializeIgnore] public int Ignored = 555;

    [IgnoreOnNull] public string? OptionalNull = null;
    [IgnoreOnNull] public string? OptionalSet = "present";

    public bool Cond = false;
    [SerializeIf(nameof(Cond))] public int Conditional = 77;

    public int PrivateFieldValue => privateField;
    public void SetPrivateField(int v) => privateField = v;
}

#endregion

#region Inheritance mirror pair (base fields incl. base-private via UnsafeAccessor)

public class ParityBase
{
    public int BasePub = 10;
    [SerializeField] private int basePriv = 20;
    [SerializeField] protected string? BaseProt = "prot";

    public int BasePrivValue => basePriv;
    public void SetBasePriv(int v) => basePriv = v;
}

[GenerateSerializer]
public partial class GenParityDerived : ParityBase
{
    public int DerivedField = 30;
    public readonly int ReadonlyField = 40;

    public GenParityDerived() { }
    public GenParityDerived(int ro) => ReadonlyField = ro;
}

public class RefParityDerived : ParityBase
{
    public int DerivedField = 30;
    public readonly int ReadonlyField = 40;

    public RefParityDerived() { }
    public RefParityDerived(int ro) => ReadonlyField = ro;
}

/// <summary>NOT annotated subclass of a generated type: the dispatcher must fall back to
/// reflection so this type's own fields still serialize.</summary>
public class PlainChildOfGenerated : GenParityDerived
{
    public int ChildField = 50;
}

#endregion

#region Shadowed field name mirror pair

public class ParityShadowBase
{
    public int Shadow = 1;
}

[GenerateSerializer]
public partial class GenParityShadow : ParityShadowBase
{
    public new int Shadow = 2;
}

public class RefParityShadow : ParityShadowBase
{
    public new int Shadow = 2;
}

#endregion

#region Reference identity mirror pair ($id parity)

[GenerateSerializer]
public partial class GenParityRefs
{
    public ParityNested? A;
    public ParityNested? B;
    public List<int>? SharedList1;
    public List<int>? SharedList2;
}

public class RefParityRefs
{
    public ParityNested? A;
    public ParityNested? B;
    public List<int>? SharedList1;
    public List<int>? SharedList2;
}

#endregion
