using XEngine.Echo;
using XEngine.RpgVfx.Editor;
using XEngine.Runtime;
using XEngine.Runtime.ParticleSystem;
using XEngine.Runtime.ParticleSystem.Modules;
using XEngine.UnityImporter.Editor;
using XEngine.Vector;

using Xunit;

namespace XEngine.Editor.Test;

public sealed class HovlPrefabConverterTests
{
    [Fact]
    public void ModernSerializedFields_PreserveShapeAndThreeDimensionalInitialValues()
    {
        using GameObject root = Convert("""
  moveWithTransform: 0
  InitialModule:
    size3D: 1
    rotation3D: 1
    startSize: {minMaxState: 0, scalar: 2}
    startSizeY: {minMaxState: 0, scalar: 3}
    startSizeZ: {minMaxState: 0, scalar: 4}
    startRotationX: {minMaxState: 0, scalar: 0.25}
    startRotationY: {minMaxState: 0, scalar: 0.5}
    startRotation: {minMaxState: 0, scalar: 0.75}
    gravityModifier: {minMaxState: 0, scalar: 0.4}
  ShapeModule:
    enabled: 1
    type: 4
    radius: {value: 2.5, mode: 0}
    radiusThickness: 0.2
    arc: {value: 180, mode: 0}
    m_Position: {x: 1, y: 2, z: 3}
    m_Rotation: {x: 10, y: 20, z: 30}
    m_Scale: {x: 4, y: 5, z: 6}
""");
        ParticleSystemComponent ps = SystemOf(root);
        Assert.Equal(SimulationSpace.World, ps.SimulationSpace);
        Assert.True(ps.Initial.SeparateSizeAxes);
        Assert.Equal(3f, ps.Initial.StartSizeY.EvaluateInitial(null));
        Assert.Equal(4f, ps.Initial.StartSizeZ.EvaluateInitial(null));
        Assert.True(ps.Initial.SeparateRotationAxes);
        Assert.Equal(0.25f, ps.Initial.StartRotationX.EvaluateInitial(null));
        Assert.Equal(0.5f, ps.Initial.StartRotationY.EvaluateInitial(null));
        Assert.Equal(0.75f, ps.Initial.StartRotation.EvaluateInitial(null));
        Assert.Equal(0.4f, ps.Initial.GravityModifier);
        Assert.Equal(2.5f, ps.Emission.Radius);
        Assert.Equal(0.2f, ps.Emission.RadiusThickness);
        Assert.Equal(Float3.One, ps.Emission.BoxSize);
        Assert.Equal(new Float3(1, 2, 3), ps.Emission.ShapePosition);
        Assert.Equal(new Float3(10, 20, 30), ps.Emission.ShapeRotation);
        Assert.Equal(new Float3(4, 5, 6), ps.Emission.ShapeScale);
        Assert.True(ps.Emission.ShapeEnabled);
        Assert.True(ps.Emission.ShapeUsesZAxis);
        Assert.Equal(Float3.UnitZ, ps.Emission.ShapeForward);
    }

