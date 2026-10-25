using System.Collections.Generic;

namespace XEngine.VFX;

/// <summary>Built-in .vfx templates: Empty, Loop, Burst, Trail, Mesh, Subgraph, Decal.</summary>
public static class VfxGraphTemplates
{
    public static VfxGraphAsset Create(VfxTemplateKind kind) => kind switch
    {
        VfxTemplateKind.Loop => CreateLoop(),
        VfxTemplateKind.Burst => CreateBurst(),
        VfxTemplateKind.Trail => CreateTrail(),
        VfxTemplateKind.Mesh => CreateMesh(),
        VfxTemplateKind.Subgraph => CreateSubgraph(),
        VfxTemplateKind.Decal => CreateDecal(),
        _ => CreateEmpty(),
    };

    public static VfxGraphAsset CreateEmpty()
    {
        var graph = NewGraph("VFX/Empty", VfxTemplateKind.Empty, 1024);
        AddStandardContexts(graph);
        return graph;
    }

    public static VfxGraphAsset CreateLoop()
    {
        var graph = NewGraph("VFX/Loop", VfxTemplateKind.Loop, 4096);
        AddStandardContexts(graph);
        AddBlock(graph, "b_rate", VfxNodeLibrary.Spawn, VfxNodeLibrary.ConstantSpawnRate, 0, Field("Rate", "64"));
        AddSpawnOverDistanceBlock(graph);
        AddPeriodicBurstBlock(graph);
        AddInitializeDefaults(graph, speed: "2", size: "0.15", lifetime: "2");
        AddBlock(graph, "b_gravity", VfxNodeLibrary.Update, VfxNodeLibrary.Gravity, 0,
            Field("X", "0"), Field("Y", "-1.5"), Field("Z", "0"));
        AddBlock(graph, "b_collide", VfxNodeLibrary.Update, VfxNodeLibrary.CollideWithPlane, 1,
            Field("Mode", "2"), Field("Bounce", "0.4"),
            Field("Nx", "0"), Field("Ny", "1"), Field("Nz", "0"), Field("Distance", "0"));
        AddSdfBlock(graph, mode: "2");
        AddTurbulenceBlock(graph, intensity: "1.2", frequency: "1.25");
        AddTriggerEventBlock(graph, kind: "1", count: "2");
        AddDepthBlock(graph);
        AddConformBlock(graph);
        AddVectorFieldBlock(graph);
        AddColorOverLifeBlock(graph);
        AddSizeOverLifeBlock(graph);
        AddLinearDragBlock(graph);
        AddForceBlock(graph);
        AddAngularVelocityBlock(graph);
        AddCustomHlslBlock(graph);
        AddGpuEventTargetBlock(graph);
        AddTriggerEventRateBlock(graph);
        AddTriggerEventOverDistanceBlock(graph);
        AddSizeOverSpeedBlock(graph);
        AddColorOverSpeedBlock(graph);
        AddTriggerEventOnceBlock(graph);
        AddCollisionFrictionBlock(graph);
        AddCollisionElasticityBlock(graph);
        AddCollisionLifetimeLossBlock(graph);
        AddLimitVelocityBlock(graph);
        AddKillSphereBlock(graph);
        AddKillAABoxBlock(graph);
        AddAttractBlock(graph);
        AddKillPlaneBlock(graph);
        AddConformToSphereBlock(graph);
        AddVortexBlock(graph);
        AddConformToCylinderBlock(graph);
        AddMultiplySizeBlock(graph);
        AddMultiplyColorBlock(graph);
        AddAddSizeBlock(graph);
        AddAddColorBlock(graph);
        AddAddVelocityBlock(graph);
        AddMultiplyVelocityBlock(graph);
        AddOverwriteSizeBlock(graph);
        AddOverwriteColorBlock(graph);
        AddOverwriteVelocityBlock(graph);
        AddOverwriteLifetimeBlock(graph);
        AddAddLifetimeBlock(graph);
        AddMultiplyLifetimeBlock(graph);
        AddAddAgeBlock(graph);
        AddMultiplyAgeBlock(graph);
        AddSetAgeBlock(graph);
        AddAddPositionBlock(graph);
        AddSetPositionBlock(graph);
        AddBlock(graph, "b_orient", VfxNodeLibrary.OutputParticleQuad, VfxNodeLibrary.OrientToView, 0);
        AddDistortBlock(graph, VfxNodeLibrary.OutputParticleQuad);
        AddParticleLightBlock(graph, VfxNodeLibrary.OutputParticleQuad);
        AddSortBlock(graph, VfxNodeLibrary.OutputParticleQuad);
        AddCameraFadeBlock(graph, VfxNodeLibrary.OutputParticleQuad);
        AddFlipbookBlendBlock(graph, VfxNodeLibrary.OutputParticleQuad);
        AddMotionVectorBlock(graph, VfxNodeLibrary.OutputParticleQuad);
        AddOrientAlongVelocityBlock(graph, VfxNodeLibrary.OutputParticleQuad);
        AddOrientFixedAxisBlock(graph, VfxNodeLibrary.OutputParticleQuad);
        AddFlipbookMotionBlock(graph, VfxNodeLibrary.OutputParticleQuad);
        AddScreenSpaceSizeBlock(graph, VfxNodeLibrary.OutputParticleQuad);
        AddOrientLookAtBlock(graph, VfxNodeLibrary.OutputParticleQuad);
        AddFlipbookRateBlock(graph, VfxNodeLibrary.OutputParticleQuad);
        AddFlipbookStartBlock(graph, VfxNodeLibrary.OutputParticleQuad);
        AddColorOperator(graph, "n_color", 40f, 160f, "1", "1", "1", "1");
        graph.Edges.Add(new VfxEdge
        {
            Id = "e_color",
            FromNode = "n_color",
            FromPort = "Out",
            ToNode = "b_color",
            ToPort = "Color",
        });
        AddStandardExposed(graph, spawnRate: "64", burst: "0", gravity: "0,-1.5,0", color: "1,1,1,1",
            collisionMode: "2", sdfMode: "2", sdfShape: "1", turbulenceIntensity: "1.2", turbulenceFrequency: "1.25",
            softParticleFade: "1", eventKind: "1", eventCount: "2");
        return graph;
    }

