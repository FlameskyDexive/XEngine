using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace XEngine.UnityImporter.Editor;

// ---------------------------------------------------------------------------
// Intermediate model: everything Hovl Studio / Shuriken prefabs use, captured
// as plain data so the importer can map it onto XEngine's ParticleSystemComponent
// (and so golden tests can assert on parsed values without any engine types).
// Field semantics follow Unity's serialized ParticleSystem (class 198),
// ParticleSystemRenderer (199) and LineRenderer (120) YAML documents.
// ---------------------------------------------------------------------------

/// <summary>One keyframe of a Unity serialized AnimationCurve.</summary>
public sealed class UnityKeyframe
{
    public float Time;
    public float Value;
    public float InSlope;
    public float OutSlope;
    public int TangentMode;
    public int WeightedMode;
    public float InWeight = 1f / 3f;
    public float OutWeight = 1f / 3f;
}

/// <summary>Unity MinMaxCurve: minMaxState 0=scalar, 1=curve, 2=two curves, 3=two constants.</summary>
public sealed class UnityMinMaxCurve
{
    public int State;
    public float Scalar;
    public float MinScalar;
    public UnityKeyframe[] MaxCurve = Array.Empty<UnityKeyframe>();
    public UnityKeyframe[] MinCurve = Array.Empty<UnityKeyframe>();
    public float Multiplier = 1f; // *_multiplier companions (frameOverTimeMultiplier etc.)

    public bool IsConstant => State == 0;
}

/// <summary>One gradient key (color+alpha merged by time when converting; kept raw here).</summary>
public sealed class UnityGradientKey
{
    public float Time;
    public float R = 1f, G = 1f, B = 1f, A = 1f;
}

/// <summary>Unity serialized Gradient: m_Key0..m_Key7 colors + ctime/atime arrays.</summary>
public sealed class UnityGradient
{
    public UnityGradientKey[] ColorKeys = Array.Empty<UnityGradientKey>();
    public UnityGradientKey[] AlphaKeys = Array.Empty<UnityGradientKey>();
}

/// <summary>Unity MinMaxGradient: 0=color, 1=gradient, 2=two colors, 3=two gradients,
/// 4=rand colors?, 5=rand gradients? (raw state kept; importer maps what the engine has).</summary>
public sealed class UnityMinMaxGradient
{
    public int State;
    public float[] MaxColor = { 1f, 1f, 1f, 1f };
    public float[] MinColor = { 1f, 1f, 1f, 1f };
    public UnityGradient? MaxGradient;
    public UnityGradient? MinGradient;
}

/// <summary>ShapeModule (legacy serialized enum kept raw + named mapping helper).</summary>
public sealed class UnityShapeData
{
    public bool Enabled;
    public int Type;
    public float Radius = 1f;
    public float RadiusThickness = 1f;
    public float Angle = 25f;
    public float Length = 5f;
    public float Arc = 360f;
    public float[] Box = { 1f, 1f, 1f }; // modern boxes use a unit box transformed by m_Scale
    public float[] Position = { 0, 0, 0 };
    public float[] Rotation = { 0, 0, 0 };
    public float[] Scale = { 1, 1, 1 };
    public int PlacementMode; // 0 volume, 1 edge, 2 surface
    public string MeshGuid = string.Empty;
    public long MeshFileId;
    public float[] MeshScale = { 1, 1, 1 };
    public float RandomDirectionAmount;
    public float SphericalDirectionAmount;
    public bool AlignToDirection;
}

public sealed class UnityBurstData
{
    public float Time;
    public UnityMinMaxCurve Count = new() { State = 0, Scalar = 0 };
    public int CycleCount = 1;
    public float RepeatInterval;
    public float Probability = 1f;
}

public sealed class UnityEmissionData
{
    public bool Enabled;
    public UnityMinMaxCurve RateOverTime = new() { State = 0, Scalar = 10 };
    public UnityMinMaxCurve RateOverDistance = new() { State = 0, Scalar = 0 };
    public List<UnityBurstData> Bursts = new();
}

public sealed class UnityInitialData
{
    public bool Enabled = true;
    public UnityMinMaxCurve StartLifetime = new() { State = 0, Scalar = 5 };
    public UnityMinMaxCurve StartSpeed = new() { State = 0, Scalar = 5 };
    public bool StartSize3D;
    public UnityMinMaxCurve StartSize = new() { State = 0, Scalar = 1 };
    public UnityMinMaxCurve StartSizeY = new() { State = 0, Scalar = 1 };
    public UnityMinMaxCurve StartSizeZ = new() { State = 0, Scalar = 1 };
    public bool StartRotation3D;
    public UnityMinMaxCurve StartRotation = new() { State = 0, Scalar = 0 };
    public UnityMinMaxCurve StartRotationX = new() { State = 0, Scalar = 0 };
    public UnityMinMaxCurve StartRotationY = new() { State = 0, Scalar = 0 };
    public UnityMinMaxCurve StartRotationZ = new() { State = 0, Scalar = 0 };
    public float RandomizeRotationDirection;
    public UnityMinMaxGradient StartColor = new();
    public float GravityModifier;
    public int MaxParticles = 1000;
}

public sealed class UnitySubEmitterData
{
    public long EmitterFileId; // ParticleSystem fileID inside this prefab
    public int Type;           // 0 birth, 1 death, 2 collision
    public int Properties;     // raw flags (inheritance etc.)
    public float EmitProbability = 1f;
}

public sealed class UnityParticleSystemData
{
    public long FileId;
    public long GameObjectId;
    public bool Enabled = true;
    public float Duration = 5f;
    public float SimulationSpeed = 1f;
    public bool Looping = true;
    public bool Prewarm;
    public bool PlayOnAwake = true;
    public int ScalingMode;
    public bool SimulationInWorldSpace;

    public UnityMinMaxCurve StartDelay = new() { State = 0, Scalar = 0 };

    public UnityInitialData Initial = new();
    public UnityShapeData Shape = new();
    public UnityEmissionData Emission = new();

