// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.IO;

using XEngine.Echo;
using XEngine.Vector;

using Xunit;

namespace XEngine.Runtime.Test;

/// <summary>
/// Format red-line proof for the [GenerateSerializer] rollout on Transform and the high-frequency
/// built-in components (MeshRenderer/SkinnedMeshRenderer/SpriteRenderer/Camera/lights).
///
/// TestData/echo-reflection-baseline.echo was written by the PRE-annotation engine (pure reflection
/// serializer). The generated serializers must (a) load it with all values intact - old assets keep
/// loading - and (b) re-serialize the loaded graph into a deeply equal EchoObject tree - new output
/// keeps the exact structure old engine versions can read.
///
/// To regenerate the fixture: revert the [GenerateSerializer] annotations, set
/// XENGINE_REGEN_ECHO_FIXTURE=1 and run RegenerateFixture_WhenRequested. Regenerating it while the
/// annotations are active would make these tests tautological.
/// </summary>
public class EchoGeneratedSerializerTests : RuntimeTestBase
{
    private static string FixturePath()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "XEngine.Runtime.Test")))
            dir = dir.Parent;
        if (dir == null)
            throw new DirectoryNotFoundException("Could not locate repo root (XEngine.Runtime.Test).");
        return Path.Combine(dir.FullName, "XEngine.Runtime.Test", "TestData", "echo-reflection-baseline.echo");
    }

    /// <summary>Builds the component-rich GameObject tree the fixture snapshots.</summary>
    private GameObject BuildFixtureTree()
    {
        var root = CreateGameObject("EchoFixtureRoot");
        root.Transform.LocalPosition = new Float3(1.5f, -2.25f, 3.125f);
        root.Transform.LocalEulerAngles = new Float3(0f, 45f, 0f);
        root.Transform.LocalScale = new Float3(2f, 2f, 2f);

        var camGo = CreateGameObject("Camera");
        camGo.SetParent(root, worldPositionStays: false);
        var cam = camGo.AddComponent<Camera>();
        cam.ClearFlags = CameraClearFlags.SolidColor;
        cam.ClearColor = new Color(0.25f, 0.5f, 0.75f, 1f);
        cam.ProjectionMode = Camera.ProjectionType.Perspective;
        cam.FieldOfView = 72.5f;
        cam.NearClipPlane = 0.25f;
        cam.FarClipPlane = 250f;
        cam.Depth = 3;
        cam.HDR = true;
        cam.RenderScale = 1.25f;
        cam.Viewrect = new Rect(0.1f, 0.2f, 0.8f, 0.7f);

        var meshGo = CreateGameObject("Mesh");
        meshGo.SetParent(root, worldPositionStays: false);
        var mr = meshGo.AddComponent<MeshRenderer>();
        mr.LightmapIndex = 4;
        mr.LightmapScaleOffset = new Float4(0.5f, 0.5f, 0.25f, 0.25f);

        var skinnedGo = CreateGameObject("Skinned");
        skinnedGo.SetParent(root, worldPositionStays: false);
        skinnedGo.AddComponent<SkinnedMeshRenderer>();

        var spriteGo = CreateGameObject("Sprite");
        spriteGo.SetParent(root, worldPositionStays: false);
        var sr = spriteGo.AddComponent<SpriteRenderer>();
        sr.Color = new Color(1f, 0.5f, 0.25f, 0.75f);

        var dirGo = CreateGameObject("Sun");
        dirGo.SetParent(root, worldPositionStays: false);
        var dl = dirGo.AddComponent<DirectionalLight>();
        dl.Intensity = 2.5f;
        dl.ShadowDistance = 55f;
        dl.CastShadows = true;

        var pointGo = CreateGameObject("Bulb");
        pointGo.SetParent(root, worldPositionStays: false);
        var pl = pointGo.AddComponent<PointLight>();
        pl.Range = 12.5f;
        pl.Intensity = 1.75f;

        var spotGo = CreateGameObject("Spot");
        spotGo.SetParent(root, worldPositionStays: false);
        var sl = spotGo.AddComponent<SpotLight>();
        sl.Range = 8.25f;
        sl.SpotAngle = 33f;
        sl.InnerSpotAngle = 21f;

        return root;
    }

    /// <summary>
    /// Manual fixture (re)generation - only with XENGINE_REGEN_ECHO_FIXTURE=1, and only meaningful
    /// while the [GenerateSerializer] annotations are NOT applied (reflection path output).
    /// </summary>
    [Fact]
    public void RegenerateFixture_WhenRequested()
    {
        if (Environment.GetEnvironmentVariable("XENGINE_REGEN_ECHO_FIXTURE") != "1")
            return;

        var root = BuildFixtureTree();
        var echo = Serializer.Serialize(typeof(object), root);
        Directory.CreateDirectory(Path.GetDirectoryName(FixturePath())!);
        File.WriteAllText(FixturePath(), echo.WriteToString());
    }

    [Fact]
    public void ReflectionBaseline_LoadsThroughGeneratedSerializers_WithValuesIntact()
    {
        var echo = EchoObject.ReadFromString(File.ReadAllText(FixturePath()));
        var root = Serializer.Deserialize<GameObject>(echo)!;

        Assert.Equal("EchoFixtureRoot", root.Name);
        Assert.Equal(new Float3(1.5f, -2.25f, 3.125f), root.Transform.LocalPosition);
        Assert.Equal(new Float3(2f, 2f, 2f), root.Transform.LocalScale);

        var cam = root.GetComponentInChildren<Camera>(includeSelf: false)!;
        Assert.Equal(CameraClearFlags.SolidColor, cam.ClearFlags);
        Assert.Equal(new Color(0.25f, 0.5f, 0.75f, 1f), cam.ClearColor);
        Assert.Equal(72.5f, cam.FieldOfView);
        Assert.Equal(0.25f, cam.NearClipPlane);
        Assert.Equal(250f, cam.FarClipPlane);
        Assert.Equal(3, cam.Depth);
        Assert.True(cam.HDR);
        Assert.Equal(1.25f, cam.RenderScale);
        Assert.Equal(new Rect(0.1f, 0.2f, 0.8f, 0.7f), cam.Viewrect);

        var mr = root.GetComponentInChildren<MeshRenderer>(includeSelf: false)!;
        Assert.Equal(4, mr.LightmapIndex);
        Assert.Equal(new Float4(0.5f, 0.5f, 0.25f, 0.25f), mr.LightmapScaleOffset);

        var sr = root.GetComponentInChildren<SpriteRenderer>(includeSelf: false)!;
        Assert.Equal(new Color(1f, 0.5f, 0.25f, 0.75f), sr.Color);

        var dl = root.GetComponentInChildren<DirectionalLight>(includeSelf: false)!;
        Assert.Equal(2.5f, dl.Intensity);
        Assert.Equal(55f, dl.ShadowDistance);
        Assert.True(dl.CastShadows);

        var pl = root.GetComponentInChildren<PointLight>(includeSelf: false)!;
        Assert.Equal(12.5f, pl.Range);
        Assert.Equal(1.75f, pl.Intensity);

        var sl = root.GetComponentInChildren<SpotLight>(includeSelf: false)!;
        Assert.Equal(8.25f, sl.Range);
        Assert.Equal(33f, sl.SpotAngle);
        Assert.Equal(21f, sl.InnerSpotAngle);

        Assert.NotNull(root.GetComponentInChildren<SkinnedMeshRenderer>(includeSelf: false));
    }

    [Fact]
    public void ReflectionBaseline_Reserialized_IsStructurallyIdentical()
    {
        var fixture = EchoObject.ReadFromString(File.ReadAllText(FixturePath()));
        var root = Serializer.Deserialize<GameObject>(fixture)!;

        var reserialized = Serializer.Serialize(typeof(object), root);

        // GameObject.Deserialize and MonoBehaviour.OnAfterDeserialize intentionally regenerate the
        // "Identifier"/"_identifier" guids on every load (scenes restore them from their parallel
        // identifier arrays afterwards) - that pre-existing behavior fires identically on both the
        // reflection and generated paths, so normalize those keys before comparing. Everything else
        // must match exactly.
        NormalizeRegeneratedIdentifiers(fixture);
        NormalizeRegeneratedIdentifiers(reserialized);

        if (!fixture.Equals(reserialized))
        {
            Assert.Fail(
                "Re-serialized graph differs from the reflection-written baseline.\n--- baseline ---\n"
                + fixture.WriteToString() + "\n--- reserialized ---\n" + reserialized.WriteToString());
        }
    }

    private static void NormalizeRegeneratedIdentifiers(EchoObject node)
    {
        if (node.TagType == EchoType.Compound)
        {
            if (node.TryGet("Identifier", out var id) && id!.TagType == EchoType.String)
                id.Value = "00000000-0000-0000-0000-000000000000";
            if (node.TryGet("_identifier", out var cid) && cid!.TagType == EchoType.String)
                cid.Value = "00000000-0000-0000-0000-000000000000";

            // The baseline was written with legacy full AQN $type strings; the writer now emits the
            // short "Namespace.Type, Assembly" form. Both must RESOLVE to the same type - compare
            // type identity, not the string spelling.
            if (node.TryGet("$type", out var typeTag) && typeTag!.TagType == EchoType.String)
            {
                var resolved = RuntimeUtils.FindType(typeTag.StringValue);
                Assert.True(resolved != null, $"$type '{typeTag.StringValue}' failed to resolve");
                typeTag.Value = resolved!.FullName + ", " + resolved.Assembly.GetName().Name;
            }

            foreach (var key in node.GetNames())
                NormalizeRegeneratedIdentifiers(node[key]!);
        }
        else if (node.TagType == EchoType.List)
        {
            foreach (var child in node.List)
                NormalizeRegeneratedIdentifiers(child);
        }
    }

    [Fact]
    public void AnimationDemoScene_OldFormatRealAsset_LoadsThroughGeneratedSerializers()
    {
        // Samples/AnimationDemo/AnimationDemo.scene is a committed asset written by an OLD engine
        // version (aggressive $type on every Float3/Color). It must keep loading bit-for-bit through
        // the generated Transform/Camera/DirectionalLight serializers.
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !Directory.Exists(Path.Combine(dir.FullName, "Samples", "AnimationDemo")))
            dir = dir.Parent;
        Assert.NotNull(dir);
        string path = Path.Combine(dir!.FullName, "Samples", "AnimationDemo", "AnimationDemo.scene");

        var echo = EchoObject.ReadFromString(File.ReadAllText(path));
        var scene = Serializer.Deserialize<Resources.Scene>(echo)!;
        try
        {
            Camera? cam = null;
            DirectionalLight? light = null;
            foreach (var go in scene.AllObjects)
            {
                cam ??= go.GetComponent<Camera>();
                light ??= go.GetComponent<DirectionalLight>();
            }

            Assert.NotNull(cam);
            Assert.Equal(60f, cam!.FieldOfView);
            Assert.Equal(0.1f, cam.NearClipPlane);
            Assert.Equal(100f, cam.FarClipPlane);
            Assert.Equal(new Float3(0f, 1.6f, -6f), cam.GameObject.Transform.LocalPosition);

            Assert.NotNull(light);
            Assert.Equal(1.2f, light!.Intensity);
            Assert.Equal(70f, light.ShadowDistance);
            Assert.True(light.CastShadows);
            Assert.Equal(DirectionalLight.CascadeCount.Two, light.Cascades);
        }
        finally
        {
            scene.Dispose();
        }
    }

    [Fact]
    public void GeneratedSerializers_AreWiredIn()
    {
        // The analyzer must have produced ISerializable implementations declared ON each annotated
        // type (an inherited implementation would be refused by the dispatch guard).
        foreach (var type in new[]
                 {
                     typeof(Transform), typeof(MeshRenderer), typeof(SkinnedMeshRenderer),
                     typeof(SpriteRenderer), typeof(Camera),
                     typeof(DirectionalLight), typeof(PointLight), typeof(SpotLight),
                 })
        {
            Assert.True(typeof(ISerializable).IsAssignableFrom(type), $"{type.Name} lacks generated ISerializable");
            var map = type.GetInterfaceMap(typeof(ISerializable));
            for (int i = 0; i < map.InterfaceMethods.Length; i++)
            {
                if (map.InterfaceMethods[i].Name == nameof(ISerializable.Serialize))
                    Assert.Equal(type, map.TargetMethods[i].DeclaringType);
            }
        }
    }

    [Fact]
    public void DeserializeInto_UsesGeneratedPath_ForUndoRestore()
    {
        // Undo's DeserializeInto restores component fields onto a live instance.
        var go = CreateGameObject();
        var cam = go.AddComponent<Camera>();
        cam.FieldOfView = 100f;
        cam.Depth = 9;
        var snapshot = Serializer.Serialize(cam.GetType(), cam);

        cam.FieldOfView = 10f;
        cam.Depth = -5;
        Serializer.DeserializeInto(snapshot, cam);

        Assert.Equal(100f, cam.FieldOfView);
        Assert.Equal(9, cam.Depth);
    }

    [Fact]
    public void UserScriptSubclass_OfAnnotatedComponent_KeepsItsOwnFields()
    {
        // A project script subclassing an annotated component must not lose its fields to the
        // inherited generated implementation (dispatch falls back to reflection).
        var go = CreateGameObject();
        var custom = go.AddComponent<CustomMeshRendererSubclass>();
        custom.ExtraField = 4242;
        custom.LightmapIndex = 7;

        var echo = Serializer.Serialize(custom.GetType(), custom);
        Assert.True(echo.Contains("ExtraField"), "subclass field missing - generated impl hijacked serialization");

        var back = (CustomMeshRendererSubclass)Serializer.Deserialize(echo, typeof(CustomMeshRendererSubclass))!;
        Assert.Equal(4242, back.ExtraField);
        Assert.Equal(7, back.LightmapIndex);
    }
}

/// <summary>Stand-in for a user script deriving from an annotated engine component.</summary>
public class CustomMeshRendererSubclass : MeshRenderer
{
    public int ExtraField;
}
