using System.Text;

using XEngine.Editor;
using XEngine.Editor.Importers;
using XEngine.Genshin.Editor;
using XEngine.UnityImporter.Editor;
using XEngine.Runtime;
using XEngine.Runtime.Resources;
using XEngine.Vector;

using Xunit;

namespace XEngine.Editor.Test;

/// <summary>
/// Headless acceptance for the Unity YAML importers: claims the shared extensions, delegates
/// native files, converts fixture + real KeQing meshes/prefabs into engine assets, and resolves
/// Unity-guid cross-references through the copy-time sidecar. Materials/textures are exercised
/// in the live editor run (GUI evidence m1) — headless tests must not touch GPU-bound imports.
/// </summary>
public sealed class GenshinUnityImporterTests : EditorTestHarness
{
    private const string MeshGuid = "11111111111111111111111111111111";
    private const string MatGuid = "22222222222222222222222222222222";

    [Fact]
    public void GetImporter_UnityExtensions_AreClaimedWithDelegation()
    {
        EditorRegistries.Initialize();
        Assert.IsType<UnityMeshImporter>(EditorRegistries.GetImporter(".mesh"));
        Assert.IsType<UnityPrefabImporter>(EditorRegistries.GetImporter(".prefab"));
        Assert.IsType<UnityMatImporter>(EditorRegistries.GetImporter(".mat"));
        Assert.IsType<TextureImporter>(EditorRegistries.GetImporter(".png"));
    }

    [Fact]
    public void Sniffer_DelegatesNativeFiles()
    {
        string dir = Path.Combine(Project.AssetsPath, "Native");
        Directory.CreateDirectory(dir);
        string nativeMesh = Path.Combine(dir, "native.mesh");
        File.WriteAllText(nativeMesh, "{ \"$type\": \"XEngine.Runtime.Resources.Mesh\" }");
        string unityMesh = Path.Combine(dir, "unity.mesh");
        File.WriteAllText(unityMesh, "%YAML 1.1\n%TAG !u! tag:unity3d.com,2011:\n--- !u!43 &4300000\nMesh:\n  m_Name: X\n");
        Assert.False(UnityAssetSniffer.IsUnityYaml(nativeMesh));
        Assert.True(UnityAssetSniffer.IsUnityYaml(unityMesh));
    }

    [Fact]
    public void FixtureUnityMeshAndPrefab_ImportAndWireThroughSidecar()
    {
        string unitDir = Path.Combine(Project.AssetsPath, "Unit", "Fixture");
        Directory.CreateDirectory(unitDir);
        File.WriteAllText(Path.Combine(unitDir, "TestMesh.mesh"), BuildFixtureMeshYaml());
        File.WriteAllText(Path.Combine(unitDir, "TestMat.mat"), BuildFixtureMatYaml());
        File.WriteAllText(Path.Combine(unitDir, "FixtureUnit.prefab"), BuildFixturePrefabYaml());
        File.WriteAllText(Path.Combine(unitDir, UnityGuidMap.MapFileName),
            $$"""{ "{{MeshGuid}}": "TestMesh.mesh", "{{MatGuid}}": "TestMat.mat" }""");
        UnityGuidMap.InvalidateCache();

        Guid meshGuid = Assets.ImportFile("Unit/Fixture/TestMesh.mesh");
        Assert.NotEqual(Guid.Empty, meshGuid);

        var mesh = AssetDatabase.Get(meshGuid) as Mesh;
        Assert.NotNull(mesh);
        Assert.Equal(4, mesh.VertexCount);
        Assert.Equal(6, (int)mesh.IndexCount);
        Assert.Equal(1, mesh.SubMeshCount);
        Assert.Equal(2, mesh.BindPoses!.Length);
        Assert.True(mesh.HasBoneIndices);
        // Engine convention: 1-based bone indices, 0 slots zero-weighted.
        // Fixture v2 = weights (0.5, 0.5, 0, 0) on bones (1, 0, 0, 0) → indices (2, 1, 0, 0).
        Assert.Equal(new Float4(2f, 1f, 0f, 0f), mesh.BoneIndices![1]);
        Assert.Equal(new Float4(0.5f, 0.5f, 0f, 0f), mesh.BoneWeights![1]);

        Guid prefabGuid = Assets.ImportFile("Unit/Fixture/FixtureUnit.prefab");
        Assert.NotEqual(Guid.Empty, prefabGuid);

        var model = AssetDatabase.Get(prefabGuid) as PrefabAsset;
        Assert.NotNull(model);
        Assert.NotNull(model.GameObjectData);
        GameObject? root = model.Instantiate();
        Assert.NotNull(root);
        Assert.Equal("Root", root.Name);
        Assert.NotNull(root.Transform.Find("BoneA"));
        Assert.NotNull(root.Transform.Find("BoneB"));

        var renderers = new List<SkinnedMeshRenderer>();
        CollectSkinned(root, renderers);
        var smr = Assert.Single(renderers);
        Assert.Equal(meshGuid, smr.SharedMesh.AssetID);
        Assert.NotNull(smr.BonePaths);
        Assert.Equal(2, smr.BonePaths.Length);
        Assert.Equal("BoneA", smr.RootBonePath);
        smr.Dispose();
        root.Dispose();
    }