    public static VfxGraphAsset CreateBurst()
    {
        var graph = NewGraph("VFX/Burst", VfxTemplateKind.Burst, 4096);
        AddStandardContexts(graph);
        AddBlock(graph, "b_burst", VfxNodeLibrary.Spawn, VfxNodeLibrary.Burst, 0, Field("Count", "256"));
        AddSpawnOverDistanceBlock(graph);
        AddPeriodicBurstBlock(graph);
        AddInitializeDefaults(graph, speed: "3", size: "0.2", lifetime: "1.5");
        AddBlock(graph, "b_gravity", VfxNodeLibrary.Update, VfxNodeLibrary.Gravity, 0,
            Field("X", "0"), Field("Y", "-2"), Field("Z", "0"));
        AddBlock(graph, "b_collide", VfxNodeLibrary.Update, VfxNodeLibrary.CollideWithPlane, 1,
            Field("Mode", "1"), Field("Bounce", "0"),
            Field("Nx", "0"), Field("Ny", "1"), Field("Nz", "0"), Field("Distance", "0"));
        AddSdfBlock(graph, mode: "1");
        AddTurbulenceBlock(graph, intensity: "0.6", frequency: "1");
        AddTriggerEventBlock(graph, kind: "1", count: "3");
        AddDepthBlock(graph);
        AddConformBlock(graph);
        AddVectorFieldBlock(graph);
        AddColorOverLifeBlock(graph, "1", "0.85", "0.4", "1", "1", "0.85", "0.4", "0");
        AddSizeOverLifeBlock(graph);
        AddLinearDragBlock(graph);
        AddForceBlock(graph);
        AddAngularVelocityBlock(graph);
        AddCustomHlslBlock(graph);
        AddGpuEventTargetBlock(graph);
        AddTriggerEventRateBlock(graph);
        AddTriggerEventOverDistanceBlock(graph);
        AddSizeOverSpeedBlock(graph);
        AddColorOverSpeedBlock(graph);
        AddTriggerEventOnceBlock(graph);
        AddCollisionFrictionBlock(graph);
        AddCollisionElasticityBlock(graph);
        AddCollisionLifetimeLossBlock(graph);
        AddLimitVelocityBlock(graph);
        AddKillSphereBlock(graph);
        AddKillAABoxBlock(graph);
        AddAttractBlock(graph);
        AddKillPlaneBlock(graph);
        AddConformToSphereBlock(graph);
        AddVortexBlock(graph);
        AddConformToCylinderBlock(graph);
        AddMultiplySizeBlock(graph);
        AddMultiplyColorBlock(graph);
        AddAddSizeBlock(graph);
        AddAddColorBlock(graph);
        AddAddVelocityBlock(graph);
        AddMultiplyVelocityBlock(graph);
        AddOverwriteSizeBlock(graph);
        AddOverwriteColorBlock(graph);
        AddOverwriteVelocityBlock(graph);
        AddOverwriteLifetimeBlock(graph);
        AddAddLifetimeBlock(graph);
        AddMultiplyLifetimeBlock(graph);
        AddAddAgeBlock(graph);
        AddMultiplyAgeBlock(graph);
        AddSetAgeBlock(graph);
        AddAddPositionBlock(graph);
        AddSetPositionBlock(graph);
        AddBlock(graph, "b_orient", VfxNodeLibrary.OutputParticleQuad, VfxNodeLibrary.OrientToView, 0);
        AddDistortBlock(graph, VfxNodeLibrary.OutputParticleQuad);
        AddParticleLightBlock(graph, VfxNodeLibrary.OutputParticleQuad);
        AddSortBlock(graph, VfxNodeLibrary.OutputParticleQuad);
        AddCameraFadeBlock(graph, VfxNodeLibrary.OutputParticleQuad);
        AddFlipbookBlendBlock(graph, VfxNodeLibrary.OutputParticleQuad);
        AddMotionVectorBlock(graph, VfxNodeLibrary.OutputParticleQuad);
        AddOrientAlongVelocityBlock(graph, VfxNodeLibrary.OutputParticleQuad);
        AddOrientFixedAxisBlock(graph, VfxNodeLibrary.OutputParticleQuad);
        AddFlipbookMotionBlock(graph, VfxNodeLibrary.OutputParticleQuad);
        AddScreenSpaceSizeBlock(graph, VfxNodeLibrary.OutputParticleQuad);
        AddOrientLookAtBlock(graph, VfxNodeLibrary.OutputParticleQuad);
        AddFlipbookRateBlock(graph, VfxNodeLibrary.OutputParticleQuad);
        AddFlipbookStartBlock(graph, VfxNodeLibrary.OutputParticleQuad);
        AddColorOperator(graph, "n_color", 40f, 160f, "1", "0.85", "0.4", "1");
        graph.Edges.Add(new VfxEdge
        {
            Id = "e_color",
            FromNode = "n_color",
            FromPort = "Out",
            ToNode = "b_color",
            ToPort = "Color",
        });
        AddStandardExposed(graph, spawnRate: "0", burst: "256", gravity: "0,-2,0", color: "1,0.85,0.4,1",
            collisionMode: "1", collisionBounce: "0", sdfMode: "1", sdfShape: "1",
            turbulenceIntensity: "0.6", turbulenceFrequency: "1", softParticleFade: "0.75",
            eventKind: "1", eventCount: "3");
        return graph;
    }

    public static VfxGraphAsset CreateTrail()
    {
        var graph = NewGraph("VFX/Trail", VfxTemplateKind.Trail, 8192);
        AddStandardContexts(graph, VfxNodeLibrary.OutputParticleStrip);
        AddBlock(graph, "b_rate", VfxNodeLibrary.Spawn, VfxNodeLibrary.ConstantSpawnRate, 0, Field("Rate", "200"));
        AddSpawnOverDistanceBlock(graph);
        AddPeriodicBurstBlock(graph);
        AddInitializeDefaults(graph, speed: "4", size: "0.05", lifetime: "0.4");
        AddBlock(graph, "b_overlife", VfxNodeLibrary.Update, VfxNodeLibrary.ColorOverLife, 0,
            Field("FromR", "1"), Field("FromG", "1"), Field("FromB", "1"), Field("FromA", "1"),
            Field("ToR", "1"), Field("ToG", "1"), Field("ToB", "1"), Field("ToA", "0"));
        AddTurbulenceBlock(graph, intensity: "2", frequency: "2", order: 1);
        AddTriggerEventBlock(graph, kind: "1", count: "1", order: 2);
        AddDepthBlock(graph, order: 3);
        AddConformBlock(graph, order: 4);
        AddVectorFieldBlock(graph, order: 5);
        AddSizeOverLifeBlock(graph, order: 6);
        AddLinearDragBlock(graph, order: 7);
        AddForceBlock(graph, order: 8);
        AddAngularVelocityBlock(graph, order: 9);
        AddCustomHlslBlock(graph, order: 10);
        AddGpuEventTargetBlock(graph, order: 11);
        AddTriggerEventRateBlock(graph, order: 12);
        AddTriggerEventOverDistanceBlock(graph, order: 13);
        AddSizeOverSpeedBlock(graph, order: 14);
        AddColorOverSpeedBlock(graph, order: 15);
        AddTriggerEventOnceBlock(graph, order: 16);
        AddCollisionFrictionBlock(graph, order: 17);
        AddCollisionElasticityBlock(graph, order: 18);
        AddCollisionLifetimeLossBlock(graph, order: 19);
        AddLimitVelocityBlock(graph, order: 20);
        AddKillSphereBlock(graph, order: 21);
        AddKillAABoxBlock(graph, order: 22);
        AddAttractBlock(graph, order: 23);
        AddKillPlaneBlock(graph, order: 24);
        AddConformToSphereBlock(graph, order: 25);
        AddVortexBlock(graph, order: 26);
        AddConformToCylinderBlock(graph, order: 27);
        AddMultiplySizeBlock(graph, order: 28);
        AddMultiplyColorBlock(graph, order: 29);
        AddAddSizeBlock(graph, order: 30);
        AddAddColorBlock(graph, order: 31);
        AddAddVelocityBlock(graph, order: 32);
        AddMultiplyVelocityBlock(graph, order: 33);
        AddOverwriteSizeBlock(graph, order: 34);
        AddOverwriteColorBlock(graph, order: 35);
        AddOverwriteVelocityBlock(graph, order: 36);
        AddOverwriteLifetimeBlock(graph, order: 37);
        AddAddLifetimeBlock(graph, order: 38);
        AddMultiplyLifetimeBlock(graph, order: 39);
        AddAddAgeBlock(graph, order: 40);
        AddMultiplyAgeBlock(graph, order: 41);
        AddSetAgeBlock(graph, order: 42);
        AddAddPositionBlock(graph, order: 43);
        AddSetPositionBlock(graph, order: 44);
        AddBlock(graph, "b_orient", VfxNodeLibrary.OutputParticleStrip, VfxNodeLibrary.OrientToView, 0);
        AddDistortBlock(graph, VfxNodeLibrary.OutputParticleStrip);
        AddParticleLightBlock(graph, VfxNodeLibrary.OutputParticleStrip);
        AddCameraFadeBlock(graph, VfxNodeLibrary.OutputParticleStrip, order: 3);
        AddFlipbookBlendBlock(graph, VfxNodeLibrary.OutputParticleStrip, order: 4);
        AddMotionVectorBlock(graph, VfxNodeLibrary.OutputParticleStrip, order: 5);
        AddOrientAlongVelocityBlock(graph, VfxNodeLibrary.OutputParticleStrip, order: 6);
        AddOrientFixedAxisBlock(graph, VfxNodeLibrary.OutputParticleStrip, order: 7);
        AddFlipbookMotionBlock(graph, VfxNodeLibrary.OutputParticleStrip, order: 8);
        AddScreenSpaceSizeBlock(graph, VfxNodeLibrary.OutputParticleStrip, order: 9);
        AddOrientLookAtBlock(graph, VfxNodeLibrary.OutputParticleStrip, order: 10);
        AddFlipbookRateBlock(graph, VfxNodeLibrary.OutputParticleStrip, order: 11);
        AddFlipbookStartBlock(graph, VfxNodeLibrary.OutputParticleStrip, order: 12);
        AddColorOperator(graph, "n_color", 40f, 160f, "1", "1", "1", "1");
        graph.Edges.Add(new VfxEdge
        {
            Id = "e_color",
            FromNode = "n_color",
            FromPort = "Out",
            ToNode = "b_color",
            ToPort = "Color",
        });
        AddStandardExposed(graph, spawnRate: "200", burst: "0", gravity: "0,0,0", color: "1,1,1,1",
            turbulenceIntensity: "2", turbulenceFrequency: "2", softParticleFade: "0.4",
            eventKind: "1", eventCount: "1");
        return graph;
    }