    public bool SizeEnabled;           // SizeModule
    public UnityMinMaxCurve Size = new() { State = 0, Scalar = 1 };
    public UnityMinMaxCurve SizeY = new() { State = 0, Scalar = 1 };
    public UnityMinMaxCurve SizeZ = new() { State = 0, Scalar = 1 };
    public bool SizeSeparateAxes;

    public bool RotationEnabled;       // RotationModule
    public UnityMinMaxCurve Rotation = new() { State = 0, Scalar = 0 };
    public UnityMinMaxCurve RotationX = new() { State = 0, Scalar = 0 };
    public UnityMinMaxCurve RotationY = new() { State = 0, Scalar = 0 };
    public bool RotationSeparateAxes;

    public bool ColorOverLifetimeEnabled; // ColorModule
    public UnityMinMaxGradient ColorOverLifetime = new();

    public bool VelocityEnabled;       // VelocityModule
    public int VelocitySpace;          // 0 local, 1 world
    public UnityMinMaxCurve VelocityX = new() { State = 0, Scalar = 0 };
    public UnityMinMaxCurve VelocityY = new() { State = 0, Scalar = 0 };
    public UnityMinMaxCurve VelocityZ = new() { State = 0, Scalar = 0 };
    public UnityMinMaxCurve OrbitalX = new() { State = 0, Scalar = 0 };
    public UnityMinMaxCurve OrbitalY = new() { State = 0, Scalar = 0 };
    public UnityMinMaxCurve OrbitalZ = new() { State = 0, Scalar = 0 };
    public UnityMinMaxCurve OrbitalOffsetX = new() { State = 0, Scalar = 0 };
    public UnityMinMaxCurve OrbitalOffsetY = new() { State = 0, Scalar = 0 };
    public UnityMinMaxCurve OrbitalOffsetZ = new() { State = 0, Scalar = 0 };
    public UnityMinMaxCurve Radial = new() { State = 0, Scalar = 0 };
    public UnityMinMaxCurve SpeedModifier = new() { State = 0, Scalar = 1 };

    public bool InheritVelocityEnabled; // InheritVelocityModule
    public int InheritVelocityMode;
    public UnityMinMaxCurve InheritVelocityCurve = new() { State = 0, Scalar = 0 };

    public bool ForceEnabled;          // ForceModule
    public UnityMinMaxCurve ForceX = new() { State = 0, Scalar = 0 };
    public UnityMinMaxCurve ForceY = new() { State = 0, Scalar = 0 };
    public UnityMinMaxCurve ForceZ = new() { State = 0, Scalar = 0 };
    public float ForceRandomized;
    public bool ForceInWorldSpace;

    public bool ExternalForcesEnabled; // ExternalForcesModule
    public float ExternalForcesMultiplier = 1f;

    public bool LimitVelocityEnabled;  // ClampVelocityModule
    public bool LimitSeparateAxes;
    public UnityMinMaxCurve LimitMagnitude = new() { State = 0, Scalar = 1 };
    public UnityMinMaxCurve LimitX = new() { State = 0, Scalar = 1 };
    public UnityMinMaxCurve LimitY = new() { State = 0, Scalar = 1 };
    public UnityMinMaxCurve LimitZ = new() { State = 0, Scalar = 1 };
    public float Dampen = 1f;
    public float Drag;

    public bool NoiseEnabled;          // NoiseModule
    public UnityMinMaxCurve NoiseStrength = new() { State = 0, Scalar = 1 };
    public UnityMinMaxCurve NoiseStrengthY = new() { State = 0, Scalar = 1 };
    public UnityMinMaxCurve NoiseStrengthZ = new() { State = 0, Scalar = 1 };
    public bool NoiseSeparateAxes;
    public UnityMinMaxCurve NoiseFrequency = new() { State = 0, Scalar = 0.5f };
    public int NoiseOctaves = 1;
    public float NoiseOctaveMultiplier = 0.5f;
    public float NoiseOctaveScale = 2f;
    public bool NoiseDamping = true;
    public UnityMinMaxCurve NoiseScrollSpeed = new() { State = 0, Scalar = 0 };
    public bool NoiseRemapEnabled;
    public UnityMinMaxCurve NoiseRemap = new() { State = 0, Scalar = 0 };
    public UnityMinMaxCurve NoiseRemapY = new() { State = 0, Scalar = 0 };
    public UnityMinMaxCurve NoiseRemapZ = new() { State = 0, Scalar = 0 };
    public UnityMinMaxCurve NoisePositionAmount = new() { State = 0, Scalar = 1 };
    public UnityMinMaxCurve NoiseRotationAmount = new() { State = 0, Scalar = 0 };
    public UnityMinMaxCurve NoiseSizeAmount = new() { State = 0, Scalar = 0 };
    public int NoiseQuality = 2;

    public bool SizeBySpeedEnabled;    // SizeBySpeedModule
    public UnityMinMaxCurve SizeBySpeed = new() { State = 0, Scalar = 1 };
    public float SizeBySpeedMin;
    public float SizeBySpeedMax = 1f;

    public bool RotationBySpeedEnabled; // RotationBySpeedModule
    public UnityMinMaxCurve RotationBySpeed = new() { State = 0, Scalar = 1 };
    public float RotationBySpeedMin;
    public float RotationBySpeedMax = 1f;

    public bool ColorBySpeedEnabled;   // ColorBySpeedModule
    public UnityMinMaxGradient ColorBySpeed = new();
    public float ColorBySpeedMin;
    public float ColorBySpeedMax = 1f;

    public bool CollisionEnabled;      // CollisionModule
    public int CollisionType;          // 0 planes, 1 world
    public float CollisionDampen;
    public float CollisionBounce = 0.5f;
    public float CollisionLifetimeLoss;
    public float CollisionMinKillSpeed;
    public float CollisionRadiusScale = 1f;
    public int CollisionQuality;
    public bool CollisionSendMessages;

