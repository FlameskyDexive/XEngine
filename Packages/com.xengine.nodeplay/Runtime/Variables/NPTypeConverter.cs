using System;
using System.Collections.Generic;
using System.Globalization;

namespace XEngine.NodePlay;

/// <summary>
/// Extension point for user conversions, e.g. a health type to float. Register per project via
/// engine startup code; core numeric/string bridges ship built-in below.
/// </summary>
public sealed class NPTypeConverterAttribute : Attribute
{
    public Type From { get; }
    public Type To { get; }

    public NPTypeConverterAttribute(Type from, Type to)
    {
        From = from;
        To = to;
    }
}

/// <summary>Small deterministic conversion table backing automatic variable/parameter coercion.</summary>
public static class NPTypeConverter
{
    private static readonly Dictionary<(Type from, Type to), Func<object, object>> _custom = new();
    private static readonly Dictionary<(Type from, Type to), Func<object, object>> _builtin = BuildBuiltins();

    /// <summary>Projects register extra bridges at startup (editor Type Configurator can list these).</summary>
    public static void Register(Type from, Type to, Func<object, object> convert)
        => _custom[(from, to)] = convert;

    public static void ClearCustom() => _custom.Clear();

    /// <summary>Returns null when no route exists — callers keep their current value.</summary>
    public static object? Convert(object? value, Type target)
    {
        if (value == null)
            return null;
        if (target.IsInstanceOfType(value))
            return value;

        Type from = value.GetType();
        if (_custom.TryGetValue((from, target), out Func<object, object>? custom))
            return Safe(custom, value);
        if (_builtin.TryGetValue((from, target), out Func<object, object>? builtin))
            return Safe(builtin, value);
        if (target.IsEnum && from == typeof(string) && Enum.IsDefined(target, value))
            return Enum.Parse(target, (string)value);

        // Numeric-family last resort through double, including enum<->underlying.
        object? asDouble = TryToDouble(value);
        if (asDouble == null)
            return null;
        return FromDouble(target, (double)asDouble, value);
    }

    private static object? Safe(Func<object, object> f, object v)
    {
        try
        {
            return f(v);
        }
        catch
        {
            return null;
        }
    }

    private static Dictionary<(Type, Type), Func<object, object>> BuildBuiltins()
    {
        var t = new Dictionary<(Type, Type), Func<object, object>>();
        CultureInfo inv = CultureInfo.InvariantCulture;

        foreach (var from in new[] { typeof(int), typeof(float), typeof(double), typeof(long), typeof(short), typeof(byte) })
        {
            foreach (var to in new[] { typeof(int), typeof(float), typeof(double), typeof(long), typeof(short), typeof(byte) })
            {
                if (from == to)
                    continue;
                t[(from, to)] = v => System.Convert.ChangeType(v, to, inv)!;
            }
        }

        foreach (var num in new[] { typeof(int), typeof(float), typeof(double), typeof(long) })
        {
            t[(num, typeof(string))] = v => System.Convert.ToString(v, inv)!;
            t[(typeof(string), num)] = v => System.Convert.ChangeType(((string)v).Trim(), num, inv)!;
        }

        t[(typeof(bool), typeof(string))] = v => ((bool)v) ? "true" : "false";
        t[(typeof(string), typeof(bool))] = v =>
            string.Equals((string)v, "true", StringComparison.OrdinalIgnoreCase) ? true :
            string.Equals((string)v, "false", StringComparison.OrdinalIgnoreCase) ? false :
            throw new FormatException("not a bool");
        t[(typeof(float), typeof(bool))] = v => (float)v != 0f;
        t[(typeof(bool), typeof(float))] = v => (bool)v ? 1f : 0f;

        return t;
    }

    private static double? TryToDouble(object value) => value switch
    {
        int i => i,
        long l => l,
        short s => s,
        byte b => b,
        float f => f,
        double d => d,
        bool bo => bo ? 1d : 0d,
        Enum e => System.Convert.ToDouble(e),
        _ => null,
    };

    private static object? FromDouble(Type target, double d, object original)
    {
        try
        {
            if (target == typeof(int)) return (int)d;
            if (target == typeof(long)) return (long)d;
            if (target == typeof(short)) return (short)d;
            if (target == typeof(byte)) return (byte)d;
            if (target == typeof(float)) return (float)d;
            if (target == typeof(double)) return d;
            if (target == typeof(bool)) return d != 0d;
            if (target == typeof(string)) return System.Convert.ToString(d, CultureInfo.InvariantCulture)!;
            if (target.IsEnum) return Enum.ToObject(target, (long)d);
        }
        catch
        {
            return null;
        }

        return null;
    }
}
