// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.
//
// Built-in attribute handlers for the Origami PropertyGrid.
// These handle Runtime attributes ([Range], [Header], [Space], etc.)
// and are registered by the editor at startup.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Reflection;

using XEngine.PaperUI;
using XEngine.PaperUI.LayoutEngine;
using XEngine.Runtime;

namespace XEngine.Editor.GUI;

/// <summary>[Header("text")] - draws a header label above the field.</summary>
public class HeaderAttributeHandler : OrigamiUI.AttributeHandler
{
    public override bool OnBeforeDraw(Paper paper, string id, Attribute attr, FieldInfo field, object target, int depth)
    {
        var header = (HeaderAttribute)attr;
        OrigamiUI.Origami.Header(paper, $"{id}_header", header.Text).Show();
        return true;
    }
}

/// <summary>[Space] or [Space(height)] - draws vertical spacing above the field.</summary>
public class SpaceAttributeHandler : OrigamiUI.AttributeHandler
{
    public override bool OnBeforeDraw(Paper paper, string id, Attribute attr, FieldInfo field, object target, int depth)
    {
        var space = (SpaceAttribute)attr;
        paper.Box($"{id}_space").Height(space.Height);
        return true;
    }
}

/// <summary>[ShowIf("memberName")] - hides the field if the named bool member is false.</summary>
public class ShowIfAttributeHandler : OrigamiUI.AttributeHandler
{
    public override bool OnBeforeDraw(Paper paper, string id, Attribute attr, FieldInfo field, object target, int depth)
    {
        var showIf = (ShowIfAttribute)attr;
        var type = target.GetType();

        // Check field
        var condField = type.GetField(showIf.ConditionMember, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (condField != null && condField.FieldType == typeof(bool))
            return (bool)(condField.GetValue(target) ?? false);

        // Check property
        var condProp = type.GetProperty(showIf.ConditionMember, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (condProp != null && condProp.PropertyType == typeof(bool))
            return (bool)(condProp.GetValue(target) ?? false);

        return true; // Condition not found, show by default
    }
}

/// <summary>[ReadOnly] - makes the field non-editable.</summary>
public class ReadOnlyAttributeHandler : OrigamiUI.AttributeHandler
{
    public override bool OnBeforeDraw(Paper paper, string id, Attribute attr, FieldInfo field, object target, int depth)
    {
        OrigamiUI.Origami.BeginReadOnly();
        return true;
    }

    public override void OnAfterDraw(Paper paper, string id, Attribute attr, FieldInfo field, object target, int depth)
    {
        OrigamiUI.Origami.EndReadOnly();
    }
}

/// <summary>
/// [EnableIf("memberName")] - greys the field out (visible, not editable) while the named bool
/// member is false. Same condition resolution as [ShowIf] (field first, then property). Ported
/// from upstream (01658335).
/// </summary>
public class EnableIfAttributeHandler : OrigamiUI.AttributeHandler
{
    private bool _pushedReadOnly;

    public override bool OnBeforeDraw(Paper paper, string id, Attribute attr, FieldInfo field, object target, int depth)
    {
        var enableIf = (EnableIfAttribute)attr;
        bool enabled = EvaluateCondition(enableIf.ConditionMember, target);

        // Singleton handler, so the pushed scope is remembered per draw and released in OnAfterDraw.
        // Conditions are booleans read before the control is drawn; a draw cannot flip its own gate.
        _pushedReadOnly = !enabled;
        if (_pushedReadOnly) OrigamiUI.Origami.BeginReadOnly();
        return true;
    }

    public override void OnAfterDraw(Paper paper, string id, Attribute attr, FieldInfo field, object target, int depth)
    {
        if (_pushedReadOnly)
        {
            OrigamiUI.Origami.EndReadOnly();
            _pushedReadOnly = false;
        }
    }

    private static bool EvaluateCondition(string member, object target)
    {
        var type = target.GetType();

        var condField = type.GetField(member, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (condField != null && condField.FieldType == typeof(bool))
            return (bool)(condField.GetValue(target) ?? false);

        var condProp = type.GetProperty(member, BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
        if (condProp != null && condProp.PropertyType == typeof(bool))
            return (bool)(condProp.GetValue(target) ?? false);

        return true; // Condition not found: keep the field editable rather than silently locked.
    }
}

/// <summary>[Range(min, max)] - replaces numeric fields with a slider.</summary>
public class RangeAttributeHandler : OrigamiUI.AttributeHandler
{
    public override bool OnDraw(Paper paper, string id, string label, Attribute attr,
        FieldInfo field, object target, Action<object?> onChange, int depth)
    {
        var range = (RangeAttribute)attr;
        var value = field.GetValue(target);
        var type = field.FieldType;
        var theme = OrigamiUI.Origami.Current;
        var m = theme.Metrics;
        var font = theme.Font;
        var ink = theme.Ink;

        using (paper.Row(id).Height(UnitValue.Auto).MinHeight(m.RowHeight)
            .RowBetween(m.SpacingMedium).Margin(0, 0, 0, m.SpacingSmall).Enter())
        {
            if (font != null && !string.IsNullOrEmpty(label))
            {
                paper.Box($"{id}_lbl")
                    .Width(m.LabelWidth).Height(m.RowHeight)
                    .Padding(m.PaddingSmall, 0, 0, 0)
                    .IsNotInteractable()
                    .Text(label, font).TextColor(ink.C500)
                    .FontSize(m.FontSize);
            }

            using (paper.Box($"{id}_ctl").Width(UnitValue.Stretch())
                .Height(m.RowHeight).Enter())
            {
                if (type == typeof(float))
                {
                    float f = (float)(value ?? 0f);
                    OrigamiUI.Origami.Slider(paper, $"{id}_sl", f,
                        v => onChange(v), range.Min, range.Max).Format("F2").Show();
                }
                else if (type == typeof(int))
                {
                    int i = (int)(value ?? 0);
                    OrigamiUI.Origami.Slider(paper, $"{id}_sl", (float)i,
                        v => onChange((int)MathF.Round(v)), range.Min, range.Max)
                        .Format("F0").Step(1f).Show();
                }
                else
                {
                    return false;
                }
            }
        }

        return true;
    }
}

/// <summary>[TextArea(min, max)] - replaces string field with a multiline text area.</summary>
public class TextAreaAttributeHandler : OrigamiUI.AttributeHandler
{
    public override bool OnDraw(Paper paper, string id, string label, Attribute attr,
        FieldInfo field, object target, Action<object?> onChange, int depth)
    {
        var textArea = (TextAreaAttribute)attr;
        var value = (string?)field.GetValue(target) ?? "";
        var theme = OrigamiUI.Origami.Current;
        var m = theme.Metrics;

        using (paper.Row(id).Height(UnitValue.Auto).MinHeight(m.RowHeight)
            .RowBetween(m.SpacingMedium).Margin(0, 0, 0, m.SpacingSmall).Enter())
        {
            var font = theme.Font;
            var ink = theme.Ink;
            if (font != null)
                paper.Box($"{id}_lbl")
                    .Width(m.LabelWidth).Height(m.RowHeight).Padding(m.PaddingSmall, 0, 0, 0)
                    .IsNotInteractable()
                    .Text(label, font).TextColor(ink.C500)
                    .FontSize(m.FontSize);

            using (paper.Box($"{id}_ctl").Width(UnitValue.Stretch()).Height(UnitValue.Auto).Enter())
            {
                OrigamiUI.Origami.TextArea(paper, $"{id}_ta", value, v => onChange(v), textArea.MaxLines).Show();
            }
        }

        return true;
    }
}

/// <summary>[Min(min)] - numeric field clamped to a lower bound.</summary>
public class MinAttributeHandler : OrigamiUI.AttributeHandler
{
    public override bool OnDraw(Paper paper, string id, string label, Attribute attr,
        FieldInfo field, object target, Action<object?> onChange, int depth)
    {
        var min = (MinAttribute)attr;
        var value = field.GetValue(target);
        var type = field.FieldType;
        var theme = OrigamiUI.Origami.Current;
        var m = theme.Metrics;
        var font = theme.Font;
        var ink = theme.Ink;

        using (paper.Row(id).Height(UnitValue.Auto).MinHeight(m.RowHeight)
            .RowBetween(m.SpacingMedium).Margin(0, 0, 0, m.SpacingSmall).Enter())
        {
            if (font != null && !string.IsNullOrEmpty(label))
            {
                paper.Box($"{id}_lbl")
                    .Width(m.LabelWidth).Height(m.RowHeight)
                    .Padding(m.PaddingSmall, 0, 0, 0)
                    .IsNotInteractable()
                    .Text(label, font).TextColor(ink.C500)
                    .FontSize(m.FontSize);
            }

            using (paper.Box($"{id}_ctl").Width(UnitValue.Stretch())
                .Height(m.RowHeight).Enter())
            {
                if (type == typeof(float))
                {
                    float f = (float)(value ?? 0f);
                    OrigamiUI.Origami.NumericField<float>(paper, $"{id}_nf", f,
                        v => onChange(v < min.Min ? min.Min : v)).Min(min.Min).Show();
                }
                else if (type == typeof(int))
                {
                    int lo = (int)MathF.Ceiling(min.Min);
                    int i = (int)(value ?? 0);
                    OrigamiUI.Origami.NumericField<int>(paper, $"{id}_nf", i,
                        v => onChange(v < lo ? lo : v)).Min(lo).Show();
                }
                else if (type == typeof(double))
                {
                    double d = (double)(value ?? 0.0);
                    OrigamiUI.Origami.NumericField<double>(paper, $"{id}_nf", d,
                        v => onChange(v < min.Min ? (double)min.Min : v)).Min(min.Min).Show();
                }
                else
                {
                    return false;
                }
            }
        }

        return true;
    }
}

/// <summary>[Multiline] or [Multiline(lines)] - string field as a fixed-height text area.</summary>
public class MultilineAttributeHandler : OrigamiUI.AttributeHandler
{
    public override bool OnDraw(Paper paper, string id, string label, Attribute attr,
        FieldInfo field, object target, Action<object?> onChange, int depth)
    {
        if (field.FieldType != typeof(string)) return false;
        var multiline = (MultilineAttribute)attr;
        var value = (string?)field.GetValue(target) ?? "";
        var theme = OrigamiUI.Origami.Current;
        var m = theme.Metrics;

        using (paper.Row(id).Height(UnitValue.Auto).MinHeight(m.RowHeight)
            .RowBetween(m.SpacingMedium).Margin(0, 0, 0, m.SpacingSmall).Enter())
        {
            var font = theme.Font;
            var ink = theme.Ink;
            if (font != null)
                paper.Box($"{id}_lbl")
                    .Width(m.LabelWidth).Height(m.RowHeight).Padding(m.PaddingSmall, 0, 0, 0)
                    .IsNotInteractable()
                    .Text(label, font).TextColor(ink.C500)
                    .FontSize(m.FontSize);

            using (paper.Box($"{id}_ctl").Width(UnitValue.Stretch()).Height(UnitValue.Auto).Enter())
            {
                OrigamiUI.Origami.TextArea(paper, $"{id}_ta", value, v => onChange(v), multiline.Lines).Show();
            }
        }

        return true;
    }
}

/// <summary>
/// [Delayed] - the field writes back only when the edit is committed with Enter; Escape reverts.
/// Supports string / float / int / double fields (Unity's Delayed semantics, Enter-commit variant).
/// </summary>
public class DelayedAttributeHandler : OrigamiUI.AttributeHandler
{
    private readonly Dictionary<string, string> _buffers = new();

    public override bool OnDraw(Paper paper, string id, string label, Attribute attr,
        FieldInfo field, object target, Action<object?> onChange, int depth)
    {
        var type = field.FieldType;
        if (type != typeof(string) && type != typeof(float) && type != typeof(int) && type != typeof(double))
            return false;

        object? raw = field.GetValue(target);
        string current = raw switch
        {
            null => "",
            float f => f.ToString("R", CultureInfo.InvariantCulture),
            double d => d.ToString("R", CultureInfo.InvariantCulture),
            int i => i.ToString(CultureInfo.InvariantCulture),
            _ => (string)raw,
        };
        bool editing = _buffers.TryGetValue(id, out string? buffer);
        string shown = editing ? buffer! : current;

        var theme = OrigamiUI.Origami.Current;
        var m = theme.Metrics;
        var font = theme.Font;
        var ink = theme.Ink;

        using (paper.Row(id).Height(UnitValue.Auto).MinHeight(m.RowHeight)
            .RowBetween(m.SpacingMedium).Margin(0, 0, 0, m.SpacingSmall).Enter())
        {
            if (font != null && !string.IsNullOrEmpty(label))
            {
                paper.Box($"{id}_lbl")
                    .Width(m.LabelWidth).Height(m.RowHeight)
                    .Padding(m.PaddingSmall, 0, 0, 0)
                    .IsNotInteractable()
                    .Text(label, font).TextColor(editing ? ink.C300 : ink.C500)
                    .FontSize(m.FontSize);
            }

            using (paper.Box($"{id}_ctl").Width(UnitValue.Stretch()).Height(m.RowHeight).Enter())
            {
                OrigamiUI.Origami.TextField(paper, $"{id}_tf", shown, v => _buffers[id] = v ?? "").Show();
            }
        }

        if (editing)
        {
            if (paper.IsKeyPressed(PaperKey.Enter) || paper.IsKeyPressed(PaperKey.KeypadEnter))
            {
                Commit(type, buffer!, current, onChange);
                _buffers.Remove(id);
            }
            else if (paper.IsKeyPressed(PaperKey.Escape))
            {
                _buffers.Remove(id);
            }
        }

        return true;
    }

    private static void Commit(Type type, string text, string current, Action<object?> onChange)
    {
        if (type == typeof(string))
        {
            onChange(text);
            return;
        }
        if (text == current) return;
        if (type == typeof(float) && float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out float f))
            onChange(f);
        else if (type == typeof(int) && int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int i))
            onChange(i);
        else if (type == typeof(double) && double.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out double d))
            onChange(d);
        // Unparsable numeric input reverts to the committed value (buffer is dropped by the caller).
    }
}

/// <summary>
/// Registers all built-in attribute handlers with the Origami AttributeHandlerRegistry.
/// Called once at editor startup.
/// </summary>
public static class BuiltInAttributeHandlers
{
    public static void Register(OrigamiUI.AttributeHandlerRegistry registry)
    {
        registry.Register<HeaderAttribute>(new HeaderAttributeHandler());
        registry.Register<SpaceAttribute>(new SpaceAttributeHandler());
        registry.Register<ShowIfAttribute>(new ShowIfAttributeHandler());
        registry.Register<EnableIfAttribute>(new EnableIfAttributeHandler());
        registry.Register<ReadOnlyAttribute>(new ReadOnlyAttributeHandler());
        registry.Register<RangeAttribute>(new RangeAttributeHandler());
        registry.Register<TextAreaAttribute>(new TextAreaAttributeHandler());
        registry.Register<MinAttribute>(new MinAttributeHandler());
        registry.Register<MultilineAttribute>(new MultilineAttributeHandler());
        registry.Register<DelayedAttribute>(new DelayedAttributeHandler());
        // TooltipAttribute needs no handler: PropertyGridConfig.ResolveTooltip (EditorApplication)
        // feeds the grid's row tooltips directly.
        // InspectorNameAttribute needs no handler: PropertyGrid.ResolveFieldLabel reads it when
        // building the cached field label.
    }
}
