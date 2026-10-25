using System.Reflection;
using System.Text.Json;
using XEngine.Echo;
using XEngine.Editor.Importers;
using XEngine.Runtime;
using XEngine.Runtime.AssetImporting;
using XEngine.Runtime.Resources;
using XEngine.Vector;
using Xunit;
using AnimationCurve = XEngine.Runtime.AnimationCurve;

namespace XEngine.Editor.Test;

public sealed class ModelFileUnitTests : EditorTestHarness
{
    internal static string Fbx(float centimetersPerUnit) => FormattableString.Invariant($$"""
        ; FBX 7.4.0 project file
        FBXHeaderExtension: { FBXVersion: 7400 }
        GlobalSettings: {
            Properties70: { P: "UnitScaleFactor", "double", "Number", "", {{centimetersPerUnit}} }
        }
        Objects: {
            Geometry: 10, "Geometry::Triangle", "Mesh" {
                Vertices: *9 { a: 0,0,0, 100,0,0, 0,100,0 }
                PolygonVertexIndex: *3 { a: 0,1,-3 }
            }
            Model: 1, "Model::Triangle", "Mesh" {
                Properties70: { P: "Lcl Translation", "Lcl Translation", "", "A", 20,0,0 }
            }
            AnimationStack: 2, "AnimStack::Take", "" { }
            AnimationLayer: 3, "AnimLayer::Base", "" { }
            AnimationCurveNode: 4, "AnimCurveNode::T", "" { }
            AnimationCurve: 5, "AnimCurve::X", "" {
                KeyTime: *2 { a: 0, 46186158000 }
                KeyValueFloat: *2 { a: 20, 120 }
                KeyAttrFlags: *1 { a: 4 }
                KeyAttrDataFloat: *4 { a: 0,0,0,0 }
                KeyAttrRefCount: *1 { a: 2 }
            }
        }
        Connections: {
            C: "OO", 1, 0
            C: "OO", 10, 1
            C: "OO", 3, 2
            C: "OO", 4, 3
            C: "OP", 4, 1, "Lcl Translation"
            C: "OP", 5, 4, "d|X"
        }
        """);

    [Theory]
    [InlineData(1f, true, 1f)]
    [InlineData(1f, false, 1f)]
    [InlineData(1f, true, 100f)]
    [InlineData(1f, false, 100f)]
    [InlineData(100f, true, 1f)]
    [InlineData(100f, false, 1f)]
    [InlineData(100f, true, 100f)]
    [InlineData(100f, false, 100f)]
    public void EditorImportComposesFileUnitsAndUserScaleForGeometryAndFixedTimeAnimation(
        float centimetersPerUnit, bool convert, float userScale)
    {
        string path = AssetAbsolutePath("Units.fbx"); File.WriteAllText(path, Fbx(centimetersPerUnit));
        var settings = EchoObject.NewCompound();
        settings["convertFileUnits"] = new EchoObject(convert);
        settings["unitScale"] = new EchoObject(userScale);
        var ctx = new ImportContext(Guid.NewGuid(), path, settings);
        try
        {
            Assert.True(new EditorModelImporter().Import(ctx), ctx.Error);
            float factor = (convert ? centimetersPerUnit * 0.01f : 1f) * userScale;
            var mesh = Assert.Single(ctx.SubAssets.OfType<Mesh>());
            Assert.Equal(100f * factor, mesh.bounds.Size.X, 3);
            var bone = Assert.Single(Assert.Single(ctx.SubAssets.OfType<AnimationClip>()).Bones,
                b => b.BoneName.EndsWith("Triangle"));
            Assert.Equal(45f * factor, bone.PosX!.Evaluate(0.25f), 3);
            Assert.Equal(95f * factor, bone.PosX.Evaluate(0.75f), 3);
            Assert.Equal(20f * factor, bone.SourcePosition.X, 3);
        }
        finally { ctx.MainAsset?.Dispose(); foreach (var asset in ctx.SubAssets) asset.Dispose(); }
    }

    [Fact]
    public void UserScaleAlsoScalesMorphDeltasAndPreservesDirectionDeltas()
    {
        using var mesh = new Mesh { Vertices = [new Float3(2, 3, 4)] };
        var frame = new BlendShapeFrame
        { DeltaVertices = [new Float3(1, 2, 3)], DeltaNormals = [Float3.UnitY], DeltaTangents = [Float3.UnitX] };
        mesh.BlendShapes = [new BlendShape { Name = "Smile", Frames = [frame] }];
        var data = new ModelImportResult(); data.Meshes.Add(mesh);
        ApplyUserScale(data, 100f);
        Assert.Equal(new Float3(100, 200, 300), frame.DeltaVertices[0]);
        Assert.Equal(Float3.UnitY, frame.DeltaNormals[0]); Assert.Equal(Float3.UnitX, frame.DeltaTangents[0]);
    }

