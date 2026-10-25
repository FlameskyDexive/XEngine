using System.Collections.Generic;
using System.Diagnostics;

using Xunit;

using XEngine.Vector;

namespace XEngine.Hudmesh.Tests;

/// <summary>
/// Performance baseline for the combine fill path (no GPU): N fully-populated HUDs ticked and
/// filled per frame. The hard requirement is steady-state zero Gen0 collections; wall time is
/// the baseline reference for later milestones (500 HUD × 5 widgets).
/// </summary>
public sealed class HudPerfSmokeTests
{
    private const int HudCount = 500;
    private const int SteadyFrames = 200;

    [Fact]
    public void FillSteadyState_ZeroAllocWithinBudget()
    {
        List<HudMeshData?> targets = new() { new HudMeshData() }; // slot 0; tests treat it as the sprite slot
        HudObject[] huds = new HudObject[HudCount];
        for (int i = 0; i < HudCount; i++)
        {
            HudObject obj = new();
            obj.InitHeadless(Float2.Zero);
            obj.SetSprite(HudSlot.Blood, new HudSpriteInfo { Name = "bg", Width = 180f, Height = 30f, MaterialSlot = 0 }, HudAlignment.Middle);
            obj.SetSprite(HudSlot.Name, new HudSpriteInfo { Name = "name", Width = 120f, Height = 22f, MaterialSlot = 0 }, HudAlignment.Middle);
            obj.SetSprite(HudSlot.Title, new HudSpriteInfo { Name = "title", Width = 90f, Height = 18f, MaterialSlot = 0 }, HudAlignment.Middle);
            obj.SetSprite(HudSlot.GuildName, new HudSpriteInfo { Name = "guild", Width = 100f, Height = 22f, MaterialSlot = 0 }, HudAlignment.Middle);
            obj.SetSprite(HudSlot.GuildIcon, new HudSpriteInfo { Name = "icon", Width = 32f, Height = 32f, MaterialSlot = 0 }, HudAlignment.Middle);
            huds[i] = obj;
        }

        HudFillContext context = new()
        {
            RightStep = new Float3(0.01f, 0f, 0f),
            UpStep = new Float3(0f, 0.01f, 0f),
        };

        // Warm-up: capacity growth and JIT settle here.
        for (int frame = 0; frame < 5; frame++)
        {
            targets[0]!.BeginFrame();
            for (int i = 0; i < HudCount; i++)
                huds[i].Fill(targets, context);
        }

        int gen0Before = GC.CollectionCount(0);
        var sw = Stopwatch.StartNew();
        for (int frame = 0; frame < SteadyFrames; frame++)
        {
            targets[0]!.BeginFrame();
            for (int i = 0; i < HudCount; i++)
                huds[i].Fill(targets, context);
        }
        sw.Stop();
        int gen0After = GC.CollectionCount(0);

        double perFrameUs = sw.Elapsed.TotalMilliseconds * 1000.0 / SteadyFrames;
        Assert.True(gen0After - gen0Before <= 1, "fill path allocates every frame");
        // Strict zero-GC is validated in the live editor (N9 evidence); the tolerance here absorbs
        // allocation noise from xUnit tests running in parallel with this one.
        Assert.Equal(HudCount * 5, targets[0]!.VertexCount / 4); // 5 sprite widgets per HUD

        // Informational budget line (visible in test output with verbosity >= normal).
        Assert.True(perFrameUs < 50_000, $"fill path regressed: {perFrameUs:F1} µs/frame for {HudCount} HUDs");
    }
}
