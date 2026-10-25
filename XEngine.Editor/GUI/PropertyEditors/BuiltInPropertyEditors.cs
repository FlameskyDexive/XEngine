// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using XEngine.Editor.GUI.Widgets;
using XEngine.Editor.Inspector;
using XEngine.Editor.Projects.Settings;
using XEngine.Editor.Theming;
using XEngine.OrigamiUI;
using XEngine.PaperUI;
using XEngine.Runtime;
using XEngine.Vector;

using AnimationCurve = XEngine.Runtime.AnimationCurve;

namespace XEngine.Editor.GUI.PropertyEditors;

// ================================================================
//  Numeric Types
// ================================================================

[CustomPropertyEditor(typeof(bool))]
public class BoolPropertyEditor : PropertyEditor
{
    public override void OnGUI(Paper paper, string id, string label, object? value, Action<object?> onChange, int depth)
    {
        EditorGUI.Row(paper, id, label, () =>
            Origami.Switch(paper, UiId.Underscore(id, "v"), (bool)(value ?? false), v => onChange(v))
                .Primary()
                .Show());
    }
}

[CustomPropertyEditor(typeof(int))]
public class IntPropertyEditor : PropertyEditor
{
    public override void OnGUI(Paper paper, string id, string label, object? value, Action<object?> onChange, int depth)
    {
        EditorGUI.Row(paper, id, label, () =>
            Origami.NumericField<int>(paper, UiId.Underscore(id, "v"), (int)(value ?? 0), v => onChange(v))
                .Show());
    }
}

[CustomPropertyEditor(typeof(float))]
public class FloatPropertyEditor : PropertyEditor
{
    public override void OnGUI(Paper paper, string id, string label, object? value, Action<object?> onChange, int depth)
    {
        EditorGUI.Row(paper, id, label, () =>
            Origami.NumericField<float>(paper, UiId.Underscore(id, "v"), (float)(value ?? 0f), v => onChange(v))
                .Show());
    }
}

[CustomPropertyEditor(typeof(double))]
public class DoublePropertyEditor : PropertyEditor
{
    public override void OnGUI(Paper paper, string id, string label, object? value, Action<object?> onChange, int depth)
    {
        EditorGUI.Row(paper, id, label, () =>
            Origami.NumericField<double>(paper, UiId.Underscore(id, "v"), (double)(value ?? 0.0), v => onChange(v))
                .Show());
    }
}

[CustomPropertyEditor(typeof(string))]
public class StringPropertyEditor : PropertyEditor
{
    public override void OnGUI(Paper paper, string id, string label, object? value, Action<object?> onChange, int depth)
    {
        EditorGUI.Row(paper, id, label, () =>
            Origami.TextField(paper, UiId.Underscore(id, "v"), (string)(value ?? string.Empty), v => onChange(v))
                .Show());
    }
}

[CustomPropertyEditor(typeof(byte))]
public class BytePropertyEditor : PropertyEditor
{
    public override void OnGUI(Paper paper, string id, string label, object? value, Action<object?> onChange, int depth)
    {
        EditorGUI.Row(paper, id, label, () =>
            Origami.NumericField<byte>(paper, UiId.Underscore(id, "v"), (byte)(value ?? (byte)0), v => onChange(v))
                .Min((byte)0).Max((byte)255)
                .Show());
    }
}

[CustomPropertyEditor(typeof(sbyte))]
public class SBytePropertyEditor : PropertyEditor
{
    public override void OnGUI(Paper paper, string id, string label, object? value, Action<object?> onChange, int depth)
    {
        EditorGUI.Row(paper, id, label, () =>
            Origami.NumericField<sbyte>(paper, UiId.Underscore(id, "v"), (sbyte)(value ?? (sbyte)0), v => onChange(v))
                .Min(sbyte.MinValue).Max(sbyte.MaxValue)
                .Show());
    }
}

[CustomPropertyEditor(typeof(short))]
public class ShortPropertyEditor : PropertyEditor
{
    public override void OnGUI(Paper paper, string id, string label, object? value, Action<object?> onChange, int depth)
    {
        EditorGUI.Row(paper, id, label, () =>
            Origami.NumericField<short>(paper, UiId.Underscore(id, "v"), (short)(value ?? (short)0), v => onChange(v))
                .Min(short.MinValue).Max(short.MaxValue)
                .Show());
    }
}

[CustomPropertyEditor(typeof(ushort))]
public class UShortPropertyEditor : PropertyEditor
{
    public override void OnGUI(Paper paper, string id, string label, object? value, Action<object?> onChange, int depth)
    {
        EditorGUI.Row(paper, id, label, () =>
            Origami.NumericField<ushort>(paper, UiId.Underscore(id, "v"), (ushort)(value ?? (ushort)0), v => onChange(v))
                .Min(ushort.MinValue).Max(ushort.MaxValue)
                .Show());
    }
}