    public bool TriggerEnabled;        // TriggerModule (Hovl: unused; kept for warnings)

    public bool SubEmittersEnabled;    // SubModule
    public List<UnitySubEmitterData> SubEmitters = new();

    public bool UvEnabled;             // UVModule (texture sheet animation)
    public UnityMinMaxCurve FrameOverTime = new() { State = 1, Scalar = 1 };
    public UnityMinMaxCurve StartFrame = new() { State = 0, Scalar = 0 };
    public int TilesX = 1;
    public int TilesY = 1;
    public int UvAnimationType;        // 0 whole sheet, 1 single row
    public int UvMode;                 // 0 grid, 1 sprite list
    public int UvTimeMode;             // 0 lifetime, 1 speed, 2 FPS
    public int UvRowMode;              // 0 custom, 1 random, 2 mesh index
    public int RowIndex;
    public int Cycles = 1;
    public bool FlipU;
    public bool FlipV;

    public bool LightsEnabled;         // LightsModule
    public float LightRatio = 0.5f;
    public long LightPrefabFileId;     // default light prefab reference (0 = none)
    public UnityMinMaxGradient LightColor = new();
    public UnityMinMaxCurve LightRange = new() { State = 0, Scalar = 1 };
    public UnityMinMaxCurve LightIntensity = new() { State = 0, Scalar = 1 };
    public int MaxLights = 20;

    public bool TrailsEnabled;         // TrailModule
    public int TrailMode;              // 0 per particle, 1 ribbon
    public float TrailRatio = 0.5f;
    public UnityMinMaxCurve TrailLifetime = new() { State = 0, Scalar = 1 };
    public bool TrailDieWithParticles = true;
    public bool TrailInheritParticleColor = true;
    public int TrailTextureMode;
    public UnityMinMaxGradient TrailColorOverLifetime = new();
    public UnityMinMaxCurve TrailWidthOverTrail = new() { State = 0, Scalar = 1 };
    public bool TrailSizeAffectsWidth = true;
    public long TrailMaterialFileId;

    public bool CustomDataEnabled;     // CustomDataModule
    public int CustomDataMode;
    public int NumColorComponents;
    public int NumVectorComponents;
    public int Custom1Mode; // 0 disabled, 1 vector, 2 color
    public int Custom2Mode;
    public int Custom1ComponentCount = 4;
    public int Custom2ComponentCount = 4;
    /// <summary>Raw component curves by serialized key ("vector0_0", "colorComponent1_2", …).</summary>
    public Dictionary<string, UnityMinMaxCurve> CustomDataComponents = new();
}

/// <summary>ParticleSystemRenderer (class 199).</summary>
public sealed class UnityParticleRendererData
{
    public long GameObjectId;
    public bool Enabled = true;
    public int RenderMode;             // 0 billboard, 1 stretch, 2 horizontal, 3 vertical, 4 mesh, 5 none
    public string MeshGuid = string.Empty;
    public long MeshFileId;
    public List<string> MaterialGuids = new();
    public float LengthScale = 1f;
    public float VelocityScale = 1f;
    public float CameraVelocityScale = 1f;
    public float SortingFudge;
    public int SortMode;
    public float NormalDirection = 1f;
    public int Alignment;              // 0 view, 1 world, 2 local, 3 facing, 4 velocity
    public string VertexStreams = string.Empty; // hex, e.g. "00010304"
    public bool UseCustomVertexStreams;
    public byte[] VertexStreamIds = Array.Empty<byte>(); // Unity ParticleSystemVertexStream ordinals
    public bool VertexStreamsValid = true;
    public long TrailMaterialFileId;
}

/// <summary>LineRenderer (class 120) — Hovl lasers. Positions are script-driven and absent.</summary>
public sealed class UnityLineRendererData
{
    public long GameObjectId;
    public bool Enabled = true;
    public int Alignment;
    public int TextureMode;
    public float WidthMultiplier = 1f;
    public UnityMinMaxCurve WidthCurve = new() { State = 1 };
    public UnityGradient? ColorGradient;
    public List<string> MaterialGuids = new();
    public bool UseWorldSpace = true;
    public bool Loop;
    /// <summary>Serialized m_Positions (Hovl ships 0→+Z stubs; scripts drive real endpoints).</summary>
    public List<float[]> Positions = new();
}

/// <summary>All particle-related documents parsed from one Unity prefab, keyed by GameObject.</summary>
public sealed class UnityParticlePrefabData
{
    public List<UnityParticleSystemData> Systems = new();
    public List<UnityParticleRendererData> Renderers = new();
    public List<UnityLineRendererData> LineRenderers = new();
    public int MonoBehaviours; // count of unmapped scripts (business logic — ported by hand)
}

/// <summary>Parses class 198/199/120 documents out of a Unity .prefab YAML file.</summary>
public static class UnityParticleReader
{
    public static UnityParticlePrefabData Read(string path)
    {
        List<UnityYamlDocument> docs = UnityYaml.ParseFile(path);
        var data = new UnityParticlePrefabData();

        foreach (UnityYamlDocument doc in docs)
        {
            switch (doc.ClassId)
            {
                case 198:
                    data.Systems.Add(ReadSystem(doc));
                    break;
                case 199:
                    data.Renderers.Add(ReadRenderer(doc));
                    break;
                case 120:
                    data.LineRenderers.Add(ReadLineRenderer(doc));
                    break;
                case 1001 or 114:
                    data.MonoBehaviours++;
                    break;
            }
        }
        return data;
    }

    // -- class 198 ----------------------------------------------------------