    public static VfxGraphAsset CreateMesh()
    {
        var graph = NewGraph("VFX/Mesh", VfxTemplateKind.Mesh, 4096);
        AddStandardContexts(graph, VfxNodeLibrary.OutputParticleMesh);
        AddBlock(graph, "b_rate", VfxNodeLibrary.Spawn, VfxNodeLibrary.ConstantSpawnRate, 0, Field("Rate", "32"));
        AddSpawnOverDistanceBlock(graph);
        AddPeriodicBurstBlock(graph);
        AddInitializeDefaults(graph, speed: "1.5", size: "0.2", lifetime: "2");
        AddBlock(graph, "b_gravity", VfxNodeLibrary.Update, VfxNodeLibrary.Gravity, 0,
            Field("X", "0"), Field("Y", "-1.5"), Field("Z", "0"));
        AddBlock(graph, "b_collide", VfxNodeLibrary.Update, VfxNodeLibrary.CollideWithPlane, 1,
            Field("Mode", "2"), Field("Bounce", "0.4"),
            Field("Nx", "0"), Field("Ny", "1"), Field("Nz", "0"), Field("Distance", "0"));
        AddSdfBlock(graph, mode: "2");
        AddTurbulenceBlock(graph, intensity: "0.8", frequency: "1.25");
        AddTriggerEventBlock(graph, kind: "1", count: "2");
        AddDepthBlock(graph);
        AddConformBlock(graph);
        AddVectorFieldBlock(graph);
        AddColorOverLifeBlock(graph, "0.85", "0.95", "1", "1", "0.85", "0.95", "1", "0");
        AddSizeOverLifeBlock(graph);
        AddLinearDragBlock(graph);
        AddForceBlock(graph);
        AddAngularVelocityBlock(graph);
        AddCustomHlslBlock(graph);
        AddGpuEventTargetBlock(graph);
        AddTriggerEventRateBlock(graph);
        AddTriggerEventOverDistanceBlock(graph);
        AddSizeOverSpeedBlock(graph);
        AddColorOverSpeedBlock(graph);
        AddTriggerEventOnceBlock(graph);
        AddCollisionFrictionBlock(graph);
        AddCollisionElasticityBlock(graph);
        AddCollisionLifetimeLossBlock(graph);
        AddLimitVelocityBlock(graph);
        AddKillSphereBlock(graph);
        AddKillAABoxBlock(graph);
        AddAttractBlock(graph);
        AddKillPlaneBlock(graph);
        AddConformToSphereBlock(graph);
        AddVortexBlock(graph);
        AddConformToCylinderBlock(graph);
        AddMultiplySizeBlock(graph);
        AddMultiplyColorBlock(graph);
        AddAddSizeBlock(graph);
        AddAddColorBlock(graph);
        AddAddVelocityBlock(graph);
        AddMultiplyVelocityBlock(graph);
        AddOverwriteSizeBlock(graph);
        AddOverwriteColorBlock(graph);
        AddOverwriteVelocityBlock(graph);
        AddOverwriteLifetimeBlock(graph);
        AddAddLifetimeBlock(graph);
        AddMultiplyLifetimeBlock(graph);
        AddAddAgeBlock(graph);
        AddMultiplyAgeBlock(graph);
        AddSetAgeBlock(graph);
        AddAddPositionBlock(graph);
        AddSetPositionBlock(graph);
        AddDistortBlock(graph, VfxNodeLibrary.OutputParticleMesh, order: 0);
        AddParticleLightBlock(graph, VfxNodeLibrary.OutputParticleMesh, order: 1);
        AddSortBlock(graph, VfxNodeLibrary.OutputParticleMesh, order: 2);
        AddCameraFadeBlock(graph, VfxNodeLibrary.OutputParticleMesh, order: 3);
        AddFlipbookBlendBlock(graph, VfxNodeLibrary.OutputParticleMesh, order: 4);
        AddMotionVectorBlock(graph, VfxNodeLibrary.OutputParticleMesh, order: 5);
        AddOrientAlongVelocityBlock(graph, VfxNodeLibrary.OutputParticleMesh, order: 6);
        AddOrientFixedAxisBlock(graph, VfxNodeLibrary.OutputParticleMesh, order: 7);
        AddFlipbookMotionBlock(graph, VfxNodeLibrary.OutputParticleMesh, order: 8);
        AddScreenSpaceSizeBlock(graph, VfxNodeLibrary.OutputParticleMesh, order: 9);
        AddOrientLookAtBlock(graph, VfxNodeLibrary.OutputParticleMesh, order: 10);
        AddFlipbookRateBlock(graph, VfxNodeLibrary.OutputParticleMesh, order: 11);
        AddFlipbookStartBlock(graph, VfxNodeLibrary.OutputParticleMesh, order: 12);
        AddColorOperator(graph, "n_color", 40f, 160f, "0.85", "0.95", "1", "1");
        graph.Edges.Add(new VfxEdge
        {
            Id = "e_color",
            FromNode = "n_color",
            FromPort = "Out",
            ToNode = "b_color",
            ToPort = "Color",
        });
        AddStandardExposed(graph, spawnRate: "32", burst: "0", gravity: "0,-1.5,0", color: "0.85,0.95,1,1",
            collisionMode: "2", sdfMode: "2", sdfShape: "1", turbulenceIntensity: "0.8", turbulenceFrequency: "1.25",
            softParticleFade: "0.5", eventKind: "1", eventCount: "2");
        graph.AddProperty(VfxExposedNames.OutputMesh, VfxValueType.Mesh, "");
        graph.FindProperty(VfxExposedNames.LitMode)!.DefaultValue = "1";
        return graph;
    }

