// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using Xunit;

using XEngine.Runtime;
using XEngine.Runtime.Resources;

namespace XEngine.Runtime.Test;

/// <summary>
/// Regression tests for the scene lifecycle additions (prowl-sync M3.2/M3.3): Unity-style deferred
/// <see cref="EngineObject.Destroy"/> (upstream 01717709) and <see cref="Scene.DontDestroyOnLoad"/>
/// (upstream 521dea61).
/// </summary>
public class SceneLifecycleTests : RuntimeTestBase
{
    [Fact]
    public void DeferredDestroy_QueuesThenDisposesOnFlush()
    {
        var go = CreateGameObject("DeferredVictim");
        var obj = (EngineObject)go;

        EngineObject.Destroy(go);
        Assert.False(go.IsDisposed, "Destroy must be deferred: the object stays alive until the flush.");

        EngineObject.FlushDestroyed();
        Assert.True(go.IsDisposed, "Flush must dispose queued objects.");
        Assert.True(obj.IsDisposed);
    }

    [Fact]
    public void DeferredDestroy_DoubleQueueAndDisposedObject_AreSafe()
    {
        var go = CreateGameObject("DeferredDouble");

        EngineObject.Destroy(go);
        EngineObject.Destroy(go); // duplicate queue is a no-op
        EngineObject.FlushDestroyed();

        EngineObject.Destroy(go); // already disposed: ignored
        EngineObject.FlushDestroyed();
        Assert.True(go.IsDisposed);
    }

    [Fact]
    public void DontDestroyOnLoad_ObjectMigratesIntoNewScene()
    {
        var oldScene = CreateScene();
        Scene.Load(oldScene); // establishes Current

        var survivor = CreateGameObject("Survivor");
        var mortal = CreateGameObject("Mortal");
        oldScene.Add(survivor);
        oldScene.Add(mortal);
        Scene.DontDestroyOnLoad(survivor);

        var newScene = CreateScene();
        Scene.Load(newScene);

        Assert.Same(newScene, Scene.Current);
        Assert.False(survivor.IsDisposed, "Marked objects must survive the load.");
        Assert.Same(newScene, survivor.Scene);

        // XEngine semantics: scene disposal disposes unmarked GameObjects outright (Scene.OnDispose).
        Assert.True(mortal.IsDisposed);
    }

    [Fact]
    public void Unload_OrphansMarkedObjects_AndClearsCurrent()
    {
        var scene = CreateScene();
        Scene.Load(scene);
        var survivorWannabe = CreateGameObject("MarkedButUnloadWins");
        scene.Add(survivorWannabe);
        Scene.DontDestroyOnLoad(survivorWannabe);

        Scene.Unload();

        // With no new scene to move into, the marked object is detached from its scene (the engine
        // orphans GameObjects on scene disposal instead of disposing them) and Current is cleared.
        Assert.True(survivorWannabe.Scene == null || survivorWannabe.Scene.IsDisposed);
        Assert.Null(Scene.Current);
    }
}