    private static UnityParticleSystemData ReadSystem(UnityYamlDocument doc)
    {
        UnityYamlNode r = doc.Root;
        var s = new UnityParticleSystemData
        {
            FileId = doc.FileId,
            GameObjectId = r["m_GameObject"]?.LongOf("fileID") ?? 0,
            Duration = r.FloatOf("lengthInSec", 5f),
            SimulationSpeed = r.FloatOf("simulationSpeed", 1f),
            Looping = (r.ScalarOf("looping") ?? "1") != "0",
            Prewarm = (r.ScalarOf("prewarm") ?? "0") != "0",
            PlayOnAwake = (r.ScalarOf("playOnAwake") ?? "1") != "0",
            ScalingMode = (int)r.LongOf("scalingMode"),
            SimulationInWorldSpace = (r.ScalarOf("moveWithTransform") ?? "1") == "0",
        };
        s.Enabled = (r["m_Enabled"]?.Scalar ?? "1") != "0";
        ReadMinMaxCurve(r["startDelay"], s.StartDelay);

        UnityYamlNode? initial = r["InitialModule"];
        if (initial != null)
        {
            var i = s.Initial;
            i.Enabled = (initial.ScalarOf("enabled") ?? "1") != "0";
            ReadMinMaxCurve(initial["startLifetime"], i.StartLifetime);
            ReadMinMaxCurve(initial["startSpeed"], i.StartSpeed);
            i.StartSize3D = (initial.ScalarOf("size3D") ?? initial.ScalarOf("startSize3D") ?? "0") != "0";
            ReadMinMaxCurve(initial["startSize"], i.StartSize);
            ReadMinMaxCurve(initial["startSizeY"], i.StartSizeY);
            ReadMinMaxCurve(initial["startSizeZ"], i.StartSizeZ);
            i.StartRotation3D = (initial.ScalarOf("rotation3D") ?? initial.ScalarOf("startRotation3D") ?? "0") != "0";
            ReadMinMaxCurve(initial["startRotation"], i.StartRotation);
            ReadMinMaxCurve(initial["startRotationX"], i.StartRotationX);
            ReadMinMaxCurve(initial["startRotationY"], i.StartRotationY);
            ReadMinMaxCurve(initial["startRotationZ"], i.StartRotationZ);
            i.RandomizeRotationDirection = initial.FloatOf("randomizeRotationDirection");
            ReadMinMaxGradient(initial["startColor"], i.StartColor);
            i.GravityModifier = initial["gravityModifier"]?.Map is not null
                ? initial["gravityModifier"]!.FloatOf("scalar")
                : initial.FloatOf("gravityModifier");
            i.MaxParticles = (int)initial.LongOf("maxNumParticles", 1000);
        }

        ReadShape(r["ShapeModule"], s.Shape);
        ReadEmission(r["EmissionModule"], s.Emission);

        UnityYamlNode? size = r["SizeModule"];
        if (size != null)
        {
            s.SizeEnabled = (size.ScalarOf("enabled") ?? "0") != "0";
            s.SizeSeparateAxes = (size.ScalarOf("separateAxes") ?? "0") != "0";
            ReadMinMaxCurve(size["curve"], s.Size);
            ReadMinMaxCurve(size["y"], s.SizeY);
            ReadMinMaxCurve(size["z"], s.SizeZ);
        }

        UnityYamlNode? rotation = r["RotationModule"];
        if (rotation != null)
        {
            s.RotationEnabled = (rotation.ScalarOf("enabled") ?? "0") != "0";
            s.RotationSeparateAxes = (rotation.ScalarOf("separateAxes") ?? "0") != "0";
            ReadMinMaxCurve(rotation["curve"], s.Rotation);
            ReadMinMaxCurve(rotation["x"], s.RotationX);
            ReadMinMaxCurve(rotation["y"], s.RotationY);
        }

        UnityYamlNode? color = r["ColorModule"];
        if (color != null)
        {
            s.ColorOverLifetimeEnabled = (color.ScalarOf("enabled") ?? "0") != "0";
            ReadMinMaxGradient(color["gradient"] ?? color["color"], s.ColorOverLifetime);
        }

        UnityYamlNode? velocity = r["VelocityModule"];
        if (velocity != null)
        {
            s.VelocityEnabled = (velocity.ScalarOf("enabled") ?? "0") != "0";
            s.VelocitySpace = (int)velocity.LongOf("inWorldSpace");
            ReadMinMaxCurve(velocity["x"], s.VelocityX);
            ReadMinMaxCurve(velocity["y"], s.VelocityY);
            ReadMinMaxCurve(velocity["z"], s.VelocityZ);
            ReadMinMaxCurve(velocity["orbitalX"], s.OrbitalX);
            ReadMinMaxCurve(velocity["orbitalY"], s.OrbitalY);
            ReadMinMaxCurve(velocity["orbitalZ"], s.OrbitalZ);
            ReadMinMaxCurve(velocity["orbitalOffsetX"], s.OrbitalOffsetX);
            ReadMinMaxCurve(velocity["orbitalOffsetY"], s.OrbitalOffsetY);
            ReadMinMaxCurve(velocity["orbitalOffsetZ"], s.OrbitalOffsetZ);
            ReadMinMaxCurve(velocity["radial"], s.Radial);
            ReadMinMaxCurve(velocity["speedModifier"], s.SpeedModifier, readMultiplier: false);
        }

        UnityYamlNode? inherit = r["InheritVelocityModule"];
        if (inherit != null)
        {
            s.InheritVelocityEnabled = (inherit.ScalarOf("enabled") ?? "0") != "0";
            s.InheritVelocityMode = (int)inherit.LongOf("m_Mode", inherit.LongOf("mode"));
            ReadMinMaxCurve(inherit["m_Curve"] ?? inherit["curve"], s.InheritVelocityCurve);
        }

        UnityYamlNode? force = r["ForceModule"];
        if (force != null)
        {
            s.ForceEnabled = (force.ScalarOf("enabled") ?? "0") != "0";
            ReadMinMaxCurve(force["x"], s.ForceX);
            ReadMinMaxCurve(force["y"], s.ForceY);
            ReadMinMaxCurve(force["z"], s.ForceZ);
            s.ForceRandomized = force.FloatOf("randomizePerFrame", force.FloatOf("randomized"));
            s.ForceInWorldSpace = force.LongOf("inWorldSpace") == 1;
        }

        UnityYamlNode? extForces = r["ExternalForcesModule"];
        if (extForces != null)
        {
            s.ExternalForcesEnabled = (extForces.ScalarOf("enabled") ?? "0") != "0";
            s.ExternalForcesMultiplier = extForces.FloatOf("multiplier", 1f);
        }

        UnityYamlNode? clamp = r["ClampVelocityModule"];
        if (clamp != null)
        {
            s.LimitVelocityEnabled = (clamp.ScalarOf("enabled") ?? "0") != "0";
            s.LimitSeparateAxes = (clamp.ScalarOf("separateAxis") ?? clamp.ScalarOf("separateAxes") ?? "0") != "0";
            ReadMinMaxCurve(clamp["magnitude"], s.LimitMagnitude);
            ReadMinMaxCurve(clamp["x"], s.LimitX);
            ReadMinMaxCurve(clamp["y"], s.LimitY);
            ReadMinMaxCurve(clamp["z"], s.LimitZ);
            s.Dampen = clamp.FloatOf("dampen", 1f);
            s.Drag = ScalarOrCurveValue(clamp["drag"]);
        }

        UnityYamlNode? noise = r["NoiseModule"];
        if (noise != null)
        {
            s.NoiseEnabled = (noise.ScalarOf("enabled") ?? "0") != "0";
            s.NoiseSeparateAxes = (noise.ScalarOf("separateAxes") ?? "0") != "0";
            ReadMinMaxCurve(noise["strength"], s.NoiseStrength);
            ReadMinMaxCurve(noise["strengthY"], s.NoiseStrengthY);
            ReadMinMaxCurve(noise["strengthZ"], s.NoiseStrengthZ);
            ReadMinMaxCurve(noise["frequency"], s.NoiseFrequency);
            s.NoiseOctaves = (int)noise.LongOf("octaves", 1);
            s.NoiseOctaveMultiplier = noise.FloatOf("octaveMultiplier", 0.5f);
            s.NoiseOctaveScale = noise.FloatOf("octaveScale", 2f);
            s.NoiseDamping = noise.LongOf("damping", 1) != 0;
            ReadMinMaxCurve(noise["scrollSpeed"], s.NoiseScrollSpeed);
            s.NoiseRemapEnabled = noise.LongOf("remapEnabled") != 0;
            ReadMinMaxCurve(noise["remap"], s.NoiseRemap);
            ReadMinMaxCurve(noise["remapY"], s.NoiseRemapY);
            ReadMinMaxCurve(noise["remapZ"], s.NoiseRemapZ);
            ReadMinMaxCurve(noise["positionAmount"], s.NoisePositionAmount);
            ReadMinMaxCurve(noise["rotationAmount"], s.NoiseRotationAmount);
            ReadMinMaxCurve(noise["sizeAmount"], s.NoiseSizeAmount);
            s.NoiseQuality = (int)noise.LongOf("quality", 2);
        }

        UnityYamlNode? sizeBySpeed = r["SizeBySpeedModule"];
        if (sizeBySpeed != null)
        {
            s.SizeBySpeedEnabled = (sizeBySpeed.ScalarOf("enabled") ?? "0") != "0";
            ReadMinMaxCurve(sizeBySpeed["curve"] ?? sizeBySpeed["size"], s.SizeBySpeed);
            ReadRange(sizeBySpeed["range"], out s.SizeBySpeedMin, out s.SizeBySpeedMax);
        }

        UnityYamlNode? rotBySpeed = r["RotationBySpeedModule"];
        if (rotBySpeed != null)
        {
            s.RotationBySpeedEnabled = (rotBySpeed.ScalarOf("enabled") ?? "0") != "0";
            ReadMinMaxCurve(rotBySpeed["curve"], s.RotationBySpeed);
            ReadRange(rotBySpeed["range"], out s.RotationBySpeedMin, out s.RotationBySpeedMax);
        }

        UnityYamlNode? colorBySpeed = r["ColorBySpeedModule"];
        if (colorBySpeed != null)
        {
            s.ColorBySpeedEnabled = (colorBySpeed.ScalarOf("enabled") ?? "0") != "0";
            ReadMinMaxGradient(colorBySpeed["gradient"] ?? colorBySpeed["color"], s.ColorBySpeed);
            ReadRange(colorBySpeed["range"], out s.ColorBySpeedMin, out s.ColorBySpeedMax);
        }

        UnityYamlNode? collision = r["CollisionModule"];
        if (collision != null)
        {
            s.CollisionEnabled = (collision.ScalarOf("enabled") ?? "0") != "0";
            s.CollisionType = (int)collision.LongOf("type");
            s.CollisionDampen = ScalarOrCurveValue(collision["m_Dampen"] ?? collision["dampen"]);
            s.CollisionBounce = ScalarOrCurveValue(collision["m_Bounce"] ?? collision["bounce"], 0.5f);
            s.CollisionLifetimeLoss = ScalarOrCurveValue(collision["m_EnergyLossOnCollision"] ?? collision["lifetimeLoss"]);
            s.CollisionMinKillSpeed = collision.FloatOf("minKillSpeed");
            s.CollisionRadiusScale = collision.FloatOf("radiusScale", 1f);
            s.CollisionQuality = (int)collision.LongOf("quality");
            s.CollisionSendMessages = (collision.ScalarOf("collisionMessages") ?? "0") != "0";
        }

        UnityYamlNode? trigger = r["TriggerModule"];
        if (trigger != null)
            s.TriggerEnabled = (trigger.ScalarOf("enabled") ?? "0") != "0";

        UnityYamlNode? sub = r["SubModule"];
        if (sub != null)
        {
            s.SubEmittersEnabled = (sub.ScalarOf("enabled") ?? "0") != "0";
            foreach (UnityYamlNode node in sub["subEmitters"]?["m_SubEmitters"]?.List
                     ?? sub["subEmitters"]?.List
                     ?? new List<UnityYamlNode>())
            {
                s.SubEmitters.Add(new UnitySubEmitterData
                {
                    EmitterFileId = node["emitter"]?.LongOf("fileID") ?? 0,
                    Type = (int)node.LongOf("type"),
                    Properties = (int)node.LongOf("properties"),
                    EmitProbability = node.FloatOf("emitProbability", 1f),
                });
            }
        }

        UnityYamlNode? uv = r["UVModule"];
        if (uv != null)
        {
            s.UvEnabled = (uv.ScalarOf("enabled") ?? "0") != "0";
            s.UvMode = (int)uv.LongOf("mode");
            s.UvTimeMode = (int)uv.LongOf("timeMode");
            s.UvRowMode = (int)uv.LongOf("rowMode", uv.LongOf("randomRow", 1));
            ReadMinMaxCurve(uv["frameOverTime"], s.FrameOverTime, readMultiplier: true);
            ReadMinMaxCurve(uv["startFrame"], s.StartFrame, readMultiplier: true);
            s.TilesX = (int)uv.LongOf("tilesX", 1);
            s.TilesY = (int)uv.LongOf("tilesY", 1);
            s.UvAnimationType = (int)uv.LongOf("animationType");
            s.RowIndex = (int)uv.LongOf("rowIndex");
            s.Cycles = (int)uv.LongOf("cycles", 1);
            s.FlipU = (uv.LongOf("flipU") & 1) != 0;
            s.FlipV = (uv.LongOf("flipV") & 2) != 0;
        }

        UnityYamlNode? lights = r["LightsModule"];
        if (lights != null)
        {
            s.LightsEnabled = (lights.ScalarOf("enabled") ?? "0") != "0";
            s.LightRatio = lights.FloatOf("ratio", 0.5f);
            s.LightPrefabFileId = lights["light"]?.LongOf("fileID") ?? 0;
            ReadMinMaxGradient(lights["color"], s.LightColor);
            ReadMinMaxCurve(lights["rangeCurve"] ?? lights["range"], s.LightRange);
            ReadMinMaxCurve(lights["intensityCurve"] ?? lights["intensity"], s.LightIntensity);
            s.MaxLights = (int)lights.LongOf("maxLights", 20);
        }

        UnityYamlNode? trails = r["TrailModule"];
        if (trails != null)
        {
            s.TrailsEnabled = (trails.ScalarOf("enabled") ?? "0") != "0";
            s.TrailMode = (int)trails.LongOf("mode");
            s.TrailRatio = trails.FloatOf("ratio", 0.5f);
            ReadMinMaxCurve(trails["lifetime"], s.TrailLifetime);
            s.TrailDieWithParticles = (trails.ScalarOf("dieWithParticles") ?? "1") != "0";
            s.TrailInheritParticleColor = (trails.ScalarOf("inheritParticleColor") ?? "1") != "0";
            s.TrailTextureMode = (int)trails.LongOf("textureMode");
            ReadMinMaxGradient(trails["colorOverLifetime"], s.TrailColorOverLifetime);
            ReadMinMaxCurve(trails["widthOverTrail"], s.TrailWidthOverTrail);
            s.TrailSizeAffectsWidth = (trails.ScalarOf("sizeAffectsWidth") ?? "1") != "0";
            s.TrailMaterialFileId = trails["material"]?.LongOf("fileID") ?? 0;
        }

        UnityYamlNode? customData = r["CustomDataModule"];
        if (customData != null)
        {
            s.CustomDataEnabled = (customData.ScalarOf("enabled") ?? "0") != "0";
            s.CustomDataMode = (int)customData.LongOf("mode");
            s.NumColorComponents = (int)customData.LongOf("numColorComponents");
            s.NumVectorComponents = (int)customData.LongOf("numVectorComponents");
            s.Custom1Mode = (int)customData.LongOf("mode0", s.CustomDataMode);
            s.Custom2Mode = (int)customData.LongOf("mode1");
            s.Custom1ComponentCount = (int)customData.LongOf("vectorComponentCount0", 4);
            s.Custom2ComponentCount = (int)customData.LongOf("vectorComponentCount1", 4);
            foreach ((string key, UnityYamlNode node) in customData.Map ?? new Dictionary<string, UnityYamlNode>())
            {
                if (node.Map == null) continue;
                bool isComponentCurve = key.StartsWith("vector")
                    || key.StartsWith("colorComponent")
                    || key.StartsWith("offset");
                if (!isComponentCurve) continue;
                var curve = new UnityMinMaxCurve();
                if (ReadMinMaxCurve(node, curve))
                    s.CustomDataComponents[key] = curve;
            }
        }

        return s;
    }

