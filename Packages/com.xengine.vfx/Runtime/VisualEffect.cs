using System;
using System.Collections.Generic;

using XEngine.Runtime;
using XEngine.Runtime.Rendering;
using XEngine.Runtime.Resources;
using XEngine.Vector;

namespace XEngine.VFX;

/// <summary>GPU-driven visual effect. Skips simulation when the device has no compute.</summary>
[AddComponentMenu("Effects/Visual Effect")]
[ExecuteAlways]
[Preserve]
[ComponentIcon("\ue2ca")]
public sealed class VisualEffect : MonoBehaviour
{
    public AssetRef<VfxGraphAsset> Graph;
    public int Capacity = 4096;
    public float SpawnRate = 64f;
    public float BurstCount;
    public float SpawnOverDistance;
    public float PeriodicBurstCount;
    public float PeriodicBurstPeriod = 1f;
    public float Lifetime = 2f;
    public float StartSpeed = 2f;
    public float InheritVelocityAmount;
    public float TriggerEventRate;
    public float TriggerEventOverDistance;
    public float TriggerEventOnceCount;
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
    public float LookAtMode;
    public float LookAtX;
    public float LookAtY;
    public float LookAtZ;
    public float FlipbookRate;
    public float FlipbookStart;
    public float StartSize = 0.15f;
    public Float3 Gravity = new(0f, -1.5f, 0f);
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
    public string EventTargetName = "";
    public VisualEffect? EventTarget;
    public float DepthCollisionMode;
    public float ConformAttraction;
    public float VectorFieldIntensity;
    public float AttributeMapMode;
    public float AttributeMapScale = 1f;
    public bool OutputMesh;
    public bool OutputStrip;
    public bool OutputDecal;
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
    public Texture3D? SdfVolume;
    public Texture3D? VectorField;
    public Texture2D? CameraDepth;
    public Texture2D? AttributeMap;
    public Texture2D? FlipbookMotion;
    public Texture2D? SceneColor;
    public Mesh? ParticleMesh;
    public bool PlayOnEnable = true;
    public AssetRef<Material> Material;

    private GpuParticleSystem? _system;
    private GpuParticleRenderable? _renderable;
    private Mesh? _quad;
    private Mesh? _cube;
    private Shader? _shader;
    private Material? _fallbackMaterial;
    private readonly Dictionary<string, float> _floatOverrides = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Float4> _vectorOverrides = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Texture2D> _textureOverrides = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Texture3D> _texture3DOverrides = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Mesh> _meshOverrides = new(StringComparer.Ordinal);
    private float _emitAccumulator;
    private float _distanceLeftover;
    private float _periodicBurstLeftover;
    private float _triggerEventLeftover;
    private float _triggerDistanceLeftover;
    private bool _triggerOnceFired;
    private uint _queuedEventSpawn;
    private float _lastEmitX;
    private float _lastEmitY;
    private float _lastEmitZ;
    private bool _hasEmitPos;
    private bool _playing;
    private bool _pendingBurst;
    private int _allocatedCapacity = -1;
    private ParticleLightProxy[] _lightProxies = [];
    private float _sortCamX;
    private float _sortCamY;
    private float _sortCamZ;
    private bool _hasSortCam;

    public bool IsPlaying => _playing;
    public bool UsesGpuParticles => _system != null;
    public GpuParticleSystem? GpuSystem => _system;
    internal bool PendingBurst => _pendingBurst;
    internal uint PendingEventSpawn => _queuedEventSpawn;

    public override void OnEnable()
    {
        base.OnEnable();
        ApplyGraph();
        EnsureResources();
        if (PlayOnEnable)
            Play();
    }

    public override void OnValidate()
    {
        ApplyGraph();
    }

