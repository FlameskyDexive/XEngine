using System.Reflection;
using System.Text.Json;
using XEngine.Echo;
using XEngine.Editor.Importers;
using XEngine.Runtime;
using XEngine.Runtime.AssetImporting;
using XEngine.Runtime.Resources;
using XEngine.Vector;
using Xunit;
using Xunit.Abstractions;

namespace XEngine.Editor.Test;

public sealed class ModelSkinInfluenceTests(ITestOutputHelper output) : EditorTestHarness
{
    internal static string SkinnedGltf(float[] weights)
    {
        var bytes = new List<byte>(); var views = new List<object>(); var accessors = new List<object>();
        int Add(byte[] data, int count, string type, int componentType)
        {
            while (bytes.Count % 4 != 0) bytes.Add(0);
            views.Add(new { buffer = 0, byteOffset = bytes.Count, byteLength = data.Length }); bytes.AddRange(data);
            accessors.Add(new { bufferView = views.Count - 1, componentType, count, type }); return accessors.Count - 1;
        }
        byte[] Floats(float[] values)
        { var data = new byte[values.Length * 4]; Buffer.BlockCopy(values, 0, data, 0, data.Length); return data; }
        var attributes = new Dictionary<string, int> { ["POSITION"] = Add(Floats([0,0,0, 1,0,0, 0,1,0]), 3, "VEC3", 5126) };
        for (int set = 0; set < weights.Length / 4; set++)
        {
            var joints = new byte[24]; var values = new float[12];
            for (int v = 0; v < 3; v++) for (int k = 0; k < 4; k++)
            {
                BitConverter.GetBytes((ushort)(set * 4 + k)).CopyTo(joints, (v * 4 + k) * 2);
                values[v * 4 + k] = v == 2 ? 0f : weights[set * 4 + k];
            }
            attributes["JOINTS_" + set] = Add(joints, 3, "VEC4", 5123);
            attributes["WEIGHTS_" + set] = Add(Floats(values), 3, "VEC4", 5126);
        }
        var nodes = new List<object>(); var children = new int[weights.Length + 1];
        var boneNodes = new int[weights.Length];
        for (int i = 0; i < children.Length; i++) children[i] = i + 1;
        for (int i = 0; i < boneNodes.Length; i++) boneNodes[i] = i + 2;
        nodes.Add(new { name = "Root", children }); nodes.Add(new { name = "Body", mesh = 0, skin = 0 });
        for (int i = 0; i < weights.Length; i++) nodes.Add(new { name = "Bone" + i });
        return JsonSerializer.Serialize(new
        {
            asset = new { version = "2.0" }, scene = 0, scenes = new[] { new { nodes = new[] { 0 } } }, nodes,
            meshes = new[] { new { name = "Body", primitives = new[] { new { attributes } } } },
            skins = new[] { new { joints = boneNodes } },
            buffers = new[] { new { byteLength = bytes.Count, uri = "data:application/octet-stream;base64," + Convert.ToBase64String(bytes.ToArray()) } },
            bufferViews = views, accessors,
        });
    }

    private static float[] SourceWeights(int count)
    {
        if (count == 8) return [.02f, .03f, .05f, .08f, .30f, .22f, .18f, .12f];
        var weights = new float[count];
        for (int i = 0; i < count; i++) weights[i] = .001f;
        weights[35] = .4f; weights[39] = .3f; weights[33] = .2f; weights[34] = weights[38] = .1f;
        return weights;
    }

    private static int[] Strongest(float[] weights, int count)
    {
        var indices = new List<int>();
        for (int i = 0; i < weights.Length; i++)
        {
            int at = 0; while (at < indices.Count && weights[indices[at]] >= weights[i]) at++;
            indices.Insert(at, i);
        }
        return indices.GetRange(0, count).ToArray();
    }
    private static float Component(Float4 v, int i) => i switch { 0 => v.X, 1 => v.Y, 2 => v.Z, _ => v.W };