    private static void ReadShape(UnityYamlNode? shape, UnityShapeData target)
    {
        if (shape == null) return;
        target.Enabled = (shape.ScalarOf("enabled") ?? "0") != "0";
        target.Type = (int)shape.LongOf("type");
        target.Radius = shape["radius"]?.Map is not null
            ? shape["radius"]!.FloatOf("value", 1f) : shape.FloatOf("radius", 1f);
        target.RadiusThickness = shape.FloatOf("radiusThickness", 1f);
        target.Angle = shape.FloatOf("angle", 25f);
        target.Length = shape.FloatOf("length", 5f);
        target.Arc = shape["arc"]?.Map is not null
            ? shape["arc"]!.FloatOf("value", 360f) : shape.FloatOf("arc", 360f);
        target.PlacementMode = (int)shape.LongOf("placementMode");
        target.RandomDirectionAmount = shape.FloatOf("randomDirectionAmount");
        target.SphericalDirectionAmount = shape.FloatOf("sphericalDirectionAmount");
        target.AlignToDirection = (shape.ScalarOf("alignToDirection") ?? "0") != "0";
        UnityYamlNode? box = shape["m_BoxSize"] ?? shape["box"];
        if (box != null)
            target.Box = new[] { box.FloatOf("x", 1f), box.FloatOf("y", 1f), box.FloatOf("z", 1f) };
        ReadFloat3(shape["m_Position"], target.Position);
        ReadFloat3(shape["m_Rotation"], target.Rotation);
        ReadFloat3(shape["m_Scale"], target.Scale);
        ReadFloat3(shape["meshScale"], target.MeshScale);
        target.MeshGuid = shape["m_Mesh"]?.ScalarOf("guid") ?? string.Empty;
        target.MeshFileId = shape["m_Mesh"]?.LongOf("fileID") ?? 0;
    }

