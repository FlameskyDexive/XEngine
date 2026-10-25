using System;
using System.IO;

using XEngine.Vector;
using XEngine.VoxelWorld;

using Xunit;

namespace XEngine.Runtime.Test.VoxelWorld;

public sealed class VoxelPlayableTests
{
    [Fact]
    public void Break_EmitsDebrisChipsFromBrokenType()
    {
        using var world = new PlayerWorld();
        world.LoadColumn();
        Assert.Equal(0, world.Host.BreakFx.Alive.Count);
        Assert.True(world.Host.TryBreak(new Float3(8.5f, 20f, 8.5f), new Float3(0f, -1f, 0f), 20f));
        Assert.True(world.Host.Streamer.TryGetVoxel(new Int3(8, 15, 8), out Voxel hole) && hole.IsEmpty);
        Assert.Equal(VoxelBreakFx.ChipsPerBreak, world.Host.BreakFx.Alive.Count);
        Assert.Equal((ushort)2, world.Host.LastBrokenType);
    }

    [Fact]
    public void Debris_FallsWithGravity()
    {
        var fx = new VoxelBreakFx();
        fx.Burst(new Float3(0f, 2f, 0f), new Color((byte)76, (byte)168, (byte)78), seed: 3);
        float y0 = fx.Alive[0].Position.Y;
        fx.Tick(0.1f);
        Assert.True(fx.Alive[0].Position.Y < y0);
        Assert.True(fx.Alive[0].Velocity.Y < 0f);
    }

    [Fact]
    public void Place_RebuildsBrokenColumn()
    {
        using var world = new PlayerWorld();
        world.LoadColumn();
        Assert.True(world.Host.TryBreak(new Float3(8.5f, 20f, 8.5f), new Float3(0f, -1f, 0f), 20f));
        world.Host.Inventory.Selected = 2;
        Assert.True(world.Host.TryPlace(new Float3(8.5f, 20f, 8.5f), new Float3(0f, -1f, 0f), 4, 20f));
        Assert.True(world.Host.Streamer.TryGetVoxel(new Int3(8, 15, 8), out Voxel placed));
        Assert.Equal(world.Host.Inventory.Types[2], placed.TypeIndex);
    }

    [Fact]
    public void Motor_JumpLeavesGroundThenFalls()
    {
        var motor = new VoxelCharacterMotor();
        Float3 up = motor.Step(Float2.Zero, jump: true, grounded: true, new Float3(0, 0, 1), new Float3(1, 0, 0), 1f / 60f);
        Assert.True(up.Y > 0f);
        Assert.False(motor.Grounded);
        Float3 later = Float3.Zero;
        for (int i = 0; i < 40; i++)
            later = motor.Step(Float2.Zero, jump: false, grounded: false, new Float3(0, 0, 1), new Float3(1, 0, 0), 1f / 60f);
        Assert.True(later.Y < 0f);
        Assert.True(motor.VerticalSpeed < 0f);
    }

    [Fact]
    public void Motor_WasdMovesForwardOnGround()
    {
        var motor = new VoxelCharacterMotor { MoveSpeed = 6f };
        Float3 step = motor.Step(new Float2(0f, 1f), jump: false, grounded: true, new Float3(0f, 0f, 1f), new Float3(1f, 0f, 0f), 1f);
        Assert.True(step.Z > 1f);
        Assert.True(MathF.Abs(step.Y) < 1e-4f);
    }

    [Fact]
    public void CharacterRig_PlayerHasHeadTorsoLimbs()
    {
        VoxelBodyPart[] parts = VoxelCharacterRig.Player();
        Assert.True(parts.Length >= 6);
        Assert.Contains(parts, p => p.Name == "Head");
        Assert.Contains(parts, p => p.Name == "Torso");
        Assert.Contains(parts, p => p.Name == "ArmL");
        Assert.Contains(parts, p => p.Name == "LegR");
        Assert.True(parts[0].Size.X > 0f);
    }

    [Fact]
    public void Mob_DiesAfterEnoughHits()
    {
        var mob = new VoxelMob { Position = new Float3(12f, 16f, 8f), Health = 20 };
        Assert.True(mob.IsAlive);
        Assert.Equal(12, mob.Hurt(8));
        Assert.True(mob.IsAlive);
        Assert.Equal(0, mob.Hurt(20));
        Assert.False(mob.IsAlive);
    }

