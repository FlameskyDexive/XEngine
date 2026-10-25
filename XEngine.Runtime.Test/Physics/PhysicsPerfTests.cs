// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Runtime.Resources;
using XEngine.Vector;

using Xunit;

namespace XEngine.Runtime.Test;

/// <summary>
/// P3 allocation gates: callback dispatch and layer-masked queries must be 0 B in the
/// steady state. Debug JIT boxing is ignored; the property is asserted in Release.
/// </summary>
public class PhysicsPerfTests : RuntimeTestBase
{
    private readonly bool _previousLoopInitialized = XEngine.Async.XTaskLoopHelper.IsInitialized;
    private readonly int _previousLoopThread = XEngine.Async.XTaskLoopHelper.MainThreadId;

    public PhysicsPerfTests()
    {
        // Serial xUnit tests may execute on different managed threads. A scheduler
        // initialized by an earlier test must regard this fixture's simulation thread
        // as the engine thread, otherwise each query measures an off-thread diagnostic.
        if (_previousLoopInitialized)
            XEngine.Async.XTaskLoopHelper.Initialize(Environment.CurrentManagedThreadId);
    }

    public override void Dispose()
    {
        try { base.Dispose(); }
        finally
        {
            if (_previousLoopInitialized)
                XEngine.Async.XTaskLoopHelper.Initialize(_previousLoopThread);
        }
    }

    private Scene CreatePhysicsScene()
    {
        var scene = CreateScene(enable: true);
        scene.Physics.UseMultithreading = false;
        return scene;
    }

    private BoxCollider AddStaticBox(Scene scene, Float3 position, bool trigger = false)
    {
        var go = CreateGameObject("StaticBox");
        go.Transform.Position = position;
        BoxCollider box = go.AddComponent<BoxCollider>();
        box.Size = new Float3(2, 2, 2);
        box.IsTrigger = trigger;
        scene.Add(go);
        return box;
    }

    [Fact]
    public void Dispatch_EmptyScene_IsZeroAllocation()
    {
        Scene scene = CreatePhysicsScene();
        scene.Physics.Update();

        for (int i = 0; i < 8; i++)
            scene.Physics.ContactEvents.Dispatch(scene.Physics.World);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++)
            scene.Physics.ContactEvents.Dispatch(scene.Physics.World);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

#if !DEBUG
        Assert.Equal(0, allocated);
#else
        Assert.True(allocated >= 0);
#endif
    }

    [Fact]
    public void Dispatch_OverlappingBodiesWithNoListeners_IsZeroAllocation()
    {
        Scene scene = CreatePhysicsScene();
        AddStaticBox(scene, Float3.Zero);
        AddStaticBox(scene, new Float3(0.5f, 0, 0));
        scene.Physics.Update();

        for (int i = 0; i < 8; i++)
            scene.Physics.ContactEvents.Dispatch(scene.Physics.World);

        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++)
            scene.Physics.ContactEvents.Dispatch(scene.Physics.World);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;

#if !DEBUG
        Assert.Equal(0, allocated);
#else
        Assert.True(allocated >= 0);
#endif
    }

    [Fact]
    public void Raycast_LayerMask_IsZeroAllocation()
    {
        Scene scene = CreatePhysicsScene();
        AddStaticBox(scene, Float3.Zero);
        scene.Physics.Update();

        LayerMask mask = LayerMask.Everything;
        var origin = new Float3(0, 5, 0);
        var down = new Float3(0, -1, 0);
        Assert.True(scene.Physics.Raycast(origin, down, 10f, mask));
        // Generous warmup so tiered-JIT promotion (which can allocate on the calling thread,
        // observed on macOS ARM64) settles before the measured window; the steady-state
        // zero-allocation contract itself is unchanged.
        for (int i = 0; i < 1_000; i++)
            scene.Physics.Raycast(origin, down, 10f, mask);

        long before = GC.GetAllocatedBytesForCurrentThread();
        int hits = 0;
        for (int i = 0; i < 100; i++)
            if (scene.Physics.Raycast(origin, down, 10f, mask)) hits++;
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(100, hits);

        string? evidenceRoot = Environment.GetEnvironmentVariable("XENGINE_TEST_SCREENSHOT_DIR");
        if (!string.IsNullOrWhiteSpace(evidenceRoot))
        {
            System.IO.Directory.CreateDirectory(evidenceRoot);
            System.IO.File.WriteAllText(System.IO.Path.Combine(evidenceRoot, "physics-raycast-allocation.json"),
                System.Text.Json.JsonSerializer.Serialize(new
                {
                    _previousLoopInitialized, _previousLoopThread,
                    currentThread = Environment.CurrentManagedThreadId,
                    engineThread = XEngine.Async.XTaskLoopHelper.MainThreadId,
                    warmupQueries = 1000, measuredQueries = 100, hits, allocatedBytes = allocated,
                }));
        }

#if !DEBUG
        Assert.Equal(0, allocated);
#else
        Assert.True(allocated >= 0);
#endif
    }
}

public sealed class PhysicsPerfContextTests
{
    [Fact]
    public void Raycast_AllocationFixture_RebindsAndRestoresStaleEngineThread()
    {
        int previousThread = XEngine.Async.XTaskLoopHelper.MainThreadId;
        bool previousInitialized = XEngine.Async.XTaskLoopHelper.IsInitialized;
        int staleThread = Environment.CurrentManagedThreadId + 1_000_000;
        try
        {
            XEngine.Async.XTaskLoopHelper.Initialize(staleThread);
            using (var fixture = new PhysicsPerfTests())
            {
                fixture.Raycast_LayerMask_IsZeroAllocation();
                Assert.Equal(Environment.CurrentManagedThreadId, XEngine.Async.XTaskLoopHelper.MainThreadId);
            }
            Assert.Equal(staleThread, XEngine.Async.XTaskLoopHelper.MainThreadId);
        }
        finally
        {
            XEngine.Async.XTaskLoopHelper.Initialize(previousThread);
            typeof(XEngine.Async.XTaskLoopHelper).GetProperty(nameof(XEngine.Async.XTaskLoopHelper.IsInitialized))!
                .SetValue(null, previousInitialized);
        }
    }
}
