using System;
using System.IO;

using XEngine.Echo;
using XEngine.Runtime.AssetImporting;
using XEngine.Runtime.Resources;
using XEngine.Vector;

namespace XEngine.VFX;

/// <summary>CPU-side playback knobs lowered from a <see cref="VfxGraphAsset"/>.</summary>
public sealed class VfxCompiledSettings
{
    public int Capacity = 4096;
    public float SpawnRate;
    public float BurstCount;
    public float SpawnOverDistance;
    public float PeriodicBurstCount;
    public float PeriodicBurstPeriod = 1f;
    public float Lifetime = 2f;
    public float StartSpeed = 2f;
    public float StartSize = 0.15f;
    public Float3 Gravity;
    public Color StartColor = Color.White;
    public float FlipbookColumns = 1f;
    public float FlipbookRows = 1f;
    public float CollisionMode;
    public float CollisionBounce = 0.4f;
    public Float4 CollisionPlane = new(0f, 1f, 0f, 0f);
    public float SdfCollisionMode;
    public float SdfShape;
    public Float3 SdfCenter = new(0f, 0.55f, 0f);
    public Float3 SdfSize = new(0.4f, 0.4f, 0.4f);
    public float TurbulenceIntensity;
    public float TurbulenceFrequency = 1f;
    public float EventKind;
    public float EventCount;
    public float EventTarget;
    public string EventTargetName = "";
    public float DepthCollisionMode;
    public float ConformAttraction;
    public float VectorFieldIntensity;
    public float AttributeMapMode;
    public float AttributeMapScale = 1f;
    public bool OutputMesh;
    public bool OutputStrip;
    public bool OutputDecal;
    public int SubgraphCount;
    public float LitMode;
    public Float3 LightDirection = new(0.35f, 0.85f, 0.4f);
    public Color LightColor = Color.White;
    public Color AmbientColor = new(0.18f, 0.18f, 0.22f, 1f);
    public float Shininess = 32f;
    public float SoftParticleFade;
    public float DistortionIntensity;
    public float ParticleLightIntensity;
    public float ParticleLightRange = 1.5f;
    public float ParticleLightCount;
    public float SortMode;
    public float CameraFadeNear;
    public float CameraFadeFar;
    public float FlipbookBlend;
    public float FlipbookMotionScale;
    public float MotionVectorMode;
    public float MotionVectorScale = 1f;
    public float OrientMode;
    public float PositionShapeMode;
    public float PositionShapeSx = 0.25f;
    public float PositionShapeSy = 0.25f;
    public float PositionShapeSz = 0.25f;
    public float SequentialMode;
    public float SequentialSx = 0.5f;
    public float SequentialSy = 0.5f;
    public float SequentialSz = 8f;
    public float InheritVelocityAmount;
    public float TriggerEventRate;
    public float TriggerEventOverDistance;
    public float TriggerEventOnceCount;
    public float ColorOverLife;
    public float SizeOverLifeFrom = 1f;
    public float SizeOverLifeTo = 1f;
    public float SizeOverSpeedAmount;
    public float MultiplySizeAmount = 1f;
    public float MultiplyColorR = 1f;
    public float MultiplyColorG = 1f;
    public float MultiplyColorB = 1f;
    public float MultiplyColorA = 1f;
    public float AddSizeAmount;
    public float AddColorR;
    public float AddColorG;
    public float AddColorB;
    public float AddColorA;
    public float AddVelocityX;
    public float AddVelocityY;
    public float AddVelocityZ;
    public float MultiplyVelocityAmount = 1f;
    public float OverwriteSizeAmount;
    public float OverwriteColorR;
    public float OverwriteColorG;
    public float OverwriteColorB;
    public float OverwriteColorA;
    public float OverwriteVelocityX;
    public float OverwriteVelocityY;
    public float OverwriteVelocityZ;
    public float OverwriteLifetimeAmount;
    public float AddLifetimeAmount;
    public float MultiplyLifetimeAmount = 1f;
    public float AddAgeAmount;
    public float MultiplyAgeAmount = 1f;
    public float SetAgeAmount;
    public float AddPositionX;
    public float AddPositionY;
    public float AddPositionZ;
    public float SetPositionX;
    public float SetPositionY;
    public float SetPositionZ;
    public float ColorOverSpeedScale;
    public float ScreenSpaceSizeAmount;
    public float CollisionFrictionAmount;
    public float CollisionElasticityAmount;
    public float CollisionLifetimeLossAmount;
    public float LimitVelocitySpeed;
    public float KillSphereX;
    public float KillSphereY;
    public float KillSphereZ;
    public float KillSphereRadius;
    public float KillAABoxX;
    public float KillAABoxY;
    public float KillAABoxZ;
    public float KillAABoxSx;
    public float KillAABoxSy;
    public float KillAABoxSz;
    public float AttractX;
    public float AttractY;
    public float AttractZ;
    public float AttractStrength;
    public float ConformSphereX;
    public float ConformSphereY;
    public float ConformSphereZ;
    public float ConformSphereRadius;
    public float ConformSphereStrength;
    public float VortexX;
    public float VortexY;
    public float VortexZ;
    public float VortexAx;
    public float VortexAy = 1f;
    public float VortexAz;
    public float VortexRotation;
    public float VortexAttraction;
    public float ConformCylinderX;
    public float ConformCylinderY;
    public float ConformCylinderZ;
    public float ConformCylinderAx;
    public float ConformCylinderAy = 1f;
    public float ConformCylinderAz;
    public float ConformCylinderRadius = 1f;
    public float ConformCylinderStrength;
    public float KillPlaneNx;
    public float KillPlaneNy;
    public float KillPlaneNz;
    public float KillPlaneDistance;
    public float DragAmount;
    public float ForceX;
    public float ForceY;
    public float ForceZ;
    public float AngularVelocityRate;
    public float CustomHlsl;
    public float FixedAxisMode;
    public float FixedAxisX;
    public float FixedAxisY = 1f;
    public float FixedAxisZ;
    public float LookAtMode;
    public float LookAtX;
    public float LookAtY;
    public float LookAtZ;
    public float FlipbookRate;
    public float FlipbookStart;
}

