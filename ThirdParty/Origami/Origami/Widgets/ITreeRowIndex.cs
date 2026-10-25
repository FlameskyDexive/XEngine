namespace XEngine.OrigamiUI;

/// <summary>Caller-owned visible preorder mapping. Rebuild on topology changes and
/// update external expansion changes before Show. Does not own scene data or input.</summary>
public interface ITreeRowIndex
{
    int Count { get; }
    int GetNodeIndex(int visibleRank);
    bool IsExpanded(int nodeIndex);
    void SetExpanded(string nodeId, bool expanded);

    /// <summary>Visible rank of a node, or -1 while an ancestor hides it.</summary>
    int GetVisibleRank(int nodeIndex);

    /// <summary>Rows currently shown beneath a node (0 for leaves and collapsed nodes).</summary>
    int GetVisibleDescendantCount(int nodeIndex);

    /// <summary>Increments whenever <see cref="SetExpanded"/> actually changed visibility.
    /// <see cref="LastFoldNodeIndex"/>/<see cref="LastFoldExpanded"/> describe that change so the
    /// tree can animate the fold exactly like the unvirtualized wrapper path.</summary>
    long FoldRevision { get; }
    int LastFoldNodeIndex { get; }
    bool LastFoldExpanded { get; }

    /// <summary>Show or hide one collapsed subtree for transition drawing only. Stored expansion,
    /// <see cref="IsExpanded"/> and the fold record are unchanged; the caller must restore it
    /// before returning control to the owner.</summary>
    void SetTransientVisible(int nodeIndex, bool visible);
}
