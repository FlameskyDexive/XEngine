namespace XEngine.UiPrototype;

[Flags]
public enum PrototypeKeys { None = 0, Control = 1, Shift = 2, A = 4, Backspace = 8, Delete = 16,
    Left = 32, Right = 64, Home = 128, End = 256, Enter = 512, Escape = 1024 }

/// <summary>Logical-coordinate input, shared by native polling and deterministic
/// adapter-level replay. A replay is not an OS event injection or latency measurement.</summary>
public readonly record struct PrototypeInput(float X, float Y, bool LeftDown = false,
    float Wheel = 0, PrototypeKeys Keys = PrototypeKeys.None, string Text = "", float DeltaSeconds = 1f / 60);

/// <summary>Common custom splitter and scroll-region interaction for the three N1
/// frontends. Tree selection and text editing are handled by their widget adapters.</summary>
public sealed class PrototypeViewportInput
{
    private bool _wasDown, _dragging;
    private float _grabOffset;
    public bool Dragging => _dragging;
    public void Apply(in PrototypeInput input, float width, float height, PrototypeWorkload model)
    {
        float left = (float)((width - 12) * model.SplitRatio), rowsHeight = (height - 88) * .6f;
        if (input.LeftDown && !_wasDown && input.X >= left + 4 && input.X < left + 12 && input.Y >= 36 && input.Y < 36 + rowsHeight)
        { _dragging = true; _grabOffset = input.X - left; }
        if (_dragging && input.LeftDown)
            model.Apply(new ReplayEvent(0, ReplayAction.DragSplit, Value: (input.X - _grabOffset) / (width - 12)));
        if (!input.LeftDown) _dragging = false;
        if (input.Wheel != 0 && !_dragging && input.Y >= 36 && input.Y < 36 + rowsHeight)
        {
            if (input.X >= 4 && input.X < left + 4)
                model.Apply(new ReplayEvent(0, ReplayAction.ScrollTree,
                    Value: Math.Clamp(model.TreeScroll - input.Wheel * 22, 0, Math.Max(0, model.Tree.Count * 22d - rowsHeight))));
            else if (input.X >= left + 12 && input.X < width - 4)
                model.Apply(new ReplayEvent(0, ReplayAction.ScrollProperties,
                    Value: Math.Clamp(model.PropertyScroll - input.Wheel * 22, 0, Math.Max(0, model.Properties.Count * 22d - rowsHeight))));
        }
        _wasDown = input.LeftDown;
    }
}

/// <summary>N1 single-line editing state. UTF-16 surrogate pairs remain intact.
/// Full grapheme/IME/clipboard/undo support remains a later text-system gate.</summary>
public sealed class PrototypeTextEditor
{
    public string Text { get; private set; } = "";
    public int Cursor { get; private set; }
    public int Anchor { get; private set; }
    public int SelectionStart => Math.Min(Cursor, Anchor);
    public int SelectionLength => Math.Abs(Cursor - Anchor);
    public void SetText(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (Text == text) return;
        Text = text; Cursor = Anchor = text.Length;
    }
    public void MoveToEnd() => Cursor = Anchor = Text.Length;
    public void Apply(PrototypeKeys pressed, PrototypeKeys held, string input)
    {
        if ((held & PrototypeKeys.Control) != 0 && (pressed & PrototypeKeys.A) != 0)
        { Anchor = 0; Cursor = Text.Length; }
        bool shift = (held & PrototypeKeys.Shift) != 0;
        if ((pressed & PrototypeKeys.Home) != 0) Move(0, shift);
        if ((pressed & PrototypeKeys.End) != 0) Move(Text.Length, shift);
        if ((pressed & PrototypeKeys.Left) != 0) Move(!shift && SelectionLength > 0 ? SelectionStart : Previous(Cursor), shift);
        if ((pressed & PrototypeKeys.Right) != 0) Move(!shift && SelectionLength > 0 ? SelectionStart + SelectionLength : Next(Cursor), shift);
        if ((pressed & PrototypeKeys.Backspace) != 0)
        { if (SelectionLength == 0) Anchor = Previous(Cursor); Replace(""); }
        if ((pressed & PrototypeKeys.Delete) != 0)
        { if (SelectionLength == 0) Anchor = Next(Cursor); Replace(""); }
        if ((held & PrototypeKeys.Control) == 0 && input.Length > 0)
        {
            string line = input.Replace("\r", "").Replace("\n", "");
            if (line.Length > 0) Replace(line);
        }
    }
    private void Move(int target, bool extend) { Cursor = target; if (!extend) Anchor = target; }
    private int Previous(int at) => at == 0 ? 0 : at >= 2 && char.IsLowSurrogate(Text[at - 1]) && char.IsHighSurrogate(Text[at - 2]) ? at - 2 : at - 1;
    private int Next(int at) => at == Text.Length ? at : at + 1 < Text.Length && char.IsHighSurrogate(Text[at]) && char.IsLowSurrogate(Text[at + 1]) ? at + 2 : at + 1;
    private void Replace(string inserted)
    {
        int start = SelectionStart, length = SelectionLength;
        if (Text.Length - length + inserted.Length > 4095) return;
        if (length != 0 || inserted.Length != 0) Text = Text.Remove(start, length).Insert(start, inserted);
        Cursor = Anchor = start + inserted.Length;
    }
}