    [Fact]
    public void UserScalePreservesCurveShapeBetweenKeysAndSourceReference()
    {
        using var clip = new AnimationClip();
        var curve = new AnimationCurve(); curve.Keys.Clear();
        curve.Keys.Add(new KeyFrame(0, 2, 0, 5)); curve.Keys.Add(new KeyFrame(1, 3, -2, 0));
        var bone = new AnimationClip.AnimBone
        { BoneName = "Bone", PosX = curve, HasSourceReferencePose = true, SourcePosition = new Float3(2, 0, 0) };
        clip.AddBone(bone);
        float quarter = curve.Evaluate(0.25f), threeQuarters = curve.Evaluate(0.75f);
        var data = new ModelImportResult(); data.Animations.Add(clip); ApplyUserScale(data, 100f);
        Assert.Equal(quarter * 100f, curve.Evaluate(0.25f), 4);
        Assert.Equal(threeQuarters * 100f, curve.Evaluate(0.75f), 4);
        Assert.Equal(200f, bone.SourcePosition.X);
        Assert.Equal(500f, curve.Keys[0].TangentOut); Assert.Equal(-200f, curve.Keys[1].TangentIn);
        Assert.Equal(0f, curve.Keys[0].Position); Assert.Equal(1f, curve.Keys[1].Position);
    }

    private static void ApplyUserScale(ModelImportResult data, float scale)
        => typeof(EditorModelImporter).GetMethod("ApplyUnitScale", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [data, scale]);