    public static VfxGraphAsset CreateSubgraph()
    {
        VfxGraphAsset graph = CreateLoop();
        graph.GraphName = "VFX/Subgraph";
        graph.Template = VfxTemplateKind.Subgraph;
        var include = new VfxOperator
        {
            Id = "n_sub",
            Type = VfxNodeLibrary.Subgraph,
            PosX = 40f,
            PosY = 320f,
        };
        include.Fields.Add(Field(VfxExposedNames.SubgraphPath, "Trail"));
        graph.Operators.Add(include);
        return graph;
    }

    public static VfxGraphAsset CreateDecal()
    {
        var graph = NewGraph("VFX/Decal", VfxTemplateKind.Decal, 4096);
        AddStandardContexts(graph, VfxNodeLibrary.OutputParticleDecal);
        AddBlock(graph, "b_rate", VfxNodeLibrary.Spawn, VfxNodeLibrary.ConstantSpawnRate, 0, Field("Rate", "64"));
        AddSpawnOverDistanceBlock(graph);
        AddPeriodicBurstBlock(graph);
        AddInitializeDefaults(graph, speed: "2", size: "0.15", lifetime: "2");
        AddBlock(graph, "b_gravity", VfxNodeLibrary.Update, VfxNodeLibrary.Gravity, 0,
            Field("X", "0"), Field("Y", "-1.5"), Field("Z", "0"));
        AddBlock(graph, "b_collide", VfxNodeLibrary.Update, VfxNodeLibrary.CollideWithPlane, 1,
            Field("Mode", "2"), Field("Bounce", "0.4"),
            Field("Nx", "0"), Field("Ny", "1"), Field("Nz", "0"), Field("Distance", "0"));
        AddSdfBlock(graph, mode: "2");
        AddTurbulenceBlock(graph, intensity: "1.2", frequency: "1.25");
        AddTriggerEventBlock(graph, kind: "1", count: "2");
        AddDepthBlock(graph);
        AddConformBlock(graph);
        AddVectorFieldBlock(graph);
        AddColorOverLifeBlock(graph, "1", "0.45", "0.18", "1", "1", "0.45", "0.18", "0");
        AddSizeOverLifeBlock(graph);
        AddLinearDragBlock(graph);
        AddForceBlock(graph);
        AddAngularVelocityBlock(graph);
        AddCustomHlslBlock(graph);
        AddGpuEventTargetBlock(graph);
        AddTriggerEventRateBlock(graph);
        AddTriggerEventOverDistanceBlock(graph);
        AddSizeOverSpeedBlock(graph);
        AddColorOverSpeedBlock(graph);
        AddTriggerEventOnceBlock(graph);
        AddCollisionFrictionBlock(graph);
        AddCollisionElasticityBlock(graph);
        AddCollisionLifetimeLossBlock(graph);
        AddLimitVelocityBlock(graph);
        AddKillSphereBlock(graph);
        AddKillAABoxBlock(graph);
        AddAttractBlock(graph);
        AddKillPlaneBlock(graph);
        AddConformToSphereBlock(graph);
        AddVortexBlock(graph);
        AddConformToCylinderBlock(graph);
        AddMultiplySizeBlock(graph);
        AddMultiplyColorBlock(graph);
        AddAddSizeBlock(graph);
        AddAddColorBlock(graph);
        AddAddVelocityBlock(graph);
        AddMultiplyVelocityBlock(graph);
        AddOverwriteSizeBlock(graph);
        AddOverwriteColorBlock(graph);
        AddOverwriteVelocityBlock(graph);
        AddOverwriteLifetimeBlock(graph);
        AddAddLifetimeBlock(graph);
        AddMultiplyLifetimeBlock(graph);
        AddAddAgeBlock(graph);
        AddMultiplyAgeBlock(graph);
        AddSetAgeBlock(graph);
        AddAddPositionBlock(graph);
        AddSetPositionBlock(graph);
        AddDistortBlock(graph, VfxNodeLibrary.OutputParticleDecal, order: 0);
        AddParticleLightBlock(graph, VfxNodeLibrary.OutputParticleDecal, order: 1);
        AddSortBlock(graph, VfxNodeLibrary.OutputParticleDecal, order: 2);
        AddCameraFadeBlock(graph, VfxNodeLibrary.OutputParticleDecal, order: 3);
        AddFlipbookBlendBlock(graph, VfxNodeLibrary.OutputParticleDecal, order: 4);
        AddMotionVectorBlock(graph, VfxNodeLibrary.OutputParticleDecal, order: 5);
        AddFlipbookMotionBlock(graph, VfxNodeLibrary.OutputParticleDecal, order: 6);
        AddScreenSpaceSizeBlock(graph, VfxNodeLibrary.OutputParticleDecal, order: 7);
        AddFlipbookRateBlock(graph, VfxNodeLibrary.OutputParticleDecal, order: 8);
        AddFlipbookStartBlock(graph, VfxNodeLibrary.OutputParticleDecal, order: 9);
        AddColorOperator(graph, "n_color", 40f, 160f, "1", "0.45", "0.18", "1");
        graph.Edges.Add(new VfxEdge
        {
            Id = "e_color",
            FromNode = "n_color",
            FromPort = "Out",
            ToNode = "b_color",
            ToPort = "Color",
        });
        AddStandardExposed(graph, spawnRate: "64", burst: "0", gravity: "0,-1.5,0", color: "1,0.45,0.18,1",
            collisionMode: "2", sdfMode: "2", sdfShape: "1", turbulenceIntensity: "1.2", turbulenceFrequency: "1.25",
            softParticleFade: "1", eventKind: "1", eventCount: "2");
        return graph;
    }

    private static VfxGraphAsset NewGraph(string name, VfxTemplateKind kind, int capacity)
    {
        return new VfxGraphAsset
        {
            GraphName = name,
            Template = kind,
            Capacity = capacity,
        };
    }

    private static void AddStandardContexts(
        VfxGraphAsset graph, string outputType = VfxNodeLibrary.OutputParticleQuad)
    {
        graph.Contexts.Add(new VfxContext { Id = "ctx_spawn", Type = VfxNodeLibrary.Spawn, PosX = 240f, PosY = 0f });
        graph.Contexts.Add(new VfxContext { Id = "ctx_init", Type = VfxNodeLibrary.Initialize, PosX = 240f, PosY = 160f });
        graph.Contexts.Add(new VfxContext { Id = "ctx_update", Type = VfxNodeLibrary.Update, PosX = 240f, PosY = 320f });
        graph.Contexts.Add(new VfxContext { Id = "ctx_output", Type = outputType, PosX = 240f, PosY = 480f });
        graph.Edges.Add(Flow("e_flow0", "ctx_spawn", "ctx_init"));
        graph.Edges.Add(Flow("e_flow1", "ctx_init", "ctx_update"));
        graph.Edges.Add(Flow("e_flow2", "ctx_update", "ctx_output"));
    }