    private static void ReadEmission(UnityYamlNode? emission, UnityEmissionData target)
    {
        if (emission == null) return;
        target.Enabled = (emission.ScalarOf("enabled") ?? "0") != "0";
        ReadMinMaxCurve(emission["rateOverTime"], target.RateOverTime);
        ReadMinMaxCurve(emission["rateOverDistance"], target.RateOverDistance);
        foreach (UnityYamlNode burst in emission["m_Bursts"]?.List ?? new List<UnityYamlNode>())
        {
            var b = new UnityBurstData { Time = burst.FloatOf("time") };
            UnityYamlNode? countCurve = burst["countCurve"];
            if (countCurve != null)
            {
                ReadMinMaxCurve(countCurve, b.Count);
            }
            else
            {
                b.Count = new UnityMinMaxCurve { State = 0, Scalar = burst.FloatOf("count") };
            }
            b.CycleCount = (int)burst.LongOf("cycleCount", 1);
            b.RepeatInterval = burst.FloatOf("repeatInterval");
            b.Probability = burst.FloatOf("probability", 1f);
            target.Bursts.Add(b);
        }
    }

    // -- class 199 ----------------------------------------------------------

    private static UnityParticleRendererData ReadRenderer(UnityYamlDocument doc)
    {
        UnityYamlNode r = doc.Root;
        var renderer = new UnityParticleRendererData
        {
            GameObjectId = r["m_GameObject"]?.LongOf("fileID") ?? 0,
            Enabled = (r["m_Enabled"]?.Scalar ?? "1") != "0",
            RenderMode = (int)r.LongOf("m_RenderMode"),
            MeshGuid = r["m_Mesh"]?.ScalarOf("guid") ?? string.Empty,
            MeshFileId = r["m_Mesh"]?.LongOf("fileID") ?? 0,
            LengthScale = r.FloatOf("m_LengthScale", 1f),
            VelocityScale = r.FloatOf("m_VelocityScale", 1f),
            CameraVelocityScale = r.FloatOf("m_CameraVelocityScale"),
            SortingFudge = r.FloatOf("m_SortingFudge"),
            SortMode = (int)r.LongOf("m_SortMode"),
            NormalDirection = r.FloatOf("m_NormalDirection", 1f),
            Alignment = (int)r.LongOf("m_RenderAlignment"),
            VertexStreams = r.ScalarOf("m_VertexStreams") ?? string.Empty,
            UseCustomVertexStreams = (r.ScalarOf("m_UseCustomVertexStreams") ?? "0") != "0",
            TrailMaterialFileId = r["m_TrailMaterial"]?.LongOf("fileID") ?? 0,
        };
        string streams = renderer.VertexStreams;
        renderer.VertexStreamsValid = streams.Length % 2 == 0;
        if (renderer.VertexStreamsValid)
        {
            var streamIds = new byte[streams.Length / 2];
            for (int i = 0; i < streamIds.Length; i++)
            {
                if (!byte.TryParse(streams.AsSpan(i * 2, 2), NumberStyles.HexNumber,
                    CultureInfo.InvariantCulture, out streamIds[i]))
                {
                    renderer.VertexStreamsValid = false;
                    break;
                }
            }
            if (renderer.VertexStreamsValid)
                renderer.VertexStreamIds = streamIds;
        }
        foreach (UnityYamlNode mat in r["m_Materials"]?.List ?? new List<UnityYamlNode>())
        {
            string? guid = mat.ScalarOf("guid");
            // Slot 0 may intentionally be null while slot 1 owns the trail material.
            // Keep placeholders so a trail material never becomes the particle material.
            renderer.MaterialGuids.Add(guid ?? string.Empty);
        }
        return renderer;
    }

