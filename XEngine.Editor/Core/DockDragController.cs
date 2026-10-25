using XEngine.OrigamiUI;
using XEngine.Vector;

namespace XEngine.Editor.Core;

public enum DockDragOutcome
{
    Cancelled,
    Docked,
    Floating,
}

public readonly record struct DockDragResult(
    DockDragOutcome Outcome,
    DockPanel? Panel,
    DockDropCandidate Candidate);

/// <summary>Transaction shell for cross-host tab drags. It never mutates a dock tree before commit.</summary>
public sealed class DockDragController
{
    private const float DetachThreshold = 6f;
    private readonly EditorWorkspace _workspace;
    private DockPanel? _panel;
    private Float2 _startPosition;
    private Float2 _currentPosition;
    private DockDropCandidate _candidate;

    public DockDragController(EditorWorkspace workspace) => _workspace = workspace;
    public bool IsDragging => _panel != null;
    public DockPanel? DraggedPanel => _panel;
    public DockDropCandidate CurrentCandidate => _candidate;

    public bool BeginDrag(DockPanel panel, Float2 screenPosition)
    {
        if (IsDragging || _workspace.FindOwner(panel) == null) return false;
        _panel = panel;
        _startPosition = screenPosition;
        _currentPosition = screenPosition;
        _candidate = DockDropCandidate.None;
        return true;
    }

    public bool UpdateDrag(Float2 screenPosition)
    {
        if (_panel == null) return false;
        _currentPosition = screenPosition;
        float dx = screenPosition.X - _startPosition.X;
        float dy = screenPosition.Y - _startPosition.Y;
        if (dx * dx + dy * dy < DetachThreshold * DetachThreshold)
        {
            _candidate = DockDropCandidate.None;
            return false;
        }

        _candidate = _workspace.FindDropCandidate(screenPosition);
        if (!_candidate.IsValid)
            _candidate = new DockDropCandidate(-1, DockDropZone.Float, default, true);
        return true;
    }

    public DockDragResult CommitDrag()
    {
        if (_panel == null) return new DockDragResult(DockDragOutcome.Cancelled, null, DockDropCandidate.None);
        DockPanel panel = _panel;
        DockDropCandidate candidate = _candidate;
        DockDragOutcome outcome = candidate.Zone == DockDropZone.Float
            ? _workspace.TryFloatPanel(panel, _currentPosition, out _)
                ? DockDragOutcome.Floating
                : DockDragOutcome.Cancelled
            : candidate.IsValid && _workspace.TryDockPanel(panel, candidate)
                ? DockDragOutcome.Docked
                : DockDragOutcome.Cancelled;
        Clear();
        return new DockDragResult(outcome, panel, candidate);
    }

    public void CancelDrag() => Clear();

    private void Clear()
    {
        _panel = null;
        _startPosition = default;
        _currentPosition = default;
        _candidate = DockDropCandidate.None;
    }
}