    private static void AddStandardExposed(
        VfxGraphAsset graph, string spawnRate, string burst, string gravity, string color,
        string collisionMode = "0", string collisionBounce = "0.4", string collisionPlane = "0,1,0,0",
        string sdfMode = "0", string sdfShape = "0", string sdfCenter = "0,0.55,0", string sdfSize = "0.4,0.4,0.4",
        string turbulenceIntensity = "0", string turbulenceFrequency = "1",
        string softParticleFade = "0", string eventKind = "0", string eventCount = "1",
        string depthCollisionMode = "0", string conformAttraction = "0",
        string vectorFieldIntensity = "0")
    {
        graph.AddProperty(VfxExposedNames.SpawnRate, VfxValueType.Float, spawnRate);
        graph.AddProperty(VfxExposedNames.BurstCount, VfxValueType.Float, burst);
        graph.AddProperty(VfxExposedNames.SpawnOverDistance, VfxValueType.Float, "0");
        graph.AddProperty(VfxExposedNames.Gravity, VfxValueType.Float3, gravity);
        graph.AddProperty(VfxExposedNames.StartColor, VfxValueType.Color, color);
        graph.AddProperty(VfxExposedNames.MainTexture, VfxValueType.Texture, "");
        graph.AddProperty(VfxExposedNames.FlipbookColumns, VfxValueType.Float, "1");
        graph.AddProperty(VfxExposedNames.FlipbookRows, VfxValueType.Float, "1");
        graph.AddProperty(VfxExposedNames.CollisionMode, VfxValueType.Float, collisionMode);
        graph.AddProperty(VfxExposedNames.CollisionBounce, VfxValueType.Float, collisionBounce);
        graph.AddProperty(VfxExposedNames.CollisionPlane, VfxValueType.Float4, collisionPlane);
        graph.AddProperty(VfxExposedNames.SdfCollisionMode, VfxValueType.Float, sdfMode);
        graph.AddProperty(VfxExposedNames.SdfShape, VfxValueType.Float, sdfShape);
        graph.AddProperty(VfxExposedNames.SdfCenter, VfxValueType.Float3, sdfCenter);
        graph.AddProperty(VfxExposedNames.SdfSize, VfxValueType.Float3, sdfSize);
        graph.AddProperty(VfxExposedNames.SdfVolume, VfxValueType.Texture3D, "");
        graph.AddProperty(VfxExposedNames.TurbulenceIntensity, VfxValueType.Float, turbulenceIntensity);
        graph.AddProperty(VfxExposedNames.TurbulenceFrequency, VfxValueType.Float, turbulenceFrequency);
        graph.AddProperty(VfxExposedNames.LitMode, VfxValueType.Float, "0");
        graph.AddProperty(VfxExposedNames.LightDirection, VfxValueType.Float3, "0.35,0.85,0.4");
        graph.AddProperty(VfxExposedNames.LightColor, VfxValueType.Color, "1,1,1,1");
        graph.AddProperty(VfxExposedNames.AmbientColor, VfxValueType.Color, "0.18,0.18,0.22,1");
        graph.AddProperty(VfxExposedNames.Shininess, VfxValueType.Float, "32");
        graph.AddProperty(VfxExposedNames.SoftParticleFade, VfxValueType.Float, softParticleFade);
        graph.AddProperty(VfxExposedNames.EventKind, VfxValueType.Float, eventKind);
        graph.AddProperty(VfxExposedNames.EventCount, VfxValueType.Float, eventCount);
        graph.AddProperty(VfxExposedNames.DepthCollisionMode, VfxValueType.Float, depthCollisionMode);
        graph.AddProperty(VfxExposedNames.ConformAttraction, VfxValueType.Float, conformAttraction);
        graph.AddProperty(VfxExposedNames.VectorFieldIntensity, VfxValueType.Float, vectorFieldIntensity);
        graph.AddProperty(VfxExposedNames.VectorField, VfxValueType.Texture3D, "");
        graph.AddProperty(VfxExposedNames.AttributeMapMode, VfxValueType.Float, "0");
        graph.AddProperty(VfxExposedNames.AttributeMapScale, VfxValueType.Float, "1");
        graph.AddProperty(VfxExposedNames.AttributeMap, VfxValueType.Texture, "");
        graph.AddProperty(VfxExposedNames.DistortionIntensity, VfxValueType.Float, "0");
        graph.AddProperty(VfxExposedNames.ParticleLightIntensity, VfxValueType.Float, "0");
        graph.AddProperty(VfxExposedNames.ParticleLightRange, VfxValueType.Float, "1.5");
        graph.AddProperty(VfxExposedNames.ParticleLightCount, VfxValueType.Float, "0");
        graph.AddProperty(VfxExposedNames.SortMode, VfxValueType.Float, "0");
        graph.AddProperty(VfxExposedNames.CameraFadeNear, VfxValueType.Float, "0");
        graph.AddProperty(VfxExposedNames.CameraFadeFar, VfxValueType.Float, "0");
        graph.AddProperty(VfxExposedNames.FlipbookBlend, VfxValueType.Float, "0");
        graph.AddProperty(VfxExposedNames.MotionVectorMode, VfxValueType.Float, "0");
        graph.AddProperty(VfxExposedNames.MotionVectorScale, VfxValueType.Float, "1");
        graph.AddProperty(VfxExposedNames.OrientMode, VfxValueType.Float, "0");
    }

    private static void AddSdfBlock(VfxGraphAsset graph, string mode)
    {
        AddBlock(graph, "b_sdf", VfxNodeLibrary.Update, VfxNodeLibrary.CollideWithSDF, 2,
            Field("Mode", mode), Field("Shape", "1"),
            Field("Cx", "0"), Field("Cy", "0.55"), Field("Cz", "0"),
            Field("Sx", "0.4"), Field("Sy", "0.4"), Field("Sz", "0.4"));
    }

    private static void AddTurbulenceBlock(
        VfxGraphAsset graph, string intensity, string frequency, int order = 3)
    {
        AddBlock(graph, "b_turb", VfxNodeLibrary.Update, VfxNodeLibrary.Turbulence, order,
            Field("Intensity", intensity), Field("Frequency", frequency));
    }

    private static void AddTriggerEventBlock(
        VfxGraphAsset graph, string kind, string count, int order = 4)
    {
        AddBlock(graph, "b_event", VfxNodeLibrary.Update, VfxNodeLibrary.TriggerEvent, order,
            Field("Kind", kind), Field("Count", count));
    }

    private static void AddGpuEventTargetBlock(VfxGraphAsset graph, string name = "", int order = 14)
    {
        AddBlock(graph, "b_eventtgt", VfxNodeLibrary.Update, VfxNodeLibrary.GPUEventTarget, order,
            Field("Name", name));
    }

    private static void AddTriggerEventRateBlock(VfxGraphAsset graph, string rate = "0", int order = 15)
    {
        AddBlock(graph, "b_trigrate", VfxNodeLibrary.Update, VfxNodeLibrary.TriggerEventRate, order,
            Field("Rate", rate));
    }

    private static void AddTriggerEventOverDistanceBlock(VfxGraphAsset graph, string rate = "0", int order = 16)
    {
        AddBlock(graph, "b_trigdist", VfxNodeLibrary.Update, VfxNodeLibrary.TriggerEventOverDistance, order,
            Field("Rate", rate));
    }

    private static void AddSizeOverSpeedBlock(VfxGraphAsset graph, string amount = "0", int order = 17)
    {
        AddBlock(graph, "b_sizespeed", VfxNodeLibrary.Update, VfxNodeLibrary.SizeOverSpeed, order,
            Field("Amount", amount));
    }