    [Fact]
    public void CurveMultiplierAndAuthoredSlopes_SurviveEchoRoundtrip()
    {
        using GameObject root = Convert("""
  InitialModule:
    startSpeed:
      minMaxState: 1
      scalar: 3
      maxCurve:
        m_Curve:
        - {time: 0, value: 0, inSlope: 0, outSlope: 8}
        - {time: 0.25, value: 2, inSlope: 8, outSlope: 0}
        - {time: 1, value: 0, inSlope: 0, outSlope: 0}
    startSize:
      minMaxState: 2
      scalar: 4
      minCurve:
        m_Curve:
        - {time: 0, value: 2, inSlope: 0, outSlope: 0}
      maxCurve:
        m_Curve:
        - {time: 0, value: 3, inSlope: 0, outSlope: 0}
  SizeModule:
    enabled: 1
    curve: {minMaxState: 0, scalar: 2}
  VelocityModule:
    enabled: 1
    x: {minMaxState: 0, scalar: 3}
    y: {minMaxState: 0, scalar: -2}
    z: {minMaxState: 0, scalar: 0}
""");
        ParticleSystemComponent ps = SystemOf(root);
        Assert.Equal(3f, ps.Initial.StartSpeed.Evaluate(0.125f, null), 5);
        Assert.Equal(6f, ps.Initial.StartSpeed.Evaluate(0.25f, null), 5);
        Assert.Equal(8f, ps.Initial.StartSize.MinValue);
        Assert.Equal(12f, ps.Initial.StartSize.MaxValue);
        Assert.Equal(8f, ps.Initial.StartSize.Evaluate(0.5f, null));
        Assert.Equal(2f, ps.SizeOverLifetime.SizeCurve.Evaluate(0.7f));
        Assert.Equal(3f, ps.VelocityOverLifetime.VelocityX.Evaluate(0.7f));
        Assert.Equal(-2f, ps.VelocityOverLifetime.VelocityY.Evaluate(0.7f));
    }

    [Fact]
    public void Gradient_UsesUnsignedShortTimesAndTheRuntimeSingleGradientField()
    {
        using GameObject root = Convert("""
  InitialModule:
    startColor:
      minMaxState: 1
      maxGradient:
        key0: {r: 1, g: 0, b: 0, a: 1}
        key1: {r: 0, g: 0, b: 1, a: 0}
        ctime0: 0
        ctime1: 65535
        atime0: 0
        atime1: 65535
        m_NumColorKeys: 2
        m_NumAlphaKeys: 2
  ColorModule:
    enabled: 1
    color:
      minMaxState: 0
      maxColor: {r: 0.25, g: 0.5, b: 0.75, a: 0.5}
""");
        ParticleSystemComponent ps = SystemOf(root);
        Assert.Equal(Color.Red, ps.Initial.StartColor.EvaluateInitial(null));
        Color middle = ps.Initial.StartColor.Evaluate(0.5f, null);
        Assert.Equal(0.5f, middle.R, 5);
        Assert.Equal(0.5f, middle.B, 5);
        Assert.Equal(0.5f, middle.A, 5);
        Assert.Equal(0f, ps.Initial.StartColor.Evaluate(1f, null).A);
        Assert.Equal(0.5f, ps.ColorOverLifetime.ColorGradient.Evaluate(0.5f).A);
    }

    [Fact]
    public void ColorLifetime_RealMagicCircleGradient_PreservesFadeInHoldAndFadeOut()
    {
        // Trimmed from Magic circles/Prefabs/Loop version/Magic circle 1 loop.prefab,
        // root ParticleSystem 198149610021991344, ColorModule.gradient.
        using GameObject root = Convert("""
  ColorModule:
    enabled: 1
    gradient:
      serializedVersion: 2
      minMaxState: 1
      maxGradient:
        key0: {r: 1, g: 1, b: 1, a: 0}
        key1: {r: 1, g: 1, b: 1, a: 1}
        key2: {r: 0, g: 0, b: 0, a: 1}
        key3: {r: 0, g: 0, b: 0, a: 0}
        ctime0: 0
        ctime1: 65535
        atime0: 0
        atime1: 3277
        atime2: 58982
        atime3: 65535
        m_NumColorKeys: 2
        m_NumAlphaKeys: 4
""");
        var color = SystemOf(root).ColorOverLifetime;
        Assert.True(color.Enabled);
        Assert.Equal(0f, color.ColorGradient.Evaluate(0f).A);
        Assert.Equal(1f, color.ColorGradient.Evaluate(3277f / 65535f).A, 5);
        Assert.Equal(1f, color.ColorGradient.Evaluate(0.5f).A, 5);
        Assert.Equal(0f, color.ColorGradient.Evaluate(1f).A);
    }

