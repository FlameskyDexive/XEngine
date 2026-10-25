// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using Xunit;

namespace XEngine.Runtime.Test.GameObjects;

/// <summary>
/// Application.IsPaused must freeze per-frame component ticks (Update/LateUpdate/FixedUpdate) —
/// the Frame Debugger relies on this so a captured frame stays frozen while stepping events.
/// Lifecycle callbacks and [ExecuteAlways] components are intentionally not pause-gated.
/// </summary>
public sealed class PauseGatingTests : IDisposable
{
    private sealed class TickCounter : MonoBehaviour
    {
        public int Updates;
        public int LateUpdates;
        public int FixedUpdates;
        public override void Update() => Updates++;
        public override void LateUpdate() => LateUpdates++;
        public override void FixedUpdate() => FixedUpdates++;
    }

    [ExecuteAlways]
    private sealed class AlwaysTicker : MonoBehaviour
    {
        public int Updates;
        public override void Update() => Updates++;
    }

    public PauseGatingTests() => ResetState();

    public void Dispose() => ResetState();

    private static void ResetState()
    {
        Application.IsPlaying = false;
        Application.IsPaused = false;
        Application.StepRequested = false;
    }

    [Fact]
    public void Pause_FreezesTicks_StepReleasesOneFrame()
    {
        var go = new GameObject("pause-gating");
        try
        {
            TickCounter c = go.AddComponent<TickCounter>();

            Application.IsPlaying = true;
            c.InternalUpdate();
            c.InternalLateUpdate();
            c.InternalFixedUpdate();
            Assert.Equal(1, c.Updates);
            Assert.Equal(1, c.LateUpdates);
            Assert.Equal(1, c.FixedUpdates);

            Application.IsPaused = true;
            c.InternalUpdate();
            c.InternalLateUpdate();
            c.InternalFixedUpdate();
            Assert.Equal(1, c.Updates);
            Assert.Equal(1, c.LateUpdates);
            Assert.Equal(1, c.FixedUpdates);

            // Editor Step button: exactly one frame runs while StepRequested is set.
            Application.StepRequested = true;
            c.InternalUpdate();
            c.InternalLateUpdate();
            Assert.Equal(2, c.Updates);
            Assert.Equal(2, c.LateUpdates);
            Application.StepRequested = false;
            c.InternalUpdate();
            Assert.Equal(2, c.Updates);
        }
        finally
        {
            go.Dispose();
        }
    }

    [Fact]
    public void Pause_DoesNotFreezeExecuteAlways()
    {
        var go = new GameObject("pause-gating-always");
        try
        {
            AlwaysTicker c = go.AddComponent<AlwaysTicker>();
            Application.IsPlaying = true;
            Application.IsPaused = true;
            c.InternalUpdate();
            Assert.Equal(1, c.Updates);
        }
        finally
        {
            go.Dispose();
        }
    }
}