    /// <summary>Copies compiled graph settings onto this component when <see cref="Graph"/> is assigned.</summary>
    public void ApplyGraph()
    {
        VfxGraphAsset? graph = Graph.Res;
        if (graph == null)
            return;

        VfxCompiledSettings settings = VfxGraphCompiler.CompileSettings(graph);
        Capacity = settings.Capacity;
        SpawnRate = settings.SpawnRate;
        BurstCount = settings.BurstCount;
        SpawnOverDistance = settings.SpawnOverDistance;
        PeriodicBurstCount = settings.PeriodicBurstCount;
        PeriodicBurstPeriod = settings.PeriodicBurstPeriod;
        Lifetime = settings.Lifetime;
        StartSpeed = settings.StartSpeed;
        InheritVelocityAmount = settings.InheritVelocityAmount;
        TriggerEventRate = settings.TriggerEventRate;
        TriggerEventOverDistance = settings.TriggerEventOverDistance;
        TriggerEventOnceCount = settings.TriggerEventOnceCount;
        SizeOverSpeedAmount = settings.SizeOverSpeedAmount;
        MultiplySizeAmount = settings.MultiplySizeAmount;
        MultiplyColorR = settings.MultiplyColorR;
        MultiplyColorG = settings.MultiplyColorG;
        MultiplyColorB = settings.MultiplyColorB;
        MultiplyColorA = settings.MultiplyColorA;
        AddSizeAmount = settings.AddSizeAmount;
        AddColorR = settings.AddColorR;
        AddColorG = settings.AddColorG;
        AddColorB = settings.AddColorB;
        AddColorA = settings.AddColorA;
        AddVelocityX = settings.AddVelocityX;
        AddVelocityY = settings.AddVelocityY;
        AddVelocityZ = settings.AddVelocityZ;
        MultiplyVelocityAmount = settings.MultiplyVelocityAmount;
        OverwriteSizeAmount = settings.OverwriteSizeAmount;
        OverwriteColorR = settings.OverwriteColorR;
        OverwriteColorG = settings.OverwriteColorG;
        OverwriteColorB = settings.OverwriteColorB;
        OverwriteColorA = settings.OverwriteColorA;
        OverwriteVelocityX = settings.OverwriteVelocityX;
        OverwriteVelocityY = settings.OverwriteVelocityY;
        OverwriteVelocityZ = settings.OverwriteVelocityZ;
        OverwriteLifetimeAmount = settings.OverwriteLifetimeAmount;
        AddLifetimeAmount = settings.AddLifetimeAmount;
        MultiplyLifetimeAmount = settings.MultiplyLifetimeAmount;
        AddAgeAmount = settings.AddAgeAmount;
        MultiplyAgeAmount = settings.MultiplyAgeAmount;
        SetAgeAmount = settings.SetAgeAmount;
        AddPositionX = settings.AddPositionX;
        AddPositionY = settings.AddPositionY;
        AddPositionZ = settings.AddPositionZ;
        SetPositionX = settings.SetPositionX;
        SetPositionY = settings.SetPositionY;
        SetPositionZ = settings.SetPositionZ;
        ColorOverSpeedScale = settings.ColorOverSpeedScale;
        ScreenSpaceSizeAmount = settings.ScreenSpaceSizeAmount;
        CollisionFrictionAmount = settings.CollisionFrictionAmount;
        CollisionElasticityAmount = settings.CollisionElasticityAmount;
        CollisionLifetimeLossAmount = settings.CollisionLifetimeLossAmount;
        LimitVelocitySpeed = settings.LimitVelocitySpeed;
        KillSphereX = settings.KillSphereX;
        KillSphereY = settings.KillSphereY;
        KillSphereZ = settings.KillSphereZ;
        KillSphereRadius = settings.KillSphereRadius;
        KillAABoxX = settings.KillAABoxX;
        KillAABoxY = settings.KillAABoxY;
        KillAABoxZ = settings.KillAABoxZ;
        KillAABoxSx = settings.KillAABoxSx;
        KillAABoxSy = settings.KillAABoxSy;
        KillAABoxSz = settings.KillAABoxSz;
        AttractX = settings.AttractX;
        AttractY = settings.AttractY;
        AttractZ = settings.AttractZ;
        AttractStrength = settings.AttractStrength;
        ConformSphereX = settings.ConformSphereX;
        ConformSphereY = settings.ConformSphereY;
        ConformSphereZ = settings.ConformSphereZ;
        ConformSphereRadius = settings.ConformSphereRadius;
        ConformSphereStrength = settings.ConformSphereStrength;
        VortexX = settings.VortexX;
        VortexY = settings.VortexY;
        VortexZ = settings.VortexZ;
        VortexAx = settings.VortexAx;
        VortexAy = settings.VortexAy;
        VortexAz = settings.VortexAz;
        VortexRotation = settings.VortexRotation;
        VortexAttraction = settings.VortexAttraction;
        ConformCylinderX = settings.ConformCylinderX;
        ConformCylinderY = settings.ConformCylinderY;
        ConformCylinderZ = settings.ConformCylinderZ;
        ConformCylinderAx = settings.ConformCylinderAx;
        ConformCylinderAy = settings.ConformCylinderAy;
        ConformCylinderAz = settings.ConformCylinderAz;
        ConformCylinderRadius = settings.ConformCylinderRadius;
        ConformCylinderStrength = settings.ConformCylinderStrength;
        KillPlaneNx = settings.KillPlaneNx;
        KillPlaneNy = settings.KillPlaneNy;
        KillPlaneNz = settings.KillPlaneNz;
        KillPlaneDistance = settings.KillPlaneDistance;
        LookAtMode = settings.LookAtMode;
        LookAtX = settings.LookAtX;
        LookAtY = settings.LookAtY;
        LookAtZ = settings.LookAtZ;
        FlipbookRate = settings.FlipbookRate;
        FlipbookStart = settings.FlipbookStart;
        StartSize = settings.StartSize;
        Gravity = settings.Gravity;
        StartColor = settings.StartColor;
        FlipbookColumns = settings.FlipbookColumns;
        FlipbookRows = settings.FlipbookRows;
        CollisionMode = settings.CollisionMode;
        CollisionBounce = settings.CollisionBounce;
        CollisionPlane = settings.CollisionPlane;
        SdfCollisionMode = settings.SdfCollisionMode;
        SdfShape = settings.SdfShape;
        SdfCenter = settings.SdfCenter;
        SdfSize = settings.SdfSize;
        TurbulenceIntensity = settings.TurbulenceIntensity;
        TurbulenceFrequency = settings.TurbulenceFrequency;
        EventKind = settings.EventKind;
        EventCount = settings.EventCount;
        EventTargetName = settings.EventTargetName ?? "";
        ResolveEventTarget();
        DepthCollisionMode = settings.DepthCollisionMode;
        ConformAttraction = settings.ConformAttraction;
        VectorFieldIntensity = settings.VectorFieldIntensity;
        AttributeMapMode = settings.AttributeMapMode;
        AttributeMapScale = settings.AttributeMapScale;
        OutputMesh = settings.OutputMesh;
        OutputStrip = settings.OutputStrip;
        OutputDecal = settings.OutputDecal;
        LitMode = settings.LitMode;
        LightDirection = settings.LightDirection;
        LightColor = settings.LightColor;
        AmbientColor = settings.AmbientColor;
        Shininess = settings.Shininess;
        SoftParticleFade = settings.SoftParticleFade;
        DistortionIntensity = settings.DistortionIntensity;
        ParticleLightIntensity = settings.ParticleLightIntensity;
        ParticleLightRange = settings.ParticleLightRange;
        ParticleLightCount = settings.ParticleLightCount;
        SortMode = settings.SortMode;
        CameraFadeNear = settings.CameraFadeNear;
        CameraFadeFar = settings.CameraFadeFar;
        FlipbookBlend = settings.FlipbookBlend;
        FlipbookMotionScale = settings.FlipbookMotionScale;
        MotionVectorMode = settings.MotionVectorMode;
        MotionVectorScale = settings.MotionVectorScale;
        OrientMode = settings.OrientMode;
        ApplyOverrides();
    }

    public void ResetExposedOverrides()
    {
        _floatOverrides.Clear();
        _vectorOverrides.Clear();
        _textureOverrides.Clear();
        _texture3DOverrides.Clear();
        _meshOverrides.Clear();
        ParticleMesh = null;
        SdfVolume = null;
        VectorField = null;
        CameraDepth = null;
        AttributeMap = null;
        FlipbookMotion = null;
        SceneColor = null;
        ApplyGraph();
    }

    public bool HasFloat(string name) => HasOverrideOrProperty(name, VfxValueType.Float);

    public bool HasVector2(string name) => HasOverrideOrProperty(name, VfxValueType.Float2);

    public bool HasVector3(string name)
        => _vectorOverrides.ContainsKey(name) || HasProperty(name, VfxValueType.Float3);

    public bool HasVector4(string name)
        => _vectorOverrides.ContainsKey(name)
            || HasProperty(name, VfxValueType.Float4)
            || HasProperty(name, VfxValueType.Color);

    public bool HasTexture(string name)
        => _textureOverrides.ContainsKey(name) || HasProperty(name, VfxValueType.Texture);

    public bool HasTexture3D(string name)
        => _texture3DOverrides.ContainsKey(name) || HasProperty(name, VfxValueType.Texture3D);

    public bool HasMesh(string name)
        => _meshOverrides.ContainsKey(name) || HasProperty(name, VfxValueType.Mesh);

