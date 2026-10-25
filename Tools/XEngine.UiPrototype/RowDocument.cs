namespace XEngine.UiPrototype;

/// <summary>Owned, fixed-size input for the N1 renderer comparison. Structural/tree edits are
/// introduced separately; the prototype does not pretend a flat row index is a tree identity.</summary>
public sealed class RowDocument : IRowSource
{
    private readonly string[] _text;
    private readonly long[] _versions;
    private int _selected = -1;

    public RowDocument(ReadOnlySpan<string> text)
    {
        _text = text.ToArray();
        _versions = new long[text.Length];
        for (int i = 0; i < _text.Length; i++) ArgumentNullException.ThrowIfNull(_text[i]);
    }

    public int Count => _text.Length;
    public long Version { get; private set; }
    public int SelectedIndex => _selected;
    public string GetText(int index) => _text[index];
    public PrototypeRow GetRow(int index) => new(index, _text[index], _versions[index], _selected == index);

    public void SetText(int index, string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (string.Equals(_text[index], text, StringComparison.Ordinal)) return;
        _text[index] = text;
        _versions[index]++;
        Version++;
    }

    public void Select(int index)
    {
        if (index < -1 || index >= Count) throw new ArgumentOutOfRangeException(nameof(index));
        if (_selected == index) return;
        _selected = index;
        Version++;
    }
}