    // -- class 120 ----------------------------------------------------------

    private static UnityLineRendererData ReadLineRenderer(UnityYamlDocument doc)
    {
        UnityYamlNode r = doc.Root;
        var line = new UnityLineRendererData
        {
            GameObjectId = r["m_GameObject"]?.LongOf("fileID") ?? 0,
            Enabled = (r["m_Enabled"]?.Scalar ?? "1") != "0",
        };
        UnityYamlNode? p = r["m_Parameters"];
        if (p != null)
        {
            line.Alignment = (int)p.LongOf("alignment");
            line.TextureMode = (int)p.LongOf("textureMode");
            line.WidthMultiplier = p.FloatOf("widthMultiplier", 1f);
            ReadMinMaxCurve(p["widthCurve"], line.WidthCurve);
            UnityYamlNode? gradient = p["colorGradient"];
            if (gradient != null)
            {
                var g = new UnityGradient();
                ReadGradient(gradient, g);
                line.ColorGradient = g;
            }
        }
        line.UseWorldSpace = (r.ScalarOf("m_UseWorldSpace") ?? "1") != "0";
        line.Loop = (r.ScalarOf("m_Loop") ?? "0") != "0";
        foreach (UnityYamlNode pos in r["m_Positions"]?.List ?? new List<UnityYamlNode>())
            line.Positions.Add(new[] { pos.FloatOf("x"), pos.FloatOf("y"), pos.FloatOf("z") });
        foreach (UnityYamlNode mat in r["m_Materials"]?.List ?? new List<UnityYamlNode>())
        {
            string? guid = mat.ScalarOf("guid");
            if (!string.IsNullOrEmpty(guid)) line.MaterialGuids.Add(guid);
        }
        return line;
    }

    // -- shared helpers ------------------------------------------------------

