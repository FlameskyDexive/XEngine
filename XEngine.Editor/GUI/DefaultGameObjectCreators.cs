using System;

using XEngine.Editor.GUI.Panels;
using XEngine.Editor.Projects;
using XEngine.Editor.Theming;
using XEngine.Runtime;
using XEngine.Runtime.ParticleSystem;
using XEngine.Runtime.ParticleSystem.Modules;
using XEngine.Runtime.Rendering;
using XEngine.Runtime.Resources;
using XEngine.Vector;

namespace XEngine.Editor.GUI;

internal static class DefaultGameObjectCreators
{
    [MenuItem("GameObject/Empty Object", priority: 0, Icon = EditorIcons.Cube)]
    static void CreateEmpty()
    {
        HierarchyPanel.CreateGameObject("GameObject", MenuContext.ActiveGameObject);
    }

    [MenuItem("GameObject/3D Object/Cube", priority: 10, Icon = EditorIcons.Cube, Separator = true)]
    static void CreateCube() => CreatePrimitive("Cube", DefaultModel.Cube);

    [MenuItem("GameObject/3D Object/Sphere", priority: 11, Icon = EditorIcons.CircleDot)]
    static void CreateSphere() => CreatePrimitive("Sphere", DefaultModel.Sphere);

    [MenuItem("GameObject/3D Object/Cylinder", priority: 12, Icon = EditorIcons.Circle)]
    static void CreateCylinder() => CreatePrimitive("Cylinder", DefaultModel.Cylinder);

    [MenuItem("GameObject/3D Object/Plane", priority: 13, Icon = EditorIcons.Square)]
    static void CreatePlane() => CreatePrimitive("Plane", DefaultModel.Plane);

    [MenuItem("GameObject/3D Object/Text Mesh", priority: 24, Icon = EditorIcons.Font, Separator = true)]
    static void CreateTextMesh()
    {
        var go = HierarchyPanel.CreateGameObject("Text Mesh", MenuContext.ActiveGameObject);
        var text = go.AddComponent<TextMeshComponent>();
        text.Text = "New Text";
    }

    [MenuItem("GameObject/Light/Directional Light", priority: 30, Icon = EditorIcons.Sun)]
    static void CreateDirectionalLight()
    {
        var go = HierarchyPanel.CreateGameObject("Directional Light", MenuContext.ActiveGameObject);
        go.Transform.Rotation = Quaternion.FromEuler(new Float3(-50, 30, 0));
        UniversalAdditionalLightData.GetOrAdd(go.AddComponent<DirectionalLight>());
    }

    [MenuItem("GameObject/Light/Point Light", priority: 31, Icon = EditorIcons.Lightbulb)]
    static void CreatePointLight()
    {
        var go = HierarchyPanel.CreateGameObject("Point Light", MenuContext.ActiveGameObject);
        UniversalAdditionalLightData.GetOrAdd(go.AddComponent<PointLight>());
    }

    [MenuItem("GameObject/Light/Spot Light", priority: 32, Icon = EditorIcons.Bullseye)]
    static void CreateSpotLight()
    {
        var go = HierarchyPanel.CreateGameObject("Spot Light", MenuContext.ActiveGameObject);
        go.Transform.Rotation = Quaternion.FromEuler(new Float3(90, 0, 0));
        UniversalAdditionalLightData.GetOrAdd(go.AddComponent<SpotLight>());
    }

    [MenuItem("GameObject/Light/Reflection Probe", priority: 33, Icon = EditorIcons.Circle)]
    static void CreateReflectionProbe()
    {
        HierarchyPanel.CreateGameObject("Reflection Probe", MenuContext.ActiveGameObject)
            .AddComponent<ReflectionProbe>();
    }

    [MenuItem("GameObject/Effects/Fog/Global", priority: 40, Icon = EditorIcons.Cloud)]
    static void CreateGlobalFogVolume()
    {
        var go = HierarchyPanel.CreateGameObject("Global Fog Volume", MenuContext.ActiveGameObject);
        var v = go.AddComponent<FogVolume>();
        v.Shape = FogVolumeShape.Global;
    }

    [MenuItem("GameObject/Effects/Fog/Box", priority: 41, Icon = EditorIcons.Cube)]
    static void CreateBoxFogVolume()
    {
        var go = HierarchyPanel.CreateGameObject("Box Fog Volume", MenuContext.ActiveGameObject);
        go.Transform.LocalScale = new Float3(2, 2, 2);
        var v = go.AddComponent<FogVolume>();
        v.Shape = FogVolumeShape.Box;
    }

