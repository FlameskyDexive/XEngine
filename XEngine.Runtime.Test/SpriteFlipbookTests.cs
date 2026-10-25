// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Runtime.Resources;
using XEngine.Vector;

using Xunit;

namespace XEngine.Runtime.Test;

/// <summary>★ Gen-P2.3 — SpriteFlipbook frame advance / loop / pause / zero-alloc Update.</summary>
public sealed class SpriteFlipbookTests : RuntimeTestBase
{
    [Fact]
    public void Advance_AtFps_WrapsOnLoop()
    {
        Scene scene = CreateScene(enable: true);
        GameObject go = CreateGameObject("Hero");
        var renderer = go.AddComponent<SpriteRenderer>();
        var flip = go.AddComponent<SpriteFlipbook>();
        using Texture2D tex = new(8, 8);
        flip.Frames.Add(Sprite.Create(tex, new SpriteRect(0, 0, 8, 8), new Float2(0.5f, 0.5f), 8f, "a"));
        flip.Frames.Add(Sprite.Create(tex, new SpriteRect(0, 0, 8, 8), new Float2(0.5f, 0.5f), 8f, "b"));
        flip.Frames.Add(Sprite.Create(tex, new SpriteRect(0, 0, 8, 8), new Float2(0.5f, 0.5f), 8f, "c"));
        flip.Frames.Add(Sprite.Create(tex, new SpriteRect(0, 0, 8, 8), new Float2(0.5f, 0.5f), 8f, "d"));
        flip.FramesPerSecond = 60f;
        flip.Loop = true;
        scene.Add(go);
        flip.OnEnable();

        Assert.Equal(0, flip.CurrentFrame);
        Assert.True(flip.IsPlaying);
        Assert.Same(flip.Frames[0].Res, renderer.Sprite.Res);

        Update(scene, 1);
        Assert.Equal(1, flip.CurrentFrame);
        Update(scene, 1);
        Assert.Equal(2, flip.CurrentFrame);
        Update(scene, 1);
        Assert.Equal(3, flip.CurrentFrame);
        Update(scene, 1);
        Assert.Equal(0, flip.CurrentFrame);
        Assert.True(flip.IsPlaying);
        Assert.Same(flip.Frames[0].Res, renderer.Sprite.Res);
    }

    [Fact]
    public void Pause_HoldsFrame()
    {
        Scene scene = CreateScene(enable: true);
        GameObject go = CreateGameObject("Paused");
        go.AddComponent<SpriteRenderer>();
        var flip = go.AddComponent<SpriteFlipbook>();
        using Texture2D tex = new(4, 4);
        flip.Frames.Add(Sprite.Create(tex, new SpriteRect(0, 0, 4, 4), new Float2(0.5f, 0.5f), 4f, "0"));
        flip.Frames.Add(Sprite.Create(tex, new SpriteRect(0, 0, 4, 4), new Float2(0.5f, 0.5f), 4f, "1"));
        flip.FramesPerSecond = 60f;
        scene.Add(go);
        flip.OnEnable();

        Update(scene, 1);
        Assert.Equal(1, flip.CurrentFrame);
        flip.Pause();
        Update(scene, 4);
        Assert.Equal(1, flip.CurrentFrame);
        Assert.False(flip.IsPlaying);
    }

    [Fact]
    public void Once_StopsOnLastFrame()
    {
        Scene scene = CreateScene(enable: true);
        GameObject go = CreateGameObject("Once");
        go.AddComponent<SpriteRenderer>();
        var flip = go.AddComponent<SpriteFlipbook>();
        using Texture2D tex = new(4, 4);
        flip.Frames.Add(Sprite.Create(tex, new SpriteRect(0, 0, 4, 4), new Float2(0.5f, 0.5f), 4f, "0"));
        flip.Frames.Add(Sprite.Create(tex, new SpriteRect(0, 0, 4, 4), new Float2(0.5f, 0.5f), 4f, "1"));
        flip.FramesPerSecond = 60f;
        flip.Loop = false;
        scene.Add(go);
        flip.OnEnable();

        Update(scene, 4);
        Assert.Equal(1, flip.CurrentFrame);
        Assert.False(flip.IsPlaying);
    }

    [Fact]
    public void Update_SteadyState_AllocatesNothing()
    {
        var flip = new SpriteFlipbook();
        using Texture2D tex = new(2, 2);
        flip.Frames.Add(Sprite.Create(tex, new SpriteRect(0, 0, 2, 2), new Float2(0.5f, 0.5f), 2f, "0"));
        flip.Frames.Add(Sprite.Create(tex, new SpriteRect(0, 0, 2, 2), new Float2(0.5f, 0.5f), 2f, "1"));
        flip.FramesPerSecond = 1f; // dt = 1/60 → no frame change for many Updates
        flip.IsPlaying = true;
        flip.CurrentFrame = 0;

        flip.Update();
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 2000; i++)
            flip.Update();
        long after = GC.GetAllocatedBytesForCurrentThread();
        Assert.Equal(0, after - before);
    }
}