/// <summary>
/// Lowers a Visual Effect Graph to <see cref="VfxCompiledSettings"/> and a compute/output
/// Shader DSL string. Delegates graph walking to <see cref="VfxGraphSourceCompiler"/> so the
/// AI Host can compile Echo <c>.vfx</c> without ScriptableObject / ShaderParser.
/// </summary>
public static class VfxGraphCompiler
{
    public const string InitMarker = VfxGraphSourceCompiler.InitMarker;
    public const string UpdateMarker = VfxGraphSourceCompiler.UpdateMarker;

    public static VfxCompiledSettings CompileSettings(VfxGraphAsset graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        graph.OnValidate();
        return ToCompiled(VfxGraphSourceCompiler.CompileSettings(
            graph.GraphName, graph.Template, graph.Capacity,
            graph.Properties, graph.Contexts, graph.Blocks, graph.Operators, graph.Edges,
            ResolveEcho));
    }

    public static string CompileToSource(VfxGraphAsset graph)
    {
        ArgumentNullException.ThrowIfNull(graph);
        graph.OnValidate();
        return VfxGraphSourceCompiler.CompileToSource(
            graph.GraphName, graph.Template, graph.Capacity,
            graph.Properties, graph.Contexts, graph.Blocks, graph.Operators, graph.Edges,
            VfxShaderLibrary.ReadShaderSource(),
            ResolveEcho);
    }

