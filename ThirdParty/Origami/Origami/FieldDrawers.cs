// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using XEngine.PaperUI;
using XEngine.PaperUI.LayoutEngine;
using XEngine.Vector;

namespace XEngine.OrigamiUI;

// ── Primitives ──────────────────────────────────────────────

public class BoolDrawer : FieldDrawer
{
    public override void Draw(Paper paper, string id, object? value, Type fieldType, Action<object?> onChange, int depth)
        => Origami.Switch(paper, id, (bool)(value ?? false), v => onChange(v)).Primary().Show();
}

public class IntDrawer : FieldDrawer
{
    public override void Draw(Paper paper, string id, object? value, Type fieldType, Action<object?> onChange, int depth)
        => Origami.NumericField<int>(paper, id, (int)(value ?? 0), v => onChange(v)).MixedValue(PropertyGridRenderer.IsMixedField).Show();
}

public class FloatDrawer : FieldDrawer
{
    public override void Draw(Paper paper, string id, object? value, Type fieldType, Action<object?> onChange, int depth)
        => Origami.NumericField<float>(paper, id, (float)(value ?? 0f), v => onChange(v)).MixedValue(PropertyGridRenderer.IsMixedField).Show();
}

public class DoubleDrawer : FieldDrawer
{
    public override void Draw(Paper paper, string id, object? value, Type fieldType, Action<object?> onChange, int depth)
        => Origami.NumericField<double>(paper, id, (double)(value ?? 0.0), v => onChange(v)).MixedValue(PropertyGridRenderer.IsMixedField).Show();
}

public class StringDrawer : FieldDrawer
{
    public override void Draw(Paper paper, string id, object? value, Type fieldType, Action<object?> onChange, int depth)
        => Origami.TextField(paper, id, (string?)value ?? "", v => onChange(v)).Show();
}

public class ByteDrawer : FieldDrawer
{
    public override void Draw(Paper paper, string id, object? value, Type fieldType, Action<object?> onChange, int depth)
        => Origami.NumericField<byte>(paper, id, (byte)(value ?? (byte)0), v => onChange(v)).Min(0).Max(255).MixedValue(PropertyGridRenderer.IsMixedField).Show();
}

public class LongDrawer : FieldDrawer
{
    public override void Draw(Paper paper, string id, object? value, Type fieldType, Action<object?> onChange, int depth)
        => Origami.NumericField<long>(paper, id, (long)(value ?? 0L), v => onChange(v)).MixedValue(PropertyGridRenderer.IsMixedField).Show();
}

public class SByteDrawer : FieldDrawer
{
    public override void Draw(Paper paper, string id, object? value, Type fieldType, Action<object?> onChange, int depth)
        => Origami.NumericField<sbyte>(paper, id, (sbyte)(value ?? (sbyte)0), v => onChange(v)).MixedValue(PropertyGridRenderer.IsMixedField).Show();
}

public class ShortDrawer : FieldDrawer
{
    public override void Draw(Paper paper, string id, object? value, Type fieldType, Action<object?> onChange, int depth)
        => Origami.NumericField<short>(paper, id, (short)(value ?? (short)0), v => onChange(v)).MixedValue(PropertyGridRenderer.IsMixedField).Show();
}

public class UShortDrawer : FieldDrawer
{
    public override void Draw(Paper paper, string id, object? value, Type fieldType, Action<object?> onChange, int depth)
        => Origami.NumericField<ushort>(paper, id, (ushort)(value ?? (ushort)0), v => onChange(v)).MixedValue(PropertyGridRenderer.IsMixedField).Show();
}

public class UIntDrawer : FieldDrawer
{
    public override void Draw(Paper paper, string id, object? value, Type fieldType, Action<object?> onChange, int depth)
        => Origami.NumericField<uint>(paper, id, (uint)(value ?? 0u), v => onChange(v)).MixedValue(PropertyGridRenderer.IsMixedField).Show();
}

