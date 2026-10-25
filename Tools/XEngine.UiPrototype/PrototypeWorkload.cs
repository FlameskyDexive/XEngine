namespace XEngine.UiPrototype;

public enum ReplayAction { ToggleBranch, SelectNode, SetProperty, SetInputText, ScrollTree, ScrollProperties, DragSplit, UpdateViewport }
public readonly record struct ReplayEvent(int Tick, ReplayAction Action, int Target = 0, double Value = 0, string? Text = null);

/// <summary>Common data mutations for renderer comparisons. Adapters must additionally test
/// their own input routing; applying these events alone is not a widget interaction test.</summary>
public sealed class PrototypeWorkload
{
    public TreeDocument Tree { get; }
    public RowDocument Properties { get; }
    public string InputText { get; private set; } = "";
    public double TreeScroll { get; private set; }
    public double PropertyScroll { get; private set; }
    public double SplitRatio { get; private set; } = .5;
    public long ViewportVersion { get; private set; }
    public int EditingProperty { get; private set; } = -1;
    public bool VisualEffects { get; set; }
    public string FieldText => EditingProperty < 0 ? InputText : Properties.GetText(EditingProperty);

    public void SelectProperty(int identity)
    {
        Properties.Select(identity);
        EditingProperty = identity;
    }

    public void EditField(string text)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (EditingProperty < 0) InputText = text;
        else Properties.SetText(EditingProperty, text);
    }

    public PrototypeWorkload(TreeDocument tree, RowDocument properties)
    {
        ArgumentNullException.ThrowIfNull(tree); ArgumentNullException.ThrowIfNull(properties);
        Tree = tree; Properties = properties;
    }

    public void Apply(in ReplayEvent input)
    {
        if (!double.IsFinite(input.Value)) throw new ArgumentOutOfRangeException(nameof(input));
        switch (input.Action)
        {
            case ReplayAction.ToggleBranch: Tree.SetExpanded(input.Target, !Tree.IsExpanded(input.Target)); break;
            case ReplayAction.SelectNode: Tree.Select(input.Target); break;
            case ReplayAction.SetProperty: Properties.SetText(input.Target, input.Text ?? throw new ArgumentException("Property text missing.")); break;
            case ReplayAction.SetInputText: InputText = input.Text ?? throw new ArgumentException("Input text missing."); break;
            case ReplayAction.ScrollTree: TreeScroll = Math.Max(0, input.Value); break;
            case ReplayAction.ScrollProperties: PropertyScroll = Math.Max(0, input.Value); break;
            case ReplayAction.DragSplit: SplitRatio = Math.Clamp(input.Value, .1, .9); break;
            case ReplayAction.UpdateViewport: ViewportVersion++; break;
            default: throw new ArgumentOutOfRangeException(nameof(input));
        }
    }
}

public sealed class ReplayTimeline
{
    private readonly ReplayEvent[] _events;
    private int _next, _lastTick = -1;

    public ReplayTimeline(ReadOnlySpan<ReplayEvent> events)
    {
        _events = events.ToArray();
        int previous = -1;
        for (int i = 0; i < events.Length; i++)
        {
            if (events[i].Tick < 0 || events[i].Tick < previous) throw new ArgumentException("Events must have nondecreasing nonnegative ticks.");
            previous = events[i].Tick;
        }
    }

    public int AppliedCount => _next;
    public int AdvanceTo(int tick, PrototypeWorkload workload)
    {
        ArgumentNullException.ThrowIfNull(workload);
        if (tick < 0 || tick < _lastTick) throw new ArgumentOutOfRangeException(nameof(tick));
        int start = _next;
        while (_next < _events.Length && _events[_next].Tick <= tick)
        { workload.Apply(_events[_next]); _next++; }
        _lastTick = tick;
        return _next - start;
    }
}
