using Xunit;

namespace XEngine.Runtime.Test;

public sealed class WindowFramePacingTests
{
    [Theory]
    [InlineData(true, 60, 1, 0)]
    [InlineData(true, 30, 1, 0)]
    [InlineData(false, 0, 1, 0)]
    [InlineData(false, -1, 1, 0)]
    [InlineData(false, 60, 5, 12)]
    [InlineData(false, 30, 5, 29)]
    [InlineData(false, 60, 20, 0)]
    public void PlayerBudgetNeverThrottlesEditorOrOverBudgetFrames(bool editor, int target, double elapsed, int expected)
        => Assert.Equal(expected, Window.CalculatePlayerFrameWaitMilliseconds(editor, target, elapsed));
}