    [Fact]
    public void RealKeQing_ImportsThroughBackend_OrSkip()
    {
        if (!Directory.Exists(GenshinUnityAssetCopier.DefaultSourceRoot)) return;

        GenshinUnityAssetCopier.CopyResult copy =
            GenshinUnityAssetCopier.CopyUnits(GenshinUnityAssetCopier.DefaultSourceRoot,
                new[] { "KeQing", "Common" }, Path.Combine(Project.AssetsPath, "Unit"));
        Assert.Empty(copy.Warnings);
        Assert.True(copy.FilesCopied >= 12, $"expected >= 12 copied files, got {copy.FilesCopied}");
        Assert.True(copy.TexturesAligned >= 4, $"expected >= 4 aligned textures, got {copy.TexturesAligned}");
        Assert.True(File.Exists(Path.Combine(Project.AssetsPath, "Unit", UnityGuidMap.MapFileName)));

        // Meshes first so the prefab's sidecar lookups find entries.
        int meshCount = 0, totalBones = 0, totalVertices = 0;
        foreach (string meshPath in Directory.GetFiles(Path.Combine(Project.AssetsPath, "Unit", "KeQing", "Models"), "*.mesh"))
        {
            Guid guid = Assets.ImportFile(Path.GetRelativePath(Project.AssetsPath, meshPath).Replace('\\', '/'));
            Assert.NotEqual(Guid.Empty, guid);
            var mesh = AssetDatabase.Get(guid) as Mesh;
            Assert.NotNull(mesh);
            meshCount++;
            totalVertices += mesh.VertexCount;
            totalBones = Math.Max(totalBones, mesh.BindPoses?.Length ?? 0);
        }
        Assert.True(meshCount >= 5, $"expected >= 5 meshes, got {meshCount}");
        // True vertex total: Body 14021 + Brow 52 + EffectMesh 102 + EyeStar 50 + Face 939 + Face_Eye 1173 = 16337.
        Assert.True(totalVertices >= 16337, $"expected >= 16337 vertices, got {totalVertices}");
        Assert.True(totalBones >= 80, $"expected >= 80 bones, got {totalBones}");

        Guid prefabGuid = Assets.ImportFile("Unit/KeQing/Prefabs/Girl_Keqing.prefab");
        Assert.NotEqual(Guid.Empty, prefabGuid);
        var model = AssetDatabase.Get(prefabGuid) as PrefabAsset;
        Assert.NotNull(model);
        GameObject? root = model.Instantiate();
        Assert.NotNull(root);
        var renderers = new List<SkinnedMeshRenderer>();
        CollectSkinned(root, renderers);
        Assert.True(renderers.Count >= 5, $"expected >= 5 skinned renderers, got {renderers.Count}");
        foreach (var smr in renderers)
            Assert.NotEqual(Guid.Empty, smr.SharedMesh.AssetID);
        int boneCount = CountHierarchy(root);
        Assert.True(boneCount >= 120, $"expected >= 120 nodes, got {boneCount}");

        WriteM1Evidence(meshCount, totalBones, totalVertices, renderers.Count, boneCount, copy);

        foreach (var smr in renderers) smr.Dispose();
        root.Dispose();
    }