    [Theory]
    [InlineData("obj", true, 1f)]
    [InlineData("obj", false, 100f)]
    [InlineData("gltf", true, 100f)]
    [InlineData("gltf", false, 1f)]
    [InlineData("gltf", false, 100f)]
    public void MeterFormatsRetainDimensionsAndSkinMorphAnimationUnits(string extension, bool convert, float scale)
    {
        string path = AssetAbsolutePath("Meter." + extension);
        File.WriteAllText(path, extension == "obj" ? "v 0 0 0\nv 1 0 0\nv 0 1 0\nf 1 2 3\n" : SkinnedMorphGltf());
        var settings = EchoObject.NewCompound(); settings["convertFileUnits"] = new EchoObject(convert);
        settings["unitScale"] = new EchoObject(scale);
        var ctx = new ImportContext(Guid.NewGuid(), path, settings);
        try
        {
            Assert.True(new EditorModelImporter().Import(ctx), ctx.Error);
            var mesh = Assert.Single(ctx.SubAssets.OfType<Mesh>());
            Assert.Equal(scale, mesh.bounds.Size.X, 4);
            if (extension == "obj") return;
            var frame = Assert.Single(Assert.Single(mesh.BlendShapes).Frames);
            Assert.Equal(scale, frame.DeltaVertices[1].X, 4);
            var clip = Assert.Single(ctx.SubAssets.OfType<AnimationClip>());
            var bone = Assert.Single(clip.Bones, b => b.BoneName.EndsWith("Bone"));
            Assert.Equal(0.5f * scale, bone.PosX!.Evaluate(0.25f), 4);
            Assert.Equal(1f * scale, bone.PosX.Evaluate(0.75f), 4);
            Assert.Equal(0.25f * scale, bone.SourcePosition.X, 4);
            using var root = Assert.IsType<PrefabAsset>(ctx.MainAsset).Instantiate();
            var boneTransform = root!.Transform.Find(bone.BoneName); Assert.NotNull(boneTransform);
            Assert.Equal(0.25f * scale, boneTransform!.LocalPosition.X, 4);
            var renderer = Assert.Single(root.GetComponentsInChildren<SkinnedMeshRenderer>(true, true));
            var skin = renderer.Transform.WorldToLocalMatrix * boneTransform.LocalToWorldMatrix * Assert.Single(mesh.BindPoses!);
            Assert.InRange(Float3.Length(new Float3(skin.c3.X, skin.c3.Y, skin.c3.Z)), 0, 1e-4f);
        }
        finally { ctx.MainAsset?.Dispose(); foreach (var asset in ctx.SubAssets) asset.Dispose(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void OldGlobalScalePreferenceCannotDisableLegacyDefaultConversion(bool legacy)
    {
        string path = AssetAbsolutePath("Old.fbx"); File.WriteAllText(path, Fbx(1));
        var settings = EchoObject.NewCompound(); settings["globalScale"] = new EchoObject(legacy);
        var ctx = new ImportContext(Guid.NewGuid(), path, settings);
        try
        {
            Assert.True(new EditorModelImporter().Import(ctx), ctx.Error);
            Assert.Equal(1f, Assert.Single(ctx.SubAssets.OfType<Mesh>()).bounds.Size.X, 5);
            Assert.False(settings.TryGet("convertFileUnits", out _));
            Assert.Equal(legacy, settings["globalScale"].BoolValue);
        }
        finally { ctx.MainAsset?.Dispose(); foreach (var asset in ctx.SubAssets) asset.Dispose(); }
    }

    internal static string SkinnedMorphGltf()
    {
        var bytes = new List<byte>(); var views = new List<object>(); var accessors = new List<object>();
        int Add(float[] values, int count, string type)
        {
            while (bytes.Count % 4 != 0) bytes.Add(0);
            byte[] data = new byte[values.Length * sizeof(float)]; Buffer.BlockCopy(values, 0, data, 0, data.Length);
            views.Add(new { buffer = 0, byteOffset = bytes.Count, byteLength = data.Length }); bytes.AddRange(data);
            accessors.Add(new { bufferView = views.Count - 1, componentType = 5126, count, type });
            return accessors.Count - 1;
        }
        int pos = Add([0,0,0, 1,0,0, 0,1,0], 3, "VEC3");
        int morph = Add([0,0,0, 1,0,0, 0,1,0], 3, "VEC3");
        int weights = Add([1,0,0,0, 1,0,0,0, 1,0,0,0], 3, "VEC4");
        int bind = Add([1,0,0,0, 0,1,0,0, 0,0,1,0, -0.25f,0,0,1], 1, "MAT4");
        int times = Add([0, 1], 2, "SCALAR");
        int positions = Add([0.25f,0,0, 1.25f,0,0], 2, "VEC3");
        while (bytes.Count % 4 != 0) bytes.Add(0);
        int jointView = views.Count; views.Add(new { buffer = 0, byteOffset = bytes.Count, byteLength = 24 });
        bytes.AddRange(new byte[24]); int joints = accessors.Count;
        accessors.Add(new { bufferView = jointView, componentType = 5123, count = 3, type = "VEC4" });
        return JsonSerializer.Serialize(new
        {
            asset = new { version = "2.0" }, scene = 0, scenes = new[] { new { nodes = new[] { 0 } } },
            nodes = new object[] { new { name = "Root", children = new[] { 1, 2 } },
                new { name = "Body", mesh = 0, skin = 0 }, new { name = "Bone", translation = new[] { 0.25f, 0, 0 } } },
            meshes = new[] { new { name = "Body", primitives = new[] { new {
                attributes = new { POSITION = pos, JOINTS_0 = joints, WEIGHTS_0 = weights },
                targets = new[] { new { POSITION = morph } } } } } },
            skins = new[] { new { joints = new[] { 2 }, inverseBindMatrices = bind, skeleton = 2 } },
            animations = new[] { new { name = "Move", samplers = new[] { new { input = times, output = positions, interpolation = "LINEAR" } },
                channels = new[] { new { sampler = 0, target = new { node = 2, path = "translation" } } } } },
            buffers = new[] { new { byteLength = bytes.Count, uri = "data:application/octet-stream;base64," + Convert.ToBase64String(bytes.ToArray()) } },
            bufferViews = views, accessors,
        });
    }

    private sealed class NullResolver : IModelTextureResolver
    {
        public AssetRef<Texture2D> ResolveExternal(string path) => default;
        public AssetRef<Texture2D> ResolveEmbedded(string? name, byte[] bytes, string? mime) => default;
    }

    [Theory]
    [InlineData("Anbi.FBX", 1f)]
    [InlineData("Anbi.FBX", 100f)]
    [InlineData("Anbi@Idle.FBX", 1f)]
    [InlineData("Anbi@Idle.FBX", 100f)]
    public void RealAnbiFileConversionScalesNodesSkinAndFixedTimePosesTogether(string fileName, float userScale)
    {
        string path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "../../../../ZonezeroTestProject/Assets/ZZZ/Arts/PlayerModel/Anbi", fileName));
        Assert.True(File.Exists(path), path);
        var importer = new ModelImporter();
        var meters = importer.Import(new FileInfo(path), new ModelImporterSettings { TextureResolver = new NullResolver() });
        var source = importer.Import(new FileInfo(path), new ModelImporterSettings
            { TextureResolver = new NullResolver(), ConvertFileUnits = false });
        try
        {
            ApplyUserScale(meters, userScale); ApplyUserScale(source, userScale);
            Assert.Equal(meters.Meshes.Count, source.Meshes.Count);
            for (int i = 0; i < meters.Meshes.Count; i++)
            {
                var a = meters.Meshes[i]; var b = source.Meshes[i];
                Assert.Equal(a.BoneNames, b.BoneNames); Assert.Equal(a.BoneIndices, b.BoneIndices);
                Assert.Equal(a.BoneWeights, b.BoneWeights); Assert.Equal(a.Vertices.Length, b.Vertices.Length);
                for (int v = 0; v < a.Vertices.Length; v++) Near(a.Vertices[v] * 100f, b.Vertices[v]);
            }
            CheckNodes(meters.RootGO!.Transform, source.RootGO!.Transform);
            CheckRestSkin(meters.RootGO); CheckRestSkin(source.RootGO);
            Assert.Equal(meters.Animations.Count, source.Animations.Count);
            for (int i = 0; i < meters.Animations.Count; i++)
            {
                var a = meters.Animations[i]; var b = source.Animations[i];
                Assert.Equal(a.Duration, b.Duration); Assert.Equal(a.Bones.Count, b.Bones.Count);
                foreach (var bone in a.Bones)
                {
                    var other = b.GetBone(bone.BoneName); Assert.NotNull(other);
                    Near(bone.SourcePosition * 100f, other!.SourcePosition);
                    foreach (float time in new[] { 0.5f, 2.5f })
                    {
                        if (bone.PosX != null) NearScalar(bone.PosX.Evaluate(time) * 100f, other.PosX!.Evaluate(time));
                        if (bone.PosY != null) NearScalar(bone.PosY.Evaluate(time) * 100f, other.PosY!.Evaluate(time));
                        if (bone.PosZ != null) NearScalar(bone.PosZ.Evaluate(time) * 100f, other.PosZ!.Evaluate(time));
                    }
                }
            }
        }
        finally { DisposeImport(meters); DisposeImport(source); }
    }

    private static void Near(Float3 expected, Float3 actual)
    { NearScalar(expected.X, actual.X); NearScalar(expected.Y, actual.Y); NearScalar(expected.Z, actual.Z); }
    private static void NearScalar(float expected, float actual)
        => Assert.InRange(MathF.Abs(expected - actual), 0f, 2e-4f * MathF.Max(1f, MathF.Abs(expected)));
    private static void CheckNodes(Transform meters, Transform source)
    {
        Assert.Equal(meters.GameObject.Name, source.GameObject.Name); Near(meters.LocalPosition * 100f, source.LocalPosition);
        Assert.Equal(meters.LocalRotation, source.LocalRotation); Assert.Equal(meters.LocalScale, source.LocalScale);
        Assert.Equal(meters.ChildCount, source.ChildCount);
        for (int i = 0; i < meters.ChildCount; i++) CheckNodes(meters.GetChild(i), source.GetChild(i));
    }
    private static void CheckRestSkin(GameObject root)
    {
        foreach (var renderer in root.GetComponentsInChildren<SkinnedMeshRenderer>(true, true))
        {
            var mesh = renderer.SharedMesh.Res; Assert.NotNull(mesh);
            var bones = renderer.Bones; Assert.NotNull(bones); Assert.Equal(bones.Length, mesh!.BindPoses!.Length);
            for (int i = 0; i < bones.Length; i++)
            {
                var skin = renderer.Transform.WorldToLocalMatrix * bones[i]!.LocalToWorldMatrix * mesh.BindPoses[i];
                Near(Float3.UnitX, new Float3(skin.c0.X, skin.c0.Y, skin.c0.Z));
                Near(Float3.UnitY, new Float3(skin.c1.X, skin.c1.Y, skin.c1.Z));
                Near(Float3.UnitZ, new Float3(skin.c2.X, skin.c2.Y, skin.c2.Z));
                // Absolute residual scales with the source translation magnitude.
                float tolerance = 2e-4f * MathF.Max(1f, Float3.Length(bones[i]!.LocalPosition));
                Assert.InRange(Float3.Length(new Float3(skin.c3.X, skin.c3.Y, skin.c3.Z)), 0, tolerance);
            }
        }
    }
    private static void DisposeImport(ModelImportResult data)
    {
        data.RootGO?.Dispose(); foreach (var mesh in data.Meshes) mesh.Dispose();
        foreach (var material in data.Materials) material.Dispose(); foreach (var clip in data.Animations) clip.Dispose();
    }
}