    [Fact]
    public void VelocityModule_PreservesRandomLocalOrbitOffsetsAndRadialCurve()
    {
        using GameObject root = Convert("""
  VelocityModule:
    enabled: 1
    x: {minMaxState: 0, scalar: 0}
    y: {minMaxState: 0, scalar: 0}
    z: {minMaxState: 3, minScalar: 2, scalar: 3}
    orbitalZ: {minMaxState: 3, minScalar: -3, scalar: -6}
    orbitalOffsetX: {minMaxState: 3, minScalar: -0.3, scalar: 0.3}
    orbitalOffsetY: {minMaxState: 3, minScalar: -0.3, scalar: 0.3}
    orbitalOffsetZ: {minMaxState: 3, minScalar: -0.3, scalar: 0.3}
    radial:
      minMaxState: 1
      scalar: 2
      maxCurve:
        m_Curve:
        - {time: 0, value: -1, inSlope: 1, outSlope: 1}
        - {time: 1, value: 0, inSlope: 1, outSlope: 1}
    speedModifier: {minMaxState: 0, scalar: 1.5}
    inWorldSpace: 0
""");
        var velocity = SystemOf(root).VelocityOverLifetime;
        Assert.True(velocity.UseMinMaxCurves);
        Assert.Equal(SimulationSpace.Local, velocity.Space);
        Assert.Equal(2f, velocity.LinearZ.MinCurve.Evaluate(0.5f));
        Assert.Equal(3f, velocity.LinearZ.MaxCurve.Evaluate(0.5f));
        Assert.Equal(-3f, velocity.OrbitalZ.MinCurve.Evaluate(0.5f));
        Assert.Equal(-6f, velocity.OrbitalZ.MaxCurve.Evaluate(0.5f));
        Assert.Equal(-0.3f, velocity.OrbitalOffsetX.MinCurve.Evaluate(0.5f));
        Assert.Equal(0.3f, velocity.OrbitalOffsetY.MaxCurve.Evaluate(0.5f));
        Assert.Equal(0.3f, velocity.OrbitalOffsetZ.MaxCurve.Evaluate(0.5f));
        Assert.Equal(-1f, velocity.Radial.Evaluate(0.5f, null), 5);
        Assert.Equal(1.5f, velocity.SpeedModifier.Evaluate(0.5f, null));
    }

    [Fact]
    public void BurstTimeRendererAndSubEmitterReferences_PreserveSourceSemantics()
    {
        using GameObject root = Convert("""
  lengthInSec: 4
  ShapeModule: {enabled: 0}
  EmissionModule:
    enabled: 1
    rateOverDistance: {minMaxState: 0, scalar: 15}
    m_Bursts:
    - time: 2
      countCurve: {minMaxState: 3, scalar: 5, minScalar: 2}
  SubModule:
    enabled: 1
    subEmitters:
      m_SubEmitters:
      - emitter: {fileID: 1982}
        type: 1
        properties: 11
        emitProbability: 0.75
""", """
  m_Enabled: 1
  m_RenderMode: 1
  m_LengthScale: 2.5
  m_VelocityScale: 0.2
""");
        ParticleSystemComponent ps = SystemOf(root);
        Assert.False(ps.Emission.ShapeEnabled);
        Assert.Equal(15f, ps.Emission.RateOverDistance.Evaluate(0.5f, null));
        Assert.Equal(2f, Assert.Single(ps.Emission.Bursts).Time);
        Assert.Equal(2, Assert.Single(ps.Emission.Bursts).MinCount);
        Assert.Equal(5, Assert.Single(ps.Emission.Bursts).MaxCount);
        Assert.Equal(2.5f, ps.LengthScale);
        Assert.Equal(0.2f, ps.VelocityScale);
        SubEmitterSetting sub = Assert.Single(ps.SubEmitters.Emitters);
        Assert.Equal("Child", sub.TargetPath);
        Assert.Equal(SubEmitterTrigger.Death, sub.Trigger);
        Assert.True(sub.UseTargetEmission);
        Assert.True(sub.InheritColor);
        Assert.True(sub.InheritSize);
        Assert.True(sub.InheritLifetime);
        Assert.Equal(0.75f, sub.Probability);
        Assert.False(SystemOf(Assert.Single(root.Children)).PlayOnEnable);
        Assert.False(Assert.Single(root.Children).Enabled);
    }