    [MenuItem("GameObject/Effects/Fog/Sphere", priority: 42, Icon = EditorIcons.CircleDot)]
    static void CreateSphereFogVolume()
    {
        var go = HierarchyPanel.CreateGameObject("Sphere Fog Volume", MenuContext.ActiveGameObject);
        go.Transform.LocalScale = new Float3(3, 3, 3);
        var v = go.AddComponent<FogVolume>();
        v.Shape = FogVolumeShape.Sphere;
    }

    [MenuItem("GameObject/Effects/Fog/Cylinder", priority: 43, Icon = EditorIcons.Circle)]
    static void CreateCylinderFogVolume()
    {
        var go = HierarchyPanel.CreateGameObject("Cylinder Fog Volume", MenuContext.ActiveGameObject);
        go.Transform.LocalScale = new Float3(2, 3, 2);
        var v = go.AddComponent<FogVolume>();
        v.Shape = FogVolumeShape.Cylinder;
    }

    [MenuItem("GameObject/Effects/Fog/Cone", priority: 44, Icon = EditorIcons.Bullseye)]
    static void CreateConeFogVolume()
    {
        var go = HierarchyPanel.CreateGameObject("Cone Fog Volume", MenuContext.ActiveGameObject);
        go.Transform.LocalScale = new Float3(1, 4, 1);
        var v = go.AddComponent<FogVolume>();
        v.Shape = FogVolumeShape.Cone;
    }

    [MenuItem("GameObject/Effects/Particle System", priority: 55, Icon = EditorIcons.SprayCanSparkles, Separator = true)]
    static void CreateParticleSystem()
    {
        var go = HierarchyPanel.CreateGameObject("Particle System", MenuContext.ActiveGameObject);
        var ps = go.AddComponent<ParticleSystemComponent>();
        ps.Material = new AssetRef<Material>(BuiltInAssets.GuidFor(DefaultMaterial.Particle));
        ps.Emission.Enabled = true;
        ps.Emission.RateOverTime = new MinMaxCurve(10f);
        ps.Emission.Shape = EmissionShape.Cone;
        ps.Initial.Enabled = true;
        ps.Initial.StartLifetime = new MinMaxCurve(2f);
        ps.Initial.StartSpeed = new MinMaxCurve(3f);
        ps.Initial.StartSize = new MinMaxCurve(0.2f);
    }

    [MenuItem("GameObject/Audio/Audio Source", priority: 60, Icon = EditorIcons.VolumeHigh)]
    static void CreateAudioSource()
    {
        var go = HierarchyPanel.CreateGameObject("Audio Source", MenuContext.ActiveGameObject);
        go.AddComponent<AudioSource>();
    }

    [MenuItem("GameObject/Audio/Audio Listener", priority: 61, Icon = EditorIcons.Headphones)]
    static void CreateAudioListener()
    {
        var go = HierarchyPanel.CreateGameObject("Audio Listener", MenuContext.ActiveGameObject);
        go.AddComponent<AudioListener>();
    }

    [MenuItem("GameObject/Volume", priority: 90, Icon = EditorIcons.Cube)]
    static void CreateGlobalVolume()
    {
        var go = HierarchyPanel.CreateGameObject("Global Volume", MenuContext.ActiveGameObject);
        var volume = go.AddComponent<Volume>();
        volume.IsGlobal = true;
        if (EditorAssetBackend.Instance != null)
        {
            Guid guid = EditorAssetBackend.Instance.PathToGuid(UniversalProjectTemplate.VolumeProfilePath);
            if (guid != Guid.Empty)
                volume.Profile = new AssetRef<VolumeProfile>(guid);
        }
    }

    [MenuItem("GameObject/Camera", priority: 100, Icon = EditorIcons.Camera, Separator = true)]
    static void CreateCamera()
    {
        var go = HierarchyPanel.CreateGameObject("Camera", MenuContext.ActiveGameObject);
        Camera cam = go.AddComponent<Camera>();
        cam.HDR = true;
        UniversalAdditionalCameraData data = UniversalAdditionalCameraData.GetOrAdd(cam);
        data.RenderPostProcessing = true;
        data.RenderShadows = true;
        go.AddComponent<AudioListener>();
    }

    private static void CreatePrimitive(string name, DefaultModel model)
    {
        var go = HierarchyPanel.CreateGameObject(name, MenuContext.ActiveGameObject);
        var renderer = go.AddComponent<MeshRenderer>();
        renderer.Mesh = new AssetRef<Mesh>(BuiltInAssets.GuidForMesh(model));
        renderer.Material = new AssetRef<Material>(BuiltInAssets.GuidFor(DefaultMaterial.Standard));
    }

}
