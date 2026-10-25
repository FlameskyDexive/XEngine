namespace XEngine.Editor.UI;

/// <summary>
/// Single-threaded bounded source for one GPU consumer. The source outlives that consumer;
/// independent GPU copies require independent dirty tracking. Capacity/page count are fixed.
/// </summary>
public interface IUiUploadSource<T> where T : unmanaged
{
    int Capacity { get; }
    int PageCount { get; }
    int DirtyPageCount { get; }
    ReadOnlySpan<T> Read(int offset, int count);
    bool TryGetPending(out UiUploadRange range, int maxPages = int.MaxValue, int startOffset = 0);
    ReadOnlySpan<T> ReadPending(UiUploadRange range);
    bool Acknowledge(UiUploadRange range);
}