public class ULongDrawer : FieldDrawer
{
    public override void Draw(Paper paper, string id, object? value, Type fieldType, Action<object?> onChange, int depth)
        => Origami.NumericField<ulong>(paper, id, (ulong)(value ?? 0ul), v => onChange(v)).MixedValue(PropertyGridRenderer.IsMixedField).Show();
}

public class DecimalDrawer : FieldDrawer
{
    public override void Draw(Paper paper, string id, object? value, Type fieldType, Action<object?> onChange, int depth)
        => Origami.NumericField<decimal>(paper, id, (decimal)(value ?? 0m), v => onChange(v)).MixedValue(PropertyGridRenderer.IsMixedField).Show();
}

// ── Vectors ─────────────────────────────────────────────────

public class Float2Drawer : FieldDrawer
{
    public override void Draw(Paper paper, string id, object? value, Type fieldType, Action<object?> onChange, int depth)
        => Origami.Float2Field(paper, id, (Float2)(value ?? Float2.Zero), v => onChange(v)).Show();
}

public class Float3Drawer : FieldDrawer
{
    public override void Draw(Paper paper, string id, object? value, Type fieldType, Action<object?> onChange, int depth)
        => Origami.Float3Field(paper, id, (Float3)(value ?? Float3.Zero), v => onChange(v)).Show();
}

public class Float4Drawer : FieldDrawer
{
    public override void Draw(Paper paper, string id, object? value, Type fieldType, Action<object?> onChange, int depth)
        => Origami.Float4Field(paper, id, (Float4)(value ?? Float4.Zero), v => onChange(v)).Show();
}

public class Double2Drawer : FieldDrawer
{
    public override void Draw(Paper paper, string id, object? value, Type fieldType, Action<object?> onChange, int depth)
        => Origami.Double2Field(paper, id, (Double2)(value ?? Double2.Zero), v => onChange(v)).Show();
}

public class Double3Drawer : FieldDrawer
{
    public override void Draw(Paper paper, string id, object? value, Type fieldType, Action<object?> onChange, int depth)
        => Origami.Double3Field(paper, id, (Double3)(value ?? Double3.Zero), v => onChange(v)).Show();
}

public class Double4Drawer : FieldDrawer
{
    public override void Draw(Paper paper, string id, object? value, Type fieldType, Action<object?> onChange, int depth)
        => Origami.Double4Field(paper, id, (Double4)(value ?? Double4.Zero), v => onChange(v)).Show();
}

public class Int2Drawer : FieldDrawer
{
    public override void Draw(Paper paper, string id, object? value, Type fieldType, Action<object?> onChange, int depth)
        => Origami.Int2Field(paper, id, (Int2)(value ?? Int2.Zero), v => onChange(v)).Show();
}

public class Int3Drawer : FieldDrawer
{
    public override void Draw(Paper paper, string id, object? value, Type fieldType, Action<object?> onChange, int depth)
        => Origami.Int3Field(paper, id, (Int3)(value ?? Int3.Zero), v => onChange(v)).Show();
}

public class Int4Drawer : FieldDrawer
{
    public override void Draw(Paper paper, string id, object? value, Type fieldType, Action<object?> onChange, int depth)
        => Origami.Int4Field(paper, id, (Int4)(value ?? Int4.Zero), v => onChange(v)).Show();
}

// ── Rect / Bounds ───────────────────────────────────────────

/// <summary>2D rectangle as two labeled corner rows (Min / Max), matching the struct's fields.</summary>
public class RectDrawer : FieldDrawer
{
    public override void Draw(Paper paper, string id, object? value, Type fieldType, Action<object?> onChange, int depth)
    {
        var r = (Rect)(value ?? default(Rect));
        using (paper.Column($"{id}_col").Width(UnitValue.Stretch()).Height(UnitValue.Auto).ColBetween(2).Enter())
        {
            DrawCornerRow2(paper, $"{id}_min", "Min", r.Min, v => { var n = r; n.Min = v; onChange(n); });
            DrawCornerRow2(paper, $"{id}_max", "Max", r.Max, v => { var n = r; n.Max = v; onChange(n); });
        }
    }

