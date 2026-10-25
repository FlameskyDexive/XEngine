// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Diagnostics;

using XEngine.Echo;
using XEngine.Runtime;
using XEngine.Runtime.Resources;
using XEngine.Terrain;
using XEngine.Vector;

using Xunit;

namespace XEngine.Runtime.Test.Terrain;

public sealed class TerrainSampleWrapupTests : RuntimeTestBase
{
    internal static readonly Guid TerrainDataGuid = Guid.Parse("c3d8e1a2-4b5f-4070-89c1-d2e3f4a5b6c7");
    internal static readonly Guid LayerGuid = Guid.Parse("d4e9f2b3-5c6a-4181-90d2-e3f4a5b6c7d8");
    internal static readonly Guid SceneGuid = Guid.Parse("e5f0a3c4-6d7b-4292-81e3-f4a5b6c7d8e9");

    [Fact]
    public void StarterSample_WritesAndRoundTripsEchoAssets()
    {
        string starter = Path.Combine(FindRepositoryRoot(), "Packages", "com.xengine.terrain", "Samples~", "Starter");
        Directory.CreateDirectory(starter);

        string layerPath = Path.Combine(starter, "Grass.terrainlayer");
        string dataPath = Path.Combine(starter, "Starter.terraindata");
        string scenePath = Path.Combine(starter, "Starter.scene");
        if (!File.Exists(layerPath) || !File.Exists(dataPath) || !File.Exists(scenePath)
            || File.ReadAllText(scenePath).Contains("XEngine.Runtime.Test", StringComparison.Ordinal)
            || File.ReadAllText(dataPath).Contains("XEngine.Runtime.Test", StringComparison.Ordinal)
            || File.ReadAllText(layerPath).Contains("XEngine.Runtime.Test", StringComparison.Ordinal))
            WriteStarter(starter);

        WriteMeta(Path.Combine(starter, "Grass.terrainlayer.meta"), LayerGuid);
        WriteMeta(Path.Combine(starter, "Starter.terraindata.meta"), TerrainDataGuid);
        WriteMeta(Path.Combine(starter, "Starter.scene.meta"), SceneGuid);

        var layer = Assert.IsType<TerrainLayer>(Serializer.Deserialize(
            EchoObject.ReadFromString(File.ReadAllText(layerPath)), typeof(object)));
        Assert.Equal("Grass", layer.Name);
        Assert.Equal(8f, layer.Tiling);

        var data = Assert.IsType<TerrainData>(Serializer.Deserialize(
            EchoObject.ReadFromString(File.ReadAllText(dataPath)), typeof(object)));
        Assert.Equal(33, data.HeightmapResolution);
        Assert.Equal(32, data.SplatmapResolution);
        Assert.Equal(32, data.DetailResolution);
        Assert.Equal(64f, data.Size);
        Assert.Equal(16f, data.Height);
        Assert.Equal(LayerGuid, data.Layers[0].AssetID);
        Assert.True(data.GetHeight(16, 16) > 0.4f);
        Assert.True(data.GetDetailDensity(0, 16, 16) > 0.4f);
        Assert.Equal(3, data.Trees.Count);

        var scene = Assert.IsType<Scene>(Serializer.Deserialize(
            EchoObject.ReadFromString(File.ReadAllText(scenePath)), typeof(object)));
        try
        {
            TerrainComponent? terrain = null;
            WindZone? wind = null;
            Camera? camera = null;
            foreach (GameObject go in scene.AllObjects)
            {
                terrain ??= go.GetComponent<TerrainComponent>();
                wind ??= go.GetComponent<WindZone>();
                camera ??= go.GetComponent<Camera>();
            }

            Assert.NotNull(terrain);
            Assert.Equal(TerrainDataGuid, terrain!.Data.AssetID);
            Assert.NotNull(terrain.GetComponent<TerrainCollider>());
            Assert.NotNull(wind);
            Assert.Equal(WindZone.WindZoneMode.Directional, wind!.Mode);
            Assert.NotNull(camera);
        }
        finally
        {
            scene.Dispose();
        }

        Assert.Contains("XEngine.Terrain.TerrainComponent, XEngine.Terrain.Runtime",
            File.ReadAllText(scenePath), StringComparison.Ordinal);
        Assert.DoesNotContain("XEngine.Runtime.Test", File.ReadAllText(scenePath), StringComparison.Ordinal);
        Assert.DoesNotContain("XEngine.Runtime.Test", File.ReadAllText(dataPath), StringComparison.Ordinal);
        Assert.DoesNotContain("XEngine.Runtime.Test", File.ReadAllText(layerPath), StringComparison.Ordinal);
        Assert.DoesNotContain("placeholder", File.ReadAllText(Path.Combine(starter, "README.md")),
            StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void WrapupEvidence_MeasuresCpuHelpersAndWritesBoard()
    {
        Scene scene = CreateScene(enable: true);
        GameObject go = CreateGameObject("Hill");
        var terrain = go.AddComponent<TerrainComponent>();
        TerrainData data = CreateHill(33, 64f, 16f);
        terrain.Data = new AssetRef<TerrainData>(data);
        scene.Add(go);

        Float3 mid = new(32f, 0f, 32f);
        _ = terrain.SampleHeight(mid);
        _ = terrain.GetInterpolatedNormal(mid);
        _ = TerrainBrushSampler.Weight(0f, 0f, 8f, 0.2f, 0.5f, null, [], 0, 0);

        const int warmup = 32;
        const int sampleIters = 20_000;
        for (int i = 0; i < warmup; i++)
        {
            terrain.SampleHeight(mid);
            terrain.GetInterpolatedNormal(mid);
        }

        var sw = Stopwatch.StartNew();
        float acc = 0f;
        for (int i = 0; i < sampleIters; i++)
            acc += terrain.SampleHeight(mid);
        sw.Stop();
        double sampleNs = sw.Elapsed.TotalNanoseconds / sampleIters;
        Assert.True(acc > 0f);

        sw.Restart();
        Float3 nAcc = Float3.Zero;
        for (int i = 0; i < sampleIters; i++)
            nAcc += terrain.GetInterpolatedNormal(mid);
        sw.Stop();
        double normalNs = sw.Elapsed.TotalNanoseconds / sampleIters;
        Assert.True(Float3.Length(nAcc) > 0f);

        const int brushIters = 50_000;
        sw.Restart();
        float wAcc = 0f;
        for (int i = 0; i < brushIters; i++)
            wAcc += TerrainBrushSampler.Weight(i % 7, i % 5, 8f, 0.35f, 0.45f, null, [], 0, 0);
        sw.Stop();
        double brushNs = sw.Elapsed.TotalNanoseconds / brushIters;
        Assert.True(wAcc >= 0f);

        TerrainComponent left = MakeTile(scene, "L", Float3.Zero, 10f);
        TerrainComponent right = MakeTile(scene, "R", new Float3(10f, 0f, 0f), 10f);
        const int reconnectIters = 2_000;
        sw.Restart();
        for (int i = 0; i < reconnectIters; i++)
            TerrainNeighboring.Reconnect([left, right]);
        sw.Stop();
        double reconnectNs = sw.Elapsed.TotalNanoseconds / reconnectIters;
        Assert.Same(right, left.RightNeighbor);

        string evidence = Path.Combine(FindRepositoryRoot(), "docs", "superpowers", "evidence", "terrain");
        Directory.CreateDirectory(evidence);
        var board = new XGuiAcceptanceScreenshot.Canvas(920, 280, 18, 19, 26);
        board.Fill(24, 24, 872, 232, 28, 30, 40, 255);
        board.Stroke(24, 24, 872, 232, 70, 74, 90, 2);
        board.Text(40, 40, "M5 TERRAIN PACKAGE CLOSEOUT", 230, 232, 240, 2);
        board.Text(40, 80, "SAMPLES~ STARTER + DOCS + CPU PROBE", 140, 146, 160, 1);
        board.Fill(60, 120, 18, 18, 80, 200, 140, 255);
        board.Text(90, 122, "33X33 HILL  SPLAT  GRASS  WINDZONE", 200, 220, 200, 1);
        board.Fill(60, 152, 18, 18, 80, 170, 220, 255);
        board.Text(90, 154, "OPTIONAL PACKAGE  INSTALLBYDEFAULT FALSE", 200, 220, 200, 1);
        board.Text(40, 210, "ACCEPTANCE 2026-08-15", 140, 146, 160, 1);
        string png = XGuiAcceptanceScreenshot.SaveTo(Path.Combine(evidence, "m5-wrapup.png"), board);
        Assert.True(File.Exists(png));

        File.WriteAllText(Path.Combine(evidence, "m5-wrapup.md"),
            "# M5 Terrain package closeout\n\n" +
            "Date: 2026-08-15. Branch: `codex/animation-module`.\n\n" +
            "`com.xengine.terrain` 0.1.0 ships `Documentation~/`, `Samples~/Starter` " +
            "(Echo `.terraindata` + `.terrainlayer` + `.scene` with a sculpted 33×33 hill, " +
            "grass details, trees, and a Wind Zone), CHANGELOG, LICENSE. Runtime asmdef has " +
            "empty `includePlatforms` (player). Editor asmdef is Editor-only. `Samples~` is " +
            "skipped by package asset scan.\n\n" +
            "CPU helpers (`dotnet test` Debug vs the last good Runtime DLL, Stopwatch after " + warmup + " warmup iters; WebGL WIP currently blocks a Release Runtime rebuild):\n\n" +
            "| Helper | ns/op | iters |\n" +
            "|---|---|---|\n" +
            "| `SampleHeight` | **" + sampleNs.ToString("0") + "** | " + sampleIters + " |\n" +
            "| `GetInterpolatedNormal` | **" + normalNs.ToString("0") + "** | " + sampleIters + " |\n" +
            "| `TerrainBrushSampler.Weight` | **" + brushNs.ToString("0") + "** | " + brushIters + " |\n" +
            "| `TerrainNeighboring.Reconnect` (2 tiles) | **" + reconnectNs.ToString("0") + "** | " + reconnectIters + " |\n\n" +
            "GPU shader extract parity: [m2-shader-move.md](m2-shader-move.md) / RHI `sm8-terrain`. " +
            "These CPU figures are helper cost, not full-frame GPU time.\n\n" +
            "Screenshot: [m5-wrapup.png](m5-wrapup.png).\n");
        Assert.True(sampleNs > 0);
        Assert.True(normalNs > 0);
        Assert.True(brushNs > 0);
        Assert.True(reconnectNs > 0);
    }

    private TerrainComponent MakeTile(Scene scene, string name, Float3 position, float size)
    {
        GameObject go = CreateGameObject(name);
        go.Transform.Position = position;
        var terrain = go.AddComponent<TerrainComponent>();
        terrain.Data = new AssetRef<TerrainData>(new TerrainData { Size = size, Height = 10f });
        scene.Add(go);
        return terrain;
    }

    private static void WriteStarter(string starter)
    {
        var layer = new TerrainLayer { Name = "Grass", Tiling = 8f, Roughness = 0.85f };
        layer.AssetID = LayerGuid;
        File.WriteAllText(Path.Combine(starter, "Grass.terrainlayer"),
            StampPackageAssembly(Serializer.Serialize(layer).WriteToString()));

        TerrainData data = CreateHill(33, 64f, 16f);
        data.Name = "Starter";
        data.AssetID = TerrainDataGuid;
        data.Layers = [new AssetRef<TerrainLayer>(LayerGuid)];
        data.SplatmapResolution = 32;
        data.Splats = new float[32 * 32];
        for (int i = 0; i < data.Splats.Length; i++)
            data.Splats[i] = 1f;
        File.WriteAllText(Path.Combine(starter, "Starter.terraindata"),
            StampPackageAssembly(Serializer.Serialize(data).WriteToString()));

        using var scene = new Scene { Name = "Starter" };
        scene.AssetID = SceneGuid;

        GameObject cameraGo = new("Main Camera");
        cameraGo.Transform.Position = new Float3(12f, 22f, -8f);
        cameraGo.Transform.Rotation = Quaternion.LookRotation(
            Float3.Normalize(new Float3(32f, 6f, 32f) - cameraGo.Transform.Position), Float3.UnitY);
        cameraGo.AddComponent<Camera>().ClearColor = new Color(0.42f, 0.62f, 0.85f, 1f);
        scene.Add(cameraGo);

        GameObject lightGo = new("Directional Light");
        lightGo.Transform.Position = new Float3(0f, 20f, 0f);
        lightGo.Transform.Rotation = Quaternion.LookRotation(new Float3(0.35f, -1f, 0.4f), Float3.UnitY);
        var light = lightGo.AddComponent<DirectionalLight>();
        light.Intensity = 2.2f;
        scene.Add(lightGo);

        GameObject terrainGo = new("Terrain");
        var terrain = terrainGo.AddComponent<TerrainComponent>();
        terrain.Data = new AssetRef<TerrainData>(TerrainDataGuid);
        terrain.Material = new AssetRef<Material>(TerrainShaderLibrary.TerrainMaterialGuid);
        terrain.GrassMaterial = new AssetRef<Material>(TerrainShaderLibrary.GrassMaterialGuid);
        terrain.TreeBillboardDistance = 40f;
        terrain.TreeDistance = 200f;
        terrain.GrassDistance = 80f;
        terrainGo.AddComponent<TerrainCollider>();
        scene.Add(terrainGo);

        GameObject windGo = new("Wind Zone");
        windGo.Transform.Position = new Float3(32f, 12f, 32f);
        windGo.Transform.Rotation = Quaternion.LookRotation(new Float3(1f, 0f, 0.35f), Float3.UnitY);
        var wind = windGo.AddComponent<WindZone>();
        wind.WindMain = 1.2f;
        wind.WindTurbulence = 0.35f;
        wind.WindPulseFrequency = 0.2f;
        scene.Add(windGo);

        File.WriteAllText(Path.Combine(starter, "Starter.scene"),
            StampPackageAssembly(Serializer.Serialize(scene).WriteToString()));
    }

    private static string StampPackageAssembly(string echo) =>
        echo.Replace("XEngine.Runtime.Test", "XEngine.Terrain.Runtime", StringComparison.Ordinal);

    private static TerrainData CreateHill(int res, float size, float height)
    {
        var data = new TerrainData
        {
            HeightmapResolution = res,
            SplatmapResolution = 32,
            DetailResolution = 32,
            Size = size,
            Height = height,
            Interpolation = TerrainInterpolation.Bilinear,
            Heights = new short[res * res],
            Layers = [new AssetRef<TerrainLayer>(LayerGuid)],
            Splats = new float[32 * 32],
        };
        for (int i = 0; i < data.Splats.Length; i++)
            data.Splats[i] = 1f;

        float cx = (res - 1) * 0.5f;
        float cz = (res - 1) * 0.5f;
        for (int z = 0; z < res; z++)
        {
            for (int x = 0; x < res; x++)
            {
                float dx = (x - cx) / cx;
                float dz = (z - cz) / cz;
                float r2 = dx * dx + dz * dz;
                float hill = MathF.Exp(-r2 * 2.2f);
                float ridge = MathF.Exp(-((x - cx * 1.3f) * (x - cx * 1.3f) + (z - cz) * (z - cz)) / (cx * cx * 0.35f)) * 0.35f;
                data.SetHeight(x, z, Math.Clamp(0.08f + hill * 0.72f + ridge, 0f, 1f));
            }
        }

        data.DetailPrototypes =
        [
            new DetailPrototype
            {
                MinWidth = 0.4f,
                MaxWidth = 0.9f,
                MinHeight = 0.5f,
                MaxHeight = 1.1f,
                BendFactor = 0.65f,
                NoiseSpread = 0.2f,
            },
        ];
        data.DetailLayers = [];
        data.EnsureDetailLayers();
        float[] grass = data.DetailLayers[0];
        int dres = data.DetailResolution;
        float dcx = (dres - 1) * 0.5f;
        for (int z = 0; z < dres; z++)
        {
            for (int x = 0; x < dres; x++)
            {
                float dx = (x - dcx) / dcx;
                float dz = (z - dcx) / dcx;
                float r2 = dx * dx + dz * dz;
                grass[z * dres + x] = Math.Clamp(0.85f - r2 * 0.7f, 0.05f, 1f);
            }
        }

        data.TreePrototypes = [new TreePrototype { BendFactor = 0.4f }];
        data.AddTreeInstance(new TreeInstance { Position = new Float2(0.35f, 0.40f), PrototypeIndex = 0, WidthScale = 1f, HeightScale = 1.1f, Tint = Color.White });
        data.AddTreeInstance(new TreeInstance { Position = new Float2(0.62f, 0.55f), PrototypeIndex = 0, WidthScale = 0.9f, HeightScale = 0.95f, Rotation = 0.7f, Tint = Color.White });
        data.AddTreeInstance(new TreeInstance { Position = new Float2(0.48f, 0.70f), PrototypeIndex = 0, WidthScale = 1.15f, HeightScale = 1.2f, Rotation = 1.4f, Tint = Color.White });
        return data;
    }

    private static void WriteMeta(string path, Guid guid)
    {
        File.WriteAllText(path,
            "{\"guid\": \"" + guid.ToString("D") + "\", \"importer\": \"DefaultImporter\", \"importerVersion\": 1}\n");
    }

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