    public void SetFloat(string name, float value)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        _floatOverrides[name] = value;
        ApplyFloatOverride(name, value);
    }

    public float GetFloat(string name)
    {
        if (_floatOverrides.TryGetValue(name, out float value))
            return value;
        if (VfxExposedNames.EqualsName(name, VfxExposedNames.SpawnRate)) return SpawnRate;
        if (VfxExposedNames.EqualsName(name, VfxExposedNames.BurstCount)) return BurstCount;
        if (VfxExposedNames.EqualsName(name, VfxExposedNames.SpawnOverDistance)) return SpawnOverDistance;
        if (VfxExposedNames.EqualsName(name, VfxExposedNames.Lifetime)) return Lifetime;
        if (VfxExposedNames.EqualsName(name, VfxExposedNames.StartSpeed)) return StartSpeed;
        if (VfxExposedNames.EqualsName(name, VfxExposedNames.InheritVelocity)) return InheritVelocityAmount;
        if (VfxExposedNames.EqualsName(name, VfxExposedNames.TriggerEventRate)) return TriggerEventRate;
        if (VfxExposedNames.EqualsName(name, VfxExposedNames.TriggerEventOverDistance)) return TriggerEventOverDistance;
        if (VfxExposedNames.EqualsName(name, VfxExposedNames.TriggerEventOnce)) return TriggerEventOnceCount;
        if (VfxExposedNames.EqualsName(name, VfxExposedNames.SizeOverSpeed)) return SizeOverSpeedAmount;
        if (VfxExposedNames.EqualsName(name, VfxExposedNames.MultiplySize)) return MultiplySizeAmount;
        if (VfxExposedNames.EqualsName(name, VfxExposedNames.MultiplyColor)) return MultiplyColorR;
        if (VfxExposedNames.EqualsName(name, VfxExposedNames.AddSize)) return AddSizeAmount;
        if (VfxExposedNames.EqualsName(name, VfxExposedNames.AddColor)) return AddColorR;
        if (VfxExposedNames.EqualsName(name, VfxExposedNames.AddVelocity)) return AddVelocityX;
        if (VfxExposedNames.EqualsName(name, VfxExposedNames.MultiplyVelocity)) return MultiplyVelocityAmount;
        if (VfxExposedNames.EqualsName(name, VfxExposedNames.OverwriteSize)) return OverwriteSizeAmount;
        if (VfxExposedNames.EqualsName(name, VfxExposedNames.OverwriteColor)) return OverwriteColorR;
        if (VfxExposedNames.EqualsName(name, VfxExposedNames.OverwriteVelocity)) return OverwriteVelocityX;
        if (VfxExposedNames.EqualsName(name, VfxExposedNames.OverwriteLifetime)) return OverwriteLifetimeAmount;
        if (VfxExposedNames.EqualsName(name, VfxExposedNames.AddLifetime)) return AddLifetimeAmount;
        if (VfxExposedNames.EqualsName(name, VfxExposedNames.MultiplyLifetime)) return MultiplyLifetimeAmount;
        if (VfxExposedNames.EqualsName(name, VfxExposedNames.AddAge)) return AddAgeAmount;
        if (VfxExposedNames.EqualsName(name, VfxExposedNames.MultiplyAge)) return MultiplyAgeAmount;
        if (VfxExposedNames.EqualsName(name, VfxExposedNames.SetAge)) return SetAgeAmount;
        if (VfxExposedNames.EqualsName(name, VfxExposedNames.AddPosition)) return AddPositionX;
        if (VfxExposedNames.EqualsName(name, VfxExposedNames.SetPosition)) return SetPositionX;
        if (VfxExposedNames.EqualsName(name, VfxExposedNames.ColorOverSpeed)) return ColorOverSpeedScale;
        if (VfxExposedNames.EqualsName(name, VfxExposedNames.ScreenSpaceSize)) return ScreenSpaceSizeAmount;
        if (VfxExposedNames.EqualsName(name, VfxExposedNames.CollisionFriction)) return CollisionFrictionAmount;
        if (VfxExposedNames.EqualsName(name, VfxExposedNames.CollisionElasticity)) return CollisionElasticityAmount;
        if (VfxExposedNames.EqualsName(name, VfxExposedNames.CollisionLifetimeLoss)) return CollisionLifetimeLossAmount;
        if (VfxExposedNames.EqualsName(name, VfxExposedNames.LimitVelocity)) return LimitVelocitySpeed;
        if (VfxExposedNames.EqualsName(name, VfxExposedNames.KillSphere)) return KillSphereRadius;
        if (VfxExposedNames.EqualsName(name, VfxExposedNames.KillAABox)) return KillAABoxSx;
        if (VfxExposedNames.EqualsName(name, VfxExposedNames.Attract)) return AttractStrength;
        if (VfxExposedNames.EqualsName(name, VfxExposedNames.ConformToSphere)) return ConformSphereStrength;
        if (VfxExposedNames.EqualsName(name, VfxExposedNames.Vortex)) return VortexRotation;
        if (VfxExposedNames.EqualsName(name, VfxExposedNames.ConformToCylinder)) return ConformCylinderStrength;
        if (VfxExposedNames.EqualsName(name, VfxExposedNames.KillPlane)) return KillPlaneDistance;
        if (VfxExposedNames.EqualsName(name, VfxExposedNames.OrientLookAt)) return LookAtMode;
        if (VfxExposedNames.EqualsName(name, VfxExposedNames.FlipbookRate)) return FlipbookRate;
        if (VfxExposedNames.EqualsName(name, VfxExposedNames.FlipbookStart)) return FlipbookStart;
        if (VfxExposedNames.EqualsName(name, VfxExposedNames.StartSize)) return StartSize;
        if (VfxExposedNames.EqualsName(name, VfxExposedNames.FlipbookColumns)) return FlipbookColumns;
        if (VfxExposedNames.EqualsName(name, VfxExposedNames.FlipbookRows)) return FlipbookRows;
        if (VfxExposedNames.EqualsName(name, VfxExposedNames.CollisionMode)) return CollisionMode;
        if (VfxExposedNames.EqualsName(name, VfxExposedNames.CollisionBounce)) return CollisionBounce;
        if (VfxExposedNames.EqualsName(name, VfxExposedNames.SdfCollisionMode)) return SdfCollisionMode;
        if (VfxExposedNames.EqualsName(name, VfxExposedNames.SdfShape)) return SdfShape;
        if (VfxExposedNames.EqualsName(name, VfxExposedNames.TurbulenceIntensity)) return TurbulenceIntensity;
        if (VfxExposedNames.EqualsName(name, VfxExposedNames.TurbulenceFrequency)) return TurbulenceFrequency;
        if (VfxExposedNames.EqualsName(name, VfxExposedNames.EventKind)) return EventKind;
        if (VfxExposedNames.EqualsName(name, VfxExposedNames.EventCount)) return EventCount;
        if (VfxExposedNames.EqualsName(name, VfxExposedNames.DepthCollisionMode)) return DepthCollisionMode;
        if (VfxExposedNames.EqualsName(name, VfxExposedNames.ConformAttraction)) return ConformAttraction;
        if (VfxExposedNames.EqualsName(name, VfxExposedNames.VectorFieldIntensity)) return VectorFieldIntensity;
        if (VfxExposedNames.EqualsName(name, VfxExposedNames.AttributeMapMode)) return AttributeMapMode;
        if (VfxExposedNames.EqualsName(name, VfxExposedNames.AttributeMapScale)) return AttributeMapScale;
        if (VfxExposedNames.EqualsName(name, VfxExposedNames.DistortionIntensity)) return DistortionIntensity;
        if (VfxExposedNames.EqualsName(name, VfxExposedNames.ParticleLightIntensity)) return ParticleLightIntensity;
        if (VfxExposedNames.EqualsName(name, VfxExposedNames.ParticleLightRange)) return ParticleLightRange;
        if (VfxExposedNames.EqualsName(name, VfxExposedNames.ParticleLightCount)) return ParticleLightCount;
        if (VfxExposedNames.EqualsName(name, VfxExposedNames.SortMode)) return SortMode;
        if (VfxExposedNames.EqualsName(name, VfxExposedNames.CameraFadeNear)) return CameraFadeNear;
        if (VfxExposedNames.EqualsName(name, VfxExposedNames.CameraFadeFar)) return CameraFadeFar;
        if (VfxExposedNames.EqualsName(name, VfxExposedNames.FlipbookBlend)) return FlipbookBlend;
        if (VfxExposedNames.EqualsName(name, VfxExposedNames.FlipbookMotionScale)) return FlipbookMotionScale;
        if (VfxExposedNames.EqualsName(name, VfxExposedNames.MotionVectorMode)) return MotionVectorMode;
        if (VfxExposedNames.EqualsName(name, VfxExposedNames.MotionVectorScale)) return MotionVectorScale;
        if (VfxExposedNames.EqualsName(name, VfxExposedNames.OrientMode)) return OrientMode;
        if (VfxExposedNames.EqualsName(name, VfxExposedNames.LitMode)) return LitMode;
        if (VfxExposedNames.EqualsName(name, VfxExposedNames.Shininess)) return Shininess;
        if (VfxExposedNames.EqualsName(name, VfxExposedNames.SoftParticleFade)) return SoftParticleFade;
        VfxProperty? property = FindGraphProperty(name);
        if (property != null && VfxPropertyValue.TryParseFloat(property.DefaultValue, out float parsed))
            return parsed;
        return 0f;
    }

    public void SetVector2(string name, Float2 value) => SetVector4(name, new Float4(value.X, value.Y, 0f, 0f));

    public void SetVector3(string name, Float3 value) => SetVector4(name, new Float4(value.X, value.Y, value.Z, 0f));

    public void SetVector(string name, Float3 value) => SetVector3(name, value);

    public void SetVector4(string name, Float4 value)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        _vectorOverrides[name] = value;
        ApplyVectorOverride(name, value);
    }

    public Float3 GetVector3(string name)
    {
        Float4 v = GetVector4(name);
        return new Float3(v.X, v.Y, v.Z);
    }

    public Float4 GetVector4(string name)
    {
        if (_vectorOverrides.TryGetValue(name, out Float4 value))
            return value;
        if (VfxExposedNames.EqualsName(name, VfxExposedNames.Gravity))
            return new Float4(Gravity.X, Gravity.Y, Gravity.Z, 0f);
        if (VfxExposedNames.EqualsName(name, VfxExposedNames.StartColor))
            return new Float4(StartColor.R, StartColor.G, StartColor.B, StartColor.A);
        if (VfxExposedNames.EqualsName(name, VfxExposedNames.CollisionPlane))
            return CollisionPlane;
        if (VfxExposedNames.EqualsName(name, VfxExposedNames.SdfCenter))
            return new Float4(SdfCenter.X, SdfCenter.Y, SdfCenter.Z, 0f);
        if (VfxExposedNames.EqualsName(name, VfxExposedNames.SdfSize))
            return new Float4(SdfSize.X, SdfSize.Y, SdfSize.Z, 0f);
        if (VfxExposedNames.EqualsName(name, VfxExposedNames.LightDirection))
            return new Float4(LightDirection.X, LightDirection.Y, LightDirection.Z, 0f);
        if (VfxExposedNames.EqualsName(name, VfxExposedNames.LightColor))
            return new Float4(LightColor.R, LightColor.G, LightColor.B, LightColor.A);
        if (VfxExposedNames.EqualsName(name, VfxExposedNames.AmbientColor))
            return new Float4(AmbientColor.R, AmbientColor.G, AmbientColor.B, AmbientColor.A);
        VfxProperty? property = FindGraphProperty(name);
        if (property != null)
        {
            Span<float> parsed = stackalloc float[4];
            int n = VfxPropertyValue.ParseFloats(property.DefaultValue, parsed);
            return new Float4(
                n > 0 ? parsed[0] : 0f,
                n > 1 ? parsed[1] : 0f,
                n > 2 ? parsed[2] : 0f,
                n > 3 ? parsed[3] : 0f);
        }
        return default;
    }

    public void SetTexture(string name, Texture2D? texture)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (texture == null)
            _textureOverrides.Remove(name);
        else
            _textureOverrides[name] = texture;
        if (VfxExposedNames.EqualsName(name, VfxExposedNames.CameraDepth)
            || VfxExposedNames.EqualsName(name, VfxExposedNames.CameraDepthShader)
            || VfxExposedNames.EqualsName(name, VfxExposedNames.VfxCameraDepthShader))
            CameraDepth = texture;
        if (VfxExposedNames.EqualsName(name, VfxExposedNames.AttributeMap)
            || VfxExposedNames.EqualsName(name, VfxExposedNames.AttributeMapShader))
            AttributeMap = texture;
        if (VfxExposedNames.EqualsName(name, VfxExposedNames.FlipbookMotion)
            || VfxExposedNames.EqualsName(name, VfxExposedNames.FlipbookMotionShader))
            FlipbookMotion = texture;
        if (VfxExposedNames.EqualsName(name, VfxExposedNames.SceneColor)
            || VfxExposedNames.EqualsName(name, VfxExposedNames.SceneColorShader))
            SceneColor = texture;
    }

    public Texture2D? GetTexture(string name)
    {
        if (_textureOverrides.TryGetValue(name, out Texture2D? texture))
            return texture;
        if (VfxExposedNames.EqualsName(name, VfxExposedNames.CameraDepth)
            || VfxExposedNames.EqualsName(name, VfxExposedNames.CameraDepthShader)
            || VfxExposedNames.EqualsName(name, VfxExposedNames.VfxCameraDepthShader))
            return CameraDepth;
        if (VfxExposedNames.EqualsName(name, VfxExposedNames.AttributeMap)
            || VfxExposedNames.EqualsName(name, VfxExposedNames.AttributeMapShader))
            return AttributeMap;
        if (VfxExposedNames.EqualsName(name, VfxExposedNames.FlipbookMotion)
            || VfxExposedNames.EqualsName(name, VfxExposedNames.FlipbookMotionShader))
            return FlipbookMotion;
        if (VfxExposedNames.EqualsName(name, VfxExposedNames.SceneColor)
            || VfxExposedNames.EqualsName(name, VfxExposedNames.SceneColorShader))
            return SceneColor;
        return null;
    }

    public void SetTexture3D(string name, Texture3D? texture)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (texture == null)
            _texture3DOverrides.Remove(name);
        else
            _texture3DOverrides[name] = texture;
        if (VfxExposedNames.EqualsName(name, VfxExposedNames.SdfVolume))
            SdfVolume = texture;
        if (VfxExposedNames.EqualsName(name, VfxExposedNames.VectorField))
            VectorField = texture;
    }

    public Texture3D? GetTexture3D(string name)
    {
        if (_texture3DOverrides.TryGetValue(name, out Texture3D? texture))
            return texture;
        if (VfxExposedNames.EqualsName(name, VfxExposedNames.SdfVolume))
            return SdfVolume;
        if (VfxExposedNames.EqualsName(name, VfxExposedNames.VectorField))
            return VectorField;
        return null;
    }

    public void SetMesh(string name, Mesh? mesh)
    {
        ArgumentException.ThrowIfNullOrEmpty(name);
        if (mesh == null)
            _meshOverrides.Remove(name);
        else
            _meshOverrides[name] = mesh;
        if (VfxExposedNames.EqualsName(name, VfxExposedNames.OutputMesh))
            ParticleMesh = mesh;
    }

    public Mesh? GetMesh(string name)
        => _meshOverrides.TryGetValue(name, out Mesh? mesh) ? mesh : null;

    private void ApplyOverrides()
    {
        foreach (KeyValuePair<string, float> entry in _floatOverrides)
            ApplyFloatOverride(entry.Key, entry.Value);
        foreach (KeyValuePair<string, Float4> entry in _vectorOverrides)
            ApplyVectorOverride(entry.Key, entry.Value);
    }

    private void ApplyFloatOverride(string name, float value)
    {
        if (VfxExposedNames.EqualsName(name, VfxExposedNames.SpawnRate)) SpawnRate = value;
        else if (VfxExposedNames.EqualsName(name, VfxExposedNames.BurstCount)) BurstCount = value;
        else if (VfxExposedNames.EqualsName(name, VfxExposedNames.SpawnOverDistance)) SpawnOverDistance = value;
        else if (VfxExposedNames.EqualsName(name, VfxExposedNames.Lifetime)) Lifetime = value;
        else if (VfxExposedNames.EqualsName(name, VfxExposedNames.StartSpeed)) StartSpeed = value;
        else if (VfxExposedNames.EqualsName(name, VfxExposedNames.InheritVelocity)) InheritVelocityAmount = value;
        else if (VfxExposedNames.EqualsName(name, VfxExposedNames.TriggerEventRate)) TriggerEventRate = value;
        else if (VfxExposedNames.EqualsName(name, VfxExposedNames.TriggerEventOverDistance)) TriggerEventOverDistance = value;
        else if (VfxExposedNames.EqualsName(name, VfxExposedNames.TriggerEventOnce)) TriggerEventOnceCount = value;
        else if (VfxExposedNames.EqualsName(name, VfxExposedNames.SizeOverSpeed)) SizeOverSpeedAmount = value;
        else if (VfxExposedNames.EqualsName(name, VfxExposedNames.MultiplySize)) MultiplySizeAmount = value;
        else if (VfxExposedNames.EqualsName(name, VfxExposedNames.MultiplyColor)) MultiplyColorR = value;
        else if (VfxExposedNames.EqualsName(name, VfxExposedNames.AddSize)) AddSizeAmount = value;
        else if (VfxExposedNames.EqualsName(name, VfxExposedNames.AddColor)) AddColorR = value;
        else if (VfxExposedNames.EqualsName(name, VfxExposedNames.AddVelocity)) AddVelocityX = value;
        else if (VfxExposedNames.EqualsName(name, VfxExposedNames.MultiplyVelocity)) MultiplyVelocityAmount = value;
        else if (VfxExposedNames.EqualsName(name, VfxExposedNames.OverwriteSize)) OverwriteSizeAmount = value;
        else if (VfxExposedNames.EqualsName(name, VfxExposedNames.OverwriteColor)) OverwriteColorR = value;
        else if (VfxExposedNames.EqualsName(name, VfxExposedNames.OverwriteVelocity)) OverwriteVelocityX = value;
        else if (VfxExposedNames.EqualsName(name, VfxExposedNames.OverwriteLifetime)) OverwriteLifetimeAmount = value;
        else if (VfxExposedNames.EqualsName(name, VfxExposedNames.AddLifetime)) AddLifetimeAmount = value;
        else if (VfxExposedNames.EqualsName(name, VfxExposedNames.MultiplyLifetime)) MultiplyLifetimeAmount = value;
        else if (VfxExposedNames.EqualsName(name, VfxExposedNames.AddAge)) AddAgeAmount = value;
        else if (VfxExposedNames.EqualsName(name, VfxExposedNames.MultiplyAge)) MultiplyAgeAmount = value;
        else if (VfxExposedNames.EqualsName(name, VfxExposedNames.SetAge)) SetAgeAmount = value;
        else if (VfxExposedNames.EqualsName(name, VfxExposedNames.AddPosition)) AddPositionX = value;
        else if (VfxExposedNames.EqualsName(name, VfxExposedNames.SetPosition)) SetPositionX = value;
        else if (VfxExposedNames.EqualsName(name, VfxExposedNames.ColorOverSpeed)) ColorOverSpeedScale = value;
        else if (VfxExposedNames.EqualsName(name, VfxExposedNames.ScreenSpaceSize)) ScreenSpaceSizeAmount = value;
        else if (VfxExposedNames.EqualsName(name, VfxExposedNames.CollisionFriction)) CollisionFrictionAmount = value;
        else if (VfxExposedNames.EqualsName(name, VfxExposedNames.CollisionElasticity)) CollisionElasticityAmount = value;
        else if (VfxExposedNames.EqualsName(name, VfxExposedNames.CollisionLifetimeLoss)) CollisionLifetimeLossAmount = value;
        else if (VfxExposedNames.EqualsName(name, VfxExposedNames.LimitVelocity)) LimitVelocitySpeed = value;
        else if (VfxExposedNames.EqualsName(name, VfxExposedNames.KillSphere)) KillSphereRadius = value;
        else if (VfxExposedNames.EqualsName(name, VfxExposedNames.KillAABox)) KillAABoxSx = value;
        else if (VfxExposedNames.EqualsName(name, VfxExposedNames.Attract)) AttractStrength = value;
        else if (VfxExposedNames.EqualsName(name, VfxExposedNames.ConformToSphere)) ConformSphereStrength = value;
        else if (VfxExposedNames.EqualsName(name, VfxExposedNames.Vortex)) VortexRotation = value;
        else if (VfxExposedNames.EqualsName(name, VfxExposedNames.ConformToCylinder)) ConformCylinderStrength = value;
        else if (VfxExposedNames.EqualsName(name, VfxExposedNames.KillPlane)) KillPlaneDistance = value;
        else if (VfxExposedNames.EqualsName(name, VfxExposedNames.OrientLookAt))
        {
            LookAtMode = value;
            if (value >= 1.5f)
                OrientMode = value;
        }
        else if (VfxExposedNames.EqualsName(name, VfxExposedNames.FlipbookRate)) FlipbookRate = value;
        else if (VfxExposedNames.EqualsName(name, VfxExposedNames.FlipbookStart)) FlipbookStart = value;
        else if (VfxExposedNames.EqualsName(name, VfxExposedNames.StartSize)) StartSize = value;
        else if (VfxExposedNames.EqualsName(name, VfxExposedNames.FlipbookColumns)) FlipbookColumns = value;
        else if (VfxExposedNames.EqualsName(name, VfxExposedNames.FlipbookRows)) FlipbookRows = value;
        else if (VfxExposedNames.EqualsName(name, VfxExposedNames.CollisionMode)) CollisionMode = value;
        else if (VfxExposedNames.EqualsName(name, VfxExposedNames.CollisionBounce)) CollisionBounce = value;
        else if (VfxExposedNames.EqualsName(name, VfxExposedNames.SdfCollisionMode)) SdfCollisionMode = value;
        else if (VfxExposedNames.EqualsName(name, VfxExposedNames.SdfShape)) SdfShape = value;
        else if (VfxExposedNames.EqualsName(name, VfxExposedNames.TurbulenceIntensity)) TurbulenceIntensity = value;
        else if (VfxExposedNames.EqualsName(name, VfxExposedNames.TurbulenceFrequency)) TurbulenceFrequency = value;
        else if (VfxExposedNames.EqualsName(name, VfxExposedNames.EventKind)) EventKind = value;
        else if (VfxExposedNames.EqualsName(name, VfxExposedNames.EventCount)) EventCount = value;
        else if (VfxExposedNames.EqualsName(name, VfxExposedNames.DepthCollisionMode)) DepthCollisionMode = value;
        else if (VfxExposedNames.EqualsName(name, VfxExposedNames.ConformAttraction)) ConformAttraction = value;
        else if (VfxExposedNames.EqualsName(name, VfxExposedNames.VectorFieldIntensity)) VectorFieldIntensity = value;
        else if (VfxExposedNames.EqualsName(name, VfxExposedNames.AttributeMapMode)) AttributeMapMode = value;
        else if (VfxExposedNames.EqualsName(name, VfxExposedNames.AttributeMapScale)) AttributeMapScale = value;
        else if (VfxExposedNames.EqualsName(name, VfxExposedNames.DistortionIntensity)) DistortionIntensity = value;
        else if (VfxExposedNames.EqualsName(name, VfxExposedNames.ParticleLightIntensity)) ParticleLightIntensity = value;
        else if (VfxExposedNames.EqualsName(name, VfxExposedNames.ParticleLightRange)) ParticleLightRange = value;
        else if (VfxExposedNames.EqualsName(name, VfxExposedNames.ParticleLightCount)) ParticleLightCount = value;
        else if (VfxExposedNames.EqualsName(name, VfxExposedNames.SortMode)) SortMode = value;
        else if (VfxExposedNames.EqualsName(name, VfxExposedNames.CameraFadeNear)) CameraFadeNear = value;
        else if (VfxExposedNames.EqualsName(name, VfxExposedNames.CameraFadeFar)) CameraFadeFar = value;
        else if (VfxExposedNames.EqualsName(name, VfxExposedNames.FlipbookBlend)) FlipbookBlend = value;
        else if (VfxExposedNames.EqualsName(name, VfxExposedNames.FlipbookMotionScale)) FlipbookMotionScale = value;
        else if (VfxExposedNames.EqualsName(name, VfxExposedNames.MotionVectorMode)) MotionVectorMode = value;
        else if (VfxExposedNames.EqualsName(name, VfxExposedNames.MotionVectorScale)) MotionVectorScale = value;
        else if (VfxExposedNames.EqualsName(name, VfxExposedNames.OrientMode)) OrientMode = value;
        else if (VfxExposedNames.EqualsName(name, VfxExposedNames.LitMode)) LitMode = value;
        else if (VfxExposedNames.EqualsName(name, VfxExposedNames.Shininess)) Shininess = value;
        else if (VfxExposedNames.EqualsName(name, VfxExposedNames.SoftParticleFade)) SoftParticleFade = value;
    }

    private void ApplyVectorOverride(string name, Float4 value)
    {
        if (VfxExposedNames.EqualsName(name, VfxExposedNames.Gravity))
            Gravity = new Float3(value.X, value.Y, value.Z);
        else if (VfxExposedNames.EqualsName(name, VfxExposedNames.StartColor))
            StartColor = new Color(value.X, value.Y, value.Z, value.W);
        else if (VfxExposedNames.EqualsName(name, VfxExposedNames.FlipbookSize))
        {
            FlipbookColumns = value.X;
            FlipbookRows = value.Y;
        }
        else if (VfxExposedNames.EqualsName(name, VfxExposedNames.CollisionPlane))
            CollisionPlane = value;
        else if (VfxExposedNames.EqualsName(name, VfxExposedNames.SdfCenter))
            SdfCenter = new Float3(value.X, value.Y, value.Z);
        else if (VfxExposedNames.EqualsName(name, VfxExposedNames.SdfSize))
            SdfSize = new Float3(value.X, value.Y, value.Z);
        else if (VfxExposedNames.EqualsName(name, VfxExposedNames.LightDirection))
            LightDirection = new Float3(value.X, value.Y, value.Z);
        else if (VfxExposedNames.EqualsName(name, VfxExposedNames.LightColor))
            LightColor = new Color(value.X, value.Y, value.Z, value.W);
        else if (VfxExposedNames.EqualsName(name, VfxExposedNames.AmbientColor))
            AmbientColor = new Color(value.X, value.Y, value.Z, value.W);
    }

    private bool HasOverrideOrProperty(string name, VfxValueType type)
        => _floatOverrides.ContainsKey(name) || HasProperty(name, type);

    private bool HasProperty(string name, VfxValueType type)
    {
        VfxProperty? property = FindGraphProperty(name);
        return property != null && property.Type == type;
    }

    private VfxProperty? FindGraphProperty(string name)
        => Graph.Res == null ? null : VfxProperties.Find(Graph.Res.Properties, name);

    public override void OnDisable()
    {
        Stop();
        ReleaseGpu();
        base.OnDisable();
    }

    public void Play()
    {
        _playing = true;
        if (BurstCount > 0f)
            _pendingBurst = true;
        QueueCpuEvent(VfxGpuEvent.EmitOnPlay(EventKind, EventCount));
    }

    public void Pause() => _playing = false;

    /// <summary>Queues particles for the next <see cref="Simulate"/> on this system.</summary>
    public void QueueEventSpawn(uint count)
        => VfxGpuEvent.QueueTarget(ref _queuedEventSpawn, count, Capacity);

    /// <summary>
    /// Unity SendEvent: enqueue <see cref="EventCount"/> on <see cref="EventTarget"/> when set,
    /// otherwise on this system.
    /// </summary>
    public void SendEvent()
    {
        ResolveEventTarget();
        uint n = EventCount < 0.5f ? 0u : (uint)EventCount;
        if (EventTarget != null && !ReferenceEquals(EventTarget, this))
            EventTarget.QueueEventSpawn(n);
        else
            QueueEventSpawn(n);
    }

    /// <summary>
    /// Forwards a GPU OnDie / OnCollide pulse to <see cref="EventTarget"/> (or this system).
    /// </summary>
    public void ForwardGpuEvents(bool died, bool collided)
        => QueueCpuEvent(VfxGpuEvent.EmitCount(EventKind, EventCount, died, collided));

    /// <summary>Unity Always GPU Event: enqueue <see cref="EventCount"/> once per <see cref="Simulate"/>.</summary>
    internal void PulseAlwaysEvents()
        => QueueCpuEvent(VfxGpuEvent.EmitAlways(EventKind, EventCount));

    private void QueueCpuEvent(uint n)
    {
        if (n == 0)
            return;
        ResolveEventTarget();
        if (EventTarget != null && !ReferenceEquals(EventTarget, this))
            EventTarget.QueueEventSpawn(n);
        else
            QueueEventSpawn(n);
    }

    private void ResolveEventTarget()
    {
        if (EventTarget != null || string.IsNullOrWhiteSpace(EventTargetName) || GameObject == null)
            return;
        GameObject found = GameObject.Find(EventTargetName);
        if (found == null)
            return;
        EventTarget = found.GetComponent<VisualEffect>();
    }

    public void Stop()
    {
        _playing = false;
        _emitAccumulator = 0f;
        _distanceLeftover = 0f;
        _periodicBurstLeftover = 0f;
        _triggerEventLeftover = 0f;
        _triggerDistanceLeftover = 0f;
        _triggerOnceFired = false;
        _queuedEventSpawn = 0;
        _hasEmitPos = false;
        _pendingBurst = false;
    }

    public void Reinit()
    {
        ReleaseGpu();
        EnsureResources();
    }

    private void ReleaseGpu()
    {
        _system?.Dispose();
        _system = null;
        _renderable = null;
        _allocatedCapacity = -1;
    }

    public override void Update()
    {
        if (!_playing)
            return;
        Simulate(Time.DeltaTime);
    }

    /// <summary>Advance GPU spawn/init/update with an explicit delta. Used by the editor preview.</summary>
    public void Simulate(float dt)
    {
        if (!_playing)
            return;

        EnsureResources();
        if (_system == null)
            return;

        if (dt < 0f)
            dt = 0f;
        PulseAlwaysEvents();
        QueueCpuEvent(VfxTriggerEventOnce.Apply(ref _triggerOnceFired, TriggerEventOnceCount, Capacity));
        QueueCpuEvent(VfxTriggerEventRate.Apply(ref _triggerEventLeftover, dt, TriggerEventRate, Capacity));
        _emitAccumulator += SpawnRate * dt;
        uint spawn = (uint)Math.Min((int)_emitAccumulator, Capacity);
        _emitAccumulator -= spawn;
        Float3 emitPos = Transform.Position;
        VfxInheritVelocity.Sample(
            _hasEmitPos, _lastEmitX, _lastEmitY, _lastEmitZ,
            emitPos.X, emitPos.Y, emitPos.Z, dt,
            out float inheritVx, out float inheritVy, out float inheritVz);
        QueueCpuEvent(VfxTriggerEventOverDistance.Apply(
            ref _triggerDistanceLeftover,
            _hasEmitPos, _lastEmitX, _lastEmitY, _lastEmitZ,
            emitPos.X, emitPos.Y, emitPos.Z, TriggerEventOverDistance, Capacity));
        spawn += VfxSpawnOverDistance.Apply(
            ref _distanceLeftover,
            ref _lastEmitX, ref _lastEmitY, ref _lastEmitZ, ref _hasEmitPos,
            emitPos.X, emitPos.Y, emitPos.Z, SpawnOverDistance, Capacity);
        spawn += VfxPeriodicBurst.Apply(
            ref _periodicBurstLeftover, dt, PeriodicBurstCount, PeriodicBurstPeriod, Capacity);
        spawn += VfxGpuEvent.Consume(ref _queuedEventSpawn);
        if (spawn > (uint)Capacity)
            spawn = (uint)Capacity;
        if (_pendingBurst)
        {
            int burst = Math.Max(0, (int)BurstCount);
            spawn = (uint)Math.Min((int)spawn + burst, Capacity);
            _pendingBurst = false;
        }

        if (!TryGetComputeVariants(out var init, out var update, out var writeArgs, out var sort))
            return;

        var sim = new VfxSimParams
        {
            EmitterPosition = Transform.Position,
            StartSpeed = StartSpeed,
            Gravity = Gravity,
            Lifetime = Math.Max(0.01f, Lifetime),
            StartColor = StartColor,
            StartSize = Math.Max(0.001f, StartSize),
            CollisionMode = CollisionMode,
            CollisionBounce = CollisionBounce,
            CollisionPlane = CollisionPlane,
            SdfMode = SdfCollisionMode,
            SdfCenter = new Float4(this.SdfCenter.X, this.SdfCenter.Y, this.SdfCenter.Z, DepthCollisionMode),
            SdfSize = new Float4(this.SdfSize.X, this.SdfSize.Y, this.SdfSize.Z, SdfShape),
            TurbulenceIntensity = TurbulenceIntensity,
            TurbulenceFrequency = TurbulenceFrequency,
            EventKind = EventKind,
            EventCount = EventCount,
            ConformAttraction = ConformAttraction,
            VectorFieldIntensity = VectorFieldIntensity,
            AttributeMapMode = AttributeMapMode,
            AttributeMapScale = AttributeMapScale,
            IndexCount = PackDrawIndexCount(ResolveOutputMesh().IndexCount),
        };

        float camX = _hasSortCam ? _sortCamX : Transform.Position.X;
        float camY = _hasSortCam ? _sortCamY : Transform.Position.Y;
        float camZ = _hasSortCam ? _sortCamZ : Transform.Position.Z;

        using CommandBuffer cmd = Graphics.GetCommandBuffer("VFX.Simulate");
        Texture3D? volume = GetTexture3D(VfxExposedNames.SdfVolume) ?? SdfVolume;
        Texture3D? field = GetTexture3D(VfxExposedNames.VectorField) ?? VectorField;
        Texture2D? depth = GetTexture(VfxExposedNames.CameraDepthShader)
            ?? GetTexture(VfxExposedNames.CameraDepth)
            ?? GetTexture(VfxExposedNames.VfxCameraDepthShader)
            ?? CameraDepth;
        Texture2D? map = GetTexture(VfxExposedNames.AttributeMap)
            ?? GetTexture(VfxExposedNames.AttributeMapShader)
            ?? AttributeMap;
        _system.EncodeSimulate(
            cmd, init, update, writeArgs, dt, spawn, in sim, volume, depth, field, map, sort, camX, camY, camZ,
            inheritVelocity: InheritVelocityAmount > 0f,
            inheritVelX: inheritVx, inheritVelY: inheritVy, inheritVelZ: inheritVz);
        Graphics.Submit(cmd);
    }

    public override void OnRenderCollect(Camera camera, List<IRenderable> renderables, List<IRenderableLight> lights)
    {
        if (camera != null)
        {
            Float3 camPos = camera.Transform.Position;
            _sortCamX = camPos.X;
            _sortCamY = camPos.Y;
            _sortCamZ = camPos.Z;
            _hasSortCam = true;
        }

        if (_system == null || _renderable == null)
        {
            CollectParticleLights(lights);
            return;
        }

        Material? material = Material.Res ?? _fallbackMaterial;
        if (material == null || material.Shader.IsNotValid())
        {
            CollectParticleLights(lights);
            return;
        }

        _renderable.Configure(material, Transform.Position);
        _system.BindOutput(_renderable.Properties);
        Texture2D? main = GetTexture(VfxExposedNames.MainTexture) ?? GetTexture(VfxExposedNames.MainTexShader);
        if (main != null)
            _renderable.Properties.SetTexture(VfxExposedNames.MainTexShader, main);
        _renderable.Properties.SetFloat(VfxExposedNames.FlipbookColumnsShader, Math.Max(1f, FlipbookColumns));
        _renderable.Properties.SetFloat(VfxExposedNames.FlipbookRowsShader, Math.Max(1f, FlipbookRows));
        _renderable.Properties.SetFloat(VfxExposedNames.OutputModeShader, ResolveOutputMode());
        Float3 lightDir = LightDirection;
        Color lightColor = LightColor;
        if (!_vectorOverrides.ContainsKey(VfxExposedNames.LightDirection)
            && !_vectorOverrides.ContainsKey(VfxExposedNames.LightColor))
        {
            for (int i = 0; i < lights.Count; i++)
            {
                if (lights[i].GetLightType() != LightType.Directional)
                    continue;
                ForwardLightData data = lights[i].GetForwardLightData();
                lightDir = data.Direction;
                lightColor = new Color(data.Color.X, data.Color.Y, data.Color.Z, data.Intensity);
                break;
            }
        }
        _renderable.Properties.SetFloat(VfxExposedNames.LitModeShader, LitMode);
        _renderable.Properties.SetVector(VfxExposedNames.LightDirectionShader, new Float4(lightDir.X, lightDir.Y, lightDir.Z, 0f));
        _renderable.Properties.SetVector(VfxExposedNames.LightColorShader, new Float4(lightColor.R, lightColor.G, lightColor.B, lightColor.A));
        _renderable.Properties.SetVector(
            VfxExposedNames.AmbientColorShader,
            new Float4(AmbientColor.R, AmbientColor.G, AmbientColor.B, Shininess));
        _renderable.Properties.SetFloat(VfxExposedNames.SoftParticleFadeShader, SoftParticleFade);
        _renderable.Properties.SetFloat(VfxExposedNames.DistortionIntensityShader, DistortionIntensity);
        _renderable.Properties.SetFloat(VfxExposedNames.CameraFadeNearShader, CameraFadeNear);
        _renderable.Properties.SetFloat(VfxExposedNames.CameraFadeFarShader, CameraFadeFar);
        _renderable.Properties.SetFloat(VfxExposedNames.FlipbookBlendShader, FlipbookBlend);
        Texture2D? flipbookMotion = GetTexture(VfxExposedNames.FlipbookMotion)
            ?? GetTexture(VfxExposedNames.FlipbookMotionShader)
            ?? FlipbookMotion;
        if (flipbookMotion != null)
            _renderable.Properties.SetTexture(VfxExposedNames.FlipbookMotionShader, flipbookMotion);
        _renderable.Properties.SetFloat(VfxExposedNames.MotionVectorModeShader, MotionVectorMode);
        _renderable.Properties.SetFloat(VfxExposedNames.MotionVectorScaleShader, MotionVectorScale);
        _renderable.Properties.SetFloat(VfxExposedNames.OrientModeShader, OrientMode);
        _renderable.Properties.SetVector(VfxExposedNames.DecalPlaneShader, CollisionPlane);
        Texture2D? scene = GetTexture(VfxExposedNames.SceneColor)
            ?? GetTexture(VfxExposedNames.SceneColorShader)
            ?? SceneColor;
        if (scene != null)
            _renderable.Properties.SetTexture(VfxExposedNames.SceneColorShader, scene);
        renderables.Add(_renderable);
        CollectParticleLights(lights);
    }

    private void CollectParticleLights(List<IRenderableLight> lights)
    {
        int count = VfxParticleLight.ResolveCount(ParticleLightCount);
        if (ParticleLightIntensity <= 0f || count <= 0)
            return;
        if (_lightProxies.Length < count)
        {
            var grown = new ParticleLightProxy[count];
            Array.Copy(_lightProxies, grown, _lightProxies.Length);
            for (int i = _lightProxies.Length; i < count; i++)
                grown[i] = new ParticleLightProxy(this, i);
            _lightProxies = grown;
        }
        for (int i = 0; i < count; i++)
            lights.Add(_lightProxies[i]);
    }

    internal ForwardLightData GetParticleLightData(int slot)
    {
        Float3 emitter = Transform.Position;
        VfxParticleLight.TryEvaluate(
            (uint)slot,
            VfxParticleLight.ResolveCount(ParticleLightCount),
            emitter.X, emitter.Y, emitter.Z,
            StartSpeed,
            Gravity.X, Gravity.Y, Gravity.Z,
            Lifetime,
            StartColor.R, StartColor.G, StartColor.B,
            ParticleLightIntensity,
            ParticleLightRange,
            out float px, out float py, out float pz,
            out float cr, out float cg, out float cb,
            out float intensity, out float range);
        return new ForwardLightData
        {
            Type = LightType.Point,
            Position = new Float3(px, py, pz),
            Direction = Float3.UnitY,
            Color = new Float3(cr, cg, cb),
            Intensity = intensity,
            Range = range,
            ShadowEnabled = false,
        };
    }

    private sealed class ParticleLightProxy : IRenderableLight
    {
        private readonly VisualEffect _effect;
        private readonly int _slot;

        public ParticleLightProxy(VisualEffect effect, int slot)
        {
            _effect = effect;
            _slot = slot;
        }

        public int GetLightID() => unchecked(_effect.InstanceID * 397) ^ _slot;
        public int GetLayer() => _effect.GameObject.LayerIndex;
        public LightType GetLightType() => LightType.Point;
        public Float3 GetLightPosition() => _effect.GetParticleLightData(_slot).Position;
        public Float3 GetLightDirection() => Float3.UnitY;
        public bool DoCastShadows() => false;
        public ForwardLightData GetForwardLightData() => _effect.GetParticleLightData(_slot);
    }

    private void EnsureResources()
    {
        Mesh mesh = ResolveOutputMesh();
        int capacity = Math.Max(1, Capacity);
        if (_system == null || _allocatedCapacity != capacity)
        {
            _system?.Dispose();
            _system = VfxRuntime.DeviceSupportsCompute ? new GpuParticleSystem(capacity) : null;
            _allocatedCapacity = capacity;
            if (_system != null)
                _renderable = new GpuParticleRenderable(_system, mesh, Transform.Position, GameObject.LayerIndex);
            else
                _renderable = null;
        }
        else
        {
            _renderable?.SetMesh(mesh);
        }

        if (_shader == null || _shader.IsDisposed)
        {
            try { _shader = VfxShaderLibrary.LoadParticlesShader(); }
            catch (Exception) { _shader = null; }
        }

        if (_fallbackMaterial == null && _shader != null)
            _fallbackMaterial = new Material(_shader);
    }

    private bool TryGetComputeVariants(
        out XEngine.Runtime.RHI.Shaders.ShaderVariant init,
        out XEngine.Runtime.RHI.Shaders.ShaderVariant update,
        out XEngine.Runtime.RHI.Shaders.ShaderVariant writeArgs,
        out XEngine.Runtime.RHI.Shaders.ShaderVariant? sort)
    {
        init = null!;
        update = null!;
        writeArgs = null!;
        sort = null;
        if (_shader == null)
            return false;

        bool ok = _shader.GetPass(VfxShaderLibrary.InitPass).TryGetVariant(null, out init)
            && _shader.GetPass(VfxShaderLibrary.UpdatePass).TryGetVariant(null, out update)
            && _shader.GetPass(VfxShaderLibrary.WriteArgsPass).TryGetVariant(null, out writeArgs);
        if (ok)
            _shader.GetPass(VfxShaderLibrary.SortPass).TryGetVariant(null, out sort);
        return ok;
    }

    internal static Mesh CreateQuadMesh()
    {
        var mesh = new Mesh();
        mesh.Vertices =
        [
            new Float3(-0.5f, -0.5f, 0f),
            new Float3(0.5f, -0.5f, 0f),
            new Float3(0.5f, 0.5f, 0f),
            new Float3(-0.5f, 0.5f, 0f),
        ];
        mesh.UV =
        [
            new Float2(0f, 0f),
            new Float2(1f, 0f),
            new Float2(1f, 1f),
            new Float2(0f, 1f),
        ];
        mesh.Indices = [0, 1, 2, 0, 2, 3];
        mesh.RecalculateBounds();
        mesh.Upload();
        return mesh;
    }

    private Mesh ResolveOutputMesh()
    {
        if (_meshOverrides.TryGetValue(VfxExposedNames.OutputMesh, out Mesh? overrideMesh) && overrideMesh != null)
            return overrideMesh;
        if (ParticleMesh != null)
            return ParticleMesh;
        if (OutputMesh && !OutputStrip && !OutputDecal)
            return _cube ??= CreateCubeMesh();
        return _quad ??= CreateQuadMesh();
    }

    private float ResolveOutputMode()
    {
        if (OutputDecal)
            return VfxExposedNames.OutputModeDecal;
        if (OutputStrip)
            return VfxExposedNames.OutputModeStrip;
        if (OutputMesh)
            return VfxExposedNames.OutputModeMesh;
        return VfxExposedNames.OutputModeQuad;
    }

    private uint PackDrawIndexCount(int indexCount)
    {
        float sort = OutputStrip ? 0f : SortMode;
        return VfxSort.PackIndexCount((uint)indexCount, (uint)ResolveOutputMode(), sort);
    }

    internal static Mesh CreateCubeMesh()
    {
        Mesh mesh = Mesh.CreateCube(Float3.One);
        mesh.Upload();
        return mesh;
    }
}
