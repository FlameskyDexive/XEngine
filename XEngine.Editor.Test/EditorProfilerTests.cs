// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Editor.Core;
using XEngine.Runtime.Profiling;

using Xunit;

namespace XEngine.Editor.Test;

public sealed class EditorProfilerTests
{
    [Fact]
    public void SampleNow_IsRateLimitedAndReportsExplicitGpuCapability()
    {
        EditorProfiler.ResetForTests();

        ProfilerSample first = EditorProfiler.SampleNow();
        ProfilerSample second = EditorProfiler.SampleNow();

        Assert.Equal(first.TimestampUtc, second.TimestampUtc);
        Assert.InRange(first.ProcessCpuPercent, 0f, 100f);
        Assert.True(first.WorkingSetBytes > 0);
        Assert.True(first.ManagedHeapBytes > 0);
        Assert.False(first.GpuTimestampSupported);
        Assert.Null(first.GpuFrameTimeMs);
        Assert.NotEmpty(first.GpuStatus);
        Assert.Equal(EditorSurfaceSchedulerState.Unscheduled, first.Surface.SchedulerState);
        Assert.Equal(EditorSurfaceDirtyReason.None, first.Surface.DirtyReason);
    }

    [Fact]
    public void SampleTelemetryNow_UsesThePlayerCompatibleSchema()
    {
        EditorProfiler.ResetForTests();

        ProfilerTelemetryFrame frame = EditorProfiler.SampleTelemetryNow();

        Assert.Equal(ProfilerTelemetryTarget.LocalEditor, frame.Target);
        Assert.Equal(ProfilerMetricSource.Unavailable, frame.ProcessCpuSource);
        Assert.Equal(ProfilerMetricSource.OperatingSystem, frame.WorkingSetSource);
        Assert.Equal(ProfilerMetricSource.Runtime, frame.ManagedHeapSource);
        Assert.Equal(ProfilerMetricSource.Unavailable, frame.RhiMemorySource);
        Assert.Equal(0, frame.RhiMemoryBytes);
    }

    [Fact]
    public void CpuScopes_RetainNestedScopesForCurrentFrame()
    {
        EditorProfiler.ResetForTests();
        EditorProfiler.BeginCpuFrame();

        using (EditorProfiler.BeginCpuScope("Outer"))
        {
            using var inner = EditorProfiler.BeginCpuScope("Inner");
        }

        CpuScopeFrame frame = EditorProfiler.GetCurrentThreadCpuFrame();
        Assert.Equal(2, frame.ScopeCount);
        Assert.Equal(0, frame.DroppedScopeCount);
        Assert.Equal(EditorProfiler.MaxCpuScopesPerThread, frame.Capacity);
        Assert.True(EditorProfiler.TryGetCurrentThreadCpuScope(0, out CpuScopeSample first));
        Assert.True(EditorProfiler.TryGetCurrentThreadCpuScope(1, out CpuScopeSample second));
        Assert.Equal("Inner", first.Name);
        Assert.Equal("Outer", second.Name);
        Assert.True(first.DurationMs >= 0f);
        Assert.True(second.DurationMs >= 0f);
    }

    [Fact]
    public void CpuScopes_AreBoundedPerThreadFrame()
    {
        EditorProfiler.ResetForTests();
        EditorProfiler.BeginCpuFrame();

        for (int i = 0; i < EditorProfiler.MaxCpuScopesPerThread + 3; i++)
        {
            using var scope = EditorProfiler.BeginCpuScope("Bounded");
        }

        CpuScopeFrame frame = EditorProfiler.GetCurrentThreadCpuFrame();
        Assert.Equal(EditorProfiler.MaxCpuScopesPerThread, frame.ScopeCount);
        Assert.Equal(3, frame.DroppedScopeCount);
    }

    [Fact]
    public void CpuScopes_SteadyStateDoesNotAllocate()
    {
        EditorProfiler.ResetForTests();
        EditorProfiler.BeginCpuFrame();
        using (EditorProfiler.BeginCpuScope("Warmup")) { }

        EditorProfiler.BeginCpuFrame();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 32; i++)
        {
            using var scope = EditorProfiler.BeginCpuScope("SteadyState");
        }
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

        Assert.Equal(0, allocated);
    }
}
