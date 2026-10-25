using System.IO;
using System.Linq;

using XEngine.UnityImporter.Editor;

using Xunit;

namespace XEngine.Editor.Test;

/// <summary>
/// Golden tests for UnityParticleReader against trimmed real Hovl Studio prefab documents
/// (Fixtures/Particles). The fixtures are Unity YAML class documents (1/4/198/199/120) copied
/// from the source pack, so the assertions pin the exact Shuriken values the converter maps.
/// </summary>
public sealed class UnityParticleReaderTests
{
    private static string FixturePath(string name)
    {
        // Fixtures live in the project source tree (no csproj copy step needed):
        // bin/Debug/net10.0 → up 4 → repo root.
        string repoRoot = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", ".."));
        return Path.Combine(repoRoot, "XEngine.Editor.Test", "Fixtures", "Particles", name);
    }

    [Fact]
    public void Buff1_RootSystem_ParsesCoreAndColorOverLifetime()
    {
        var data = UnityParticleReader.Read(FixturePath("Buff1_RootDocs.prefab"));
        Assert.NotEmpty(data.Systems);
        Assert.Equal(data.Systems.Count, data.Renderers.Count);

        UnityParticleSystemData root = data.Systems[0];
        Assert.True(root.Looping);
        Assert.True(root.PlayOnAwake);
        Assert.Equal(3f, root.Duration, 3);
        Assert.True(root.Initial.Enabled);
        Assert.Equal(0.5f, root.Initial.StartLifetime.Scalar, 3);
        Assert.Equal(1000, root.Initial.MaxParticles);
        Assert.NotNull(root.ColorOverLifetime.MaxGradient);
        Assert.Equal(3, root.ColorOverLifetime.MaxGradient!.AlphaKeys.Length);
        Assert.Equal(32768f / 65535f, root.ColorOverLifetime.MaxGradient.AlphaKeys[1].Time, 5);
        Assert.Equal(0f, root.ColorOverLifetime.MaxGradient.AlphaKeys[2].A);

        // The renderer is a billboard with a material reference and default vertex streams.
        UnityParticleRendererData renderer = data.Renderers[0];
        Assert.Equal(0, renderer.RenderMode); // billboard
        Assert.NotEmpty(renderer.MaterialGuids);
        Assert.False(string.IsNullOrEmpty(renderer.VertexStreams));
    }

