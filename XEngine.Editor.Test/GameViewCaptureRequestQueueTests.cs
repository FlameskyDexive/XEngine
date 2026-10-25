using XEngine.Async;
using XEngine.Editor.GUI.Panels;

using Xunit;

namespace XEngine.Editor.Test;

public sealed class GameViewCaptureRequestQueueTests
{
    [Fact]
    public void RequestsCompleteOnlyWhenAFrameIsPublished()
    {
        var requests = new GameViewCaptureRequestQueue<int>();
        XTask<int> first = requests.Request().Task;
        XTask<int> second = requests.Request().Task;

        Assert.False(first.IsCompleted);
        Assert.False(second.IsCompleted);

        requests.Complete(42);

        Assert.Equal(42, first.GetAwaiter().GetResult());
        Assert.Equal(42, second.GetAwaiter().GetResult());
    }

    [Fact]
    public void CancelRemovesOnlyTheMatchingRequest()
    {
        var requests = new GameViewCaptureRequestQueue<int>();
        GameViewCaptureRequest<int> canceled = requests.Request();
        XTask<int> retained = requests.Request().Task;

        Assert.True(requests.Cancel(canceled));
        Assert.Throws<OperationCanceledException>(() => canceled.Task.GetAwaiter().GetResult());

        requests.Complete(42);

        Assert.Equal(42, retained.GetAwaiter().GetResult());
    }
}
