using System;
using System.Collections.Generic;

namespace XEngine.VFX;

/// <summary>
/// Visual Effect Graph node catalog. Compiler and editor share these type names.
/// Contexts stack blocks; operators feed block ports over <see cref="VfxEdge"/>.
/// </summary>
public static class VfxNodeLibrary
{
    public const string Spawn = "Spawn";
    public const string Initialize = "Initialize";
    public const string Update = "Update";
    public const string OutputParticleQuad = "OutputParticleQuad";
    public const string OutputParticleMesh = "OutputParticleMesh";
    public const string OutputParticleStrip = "OutputParticleStrip";
    public const string OutputParticleDecal = "OutputParticleDecal";

    public const string ConstantSpawnRate = "ConstantSpawnRate";
    public const string SpawnOverDistance = "SpawnOverDistance";
    public const string Burst = "Burst";
    public const string PeriodicBurst = "PeriodicBurst";
    public const string SetVelocity = "SetVelocity";
    public const string SetColor = "SetColor";
    public const string SetSize = "SetSize";
    public const string SetLifetime = "SetLifetime";
    public const string AttributeMap = "AttributeMap";
    public const string PositionShape = "PositionShape";
    public const string Sequential = "Sequential";
    public const string InheritVelocity = "InheritVelocity";
    public const string Gravity = "Gravity";
    public const string ColorOverLife = "ColorOverLife";
    public const string SizeOverLife = "SizeOverLife";
    public const string SizeOverSpeed = "SizeOverSpeed";
    public const string MultiplySize = "MultiplySize";
    public const string MultiplyColor = "MultiplyColor";
    public const string AddSize = "AddSize";
    public const string AddColor = "AddColor";
    public const string AddVelocity = "AddVelocity";
    public const string MultiplyVelocity = "MultiplyVelocity";
    public const string OverwriteSize = "OverwriteSize";
    public const string OverwriteColor = "OverwriteColor";
    public const string OverwriteVelocity = "OverwriteVelocity";
    public const string OverwriteLifetime = "OverwriteLifetime";
    public const string AddLifetime = "AddLifetime";
    public const string MultiplyLifetime = "MultiplyLifetime";
    public const string AddAge = "AddAge";
    public const string MultiplyAge = "MultiplyAge";
    public const string SetAge = "SetAge";
    public const string AddPosition = "AddPosition";
    public const string SetPosition = "SetPosition";
    public const string ColorOverSpeed = "ColorOverSpeed";
    public const string LinearDrag = "LinearDrag";
    public const string Force = "Force";
    public const string AngularVelocity = "AngularVelocity";
    public const string CustomHLSL = "CustomHLSL";
    public const string CollideWithPlane = "CollideWithPlane";
    public const string CollisionFriction = "CollisionFriction";
    public const string CollisionElasticity = "CollisionElasticity";
    public const string CollisionLifetimeLoss = "CollisionLifetimeLoss";
    public const string LimitVelocity = "LimitVelocity";
    public const string KillSphere = "KillSphere";
    public const string KillAABox = "KillAABox";
    public const string KillPlane = "KillPlane";
    public const string Attract = "Attract";
    public const string ConformToSphere = "ConformToSphere";
    public const string Vortex = "Vortex";
    public const string ConformToCylinder = "ConformToCylinder";
    public const string CollideWithSDF = "CollideWithSDF";
    public const string Turbulence = "Turbulence";
    public const string TriggerEvent = "TriggerEvent";
    public const string TriggerEventRate = "TriggerEventRate";
    public const string TriggerEventOverDistance = "TriggerEventOverDistance";
    public const string TriggerEventOnce = "TriggerEventOnce";
    public const string GPUEventTarget = "GPUEventTarget";
    public const string CollideWithDepth = "CollideWithDepth";
    public const string ConformToSDF = "ConformToSDF";
    public const string VectorField = "VectorField";
    public const string OrientToView = "OrientToView";
    public const string OrientAlongVelocity = "OrientAlongVelocity";
    public const string OrientFixedAxis = "OrientFixedAxis";
    public const string OrientLookAt = "OrientLookAt";
    public const string Distort = "Distort";
    public const string ParticleLight = "ParticleLight";
    public const string Sort = "Sort";
    public const string CameraFade = "CameraFade";
    public const string FlipbookBlend = "FlipbookBlend";
    public const string FlipbookMotion = "FlipbookMotion";
    public const string FlipbookRate = "FlipbookRate";
    public const string FlipbookStart = "FlipbookStart";
    public const string MotionVector = "MotionVector";
    public const string ScreenSpaceSize = "ScreenSpaceSize";

