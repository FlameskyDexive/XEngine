using XEngine.Runtime;

using Xunit;

namespace XEngine.Runtime.Test;

public sealed class WindowRenderGateTests
{
    [Fact]
    public void MissingRenderGatePreservesDefaultRendering()
    {
        Window.RenderRequested = null;

        Assert.True(Window.ShouldRenderCurrentFrame());
    }

    [Fact]
    public void FalseRenderGateSkipsTheCurrentFrame()
    {
        Window.RenderRequested = static () => false;
        try
        {
            Assert.False(Window.ShouldRenderCurrentFrame());
        }
        finally
        {
            Window.RenderRequested = null;
        }
    }

    [Fact]
    public void IdleWait_IsZeroWhenElapsedMeetsBudget()
    {
        long freq = 1_000_000;
        long elapsed = (long)(freq / Window.IdleLoopHz);
        Assert.Equal(0, Window.IdleWaitMilliseconds(elapsed, freq));
        Assert.Equal(0, Window.IdleWaitMilliseconds(elapsed + freq, freq));
        Assert.Equal(0, Window.IdleWaitMilliseconds(0, freq));
        Assert.Equal(0, Window.IdleWaitMilliseconds(1, 0));
    }

    [Fact]
    public void IdleWait_SleepsRemainderOfSixtyHertzBudget()
    {
        long freq = 1_000_000;
        int wait = Window.IdleWaitMilliseconds(freq / 1000, freq); // 1 ms elapsed
        Assert.InRange(wait, 14, 16);
    }

    [Fact]
    public void IdlePumpSlice_CapsAtFourMilliseconds()
    {
        Assert.Equal(0, Window.IdlePumpSlice(0));
        Assert.Equal(0, Window.IdlePumpSlice(-1));
        Assert.Equal(3, Window.IdlePumpSlice(3));
        Assert.Equal(Window.IdlePumpSliceMilliseconds, Window.IdlePumpSlice(16));
    }

    [Fact]
    public void IdlePump_ExitsOnlyOnPendingUserInput()
    {
        const uint waitObject0 = 0;       // MsgWaitForMultipleObjects woke on the queue
        const uint waitTimeout = 0x102;   // WAIT_TIMEOUT: slice expired with no message

        // A timed-out slice never exits, whatever the queue snapshot says.
        Assert.False(Window.ShouldExitIdlePump(waitTimeout, 0x0007));

        // Non-input wakes (paint / timer / posted) keep pumping inside the idle budget.
        Assert.False(Window.ShouldExitIdlePump(waitObject0, 0));
        Assert.False(Window.ShouldExitIdlePump(waitObject0, 0x0020)); // QS_PAINT
        Assert.False(Window.ShouldExitIdlePump(waitObject0, 0x0010)); // QS_TIMER

        // Real user input — key, move, or button — hands control back to the main loop so
        // LateUpdate sees button transitions while the button is still held.
        Assert.True(Window.ShouldExitIdlePump(waitObject0, 0x0001)); // QS_KEY
        Assert.True(Window.ShouldExitIdlePump(waitObject0, 0x0002)); // QS_MOUSEMOVE
        Assert.True(Window.ShouldExitIdlePump(waitObject0, 0x0004)); // QS_MOUSEBUTTON

        // GetQueueStatus packs "changed since last call" in the high word; those bits alone
        // (input drained already) must not count as pending.
        Assert.False(Window.ShouldExitIdlePump(waitObject0, 0x0004 << 16));
    }
}
