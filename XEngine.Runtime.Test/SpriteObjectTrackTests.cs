// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using Xunit;

namespace XEngine.Runtime.Test;

/// <summary>Discrete sprite object-reference keys (hold interpolation, no GPU).</summary>
public sealed class SpriteObjectTrackTests : RuntimeTestBase
{
    [Fact]
    public void SpriteObjectTrack_Evaluate_HoldsLastKeyAtOrBeforeTime()
    {
        var track = new SpriteObjectTrack();
        track.Keys.Add(new SpriteObjectKey { Time = 0f, Id = "idle" });
        track.Keys.Add(new SpriteObjectKey { Time = 0.5f, Id = "walk" });

        Assert.Null(track.Evaluate(-0.1f));
        Assert.Equal("idle", track.Evaluate(0f));
        Assert.Equal("idle", track.Evaluate(0.25f));
        Assert.Equal("walk", track.Evaluate(0.5f));
        Assert.Equal("walk", track.Evaluate(2f));
    }

    [Fact]
    public void SpriteObjectTrack_Apply_SetsResolverLabel()
    {
        var track = new SpriteObjectTrack();
        track.Keys.Add(new SpriteObjectKey { Time = 0f, Id = "walk" });
        GameObject go = CreateGameObject("Actor");
        var resolver = go.AddComponent<SpriteResolver>();
        resolver.Category = "Move";

        track.Apply(resolver, 0f);

        Assert.Equal("walk", resolver.Label);
    }
}
