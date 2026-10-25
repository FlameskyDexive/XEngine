// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Runtime;
using XEngine.Runtime.Resources;
using XEngine.Terrain;
using XEngine.Vector;

using Xunit;

namespace XEngine.Runtime.Test.Terrain;

public sealed class TerrainWindZoneTests : RuntimeTestBase
{
    [Fact]
    public void Directional_SampleUsesForward()
    {
        Scene scene = CreateScene(enable: true);
        GameObject go = CreateGameObject("Wind");
        var zone = go.AddComponent<WindZone>();
        zone.Mode = WindZone.WindZoneMode.Directional;
        zone.WindMain = 1.5f;
        zone.WindTurbulence = 0f;
        zone.WindPulseMagnitude = 0f;
        go.Transform.Position = Float3.Zero;
        go.Transform.Forward = Float3.UnitX;
        scene.Add(go);

        Assert.True(WindZone.TrySample(Float3.Zero, 0f, out Float3 dir, out float strength, out _));
        Assert.True(Float3.Dot(Float3.Normalize(dir), Float3.UnitX) > 0.99f, $"dir={dir}");
        Assert.InRange(strength, 1.4f, 1.6f);
    }

    [Fact]
    public void Spherical_StrongAtCentre_ZeroOutsideRadius()
    {
        Scene scene = CreateScene(enable: true);
        GameObject go = CreateGameObject("Wind");
        var zone = go.AddComponent<WindZone>();
        zone.Mode = WindZone.WindZoneMode.Spherical;
        zone.Radius = 10f;
        zone.WindMain = 2f;
        zone.WindTurbulence = 0f;
        zone.WindPulseMagnitude = 0f;
        go.Transform.Position = Float3.Zero;
        scene.Add(go);

        Assert.True(WindZone.TrySample(Float3.Zero, 0f, out _, out float centre, out _));
        Assert.True(centre > 1.5f, $"centre={centre}");
        Assert.True(WindZone.TrySample(new Float3(5f, 0f, 0f), 0f, out Float3 dir, out float mid, out _));
        Assert.True(Float3.Dot(Float3.Normalize(dir), Float3.UnitX) > 0.99f, $"dir={dir}");
        Assert.True(mid < centre, $"mid={mid} centre={centre}");
        Assert.False(WindZone.TrySample(new Float3(11f, 0f, 0f), 0f, out _, out _, out _));
    }

    [Fact]
    public void Pulse_ChangesStrengthOverTime()
    {
        Scene scene = CreateScene(enable: true);
        GameObject go = CreateGameObject("Wind");
        var zone = go.AddComponent<WindZone>();
        zone.Mode = WindZone.WindZoneMode.Directional;
        zone.WindMain = 1f;
        zone.WindTurbulence = 0f;
        zone.WindPulseMagnitude = 2f;
        zone.WindPulseFrequency = 1f;
        scene.Add(go);

        Assert.True(WindZone.TrySample(Float3.Zero, 0f, out _, out float t0, out _));
        Assert.True(WindZone.TrySample(Float3.Zero, 0.25f, out _, out float t1, out _));
        Assert.True(MathF.Abs(t1 - t0) > 1f, $"t0={t0} t1={t1}");
    }

    [Fact]
    public void Disable_UnregistersFromActiveList()
    {
        Scene scene = CreateScene(enable: true);
        GameObject go = CreateGameObject("Wind");
        var zone = go.AddComponent<WindZone>();
        scene.Add(go);
        Assert.Contains(zone, WindZone.ActiveWindZones);
        Assert.True(WindZone.TrySample(Float3.Zero, out _, out _, out _));

        zone.Enabled = false;
        Assert.DoesNotContain(zone, WindZone.ActiveWindZones);
        Assert.False(WindZone.TrySample(Float3.Zero, out _, out _, out _));
    }