    [Fact]
    public void DisabledRenderer_RemainsSimulationOnly()
    {
        using GameObject root = Convert("", "  m_Enabled: 0\n  m_RenderMode: 4");
        Assert.Equal(ParticleRenderMode.None, SystemOf(root).RenderMode);
    }

    [Fact]
    public void ShieldOneParticles_KeepsCircleRadiusRingThicknessAndRandomArc()
    {
        using GameObject root = Convert("""
  ShapeModule:
    enabled: 1
    type: 10
    radiusThickness: 0
    radius: {value: 0.7, mode: 0, spread: 0}
    arc: {value: 260, mode: 0, spread: 0}
""");
        var emission = SystemOf(root).Emission;
        Assert.True(emission.ShapeEnabled);
        Assert.Equal(EmissionShape.Circle, emission.Shape);
        Assert.Equal(0.7f, emission.Radius);
        Assert.Equal(0f, emission.RadiusThickness);
        Assert.Equal(260f, emission.Arc);
    }

    [Fact]
    public void ForceModule_RetainsRealLocalRandomFireAndWorldGlowAcceleration()
    {
        using GameObject root = Convert("""
  ForceModule:
    enabled: 1
    x: {minMaxState: 0, scalar: 0}
    y: {minMaxState: 0, scalar: 3}
    z: {minMaxState: 3, scalar: 6, minScalar: 1}
    inWorldSpace: 1
    randomizePerFrame: 1
""", "  m_Enabled: 1");
        var force = SystemOf(root).ForceOverLifetime;
        Assert.True(force.Enabled);
        Assert.Equal(SimulationSpace.World, force.Space);
        Assert.True(force.RandomizeEachFrame);
        Assert.Equal(3f, force.ForceY.Evaluate(0.5f, null));
        Assert.Equal(MinMaxCurveMode.Random, force.ForceZ.Mode);
        Assert.Equal(1f, force.ForceZ.MinCurve.Evaluate(0.5f));
        Assert.Equal(6f, force.ForceZ.MaxCurve.Evaluate(0.5f));
    }

    [Fact]
    public void TextureSheet_RetainsFrameCurveStartRangeAndGridOptions()
    {
        using GameObject root = Convert("""
  UVModule:
    enabled: 1
    mode: 0
    tilesX: 3
    tilesY: 2
    cycles: 4
    animationType: 1
    rowMode: 0
    rowIndex: 1
    frameOverTime:
      minMaxState: 1
      scalar: 0.9999
      maxCurve:
        m_Curve:
        - {time: 0, value: 1, inSlope: 0, outSlope: 0}
        - {time: 1, value: 1, inSlope: 0, outSlope: 0}
    startFrame: {minMaxState: 3, scalar: 0.9999, minScalar: 0}
""", "  m_Enabled: 1");
        var uv = SystemOf(root).UV;
        Assert.True(uv.Enabled);
        Assert.True(uv.UseMinMaxCurves);
        Assert.True(uv.TopRowFirst);
        Assert.Equal(3, uv.TilesX);
        Assert.Equal(2, uv.TilesY);
        Assert.Equal(4, uv.CycleCount);
        Assert.True(uv.SingleRow);
        Assert.False(uv.RandomRow);
        Assert.Equal(1, uv.RowIndex);
        Assert.Equal(0.9999f, uv.FrameOverTimeCurve.Evaluate(0.5f, null));
        Assert.Equal(MinMaxCurveMode.Random, uv.StartFrameCurve.Mode);
        Assert.Equal(0f, uv.StartFrameCurve.MinCurve.Evaluate(0.5f));
        Assert.Equal(0.9999f, uv.StartFrameCurve.MaxCurve.Evaluate(0.5f));
    }