    public const string Float = "Float";
    public const string Color = "Color";
    public const string Add = "Add";
    public const string Multiply = "Multiply";
    public const string Time = "Time";
    public const string Random = "Random";
    public const string Subgraph = "Subgraph";

    public const string FlowPort = "Flow";

    public static readonly string[] ContextTypes =
    [
        Spawn, Initialize, Update, OutputParticleQuad, OutputParticleMesh, OutputParticleStrip, OutputParticleDecal,
    ];

    public static readonly string[] BlockTypes =
    [
        ConstantSpawnRate, SpawnOverDistance, Burst, PeriodicBurst, SetVelocity, SetColor, SetSize, SetLifetime, AttributeMap, PositionShape, Sequential, InheritVelocity,
        Gravity, ColorOverLife, SizeOverLife, SizeOverSpeed, ColorOverSpeed, MultiplySize, MultiplyColor, AddSize, AddColor, AddVelocity, MultiplyVelocity, OverwriteSize, OverwriteColor, OverwriteVelocity, OverwriteLifetime, AddLifetime, MultiplyLifetime, AddAge, MultiplyAge, SetAge, AddPosition, SetPosition, LinearDrag, Force, AngularVelocity, CustomHLSL, CollideWithPlane, CollisionFriction, CollisionElasticity, CollisionLifetimeLoss, LimitVelocity, KillSphere, KillAABox, KillPlane, Attract, ConformToSphere, Vortex, ConformToCylinder, CollideWithSDF, Turbulence, TriggerEvent, TriggerEventRate, TriggerEventOverDistance, TriggerEventOnce, GPUEventTarget, CollideWithDepth, ConformToSDF, VectorField, OrientToView, OrientAlongVelocity, OrientFixedAxis, OrientLookAt, Distort, ParticleLight, Sort, CameraFade, FlipbookBlend, FlipbookMotion, FlipbookRate, FlipbookStart, MotionVector, ScreenSpaceSize, Subgraph,
    ];

    public static readonly string[] OperatorTypes =
    [
        Float, Color, Add, Multiply, Time, Random, Subgraph,
    ];

    public static bool IsKnown(string type)
        => KindOf(type) != null;

    public static VfxNodeKind? KindOf(string type)
    {
        if (Contains(ContextTypes, type))
            return VfxNodeKind.Context;
        if (Contains(BlockTypes, type))
            return VfxNodeKind.Block;
        if (Contains(OperatorTypes, type))
            return VfxNodeKind.Operator;
        return null;
    }

    public static bool IsOutputContext(string type)
        => string.Equals(type, OutputParticleQuad, StringComparison.Ordinal)
            || string.Equals(type, OutputParticleMesh, StringComparison.Ordinal)
            || string.Equals(type, OutputParticleStrip, StringComparison.Ordinal)
            || string.Equals(type, OutputParticleDecal, StringComparison.Ordinal);

    public static bool IsContext(string type) => Contains(ContextTypes, type);
    public static bool IsBlock(string type) => Contains(BlockTypes, type);
    public static bool IsOperator(string type) => Contains(OperatorTypes, type);

