// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Xunit;

using XEngine.Playables;

namespace XEngine.Runtime.Test.Playables;

/// <summary>
/// Verifies the <see cref="Clock"/> contract: unbounded accumulation, TimeScale, Clamp/Loop/
/// PingPong wrap modes, DeltaTime reporting, and the ManualClock scrub clamp.
/// </summary>
public class ClockTests
{
    [Fact]
    public void GameClock_DefaultsAreUnboundedAndClamp()
    {
        var clock = new GameClock();
        Assert.Equal(-1d, clock.Duration);
        Assert.Equal(ClockWrapMode.Clamp, clock.Wrap);
        Assert.Equal(1f, clock.TimeScale);
    }

    [Fact]
    public void GameClock_UnboundedAccumulatesWithoutWrap()
    {
        var clock = new GameClock();

        clock.Advance(0.5f);
        clock.Advance(0.5f);

        Assert.Equal(1.0, clock.CurrentTime, precision: 6);
    }

    [Fact]
    public void Advance_AppliesTimeScale()
    {
        var clock = new GameClock { TimeScale = 0.5f };

        float applied = clock.Advance(1.0f);

        Assert.Equal(0.5f, applied);
        Assert.Equal(0.5, clock.CurrentTime, precision: 6);
        Assert.Equal(0.5f, clock.DeltaTime);
    }

    [Fact]
    public void Advance_NegativeDeltaMovesBackward()
    {
        var clock = new GameClock();
        clock.Advance(1.0f);

        clock.Advance(-0.4f);

        Assert.Equal(0.6, clock.CurrentTime, precision: 6);
    }

    [Fact]
    public void Clamp_StopsAtDuration()
    {
        var clock = new ManualClock { Duration = 1.0, Wrap = ClockWrapMode.Clamp };

        clock.Advance(0.6f);
        Assert.Equal(0.6, clock.CurrentTime, precision: 6);

        clock.Advance(0.6f);
        Assert.Equal(1.0, clock.CurrentTime, precision: 6);
    }

    [Fact]
    public void Clamp_StopsAtZeroWhenMovingBackward()
    {
        var clock = new ManualClock { Duration = 1.0, Wrap = ClockWrapMode.Clamp };
        clock.Advance(0.5f);

        clock.Advance(-1.0f);

        Assert.Equal(0.0, clock.CurrentTime, precision: 6);
    }

    [Fact]
    public void Loop_WrapsAtDuration()
    {
        var clock = new ManualClock { Duration = 2.0, Wrap = ClockWrapMode.Loop };

        clock.Advance(1.5f);
        Assert.Equal(1.5, clock.CurrentTime, precision: 6);

        clock.Advance(1.0f);
        Assert.Equal(0.5, clock.CurrentTime, precision: 6);
    }

    [Fact]
    public void Loop_WrapsNegativeToPositive()
    {
        var clock = new ManualClock { Duration = 2.0, Wrap = ClockWrapMode.Loop };
        clock.Advance(0.5f);

        clock.Advance(-1.0f);

        Assert.Equal(1.5, clock.CurrentTime, precision: 6);
    }

    [Fact]
    public void Loop_HandlesMultiWrapInOneStep()
    {
        var clock = new ManualClock { Duration = 1.0, Wrap = ClockWrapMode.Loop };

        // A delta larger than duration should wrap fully, not clamp.
        clock.Advance(2.5f);

        Assert.Equal(0.5, clock.CurrentTime, precision: 6);
    }

    [Fact]
    public void PingPong_ReversesAtUpperBound()
    {
        var clock = new ManualClock { Duration = 2.0, Wrap = ClockWrapMode.PingPong };

        clock.Advance(1.5f);   // t = 1.5
        Assert.Equal(1.5, clock.CurrentTime, precision: 6);

        clock.Advance(1.0f);   // t crosses 2.0, reflects: 2.0 - (2.5 - 2.0) = 1.5
        Assert.Equal(1.5, clock.CurrentTime, precision: 6);

        clock.Advance(1.0f);   // now moving backward: 1.5 - 1.0 = 0.5
        Assert.Equal(0.5, clock.CurrentTime, precision: 6);
    }

    [Fact]
    public void PingPong_ReversesAtLowerBound()
    {
        var clock = new ManualClock { Duration = 2.0, Wrap = ClockWrapMode.PingPong };
        clock.Advance(0.5f);   // t = 0.5, forward

        clock.Advance(-1.0f);  // crosses 0, reflects: -(0.5 - 1.0) = 0.5, direction flips to forward
        Assert.Equal(0.5, clock.CurrentTime, precision: 6);

        clock.Advance(0.5f);   // forward again: 0.5 + 0.5 = 1.0
        Assert.Equal(1.0, clock.CurrentTime, precision: 6);
    }

    [Fact]
    public void ManualClock_ScrubClampsToDuration()
    {
        var clock = new ManualClock { Duration = 5.0 };

        clock.CurrentTime = 100.0;
        Assert.Equal(5.0, clock.CurrentTime, precision: 6);

        clock.CurrentTime = -3.0;
        Assert.Equal(0.0, clock.CurrentTime, precision: 6);

        clock.CurrentTime = 2.5;
        Assert.Equal(2.5, clock.CurrentTime, precision: 6);
    }

    [Fact]
    public void ManualClock_ScrubIgnoresClampWhenUnbounded()
    {
        var clock = new ManualClock { Duration = -1d };

        clock.CurrentTime = 12345.0;

        Assert.Equal(12345.0, clock.CurrentTime);
    }

    [Fact]
    public void GameClock_ScrubDoesNotClamp()
    {
        var clock = new GameClock();

        clock.CurrentTime = -100.0;

        Assert.Equal(-100.0, clock.CurrentTime);
    }
}
