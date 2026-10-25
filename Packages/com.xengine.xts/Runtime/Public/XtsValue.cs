using System;
using System.Runtime.InteropServices;
using System.Text;

namespace XEngine.XTS;

/// <summary>
/// A primitive value crossing the XTS boundary: the result shapes scripts can
/// hand back to the host before the object binding layer exists. Strings are
/// copied out of the environment when the value is produced; they do not keep
/// the runtime alive.
/// </summary>
public readonly struct XtsValue
{
    public static XtsValue Undefined => default;

    public XtsValueKind Kind { get; }
    private readonly object? _boxed;
    internal object? Boxed => _boxed;

    public static XtsValue Null => new(XtsValueKind.Null, null);

    internal XtsValue(XtsValueKind kind, object? boxed)
    {
        Kind = kind;
        _boxed = boxed;
    }

    public static XtsValue From(bool value) => new(XtsValueKind.Bool, value);
    public static XtsValue From(int value) => new(XtsValueKind.Int32, value);
    /// <summary>64-bit integers cross as JS BigInt.</summary>
    public static XtsValue From(long value) => new(XtsValueKind.BigInt, value);
    public static XtsValue From(double value) => new(XtsValueKind.Float64, value);
    public static XtsValue From(string value) => new(XtsValueKind.String, value ?? string.Empty);

    public bool IsUndefined => Kind == XtsValueKind.Undefined;
    public bool IsNull => Kind == XtsValueKind.Null;
    public bool IsNumber => Kind is XtsValueKind.Int32 or XtsValueKind.Float64;
    public bool IsString => Kind == XtsValueKind.String;
    public bool IsBool => Kind == XtsValueKind.Bool;

    public bool AsBool() => Kind == XtsValueKind.Bool && _boxed is true;
    public long AsBigInt() => Kind switch
    {
        XtsValueKind.BigInt => (long)_boxed!,
        XtsValueKind.Int32 => (int)_boxed!,
        XtsValueKind.Float64 => (long)(double)_boxed!,
        _ => 0L,
    };
    public int AsInt32() => Kind switch
    {
        XtsValueKind.Int32 => (int)_boxed!,
        XtsValueKind.Float64 => (int)(double)_boxed!,
        _ => 0,
    };
    public double AsDouble() => Kind switch
    {
        XtsValueKind.Int32 => (int)_boxed!,
        XtsValueKind.Float64 => (double)_boxed!,
        _ => 0d,
    };
    public string AsString() => Kind == XtsValueKind.String ? (string)_boxed! : string.Empty;

    public override string ToString() => Kind switch
    {
        XtsValueKind.Undefined => "undefined",
        XtsValueKind.Null => "null",
        XtsValueKind.Bool => AsBool() ? "true" : "false",
        XtsValueKind.Int32 => AsInt32().ToString(),
        XtsValueKind.Float64 => AsDouble().ToString(System.Globalization.CultureInfo.InvariantCulture),
        XtsValueKind.String => AsString(),
        _ => base.ToString() ?? string.Empty,
    };
}

public enum XtsValueKind : int
{
    Undefined = 0,
    Bool = 1,
    Int32 = 2,
    Float64 = 3,
    String = 4,
    Null = 5,
    /// <summary>JS BigInt crossing as a 64-bit signed integer.</summary>
    BigInt = 6,
}