    [Fact]
    public void NoiseModule_RetainsSubGlowSpatialNoiseAndPerAxisCurveSettings()
    {
        using GameObject root = Convert("""
  NoiseModule:
    enabled: 1
    strength: {minMaxState: 0, scalar: 0.3}
    strengthY: {minMaxState: 3, scalar: 1, minScalar: 0.2}
    strengthZ: {minMaxState: 0, scalar: 0.4}
    separateAxes: 1
    frequency: 1
    damping: 1
    octaves: 2
    octaveMultiplier: 0.5
    octaveScale: 2
    quality: 2
    scrollSpeed: {minMaxState: 0, scalar: 0.1}
    remapEnabled: 1
    remap: {minMaxState: 0, scalar: 0.25}
    remapY: {minMaxState: 0, scalar: 0.5}
    remapZ: {minMaxState: 0, scalar: 0.75}
    positionAmount: {minMaxState: 0, scalar: 1}
    rotationAmount: {minMaxState: 0, scalar: 0.2}
    sizeAmount: {minMaxState: 0, scalar: 0.3}
""", "  m_Enabled: 1");
        var noise = SystemOf(root).Noise;
        Assert.True(noise.Enabled);
        Assert.True(noise.UseSpatialNoise);
        Assert.True(noise.SeparateAxes);
        Assert.Equal(0.3f, noise.StrengthCurve.Evaluate(0.5f, null));
        Assert.Equal(MinMaxCurveMode.Random, noise.StrengthYCurve.Mode);
        Assert.Equal(0.2f, noise.StrengthYCurve.MinCurve.Evaluate(0.5f));
        Assert.Equal(0.4f, noise.StrengthZCurve.Evaluate(0.5f, null));
        Assert.Equal(1f, noise.Frequency);
        Assert.True(noise.Damping);
        Assert.Equal(2, noise.Octaves);
        Assert.Equal(0.5f, noise.OctaveMultiplier);
        Assert.Equal(2f, noise.OctaveScale);
        Assert.Equal(ParticleNoiseQuality.High, noise.Quality);
        Assert.Equal(0.1f, noise.ScrollSpeed.Evaluate(0.5f, null));
        Assert.True(noise.RemapEnabled);
        Assert.Equal(0.25f, noise.Remap.Evaluate(0.5f, null));
        Assert.Equal(0.5f, noise.RemapY.Evaluate(0.5f, null));
        Assert.Equal(0.75f, noise.RemapZ.Evaluate(0.5f, null));
        Assert.Equal(1f, noise.PositionAmount.Evaluate(0.5f, null));
        Assert.Equal(0.2f, noise.RotationAmount.Evaluate(0.5f, null));
        Assert.Equal(0.3f, noise.SizeAmount.Evaluate(0.5f, null));
    }

    [Theory]
    [InlineData(3, 0, 0.5f, 5f)] // Magic shield 1 / Particles
    [InlineData(1, 3, 0.3f, 0.1f)] // Magic circle 1 / Fire
    public void RepeatedBurst_RealShieldAndFireKeepSourceCountsTimingAndLifetime(
        int count, int lifetimeMode, float lifetimeMax, float lifetimeMin)
    {
        using GameObject root = Convert(FormattableString.Invariant($$"""
  lengthInSec: 6
  looping: 1
  startDelay: {minMaxState: 0, scalar: 0}
  InitialModule:
    startLifetime: {minMaxState: {{lifetimeMode}}, scalar: {{lifetimeMax}}, minScalar: {{lifetimeMin}}}
    maxNumParticles: 1000
  EmissionModule:
    enabled: 1
    rateOverTime: {minMaxState: 0, scalar: 0}
    rateOverDistance: {minMaxState: 0, scalar: 0}
    m_Bursts:
    - time: 0
      countCurve: {minMaxState: 0, scalar: {{count}}}
      cycleCount: 400
      repeatInterval: 0.01
"""));
        var system = SystemOf(root);
        Assert.Equal(6f, system.Duration);
        Assert.True(system.Looping);
        Assert.Equal(0f, system.StartDelay);
        Assert.Equal(1000, system.MaxParticles);
        Assert.True(system.Emission.UseUnityBurstScheduling);
        Assert.Equal(0f, system.Emission.RateOverTime.Evaluate(0.5f, null));
        Assert.Equal(0f, system.Emission.RateOverDistance.Evaluate(0.5f, null));
        var burst = Assert.Single(system.Emission.Bursts);
        Assert.Equal(0f, burst.Time);
        Assert.Equal(count, burst.MinCount);
        Assert.Equal(count, burst.MaxCount);
        Assert.Equal(400, burst.CycleCount);
        Assert.Equal(0.01f, burst.RepeatInterval);
        if (lifetimeMode == 0)
            Assert.Equal(lifetimeMax, system.Initial.StartLifetime.ConstantValue);
        else
        {
            Assert.Equal(MinMaxCurveMode.Random, system.Initial.StartLifetime.Mode);
            Assert.Equal(lifetimeMin, system.Initial.StartLifetime.MinValue);
            Assert.Equal(lifetimeMax, system.Initial.StartLifetime.MaxValue);
        }
    }