    [Theory]
    [InlineData(8, 1)] [InlineData(8, 2)] [InlineData(8, 4)]
    [InlineData(40, 1)] [InlineData(40, 2)] [InlineData(40, 4)]
    [InlineData(8, 0)] [InlineData(8, -1)] [InlineData(8, 3)] [InlineData(8, 8)] [InlineData(8, 100)]
    public void EditorKeepsStrongestSupportedLimitAndLeavesUnweightedVertex(int sourceCount, int requested)
    {
        var weights = SourceWeights(sourceCount);
        string path = AssetAbsolutePath("Influences.gltf"); File.WriteAllText(path, SkinnedGltf(weights));
        var settings = EchoObject.NewCompound(); settings["skinBoneCount"] = new EchoObject(requested);
        int limit = requested is 1 or 2 or 4 ? requested : 4;
        var ctx = new ImportContext(Guid.NewGuid(), path, settings);
        try
        {
            Assert.True(new EditorModelImporter().Import(ctx), ctx.Error);
            var mesh = Assert.Single(ctx.SubAssets.OfType<Mesh>());
            var best = Strongest(weights, limit); float sum = 0;
            foreach (int index in best) sum += weights[index];
            for (int v = 0; v < 2; v++) for (int k = 0; k < 4; k++)
            {
                Assert.Equal(k < limit ? best[k] + 1 : 0, (int)Component(mesh.BoneIndices![v], k));
                Assert.Equal(k < limit ? weights[best[k]] / sum : 0, Component(mesh.BoneWeights![v], k), 5);
            }
            Assert.Equal(Float4.Zero, mesh.BoneWeights![2]);
            Assert.Equal(Float4.Zero, mesh.BoneIndices![2]);
            Assert.Equal(sourceCount, mesh.BoneNames!.Length); Assert.Equal(sourceCount, mesh.BindPoses!.Length);
            Assert.Equal(requested, settings["skinBoneCount"].IntValue);
        }
        finally { ctx.MainAsset?.Dispose(); foreach (var asset in ctx.SubAssets) asset.Dispose(); }
    }

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(4)]
    public void EqualWeightsRetainSourceOrderDeterministically(int limit)
    {
        string path = AssetAbsolutePath("Ties.gltf"); File.WriteAllText(path, SkinnedGltf([.125f,.125f,.125f,.125f,.125f,.125f,.125f,.125f]));
        var settings = EchoObject.NewCompound(); settings["skinBoneCount"] = new EchoObject(limit);
        var ctx = new ImportContext(Guid.NewGuid(), path, settings);
        try
        {
            Assert.True(new EditorModelImporter().Import(ctx), ctx.Error);
            var mesh = Assert.Single(ctx.SubAssets.OfType<Mesh>());
            for (int k = 0; k < 4; k++)
            {
                Assert.Equal(k < limit ? k + 1 : 0, (int)Component(mesh.BoneIndices![0], k));
                Assert.Equal(k < limit ? 1f / limit : 0f, Component(mesh.BoneWeights![0], k), 5);
            }
        }
        finally { ctx.MainAsset?.Dispose(); foreach (var asset in ctx.SubAssets) asset.Dispose(); }
    }

    private sealed class NullResolver : IModelTextureResolver
    {
        public AssetRef<Texture2D> ResolveExternal(string path) => default;
        public AssetRef<Texture2D> ResolveEmbedded(string? name, byte[] bytes, string? mime) => default;
    }

    private static ModelImportResult Import(string path, int count)
    {
        // Reflection keeps the same behavior test runnable against the pre-feature baseline.
        object settings = new ModelImporterSettings { TextureResolver = new NullResolver() };
        typeof(ModelImporterSettings).GetField("SkinBoneCount")?.SetValue(settings, count);
        return new ModelImporter().Import(new FileInfo(path), (ModelImporterSettings)settings);
    }

    [Theory]
    [InlineData("Anbi.FBX")] [InlineData("Anbi@Idle.FBX")]
    public void RealAnbiLimitsKeepBonesBindsGeometryAndMeasureFixedTimePoseLoss(string fileName)
    {
        string path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "../../../../ZonezeroTestProject/Assets/ZZZ/Arts/PlayerModel/Anbi", fileName));
        var baseline = Import(path, 4); var one = Import(path, 1); var two = Import(path, 2);
        try
        {
            foreach (var pair in new[] { (data: one, limit: 1), (data: two, limit: 2) })
            {
                Assert.Equal(baseline.Meshes.Count, pair.data.Meshes.Count);
                for (int m = 0; m < baseline.Meshes.Count; m++)
                {
                    var a = baseline.Meshes[m]; var b = pair.data.Meshes[m];
                    Assert.Equal(a.Name, b.Name); Assert.Equal(a.Vertices, b.Vertices);
                    Assert.Equal(a.BoneNames, b.BoneNames); Assert.Equal(a.BindPoses, b.BindPoses);
                    for (int v = 0; v < a.Vertices.Length; v++)
                    {
                        float sum = 0; for (int k = 0; k < pair.limit; k++) sum += Component(a.BoneWeights![v], k);
                        for (int k = 0; k < 4; k++)
                        {
                            float expected = k < pair.limit && sum > 1e-6f ? Component(a.BoneWeights![v], k) / sum : 0;
                            Assert.Equal(expected, Component(b.BoneWeights![v], k), 5);
                            if (expected > 0) Assert.Equal(Component(a.BoneIndices![v], k), Component(b.BoneIndices![v], k));
                        }
                    }
                }
            }
            if (baseline.Animations.Count == 0) return;
            var clip = Assert.Single(baseline.Animations);
            foreach (float time in new[] { .5f, 2.5f })
            {
                foreach (var bone in clip.Bones)
                {
                    var transform = baseline.RootGO!.Transform.Find(bone.BoneName); Assert.NotNull(transform);
                    var p = transform!.LocalPosition;
                    transform.LocalPosition = new Float3(bone.PosX?.Evaluate(time) ?? p.X,
                        bone.PosY?.Evaluate(time) ?? p.Y, bone.PosZ?.Evaluate(time) ?? p.Z);
                    if (bone.RotX != null) transform.LocalRotation = bone.EvaluateRotationAt(time);
                    if (bone.ScaleX != null) transform.LocalScale = bone.EvaluateScaleAt(time);
                }
                foreach (var renderer in baseline.RootGO!.GetComponentsInChildren<SkinnedMeshRenderer>(true, true))
                {
                    var mesh = renderer.SharedMesh.Res!; int index = baseline.Meshes.IndexOf(mesh);
                    var matrices = new Float4x4[renderer.Bones!.Length];
                    for (int i = 0; i < matrices.Length; i++) matrices[i] = renderer.Transform.WorldToLocalMatrix
                        * renderer.Bones[i]!.LocalToWorldMatrix * mesh.BindPoses![i];
                    foreach (var pair in new[] { (data: one, limit: 1), (data: two, limit: 2) })
                    {
                        double squares = 0; float max = 0;
                        for (int v = 0; v < mesh.Vertices.Length; v++)
                        {
                            float distance = Float3.Length(Skin(mesh, v, matrices) - Skin(pair.data.Meshes[index], v, matrices));
                            Assert.True(float.IsFinite(distance)); max = MathF.Max(max, distance); squares += distance * distance;
                        }
                        output.WriteLine(JsonSerializer.Serialize(new { fileName, mesh = mesh.Name, time,
                            limit = pair.limit, rmsSourceMeters = Math.Sqrt(squares / mesh.Vertices.Length), maxSourceMeters = max }));
                    }
                }
            }
        }
        finally { Dispose(baseline); Dispose(one); Dispose(two); }
    }

    private static Float3 Skin(Mesh mesh, int v, Float4x4[] matrices)
    {
        var p = mesh.Vertices[v]; var result = Float3.Zero; float sum = 0;
        for (int k = 0; k < 4; k++)
        {
            float w = Component(mesh.BoneWeights![v], k); if (w <= 0) continue;
            var m = matrices[(int)Component(mesh.BoneIndices![v], k) - 1]; sum += w;
            result += new Float3(m.c0.X*p.X+m.c1.X*p.Y+m.c2.X*p.Z+m.c3.X,
                m.c0.Y*p.X+m.c1.Y*p.Y+m.c2.Y*p.Z+m.c3.Y,
                m.c0.Z*p.X+m.c1.Z*p.Y+m.c2.Z*p.Z+m.c3.Z) * w;
        }
        return sum == 0 ? p : result;
    }
    private static void Dispose(ModelImportResult data)
    {
        data.RootGO?.Dispose(); foreach (var mesh in data.Meshes) mesh.Dispose();
        foreach (var mat in data.Materials) mat.Dispose(); foreach (var clip in data.Animations) clip.Dispose();
    }
}