    private static void CollectSkinned(GameObject go, List<SkinnedMeshRenderer> result)
    {
        var smr = go.GetComponent<SkinnedMeshRenderer>();
        if (smr != null) result.Add(smr);
        foreach (GameObject child in go.Children)
            CollectSkinned(child, result);
    }

    private static int CountHierarchy(GameObject go)
    {
        int count = 1;
        foreach (GameObject child in go.Children)
            count += CountHierarchy(child);
        return count;
    }

    private static void WriteM1Evidence(int meshCount, int bones, int vertices, int renderers, int nodes, GenshinUnityAssetCopier.CopyResult copy)
    {
        string repo = FindRepositoryRoot();
        string dir = Path.Combine(repo, "docs", "superpowers", "evidence", "genshin");
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, "m1-keqing-convert.md"),
            "# M1 Unity YAML 导入器（替代初版 .glb 方案）\n\n" +
            "Date: 2026-08-20. Branch: `main`.\n\n" +
            "方案（用户决策）：资源连文件拷入 XEngine 工程（不拷 Unity .meta），meta 由引擎生成——纹理" +
            "导入设置（sRGB/mipmap/wrap/filter）从源 Unity .meta 对齐进引擎 meta；引擎侧补齐 " +
            "`Texture2D.Srgb` 并让 TextureImporter 真正读取。跨引用通过引擎生成的 " +
            "`unity-guid-map.json` sidecar 解析；GUID 由源 Unity guid 确定性派生（跨机器一致）。\n\n" +
            $"- 拷贝文件 {copy.FilesCopied} 个（纹理设置对齐 {copy.TexturesAligned} 张）\n" +
            $"- 导入网格 {meshCount} 个，总顶点 {vertices}，最大骨骼数 {bones}\n" +
            $"- Girl_Keqing.prefab → Model：{renderers} 个 SkinnedMeshRenderer，{nodes} 个节点\n\n" +
            "导入器以 Priority=100 注册 .mesh/.prefab/.mat，文件头嗅探（%YAML 1.1）非 Unity 文件委托" +
            "引擎原生导入器。坐标系：Unity LH 直通。关键坑（皮肤通道声明格式互换/流分块/脚本管线无" +
            "ImplicitUsings/方向光 -forward 约定）详见 GenshinLightingProbeTests 与排查记录。\n\n" +
            "视觉验收：m1-keqing-editor.png（编辑器实跑，贴图完整；M1 为 Standard 占位，二渲在 M3/M4）。\n");
    }

    // ── Fixture YAML (values little-endian hex, mirroring Unity's serialized mesh layout) ──

    private static string Hex(byte[] bytes) => BitConverter.ToString(bytes).Replace("-", "").ToLowerInvariant();

    private static string BuildFixtureMeshYaml()
    {
        float[][] positions = { new[] { 0f, 0f, 0f }, new[] { 1f, 0f, 0f }, new[] { 0f, 1f, 0f }, new[] { 1f, 1f, 0f } };
        float[][] normals = { new[] { 0f, 0f, -1f }, new[] { 0f, 0f, -1f }, new[] { 0f, 0f, -1f }, new[] { 0f, 0f, -1f } };
        float[][] uvs = { new[] { 0f, 0f }, new[] { 1f, 0f }, new[] { 0f, 1f }, new[] { 1f, 1f } };
        float[][] weights = { new[] { 1f, 0f, 0f, 0f }, new[] { 0.5f, 0.5f, 0f, 0f }, new[] { 0.25f, 0.75f, 0f, 0f }, new[] { 0f, 1f, 0f, 0f } };
        uint[][] indices = { new uint[] { 0, 0, 0, 0 }, new uint[] { 1, 0, 0, 0 }, new uint[] { 0, 1, 0, 0 }, new uint[] { 1, 1, 0, 0 } };

        var stream0 = new MemoryStream();
        var stream2 = new MemoryStream();
        for (int v = 0; v < 4; v++)
        {
            foreach (float f in positions[v]) stream0.Write(BitConverter.GetBytes(f));
            foreach (float f in normals[v]) stream0.Write(BitConverter.GetBytes(f));
            foreach (float f in weights[v]) stream2.Write(BitConverter.GetBytes(f));
            foreach (uint i in indices[v]) stream2.Write(BitConverter.GetBytes(i));
        }

        var indexBuffer = new MemoryStream();
        foreach (ushort i in new ushort[] { 0, 1, 2, 2, 1, 3 })
            indexBuffer.Write(BitConverter.GetBytes(i));

        var yaml = new StringBuilder();
        yaml.AppendLine("%YAML 1.1");
        yaml.AppendLine("%TAG !u! tag:unity3d.com,2011:");
        yaml.AppendLine("--- !u!43 &4300000");
        yaml.AppendLine("Mesh:");
        yaml.AppendLine("  m_ObjectHideFlags: 0");
        yaml.AppendLine("  m_Name: TestMesh");
        yaml.AppendLine("  m_SubMeshes:");
        yaml.AppendLine("  - serializedVersion: 2");
        yaml.AppendLine("    firstByte: 0");
        yaml.AppendLine("    indexCount: 6");
        yaml.AppendLine("    topology: 0");
        yaml.AppendLine("    baseVertex: 0");
        yaml.AppendLine("    firstVertex: 0");
        yaml.AppendLine("    vertexCount: 4");
        yaml.AppendLine("  m_Shapes:");
        yaml.AppendLine("    vertices: []");
        yaml.AppendLine("  m_BindPose:");
        for (int b = 0; b < 2; b++)
        {
            yaml.AppendLine(b == 0
                ? "  - e00: 1\n    e01: 0\n    e02: 0\n    e03: 0\n    e10: 0\n    e11: 1\n    e12: 0\n    e13: 0\n    e20: 0\n    e21: 0\n    e22: 1\n    e23: 0\n    e30: 0\n    e31: 0\n    e32: 0\n    e33: 1"
                : "  - e00: 1\n    e01: 0\n    e02: 0\n    e03: 1\n    e10: 0\n    e11: 1\n    e12: 0\n    e13: 0\n    e20: 0\n    e21: 0\n    e22: 1\n    e23: 0\n    e30: 0\n    e31: 0\n    e32: 0\n    e33: 1");
        }
        yaml.AppendLine("  m_IndexFormat: 0");
        yaml.AppendLine("  m_IndexBuffer: " + Hex(indexBuffer.ToArray()));
        yaml.AppendLine("  m_VertexData:");
        yaml.AppendLine("    serializedVersion: 3");
        yaml.AppendLine("    m_VertexCount: 4");
        yaml.AppendLine("    m_Channels:");
        yaml.AppendLine("    - stream: 0\n      offset: 0\n      format: 0\n      dimension: 3");
        yaml.AppendLine("    - stream: 0\n      offset: 12\n      format: 0\n      dimension: 3");
        for (int i = 2; i < 12; i++)
            yaml.AppendLine("    - stream: 0\n      offset: 0\n      format: 0\n      dimension: 0");
        yaml.AppendLine("    - stream: 2\n      offset: 0\n      format: 0\n      dimension: 4");
        yaml.AppendLine("    - stream: 2\n      offset: 16\n      format: 10\n      dimension: 4");
        yaml.AppendLine("    m_DataSize: " + (stream0.Length + stream2.Length));
        yaml.AppendLine("    _typelessdata: " + Hex(stream0.ToArray()) + Hex(stream2.ToArray()));
        return yaml.ToString();
    }

    private static string BuildFixtureMatYaml() => """
        %YAML 1.1
        %TAG !u! tag:unity3d.com,2011:
        --- !u!21 &2100000
        Material:
          m_ObjectHideFlags: 0
          m_Name: TestMat
          m_Shader: {fileID: 4800000, guid: fe17be7efff344feb317f2eab5baf08f, type: 3}
          m_SavedProperties:
            serializedVersion: 3
            m_TexEnvs:
            - _BaseMap:
                m_Texture: {fileID: 0}
                m_Scale: {x: 1, y: 1}
                m_Offset: {x: 0, y: 0}
            m_Floats: []
            m_Colors: []
        """;

    private static string BuildFixturePrefabYaml() => """
        %YAML 1.1
        %TAG !u! tag:unity3d.com,2011:
        --- !u!1 &10000000000000001
        GameObject:
          m_Name: Root
          m_Component:
          - component: {fileID: 20000000000000001}
        --- !u!4 &20000000000000001
        Transform:
          m_GameObject: {fileID: 10000000000000001}
          m_LocalRotation: {x: 0, y: 0, z: 0, w: 1}
          m_LocalPosition: {x: 0, y: 0, z: 0}
          m_LocalScale: {x: 1, y: 1, z: 1}
          m_Children:
          - {fileID: 20000000000000002}
          - {fileID: 20000000000000003}
          - {fileID: 20000000000000004}
          m_Father: {fileID: 0}
        --- !u!1 &10000000000000002
        GameObject:
          m_Name: BoneA
          m_Component:
          - component: {fileID: 20000000000000002}
        --- !u!4 &20000000000000002
        Transform:
          m_GameObject: {fileID: 10000000000000002}
          m_LocalRotation: {x: 0, y: 0, z: 0, w: 1}
          m_LocalPosition: {x: 0, y: 0, z: 0}
          m_LocalScale: {x: 1, y: 1, z: 1}
          m_Children: []
          m_Father: {fileID: 20000000000000001}
        --- !u!1 &10000000000000003
        GameObject:
          m_Name: BoneB
          m_Component:
          - component: {fileID: 20000000000000003}
        --- !u!4 &20000000000000003
        Transform:
          m_GameObject: {fileID: 10000000000000003}
          m_LocalRotation: {x: 0, y: 0, z: 0, w: 1}
          m_LocalPosition: {x: 0, y: 1, z: 0}
          m_LocalScale: {x: 1, y: 1, z: 1}
          m_Children: []
          m_Father: {fileID: 20000000000000001}
        --- !u!1 &10000000000000004
        GameObject:
          m_Name: Mesh
          m_Component:
          - component: {fileID: 20000000000000004}
          - component: {fileID: 30000000000000004}
        --- !u!4 &20000000000000004
        Transform:
          m_GameObject: {fileID: 10000000000000004}
          m_LocalRotation: {x: 0, y: 0, z: 0, w: 1}
          m_LocalPosition: {x: 0, y: 0, z: 0}
          m_LocalScale: {x: 1, y: 1, z: 1}
          m_Children: []
          m_Father: {fileID: 20000000000000001}
        --- !u!137 &30000000000000004
        SkinnedMeshRenderer:
          m_GameObject: {fileID: 10000000000000004}
          m_Enabled: 1
          m_CastShadows: 1
          m_Materials:
          - {fileID: 2100000, guid: @@MATGUID@@, type: 2}
          m_Mesh: {fileID: 4300000, guid: @@MESHGUID@@, type: 2}
          m_RootBone: {fileID: 20000000000000002}
          m_Bones:
          - {fileID: 20000000000000002}
          - {fileID: 20000000000000003}
        """.Replace("@@MATGUID@@", MatGuid).Replace("@@MESHGUID@@", MeshGuid);

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "XEngine.sln")))
                return current.FullName;
            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the XEngine repository root.");
    }
}