    [Fact]
    public void TrailMaterial_EmptyParticleSlotStaysEmptyAndSecondSlotRemainsDedicated()
    {
        const string sourceGuid = "60c4f99d55be7ee44a8521d1da5a59c6";
        const string packageGuid = "935b4b08-854a-484b-9066-fbc922f409e0";
        var warnings = new List<string>();
        using GameObject root = Convert("""
  TrailModule:
    enabled: 1
    ratio: 1
""", """
  m_Enabled: 1
  m_RenderMode: 5
  m_Materials:
  - {fileID: 0}
  - {fileID: 2100000, guid: 60c4f99d55be7ee44a8521d1da5a59c6, type: 2}
""", warnings, new Dictionary<string, string> { [sourceGuid] = packageGuid });
        ParticleSystemComponent system = SystemOf(root);
        Assert.True(system.Material.IsExplicitNull);
        Assert.Equal(ParticleRenderMode.None, system.RenderMode);
        Assert.True(system.Trails.Enabled);
        Assert.Equal(Guid.Parse(packageGuid), system.Trails.TrailMaterial!.Value.AssetID);
        Assert.DoesNotContain(warnings, static warning => warning.Contains("material"));
    }

    [Theory]
    [InlineData("0001030420")]
    [InlineData("000103040220")]
    public void Custom1Vector_PreservesAllFourCurvesAndAuthoredUvPacking(string streams)
    {
        using GameObject root = Convert("""
  CustomDataModule:
    enabled: 1
    mode0: 1
    mode1: 0
    vectorComponentCount0: 4
    vector0_0:
      minMaxState: 1
      scalar: 3
      maxCurve:
        m_Curve:
        - {time: 0, value: 0, inSlope: 0, outSlope: 15.397044}
        - {time: 0.1, value: 1, inSlope: 0, outSlope: 0}
        - {time: 1, value: 1, inSlope: 0, outSlope: 0}
    vector0_1: {minMaxState: 0, scalar: 0.25}
    vector0_2: {minMaxState: 3, minScalar: 1, scalar: 2}
    vector0_3:
      minMaxState: 2
      scalar: 2
      minCurve:
        m_Curve:
        - {time: 0, value: 1, inSlope: 0, outSlope: 0}
      maxCurve:
        m_Curve:
        - {time: 0, value: 3, inSlope: 0, outSlope: 0}
""", "  m_UseCustomVertexStreams: 1\n  m_VertexStreams: " + streams);
        var custom = SystemOf(root).CustomData;
        Assert.True(custom.Enabled);
        Assert.True(custom.Custom1ToUV0ZW);
        Assert.Equal(4, custom.Custom1ComponentCount);
        Assert.Equal(3f, custom.Custom1X.Evaluate(0.1f, null), 5);
        Assert.Equal(0.25f, custom.Custom1Y.Evaluate(0.1f, null));
        Assert.Equal(1f, custom.Custom1Z.MinCurve.Evaluate(0.1f));
        Assert.Equal(2f, custom.Custom1Z.MaxCurve.Evaluate(0.1f));
        Assert.Equal(2f, custom.Custom1W.MinCurve.Evaluate(0.1f));
        Assert.Equal(6f, custom.Custom1W.MaxCurve.Evaluate(0.1f));
    }