    [Fact]
    public void Combat_PrefersMobOverTerrain()
    {
        using var world = new PlayerWorld();
        world.LoadColumn();
        var mob = new VoxelMob { Position = new Float3(8.5f, 17.2f, 8.5f), Health = 10 };
        var combat = new VoxelCombat();
        Assert.True(combat.TryAttack(
            world.Host,
            new[] { mob },
            new Float3(8.5f, 20f, 8.5f),
            new Float3(0f, -1f, 0f),
            20f,
            damage: 4));
        Assert.Equal(6, mob.Health);
        Assert.True(world.Host.Streamer.TryGetVoxel(new Int3(8, 15, 8), out Voxel surface) && !surface.IsEmpty);
    }

    [Fact]
    public void Evidence_RendersBuildBreakAndCharacter()
    {
        using var world = new PlayerWorld();
        world.LoadColumn();
        Assert.True(world.Host.TryBreak(new Float3(8.5f, 20f, 8.5f), new Float3(0f, -1f, 0f), 20f));
        world.Host.BreakFx.Tick(0.05f);
        world.Host.Inventory.Selected = 2;
        Assert.True(world.Host.TryPlace(new Float3(10.5f, 20f, 8.5f), new Float3(0f, -1f, 0f), 4, 20f));

        string evidence = Path.Combine(FindRepositoryRoot(), "docs", "superpowers", "evidence", "voxelworld");
        Directory.CreateDirectory(evidence);
        string breakPath = VoxelPlayableRender.Write(
            Path.Combine(evidence, "demo-build-break.png"),
            world.Host,
            new Float3(11.6f, 18.4f, 4.2f),
            new Float3(8.8f, 16.3f, 8.4f),
            VoxelCharacterRig.Player(),
            new Float3(7.1f, 16f, 7.6f),
            new VoxelMob { Position = new Float3(12.4f, 16f, 9.2f), Health = 12 },
            includeDebris: true);
        string playPath = VoxelPlayableRender.Write(
            Path.Combine(evidence, "demo-voxel-character.png"),
            world.Host,
            new Float3(12.2f, 19.4f, 3.6f),
            new Float3(9.2f, 16.7f, 8.2f),
            VoxelCharacterRig.Player(),
            new Float3(7.3f, 16.85f, 7.1f),
            new VoxelMob { Position = new Float3(12.8f, 16f, 9.1f), Health = 8 },
            includeDebris: true);
        Assert.True(new FileInfo(breakPath).Length > 4000);
        Assert.True(new FileInfo(playPath).Length > 4000);
        File.WriteAllText(Path.Combine(evidence, "demo-build-break.md"),
            "# Voxel build / break / debris\n\nDate: 2026-09-16. Branch: `main`.\n\n" +
            "Break removes the grass cap, emits owned debris chips, then place rebuilds a neighbor cell. " +
            "Screenshot: [demo-build-break.png](demo-build-break.png)\n");
        File.WriteAllText(Path.Combine(evidence, "demo-voxel-character.md"),
            "# Voxel character + mob\n\nDate: 2026-09-16. Branch: `main`.\n\n" +
            "Owned cube-rig player (not Dungeon Mason) plus a stone mob. Jump uses gravity, LMB hits the mob when the ray meets it.\n\n" +
            "Screenshot: [demo-voxel-character.png](demo-voxel-character.png)\n");
    }

    private sealed class PlayerWorld : IDisposable
    {
        public XEngine.VoxelWorld.VoxelWorld Host { get; } = new()
        {
            EnableStreaming = false,
            ViewDistance = 1,
            TerrainTypeIndex = 3,
            TerrainSurfaceTypeIndex = 2,
        };

        public PlayerWorld()
        {
            Host.ConfigureStreamer();
            Host.Streamer.MaxChunkY = 1;
            Host.Streamer.ViewDistance = 1;
            Host.Streamer.Generator = new FlatTerrainGenerator { Altitude = 16, SurfaceTypeIndex = 2 };        }

        public void LoadColumn()
        {
            Host.Focus = new Float3(8f, 16f, 8f);
            Host.Streamer.SetFocus(Host.Focus);
            Host.Streamer.Tick(32);
        }

        public void Dispose() => Host.Streamer.UnloadAll();
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "XEngine.sln")))
                return current.FullName;
            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the XEngine repository root.");
    }
}