[CustomPropertyEditor(typeof(long))]
public class LongPropertyEditor : PropertyEditor
{
    public override void OnGUI(Paper paper, string id, string label, object? value, Action<object?> onChange, int depth)
    {
        EditorGUI.Row(paper, id, label, () =>
            Origami.NumericField<long>(paper, UiId.Underscore(id, "v"), (long)(value ?? 0L), v => onChange(v))
                .Show());
    }
}

[CustomPropertyEditor(typeof(ulong))]
public class ULongPropertyEditor : PropertyEditor
{
    public override void OnGUI(Paper paper, string id, string label, object? value, Action<object?> onChange, int depth)
    {
        EditorGUI.Row(paper, id, label, () =>
            Origami.NumericField<ulong>(paper, UiId.Underscore(id, "v"), (ulong)(value ?? 0UL), v => onChange(v))
                .Show());
    }
}

[CustomPropertyEditor(typeof(uint))]
public class UIntPropertyEditor : PropertyEditor
{
    public override void OnGUI(Paper paper, string id, string label, object? value, Action<object?> onChange, int depth)
    {
        EditorGUI.Row(paper, id, label, () =>
            Origami.NumericField<uint>(paper, UiId.Underscore(id, "v"), (uint)(value ?? 0u), v => onChange(v))
                .Show());
    }
}

// ================================================================
//  Math Types  (vector / colour / curve composites - outside the
//  Origami dropdown/textfield migration scope; left on the legacy widgets)
// ================================================================

[CustomPropertyEditor(typeof(Float2))]
public class Float2PropertyEditor : PropertyEditor
{
    public override void OnGUI(Paper paper, string id, string label, object? value, Action<object?> onChange, int depth)
    {
        EditorGUI.Row(paper, id, label, () =>
            Origami.Float2Field(paper, UiId.Underscore(id, "vf"), (Float2)(value ?? Float2.Zero), v => onChange(v)).Show());
    }
}

[CustomPropertyEditor(typeof(Float3))]
public class Float3PropertyEditor : PropertyEditor
{
    public override void OnGUI(Paper paper, string id, string label, object? value, Action<object?> onChange, int depth)
    {
        EditorGUI.Row(paper, id, label, () =>
            Origami.Float3Field(paper, UiId.Underscore(id, "vf"), (Float3)(value ?? Float3.Zero), v => onChange(v)).Show());
    }
}

[CustomPropertyEditor(typeof(Float4))]
public class Float4PropertyEditor : PropertyEditor
{
    public override void OnGUI(Paper paper, string id, string label, object? value, Action<object?> onChange, int depth)
    {
        EditorGUI.Row(paper, id, label, () =>
            Origami.Float4Field(paper, UiId.Underscore(id, "vf"), (Float4)(value ?? Float4.Zero), v => onChange(v)).Show());
    }
}

[CustomPropertyEditor(typeof(Double2))]
public class Double2PropertyEditor : PropertyEditor
{
    public override void OnGUI(Paper paper, string id, string label, object? value, Action<object?> onChange, int depth)
    {
        EditorGUI.Row(paper, id, label, () =>
            Origami.Double2Field(paper, UiId.Underscore(id, "vf"), (Double2)(value ?? Double2.Zero), v => onChange(v)).Show());
    }
}

[CustomPropertyEditor(typeof(Double3))]
public class Double3PropertyEditor : PropertyEditor
{
    public override void OnGUI(Paper paper, string id, string label, object? value, Action<object?> onChange, int depth)
    {
        EditorGUI.Row(paper, id, label, () =>
            Origami.Double3Field(paper, UiId.Underscore(id, "vf"), (Double3)(value ?? Double3.Zero), v => onChange(v)).Show());
    }
}

[CustomPropertyEditor(typeof(Double4))]
public class Double4PropertyEditor : PropertyEditor
{
    public override void OnGUI(Paper paper, string id, string label, object? value, Action<object?> onChange, int depth)
    {
        EditorGUI.Row(paper, id, label, () =>
            Origami.Double4Field(paper, UiId.Underscore(id, "vf"), (Double4)(value ?? Double4.Zero), v => onChange(v)).Show());
    }
}

[CustomPropertyEditor(typeof(Int2))]
public class Int2PropertyEditor : PropertyEditor
{
    public override void OnGUI(Paper paper, string id, string label, object? value, Action<object?> onChange, int depth)
    {
        EditorGUI.Row(paper, id, label, () =>
            Origami.Int2Field(paper, UiId.Underscore(id, "vf"), (Int2)(value ?? Int2.Zero), v => onChange(v)).Show());
    }
}