    internal static void DrawCornerRow2(Paper paper, string id, string label, Float2 v, Action<Float2> set)
    {
        var theme = Origami.Current;
        var m = theme.Metrics;
        using (paper.Row(id).Height(m.RowHeight).RowBetween(m.SpacingMedium).Enter())
        {
            if (theme.Font != null)
                paper.Box($"{id}_l").Width(30).Height(m.RowHeight).IsNotInteractable()
                    .Text(label, theme.Font).TextColor(theme.Ink.C500).FontSize(m.FontSize);
            Origami.Float2Field(paper, $"{id}_f", v, x => set(x)).Show();
        }
    }
}

/// <summary>3D axis-aligned box as two labeled corner rows (Min / Max), matching the struct's fields.</summary>
public class AABBDrawer : FieldDrawer
{
    public override void Draw(Paper paper, string id, object? value, Type fieldType, Action<object?> onChange, int depth)
    {
        var b = (AABB)(value ?? default(AABB));
        using (paper.Column($"{id}_col").Width(UnitValue.Stretch()).Height(UnitValue.Auto).ColBetween(2).Enter())
        {
            DrawCornerRow3(paper, $"{id}_min", "Min", b.Min, v => { var n = b; n.Min = v; onChange(n); });
            DrawCornerRow3(paper, $"{id}_max", "Max", b.Max, v => { var n = b; n.Max = v; onChange(n); });
        }
    }

    static void DrawCornerRow3(Paper paper, string id, string label, Float3 v, Action<Float3> set)
    {
        var theme = Origami.Current;
        var m = theme.Metrics;
        using (paper.Row(id).Height(m.RowHeight).RowBetween(m.SpacingMedium).Enter())
        {
            if (theme.Font != null)
                paper.Box($"{id}_l").Width(30).Height(m.RowHeight).IsNotInteractable()
                    .Text(label, theme.Font).TextColor(theme.Ink.C500).FontSize(m.FontSize);
            Origami.Float3Field(paper, $"{id}_f", v, x => set(x)).Show();
        }
    }
}

// ── Color ───────────────────────────────────────────────────

public class ColorDrawer : FieldDrawer
{
    public override void Draw(Paper paper, string id, object? value, Type fieldType, Action<object?> onChange, int depth)
        => Origami.ColorField(paper, id, (XEngine.Vector.Color)(value ?? new XEngine.Vector.Color(1, 1, 1, 1)), v => onChange(v)).Show();
}

// ── Registration ────────────────────────────────────────────

/// <summary>Registers all built-in field drawers into the given registry.</summary>
public static class BuiltInFieldDrawers
{
    public static void Register(FieldDrawerRegistry registry)
    {
        registry.Register<bool>(new BoolDrawer());
        registry.Register<int>(new IntDrawer());
        registry.Register<float>(new FloatDrawer());
        registry.Register<double>(new DoubleDrawer());
        registry.Register<string>(new StringDrawer());
        registry.Register<byte>(new ByteDrawer());
        registry.Register<long>(new LongDrawer());
        registry.Register<sbyte>(new SByteDrawer());
        registry.Register<short>(new ShortDrawer());
        registry.Register<ushort>(new UShortDrawer());
        registry.Register<uint>(new UIntDrawer());
        registry.Register<ulong>(new ULongDrawer());
        registry.Register<decimal>(new DecimalDrawer());

        registry.Register<Float2>(new Float2Drawer());
        registry.Register<Float3>(new Float3Drawer());
        registry.Register<Float4>(new Float4Drawer());
        registry.Register<Double2>(new Double2Drawer());
        registry.Register<Double3>(new Double3Drawer());
        registry.Register<Double4>(new Double4Drawer());
        registry.Register<Int2>(new Int2Drawer());
        registry.Register<Int3>(new Int3Drawer());
        registry.Register<Int4>(new Int4Drawer());

        registry.Register<XEngine.Vector.Color>(new ColorDrawer());

        registry.Register<Rect>(new RectDrawer());
        registry.Register<AABB>(new AABBDrawer());
    }
}
