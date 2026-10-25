// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

namespace XEngine.Runtime.RHI;

/// <summary>
/// Backend-neutral graphics device. Owns presentation, command execution, and
/// GPU-work retirement. Resource wrappers (<c>GraphicsBuffer</c>, etc.) may call
/// into the active device; this interface does not mandate a full create/destroy API yet.
/// </summary>
public interface IGraphicsDevice : IDisposable
{
    GraphicsBackend Backend { get; }
    GraphicsDeviceCapabilities Capabilities { get; }
    bool IsInitialized { get; }

    /// <summary>True after a terminal GPU device-loss result.</summary>
    bool IsDeviceLost => false;

    /// <summary>
    /// Bind to an optional window surface and finish device setup.
    /// Pass <c>null</c> for headless / Null backends.
    /// </summary>
    void Initialize(IWindowSurface? surface);

    void Shutdown();

    /// <summary>Begin a new frame (acquire image / arm frame fence).</summary>
    void BeginFrame();

    /// <summary>End the frame and present when a surface is attached.</summary>
    void EndFrame();

    /// <summary>Notify the device that the drawable framebuffer changed size.</summary>
    void Resize(int width, int height) { }

    /// <summary>
    /// Execute a recorded <see cref="CommandBuffer"/> on the device's GPU queue / executor.
    /// When <paramref name="wait"/> is true, blocks until the buffer has finished (and
    /// rethrows render-thread exceptions on the caller), matching historical SubmitAndWait.
    /// </summary>
    void Execute(CommandBuffer commandBuffer, bool wait);

    /// <summary>Block until all submitted GPU work has completed.</summary>
    void WaitIdle();

    /// <summary>Monotonically increasing fence value reflecting completed GPU work.</summary>
    ulong GetFenceValue();

    /// <summary>Block until the device fence reaches at least <paramref name="fenceValue"/>.</summary>
    void WaitFence(ulong fenceValue);

    /// <summary>
    /// Reads the most recently resolved asynchronous GPU timestamp interval. Returns false until
    /// a backend has both recorded timestamps and observed completion of their submission.
    /// </summary>
    bool TryGetCompletedGpuTimestampFrame(out GpuTimestampFrame frame)
    {
        frame = default;
        return false;
    }

    /// <summary>Whether opt-in command-buffer GPU intervals can be recorded on this backend.</summary>
    bool SupportsGpuCommandTimings => false;
    long DroppedGpuCommandTimings => 0;
    bool TryDequeueGpuCommandTiming(out GpuCommandTiming timing)
    {
        timing = default;
        return false;
    }

    /// <summary>
    /// B7b — whether this device records <see cref="CommandOpcode.BeginOcclusionQuery"/>/
    /// <see cref="CommandOpcode.EndOcclusionQuery"/> pairs (ANY_SAMPLES_PASSED-style depth
    /// visibility) and publishes their results. Backends without support skip the pair at
    /// execute time; callers must treat a missing verdict as "no answer this attempt".
    /// </summary>
    bool SupportsOcclusionQueries => false;

    /// <summary>
    /// Polls the completed-result map for a previously recorded occlusion query id. True (with
    /// <paramref name="passed"/>) once the submission containing the query retired; false means
    /// no verdict yet (or ever — see <see name="SupportsOcclusionQueries"/>).
    /// </summary>
    bool TryGetOcclusionQueryResult(ulong queryId, out bool passed)
    {
        passed = false;
        return false;
    }
}
