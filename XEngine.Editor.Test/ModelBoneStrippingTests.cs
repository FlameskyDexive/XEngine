using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using XEngine.Echo;
using XEngine.Editor.Importers;
using XEngine.Runtime;
using XEngine.Runtime.AssetImporting;
using XEngine.Runtime.Resources;
using XEngine.Vector;
using Xunit;
using Xunit.Abstractions;

namespace XEngine.Editor.Test;

public sealed class ModelBoneStrippingTests(ITestOutputHelper output) : EditorTestHarness
{
    private sealed class NullResolver : IModelTextureResolver
    {
        public AssetRef<Texture2D> ResolveExternal(string path) => default;
        public AssetRef<Texture2D> ResolveEmbedded(string? name, byte[] bytes, string? mime) => default;
    }

    private static float Part(Float4 v, int i) => i switch { 0 => v.X, 1 => v.Y, 2 => v.Z, _ => v.W };

    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(4)]
    public void StripCompactsPaletteButKeepsEveryTransformRootAndAttachment(int limit)
    {
        var doc = JsonNode.Parse(ModelSkinInfluenceTests.SkinnedGltf([.1f,.2f,.3f,.4f,0,0,0,0]))!;
        var nodes = doc["nodes"]!.AsArray();
        nodes[9]!["children"] = new JsonArray(10);
        nodes.Add(new JsonObject { ["name"] = "WeaponSocket", ["translation"] = new JsonArray(1,2,3) });
        string path = AssetAbsolutePath("Strip.gltf"); File.WriteAllText(path, doc.ToJsonString());
        var off = Import(path, limit, false); var on = Import(path, limit, true);
        try
        {
            var a = Assert.Single(off.Meshes); var b = Assert.Single(on.Meshes);
            Assert.Equal(8, a.BoneNames!.Length); Assert.Equal(limit, b.BoneNames!.Length);
            Assert.Equal(a.Vertices, b.Vertices); Assert.Equal(a.BoneWeights, b.BoneWeights);
            Assert.Equal(Hierarchy(off.RootGO!), Hierarchy(on.RootGO!));
            Assert.NotNull(on.RootGO!.Transform.Find(a.BoneNames[7] + "/WeaponSocket"));
            var ar = Assert.Single(off.RootGO!.GetComponentsInChildren<SkinnedMeshRenderer>(true, true));
            var br = Assert.Single(on.RootGO!.GetComponentsInChildren<SkinnedMeshRenderer>(true, true));
            Assert.Equal(ar.RootBonePath, br.RootBonePath); Assert.Equal(b.BoneNames, br.BonePaths);
            Assert.Equal(limit, br.Bones!.Length); Assert.DoesNotContain(br.Bones, t => t == null);
            Assert.Equal(Float4.Zero, b.BoneIndices![2]);
            CheckRemap(a, b);
        }
        finally { Dispose(off); Dispose(on); }
    }

    [Fact]
    public void AllUnweightedPaletteCanBeEmptyWhileRootAndTransformsRemain()
    {
        string path = AssetAbsolutePath("Zero.gltf");
        File.WriteAllText(path, ModelSkinInfluenceTests.SkinnedGltf(new float[8]));
        var data = Import(path, 4, true);
        try
        {
            var mesh = Assert.Single(data.Meshes);
            Assert.Empty(mesh.BoneNames!); Assert.Empty(mesh.BindPoses!);
            Assert.All(mesh.BoneWeights!, w => Assert.Equal(Float4.Zero, w));
            Assert.All(mesh.BoneIndices!, w => Assert.Equal(Float4.Zero, w));
            Assert.Contains(Hierarchy(data.RootGO!), row => row.Contains("/Bone7|"));
            var renderer = Assert.Single(data.RootGO.GetComponentsInChildren<SkinnedMeshRenderer>(true, true));
            Assert.Empty(renderer.Bones!); Assert.NotNull(renderer.RootBone);
        }
        finally { Dispose(data); }
    }

    [Theory]
    [InlineData(null, 8)] [InlineData(false, 8)] [InlineData(true, 4)]
    public void EditorUsesOffForMissingSettingAndHonorsExplicitValues(bool? strip, int expected)
    {
        string path = AssetAbsolutePath("EditorStrip.gltf");
        File.WriteAllText(path, ModelSkinInfluenceTests.SkinnedGltf([.1f,.2f,.3f,.4f,0,0,0,0]));
        var settings = EchoObject.NewCompound();
        if (strip.HasValue) settings["stripBones"] = new EchoObject(strip.Value);
        var before = settings.WriteToString(); var ctx = new ImportContext(Guid.NewGuid(), path, settings);
        try
        {
            Assert.True(new EditorModelImporter().Import(ctx), ctx.Error);
            Assert.Equal(expected, Assert.Single(ctx.SubAssets.OfType<Mesh>()).BoneNames!.Length);
            Assert.Equal(before, settings.WriteToString());
        }
        finally { ctx.MainAsset?.Dispose(); foreach (var asset in ctx.SubAssets) asset.Dispose(); }
    }

    [Theory]
    [InlineData("Anbi.FBX", 1)] [InlineData("Anbi.FBX", 4)]
    [InlineData("Anbi@Idle.FBX", 1)] [InlineData("Anbi@Idle.FBX", 4)]
    public void RealModelRetainsHierarchyAndFixedTimeDeformationAcrossPaletteReplacement(string file, int limit)
    {
        string path = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "../../../../ZonezeroTestProject/Assets/ZZZ/Arts/PlayerModel/Anbi", file));
        var off = Import(path, limit, false); var on = Import(path, limit, true);
        try
        {
            Assert.Equal(Hierarchy(off.RootGO!), Hierarchy(on.RootGO!));
            Assert.Equal(off.Animations.Count, on.Animations.Count);
            for (int c = 0; c < off.Animations.Count; c++)
            {
                var a = EchoObject.NewCompound(); var b = EchoObject.NewCompound();
                off.Animations[c].Serialize(ref a, new SerializationContext());
                on.Animations[c].Serialize(ref b, new SerializationContext());
                Assert.Equal(a.WriteToString(), b.WriteToString());
                foreach (var binding in on.Animations[c].Bones)
                    Assert.NotNull(on.RootGO!.Transform.Find(binding.BoneName));
            }
            for (int i = 0; i < off.Meshes.Count; i++) CheckRemap(off.Meshes[i], on.Meshes[i]);
            var before = new List<SkinnedMeshRenderer>(off.RootGO!.GetComponentsInChildren<SkinnedMeshRenderer>(true, true));
            var after = new List<SkinnedMeshRenderer>(on.RootGO!.GetComponentsInChildren<SkinnedMeshRenderer>(true, true));
            for (int r = 0; r < before.Count; r++)
            {
                var a = before[r]; var b = after[r]; Assert.Equal(a.RootBonePath, b.RootBonePath);
                Assert.NotNull(b.RootBone); Assert.DoesNotContain(b.Bones!, t => t == null);
                // An already resolved scene-style renderer must follow the new mesh palette,
                // without requiring a manual OnValidate or changing its Root Bone override.
                _ = a.Bones;
                a.SharedMesh = b.SharedMesh;
                Assert.Equal(b.BonePaths!.Length, a.Bones!.Length);
                for (int k = 0; k < b.BonePaths.Length; k++)
                    Assert.Same(off.RootGO.Transform.Find(b.BonePaths[k]), a.Bones[k]);
                a.SharedMesh = new AssetRef<Mesh>(off.Meshes[r]); a.OnValidate();
            }
            foreach (float time in new[] { .5f, 2.5f })
            {
                Pose(off, time); Pose(on, time);
                for (int r = 0; r < before.Count; r++)
                {
                    var a = before[r]; var b = after[r]; float max = 0;
                    for (int v = 0; v < a.SharedMesh.Res!.Vertices.Length; v++)
                    {
                        float d = Float3.Length(Skin(a, v) - Skin(b, v));
                        Assert.True(float.IsFinite(d)); max = MathF.Max(max, d);
                    }
                    Assert.InRange(max, 0, 1e-6f);
                    output.WriteLine(JsonSerializer.Serialize(new { file, limit, time, mesh = a.SharedMesh.Res.Name,
                        before = off.Meshes[r].BoneNames!.Length, after = b.Bones!.Length, maxSourceMeters = max }));
                }
            }
        }
        finally { Dispose(off); Dispose(on); }
    }

    private static void CheckRemap(Mesh before, Mesh after)
    {
        Assert.Equal(before.Vertices, after.Vertices); Assert.Equal(before.BoneWeights, after.BoneWeights);
        var used = new bool[before.BoneNames!.Length];
        foreach (var ids in before.BoneIndices!) for (int k = 0; k < 4; k++)
            if (Part(ids, k) > 0) used[(int)Part(ids, k) - 1] = true;
        var kept = new List<string>(); for (int i = 0; i < used.Length; i++) if (used[i]) kept.Add(before.BoneNames[i]);
        Assert.Equal(kept, after.BoneNames!);
        for (int i = 0; i < after.BoneNames!.Length; i++)
            Assert.Equal(before.BindPoses![Array.IndexOf(before.BoneNames, after.BoneNames[i])], after.BindPoses![i]);
        for (int v = 0; v < before.Vertices.Length; v++) for (int k = 0; k < 4; k++)
        {
            int a = (int)Part(before.BoneIndices![v], k), b = (int)Part(after.BoneIndices![v], k);
            if (a == 0) Assert.Equal(0, b); else Assert.Equal(before.BoneNames[a - 1], after.BoneNames[b - 1]);
        }
    }

    private static string[] Hierarchy(GameObject root)
    {
        var rows = new List<string>();
        void Visit(Transform t)
        {
            rows.Add(Transform.GetRelativePath(t, root.Transform) + "|" + t.LocalPosition + "|" + t.LocalRotation + "|" + t.LocalScale);
            for (int i = 0; i < t.ChildCount; i++) Visit(t.GetChild(i));
        }
        Visit(root.Transform); return rows.ToArray();
    }

    private static ModelImportResult Import(string path, int limit, bool strip)
    {
        object settings = new ModelImporterSettings { TextureResolver = new NullResolver(), SkinBoneCount = limit };
        typeof(ModelImporterSettings).GetField("StripBones")?.SetValue(settings, strip);
        return new ModelImporter().Import(new FileInfo(path), (ModelImporterSettings)settings);
    }

    private static void Pose(ModelImportResult data, float time)
    {
        if (data.Animations.Count == 0) return;
        foreach (var bone in data.Animations[0].Bones)
        {
            var t = data.RootGO!.Transform.Find(bone.BoneName)!; var p = t.LocalPosition;
            t.LocalPosition = new Float3(bone.PosX?.Evaluate(time) ?? p.X, bone.PosY?.Evaluate(time) ?? p.Y, bone.PosZ?.Evaluate(time) ?? p.Z);
            if (bone.RotX != null) t.LocalRotation = bone.EvaluateRotationAt(time);
            if (bone.ScaleX != null) t.LocalScale = bone.EvaluateScaleAt(time);
        }
    }

    private static Float3 Skin(SkinnedMeshRenderer r, int v)
    {
        var m = r.SharedMesh.Res!; var p = m.Vertices[v]; var result = Float3.Zero; float sum = 0;
        for (int k = 0; k < 4; k++)
        {
            float w = Part(m.BoneWeights![v], k); if (w <= 0) continue;
            int b = (int)Part(m.BoneIndices![v], k) - 1;
            var matrix = r.Transform.WorldToLocalMatrix * r.Bones![b]!.LocalToWorldMatrix * m.BindPoses![b];
            result += Float4x4.TransformPoint(p, matrix) * w; sum += w;
        }
        return sum == 0 ? p : result;
    }

    private static void Dispose(ModelImportResult data)
    {
        data.RootGO?.Dispose(); foreach (var mesh in data.Meshes) mesh.Dispose();
        foreach (var mat in data.Materials) mat.Dispose(); foreach (var clip in data.Animations) clip.Dispose();
    }
}