    [Theory]
    [InlineData("000103040520")]
    [InlineData("000103042g")]
    [InlineData("000103042")]
    public void UnsupportedCustomPacking_IsNotMisboundAndEmitsWarning(string streams)
    {
        var warnings = new List<string>();
        using GameObject root = Convert("""
  CustomDataModule:
    enabled: 1
    mode0: 1
    vectorComponentCount0: 2
    vector0_0: {minMaxState: 0, scalar: 3}
""", "  m_UseCustomVertexStreams: 1\n  m_VertexStreams: " + streams, warnings);
        Assert.True(SystemOf(root).CustomData.Enabled);
        Assert.False(SystemOf(root).CustomData.Custom1ToUV0ZW);
        Assert.Contains(warnings, static warning => warning.Contains("custom vertex stream schema"));
    }

    [Fact]
    public void UnsupportedCustomColorAndSecondStream_AreReported()
    {
        var warnings = new List<string>();
        using GameObject root = Convert("""
  CustomDataModule:
    enabled: 1
    mode0: 2
    mode1: 1
""", conversionWarnings: warnings);
        Assert.False(SystemOf(root).CustomData.Enabled);
        Assert.Contains(warnings, static warning => warning.Contains("Custom1 mode 2"));
        Assert.Contains(warnings, static warning => warning.Contains("Custom2 mode 1"));
    }

    private static ParticleSystemComponent SystemOf(GameObject root) =>
        Assert.Single(root.GetComponents<ParticleSystemComponent>());

    private static GameObject Convert(string systemYaml, string rendererYaml = "  m_RenderMode: 0",
        List<string>? conversionWarnings = null, Dictionary<string, string>? materialMap = null)
    {
        string temp = Path.Combine(Path.GetTempPath(), "hovl-converter-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        string source = Path.Combine(temp, "source.prefab");
        string output = Path.Combine(temp, "converted.prefab");
        File.WriteAllText(source, """
--- !u!1 &1
GameObject:
  m_Name: Root
--- !u!4 &10
Transform:
  m_GameObject: {fileID: 1}
  m_LocalPosition: {x: 1, y: 2, z: 3}
  m_LocalRotation: {x: 0, y: 0, z: 0, w: 1}
  m_LocalScale: {x: 1, y: 1, z: 1}
  m_Father: {fileID: 0}
--- !u!1 &2
GameObject:
  m_Name: Child
  m_IsActive: 0
--- !u!4 &20
Transform:
  m_GameObject: {fileID: 2}
  m_LocalPosition: {x: 0, y: 0, z: 0}
  m_LocalRotation: {x: 0, y: 0, z: 0, w: 1}
  m_LocalScale: {x: 1, y: 1, z: 1}
  m_Father: {fileID: 10}
--- !u!198 &1982
ParticleSystem:
  m_GameObject: {fileID: 2}
--- !u!198 &1981
ParticleSystem:
  m_GameObject: {fileID: 1}
""" + "\n" + systemYaml + "\n--- !u!199 &1991\nParticleSystemRenderer:\n  m_GameObject: {fileID: 1}\n" + rendererYaml + "\n");
        try
        {
            var warnings = conversionWarnings ?? new List<string>();
            Assert.True(HovlPrefabConverter.Convert(source, output, materialMap ?? new(),
                new DependencySerializationContext(), warnings));
            return Serializer.Deserialize<GameObject>(EchoObject.ReadFromString(File.ReadAllText(output)))!;
        }
        finally
        {
            File.Delete(source);
            if (File.Exists(output)) File.Delete(output);
            Directory.Delete(temp);
        }
    }
}