    private static void AddCollisionFrictionBlock(VfxGraphAsset graph, string amount = "0", int order = 20)
    {
        AddBlock(graph, "b_friction", VfxNodeLibrary.Update, VfxNodeLibrary.CollisionFriction, order,
            Field("Amount", amount));
    }

    private static void AddCollisionElasticityBlock(VfxGraphAsset graph, string amount = "0", int order = 21)
    {
        AddBlock(graph, "b_elastic", VfxNodeLibrary.Update, VfxNodeLibrary.CollisionElasticity, order,
            Field("Amount", amount));
    }

    private static void AddCollisionLifetimeLossBlock(VfxGraphAsset graph, string amount = "0", int order = 22)
    {
        AddBlock(graph, "b_lifeloss", VfxNodeLibrary.Update, VfxNodeLibrary.CollisionLifetimeLoss, order,
            Field("Amount", amount));
    }

    private static void AddLimitVelocityBlock(VfxGraphAsset graph, string speed = "0", int order = 23)
    {
        AddBlock(graph, "b_limitvel", VfxNodeLibrary.Update, VfxNodeLibrary.LimitVelocity, order,
            Field("Speed", speed));
    }

    private static void AddKillSphereBlock(
        VfxGraphAsset graph,
        string x = "0", string y = "0", string z = "0", string radius = "0",
        int order = 24)
    {
        AddBlock(graph, "b_killsphere", VfxNodeLibrary.Update, VfxNodeLibrary.KillSphere, order,
            Field("X", x), Field("Y", y), Field("Z", z), Field("Radius", radius));
    }

    private static void AddKillAABoxBlock(
        VfxGraphAsset graph,
        string x = "0", string y = "0", string z = "0",
        string sx = "0", string sy = "0", string sz = "0",
        int order = 25)
    {
        AddBlock(graph, "b_killbox", VfxNodeLibrary.Update, VfxNodeLibrary.KillAABox, order,
            Field("X", x), Field("Y", y), Field("Z", z),
            Field("Sx", sx), Field("Sy", sy), Field("Sz", sz));
    }

    private static void AddAttractBlock(
        VfxGraphAsset graph,
        string x = "0", string y = "0", string z = "0", string strength = "0",
        int order = 26)
    {
        AddBlock(graph, "b_attract", VfxNodeLibrary.Update, VfxNodeLibrary.Attract, order,
            Field("X", x), Field("Y", y), Field("Z", z), Field("Strength", strength));
    }

    private static void AddKillPlaneBlock(
        VfxGraphAsset graph,
        string nx = "0", string ny = "0", string nz = "0", string distance = "0",
        int order = 27)
    {
        AddBlock(graph, "b_killplane", VfxNodeLibrary.Update, VfxNodeLibrary.KillPlane, order,
            Field("Nx", nx), Field("Ny", ny), Field("Nz", nz), Field("Distance", distance));
    }

    private static void AddConformToSphereBlock(
        VfxGraphAsset graph,
        string x = "0", string y = "0", string z = "0", string radius = "1", string strength = "0",
        int order = 28)
    {
        AddBlock(graph, "b_consphere", VfxNodeLibrary.Update, VfxNodeLibrary.ConformToSphere, order,
            Field("X", x), Field("Y", y), Field("Z", z), Field("Radius", radius), Field("Strength", strength));
    }

    private static void AddVortexBlock(
        VfxGraphAsset graph,
        string x = "0", string y = "0", string z = "0",
        string ax = "0", string ay = "1", string az = "0",
        string rotation = "0", string attraction = "0",
        int order = 29)
    {
        AddBlock(graph, "b_vortex", VfxNodeLibrary.Update, VfxNodeLibrary.Vortex, order,
            Field("X", x), Field("Y", y), Field("Z", z),
            Field("Ax", ax), Field("Ay", ay), Field("Az", az),
            Field("Rotation", rotation), Field("Attraction", attraction));
    }

    private static void AddConformToCylinderBlock(
        VfxGraphAsset graph,
        string x = "0", string y = "0", string z = "0",
        string ax = "0", string ay = "1", string az = "0",
        string radius = "1", string strength = "0",
        int order = 30)
    {
        AddBlock(graph, "b_concyl", VfxNodeLibrary.Update, VfxNodeLibrary.ConformToCylinder, order,
            Field("X", x), Field("Y", y), Field("Z", z),
            Field("Ax", ax), Field("Ay", ay), Field("Az", az),
            Field("Radius", radius), Field("Strength", strength));
    }

    private static void AddMultiplySizeBlock(VfxGraphAsset graph, string amount = "1", int order = 31)
    {
        AddBlock(graph, "b_mulsize", VfxNodeLibrary.Update, VfxNodeLibrary.MultiplySize, order, Field("Amount", amount));
    }

    private static void AddMultiplyColorBlock(
        VfxGraphAsset graph,
        string r = "1", string g = "1", string b = "1", string a = "1",
        int order = 32)
    {
        AddBlock(graph, "b_mulcolor", VfxNodeLibrary.Update, VfxNodeLibrary.MultiplyColor, order,
            Field("R", r), Field("G", g), Field("B", b), Field("A", a));
    }

    private static void AddAddSizeBlock(VfxGraphAsset graph, string amount = "0", int order = 33)
    {
        AddBlock(graph, "b_addsize", VfxNodeLibrary.Update, VfxNodeLibrary.AddSize, order, Field("Amount", amount));
    }

    private static void AddAddColorBlock(
        VfxGraphAsset graph,
        string r = "0", string g = "0", string b = "0", string a = "0",
        int order = 34)
    {
        AddBlock(graph, "b_addcolor", VfxNodeLibrary.Update, VfxNodeLibrary.AddColor, order,
            Field("R", r), Field("G", g), Field("B", b), Field("A", a));
    }

    private static void AddAddVelocityBlock(
        VfxGraphAsset graph,
        string x = "0", string y = "0", string z = "0",
        int order = 35)
    {
        AddBlock(graph, "b_addvel", VfxNodeLibrary.Update, VfxNodeLibrary.AddVelocity, order,
            Field("X", x), Field("Y", y), Field("Z", z));
    }

    private static void AddMultiplyVelocityBlock(VfxGraphAsset graph, string amount = "1", int order = 36)
    {
        AddBlock(graph, "b_mulvel", VfxNodeLibrary.Update, VfxNodeLibrary.MultiplyVelocity, order, Field("Amount", amount));
    }

    private static void AddOverwriteSizeBlock(VfxGraphAsset graph, string amount = "0", int order = 37)
    {
        AddBlock(graph, "b_owsize", VfxNodeLibrary.Update, VfxNodeLibrary.OverwriteSize, order, Field("Amount", amount));
    }

    private static void AddOverwriteColorBlock(
        VfxGraphAsset graph,
        string r = "0", string g = "0", string b = "0", string a = "0",
        int order = 38)
    {
        AddBlock(graph, "b_owcolor", VfxNodeLibrary.Update, VfxNodeLibrary.OverwriteColor, order,
            Field("R", r), Field("G", g), Field("B", b), Field("A", a));
    }

    private static void AddOverwriteVelocityBlock(
        VfxGraphAsset graph,
        string x = "0", string y = "0", string z = "0",
        int order = 39)
    {
        AddBlock(graph, "b_owvel", VfxNodeLibrary.Update, VfxNodeLibrary.OverwriteVelocity, order,
            Field("X", x), Field("Y", y), Field("Z", z));
    }