    /// <summary>Fills input/output port ids for the editor NodeGraph (M4).</summary>
    public static void GetPorts(string type, List<string> inputs, List<string> outputs)
    {
        inputs.Clear();
        outputs.Clear();
        if (string.Equals(type, Spawn, StringComparison.Ordinal))
        {
            outputs.Add(FlowPort);
            return;
        }
        if (string.Equals(type, Initialize, StringComparison.Ordinal)
            || string.Equals(type, Update, StringComparison.Ordinal))
        {
            inputs.Add(FlowPort);
            outputs.Add(FlowPort);
            return;
        }
        if (string.Equals(type, OutputParticleQuad, StringComparison.Ordinal)
            || string.Equals(type, OutputParticleMesh, StringComparison.Ordinal)
            || string.Equals(type, OutputParticleStrip, StringComparison.Ordinal)
            || string.Equals(type, OutputParticleDecal, StringComparison.Ordinal))
        {
            inputs.Add(FlowPort);
            return;
        }
        if (string.Equals(type, ConstantSpawnRate, StringComparison.Ordinal)
            || string.Equals(type, SpawnOverDistance, StringComparison.Ordinal))
        {
            inputs.Add("Rate");
            return;
        }
        if (string.Equals(type, Burst, StringComparison.Ordinal))
        {
            inputs.Add("Count");
            return;
        }
        if (string.Equals(type, PeriodicBurst, StringComparison.Ordinal))
        {
            inputs.Add("Count");
            inputs.Add("Period");
            return;
        }
        if (string.Equals(type, SetVelocity, StringComparison.Ordinal))
        {
            inputs.Add("Speed");
            return;
        }
        if (string.Equals(type, SetColor, StringComparison.Ordinal))
        {
            inputs.Add("Color");
            return;
        }
        if (string.Equals(type, SetSize, StringComparison.Ordinal))
        {
            inputs.Add("Size");
            return;
        }
        if (string.Equals(type, SetLifetime, StringComparison.Ordinal))
        {
            inputs.Add("Lifetime");
            return;
        }
        if (string.Equals(type, AttributeMap, StringComparison.Ordinal))
        {
            inputs.Add("Mode");
            inputs.Add("Scale");
            return;
        }
        if (string.Equals(type, InheritVelocity, StringComparison.Ordinal))
        {
            inputs.Add("Amount");
            return;
        }
        if (string.Equals(type, PositionShape, StringComparison.Ordinal)
            || string.Equals(type, Sequential, StringComparison.Ordinal))
        {
            inputs.Add("Mode");
            inputs.Add("Sx");
            inputs.Add("Sy");
            inputs.Add("Sz");
            return;
        }
        if (string.Equals(type, Gravity, StringComparison.Ordinal))
        {
            inputs.Add("Force");
            return;
        }
        if (string.Equals(type, ColorOverLife, StringComparison.Ordinal)
            || string.Equals(type, SizeOverLife, StringComparison.Ordinal))
        {
            inputs.Add("From");
            inputs.Add("To");
            return;
        }
        if (string.Equals(type, ColorOverSpeed, StringComparison.Ordinal))
        {
            inputs.Add("From");
            inputs.Add("To");
            inputs.Add("Scale");
            return;
        }
        if (string.Equals(type, MultiplyColor, StringComparison.Ordinal)
            || string.Equals(type, AddColor, StringComparison.Ordinal)
            || string.Equals(type, OverwriteColor, StringComparison.Ordinal))
        {
            inputs.Add("R");
            inputs.Add("G");
            inputs.Add("B");
            inputs.Add("A");
            return;
        }
        if (string.Equals(type, LinearDrag, StringComparison.Ordinal)
            || string.Equals(type, SizeOverSpeed, StringComparison.Ordinal)
            || string.Equals(type, MultiplySize, StringComparison.Ordinal)
            || string.Equals(type, MultiplyVelocity, StringComparison.Ordinal)
            || string.Equals(type, OverwriteSize, StringComparison.Ordinal)
            || string.Equals(type, OverwriteLifetime, StringComparison.Ordinal)
            || string.Equals(type, AddLifetime, StringComparison.Ordinal)
            || string.Equals(type, MultiplyLifetime, StringComparison.Ordinal)
            || string.Equals(type, AddAge, StringComparison.Ordinal)
            || string.Equals(type, MultiplyAge, StringComparison.Ordinal)
            || string.Equals(type, SetAge, StringComparison.Ordinal)
            || string.Equals(type, AddSize, StringComparison.Ordinal)
            || string.Equals(type, ScreenSpaceSize, StringComparison.Ordinal)
            || string.Equals(type, CollisionFriction, StringComparison.Ordinal)
            || string.Equals(type, CollisionElasticity, StringComparison.Ordinal)
            || string.Equals(type, CollisionLifetimeLoss, StringComparison.Ordinal))
        {
            inputs.Add("Amount");
            return;
        }
        if (string.Equals(type, LimitVelocity, StringComparison.Ordinal))
        {
            inputs.Add("Speed");
            return;
        }
        if (string.Equals(type, KillSphere, StringComparison.Ordinal))
        {
            inputs.Add("X");
            inputs.Add("Y");
            inputs.Add("Z");
            inputs.Add("Radius");
            return;
        }
        if (string.Equals(type, KillAABox, StringComparison.Ordinal))
        {
            inputs.Add("X");
            inputs.Add("Y");
            inputs.Add("Z");
            inputs.Add("Sx");
            inputs.Add("Sy");
            inputs.Add("Sz");
            return;
        }
        if (string.Equals(type, KillPlane, StringComparison.Ordinal))
        {
            inputs.Add("Nx");
            inputs.Add("Ny");
            inputs.Add("Nz");
            inputs.Add("Distance");
            return;
        }
        if (string.Equals(type, Attract, StringComparison.Ordinal))
        {
            inputs.Add("X");
            inputs.Add("Y");
            inputs.Add("Z");
            inputs.Add("Strength");
            return;
        }
        if (string.Equals(type, ConformToSphere, StringComparison.Ordinal))
        {
            inputs.Add("X");
            inputs.Add("Y");
            inputs.Add("Z");
            inputs.Add("Radius");
            inputs.Add("Strength");
            return;
        }
        if (string.Equals(type, Vortex, StringComparison.Ordinal))
        {
            inputs.Add("X");
            inputs.Add("Y");
            inputs.Add("Z");
            inputs.Add("Ax");
            inputs.Add("Ay");
            inputs.Add("Az");
            inputs.Add("Rotation");
            inputs.Add("Attraction");
            return;
        }
        if (string.Equals(type, ConformToCylinder, StringComparison.Ordinal))
        {
            inputs.Add("X");
            inputs.Add("Y");
            inputs.Add("Z");
            inputs.Add("Ax");
            inputs.Add("Ay");
            inputs.Add("Az");
            inputs.Add("Radius");
            inputs.Add("Strength");
            return;
        }
        if (string.Equals(type, Force, StringComparison.Ordinal)
            || string.Equals(type, AddVelocity, StringComparison.Ordinal)
            || string.Equals(type, AddPosition, StringComparison.Ordinal)
            || string.Equals(type, SetPosition, StringComparison.Ordinal)
            || string.Equals(type, OverwriteVelocity, StringComparison.Ordinal))
        {
            inputs.Add("X");
            inputs.Add("Y");
            inputs.Add("Z");
            return;
        }
        if (string.Equals(type, AngularVelocity, StringComparison.Ordinal))
        {
            inputs.Add("Rate");
            return;
        }
        if (string.Equals(type, CustomHLSL, StringComparison.Ordinal))
        {
            inputs.Add("Code");
            return;
        }
        if (string.Equals(type, CollideWithPlane, StringComparison.Ordinal))
        {
            inputs.Add("Mode");
            inputs.Add("Bounce");
            inputs.Add("Nx");
            inputs.Add("Ny");
            inputs.Add("Nz");
            inputs.Add("Distance");
            return;
        }
        if (string.Equals(type, CollideWithSDF, StringComparison.Ordinal))
        {
            inputs.Add("Mode");
            inputs.Add("Shape");
            inputs.Add("Cx");
            inputs.Add("Cy");
            inputs.Add("Cz");
            inputs.Add("Sx");
            inputs.Add("Sy");
            inputs.Add("Sz");
            return;
        }
        if (string.Equals(type, Turbulence, StringComparison.Ordinal))
        {
            inputs.Add("Intensity");
            inputs.Add("Frequency");
            return;
        }
        if (string.Equals(type, TriggerEvent, StringComparison.Ordinal))
        {
            inputs.Add("Kind");
            inputs.Add("Count");
            return;
        }
        if (string.Equals(type, TriggerEventRate, StringComparison.Ordinal)
            || string.Equals(type, TriggerEventOverDistance, StringComparison.Ordinal))
        {
            inputs.Add("Rate");
            return;
        }
        if (string.Equals(type, TriggerEventOnce, StringComparison.Ordinal))
        {
            inputs.Add("Count");
            return;
        }
        if (string.Equals(type, GPUEventTarget, StringComparison.Ordinal))
        {
            inputs.Add("Name");
            return;
        }
        if (string.Equals(type, CollideWithDepth, StringComparison.Ordinal))
        {
            inputs.Add("Mode");
            return;
        }
        if (string.Equals(type, ConformToSDF, StringComparison.Ordinal))
        {
            inputs.Add("Attraction");
            return;
        }
        if (string.Equals(type, VectorField, StringComparison.Ordinal))
        {
            inputs.Add("Intensity");
            return;
        }
        if (string.Equals(type, Distort, StringComparison.Ordinal))
        {
            inputs.Add("Intensity");
            return;
        }
        if (string.Equals(type, ParticleLight, StringComparison.Ordinal))
        {
            inputs.Add("Intensity");
            inputs.Add("Range");
            inputs.Add("Count");
            return;
        }
        if (string.Equals(type, Sort, StringComparison.Ordinal))
        {
            inputs.Add("Mode");
            return;
        }
        if (string.Equals(type, CameraFade, StringComparison.Ordinal))
        {
            inputs.Add("Near");
            inputs.Add("Far");
            return;
        }
        if (string.Equals(type, FlipbookBlend, StringComparison.Ordinal))
        {
            inputs.Add("Blend");
            return;
        }
        if (string.Equals(type, FlipbookMotion, StringComparison.Ordinal))
        {
            inputs.Add("Scale");
            return;
        }
        if (string.Equals(type, FlipbookRate, StringComparison.Ordinal))
        {
            inputs.Add("Rate");
            return;
        }
        if (string.Equals(type, FlipbookStart, StringComparison.Ordinal))
        {
            inputs.Add("Frame");
            return;
        }
        if (string.Equals(type, MotionVector, StringComparison.Ordinal))
        {
            inputs.Add("Mode");
            inputs.Add("Scale");
            return;
        }
        if (string.Equals(type, OrientAlongVelocity, StringComparison.Ordinal))
        {
            inputs.Add("Mode");
            return;
        }
        if (string.Equals(type, OrientFixedAxis, StringComparison.Ordinal)
            || string.Equals(type, OrientLookAt, StringComparison.Ordinal))
        {
            inputs.Add("Mode");
            inputs.Add("X");
            inputs.Add("Y");
            inputs.Add("Z");
            return;
        }
        if (string.Equals(type, Float, StringComparison.Ordinal)
            || string.Equals(type, Time, StringComparison.Ordinal)
            || string.Equals(type, Random, StringComparison.Ordinal))
        {
            outputs.Add("Out");
            return;
        }
        if (string.Equals(type, Color, StringComparison.Ordinal))
        {
            outputs.Add("Out");
            outputs.Add("RGB");
            outputs.Add("R");
            outputs.Add("G");
            outputs.Add("B");
            outputs.Add("A");
            return;
        }
        if (string.Equals(type, Add, StringComparison.Ordinal)
            || string.Equals(type, Multiply, StringComparison.Ordinal))
        {
            inputs.Add("A");
            inputs.Add("B");
            outputs.Add("Out");
            return;
        }
        if (string.Equals(type, Subgraph, StringComparison.Ordinal))
        {
            inputs.Add(VfxExposedNames.SubgraphPath);
        }
    }

    private static bool Contains(string[] types, string type)
    {
        if (string.IsNullOrEmpty(type))
            return false;
        for (int i = 0; i < types.Length; i++)
        {
            if (string.Equals(types[i], type, StringComparison.Ordinal))
                return true;
        }
        return false;
    }
}