    [Fact]
    public void AuthoredModuleFieldNames_ReadAliasesAndScalarCurveLayouts()
    {
        // Actual serialized names used by all 1,200 Hovl ParticleSystem documents.
        const string yaml = """
--- !u!198 &1981
ParticleSystem:
  InheritVelocityModule:
    enabled: 1
    m_Mode: 1
    m_Curve: {minMaxState: 0, scalar: 0.75}
  ClampVelocityModule:
    enabled: 1
    separateAxis: 1
    drag: {minMaxState: 0, scalar: 2.5}
  ForceModule:
    enabled: 1
    randomizePerFrame: 1
  NoiseModule:
    enabled: 1
    frequency: 3.25
  SizeBySpeedModule:
    enabled: 1
    curve: {minMaxState: 0, scalar: 1.5}
    range: {x: 3, y: 9}
  RotationBySpeedModule:
    enabled: 1
    range: {x: 2, y: 5}
  ColorBySpeedModule:
    enabled: 1
    gradient:
      minMaxState: 0
      maxColor: {r: 1, g: 0.5, b: 0, a: 0.25}
    range: {x: 4, y: 8}
  CollisionModule:
    enabled: 1
    m_Dampen: {minMaxState: 0, scalar: 0.4}
    m_Bounce: {minMaxState: 0, scalar: 0.7}
    m_EnergyLossOnCollision: {minMaxState: 0, scalar: 0.2}
  LightsModule:
    enabled: 1
    range: 1
    intensity: 1
    rangeCurve: {minMaxState: 0, scalar: 2}
    intensityCurve: {minMaxState: 0, scalar: 4}
""";
        string path = Path.GetTempFileName();
        File.WriteAllText(path, yaml);
        try
        {
            var system = Assert.Single(UnityParticleReader.Read(path).Systems);
            Assert.Equal(1, system.InheritVelocityMode);
            Assert.Equal(0.75f, system.InheritVelocityCurve.Scalar);
            Assert.True(system.LimitSeparateAxes);
            Assert.Equal(2.5f, system.Drag);
            Assert.Equal(1f, system.ForceRandomized);
            Assert.Equal(3.25f, system.NoiseFrequency.Scalar);
            Assert.Equal(1.5f, system.SizeBySpeed.Scalar);
            Assert.Equal(3f, system.SizeBySpeedMin);
            Assert.Equal(9f, system.SizeBySpeedMax);
            Assert.Equal(2f, system.RotationBySpeedMin);
            Assert.Equal(5f, system.RotationBySpeedMax);
            Assert.Equal(0.25f, system.ColorBySpeed.MaxColor[3]);
            Assert.Equal(4f, system.ColorBySpeedMin);
            Assert.Equal(8f, system.ColorBySpeedMax);
            Assert.Equal(0.4f, system.CollisionDampen);
            Assert.Equal(0.7f, system.CollisionBounce);
            Assert.Equal(0.2f, system.CollisionLifetimeLoss);
            Assert.Equal(2f, system.LightRange.Scalar);
            Assert.Equal(4f, system.LightIntensity.Scalar);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void MagicCircle1_HasMultipleSystems_ParsesShapeAndEmission()
    {
        var data = UnityParticleReader.Read(FixturePath("MagicCircle1_RootDocs.prefab"));
        Assert.True(data.Systems.Count >= 2, $"expected several systems, got {data.Systems.Count}");

        // Every system must carry a start color; circle effects tint their quads.
        Assert.All(data.Systems, s => Assert.NotNull(s.Initial.StartColor));

        // At least one system must define emission rate or a burst (circles pulse via bursts).
        Assert.Contains(data.Systems,
            s => s.Emission.RateOverTime.Scalar > 0 || s.Emission.Bursts.Count > 0);
    }

    [Fact]
    public void LaserBeam1_LineRenderer_ParsesWidthAndGradient()
    {
        var data = UnityParticleReader.Read(FixturePath("LaserBeam1_LineRenderers.prefab"));
        UnityLineRendererData line = Assert.Single(data.LineRenderers);
        // Hovl ships the line disabled; Hovl_Laser enables it at runtime.
        Assert.False(line.Enabled);
        Assert.NotEmpty(line.MaterialGuids);
        Assert.Equal(0.3f, line.WidthMultiplier, 3);
        Assert.NotNull(line.ColorGradient);
        Assert.NotEmpty(line.ColorGradient!.ColorKeys);
        Assert.Equal(2, line.Positions.Count); // serialized 0→+Z stub endpoints
    }

    [Fact]
    public void Meteor_SubSystems_PopulateModuleSnapshots()
    {
        // Fixture: 1 plain + 2 sub-emitter systems from Meteor.prefab, 3 texture-sheet
        // systems + 1 trail system from Meteor 2.prefab (real module distribution).
        var data = UnityParticleReader.Read(FixturePath("Meteor_SystemDocs.prefab"));
        Assert.True(data.Systems.Count >= 7, $"expected >=7 systems, got {data.Systems.Count}");

        // Texture sheet animation must survive parsing on at least one system.
        Assert.Contains(data.Systems, s => s.UvEnabled && s.TilesX > 1);

        // Trails and sub-emitter snapshots parse (module data is captured even when the
        // serialized emitter reference is fileID 0 — see converter warning contract).
        Assert.Contains(data.Systems, s => s.TrailsEnabled);
        Assert.Contains(data.Systems, s => s.SubEmittersEnabled && s.SubEmitters.Count > 0);
    }

    [Fact]
    public void SyntheticFixture_CoversMinMaxCurveStates_AndGradientTimes()
    {
        const string yaml = """
--- !u!198 &100
ParticleSystem:
  m_ObjectHideFlags: 1
  m_GameObject: {fileID: 42}
  serializedVersion: 5
  lengthInSec: 2.5
  simulationSpeed: 1
  looping: 0
  prewarm: 1
  playOnAwake: 1
  startDelay:
    serializedVersion: 2
    minMaxState: 3
    scalar: 0.4
    minScalar: 0.1
    maxCurve:
      serializedVersion: 2
      m_Curve:
      - serializedVersion: 3
        time: 0
        value: 0.2
        inSlope: 0
        outSlope: 1
        tangentMode: 0
        weightedMode: 0
        inWeight: 0.33333334
        outWeight: 0.33333334
      m_PreInfinity: 2
      m_PostInfinity: 2
      m_RotationOrder: 4
    minCurve:
      serializedVersion: 2
      m_Curve: []
      m_PreInfinity: 2
      m_PostInfinity: 2
      m_RotationOrder: 4
  InitialModule:
    serializedVersion: 3
    enabled: 1
    startLifetime:
      serializedVersion: 2
      minMaxState: 0
      scalar: 1.5
    startSpeed:
      serializedVersion: 2
      minMaxState: 0
      scalar: 0
    startSize:
      serializedVersion: 2
      minMaxState: 0
      scalar: 0.25
    startRotation:
      serializedVersion: 2
      minMaxState: 2
      scalar: 3.1415
      minScalar: -3.1415
    maxNumParticles: 64
    startColor:
      serializedVersion: 2
      minMaxState: 1
      maxColor: {r: 1, g: 0.5, b: 0.25, a: 1}
      minColor: {r: 1, g: 1, b: 1, a: 1}
      maxGradient:
        serializedVersion: 2
        key0: {serializedVersion: 2, r: 1, g: 0, b: 0, a: 1}
        key1: {serializedVersion: 2, r: 0, g: 0, b: 1, a: 0}
        ctime0: 0
        ctime1: 32768
        atime0: 0
        atime1: 65535
        m_NumColorKeys: 2
        m_NumAlphaKeys: 2
  EmissionModule:
    serializedVersion: 2
    enabled: 1
    rateOverTime:
      serializedVersion: 2
      minMaxState: 0
      scalar: 12
    m_Bursts:
    - serializedVersion: 2
      time: 0.5
      countCurve:
        serializedVersion: 2
        minMaxState: 3
        scalar: 30
        minScalar: 10
  UVModule:
    enabled: 1
    frameOverTime:
      serializedVersion: 2
      minMaxState: 1
      scalar: 1
    tilesX: 8
    tilesY: 8
    animationType: 1
    rowIndex: 2
    cycles: 4
    flipU: 1
    flipV: 2

--- !u!199 &200
ParticleSystemRenderer:
  m_GameObject: {fileID: 42}
  m_Enabled: 1
  m_RenderMode: 4
  m_Mesh: {fileID: 4300000, guid: 0123456789abcdef0123456789abcdef, type: 3}
  m_Materials:
  - {fileID: 2100000, guid: fedcba9876543210fedcba9876543210, type: 2}
  m_LengthScale: 2
  m_VelocityScale: 1.5
  m_SortingFudge: 10
  m_SortMode: 2
  m_RenderAlignment: 4
  m_VertexStreams: 0001030420
  m_UseCustomVertexStreams: 1
""";
        string path = Path.Combine(Path.GetTempPath(), "unity-particle-synthetic.prefab");
        File.WriteAllText(path, yaml);
        try
        {
            var data = UnityParticleReader.Read(path);
            UnityParticleSystemData s = Assert.Single(data.Systems);

            Assert.False(s.Looping);
            Assert.True(s.Prewarm);
            Assert.Equal(2.5f, s.Duration, 3);
            Assert.Equal(42, s.GameObjectId);

            // startDelay: two-constants state (3) with scalar/minScalar.
            Assert.Equal(3, s.StartDelay.State);
            Assert.Equal(0.4f, s.StartDelay.Scalar, 3);
            Assert.Equal(0.1f, s.StartDelay.MinScalar, 3);
            Assert.NotEmpty(s.StartDelay.MaxCurve);
            Assert.Equal(0.2f, s.StartDelay.MaxCurve[0].Value, 3);

            // startRotation: two-curves state (2) — scalar/minScalar carry the constants.
            Assert.Equal(2, s.Initial.StartRotation.State);
            Assert.Equal(3.1415f, s.Initial.StartRotation.Scalar, 4);
            Assert.Equal(-3.1415f, s.Initial.StartRotation.MinScalar, 4);

            // startColor: gradient state (1) with 0..1 normalized gradient times.
            Assert.Equal(1, s.Initial.StartColor.State);
            Assert.Equal(0.5f, s.Initial.StartColor.MaxColor[1], 3);
            Assert.Equal(2, s.Initial.StartColor.MaxGradient!.ColorKeys.Length);
            Assert.Equal(0f, s.Initial.StartColor.MaxGradient.ColorKeys[0].Time, 3);
            Assert.Equal(32768f / 65535f, s.Initial.StartColor.MaxGradient.ColorKeys[1].Time, 5);
            Assert.Equal(1f, s.Initial.StartColor.MaxGradient.AlphaKeys[1].Time);
            Assert.Equal(0f, s.Initial.StartColor.MaxGradient.AlphaKeys[1].A, 3);

            // Emission burst with two-constants count.
            UnityBurstData burst = Assert.Single(s.Emission.Bursts);
            Assert.Equal(0.5f, burst.Time, 3);
            Assert.Equal(3, burst.Count.State);
            Assert.Equal(30f, burst.Count.Scalar, 3);
            Assert.Equal(10f, burst.Count.MinScalar, 3);

            // Texture sheet: single-row mode, flips decoded from the bitfield.
            Assert.True(s.UvEnabled);
            Assert.Equal(8, s.TilesX);
            Assert.Equal(8, s.TilesY);
            Assert.Equal(1, s.UvAnimationType);
            Assert.Equal(2, s.RowIndex);
            Assert.Equal(4, s.Cycles);
            Assert.True(s.FlipU);
            Assert.True(s.FlipV);

            // Renderer: mesh mode with mesh+material guids and custom vertex streams.
            UnityParticleRendererData r = Assert.Single(data.Renderers);
            Assert.Equal(4, r.RenderMode);
            Assert.Equal("0123456789abcdef0123456789abcdef", r.MeshGuid);
            Assert.Single(r.MaterialGuids);
            Assert.Equal(2f, r.LengthScale, 3);
            Assert.Equal(1.5f, r.VelocityScale, 3);
            Assert.Equal(10f, r.SortingFudge, 3);
            Assert.Equal(2, r.SortMode);
            Assert.Equal(4, r.Alignment);
            Assert.Equal("0001030420", r.VertexStreams);
            Assert.True(r.UseCustomVertexStreams);
            Assert.True(r.VertexStreamsValid);
            Assert.Equal(new byte[] { 0, 1, 3, 4, 32 }, r.VertexStreamIds);

            Assert.Equal(0, data.MonoBehaviours);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
