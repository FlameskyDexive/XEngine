using System;
using XEngine.Editor.Core;
using Xunit;

namespace XEngine.Editor.Test;

public sealed class RuntimeStatsSnapshotTests
{
    [Fact]
    public void Snapshot_Preserves_Surface_Metrics_And_Gpu_Identity()
    {
        var snapshot = new EditorRuntimeTools.RenderStatsSnapshot(
            FrameTimeMs: 1f,
            ColorPassMs: 2f,
            ShadowPassMs: 3f,
            PostFxMs: 4f,
            GpuWaitMs: 5f,
            DrawCalls: 6,
            InstancedDrawCalls: 7,
            Batches: 8,
            Triangles: 9,
            Vertices: 10,
            RenderablesCollected: 11,
            RenderablesCulled: 12,
            RenderablesDrawn: 13,
            Cameras: 14,
            Lights: 15,
            DirectionalLights: 16,
            PointLights: 17,
            SpotLights: 18,
            ShadowDrawCalls: 19,
            ShadowTriangles: 20,
            ShadowPasses: 21,
            ShadowCasters: 22,
            ImageEffects: 23,
            ImageEffectPasses: 24,
            CommandBufferSubmissions: 25,
            ColorPassSubmissions: 26,
            ShadowPassSubmissions: 27,
            PostFxSubmissions: 28,
            FinalPassSubmissions: 29,
            UiSubmissions: 30,
            ClearOperations: 31,
            BlitOperations: 32,
            BlitPixels: 33,
            TransientRenderTargetBytes: 34,
            PrepassSubmissions: 35,
            PrepassSkipped: 36,
            RecentFrameTimesMs: Array.Empty<float>(),
            BackendName: "Vulkan",
            GpuTimestampQueriesSupported: true,
            HardwareTimestampQueriesSupported: true,
            GpuFrameTimeMs: 37f,
            GpuFrameId: 38,
            SurfaceWidth: 1920,
            SurfaceHeight: 1080,
            DirtyReason: "Input",
            SchedulerState: "Interactive",
            UiDrawCalls: 39,
            UiRenderTargetBytes: 43,
            PresentCount: 44,
            PresentWaitMs: 1.5f);

        Assert.Equal((ulong)38, snapshot.GpuFrameId);
        Assert.Equal(1920, snapshot.SurfaceWidth);
        Assert.Equal(1080, snapshot.SurfaceHeight);
        Assert.Equal("Input", snapshot.DirtyReason);
        Assert.Equal("Interactive", snapshot.SchedulerState);
        Assert.Equal(39, snapshot.UiDrawCalls);
        Assert.Equal(43, snapshot.UiRenderTargetBytes);
        Assert.Equal(44, snapshot.PresentCount);
        Assert.Equal(1.5f, snapshot.PresentWaitMs);
    }
}