    private static bool ReadMinMaxCurve(UnityYamlNode? node, UnityMinMaxCurve target, bool readMultiplier = false)
    {
        if (node == null) return false;
        if (node.Map == null)
        {
            if (!float.TryParse(node.Scalar, NumberStyles.Float, CultureInfo.InvariantCulture, out float scalar))
                return false;
            target.State = 0;
            target.Scalar = scalar;
            return true;
        }
        target.State = (int)node.LongOf("minMaxState");
        target.Scalar = node.FloatOf("scalar");
        target.MinScalar = node.FloatOf("minScalar");
        target.MaxCurve = ReadKeyframes(node["maxCurve"]);
        target.MinCurve = ReadKeyframes(node["minCurve"]);
        if (readMultiplier)
        {
            // The multiplier lives next to the curve (e.g. frameOverTimeMultiplier as a
            // sibling of frameOverTime); callers pass the curve node so support both nested
            // "multiplier" and Unity's flattened companion fields via the same map.
            target.Multiplier = node.FloatOf("multiplier", node.FloatOf("scalarM", 1f));
        }
        return true;
    }

    private static float ScalarOrCurveValue(UnityYamlNode? node, float fallback = 0f) =>
        node?.Map is not null ? node.FloatOf("scalar", fallback)
            : float.TryParse(node?.Scalar, NumberStyles.Float, CultureInfo.InvariantCulture, out float value)
                ? value : fallback;

    private static void ReadRange(UnityYamlNode? range, out float min, out float max)
    {
        min = range is null ? 0f : range.FloatOf("x", range.FloatOf("min"));
        max = range is null ? 1f : range.FloatOf("y", range.FloatOf("max", 1f));
    }

    private static UnityKeyframe[] ReadKeyframes(UnityYamlNode? curve)
    {
        if (curve == null) return Array.Empty<UnityKeyframe>();
        UnityYamlNode? keys = curve["m_Curve"];
        if (keys?.List == null) return Array.Empty<UnityKeyframe>();
        var result = new List<UnityKeyframe>(keys.List.Count);
        foreach (UnityYamlNode key in keys.List)
        {
            result.Add(new UnityKeyframe
            {
                Time = key.FloatOf("time"),
                Value = key.FloatOf("value"),
                InSlope = ReadSlope(key, "inSlope"),
                OutSlope = ReadSlope(key, "outSlope"),
                TangentMode = (int)key.LongOf("tangentMode"),
                WeightedMode = (int)key.LongOf("weightedMode"),
                InWeight = key.FloatOf("inWeight", 1f / 3f),
                OutWeight = key.FloatOf("outWeight", 1f / 3f),
            });
        }
        return result.ToArray();
    }

    private static float ReadSlope(UnityYamlNode key, string name) => key.ScalarOf(name) switch
    {
        ".inf" or "Infinity" => float.PositiveInfinity,
        "-.inf" or "-Infinity" => float.NegativeInfinity,
        _ => key.FloatOf(name),
    };

    private static void ReadMinMaxGradient(UnityYamlNode? node, UnityMinMaxGradient target)
    {
        if (node == null || node.Map == null) return;
        target.State = (int)node.LongOf("minMaxState");
        target.MaxColor = ReadRgba(node["maxColor"]) ?? target.MaxColor;
        target.MinColor = ReadRgba(node["minColor"]) ?? target.MinColor;
        if (node["maxGradient"] != null)
        {
            var g = new UnityGradient();
            ReadGradient(node["maxGradient"], g);
            target.MaxGradient = g;
        }
        if (node["minGradient"] != null)
        {
            var g = new UnityGradient();
            ReadGradient(node["minGradient"], g);
            target.MinGradient = g;
        }
    }

    private static void ReadGradient(UnityYamlNode? gradient, UnityGradient target)
    {
        if (gradient == null) return;
        int numColors = (int)gradient.LongOf("m_NumColorKeys");
        int numAlphas = (int)gradient.LongOf("m_NumAlphaKeys");
        var colors = new List<UnityGradientKey>(numColors);
        for (int i = 0; i < numColors && i < 8; i++)
        {
            UnityYamlNode? key = gradient[$"key{i}"];
            float[]? rgba = ReadRgba(key);
            if (rgba == null) continue;
            colors.Add(new UnityGradientKey
            {
                Time = gradient.FloatOf($"ctime{i}") / ushort.MaxValue,
                R = rgba[0], G = rgba[1], B = rgba[2], A = rgba[3],
            });
        }
        target.ColorKeys = colors.ToArray();

        // Alpha keys have no separate color node — their alpha rides atime keys; reconstruct
        // alpha-only entries from atime + the nearest color key's alpha is not possible, so
        // Unity actually stores alpha inside keyN.a and alpha times in atimeN. Emit one alpha
        // key per atime entry using keyN.a (same index) — matches Unity's layout.
        var alphas = new List<UnityGradientKey>(numAlphas);
        for (int i = 0; i < numAlphas && i < 8; i++)
        {
            UnityYamlNode? key = gradient[$"key{i}"];
            if (key == null) continue;
            alphas.Add(new UnityGradientKey
            {
                Time = gradient.FloatOf($"atime{i}") / ushort.MaxValue,
                A = key.FloatOf("a", 1f),
                R = key.FloatOf("r", 1f), G = key.FloatOf("g", 1f), B = key.FloatOf("b", 1f),
            });
        }
        target.AlphaKeys = alphas.ToArray();
    }

    private static float[]? ReadRgba(UnityYamlNode? color)
    {
        if (color == null || color.Map == null) return null;
        return new[]
        {
            color.FloatOf("r", 1f), color.FloatOf("g", 1f), color.FloatOf("b", 1f), color.FloatOf("a", 1f),
        };
    }

    private static void ReadFloat3(UnityYamlNode? node, float[] target)
    {
        if (node == null) return;
        target[0] = node.FloatOf("x");
        target[1] = node.FloatOf("y");
        target[2] = node.FloatOf("z");
    }
}
