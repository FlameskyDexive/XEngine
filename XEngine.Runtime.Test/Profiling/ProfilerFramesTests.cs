// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using XEngine.Runtime.Profiling;

using Xunit;

namespace XEngine.Runtime.Test.Profiling;

public class ProfilerFramesTests
{
    [Fact]
    public void SteadyStateScopesDoNotAllocate()
    {
        var marker = new ProfilerMarker("alloc-test-scope");

        // Warm up: intern, tier-up, first-touch of both buffers.
        for (int f = 0; f < 3; f++)
        {
            ProfilerFrames.BeginFrame();
            for (int i = 0; i < 8; i++)
                using (marker.Auto()) { }
        }

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int f = 0; f < 100; f++)
        {
            ProfilerFrames.BeginFrame();
            for (int i = 0; i < 50; i++)
                using (marker.Auto()) { }
        }
        long delta = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, delta);
    }

    [Fact]
    public void CompletedFrameCarriesNestedScopes()
    {
        var outer = new ProfilerMarker("frames-test-outer");
        var inner = new ProfilerMarker("frames-test-inner");
        int outerId = ProfilerFrames.Intern("frames-test-outer");
        int innerId = ProfilerFrames.Intern("frames-test-inner");

        ProfilerFrames.BeginFrame();
        using (outer.Auto())
        using (inner.Auto())
        { }
        ProfilerFrames.BeginFrame(); // publish

        var buffer = new ProfilerFrames.Entry[ProfilerFrames.MaxScopesPerFrame];
        int count = ProfilerFrames.CopyCompletedFrame(buffer, out long frameIndex);

        Assert.True(count >= 2);
        Assert.True(frameIndex >= 0);

        int foundOuter = -1, foundInner = -1;
        for (int i = 0; i < count; i++)
        {
            if (buffer[i].Id == outerId) foundOuter = i;
            if (buffer[i].Id == innerId) foundInner = i;
        }

        Assert.True(foundOuter >= 0, "outer scope missing from completed frame");
        Assert.True(foundInner >= 0, "inner scope missing from completed frame");
        Assert.Equal(0, buffer[foundOuter].Depth);
        Assert.Equal(1, buffer[foundInner].Depth);
        Assert.True(buffer[foundOuter].EndTicks >= buffer[foundOuter].StartTicks);
        Assert.True(buffer[foundInner].StartTicks >= buffer[foundOuter].StartTicks);
        Assert.Equal("frames-test-outer", ProfilerFrames.NameOf(outerId));
    }

    [Fact]
    public void OverflowDropsScopesWithoutCorruption()
    {
        var marker = new ProfilerMarker("frames-test-overflow");
        ProfilerFrames.BeginFrame();
        for (int i = 0; i < ProfilerFrames.MaxScopesPerFrame + 20; i++)
            using (marker.Auto()) { }
        ProfilerFrames.BeginFrame();

        var buffer = new ProfilerFrames.Entry[ProfilerFrames.MaxScopesPerFrame];
        int count = ProfilerFrames.CopyCompletedFrame(buffer, out _);
        Assert.Equal(ProfilerFrames.MaxScopesPerFrame, count);
        for (int i = 0; i < count; i++)
            Assert.True(buffer[i].EndTicks >= buffer[i].StartTicks);
    }
}