    public static bool TryCompileToShader(VfxGraphAsset graph, out Shader? shader, out string error)
    {
        shader = null;
        error = "";
        try
        {
            string source = CompileToSource(graph);
            if (!ShaderParser.ParseShader(graph.GraphName + ".shader", source, out shader) || shader is null)
            {
                error = "ShaderParser rejected generated source.";
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static VfxCompiledSettings ToCompiled(VfxGraphLoweredSettings lowered)
    {
        return new VfxCompiledSettings
        {
            Capacity = lowered.Capacity,
            SpawnRate = lowered.SpawnRate,
            BurstCount = lowered.BurstCount,
            SpawnOverDistance = lowered.SpawnOverDistance,
            PeriodicBurstCount = lowered.PeriodicBurstCount,
            PeriodicBurstPeriod = lowered.PeriodicBurstPeriod,
            Lifetime = lowered.Lifetime,
            StartSpeed = lowered.StartSpeed,
            StartSize = lowered.StartSize,
            Gravity = new Float3(lowered.GravityX, lowered.GravityY, lowered.GravityZ),
            StartColor = new Color(lowered.ColorR, lowered.ColorG, lowered.ColorB, lowered.ColorA),
            FlipbookColumns = lowered.FlipbookColumns,
            FlipbookRows = lowered.FlipbookRows,
            CollisionMode = lowered.CollisionMode,
            CollisionBounce = lowered.CollisionBounce,
            CollisionPlane = new Float4(
                lowered.CollisionNx, lowered.CollisionNy, lowered.CollisionNz, lowered.CollisionD),
            SdfCollisionMode = lowered.SdfCollisionMode,
            SdfShape = lowered.SdfShape,
            SdfCenter = new Float3(lowered.SdfCx, lowered.SdfCy, lowered.SdfCz),
            SdfSize = new Float3(lowered.SdfSx, lowered.SdfSy, lowered.SdfSz),
            TurbulenceIntensity = lowered.TurbulenceIntensity,
            TurbulenceFrequency = lowered.TurbulenceFrequency,
            EventKind = lowered.EventKind,
            EventCount = lowered.EventCount,
            EventTarget = lowered.EventTarget,
            EventTargetName = lowered.EventTargetName ?? "",
            DepthCollisionMode = lowered.DepthCollisionMode,
            ConformAttraction = lowered.ConformAttraction,
            VectorFieldIntensity = lowered.VectorFieldIntensity,
            AttributeMapMode = lowered.AttributeMapMode,
            AttributeMapScale = lowered.AttributeMapScale,
            OutputMesh = lowered.OutputMesh,
            OutputStrip = lowered.OutputStrip,
            OutputDecal = lowered.OutputDecal,
            SubgraphCount = lowered.SubgraphCount,
            LitMode = lowered.LitMode,
            LightDirection = new Float3(lowered.LightDx, lowered.LightDy, lowered.LightDz),
            LightColor = new Color(lowered.LightR, lowered.LightG, lowered.LightB, lowered.LightIntensity),
            AmbientColor = new Color(lowered.AmbientR, lowered.AmbientG, lowered.AmbientB, 1f),
            Shininess = lowered.Shininess,
            SoftParticleFade = lowered.SoftParticleFade,
            DistortionIntensity = lowered.DistortionIntensity,
            ParticleLightIntensity = lowered.ParticleLightIntensity,
            ParticleLightRange = lowered.ParticleLightRange,
            ParticleLightCount = lowered.ParticleLightCount,
            SortMode = lowered.SortMode,
            CameraFadeNear = lowered.CameraFadeNear,
            CameraFadeFar = lowered.CameraFadeFar,
            FlipbookBlend = lowered.FlipbookBlend,
            FlipbookMotionScale = lowered.FlipbookMotionScale,
            MotionVectorMode = lowered.MotionVectorMode,
            MotionVectorScale = lowered.MotionVectorScale,
            OrientMode = lowered.OrientMode,
            PositionShapeMode = lowered.PositionShapeMode,
            PositionShapeSx = lowered.PositionShapeSx,
            PositionShapeSy = lowered.PositionShapeSy,
            PositionShapeSz = lowered.PositionShapeSz,
            SequentialMode = lowered.SequentialMode,
            SequentialSx = lowered.SequentialSx,
            SequentialSy = lowered.SequentialSy,
            SequentialSz = lowered.SequentialSz,
            InheritVelocityAmount = lowered.InheritVelocityAmount,
            TriggerEventRate = lowered.TriggerEventRate,
            TriggerEventOverDistance = lowered.TriggerEventOverDistance,
            TriggerEventOnceCount = lowered.TriggerEventOnceCount,
            ColorOverLife = lowered.ColorOverLife,
            SizeOverLifeFrom = lowered.SizeOverLifeFrom,
            SizeOverLifeTo = lowered.SizeOverLifeTo,
            SizeOverSpeedAmount = lowered.SizeOverSpeedAmount,
            MultiplySizeAmount = lowered.MultiplySizeAmount,
            MultiplyColorR = lowered.MultiplyColorR,
            MultiplyColorG = lowered.MultiplyColorG,
            MultiplyColorB = lowered.MultiplyColorB,
            MultiplyColorA = lowered.MultiplyColorA,
            AddSizeAmount = lowered.AddSizeAmount,
            AddColorR = lowered.AddColorR,
            AddColorG = lowered.AddColorG,
            AddColorB = lowered.AddColorB,
            AddColorA = lowered.AddColorA,
            AddVelocityX = lowered.AddVelocityX,
            AddVelocityY = lowered.AddVelocityY,
            AddVelocityZ = lowered.AddVelocityZ,
            MultiplyVelocityAmount = lowered.MultiplyVelocityAmount,
            OverwriteSizeAmount = lowered.OverwriteSizeAmount,
            OverwriteColorR = lowered.OverwriteColorR,
            OverwriteColorG = lowered.OverwriteColorG,
            OverwriteColorB = lowered.OverwriteColorB,
            OverwriteColorA = lowered.OverwriteColorA,
            OverwriteVelocityX = lowered.OverwriteVelocityX,
            OverwriteVelocityY = lowered.OverwriteVelocityY,
            OverwriteVelocityZ = lowered.OverwriteVelocityZ,
            OverwriteLifetimeAmount = lowered.OverwriteLifetimeAmount,
            AddLifetimeAmount = lowered.AddLifetimeAmount,
            MultiplyLifetimeAmount = lowered.MultiplyLifetimeAmount,
            AddAgeAmount = lowered.AddAgeAmount,
            MultiplyAgeAmount = lowered.MultiplyAgeAmount,
            SetAgeAmount = lowered.SetAgeAmount,
            AddPositionX = lowered.AddPositionX,
            AddPositionY = lowered.AddPositionY,
            AddPositionZ = lowered.AddPositionZ,
            SetPositionX = lowered.SetPositionX,
            SetPositionY = lowered.SetPositionY,
            SetPositionZ = lowered.SetPositionZ,
            ColorOverSpeedScale = lowered.ColorOverSpeedScale,
            ScreenSpaceSizeAmount = lowered.ScreenSpaceSizeAmount,
            CollisionFrictionAmount = lowered.CollisionFrictionAmount,
            CollisionElasticityAmount = lowered.CollisionElasticityAmount,
            CollisionLifetimeLossAmount = lowered.CollisionLifetimeLossAmount,
            LimitVelocitySpeed = lowered.LimitVelocitySpeed,
            KillSphereX = lowered.KillSphereX,
            KillSphereY = lowered.KillSphereY,
            KillSphereZ = lowered.KillSphereZ,
            KillSphereRadius = lowered.KillSphereRadius,
            KillAABoxX = lowered.KillAABoxX,
            KillAABoxY = lowered.KillAABoxY,
            KillAABoxZ = lowered.KillAABoxZ,
            KillAABoxSx = lowered.KillAABoxSx,
            KillAABoxSy = lowered.KillAABoxSy,
            KillAABoxSz = lowered.KillAABoxSz,
            AttractX = lowered.AttractX,
            AttractY = lowered.AttractY,
            AttractZ = lowered.AttractZ,
            AttractStrength = lowered.AttractStrength,
            ConformSphereX = lowered.ConformSphereX,
            ConformSphereY = lowered.ConformSphereY,
            ConformSphereZ = lowered.ConformSphereZ,
            ConformSphereRadius = lowered.ConformSphereRadius,
            ConformSphereStrength = lowered.ConformSphereStrength,
            VortexX = lowered.VortexX,
            VortexY = lowered.VortexY,
            VortexZ = lowered.VortexZ,
            VortexAx = lowered.VortexAx,
            VortexAy = lowered.VortexAy,
            VortexAz = lowered.VortexAz,
            VortexRotation = lowered.VortexRotation,
            VortexAttraction = lowered.VortexAttraction,
            ConformCylinderX = lowered.ConformCylinderX,
            ConformCylinderY = lowered.ConformCylinderY,
            ConformCylinderZ = lowered.ConformCylinderZ,
            ConformCylinderAx = lowered.ConformCylinderAx,
            ConformCylinderAy = lowered.ConformCylinderAy,
            ConformCylinderAz = lowered.ConformCylinderAz,
            ConformCylinderRadius = lowered.ConformCylinderRadius,
            ConformCylinderStrength = lowered.ConformCylinderStrength,
            KillPlaneNx = lowered.KillPlaneNx,
            KillPlaneNy = lowered.KillPlaneNy,
            KillPlaneNz = lowered.KillPlaneNz,
            KillPlaneDistance = lowered.KillPlaneDistance,
            DragAmount = lowered.DragAmount,
            ForceX = lowered.ForceX,
            ForceY = lowered.ForceY,
            ForceZ = lowered.ForceZ,
            AngularVelocityRate = lowered.AngularVelocityRate,
            CustomHlsl = lowered.CustomHlsl,
            FixedAxisMode = lowered.FixedAxisMode,
            FixedAxisX = lowered.FixedAxisX,
            FixedAxisY = lowered.FixedAxisY,
            FixedAxisZ = lowered.FixedAxisZ,
            LookAtMode = lowered.LookAtMode,
            LookAtX = lowered.LookAtX,
            LookAtY = lowered.LookAtY,
            LookAtZ = lowered.LookAtZ,
            FlipbookRate = lowered.FlipbookRate,
            FlipbookStart = lowered.FlipbookStart,
        };
    }

    private static VfxGraphLists? ResolveEcho(string path)
    {
        string? file = VfxSubgraph.ResolveFile(path);
        if (file == null)
            return null;
        EchoObject echo = EchoObject.ReadFromString(File.ReadAllText(file));
        VfxGraphAsset? asset = Serializer.Deserialize<VfxGraphAsset>(echo);
        if (asset == null)
            return null;
        return VfxGraphLists.From(
            asset.GraphName, asset.Template, asset.Capacity,
            asset.Properties, asset.Contexts, asset.Blocks, asset.Operators, asset.Edges);
    }
}