[CustomPropertyEditor(typeof(Int3))]
public class Int3PropertyEditor : PropertyEditor
{
    public override void OnGUI(Paper paper, string id, string label, object? value, Action<object?> onChange, int depth)
    {
        EditorGUI.Row(paper, id, label, () =>
            Origami.Int3Field(paper, UiId.Underscore(id, "vf"), (Int3)(value ?? Int3.Zero), v => onChange(v)).Show());
    }
}

[CustomPropertyEditor(typeof(Int4))]
public class Int4PropertyEditor : PropertyEditor
{
    public override void OnGUI(Paper paper, string id, string label, object? value, Action<object?> onChange, int depth)
    {
        EditorGUI.Row(paper, id, label, () =>
            Origami.Int4Field(paper, UiId.Underscore(id, "vf"), (Int4)(value ?? Int4.Zero), v => onChange(v)).Show());
    }
}

[CustomPropertyEditor(typeof(Color))]
public class ColorPropertyEditor : PropertyEditor
{
    private static ColorPalette? s_palette;

    private static ColorPalette GetEditorPalette()
    {
        if (s_palette != null) return s_palette;

        var settings = EditorRegistries.GetSettings<ProjectsEditorSettings>();
        // Convert hex strings to Color list, keep in sync
        var colors = new List<Color>();
        foreach (var hex in settings.ColorPalette)
        {
            var sc = ColorRamp.ParseHex(hex);
            colors.Add(new Color(sc.R / 255f, sc.G / 255f, sc.B / 255f, 1f));
        }

        s_palette = new ColorPalette(colors)
        {
            OnAdd = () =>
            {
                // Return the color to add - caller provides current color via the palette list
                return null; // Will be overridden per-instance below
            },
            OnRemoved = idx =>
            {
                if (idx >= 0 && idx < settings.ColorPalette.Count)
                {
                    settings.ColorPalette.RemoveAt(idx);
                    EditorRegistries.SaveSettings();
                }
            },
        };
        return s_palette;
    }

    public override void OnGUI(Paper paper, string id, string label, object? value, Action<object?> onChange, int depth)
    {
        var color = (Color)(value ?? new Color(1, 1, 1, 1));
        var palette = GetEditorPalette();

        // Set the OnAdd to capture the current color value
        var capturedColor = color;
        palette.OnAdd = () =>
        {
            var settings = EditorRegistries.GetSettings<ProjectsEditorSettings>();
            int r = Math.Clamp((int)(capturedColor.R * 255), 0, 255);
            int g = Math.Clamp((int)(capturedColor.G * 255), 0, 255);
            int b = Math.Clamp((int)(capturedColor.B * 255), 0, 255);
            string hex = $"#{r:X2}{g:X2}{b:X2}";
            if (!settings.ColorPalette.Contains(hex))
            {
                settings.ColorPalette.Add(hex);
                palette.Colors.Add(new Color(capturedColor.R, capturedColor.G, capturedColor.B, 1f));
                EditorRegistries.SaveSettings();
            }
            return null; // Already added manually
        };

        EditorGUI.Row(paper, id, label, () =>
            Origami.ColorField(paper, UiId.Underscore(id, "cf"), color, v => onChange(v))
                .Palette(palette).Show());
    }
}

[CustomPropertyEditor(typeof(Quaternion))]
public class QuaternionPropertyEditor : PropertyEditor
{
    public override void OnGUI(Paper paper, string id, string label, object? value, Action<object?> onChange, int depth)
    {
        var q = (Quaternion)(value ?? Quaternion.Identity);
        EditorGUI.Row(paper, id, label, () =>
            Origami.Float3Field(paper, UiId.Underscore(id, "vf"), q.EulerAngles, v => onChange(Quaternion.FromEuler(v))).Show());
    }
}

// ================================================================
//  Special Types
// ================================================================

[CustomPropertyEditor(typeof(AnimationCurve))]
public class AnimationCurvePropertyEditor : PropertyEditor
{
    public override void OnGUI(Paper paper, string id, string label, object? value, Action<object?> onChange, int depth)
    {
        EditorGUI.Row(paper, id, label, () =>
            CurveField.Create(paper, UiId.Underscore(id, "cf"),
                (AnimationCurve)(value ?? new AnimationCurve()),
                v => onChange(v)).Show());
    }
}

[CustomPropertyEditor(typeof(Guid))]
public class GuidPropertyEditor : PropertyEditor
{
    public override void OnGUI(Paper paper, string id, string label, object? value, Action<object?> onChange, int depth)
    {
        Origami.Label(paper, id, $"{label}: {value}").Show();
    }
}