    private static void AddOverwriteLifetimeBlock(VfxGraphAsset graph, string amount = "0", int order = 40)
    {
        AddBlock(graph, "b_owlife", VfxNodeLibrary.Update, VfxNodeLibrary.OverwriteLifetime, order, Field("Amount", amount));
    }

    private static void AddAddLifetimeBlock(VfxGraphAsset graph, string amount = "0", int order = 41)
    {
        AddBlock(graph, "b_addlife", VfxNodeLibrary.Update, VfxNodeLibrary.AddLifetime, order, Field("Amount", amount));
    }

    private static void AddMultiplyLifetimeBlock(VfxGraphAsset graph, string amount = "1", int order = 42)
    {
        AddBlock(graph, "b_mullife", VfxNodeLibrary.Update, VfxNodeLibrary.MultiplyLifetime, order, Field("Amount", amount));
    }

    private static void AddAddAgeBlock(VfxGraphAsset graph, string amount = "0", int order = 43)
    {
        AddBlock(graph, "b_addage", VfxNodeLibrary.Update, VfxNodeLibrary.AddAge, order, Field("Amount", amount));
    }

    private static void AddMultiplyAgeBlock(VfxGraphAsset graph, string amount = "1", int order = 44)
    {
        AddBlock(graph, "b_mulage", VfxNodeLibrary.Update, VfxNodeLibrary.MultiplyAge, order, Field("Amount", amount));
    }

    private static void AddSetAgeBlock(VfxGraphAsset graph, string amount = "0", int order = 45)
    {
        AddBlock(graph, "b_setage", VfxNodeLibrary.Update, VfxNodeLibrary.SetAge, order, Field("Amount", amount));
    }

    private static void AddAddPositionBlock(
        VfxGraphAsset graph,
        string x = "0", string y = "0", string z = "0",
        int order = 46)
    {
        AddBlock(graph, "b_addpos", VfxNodeLibrary.Update, VfxNodeLibrary.AddPosition, order,
            Field("X", x), Field("Y", y), Field("Z", z));
    }

    private static void AddSetPositionBlock(
        VfxGraphAsset graph,
        string x = "0", string y = "0", string z = "0",
        int order = 47)
    {
        AddBlock(graph, "b_setpos", VfxNodeLibrary.Update, VfxNodeLibrary.SetPosition, order,
            Field("X", x), Field("Y", y), Field("Z", z));
    }

    private static void AddTriggerEventOnceBlock(VfxGraphAsset graph, string count = "0", int order = 19)
    {
        AddBlock(graph, "b_trigonce", VfxNodeLibrary.Update, VfxNodeLibrary.TriggerEventOnce, order,
            Field("Count", count));
    }

    private static void AddColorOverSpeedBlock(
        VfxGraphAsset graph,
        string fromR = "1", string fromG = "1", string fromB = "1", string fromA = "1",
        string toR = "1", string toG = "1", string toB = "1", string toA = "1",
        string scale = "0",
        int order = 18)
    {
        AddBlock(graph, "b_colorspeed", VfxNodeLibrary.Update, VfxNodeLibrary.ColorOverSpeed, order,
            Field("FromR", fromR), Field("FromG", fromG), Field("FromB", fromB), Field("FromA", fromA),
            Field("ToR", toR), Field("ToG", toG), Field("ToB", toB), Field("ToA", toA),
            Field("Scale", scale));
    }

    private static void AddDepthBlock(VfxGraphAsset graph, string mode = "0", int order = 5)
    {
        AddBlock(graph, "b_depth", VfxNodeLibrary.Update, VfxNodeLibrary.CollideWithDepth, order,
            Field("Mode", mode));
    }

    private static void AddConformBlock(VfxGraphAsset graph, string attraction = "0", int order = 6)
    {
        AddBlock(graph, "b_conform", VfxNodeLibrary.Update, VfxNodeLibrary.ConformToSDF, order,
            Field("Attraction", attraction));
    }

    private static void AddColorOverLifeBlock(
        VfxGraphAsset graph,
        string fromR = "1", string fromG = "1", string fromB = "1", string fromA = "1",
        string toR = "1", string toG = "1", string toB = "1", string toA = "0",
        int order = 8)
    {
        AddBlock(graph, "b_overlife", VfxNodeLibrary.Update, VfxNodeLibrary.ColorOverLife, order,
            Field("FromR", fromR), Field("FromG", fromG), Field("FromB", fromB), Field("FromA", fromA),
            Field("ToR", toR), Field("ToG", toG), Field("ToB", toB), Field("ToA", toA));
    }

    private static void AddSizeOverLifeBlock(
        VfxGraphAsset graph, string from = "1", string to = "0", int order = 9)
    {
        AddBlock(graph, "b_sizeover", VfxNodeLibrary.Update, VfxNodeLibrary.SizeOverLife, order,
            Field("From", from), Field("To", to));
    }

    private static void AddLinearDragBlock(VfxGraphAsset graph, string amount = "0", int order = 10)
    {
        AddBlock(graph, "b_drag", VfxNodeLibrary.Update, VfxNodeLibrary.LinearDrag, order,
            Field("Amount", amount));
    }

    private static void AddForceBlock(
        VfxGraphAsset graph, string x = "0", string y = "0", string z = "0", int order = 11)
    {
        AddBlock(graph, "b_force", VfxNodeLibrary.Update, VfxNodeLibrary.Force, order,
            Field("X", x), Field("Y", y), Field("Z", z));
    }

    private static void AddAngularVelocityBlock(VfxGraphAsset graph, string rate = "0", int order = 12)
    {
        AddBlock(graph, "b_spin", VfxNodeLibrary.Update, VfxNodeLibrary.AngularVelocity, order,
            Field("Rate", rate));
    }

    private static void AddCustomHlslBlock(VfxGraphAsset graph, string code = "", int order = 13)
    {
        AddBlock(graph, "b_hlsl", VfxNodeLibrary.Update, VfxNodeLibrary.CustomHLSL, order,
            Field("Code", code));
    }

    private static void AddVectorFieldBlock(VfxGraphAsset graph, string intensity = "0", int order = 7)
    {
        AddBlock(graph, "b_vfield", VfxNodeLibrary.Update, VfxNodeLibrary.VectorField, order,
            Field("Intensity", intensity));
    }

    private static void AddSpawnOverDistanceBlock(VfxGraphAsset graph, string rate = "0", int order = 1)
    {
        AddBlock(graph, "b_dist", VfxNodeLibrary.Spawn, VfxNodeLibrary.SpawnOverDistance, order,
            Field("Rate", rate));
    }

    private static void AddPeriodicBurstBlock(
        VfxGraphAsset graph, string count = "0", string period = "1", int order = 2)
    {
        AddBlock(graph, "b_periodic", VfxNodeLibrary.Spawn, VfxNodeLibrary.PeriodicBurst, order,
            Field("Count", count), Field("Period", period));
    }