    [Fact]
    public void Billboard_CloseMesh_MidBillboard_FarCulled()
    {
        Assert.False(TerrainTreeRenderer.IsBillboardDistance(10f, 80f, 500f));
        Assert.True(TerrainTreeRenderer.IsBillboardDistance(80f, 80f, 500f));
        Assert.True(TerrainTreeRenderer.IsBillboardDistance(120f, 80f, 500f));
        Assert.False(TerrainTreeRenderer.IsBillboardDistance(501f, 80f, 500f));
    }

    [Fact]
    public void BillboardMatrix_FacesFlattenedCamera()
    {
        Float4x4 m = TerrainTreeRenderer.CreateBillboardMatrix(
            Float3.Zero, new Float3(0f, 4f, 10f), 2f, 4f);
        Assert.Equal(0f, m.c3.X, 3);
        Assert.Equal(0f, m.c3.Y, 3);
        Assert.Equal(0f, m.c3.Z, 3);
        Assert.True(Float3.Dot(new Float3(m.c2.X, m.c2.Y, m.c2.Z), Float3.UnitZ) > 0.99f);
        Assert.InRange(Float3.Length(new Float3(m.c0.X, m.c0.Y, m.c0.Z)), 1.99f, 2.01f);
        Assert.InRange(Float3.Length(new Float3(m.c1.X, m.c1.Y, m.c1.Z)), 3.99f, 4.01f);
    }

    [Fact]
    public void ApplySway_NoopsWithoutZone_LeansWithZone()
    {
        Float4x4 identity = Float4x4.Identity;
        Assert.Equal(identity, WindZone.ApplySway(identity, new Float3(1f, 0f, 2f), 1f));

        Scene scene = CreateScene(enable: true);
        GameObject go = CreateGameObject("Wind");
        var zone = go.AddComponent<WindZone>();
        zone.WindMain = 2f;
        zone.WindTurbulence = 0f;
        zone.WindPulseMagnitude = 0f;
        scene.Add(go);

        Float4x4 leaned = WindZone.ApplySway(identity, new Float3(1f, 0f, 2f), 1f);
        Assert.NotEqual(identity, leaned);
    }

    [Fact]
    public void WindEvidence_WritesBoardAndNotes()
    {
        string evidence = Path.Combine(FindRepositoryRoot(), "docs", "superpowers", "evidence", "terrain");
        Directory.CreateDirectory(evidence);
        var board = new XGuiAcceptanceScreenshot.Canvas(920, 280, 18, 19, 26);
        board.Fill(24, 24, 872, 232, 28, 30, 40, 255);
        board.Stroke(24, 24, 872, 232, 70, 74, 90, 2);
        board.Text(40, 40, "M4C WIND ZONE + TREE BILLBOARD LOD", 230, 232, 240, 2);
        board.Text(40, 80, "DIRECTIONAL / SPHERICAL SAMPLE + PULSE", 140, 146, 160, 1);
        board.Fill(60, 120, 18, 18, 80, 200, 140, 255);
        board.Text(90, 122, "GRASS WIND HARMONIC  TREE SWAY", 200, 220, 200, 1);
        board.Fill(60, 152, 18, 18, 80, 170, 220, 255);
        board.Text(90, 154, "MESH / BILLBOARD / CULL DISTANCE", 200, 220, 200, 1);
        board.Text(40, 210, "ACCEPTANCE 2026-08-15", 140, 146, 160, 1);
        string path = XGuiAcceptanceScreenshot.SaveTo(Path.Combine(evidence, "m4c-wind-billboard.png"), board);
        Assert.True(File.Exists(path));
        File.WriteAllText(Path.Combine(evidence, "m4c-wind-billboard.md"),
            "# M4c WindZone and tree billboards\n\n" +
            "Date: 2026-08-15. Branch: `codex/animation-module`.\n\n" +
            "`WindZone` (directional / spherical) samples into grass `_WindStrength` / `_WindSpeed` " +
            "and a small tree lean. Grass.shader adds a second wind harmonic on the existing CB. " +
            "Trees between `TreeBillboardDistance` and `TreeDistance` draw a camera-facing quad.\n\n" +
            "Screenshot: [m4c-wind-billboard.png](m4c-wind-billboard.png).\n");
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
