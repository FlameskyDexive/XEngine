using System;
using System.Collections.Generic;
using XEngine.PaperUI.LayoutEngine;

namespace XEngine.PaperUI;

/// <summary>Bounded, exact comparison of final Paper paint inputs, evaluated at paint time.
/// This tracks visual dependencies, not model identity or input subscriptions. Unsupported
/// dynamic content remains dirty. External parent context/atlas validity belongs to the cache.</summary>
public sealed class PaperSubtreeVisualState : IDisposable
{
    private readonly List<Atom> _values = new();
    private readonly int _maximumElements;
    private Paper? _owner;
    private int _position, _elements;
    private bool _valid, _changed, _volatile;

    public PaperSubtreeVisualState(int maximumElements = 4096)
    {
        if (maximumElements < 1) throw new ArgumentOutOfRangeException(nameof(maximumElements));
        _maximumElements = maximumElements;
    }

    public bool HasChanged(Paper paper, ElementHandle root)
    {
        if (!root.IsValid || !ReferenceEquals(root.Owner, paper)) throw new ArgumentException("Invalid paint root", nameof(root));
        _changed = !_valid || !ReferenceEquals(_owner, paper);
        _owner = paper; _position = 0; _elements = 0; _volatile = false;
        try
        {
            Visit(paper, root);
            if (_position != _values.Count)
            { _changed = true; _values.RemoveRange(_position, _values.Count - _position); }
            _valid = !_volatile;
            return _changed || _volatile;
        }
        catch { Clear(); throw; }
    }

    private void Visit(Paper paper, ElementHandle handle)
    {
        if (++_elements > _maximumElements) throw new InvalidOperationException("Paint snapshot exceeds its element limit.");
        ref var d = ref handle.Data;
        Add(d.ID); Add(d.Visible ? 1 : 0);
        if (!d.Visible) return;
        Add(d.Layer); Add(d.X); Add(d.Y); Add(d.LayoutWidth); Add(d.LayoutHeight);
        Add(d._scissorEnabled ? 1 : 0);
        var style = d._elementStyle;
        var transform = style.GetTransformForElement(d.LayoutRect);
        Add(transform.A); Add(transform.B); Add(transform.C); Add(transform.D); Add(transform.E); Add(transform.F);
        var round = style.GetRounded(); Add(round.X); Add(round.Y); Add(round.Z); Add(round.W);
        AddColor(style.GetBackgroundColor()); AddColor(style.GetBorderColor()); Add(style.GetBorderWidth());
        var shadow = style.GetBoxShadow();
        Add(shadow.OffsetX); Add(shadow.OffsetY); Add(shadow.Blur); Add(shadow.Spread); Add(shadow.Color.ToArgb());
        var gradient = style.GetBackgroundGradient();
        Add((int)gradient.Type); Add(gradient.Color1.ToArgb()); Add(gradient.Color2.ToArgb());
        Add(gradient.X1); Add(gradient.Y1); Add(gradient.X2); Add(gradient.Y2);
        Add(gradient.InnerRadius); Add(gradient.OuterRadius); Add(gradient.Width); Add(gradient.Height);
        Add(gradient.Radius); Add(gradient.Feather); Add(style.GetBackdropBlur());
        // External images may change in place and backdrop samples change outside this subtree.
        if (style.GetBackgroundImage() != null || style.GetBackdropBlur() > 0 || d.IsMarkdown || d.IsRichText)
            _volatile = true;
        Add(new Atom(0, d.Paragraph, true));
        Add(new Atom(0, d.Font)); Add(new Atom(0, d.FontBold)); Add(new Atom(0, d.FontItalic));
        Add(new Atom(0, d.FontBoldItalic)); Add(new Atom(0, d.FontMono));
        Add((int)d.FontStyle); Add((int)d.WrapMode); Add(d.Truncate ? 1 : 0); Add((int)d.TextAlignment);
        AddColor(style.GetTextColor()); Add(style.GetFontSize()); Add(style.GetLineHeight());
        Add(style.GetLetterSpacing()); Add(style.GetWordSpacing()); Add(style.GetTabSize()); Add((int)style.GetTextQuality());
        int commands = d._renderCommands?.Count ?? 0, foreground = d._foregroundRenderCommands?.Count ?? 0;
        Add(commands); Add(foreground);
        if (commands != 0 || foreground != 0)
        {
            ReadCommands(d._renderCommands, d.CustomPaintVersion);
            ReadCommands(d._foregroundRenderCommands, d.CustomPaintVersion);
        }
        Add(d.ChildIndices.Count);
        for (int i = 0; i < d.ChildIndices.Count; i++) Visit(paper, new ElementHandle(paper, d.ChildIndices[i]));
    }

    private void Add(int value) => Add(new Atom(value, null));
    private void ReadCommands(List<ElementRenderCommand>? commands, ulong? elementVersion)
    {
        if (commands == null) return;
        for (int i = 0; i < commands.Count; i++)
        {
            var command = commands[i];
            if (command.PaintKind == null && !elementVersion.HasValue) _volatile = true;
            Add(new Atom(0, command.PaintKind, true));
            Add(new Atom(unchecked((long)(command.PaintKind != null ? command.PaintVersion : elementVersion.GetValueOrDefault())), null));
            Add(new Atom(unchecked((long)command.PaintDetail), command.PaintSource));
            Add(new Atom(0, command.PaintDependency));
        }
    }
    private void AddColor(XEngine.Vector.Color value) { Add(value.R); Add(value.G); Add(value.B); Add(value.A); }
    private void Add(float value) => Add(new Atom(BitConverter.SingleToInt32Bits(value), null));
    private void Add(Atom value)
    {
        if (_position == _values.Count) { _values.Add(value); _changed = true; }
        else
        {
            var prior = _values[_position];
            if (prior.Number != value.Number || prior.Text != value.Text ||
                !(value.Text ? string.Equals((string?)prior.Reference, (string?)value.Reference, StringComparison.Ordinal)
                    : ReferenceEquals(prior.Reference, value.Reference))) _changed = true;
            _values[_position] = value;
        }
        _position++;
    }
    private readonly struct Atom(long number, object? reference, bool text = false)
    {
        public readonly long Number = number;
        public readonly object? Reference = reference;
        public readonly bool Text = text;
    }
    public void Clear() { _values.Clear(); _owner = null; _valid = false; }
    public void Dispose() => Clear();
}