    private static void AddInitializeDefaults(VfxGraphAsset graph, string speed, string size, string lifetime)
    {
        AddBlock(graph, "b_vel", VfxNodeLibrary.Initialize, VfxNodeLibrary.SetVelocity, 0, Field("Speed", speed));
        AddBlock(graph, "b_color", VfxNodeLibrary.Initialize, VfxNodeLibrary.SetColor, 1);
        AddBlock(graph, "b_size", VfxNodeLibrary.Initialize, VfxNodeLibrary.SetSize, 2, Field("Size", size));
        AddBlock(graph, "b_life", VfxNodeLibrary.Initialize, VfxNodeLibrary.SetLifetime, 3, Field("Lifetime", lifetime));
        AddAttributeMapBlock(graph);
        AddPositionShapeBlock(graph);
        AddSequentialBlock(graph);
        AddInheritVelocityBlock(graph);
    }

    private static void AddAttributeMapBlock(VfxGraphAsset graph, string mode = "0", string scale = "1", int order = 4)
    {
        AddBlock(graph, "b_amap", VfxNodeLibrary.Initialize, VfxNodeLibrary.AttributeMap, order,
            Field("Mode", mode), Field("Scale", scale));
    }

    private static void AddPositionShapeBlock(
        VfxGraphAsset graph, string mode = "0", string sx = "0.25", string sy = "0.25", string sz = "0.25", int order = 5)
    {
        AddBlock(graph, "b_shape", VfxNodeLibrary.Initialize, VfxNodeLibrary.PositionShape, order,
            Field("Mode", mode), Field("Sx", sx), Field("Sy", sy), Field("Sz", sz));
    }

    private static void AddSequentialBlock(
        VfxGraphAsset graph, string mode = "0", string sx = "0.5", string sy = "0.5", string sz = "8", int order = 6)
    {
        AddBlock(graph, "b_seq", VfxNodeLibrary.Initialize, VfxNodeLibrary.Sequential, order,
            Field("Mode", mode), Field("Sx", sx), Field("Sy", sy), Field("Sz", sz));
    }

    private static void AddInheritVelocityBlock(VfxGraphAsset graph, string amount = "0", int order = 7)
    {
        AddBlock(graph, "b_inherit", VfxNodeLibrary.Initialize, VfxNodeLibrary.InheritVelocity, order,
            Field("Amount", amount));
    }

    private static void AddDistortBlock(VfxGraphAsset graph, string outputContext, string intensity = "0", int order = 1)
    {
        AddBlock(graph, "b_distort", outputContext, VfxNodeLibrary.Distort, order,
            Field("Intensity", intensity));
    }

    private static void AddParticleLightBlock(
        VfxGraphAsset graph, string outputContext, string intensity = "0", string range = "1.5", string count = "0",
        int order = 2)
    {
        AddBlock(graph, "b_plight", outputContext, VfxNodeLibrary.ParticleLight, order,
            Field("Intensity", intensity), Field("Range", range), Field("Count", count));
    }

    private static void AddSortBlock(
        VfxGraphAsset graph, string outputContext, string mode = "0", int order = 3)
    {
        AddBlock(graph, "b_sort", outputContext, VfxNodeLibrary.Sort, order, Field("Mode", mode));
    }

    private static void AddCameraFadeBlock(
        VfxGraphAsset graph, string outputContext, string near = "0", string far = "0", int order = 4)
    {
        AddBlock(graph, "b_camfade", outputContext, VfxNodeLibrary.CameraFade, order,
            Field("Near", near), Field("Far", far));
    }

    private static void AddFlipbookBlendBlock(
        VfxGraphAsset graph, string outputContext, string blend = "0", int order = 5)
    {
        AddBlock(graph, "b_fbblend", outputContext, VfxNodeLibrary.FlipbookBlend, order, Field("Blend", blend));
    }

    private static void AddMotionVectorBlock(
        VfxGraphAsset graph, string outputContext, string mode = "0", string scale = "1", int order = 6)
    {
        AddBlock(graph, "b_motion", outputContext, VfxNodeLibrary.MotionVector, order,
            Field("Mode", mode), Field("Scale", scale));
    }

    private static void AddOrientAlongVelocityBlock(
        VfxGraphAsset graph, string outputContext, string mode = "0", int order = 7)
    {
        AddBlock(graph, "b_orientvel", outputContext, VfxNodeLibrary.OrientAlongVelocity, order, Field("Mode", mode));
    }

    private static void AddOrientFixedAxisBlock(
        VfxGraphAsset graph, string outputContext, string mode = "0", string x = "0", string y = "1", string z = "0",
        int order = 8)
    {
        AddBlock(graph, "b_fixedaxis", outputContext, VfxNodeLibrary.OrientFixedAxis, order,
            Field("Mode", mode), Field("X", x), Field("Y", y), Field("Z", z));
    }

    private static void AddFlipbookMotionBlock(
        VfxGraphAsset graph, string outputContext, string scale = "0", int order = 9)
    {
        AddBlock(graph, "b_fbmotion", outputContext, VfxNodeLibrary.FlipbookMotion, order, Field("Scale", scale));
    }

    private static void AddScreenSpaceSizeBlock(
        VfxGraphAsset graph, string outputContext, string amount = "0", int order = 10)
    {
        AddBlock(graph, "b_screensize", outputContext, VfxNodeLibrary.ScreenSpaceSize, order, Field("Amount", amount));
    }

    private static void AddOrientLookAtBlock(
        VfxGraphAsset graph, string outputContext, string mode = "0", string x = "0", string y = "0", string z = "0",
        int order = 11)
    {
        AddBlock(graph, "b_lookat", outputContext, VfxNodeLibrary.OrientLookAt, order,
            Field("Mode", mode), Field("X", x), Field("Y", y), Field("Z", z));
    }

    private static void AddFlipbookRateBlock(
        VfxGraphAsset graph, string outputContext, string rate = "0", int order = 12)
    {
        AddBlock(graph, "b_fbrate", outputContext, VfxNodeLibrary.FlipbookRate, order, Field("Rate", rate));
    }

    private static void AddFlipbookStartBlock(
        VfxGraphAsset graph, string outputContext, string frame = "0", int order = 13)
    {
        AddBlock(graph, "b_fbstart", outputContext, VfxNodeLibrary.FlipbookStart, order, Field("Frame", frame));
    }

    private static void AddColorOperator(
        VfxGraphAsset graph, string id, float x, float y, string r, string g, string b, string a)
    {
        var op = new VfxOperator { Id = id, Type = VfxNodeLibrary.Color, PosX = x, PosY = y };
        op.Fields.Add(Field("R", r));
        op.Fields.Add(Field("G", g));
        op.Fields.Add(Field("B", b));
        op.Fields.Add(Field("A", a));
        graph.Operators.Add(op);
    }

    private static void AddBlock(
        VfxGraphAsset graph, string id, string contextType, string blockType, int order, params VfxNodeField[] fields)
    {
        string contextId = FindContextId(graph, contextType);
        var block = new VfxBlock
        {
            Id = id,
            ContextId = contextId,
            Type = blockType,
            Order = order,
        };
        for (int i = 0; i < fields.Length; i++)
            block.Fields.Add(fields[i]);
        graph.Blocks.Add(block);
    }

    private static string FindContextId(VfxGraphAsset graph, string type)
    {
        for (int i = 0; i < graph.Contexts.Count; i++)
        {
            if (graph.Contexts[i].Type == type)
                return graph.Contexts[i].Id;
        }
        return "";
    }

    private static VfxEdge Flow(string id, string from, string to)
        => new()
        {
            Id = id,
            FromNode = from,
            FromPort = VfxNodeLibrary.FlowPort,
            ToNode = to,
            ToPort = VfxNodeLibrary.FlowPort,
        };

    private static VfxNodeField Field(string key, string value)
        => new() { Key = key, Value = value };
}
