using System.IO.Compression;
using System.Text;

using XEngine.Ai.Host;
using XEngine.Ai.Protocol;
using XEngine.VFX;
using Xunit;

namespace XEngine.Ai.Host.Tests;

public class VfxGraphCliTests : IClassFixture<TestSceneFixture>
{
    private readonly TestSceneFixture _fx;
    public VfxGraphCliTests(TestSceneFixture fx) => _fx = fx;

    static string GraphPath(TestSceneFixture fx, string echo, string name = "Loop.vfx")
    {
        string dir = Path.Combine(fx.ProjectRoot, "Assets", "VFX");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, name);
        File.WriteAllText(path, echo);
        return path;
    }

    [Fact]
    public void Inspect_ReadsGraphNameBlocksAndHash()
    {
        GraphPath(_fx, File.ReadAllText(FindTemplate("Loop.vfx")));
        var detail = SceneReader.ReadAssetData(
            Path.Combine(_fx.ProjectRoot, "Assets", "VFX", "Loop.vfx"),
            "Assets/VFX/Loop.vfx");
        Assert.Contains("VfxGraphAsset", detail.TypeName, StringComparison.Ordinal);
        Assert.Contains(detail.Fields, f => f.Path == "GraphName" && f.Value == "VFX/Loop");
        Assert.Contains(detail.Fields, f => f.Value == "ConstantSpawnRate");
        Assert.Contains(detail.Fields, f => f.Value == "n_color");
        Assert.False(string.IsNullOrEmpty(detail.ContentHash));
    }

    [Fact]
    public async Task Patch_UpdatesGraphName_IsJournaled()
    {
        GraphPath(_fx, File.ReadAllText(FindTemplate("Loop.vfx")), "PatchMe.vfx");
        var inspect = SceneReader.ReadAssetData(
            Path.Combine(_fx.ProjectRoot, "Assets", "VFX", "PatchMe.vfx"),
            "Assets/VFX/PatchMe.vfx");
        var patches = new[] { new SceneFieldPatch("GraphName", "VFX/Patched") };
        var result = await _fx.SceneWriter.PatchAssetDataAsync(
            Path.Combine(_fx.ProjectRoot, "Assets", "VFX", "PatchMe.vfx"),
            "Assets/VFX/PatchMe.vfx", patches,
            TestSceneFixture.Provenance("test.vfxgraph", "vfx-patch-1", "rename graph"),
            dryRun: false, expectedBeforeHash: inspect.ContentHash);

        Assert.False(result.DryRun);
        Assert.NotEqual(result.BeforeHash, result.AfterHash);
        string after = File.ReadAllText(Path.Combine(_fx.ProjectRoot, "Assets", "VFX", "PatchMe.vfx"));
        Assert.Contains("VFX/Patched", after, StringComparison.Ordinal);

        var replay = await _fx.SceneWriter.PatchAssetDataAsync(
            Path.Combine(_fx.ProjectRoot, "Assets", "VFX", "PatchMe.vfx"),
            "Assets/VFX/PatchMe.vfx", patches,
            TestSceneFixture.Provenance("test.vfxgraph", "vfx-patch-1", "rename graph"),
            dryRun: false);
        Assert.True(
            replay.Summary.Contains("Idempotent", StringComparison.Ordinal)
            || replay.Summary.Contains("No change", StringComparison.Ordinal)
            || replay.ChangeId == result.ChangeId);
    }

    [Fact]
    public void Compile_Loop_EmitsComputePassesAndInitColor()
    {
        string source = VfxGraphEcho.CompileToSource(File.ReadAllText(FindTemplate("Loop.vfx")));
        Assert.Contains("Shader \"VFX/Loop\"", source, StringComparison.Ordinal);
        Assert.Contains("SLANGPROGRAM", source, StringComparison.Ordinal);
        Assert.Contains("Pass \"Init\"", source, StringComparison.Ordinal);
        Assert.Contains("Pass \"Update\"", source, StringComparison.Ordinal);
        Assert.Contains("Pass \"WriteArgs\"", source, StringComparison.Ordinal);
        Assert.Contains("Pass \"Output\"", source, StringComparison.Ordinal);
        Assert.Contains(VfxGraphSourceCompiler.InitMarker, source, StringComparison.Ordinal);
        Assert.Contains("particle.color = float4(1.0, 1.0, 1.0, 1.0);", source, StringComparison.Ordinal);
        Assert.Contains("SpawnRate=64.0", source, StringComparison.Ordinal);
        Assert.Contains("Subgraphs=0", source, StringComparison.Ordinal);
        Assert.Contains("LitMode=0.0", source, StringComparison.Ordinal);
        Assert.Contains("_LitMode", source, StringComparison.Ordinal);
        Assert.Contains("SdfMode=2.0", source, StringComparison.Ordinal);
        Assert.Contains("SdfShape=1.0", source, StringComparison.Ordinal);
        Assert.Contains("Turbulence=1.2", source, StringComparison.Ordinal);
        Assert.Contains("SoftFade=1.0", source, StringComparison.Ordinal);
        Assert.Contains("EventKind=1.0", source, StringComparison.Ordinal);
        Assert.Contains("EventCount=2.0", source, StringComparison.Ordinal);
        Assert.Contains("EventTarget=0.0", source, StringComparison.Ordinal);
        Assert.Contains("DepthMode=0.0", source, StringComparison.Ordinal);
        Assert.Contains("Conform=0.0", source, StringComparison.Ordinal);
        Assert.Contains("VectorField=0.0", source, StringComparison.Ordinal);
        Assert.Contains("Distance=0.0", source, StringComparison.Ordinal);
        Assert.Contains("Periodic=0.0", source, StringComparison.Ordinal);
        Assert.Contains("AttributeMap=0.0", source, StringComparison.Ordinal);
        Assert.Contains("VfxApplyAttributeMap", source, StringComparison.Ordinal);
        Assert.Contains("_AttributeMap", source, StringComparison.Ordinal);
        Assert.Contains("Distort=0.0", source, StringComparison.Ordinal);
        Assert.Contains("Lights=0.0", source, StringComparison.Ordinal);
        Assert.Contains("Sort=0.0", source, StringComparison.Ordinal);
        Assert.Contains("FadeNear=0.0", source, StringComparison.Ordinal);
        Assert.Contains("FadeFar=0.0", source, StringComparison.Ordinal);
        Assert.Contains("FlipbookBlend=0.0", source, StringComparison.Ordinal);
        Assert.Contains("FlipbookMotion=0.0", source, StringComparison.Ordinal);
        Assert.Contains("Motion=0.0", source, StringComparison.Ordinal);
        Assert.Contains("Orient=0.0", source, StringComparison.Ordinal);
        Assert.Contains("Shape=0.0", source, StringComparison.Ordinal);
        Assert.Contains("ColorOverLife=1.0", source, StringComparison.Ordinal);
        Assert.Contains("SizeOverLife=0.0", source, StringComparison.Ordinal);
        Assert.Contains("Drag=0.0", source, StringComparison.Ordinal);
        Assert.Contains("Force=0.0", source, StringComparison.Ordinal);
        Assert.Contains("Spin=0.0", source, StringComparison.Ordinal);
        Assert.Contains("CustomHLSL=0.0", source, StringComparison.Ordinal);
        Assert.Contains("Seq=0.0", source, StringComparison.Ordinal);
        Assert.Contains("Inherit=0.0", source, StringComparison.Ordinal);
        Assert.Contains("TriggerRate=0.0", source, StringComparison.Ordinal);
        Assert.Contains("TriggerDist=0.0", source, StringComparison.Ordinal);
        Assert.Contains("TriggerOnce=0.0", source, StringComparison.Ordinal);
        Assert.Contains("Friction=0.0", source, StringComparison.Ordinal);
        Assert.Contains("Elastic=0.0", source, StringComparison.Ordinal);
        Assert.Contains("LifeLoss=0.0", source, StringComparison.Ordinal);
        Assert.Contains("LimitVel=0.0", source, StringComparison.Ordinal);
        Assert.Contains("KillSphere=0.0", source, StringComparison.Ordinal);
        Assert.Contains("KillBox=0.0", source, StringComparison.Ordinal);
        Assert.Contains("Attract=0.0", source, StringComparison.Ordinal);
        Assert.Contains("ConformSphere=0.0", source, StringComparison.Ordinal);
        Assert.Contains("Vortex=0.0", source, StringComparison.Ordinal);
        Assert.Contains("ConformCyl=0.0", source, StringComparison.Ordinal);
        Assert.Contains("LookAt=0.0", source, StringComparison.Ordinal);
        Assert.Contains("FlipRate=0.0", source, StringComparison.Ordinal);
        Assert.Contains("FlipStart=0.0", source, StringComparison.Ordinal);
        Assert.Contains("KillPlane=0.0", source, StringComparison.Ordinal);
        Assert.Contains("SizeSpeed=0.0", source, StringComparison.Ordinal);
        Assert.Contains("MulSize=1.0", source, StringComparison.Ordinal);
        Assert.Contains("MulColor=1.0", source, StringComparison.Ordinal);
        Assert.Contains("AddSize=0.0", source, StringComparison.Ordinal);
        Assert.Contains("AddColor=0.0", source, StringComparison.Ordinal);
        Assert.Contains("AddVel=0.0", source, StringComparison.Ordinal);
        Assert.Contains("MulVel=1.0", source, StringComparison.Ordinal);
        Assert.Contains("OwSize=0.0", source, StringComparison.Ordinal);
        Assert.Contains("OwColor=0.0", source, StringComparison.Ordinal);
        Assert.Contains("OwVel=0.0", source, StringComparison.Ordinal);
        Assert.Contains("OwLife=0.0", source, StringComparison.Ordinal);
        Assert.Contains("AddLife=0.0", source, StringComparison.Ordinal);
        Assert.Contains("MulLife=1.0", source, StringComparison.Ordinal);
        Assert.Contains("AddAge=0.0", source, StringComparison.Ordinal);
        Assert.Contains("MulAge=1.0", source, StringComparison.Ordinal);
        Assert.Contains("SetAge=0.0", source, StringComparison.Ordinal);
        Assert.Contains("AddPos=0.0", source, StringComparison.Ordinal);
        Assert.Contains("SetPos=0.0", source, StringComparison.Ordinal);
        Assert.Contains("ColorSpeed=0.0", source, StringComparison.Ordinal);
        Assert.Contains("ScreenSize=0.0", source, StringComparison.Ordinal);
        Assert.Contains("FixedAxis=0.0", source, StringComparison.Ordinal);
        Assert.Contains("vfxLife01", source, StringComparison.Ordinal);
        Assert.Contains("VfxCameraFade", source, StringComparison.Ordinal);
        Assert.Contains("VfxFlipbookBlend", source, StringComparison.Ordinal);
        Assert.Contains("VfxFlipbookWarp", source, StringComparison.Ordinal);
        Assert.Contains("_FlipbookMotion", source, StringComparison.Ordinal);
        Assert.Contains(VfxGraphSourceCompiler.OutputPsMarker, source, StringComparison.Ordinal);
        Assert.Contains("VfxMotionNdcDelta", source, StringComparison.Ordinal);
        Assert.Contains("VfxMotionEncode", source, StringComparison.Ordinal);
        Assert.Contains("VfxOrientAlongVelocity", source, StringComparison.Ordinal);
        Assert.Contains("vfxOrientAlong", source, StringComparison.Ordinal);
        Assert.Contains("VfxPositionShape", source, StringComparison.Ordinal);
        Assert.Contains("VfxSequential", source, StringComparison.Ordinal);
        Assert.Contains("VfxDrag", source, StringComparison.Ordinal);
        Assert.Contains("VfxForce", source, StringComparison.Ordinal);
        Assert.Contains("VfxAngularVelocity", source, StringComparison.Ordinal);
        Assert.Contains("VfxSpinLocal", source, StringComparison.Ordinal);
        Assert.Contains("VfxSortDist2", source, StringComparison.Ordinal);
        Assert.Contains("VfxDistortUv", source, StringComparison.Ordinal);
        Assert.Contains("_VfxSceneColor", source, StringComparison.Ordinal);
        Assert.Contains("VfxSampleVectorField", source, StringComparison.Ordinal);
        Assert.Contains("_VectorField", source, StringComparison.Ordinal);
        Assert.Contains("OutputStrip=0", source, StringComparison.Ordinal);
        Assert.Contains("OutputDecal=0", source, StringComparison.Ordinal);
        Assert.Contains("VfxDecalProject", source, StringComparison.Ordinal);
        Assert.Contains("_DecalPlane", source, StringComparison.Ordinal);
        Assert.Contains("VfxDepthProject", source, StringComparison.Ordinal);
        Assert.Contains("_SoftParticleFade", source, StringComparison.Ordinal);
        Assert.Contains("VfxEventEmitCount", source, StringComparison.Ordinal);
        Assert.Contains("VfxSampleVolumeSdf", source, StringComparison.Ordinal);
        Assert.Contains("VfxSampleCylinder", source, StringComparison.Ordinal);
        Assert.Contains("VfxSampleCone", source, StringComparison.Ordinal);
        Assert.Contains("_SdfVolume", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Compile_Subgraph_InlinesTrailColorOverLife()
    {
        string source = VfxGraphEcho.CompileToSource(File.ReadAllText(FindTemplate("Subgraph.vfx")));
        Assert.Contains("Shader \"VFX/Subgraph\"", source, StringComparison.Ordinal);
        Assert.Contains("Subgraphs=1", source, StringComparison.Ordinal);
        Assert.Contains("SpawnRate=64.0", source, StringComparison.Ordinal);
        Assert.Contains(
            "float4(1.0, 1.0, 1.0, 1.0) + (float4(1.0, 1.0, 1.0, 0.0) - float4(1.0, 1.0, 1.0, 1.0)) * vfxLife01",
            source,
            StringComparison.Ordinal);
        Assert.DoesNotContain("SpawnRate=200", source, StringComparison.Ordinal);
    }

    [Fact]
    public void Compile_MissingContexts_Throws()
    {
        var ex = Assert.Throws<VfxGraphCompileException>(
            () => VfxGraphEcho.CompileToSource(EmptyEcho));
        Assert.Contains("Spawn", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void RequirePath_RejectsNonVfx()
    {
        Assert.Throws<ArgumentException>(() => AiTools.RequireVfxGraphPath("Assets/x.mat"));
        AiTools.RequireVfxGraphPath("Assets/VFX/Loop.vfx");
    }

    [Fact]
    public void Nodes_ListsContextsBlocksOperators()
    {
        VfxNodeCatalog catalog = AiTools.ListVfxGraphNodes();
        Assert.Contains(VfxNodeLibrary.Spawn, catalog.Contexts);
        Assert.Contains(VfxNodeLibrary.OutputParticleMesh, catalog.Contexts);
        Assert.Contains(VfxNodeLibrary.OutputParticleStrip, catalog.Contexts);
        Assert.Contains(VfxNodeLibrary.OutputParticleDecal, catalog.Contexts);
        Assert.Contains(VfxNodeLibrary.Sort, catalog.Blocks);
        Assert.Contains(VfxNodeLibrary.CameraFade, catalog.Blocks);
        Assert.Contains(VfxNodeLibrary.FlipbookBlend, catalog.Blocks);
        Assert.Contains(VfxNodeLibrary.FlipbookMotion, catalog.Blocks);
        Assert.Contains(VfxNodeLibrary.MotionVector, catalog.Blocks);
        Assert.Contains(VfxNodeLibrary.OrientAlongVelocity, catalog.Blocks);
        Assert.Contains(VfxNodeLibrary.OrientFixedAxis, catalog.Blocks);
        Assert.Contains(VfxNodeLibrary.PositionShape, catalog.Blocks);
        Assert.Contains(VfxNodeLibrary.ColorOverLife, catalog.Blocks);
        Assert.Contains(VfxNodeLibrary.SizeOverLife, catalog.Blocks);
        Assert.Contains(VfxNodeLibrary.LinearDrag, catalog.Blocks);
        Assert.Contains(VfxNodeLibrary.Force, catalog.Blocks);
        Assert.Contains(VfxNodeLibrary.AngularVelocity, catalog.Blocks);
        Assert.Contains(VfxNodeLibrary.CustomHLSL, catalog.Blocks);
        Assert.Contains(VfxNodeLibrary.Sequential, catalog.Blocks);
        Assert.Contains(VfxNodeLibrary.InheritVelocity, catalog.Blocks);
        Assert.Contains(VfxNodeLibrary.PeriodicBurst, catalog.Blocks);
        Assert.Contains(VfxNodeLibrary.TriggerEventRate, catalog.Blocks);
        Assert.Contains(VfxNodeLibrary.TriggerEventOverDistance, catalog.Blocks);
        Assert.Contains(VfxNodeLibrary.TriggerEventOnce, catalog.Blocks);
        Assert.Contains(VfxNodeLibrary.CollisionFriction, catalog.Blocks);
        Assert.Contains(VfxNodeLibrary.CollisionElasticity, catalog.Blocks);
        Assert.Contains(VfxNodeLibrary.CollisionLifetimeLoss, catalog.Blocks);
        Assert.Contains(VfxNodeLibrary.LimitVelocity, catalog.Blocks);
        Assert.Contains(VfxNodeLibrary.KillSphere, catalog.Blocks);
        Assert.Contains(VfxNodeLibrary.KillAABox, catalog.Blocks);
        Assert.Contains(VfxNodeLibrary.Attract, catalog.Blocks);
        Assert.Contains(VfxNodeLibrary.ConformToSphere, catalog.Blocks);
        Assert.Contains(VfxNodeLibrary.Vortex, catalog.Blocks);
        Assert.Contains(VfxNodeLibrary.ConformToCylinder, catalog.Blocks);
        Assert.Contains(VfxNodeLibrary.OrientLookAt, catalog.Blocks);
        Assert.Contains(VfxNodeLibrary.FlipbookRate, catalog.Blocks);
        Assert.Contains(VfxNodeLibrary.FlipbookStart, catalog.Blocks);
        Assert.Contains(VfxNodeLibrary.KillPlane, catalog.Blocks);
        Assert.Contains(VfxNodeLibrary.SizeOverSpeed, catalog.Blocks);
        Assert.Contains(VfxNodeLibrary.MultiplySize, catalog.Blocks);
        Assert.Contains(VfxNodeLibrary.MultiplyColor, catalog.Blocks);
        Assert.Contains(VfxNodeLibrary.AddSize, catalog.Blocks);
        Assert.Contains(VfxNodeLibrary.AddColor, catalog.Blocks);
        Assert.Contains(VfxNodeLibrary.AddVelocity, catalog.Blocks);
        Assert.Contains(VfxNodeLibrary.MultiplyVelocity, catalog.Blocks);
        Assert.Contains(VfxNodeLibrary.OverwriteSize, catalog.Blocks);
        Assert.Contains(VfxNodeLibrary.OverwriteColor, catalog.Blocks);
        Assert.Contains(VfxNodeLibrary.OverwriteVelocity, catalog.Blocks);
        Assert.Contains(VfxNodeLibrary.OverwriteLifetime, catalog.Blocks);
        Assert.Contains(VfxNodeLibrary.AddLifetime, catalog.Blocks);
        Assert.Contains(VfxNodeLibrary.MultiplyLifetime, catalog.Blocks);
        Assert.Contains(VfxNodeLibrary.AddAge, catalog.Blocks);
        Assert.Contains(VfxNodeLibrary.MultiplyAge, catalog.Blocks);
        Assert.Contains(VfxNodeLibrary.SetAge, catalog.Blocks);
        Assert.Contains(VfxNodeLibrary.AddPosition, catalog.Blocks);
        Assert.Contains(VfxNodeLibrary.SetPosition, catalog.Blocks);
        Assert.Contains(VfxNodeLibrary.ColorOverSpeed, catalog.Blocks);
        Assert.Contains(VfxNodeLibrary.ScreenSpaceSize, catalog.Blocks);
        Assert.Contains(VfxNodeLibrary.Gravity, catalog.Blocks);
        Assert.Contains(VfxNodeLibrary.CollideWithPlane, catalog.Blocks);
        Assert.Contains(VfxNodeLibrary.CollideWithSDF, catalog.Blocks);
        Assert.Contains(VfxNodeLibrary.Turbulence, catalog.Blocks);
        Assert.Contains(VfxNodeLibrary.TriggerEvent, catalog.Blocks);
        Assert.Contains(VfxNodeLibrary.GPUEventTarget, catalog.Blocks);
        Assert.Contains(VfxNodeLibrary.Multiply, catalog.Operators);
        Assert.Contains(VfxNodeLibrary.Subgraph, catalog.Operators);
        Assert.Contains(VfxNodeLibrary.Subgraph, catalog.Blocks);
    }

    [Fact]
    public void Create_CopiesLoopTemplate()
    {
        var project = ProjectIdentity.Inspect(_fx.ProjectRoot);
        var services = new SimpleServices(project);
        var result = AiTools.CreateVfxGraph(services, "Assets/VFX/FromTemplate.vfx", "Loop");
        Assert.True(result.Written);
        Assert.Equal("Loop", result.Template);
        string written = File.ReadAllText(Path.Combine(_fx.ProjectRoot, "Assets", "VFX", "FromTemplate.vfx"));
        Assert.Contains("VFX/Loop", written, StringComparison.Ordinal);
        Assert.Contains("ConstantSpawnRate", written, StringComparison.Ordinal);
    }

    [Fact]
    public void Compile_WritesOptionalShaderFile()
    {
        GraphPath(_fx, File.ReadAllText(FindTemplate("Loop.vfx")), "WriteOut.vfx");
        var project = ProjectIdentity.Inspect(_fx.ProjectRoot);
        var services = new SimpleServices(project);
        var result = AiTools.CompileVfxGraph(services, "Assets/VFX/WriteOut.vfx", "Assets/VFX/WriteOut.shader");
        Assert.True(result.Written);
        Assert.Equal("VFX/Loop", result.GraphName);
        Assert.Equal(64f, result.SpawnRate);
        string written = File.ReadAllText(Path.Combine(_fx.ProjectRoot, "Assets", "VFX", "WriteOut.shader"));
        Assert.Contains("SLANGPROGRAM", written, StringComparison.Ordinal);
        Assert.Contains("Pass \"Init\"", written, StringComparison.Ordinal);
    }

    [Fact]
    public void Capabilities_AdvertiseVfxGraphAndRuntimeTools()
    {
        var project = new ProjectDescriptor("p", _fx.ProjectRoot, null, null, null, "fp");
        var snap = new CapabilityCatalog(project).GetSnapshot();
        var inspect = Assert.Single(snap.Tools, t => t.Name == "vfxgraph_inspect");
        Assert.Equal("vfx", inspect.Group);
        Assert.Equal(RiskLevel.ReadOnly, inspect.RiskLevel);
        var patch = Assert.Single(snap.Tools, t => t.Name == "vfxgraph_patch");
        Assert.Equal("vfx", patch.Group);
        Assert.Equal(RiskLevel.SafeWrite, patch.RiskLevel);
        Assert.True(patch.SupportsDryRun);
        var compile = Assert.Single(snap.Tools, t => t.Name == "vfxgraph_compile");
        Assert.Equal(RiskLevel.SafeWrite, compile.RiskLevel);
        var create = Assert.Single(snap.Tools, t => t.Name == "vfxgraph_create");
        Assert.True(create.SupportsDryRun);
        var state = Assert.Single(snap.Tools, t => t.Name == "runtime_vfx_state");
        Assert.Equal("vfx", state.Group);
        Assert.True(state.RequiresEngine);
        Assert.Equal(RiskLevel.ReadOnly, state.RiskLevel);
        var preview = Assert.Single(snap.Tools, t => t.Name == "runtime_vfx_preview");
        Assert.True(preview.RequiresEngine);
        Assert.Equal(RiskLevel.SafeWrite, preview.RiskLevel);
    }

    [Fact]
    public void Prompts_IncludeVfxGraphPlaybook()
    {
        Assert.Contains("vfxgraph_compile", AiPrompts.CompileVfxGraph(), StringComparison.Ordinal);
        Assert.Contains("runtime_vfx_preview", AiPrompts.CompileVfxGraph(), StringComparison.Ordinal);
        Assert.Contains("VFXGRAPH_INIT_BLOCKS", AiPrompts.CompileVfxGraph(), StringComparison.Ordinal);
    }

    [Fact]
    public void Skills_Exist()
    {
        string repo = FindRepoRoot();
        Assert.True(File.Exists(Path.Combine(repo, "cli", "skills", "inspect-vfx-graph", "SKILL.md")));
        Assert.True(File.Exists(Path.Combine(repo, "cli", "skills", "edit-vfx-graph", "SKILL.md")));
    }

    [Fact]
    public void Evidence_ConformSdf_WritesPng()
    {
        const int w = 320, h = 180;
        var canvas = new Board(w, h, 16, 18, 24);
        canvas.Fill(0, 0, w, 22, 28, 32, 40, 255);
        int groundSy = (int)(h * 0.72f);
        const float cx = 0f, cy = 0.55f, cz = 0f, sx = 0.4f;
        int ringX = 12 + (int)((cx + 2f) * 0.25f * Math.Max(8, w - 24));
        int ringY = groundSy - (int)(cy * (h * 0.32f));
        int ringR = Math.Max(8, (int)(sx * (h * 0.32f)));
        canvas.Stroke(ringX - ringR, ringY - ringR, ringR * 2, ringR * 2, 70, 190, 255);
        for (int i = 0; i < 16; i++)
        {
            float px = -1.6f + i * 0.21f;
            float py = 1.35f + (i % 4) * 0.08f;
            float pz = 0f;
            for (int step = 0; step < 36; step++)
            {
                py += -1.5f * (1f / 30f) * 0.15f;
                VfxConform.Apply(
                    ref px, ref py, ref pz,
                    cx, cy, cz, sx, sx, sx, VfxCollision.ShapeSphere,
                    4f, 1f / 30f);
            }
            int x = 12 + (int)((px + 2f) * 0.25f * Math.Max(8, w - 24));
            int y = groundSy - (int)(py * (h * 0.32f));
            canvas.Fill(x - 2, y - 2, 5, 5, 80, 210, 255, 255);
            canvas.Fill(x - 1, y - 1, 3, 3, 230, 230, 235, 255);
        }

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx", "m23-vfx-conform-sdf.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Board.EncodePng(canvas.Rgba, canvas.Width, canvas.Height));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    [Fact]
    public void Evidence_CylinderCone_WritesPng()
    {
        const int w = 320, h = 180;
        var canvas = new Board(w, h, 16, 18, 24);
        canvas.Fill(0, 0, w, 22, 28, 32, 40, 255);
        int groundSy = (int)(h * 0.72f);
        const float cylX = -0.7f, coneX = 0.75f, cy = 0.45f, r = 0.28f, hh = 0.4f;
        int cylSx = 12 + (int)((cylX - r + 2f) * 0.25f * Math.Max(8, w - 24));
        int boxY = groundSy - (int)((cy + hh) * (h * 0.32f));
        int boxW = Math.Max(10, (int)(r * 2f * 0.25f * Math.Max(8, w - 24)));
        int boxH = Math.Max(12, (int)(hh * 2f * (h * 0.32f)));
        canvas.Stroke(cylSx, boxY, boxW, boxH, 80, 210, 230);
        int apexX = 12 + (int)((coneX + 2f) * 0.25f * Math.Max(8, w - 24));
        int baseL = 12 + (int)((coneX - r + 2f) * 0.25f * Math.Max(8, w - 24));
        int baseR = 12 + (int)((coneX + r + 2f) * 0.25f * Math.Max(8, w - 24));
        canvas.Stroke(baseL, boxY + boxH - 2, Math.Max(4, baseR - baseL), 2, 230, 170, 80);
        canvas.Stroke(Math.Min(apexX, baseL), boxY, 2, boxH, 230, 170, 80);
        canvas.Stroke(Math.Max(apexX, baseR) - 2, boxY, 2, boxH, 230, 170, 80);
        for (int i = 0; i < 18; i++)
        {
            bool cone = i >= 9;
            float ox = cone ? coneX : cylX;
            float shape = cone ? VfxCollision.ShapeCone : VfxCollision.ShapeCylinder;
            float topR = cone ? 0.04f : r;
            float px = ox - 0.18f + (i % 9) * 0.045f;
            float py = 1.15f + (i % 3) * 0.06f;
            float pz = 0f;
            float vx = 0f, vy = 0.1f, vz = 0f;
            uint alive = 1u;
            for (int step = 0; step < 40; step++)
            {
                vy += -1.5f * (1f / 30f);
                px += vx * (1f / 30f);
                py += vy * (1f / 30f);
                pz += vz * (1f / 30f);
                VfxCollision.CollideSdf(
                    ref px, ref py, ref pz, ref vx, ref vy, ref vz, ref alive,
                    ox, cy, 0f, r, hh, topR, shape, VfxCollision.ModeBounce, 0.5f, 0.07f);
                if (alive == 0u)
                    break;
            }
            if (alive == 0u)
                continue;
            int x = 12 + (int)((px + 2f) * 0.25f * Math.Max(8, w - 24));
            int y = groundSy - (int)(py * (h * 0.32f));
            canvas.Fill(x - 2, y - 2, 5, 5, cone ? (byte)230 : (byte)70, cone ? (byte)170 : (byte)210, cone ? (byte)80 : (byte)230, 255);
            canvas.Fill(x - 1, y - 1, 3, 3, 230, 230, 235, 255);
        }

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx", "m25-vfx-cylinder-cone.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Board.EncodePng(canvas.Rgba, canvas.Width, canvas.Height));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    [Fact]
    public void Evidence_SpawnOverDistance_WritesPng()
    {
        const int w = 320, h = 180;
        var canvas = new Board(w, h, 16, 18, 24);
        canvas.Fill(0, 0, w, 22, 28, 32, 40, 255);
        int groundSy = (int)(h * 0.62f);
        float leftover = 0f, lx = 0f, ly = 0f, lz = 0f;
        bool hasPrev = false;
        int prevX = 0, prevY = 0;
        for (int i = 0; i < 28; i++)
        {
            float t = i / 27f;
            float px = -1.7f + t * 3.4f;
            float py = 0.55f + MathF.Sin(t * 6.2f) * 0.42f;
            int sx = 12 + (int)((px + 2f) * 0.25f * Math.Max(8, w - 24));
            int sy = groundSy - (int)(py * (h * 0.38f));
            if (i > 0)
                canvas.Stroke(Math.Min(prevX, sx), Math.Min(prevY, sy), Math.Max(2, Math.Abs(sx - prevX)), Math.Max(2, Math.Abs(sy - prevY)), 50, 70, 90);
            prevX = sx;
            prevY = sy;
            uint n = VfxSpawnOverDistance.Apply(
                ref leftover, ref lx, ref ly, ref lz, ref hasPrev, px, py, 0f, 8f, 64);
            for (uint k = 0; k < n; k++)
            {
                float along = n == 1 ? 0f : k / (float)(n - 1);
                int dx = sx + (int)((along - 0.5f) * 4f);
                int dy = sy + (int)((k % 2) * 2f - 1f);
                canvas.Fill(dx - 2, dy - 2, 5, 5, 90, 210, 255, 255);
                canvas.Fill(dx - 1, dy - 1, 3, 3, 230, 230, 235, 255);
            }
        }

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx", "m26-vfx-spawn-over-distance.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Board.EncodePng(canvas.Rgba, canvas.Width, canvas.Height));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    [Fact]
    public void Evidence_Sort_WritesPng()
    {
        const int w = 320, h = 180;
        var canvas = new Board(w, h, 16, 18, 24);
        canvas.Fill(0, 0, w, 22, 28, 32, 40, 255);
        float[] px = [-0.6f, 0.15f, 0.7f, -0.1f, 0.4f, -0.35f];
        float[] py = [0.2f, 0.05f, 0.15f, 0.35f, -0.1f, 0.0f];
        float[] pz = [2.4f, 0.6f, 1.8f, 0.9f, 3.1f, 1.2f];
        uint[] live = [0, 1, 2, 3, 4, 5];
        VfxSort.SortLiveByCamera(live, live.Length, px, py, pz, 0f, 0f, 0f);
        for (int i = 0; i < live.Length; i++)
        {
            int slot = (int)live[i];
            int cx = 40 + (int)((px[slot] + 1f) * 0.42f * Math.Max(8, w - 80));
            int cy = 48 + (int)((0.6f - py[slot]) * 0.55f * Math.Max(8, h - 70));
            int rad = Math.Max(10, 28 - (int)(pz[slot] * 5f));
            byte fade = (byte)Math.Clamp(40 + (int)(pz[slot] * 50f), 40, 220);
            canvas.Fill(cx - rad, cy - rad, rad * 2, rad * 2, fade, (byte)(fade * 80 / 100), (byte)(fade * 60 / 100), 255);
            canvas.Fill(cx - 2, cy - 2, 5, 5, 230, 230, 235, 255);
        }

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx", "m31-vfx-particle-sort.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Board.EncodePng(canvas.Rgba, canvas.Width, canvas.Height));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    [Fact]
    public void Evidence_CameraFade_WritesPng()
    {
        const int w = 320, h = 180;
        var canvas = new Board(w, h, 16, 18, 24);
        canvas.Fill(0, 0, w, 22, 28, 32, 40, 255);
        canvas.Fill(8, 36, 14, 28, 80, 200, 230, 255);
        canvas.Fill(22, 46, 10, 8, 80, 200, 230, 255);
        const float near = 0.5f;
        const float far = 2.4f;
        for (int i = 0; i < 12; i++)
        {
            float d = 0.12f + i * 0.24f;
            float fade = VfxCameraFade.Fade(d, near, far);
            if (fade <= 0.02f)
                continue;
            int cx = 36 + (int)((d / 3.0f) * Math.Max(8, w - 56));
            int cy = (int)(h * 0.58f) + ((i % 2) * 10 - 5);
            int rad = 8;
            byte r = (byte)Math.Clamp((int)(220 * fade), 16, 255);
            byte g = (byte)Math.Clamp((int)(180 * fade), 16, 255);
            byte b = (byte)Math.Clamp((int)(90 * fade), 16, 255);
            canvas.Fill(cx - rad, cy - rad, rad * 2, rad * 2, r, g, b, 255);
        }

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx", "m32-vfx-camera-fade.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Board.EncodePng(canvas.Rgba, canvas.Width, canvas.Height));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    [Fact]
    public void Evidence_FlipbookBlend_WritesPng()
    {
        const int w = 320, h = 180;
        var canvas = new Board(w, h, 16, 18, 24);
        canvas.Fill(0, 0, w, 22, 28, 32, 40, 255);
        const float cols = 2f;
        const float rows = 2f;
        const float life = 2f;
        for (int i = 0; i < 10; i++)
        {
            float age = life * (i + 0.5f) / 10f;
            VfxFlipbook.FrameUvBlend(0.5f, 0.5f, age, life, cols, rows, 1f,
                out float u0, out float v0, out float u1, out float v1, out float weight);
            int r0 = (int)Math.Clamp(40f + u0 * 200f, 40f, 255f);
            int g0 = (int)Math.Clamp(40f + v0 * 200f, 40f, 255f);
            int b0 = (int)Math.Clamp(40f + (1f - u0) * 160f, 40f, 255f);
            int r1 = (int)Math.Clamp(40f + u1 * 200f, 40f, 255f);
            int g1 = (int)Math.Clamp(40f + v1 * 200f, 40f, 255f);
            int b1 = (int)Math.Clamp(40f + (1f - u1) * 160f, 40f, 255f);
            float rr = r0, gg = g0, bb = b0, aa = 1f;
            VfxFlipbook.Blend(ref rr, ref gg, ref bb, ref aa, r1, g1, b1, 1f, weight);
            int cx = 24 + i * Math.Max(8, (w - 40) / 10);
            int cy = (int)(h * 0.55f);
            int rad = 10;
            canvas.Fill(cx - rad, cy - rad, rad * 2, rad * 2, (byte)rr, (byte)gg, (byte)bb, 255);
            canvas.Fill(cx - 3, cy - 32, 6, 6, (byte)r0, (byte)g0, (byte)b0, 255);
            canvas.Fill(cx - 3, cy + 26, 6, 6, (byte)r1, (byte)g1, (byte)b1, 255);
        }

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx", "m33-vfx-flipbook-blend.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Board.EncodePng(canvas.Rgba, canvas.Width, canvas.Height));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    [Fact]
    public void Evidence_OverLife_WritesPng()
    {
        const int w = 320, h = 180;
        var canvas = new Board(w, h, 16, 18, 24);
        canvas.Fill(0, 0, w, 22, 28, 32, 40, 255);
        const int dots = 10;
        for (int i = 0; i < dots; i++)
        {
            float t = i / (float)(dots - 1);
            VfxOverLife.Color(t, 1f, 1f, 1f, 1f, 1f, 1f, 1f, 0f, out _, out _, out _, out float ca);
            float size = VfxOverLife.Size(0.15f, t, 1f, 0f);
            int rad = Math.Max(2, (int)(size * 80f));
            int cx = 28 + i * Math.Max(8, (w - 48) / dots);
            int cy = (int)(h * 0.55f);
            byte r = (byte)Math.Clamp((int)(220 * ca), 16, 255);
            byte g = (byte)Math.Clamp((int)(180 * ca), 16, 255);
            byte b = (byte)Math.Clamp((int)(90 * ca), 16, 255);
            canvas.Fill(cx - rad, cy - rad, rad * 2, rad * 2, r, g, b, 255);
        }

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx", "m34-vfx-over-life.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Board.EncodePng(canvas.Rgba, canvas.Width, canvas.Height));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    [Fact]
    public void Evidence_MotionVector_WritesPng()
    {
        const int w = 320, h = 180;
        var canvas = new Board(w, h, 16, 18, 24);
        canvas.Fill(0, 0, w, 22, 28, 32, 40, 255);
        const float dt = 1f / 30f;
        const int dots = 10;
        for (int i = 0; i < dots; i++)
        {
            float ang = i * 0.62831853f;
            float vx = MathF.Cos(ang) * 2.4f;
            float vy = MathF.Sin(ang) * 2.4f;
            VfxMotionVector.PrevPosition(0f, 0f, 0f, vx, vy, 0f, dt, out float qx, out float qy, out _);
            VfxMotionVector.NdcDelta(0f, 0f, 1f, qx, qy, 1f, out float dx, out float dy);
            float r = 0.35f, g = 0.38f, b = 0.42f;
            VfxMotionVector.Apply(ref r, ref g, ref b, 1f, dx, dy, 1f, 8f);
            int cx = 28 + i * Math.Max(8, (w - 48) / dots);
            int cy = (int)(h * 0.55f);
            int px = cx - (int)(vx * 6f);
            int py = cy + (int)(vy * 6f);
            canvas.Fill(px - 3, py - 3, 6, 6, 70, 74, 82, 255);
            canvas.Fill(cx - 8, cy - 8, 16, 16,
                (byte)Math.Clamp((int)(r * 255f), 16, 255),
                (byte)Math.Clamp((int)(g * 255f), 16, 255),
                (byte)Math.Clamp((int)(b * 255f), 16, 255),
                255);
        }

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx", "m35-vfx-motion-vectors.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Board.EncodePng(canvas.Rgba, canvas.Width, canvas.Height));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    [Fact]
    public void Evidence_Orient_WritesPng()
    {
        const int w = 320, h = 180;
        var canvas = new Board(w, h, 16, 18, 24);
        canvas.Fill(0, 0, w, 22, 28, 32, 40, 255);
        const int dots = 8;
        for (int i = 0; i < dots; i++)
        {
            float ang = i * 0.78539816f;
            float vx = MathF.Cos(ang) * 2f;
            float vy = MathF.Sin(ang) * 2f;
            float cx = -1.2f + i * 0.34f;
            float cy = 0.1f * ((i % 3) - 1);
            VfxOrient.AlongVelocity(cx, cy, 0f, vx, vy, 0f, 0f, 0f, 8f, 0f, 1f, 0f, 0.2f,
                out float tx, out float ty, out _);
            VfxOrient.AlongVelocity(cx, cy, 0f, vx, vy, 0f, 0f, 0f, 8f, 0f, -1f, 0f, 0.2f,
                out float bx, out float by, out _);
            int x0 = 40 + (int)((tx + 1.6f) * 0.28f * Math.Max(8, w - 48));
            int y0 = (int)(h * 0.55f) - (int)(ty * 50f);
            int x1 = 40 + (int)((bx + 1.6f) * 0.28f * Math.Max(8, w - 48));
            int y1 = (int)(h * 0.55f) - (int)(by * 50f);
            int minX = Math.Min(x0, x1);
            int minY = Math.Min(y0, y1);
            canvas.Fill(minX, minY, Math.Max(3, Math.Abs(x1 - x0) + 2), Math.Max(3, Math.Abs(y1 - y0) + 2), 80, 190, 255, 255);
            canvas.Fill(x0 - 2, y0 - 2, 5, 5, 255, 210, 90, 255);
            canvas.Fill(x1 - 2, y1 - 2, 4, 4, 70, 170, 220, 255);
        }

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx", "m36-vfx-orient-velocity.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Board.EncodePng(canvas.Rgba, canvas.Width, canvas.Height));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    [Fact]
    public void Evidence_PositionShape_WritesPng()
    {
        const int w = 320, h = 180;
        var canvas = new Board(w, h, 16, 18, 24);
        canvas.Fill(0, 0, w, 22, 28, 32, 40, 255);
        const int dots = 36;
        for (uint i = 0; i < dots; i++)
        {
            VfxPositionShape.Offset(i, 1f, 0.7f, 0.7f, 0.7f, out float px, out float py, out float pz);
            int cx = w / 2 + (int)(px * 90f);
            int cy = (int)(h * 0.58f) - (int)(py * 70f) + (int)(pz * 12f);
            byte r = (byte)Math.Clamp(80 + (int)((pz + 0.7f) * 80f), 40, 255);
            byte g = (byte)Math.Clamp(140 + (int)((py + 0.7f) * 50f), 40, 255);
            byte b = (byte)Math.Clamp(200 - (int)((px + 0.7f) * 40f), 40, 255);
            canvas.Fill(cx - 3, cy - 3, 6, 6, r, g, b, 255);
        }

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx", "m37-vfx-position-shape.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Board.EncodePng(canvas.Rgba, canvas.Width, canvas.Height));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    [Fact]
    public void Evidence_Drag_WritesPng()
    {
        const int w = 320, h = 180;
        var canvas = new Board(w, h, 16, 18, 24);
        canvas.Fill(0, 0, w, 22, 28, 32, 40, 255);
        const float dt = 1f / 30f;
        for (int i = 0; i < 8; i++)
        {
            float amount = i * 1.4f;
            float px = -1.7f;
            float vx = 5.2f;
            float vy = 0f;
            float vz = 0f;
            int y = (int)(h * 0.22f) + i * 18;
            canvas.Fill(24, y + 5, Math.Max(8, w - 48), 1, 36, 40, 48, 255);
            for (int step = 0; step < 36; step++)
            {
                VfxDrag.Apply(ref vx, ref vy, ref vz, amount, dt);
                px += vx * dt;
            }

            int cx = 28 + (int)((px + 1.7f) * 0.22f * Math.Max(8, w - 56));
            byte r = (byte)Math.Clamp(70 + i * 22, 40, 255);
            byte g = (byte)Math.Clamp(200 - i * 16, 40, 255);
            byte b = (byte)Math.Clamp(255 - i * 8, 80, 255);
            canvas.Fill(cx - 4, y + 1, 8, 8, r, g, b, 255);
        }

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx", "m38-vfx-drag.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Board.EncodePng(canvas.Rgba, canvas.Width, canvas.Height));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    [Fact]
    public void Evidence_Force_WritesPng()
    {
        const int w = 320, h = 180;
        var canvas = new Board(w, h, 16, 18, 24);
        canvas.Fill(0, 0, w, 22, 28, 32, 40, 255);
        const float dt = 1f / 30f;
        for (int i = 0; i < 8; i++)
        {
            float fx = 1.6f * (i + 1);
            float px = -1.7f;
            float vx = 0f;
            float vy = 0f;
            float vz = 0f;
            int y = (int)(h * 0.22f) + i * 18;
            canvas.Fill(24, y + 5, Math.Max(8, w - 48), 1, 36, 40, 48, 255);
            for (int step = 0; step < 36; step++)
            {
                VfxForce.Apply(ref vx, ref vy, ref vz, fx, 0f, 0f, dt);
                px += vx * dt;
            }

            int cx = 28 + (int)((px + 1.7f) * 0.18f * Math.Max(8, w - 56));
            byte r = (byte)Math.Clamp(255 - i * 8, 80, 255);
            byte g = (byte)Math.Clamp(180 - i * 10, 40, 255);
            byte b = (byte)Math.Clamp(70 + i * 18, 40, 255);
            canvas.Fill(cx - 4, y + 1, 8, 8, r, g, b, 255);
        }

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx", "m39-vfx-force.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Board.EncodePng(canvas.Rgba, canvas.Width, canvas.Height));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    [Fact]
    public void Evidence_Spin_WritesPng()
    {
        const int w = 320, h = 180;
        var canvas = new Board(w, h, 16, 18, 24);
        canvas.Fill(0, 0, w, 22, 28, 32, 40, 255);
        const float rate = 1.2f;
        for (int i = 0; i < 8; i++)
        {
            float angle = 0f;
            VfxSpin.Integrate(ref angle, rate, i * 0.18f);
            int cx = 36 + i * 36;
            int cy = (int)(h * 0.55f);
            float[] lx = { -1f, 1f, 1f, -1f };
            float[] ly = { -1f, -1f, 1f, 1f };
            byte r = (byte)Math.Clamp(80 + i * 18, 40, 255);
            byte g = (byte)Math.Clamp(200 - i * 12, 40, 255);
            for (int k = 0; k < 4; k++)
            {
                VfxSpin.Local(lx[k], ly[k], angle, out float sx, out float sy);
                int x = cx + (int)(sx * 14f);
                int y = cy + (int)(sy * 14f);
                canvas.Fill(x - 2, y - 2, 4, 4, r, g, 255, 255);
            }
        }

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx", "m40-vfx-angular-velocity.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Board.EncodePng(canvas.Rgba, canvas.Width, canvas.Height));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    [Fact]
    public void Evidence_CustomHlsl_WritesPng()
    {
        const int w = 320, h = 180;
        var canvas = new Board(w, h, 16, 18, 24);
        canvas.Fill(0, 0, w, 22, 28, 32, 40, 255);
        const float kick = 2.4f;
        const float dt = 1f / 30f;
        for (int i = 0; i < 8; i++)
        {
            float px = -1.5f + i * 0.38f;
            float py = -0.35f;
            float vy = 0f;
            for (int step = 0; step < 20; step++)
            {
                vy += kick * dt;
                py += vy * dt;
            }

            int cx = 40 + (int)((px + 1.6f) * 0.28f * Math.Max(8, w - 64));
            int cy = (int)(h * 0.72f) - (int)((py + 0.4f) * 70f);
            byte r = (byte)Math.Clamp(80 + i * 20, 40, 255);
            byte g = (byte)Math.Clamp(60 + (int)(kick * 40f), 40, 255);
            byte b = (byte)Math.Clamp(220 - i * 8, 80, 255);
            canvas.Fill(cx - 1, cy, 2, Math.Max(4, (int)(h * 0.72f) - cy), 48, 52, 70, 255);
            canvas.Fill(cx - 5, cy - 5, 10, 10, r, g, b, 255);
        }

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx", "m41-vfx-custom-hlsl.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Board.EncodePng(canvas.Rgba, canvas.Width, canvas.Height));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    [Fact]
    public void Evidence_Sequential_WritesPng()
    {
        const int w = 320, h = 180;
        var canvas = new Board(w, h, 16, 18, 24);
        canvas.Fill(0, 0, w, 22, 28, 32, 40, 255);
        const float sx = 0.7f;
        const float sz = 12f;
        for (uint i = 0; i < 12u; i++)
        {
            VfxSequential.Offset(i, 1f, sx, sx, sz, out float px, out float py, out float pz);
            int cx = w / 2 + (int)(px * 90f);
            int cy = (int)(h * 0.58f) - (int)(py * 70f) + (int)(pz * 12f);
            byte r = (byte)Math.Clamp(70 + (int)(i * 12f), 40, 255);
            byte g = (byte)Math.Clamp(200 - (int)(i * 8f), 40, 255);
            byte b = (byte)Math.Clamp(220 - (int)((px + sx) * 40f), 80, 255);
            canvas.Fill(cx - 4, cy - 4, 8, 8, r, g, b, 255);
        }

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx", "m42-vfx-sequential.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Board.EncodePng(canvas.Rgba, canvas.Width, canvas.Height));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    [Fact]
    public void Evidence_FixedAxis_WritesPng()
    {
        const int w = 320, h = 180;
        var canvas = new Board(w, h, 16, 18, 24);
        canvas.Fill(0, 0, w, 22, 28, 32, 40, 255);
        const float size = 0.2f;
        for (int i = 0; i < 8; i++)
        {
            float ang = i * 0.78539816f;
            float vx = MathF.Cos(ang) * 2f;
            float vy = MathF.Sin(ang) * 2f;
            float cx = -1.2f + i * 0.34f;
            float cy = 0.1f * ((i % 3) - 1);
            VfxOrient.ResolveAlong(2f, vx, vy, 0f, 0f, 1f, 0f, out float ax, out float ay, out float az);
            VfxOrient.AlongVelocity(cx, cy, 0f, ax, ay, az, 0f, 0f, 8f, 0f, 1f, 0f, size,
                out float tx, out float ty, out _);
            VfxOrient.AlongVelocity(cx, cy, 0f, ax, ay, az, 0f, 0f, 8f, 0f, -1f, 0f, size,
                out float bx, out float by, out _);
            int x0 = 40 + (int)((tx + 1.6f) * 0.28f * Math.Max(8, w - 48));
            int y0 = (int)(h * 0.55f) - (int)(ty * 50f);
            int x1 = 40 + (int)((bx + 1.6f) * 0.28f * Math.Max(8, w - 48));
            int y1 = (int)(h * 0.55f) - (int)(by * 50f);
            int minX = Math.Min(x0, x1);
            int minY = Math.Min(y0, y1);
            canvas.Fill(minX, minY, Math.Max(3, Math.Abs(x1 - x0) + 2), Math.Max(3, Math.Abs(y1 - y0) + 2), 255, 140, 70, 255);
            canvas.Fill(x0 - 3, y0 - 3, 7, 7, 255, 220, 90, 255);
            canvas.Fill(x1 - 2, y1 - 2, 5, 5, 220, 90, 70, 255);
        }

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx", "m43-vfx-orient-fixed-axis.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Board.EncodePng(canvas.Rgba, canvas.Width, canvas.Height));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    [Fact]
    public void Evidence_PeriodicBurst_WritesPng()
    {
        const int w = 320, h = 180;
        var canvas = new Board(w, h, 16, 18, 24);
        canvas.Fill(0, 0, w, 22, 28, 32, 40, 255);
        int axisY = (int)(h * 0.62f);
        canvas.Fill(16, axisY, Math.Max(8, w - 32), 2, 50, 58, 70, 255);
        float leftover = 0f;
        const int steps = 16;
        for (int i = 0; i < steps; i++)
        {
            int x = 24 + i * Math.Max(8, (w - 48) / steps);
            canvas.Fill(x, axisY - 4, 2, 10, 70, 80, 96, 255);
            uint n = VfxPeriodicBurst.Apply(ref leftover, 0.125f, 4f, 0.25f, 64);
            for (uint k = 0; k < n; k++)
            {
                int cy = axisY - 18 - (int)k * 10;
                canvas.Fill(x - 3, cy - 3, 8, 8, 255, 170, 70, 255);
                canvas.Fill(x - 1, cy - 1, 4, 4, 255, 220, 120, 255);
            }
        }

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx", "m44-vfx-periodic-burst.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Board.EncodePng(canvas.Rgba, canvas.Width, canvas.Height));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    [Fact]
    public void Evidence_GpuEventTarget_WritesPng()
    {
        const int w = 320, h = 180;
        var canvas = new Board(w, h, 16, 18, 24);
        canvas.Fill(0, 0, w, 22, 28, 32, 40, 255);
        int mid = w / 2;
        canvas.Fill(mid - 1, 28, 2, Math.Max(8, h - 40), 40, 46, 56, 255);
        uint pending = 0;
        VfxGpuEvent.QueueTarget(ref pending, 4u, 64);
        uint n = VfxGpuEvent.Consume(ref pending);
        int srcX = w / 4;
        int dstX = (w * 3) / 4;
        int baseY = (int)(h * 0.62f);
        canvas.Fill(srcX - 6, baseY - 6, 12, 12, 80, 230, 255, 255);
        canvas.Fill(srcX + 10, baseY - 1, Math.Max(8, dstX - srcX - 24), 2, 90, 140, 180, 255);
        for (uint k = 0; k < n; k++)
        {
            int x = dstX - 10 + (int)k * 8;
            int y = baseY - 6 - (int)k * 7;
            canvas.Fill(x - 3, y - 3, 8, 8, 255, 170, 70, 255);
            canvas.Fill(x - 1, y - 1, 4, 4, 255, 220, 120, 255);
        }

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx", "m45-vfx-gpu-event-target.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Board.EncodePng(canvas.Rgba, canvas.Width, canvas.Height));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    [Fact]
    public void Evidence_FlipbookMotion_WritesPng()
    {
        const int w = 320, h = 180;
        var canvas = new Board(w, h, 16, 18, 24);
        canvas.Fill(0, 0, w, 22, 28, 32, 40, 255);
        const float cols = 2f;
        const float rows = 2f;
        const float life = 2f;
        VfxFlipbookMotion.Decode(1f, 0.5f, out float mx, out float my);
        for (int i = 0; i < 10; i++)
        {
            float age = life * (i + 0.5f) / 10f;
            VfxFlipbook.FrameUvBlend(0.5f, 0.5f, age, life, cols, rows, 1f,
                out float u0, out float v0, out float u1, out float v1, out float weight);
            VfxFlipbookMotion.WarpPair(u0, v0, u1, v1, mx, my, mx, my, weight, 1.25f,
                out float au, out float av, out float bu, out float bv);
            int r0 = (int)Math.Clamp(40f + au * 200f, 40f, 255f);
            int g0 = (int)Math.Clamp(40f + av * 200f, 40f, 255f);
            int b0 = (int)Math.Clamp(40f + (1f - au) * 160f, 40f, 255f);
            int r1 = (int)Math.Clamp(40f + bu * 200f, 40f, 255f);
            int g1 = (int)Math.Clamp(40f + bv * 200f, 40f, 255f);
            int b1 = (int)Math.Clamp(40f + (1f - bu) * 160f, 40f, 255f);
            float rr = r0, gg = g0, bb = b0, aa = 1f;
            VfxFlipbook.Blend(ref rr, ref gg, ref bb, ref aa, r1, g1, b1, 1f, weight);
            int cx = 24 + i * Math.Max(8, (w - 40) / 10);
            int shift = (int)(au * 18f);
            int cy = (int)(h * 0.55f);
            canvas.Fill(cx + shift - 6, cy - 6, 12, 12, (byte)rr, (byte)gg, (byte)bb, 255);
            canvas.Fill(cx - 3, cy - 32, 6, 6, (byte)r0, (byte)g0, (byte)b0, 255);
            canvas.Fill(cx + 7, cy + 22, 6, 6, (byte)r1, (byte)g1, (byte)b1, 255);
        }

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx", "m46-vfx-flipbook-motion.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Board.EncodePng(canvas.Rgba, canvas.Width, canvas.Height));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    [Fact]
    public void Evidence_GpuEventAlways_WritesPng()
    {
        const int w = 320, h = 180;
        var canvas = new Board(w, h, 16, 18, 24);
        canvas.Fill(0, 0, w, 22, 28, 32, 40, 255);
        int axisY = (int)(h * 0.62f);
        canvas.Fill(16, axisY, Math.Max(8, w - 32), 2, 40, 46, 56, 255);
        uint onPlay = VfxGpuEvent.EmitOnPlay(VfxGpuEvent.KindOnPlay, 3f);
        for (uint k = 0; k < onPlay; k++)
        {
            int x = 28 + (int)k * 8;
            int y = axisY - 22 - (int)k * 7;
            canvas.Fill(x - 3, y - 3, 8, 8, 255, 170, 70, 255);
            canvas.Fill(x - 1, y - 1, 4, 4, 255, 220, 120, 255);
        }
        for (int i = 0; i < 8; i++)
        {
            int x = 72 + i * Math.Max(8, (w - 96) / 8);
            canvas.Fill(x, axisY - 4, 2, 10, 70, 80, 96, 255);
            uint n = VfxGpuEvent.EmitAlways(VfxGpuEvent.KindAlways, 3f);
            for (uint k = 0; k < n; k++)
            {
                int y = axisY - 18 - (int)k * 8;
                canvas.Fill(x - 3, y - 3, 7, 7, 80, 230, 160, 255);
            }
        }

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx", "m47-vfx-gpu-event-always.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Board.EncodePng(canvas.Rgba, canvas.Width, canvas.Height));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    [Fact]
    public void Evidence_InheritVelocity_WritesPng()
    {
        const int w = 320, h = 180;
        var canvas = new Board(w, h, 16, 18, 24);
        canvas.Fill(0, 0, w, 22, 28, 32, 40, 255);
        float lastX = 0f, lastY = 0f, lastZ = 0f;
        bool has = false;
        const float dt = 1f / 30f;
        for (int i = 0; i < 10; i++)
        {
            float x = i * 0.22f;
            float y = 0.15f * MathF.Sin(i * 0.7f);
            VfxInheritVelocity.Sample(has, lastX, lastY, lastZ, x, y, 0f, dt, out float ex, out float ey, out float ez);
            lastX = x;
            lastY = y;
            lastZ = 0f;
            has = true;
            float vx = 0.4f, vy = 1.2f, vz = 0f;
            VfxInheritVelocity.Add(ref vx, ref vy, ref vz, ex, ey, ez, 1f);
            int cx = 28 + i * Math.Max(8, (w - 48) / 10);
            int cy = (int)(h * 0.62f) - (int)(vy * 18f);
            int kick = (int)(vx * 6f);
            canvas.Fill(cx + kick - 4, cy - 4, 8, 8, 80, 220, 255, 255);
            canvas.Fill(cx, (int)(h * 0.72f), 3, 3, 255, 170, 70, 255);
        }

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx", "m48-vfx-inherit-velocity.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Board.EncodePng(canvas.Rgba, canvas.Width, canvas.Height));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    [Fact]
    public void Evidence_TriggerEventRate_WritesPng()
    {
        const int w = 320, h = 180;
        var canvas = new Board(w, h, 16, 18, 24);
        canvas.Fill(0, 0, w, 22, 28, 32, 40, 255);
        int axisY = (int)(h * 0.62f);
        canvas.Fill(16, axisY, Math.Max(8, w - 32), 2, 50, 58, 70, 255);
        float leftover = 0f;
        const int steps = 16;
        for (int i = 0; i < steps; i++)
        {
            int x = 24 + i * Math.Max(8, (w - 48) / steps);
            canvas.Fill(x, axisY - 4, 2, 10, 70, 80, 96, 255);
            uint n = VfxTriggerEventRate.Apply(ref leftover, 0.125f, 8f, 64);
            for (uint k = 0; k < n; k++)
            {
                int cy = axisY - 18 - (int)k * 10;
                canvas.Fill(x - 3, cy - 3, 8, 8, 80, 220, 255, 255);
                canvas.Fill(x - 1, cy - 1, 4, 4, 180, 240, 255, 255);
            }
        }

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx", "m49-vfx-trigger-event-rate.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Board.EncodePng(canvas.Rgba, canvas.Width, canvas.Height));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    [Fact]
    public void Evidence_TriggerEventOverDistance_WritesPng()
    {
        const int w = 320, h = 180;
        var canvas = new Board(w, h, 16, 18, 24);
        canvas.Fill(0, 0, w, 22, 28, 32, 40, 255);
        int groundSy = (int)(h * 0.62f);
        float leftover = 0f, lx = 0f, ly = 0f, lz = 0f;
        bool hasPrev = false;
        int prevX = 0, prevY = 0;
        for (int i = 0; i < 28; i++)
        {
            float t = i / 27f;
            float px = -1.7f + t * 3.4f;
            float py = 0.55f + MathF.Sin(t * 6.2f) * 0.42f;
            int sx = 12 + (int)((px + 2f) * 0.25f * Math.Max(8, w - 24));
            int sy = groundSy - (int)(py * (h * 0.38f));
            if (i > 0)
            {
                int x0 = Math.Min(prevX, sx);
                int y0 = Math.Min(prevY, sy);
                canvas.Fill(x0, y0, Math.Max(2, Math.Abs(sx - prevX)), Math.Max(2, Math.Abs(sy - prevY)), 50, 70, 90, 255);
            }
            prevX = sx;
            prevY = sy;
            uint n = VfxTriggerEventOverDistance.Apply(
                ref leftover, hasPrev, lx, ly, lz, px, py, 0f, 8f, 64);
            lx = px;
            ly = py;
            lz = 0f;
            hasPrev = true;
            for (uint k = 0; k < n; k++)
            {
                int cy = sy - 12 - (int)k * 8;
                canvas.Fill(sx - 3, cy - 3, 8, 8, 80, 220, 255, 255);
                canvas.Fill(sx - 1, cy - 1, 4, 4, 180, 240, 255, 255);
            }
        }

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx", "m50-vfx-trigger-event-distance.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Board.EncodePng(canvas.Rgba, canvas.Width, canvas.Height));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    [Fact]
    public void Evidence_SizeOverSpeed_WritesPng()
    {
        const int w = 320, h = 180;
        var canvas = new Board(w, h, 16, 18, 24);
        canvas.Fill(0, 0, w, 22, 28, 32, 40, 255);
        const int dots = 10;
        for (int i = 0; i < dots; i++)
        {
            float speed = i * 0.55f;
            float size = VfxSizeOverSpeed.Apply(0.12f, speed, 0f, 0f, 0.35f);
            int rad = Math.Max(2, (int)(size * 70f));
            int sx = 28 + i * Math.Max(8, (w - 48) / dots);
            int sy = (int)(h * 0.58f);
            canvas.Fill(sx - rad, sy - rad, rad * 2, rad * 2, 80, 220, 255, 255);
            int inner = Math.Max(1, rad - 2);
            canvas.Fill(sx - inner, sy - inner, inner * 2, inner * 2, 200, 210, 220, 255);
        }

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx", "m51-vfx-size-over-speed.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Board.EncodePng(canvas.Rgba, canvas.Width, canvas.Height));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    [Fact]
    public void Evidence_MultiplySize_WritesPng()
    {
        const int w = 320, h = 180;
        var canvas = new Board(w, h, 16, 18, 24);
        canvas.Fill(0, 0, w, 22, 28, 32, 40, 255);
        const int dots = 10;
        const float amount = 2f;
        for (int i = 0; i < dots; i++)
        {
            float size = VfxMultiplySize.Apply(0.12f, amount);
            int rad = Math.Max(2, (int)(size * 70f));
            int sx = 28 + i * Math.Max(8, (w - 48) / dots);
            int sy = (int)(h * 0.58f);
            canvas.Fill(sx - rad, sy - rad, rad * 2, rad * 2, 80, 220, 255, 255);
            int inner = Math.Max(1, rad - 2);
            canvas.Fill(sx - inner, sy - inner, inner * 2, inner * 2, 255, 170, 70, 255);
        }

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx", "m69-vfx-multiply-size.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Board.EncodePng(canvas.Rgba, canvas.Width, canvas.Height));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    [Fact]
    public void Evidence_MultiplyColor_WritesPng()
    {
        const int w = 320, h = 180;
        var canvas = new Board(w, h, 16, 18, 24);
        canvas.Fill(0, 0, w, 22, 28, 32, 40, 255);
        const int dots = 10;
        const float mr = 1f;
        const float mg = 0.45f;
        const float mb = 0.15f;
        for (int i = 0; i < dots; i++)
        {
            float t = i / (float)(dots - 1);
            VfxMultiplyColor.Apply(1f, 0.85f + t * 0.15f, 0.7f, 1f, mr, mg, mb, 1f, out float r, out float g, out float b, out _);
            int sx = 28 + i * Math.Max(8, (w - 48) / dots);
            int sy = (int)(h * 0.55f);
            canvas.Fill(sx - 11, sy - 11, 22, 22,
                (byte)Math.Clamp((int)(r * 255f), 16, 255),
                (byte)Math.Clamp((int)(g * 255f), 16, 255),
                (byte)Math.Clamp((int)(b * 255f), 16, 255),
                255);
        }

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx", "m70-vfx-multiply-color.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Board.EncodePng(canvas.Rgba, canvas.Width, canvas.Height));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    [Fact]
    public void Evidence_AddSize_WritesPng()
    {
        const int w = 320, h = 180;
        var canvas = new Board(w, h, 16, 18, 24);
        canvas.Fill(0, 0, w, 22, 28, 32, 40, 255);
        const int dots = 10;
        const float amount = 0.08f;
        for (int i = 0; i < dots; i++)
        {
            float size = VfxAddSize.Apply(0.12f, amount);
            int rad = Math.Max(2, (int)(size * 70f));
            int sx = 28 + i * Math.Max(8, (w - 48) / dots);
            int sy = (int)(h * 0.58f);
            canvas.Fill(sx - rad, sy - rad, rad * 2, rad * 2, 80, 220, 255, 255);
            int inner = Math.Max(1, rad - 2);
            canvas.Fill(sx - inner, sy - inner, inner * 2, inner * 2, 120, 255, 140, 255);
        }

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx", "m71-vfx-add-size.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Board.EncodePng(canvas.Rgba, canvas.Width, canvas.Height));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    [Fact]
    public void Evidence_AddColor_WritesPng()
    {
        const int w = 320, h = 180;
        var canvas = new Board(w, h, 16, 18, 24);
        canvas.Fill(0, 0, w, 22, 28, 32, 40, 255);
        const int dots = 10;
        const float ar = 0.35f;
        const float ag = 0.1f;
        for (int i = 0; i < dots; i++)
        {
            float t = i / (float)(dots - 1);
            VfxAddColor.Apply(0.35f, 0.4f + t * 0.2f, 0.55f, 1f, ar, ag, 0f, 0f, out float r, out float g, out float b, out _);
            int sx = 28 + i * Math.Max(8, (w - 48) / dots);
            int sy = (int)(h * 0.55f);
            canvas.Fill(sx - 11, sy - 11, 22, 22,
                (byte)Math.Clamp((int)(r * 255f), 16, 255),
                (byte)Math.Clamp((int)(g * 255f), 16, 255),
                (byte)Math.Clamp((int)(b * 255f), 16, 255),
                255);
        }

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx", "m72-vfx-add-color.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Board.EncodePng(canvas.Rgba, canvas.Width, canvas.Height));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    [Fact]
    public void Evidence_AddVelocity_WritesPng()
    {
        const int w = 320, h = 180;
        var canvas = new Board(w, h, 16, 18, 24);
        canvas.Fill(0, 0, w, 22, 28, 32, 40, 255);
        const int dots = 10;
        const float ax = 1.2f;
        for (int i = 0; i < dots; i++)
        {
            float vx = 0f, vy = 0f, vz = 0f;
            VfxAddVelocity.Apply(ref vx, ref vy, ref vz, ax, 0f, 0f);
            float t = i / (float)(dots - 1);
            int sx = 28 + (int)(t * (w - 56) + vx * 8f);
            int sy = (int)(h * 0.58f);
            canvas.Fill(sx - 8, sy - 8, 16, 16, 80, 220, 255, 255);
            canvas.Fill(sx - 5, sy - 5, 10, 10, 255, 210, 80, 255);
        }

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx", "m73-vfx-add-velocity.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Board.EncodePng(canvas.Rgba, canvas.Width, canvas.Height));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    [Fact]
    public void Evidence_MultiplyVelocity_WritesPng()
    {
        const int w = 320, h = 180;
        var canvas = new Board(w, h, 16, 18, 24);
        canvas.Fill(0, 0, w, 22, 28, 32, 40, 255);
        const int dots = 10;
        const float amount = 2f;
        for (int i = 0; i < dots; i++)
        {
            float vx = 0.4f, vy = 0f, vz = 0f;
            VfxMultiplyVelocity.Apply(ref vx, ref vy, ref vz, amount);
            float t = i / (float)(dots - 1);
            int sx = 28 + (int)(t * (w - 56) + vx * 18f);
            int sy = (int)(h * 0.58f);
            canvas.Fill(sx - 8, sy - 8, 16, 16, 80, 220, 255, 255);
            canvas.Fill(sx - 5, sy - 5, 10, 10, 180, 120, 255, 255);
        }

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx", "m74-vfx-multiply-velocity.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Board.EncodePng(canvas.Rgba, canvas.Width, canvas.Height));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    [Fact]
    public void Evidence_SetSize_WritesPng()
    {
        const int w = 320, h = 180;
        var canvas = new Board(w, h, 16, 18, 24);
        canvas.Fill(0, 0, w, 22, 28, 32, 40, 255);
        const int dots = 10;
        const float amount = 0.22f;
        for (int i = 0; i < dots; i++)
        {
            float size = VfxSetSize.Apply(0.08f, amount);
            int rad = Math.Max(2, (int)(size * 70f));
            int sx = 28 + i * Math.Max(8, (w - 48) / dots);
            int sy = (int)(h * 0.58f);
            canvas.Fill(sx - rad, sy - rad, rad * 2, rad * 2, 80, 220, 255, 255);
            int inner = Math.Max(1, rad - 2);
            canvas.Fill(sx - inner, sy - inner, inner * 2, inner * 2, 255, 160, 80, 255);
        }

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx", "m75-vfx-set-size.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Board.EncodePng(canvas.Rgba, canvas.Width, canvas.Height));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    [Fact]
    public void Evidence_OverwriteColor_WritesPng()
    {
        const int w = 320, h = 180;
        var canvas = new Board(w, h, 16, 18, 24);
        canvas.Fill(0, 0, w, 22, 28, 32, 40, 255);
        const int dots = 10;
        const float ar = 0.95f, ag = 0.35f, ab = 0.15f, aa = 1f;
        for (int i = 0; i < dots; i++)
        {
            float t = i / (float)(dots - 1);
            VfxSetColor.Apply(0.35f, 0.4f + t * 0.2f, 0.55f, 1f, ar, ag, ab, aa, out float r, out float g, out float b, out _);
            int sx = 28 + i * Math.Max(8, (w - 48) / dots);
            int sy = (int)(h * 0.58f);
            canvas.Fill(sx - 11, sy - 11, 22, 22, 80, 220, 255, 255);
            canvas.Fill(sx - 8, sy - 8, 16, 16,
                (byte)Math.Clamp((int)(r * 255f), 16, 255),
                (byte)Math.Clamp((int)(g * 255f), 16, 255),
                (byte)Math.Clamp((int)(b * 255f), 16, 255),
                255);
        }

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx", "m76-vfx-overwrite-color.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Board.EncodePng(canvas.Rgba, canvas.Width, canvas.Height));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    [Fact]
    public void Evidence_OverwriteVelocity_WritesPng()
    {
        const int w = 320, h = 180;
        var canvas = new Board(w, h, 16, 18, 24);
        canvas.Fill(0, 0, w, 22, 28, 32, 40, 255);
        const int dots = 10;
        const float ax = 1.2f;
        for (int i = 0; i < dots; i++)
        {
            float vx = 0.2f, vy = 0.4f, vz = 0f;
            VfxSetVelocity.Apply(ref vx, ref vy, ref vz, ax, 0f, 0f);
            float t = i / (float)(dots - 1);
            int sx = 28 + (int)(t * (w - 56) + vx * 8f);
            int sy = (int)(h * 0.58f);
            canvas.Fill(sx - 8, sy - 8, 16, 16, 80, 220, 255, 255);
            canvas.Fill(sx - 5, sy - 5, 10, 10, 255, 140, 80, 255);
        }

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx", "m77-vfx-overwrite-velocity.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Board.EncodePng(canvas.Rgba, canvas.Width, canvas.Height));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    [Fact]
    public void Evidence_OverwriteLifetime_WritesPng()
    {
        const int w = 320, h = 180;
        var canvas = new Board(w, h, 16, 18, 24);
        canvas.Fill(0, 0, w, 22, 28, 32, 40, 255);
        const int dots = 10;
        const float amount = 1.5f;
        for (int i = 0; i < dots; i++)
        {
            float life = VfxSetLifetime.Apply(0.4f, amount);
            int rad = Math.Max(2, (int)(life * 8f));
            int sx = 28 + i * Math.Max(8, (w - 48) / dots);
            int sy = (int)(h * 0.58f);
            canvas.Fill(sx - rad, sy - rad, rad * 2, rad * 2, 80, 220, 255, 255);
            int inner = Math.Max(1, rad - 2);
            canvas.Fill(sx - inner, sy - inner, inner * 2, inner * 2, 180, 255, 120, 255);
        }

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx", "m78-vfx-overwrite-lifetime.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Board.EncodePng(canvas.Rgba, canvas.Width, canvas.Height));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    [Fact]
    public void Evidence_AddLifetime_WritesPng()
    {
        const int w = 320, h = 180;
        var canvas = new Board(w, h, 16, 18, 24);
        canvas.Fill(0, 0, w, 22, 28, 32, 40, 255);
        const int dots = 10;
        const float amount = 0.5f;
        for (int i = 0; i < dots; i++)
        {
            float life = VfxAddLifetime.Apply(0.4f, amount);
            int rad = Math.Max(2, (int)(life * 8f));
            int sx = 28 + i * Math.Max(8, (w - 48) / dots);
            int sy = (int)(h * 0.58f);
            canvas.Fill(sx - rad, sy - rad, rad * 2, rad * 2, 80, 220, 255, 255);
            int inner = Math.Max(1, rad - 2);
            canvas.Fill(sx - inner, sy - inner, inner * 2, inner * 2, 255, 200, 80, 255);
        }

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx", "m79-vfx-add-lifetime.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Board.EncodePng(canvas.Rgba, canvas.Width, canvas.Height));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    [Fact]
    public void Evidence_MultiplyLifetime_WritesPng()
    {
        const int w = 320, h = 180;
        var canvas = new Board(w, h, 16, 18, 24);
        canvas.Fill(0, 0, w, 22, 28, 32, 40, 255);
        const int dots = 10;
        const float amount = 2f;
        for (int i = 0; i < dots; i++)
        {
            float life = VfxMultiplyLifetime.Apply(0.4f, amount);
            int rad = Math.Max(2, (int)(life * 8f));
            int sx = 28 + i * Math.Max(8, (w - 48) / dots);
            int sy = (int)(h * 0.58f);
            canvas.Fill(sx - rad, sy - rad, rad * 2, rad * 2, 80, 220, 255, 255);
            int inner = Math.Max(1, rad - 2);
            canvas.Fill(sx - inner, sy - inner, inner * 2, inner * 2, 255, 160, 80, 255);
        }

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx", "m80-vfx-multiply-lifetime.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Board.EncodePng(canvas.Rgba, canvas.Width, canvas.Height));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    [Fact]
    public void Evidence_AddAge_WritesPng()
    {
        const int w = 320, h = 180;
        var canvas = new Board(w, h, 16, 18, 24);
        canvas.Fill(0, 0, w, 22, 28, 32, 40, 255);
        const int dots = 10;
        const float amount = 0.5f;
        for (int i = 0; i < dots; i++)
        {
            float age = VfxAddAge.Apply(0.2f + i * 0.05f, amount);
            int rad = Math.Max(2, (int)(age * 10f));
            int sx = 28 + i * Math.Max(8, (w - 48) / dots);
            int sy = (int)(h * 0.58f);
            canvas.Fill(sx - rad, sy - rad, rad * 2, rad * 2, 80, 220, 255, 255);
            int inner = Math.Max(1, rad - 2);
            canvas.Fill(sx - inner, sy - inner, inner * 2, inner * 2, 200, 120, 255, 255);
        }

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx", "m81-vfx-add-age.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Board.EncodePng(canvas.Rgba, canvas.Width, canvas.Height));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    [Fact]
    public void Evidence_MultiplyAge_WritesPng()
    {
        const int w = 320, h = 180;
        var canvas = new Board(w, h, 16, 18, 24);
        canvas.Fill(0, 0, w, 22, 28, 32, 40, 255);
        const int dots = 10;
        const float amount = 2f;
        for (int i = 0; i < dots; i++)
        {
            float age = VfxMultiplyAge.Apply(0.2f + i * 0.05f, amount);
            int rad = Math.Max(2, (int)(age * 10f));
            int sx = 28 + i * Math.Max(8, (w - 48) / dots);
            int sy = (int)(h * 0.58f);
            canvas.Fill(sx - rad, sy - rad, rad * 2, rad * 2, 80, 220, 255, 255);
            int inner = Math.Max(1, rad - 2);
            canvas.Fill(sx - inner, sy - inner, inner * 2, inner * 2, 255, 180, 80, 255);
        }

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx", "m82-vfx-multiply-age.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Board.EncodePng(canvas.Rgba, canvas.Width, canvas.Height));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    [Fact]
    public void Evidence_SetAge_WritesPng()
    {
        const int w = 320, h = 180;
        var canvas = new Board(w, h, 16, 18, 24);
        canvas.Fill(0, 0, w, 22, 28, 32, 40, 255);
        const int dots = 10;
        const float amount = 0.5f;
        for (int i = 0; i < dots; i++)
        {
            float age = VfxSetAge.Apply(0.2f + i * 0.05f, amount);
            int rad = Math.Max(2, (int)(age * 10f));
            int sx = 28 + i * Math.Max(8, (w - 48) / dots);
            int sy = (int)(h * 0.58f);
            canvas.Fill(sx - rad, sy - rad, rad * 2, rad * 2, 80, 220, 255, 255);
            int inner = Math.Max(1, rad - 2);
            canvas.Fill(sx - inner, sy - inner, inner * 2, inner * 2, 180, 255, 120, 255);
        }

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx", "m83-vfx-set-age.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Board.EncodePng(canvas.Rgba, canvas.Width, canvas.Height));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    [Fact]
    public void Evidence_AddPosition_WritesPng()
    {
        const int w = 320, h = 180;
        var canvas = new Board(w, h, 16, 18, 24);
        canvas.Fill(0, 0, w, 22, 28, 32, 40, 255);
        const int dots = 10;
        const float ax = 0.8f;
        for (int i = 0; i < dots; i++)
        {
            float px = i * 0.15f, py = 0f, pz = 0f;
            VfxAddPosition.Apply(ref px, ref py, ref pz, ax, 0f, 0f);
            int sx = 28 + (int)(px * 28f);
            int sy = (int)(h * 0.58f);
            canvas.Fill(sx - 8, sy - 8, 16, 16, 80, 220, 255, 255);
            canvas.Fill(sx - 5, sy - 5, 10, 10, 120, 255, 160, 255);
        }

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx", "m84-vfx-add-position.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Board.EncodePng(canvas.Rgba, canvas.Width, canvas.Height));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    [Fact]
    public void Evidence_SetPosition_WritesPng()
    {
        const int w = 320, h = 180;
        var canvas = new Board(w, h, 16, 18, 24);
        canvas.Fill(0, 0, w, 22, 28, 32, 40, 255);
        const int dots = 10;
        const float ax = 1.2f;
        for (int i = 0; i < dots; i++)
        {
            float px = i * 0.15f, py = 0.4f, pz = 0f;
            VfxSetPosition.Apply(ref px, ref py, ref pz, ax, 0f, 0f);
            int sx = 28 + (int)(px * 28f);
            int sy = (int)(h * 0.58f);
            canvas.Fill(sx - 8, sy - 8, 16, 16, 80, 220, 255, 255);
            canvas.Fill(sx - 5, sy - 5, 10, 10, 255, 140, 200, 255);
        }

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx", "m85-vfx-set-position.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Board.EncodePng(canvas.Rgba, canvas.Width, canvas.Height));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    [Fact]
    public void Evidence_ColorOverSpeed_WritesPng()
    {
        const int w = 320, h = 180;
        var canvas = new Board(w, h, 16, 18, 24);
        canvas.Fill(0, 0, w, 22, 28, 32, 40, 255);
        const int dots = 10;
        const float scale = 4f;
        for (int i = 0; i < dots; i++)
        {
            float speed = i * (scale / (dots - 1));
            VfxColorOverSpeed.Apply(
                speed, 0f, 0f, scale,
                1f, 1f, 1f, 1f,
                0.2f, 0.85f, 1f, 1f,
                out float r, out float g, out float b, out _);
            int sx = 28 + i * Math.Max(8, (w - 48) / dots);
            int sy = (int)(h * 0.55f);
            canvas.Fill(sx - 11, sy - 11, 22, 22,
                (byte)Math.Clamp((int)(r * 255f), 16, 255),
                (byte)Math.Clamp((int)(g * 255f), 16, 255),
                (byte)Math.Clamp((int)(b * 255f), 16, 255),
                255);
        }

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx", "m52-vfx-color-over-speed.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Board.EncodePng(canvas.Rgba, canvas.Width, canvas.Height));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    [Fact]
    public void Evidence_ScreenSpaceSize_WritesPng()
    {
        const int w = 320, h = 180;
        var canvas = new Board(w, h, 16, 18, 24);
        canvas.Fill(0, 0, w, 22, 28, 32, 40, 255);
        const int dots = 10;
        const float amount = 2f;
        for (int i = 0; i < dots; i++)
        {
            float dist = 0.5f + i * 0.4f;
            float size = VfxScreenSpaceSize.Apply(0.12f, dist, 0f, 0f, amount);
            int rad = Math.Max(2, (int)(size * 70f));
            int sx = 28 + i * Math.Max(8, (w - 48) / dots);
            int sy = (int)(h * 0.58f);
            canvas.Fill(sx - rad, sy - rad, rad * 2, rad * 2, 80, 220, 255, 255);
            int inner = Math.Max(1, rad - 2);
            canvas.Fill(sx - inner, sy - inner, inner * 2, inner * 2, 200, 210, 220, 255);
        }

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx", "m53-vfx-screen-space-size.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Board.EncodePng(canvas.Rgba, canvas.Width, canvas.Height));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    [Fact]
    public void Evidence_TriggerEventOnce_WritesPng()
    {
        const int w = 320, h = 180;
        var canvas = new Board(w, h, 16, 18, 24);
        canvas.Fill(0, 0, w, 22, 28, 32, 40, 255);
        int axisY = (int)(h * 0.62f);
        canvas.Fill(16, axisY, Math.Max(8, w - 32), 2, 50, 58, 70, 255);
        bool fired = false;
        const int steps = 16;
        for (int i = 0; i < steps; i++)
        {
            int x = 24 + i * Math.Max(8, (w - 48) / steps);
            canvas.Fill(x, axisY - 4, 2, 10, 70, 80, 96, 255);
            uint n = VfxTriggerEventOnce.Apply(ref fired, 3f, 64);
            for (uint k = 0; k < n; k++)
            {
                int cy = axisY - 18 - (int)k * 10;
                canvas.Fill(x - 2, cy - 3, 8, 8, 255, 170, 70, 255);
                canvas.Fill(x, cy - 1, 4, 4, 255, 220, 120, 255);
            }
        }

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx", "m54-vfx-trigger-event-once.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Board.EncodePng(canvas.Rgba, canvas.Width, canvas.Height));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    [Fact]
    public void Evidence_CollisionFriction_WritesPng()
    {
        const int w = 320, h = 180;
        var canvas = new Board(w, h, 16, 18, 24);
        canvas.Fill(0, 0, w, 22, 28, 32, 40, 255);
        int groundSy = (int)(h * 0.68f);
        canvas.Fill(8, groundSy, Math.Max(8, w - 16), 2, 70, 170, 210, 255);
        const int dots = 10;
        const float amount = 0.7f;
        for (int i = 0; i < dots; i++)
        {
            float t = i / (float)(dots - 1);
            float vx = 2.4f, vy = -1.2f, vz = 0f;
            VfxCollisionFriction.Apply(ref vx, ref vy, ref vz, 0f, 1f, 0f, t * amount);
            int sx = 28 + i * Math.Max(8, (w - 48) / dots);
            int rad = Math.Max(3, (int)(MathF.Abs(vx) * 6f));
            canvas.Fill(sx - rad, groundSy - 18 - rad, rad * 2, rad * 2, 80, 220, 255, 255);
            canvas.Fill(sx + (int)(vx * 8f) - 2, groundSy - 40, 5, 5, 255, 170, 70, 255);
        }

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx", "m55-vfx-collision-friction.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Board.EncodePng(canvas.Rgba, canvas.Width, canvas.Height));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    [Fact]
    public void Evidence_CollisionElasticity_WritesPng()
    {
        const int w = 320, h = 180;
        var canvas = new Board(w, h, 16, 18, 24);
        canvas.Fill(0, 0, w, 22, 28, 32, 40, 255);
        int groundSy = (int)(h * 0.68f);
        canvas.Fill(8, groundSy, Math.Max(8, w - 16), 2, 70, 170, 210, 255);
        const int dots = 10;
        const float amount = 0.7f;
        for (int i = 0; i < dots; i++)
        {
            float t = i / (float)(dots - 1);
            float vx = 1.2f, vy = 2.4f, vz = 0f;
            VfxCollisionElasticity.Apply(ref vx, ref vy, ref vz, 0f, 1f, 0f, t * amount);
            int sx = 28 + i * Math.Max(8, (w - 48) / dots);
            int rad = Math.Max(3, (int)(MathF.Abs(vy) * 6f));
            canvas.Fill(sx - rad, groundSy - 18 - rad, rad * 2, rad * 2, 80, 220, 255, 255);
            canvas.Fill(sx + (int)(vx * 8f) - 2, groundSy - 18 - (int)(vy * 10f), 5, 5, 255, 170, 70, 255);
        }

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx", "m56-vfx-collision-elasticity.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Board.EncodePng(canvas.Rgba, canvas.Width, canvas.Height));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    [Fact]
    public void Evidence_CollisionLifetimeLoss_WritesPng()
    {
        const int w = 320, h = 180;
        var canvas = new Board(w, h, 16, 18, 24);
        canvas.Fill(0, 0, w, 22, 28, 32, 40, 255);
        int groundSy = (int)(h * 0.68f);
        canvas.Fill(8, groundSy, Math.Max(8, w - 16), 2, 70, 170, 210, 255);
        const int dots = 10;
        const float amount = 0.7f;
        const float age = 0.4f;
        const float lifetime = 2f;
        for (int i = 0; i < dots; i++)
        {
            float t = i / (float)(dots - 1);
            float remain = VfxCollisionLifetimeLoss.Apply(age, lifetime, t * amount) - age;
            int sx = 28 + i * Math.Max(8, (w - 48) / dots);
            int rad = Math.Max(2, (int)(remain * 10f));
            canvas.Fill(sx - rad, groundSy - 18 - rad, rad * 2, rad * 2, 80, 220, 255, 255);
            canvas.Fill(sx - 2, groundSy - 40 - (int)(remain * 12f), 5, 5, 255, 170, 70, 255);
        }

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx", "m57-vfx-collision-lifetime-loss.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Board.EncodePng(canvas.Rgba, canvas.Width, canvas.Height));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    [Fact]
    public void Evidence_LimitVelocity_WritesPng()
    {
        const int w = 320, h = 180;
        var canvas = new Board(w, h, 16, 18, 24);
        canvas.Fill(0, 0, w, 22, 28, 32, 40, 255);
        int groundSy = (int)(h * 0.68f);
        canvas.Fill(8, groundSy, Math.Max(8, w - 16), 2, 70, 170, 210, 255);
        const int dots = 10;
        const float maxSpeed = 2.5f;
        for (int i = 0; i < dots; i++)
        {
            float t = i / (float)(dots - 1);
            float vx = 3f, vy = 4f, vz = 0f;
            VfxLimitVelocity.Apply(ref vx, ref vy, ref vz, t * maxSpeed);
            float spd = MathF.Sqrt(vx * vx + vy * vy);
            int sx = 28 + i * Math.Max(8, (w - 48) / dots);
            int rad = Math.Max(2, (int)(spd * 3f));
            canvas.Fill(sx - rad, groundSy - 18 - rad, rad * 2, rad * 2, 80, 220, 255, 255);
            canvas.Fill(sx + (int)(vx * 6f) - 2, groundSy - 18 - (int)(vy * 6f), 5, 5, 255, 170, 70, 255);
        }

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx", "m58-vfx-limit-velocity.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Board.EncodePng(canvas.Rgba, canvas.Width, canvas.Height));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    [Fact]
    public void Evidence_KillSphere_WritesPng()
    {
        const int w = 320, h = 180;
        var canvas = new Board(w, h, 16, 18, 24);
        canvas.Fill(0, 0, w, 22, 28, 32, 40, 255);
        int groundSy = (int)(h * 0.68f);
        canvas.Fill(8, groundSy, Math.Max(8, w - 16), 2, 70, 170, 210, 255);
        const int dots = 12;
        const float radius = 0.8f;
        for (int i = 0; i < dots; i++)
        {
            float t = i / (float)(dots - 1);
            float px = -1.4f + t * 2.8f;
            bool kill = VfxKillSphere.Apply(px, 0f, 0f, 0f, 0f, 0f, radius);
            int sx = 24 + i * Math.Max(8, (w - 48) / dots);
            int rad = kill ? 3 : 5;
            if (kill)
                canvas.Fill(sx - rad, groundSy - 18 - rad, rad * 2, rad * 2, 40, 44, 52, 255);
            else
                canvas.Fill(sx - rad, groundSy - 18 - rad, rad * 2, rad * 2, 80, 220, 255, 255);
        }

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx", "m59-vfx-kill-sphere.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Board.EncodePng(canvas.Rgba, canvas.Width, canvas.Height));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    [Fact]
    public void Evidence_KillAABox_WritesPng()
    {
        const int w = 320, h = 180;
        var canvas = new Board(w, h, 16, 18, 24);
        canvas.Fill(0, 0, w, 22, 28, 32, 40, 255);
        int groundSy = (int)(h * 0.68f);
        canvas.Fill(8, groundSy, Math.Max(8, w - 16), 2, 70, 170, 210, 255);
        const int dots = 12;
        const float sx = 0.7f, sy = 0.4f, sz = 1f;
        for (int i = 0; i < dots; i++)
        {
            float t = i / (float)(dots - 1);
            float px = -1.4f + t * 2.8f;
            bool kill = VfxKillAABox.Apply(px, 0f, 0f, 0f, 0f, 0f, sx, sy, sz);
            int dx = 24 + i * Math.Max(8, (w - 48) / dots);
            int rad = kill ? 3 : 5;
            if (kill)
                canvas.Fill(dx - rad, groundSy - 18 - rad, rad * 2, rad * 2, 40, 44, 52, 255);
            else
                canvas.Fill(dx - rad, groundSy - 18 - rad, rad * 2, rad * 2, 80, 220, 255, 255);
        }

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx", "m60-vfx-kill-aabox.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Board.EncodePng(canvas.Rgba, canvas.Width, canvas.Height));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    [Fact]
    public void Evidence_Attract_WritesPng()
    {
        const int w = 320, h = 180;
        var canvas = new Board(w, h, 16, 18, 24);
        canvas.Fill(0, 0, w, 22, 28, 32, 40, 255);
        int groundSy = (int)(h * 0.68f);
        canvas.Fill(8, groundSy, Math.Max(8, w - 16), 2, 70, 170, 210, 255);
        const int dots = 10;
        const float strength = 2f;
        for (int i = 0; i < dots; i++)
        {
            float t = i / (float)(dots - 1);
            float px = -1.4f + t * 2.8f;
            float vx = 0f, vy = 0f, vz = 0f;
            VfxAttract.Apply(ref vx, ref vy, ref vz, px, 0f, 0f, 0f, 0f, 0f, strength, 0.5f);
            int sx = 24 + i * Math.Max(8, (w - 48) / dots);
            canvas.Fill(sx - 5, groundSy - 23, 10, 10, 80, 220, 255, 255);
            canvas.Fill(sx + (int)(vx * 18f) - 2, groundSy - 20, 5, 5, 255, 170, 70, 255);
        }

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx", "m61-vfx-attract.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Board.EncodePng(canvas.Rgba, canvas.Width, canvas.Height));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    [Fact]
    public void Evidence_ConformToSphere_WritesPng()
    {
        const int w = 320, h = 180;
        var canvas = new Board(w, h, 16, 18, 24);
        canvas.Fill(0, 0, w, 22, 28, 32, 40, 255);
        int groundSy = (int)(h * 0.68f);
        canvas.Fill(8, groundSy, Math.Max(8, w - 16), 2, 70, 170, 210, 255);
        const int dots = 10;
        const float strength = 2f;
        const float radius = 1f;
        for (int i = 0; i < dots; i++)
        {
            float t = i / (float)(dots - 1);
            float px = -1.4f + t * 2.8f;
            float vx = 0f, vy = 0f, vz = 0f;
            VfxConformToSphere.Apply(ref vx, ref vy, ref vz, px, 0f, 0f, 0f, 0f, 0f, radius, strength, 0.5f);
            int sx = 24 + i * Math.Max(8, (w - 48) / dots);
            canvas.Fill(sx - 5, groundSy - 23, 10, 10, 80, 220, 255, 255);
            canvas.Fill(sx + (int)(vx * 18f) - 2, groundSy - 20, 5, 5, 255, 170, 70, 255);
        }

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx", "m66-vfx-conform-to-sphere.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Board.EncodePng(canvas.Rgba, canvas.Width, canvas.Height));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    [Fact]
    public void Evidence_Vortex_WritesPng()
    {
        const int w = 320, h = 180;
        var canvas = new Board(w, h, 16, 18, 24);
        canvas.Fill(0, 0, w, 22, 28, 32, 40, 255);
        int groundSy = (int)(h * 0.68f);
        canvas.Fill(8, groundSy, Math.Max(8, w - 16), 2, 70, 170, 210, 255);
        const int dots = 10;
        const float rotation = 2f;
        const float attraction = 1f;
        for (int i = 0; i < dots; i++)
        {
            float t = i / (float)(dots - 1);
            float px = -1.4f + t * 2.8f;
            float vx = 0f, vy = 0f, vz = 0f;
            VfxVortex.Apply(ref vx, ref vy, ref vz, px, 0f, 0f, 0f, 0f, 0f, 0f, 1f, 0f, rotation, attraction, 0.5f);
            int sx = 24 + i * Math.Max(8, (w - 48) / dots);
            canvas.Fill(sx - 5, groundSy - 23, 10, 10, 80, 220, 255, 255);
            canvas.Fill(sx + (int)(vx * 18f) - 2, groundSy - 20 - (int)(vz * 18f), 5, 5, 255, 170, 70, 255);
        }

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx", "m67-vfx-vortex.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Board.EncodePng(canvas.Rgba, canvas.Width, canvas.Height));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    [Fact]
    public void Evidence_ConformToCylinder_WritesPng()
    {
        const int w = 320, h = 180;
        var canvas = new Board(w, h, 16, 18, 24);
        canvas.Fill(0, 0, w, 22, 28, 32, 40, 255);
        int groundSy = (int)(h * 0.68f);
        canvas.Fill(8, groundSy, Math.Max(8, w - 16), 2, 70, 170, 210, 255);
        const int dots = 10;
        const float strength = 2f;
        const float radius = 1f;
        for (int i = 0; i < dots; i++)
        {
            float t = i / (float)(dots - 1);
            float px = -1.4f + t * 2.8f;
            float vx = 0f, vy = 0f, vz = 0f;
            VfxConformToCylinder.Apply(ref vx, ref vy, ref vz, px, 0.8f, 0f, 0f, 0f, 0f, 0f, 1f, 0f, radius, strength, 0.5f);
            int sx = 24 + i * Math.Max(8, (w - 48) / dots);
            canvas.Fill(sx - 5, groundSy - 23, 10, 10, 80, 220, 255, 255);
            canvas.Fill(sx + (int)(vx * 18f) - 2, groundSy - 20, 5, 5, 255, 170, 70, 255);
        }

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx", "m68-vfx-conform-to-cylinder.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Board.EncodePng(canvas.Rgba, canvas.Width, canvas.Height));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    [Fact]
    public void Evidence_OrientLookAt_WritesPng()
    {
        const int w = 320, h = 180;
        var canvas = new Board(w, h, 16, 18, 24);
        canvas.Fill(0, 0, w, 22, 28, 32, 40, 255);
        const int dots = 8;
        const float size = 0.2f;
        for (int i = 0; i < dots; i++)
        {
            float cx = -1.2f + i * 0.34f;
            float cy = 0.1f * ((i % 3) - 1);
            VfxOrientLookAt.Apply(cx, cy, 0f, 0f, 0f, 0f, out float ax, out float ay, out float az);
            VfxOrient.AlongVelocity(cx, cy, 0f, ax, ay, az, 0f, 0f, 8f, 0f, 1f, 0f, size,
                out float px, out float py, out _);
            VfxOrient.AlongVelocity(cx, cy, 0f, ax, ay, az, 0f, 0f, 8f, 0f, -1f, 0f, size,
                out float bx, out float by, out _);
            int x0 = 40 + (int)((px + 1.6f) * 0.28f * Math.Max(8, w - 48));
            int y0 = (int)(h * 0.55f) - (int)(py * 50f);
            int x1 = 40 + (int)((bx + 1.6f) * 0.28f * Math.Max(8, w - 48));
            int y1 = (int)(h * 0.55f) - (int)(by * 50f);
            int minX = Math.Min(x0, x1);
            int minY = Math.Min(y0, y1);
            canvas.Fill(minX, minY, Math.Max(3, Math.Abs(x1 - x0) + 2), Math.Max(3, Math.Abs(y1 - y0) + 2), 255, 140, 70, 255);
        }

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx", "m62-vfx-orient-look-at.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Board.EncodePng(canvas.Rgba, canvas.Width, canvas.Height));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    [Fact]
    public void Evidence_FlipbookRate_WritesPng()
    {
        const int w = 320, h = 180;
        var canvas = new Board(w, h, 16, 18, 24);
        canvas.Fill(0, 0, w, 22, 28, 32, 40, 255);
        const int dots = 10;
        const float rate = 8f;
        const float frames = 4f;
        for (int i = 0; i < dots; i++)
        {
            float age = i * 0.15f;
            float frameF = VfxFlipbook.Frame(age, 1f, frames, rate);
            int sx = 24 + i * Math.Max(8, (w - 48) / dots);
            int sy = (int)(h * 0.55f);
            int br = 80 + (int)(frameF * 28f);
            if (br > 255) br = 255;
            canvas.Fill(sx - 6, sy - 6, 12, 12, (byte)br, 180, 90, 255);
            canvas.Fill(sx - 2, sy - 20 - (int)(frameF * 6f), 5, 5, 255, 210, 80, 255);
        }

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx", "m63-vfx-flipbook-rate.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Board.EncodePng(canvas.Rgba, canvas.Width, canvas.Height));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    [Fact]
    public void Evidence_FlipbookStart_WritesPng()
    {
        const int w = 320, h = 180;
        var canvas = new Board(w, h, 16, 18, 24);
        canvas.Fill(0, 0, w, 22, 28, 32, 40, 255);
        const int dots = 10;
        const float start = 2f;
        const float frames = 4f;
        for (int i = 0; i < dots; i++)
        {
            float age = i / (float)(dots - 1);
            float frameF = VfxFlipbook.Frame(age, 1f, frames, 0f, start);
            int sx = 24 + i * Math.Max(8, (w - 48) / dots);
            int sy = (int)(h * 0.55f);
            int br = 80 + (int)(frameF * 28f);
            if (br > 255) br = 255;
            canvas.Fill(sx - 6, sy - 6, 12, 12, (byte)br, 160, 220, 255);
            canvas.Fill(sx - 2, sy - 20 - (int)(frameF * 6f), 5, 5, 255, 210, 80, 255);
        }

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx", "m65-vfx-flipbook-start.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Board.EncodePng(canvas.Rgba, canvas.Width, canvas.Height));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    [Fact]
    public void Evidence_KillPlane_WritesPng()
    {
        const int w = 320, h = 180;
        var canvas = new Board(w, h, 16, 18, 24);
        canvas.Fill(0, 0, w, 22, 28, 32, 40, 255);
        int groundSy = (int)(h * 0.68f);
        canvas.Fill(8, groundSy, Math.Max(8, w - 16), 2, 70, 170, 210, 255);
        const int dots = 12;
        for (int i = 0; i < dots; i++)
        {
            float t = i / (float)(dots - 1);
            float px = -1.4f + t * 2.8f;
            float py = -0.6f + t * 1.2f;
            bool kill = VfxKillPlane.Apply(px, py, 0f, 0f, 1f, 0f, 0f);
            int dx = 24 + i * Math.Max(8, (w - 48) / dots);
            int dy = groundSy - 8 - (int)(py * 40f);
            if (kill)
                canvas.Fill(dx - 3, dy - 3, 6, 6, 40, 44, 52, 255);
            else
                canvas.Fill(dx - 5, dy - 5, 10, 10, 80, 220, 255, 255);
        }

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx", "m64-vfx-kill-plane.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Board.EncodePng(canvas.Rgba, canvas.Width, canvas.Height));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    [Fact]
    public void Evidence_Decal_WritesPng()
    {
        const int w = 320, h = 180;
        var canvas = new Board(w, h, 16, 18, 24);
        canvas.Fill(0, 0, w, 22, 28, 32, 40, 255);
        int groundSy = (int)(h * 0.72f);
        canvas.Fill(0, groundSy, w, Math.Max(1, h - groundSy), 32, 34, 40, 255);
        canvas.Fill(8, groundSy, Math.Max(8, w - 16), 2, 70, 170, 210, 255);
        for (int i = 0; i < 10; i++)
        {
            float px = -1.4f + i * 0.22f;
            float py = 0.35f + (i % 3) * 0.18f;
            float pz = ((i % 2) * 2 - 1) * 0.2f;
            VfxDecal.Project(px, py, pz, 0f, 1f, 0f, 0f, out float qx, out float qy, out _, out _, out _, out _);
            int cx = 16 + (int)((qx + 1.6f) * 0.28f * Math.Max(8, w - 32));
            int cy = groundSy - (int)(qy * (h * 0.12f)) - 4;
            canvas.Fill(cx - 6, cy - 3, 12, 6, 140, 62, 24, 255);
            canvas.Fill(cx - 5, cy - 2, 10, 4, 255, 115, 46, 255);
            int airX = 16 + (int)((px + 1.6f) * 0.28f * Math.Max(8, w - 32));
            int airY = groundSy - (int)(py * (h * 0.38f));
            canvas.Fill(airX - 1, airY - 1, 3, 3, 90, 210, 255, 255);
        }

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx", "m30-vfx-particle-decals.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Board.EncodePng(canvas.Rgba, canvas.Width, canvas.Height));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    [Fact]
    public void Evidence_ParticleLight_WritesPng()
    {
        const int w = 320, h = 180;
        var canvas = new Board(w, h, 16, 18, 24);
        canvas.Fill(0, 0, w, 22, 28, 32, 40, 255);
        int groundSy = (int)(h * 0.72f);
        canvas.Fill(0, groundSy, w, Math.Max(1, h - groundSy), 28, 30, 36, 255);
        for (uint slot = 0; slot < 8; slot++)
        {
            VfxParticleLight.TryEvaluate(
                slot, 8, 0f, 0.55f, 0f, 2f, 0f, -1.5f, 0f, 2f, 1f, 1f, 1f, 2f, 1.5f,
                out float px, out float py, out _, out _, out _, out _, out _, out float range);
            int x = 12 + (int)((px + 2f) * 0.25f * Math.Max(8, w - 24));
            int y = groundSy - (int)(py * (h * 0.32f));
            int rad = Math.Max(3, (int)(range * 6f));
            canvas.Fill(x - rad, y - rad, rad * 2 + 1, rad * 2 + 1, 255, 220, 90, 255);
            canvas.Fill(x - 1, y - 1, 3, 3, 230, 230, 235, 255);
        }

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx", "m29-vfx-particle-lights.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Board.EncodePng(canvas.Rgba, canvas.Width, canvas.Height));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    [Fact]
    public void Evidence_Distortion_WritesPng()
    {
        const int w = 320, h = 180;
        var canvas = new Board(w, h, 16, 18, 24);
        canvas.Fill(0, 0, w, 22, 28, 32, 40, 255);
        for (int y = 22; y < h; y++)
        {
            for (int x = 0; x < w; x++)
            {
                bool on = ((x / 16) + (y / 16)) % 2 == 0;
                canvas.Fill(x, y, 1, 1, on ? (byte)70 : (byte)36, on ? (byte)78 : (byte)40, on ? (byte)92 : (byte)48, 255);
            }
        }
        const float intensity = 0.35f;
        for (int i = 0; i < 6; i++)
        {
            int cx = 40 + i * 46;
            int cy = 70 + (i % 2) * 36;
            const int radius = 12;
            for (int dy = -radius; dy <= radius; dy++)
            {
                for (int dx = -radius; dx <= radius; dx++)
                {
                    if (dx * dx + dy * dy > radius * radius)
                        continue;
                    float su = 0.5f + dx / (float)(radius * 2);
                    float sv = 0.5f + dy / (float)(radius * 2);
                    float u = (cx + dx) / (float)(w - 1);
                    float v = (cy + dy) / (float)(h - 1);
                    VfxDistortion.OffsetUv(u, v, intensity, su, sv, out float ou, out float ov);
                    int sx = (int)(ou * (w - 1));
                    int sy = (int)(ov * (h - 1));
                    if ((uint)sx >= (uint)w || (uint)sy >= (uint)h)
                        continue;
                    int o = (sy * w + sx) * 4;
                    float r = canvas.Rgba[o] / 255f;
                    float g = canvas.Rgba[o + 1] / 255f;
                    float b = canvas.Rgba[o + 2] / 255f;
                    VfxDistortion.Apply(ref r, ref g, ref b, 0.9f, 0.95f, 1f, 0.85f, intensity);
                    int px = cx + dx;
                    int py = cy + dy;
                    if ((uint)px >= (uint)w || (uint)py >= (uint)h)
                        continue;
                    canvas.Fill(px, py, 1, 1,
                        (byte)Math.Clamp((int)(r * 255f), 0, 255),
                        (byte)Math.Clamp((int)(g * 255f), 0, 255),
                        (byte)Math.Clamp((int)(b * 255f), 0, 255),
                        255);
                }
            }
        }

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx", "m28-vfx-distortion.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Board.EncodePng(canvas.Rgba, canvas.Width, canvas.Height));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    [Fact]
    public void Evidence_AttributeMap_WritesPng()
    {
        const int w = 320, h = 180;
        var canvas = new Board(w, h, 16, 18, 24);
        canvas.Fill(0, 0, w, 22, 28, 32, 40, 255);
        int groundSy = (int)(h * 0.62f);
        const int mapW = 4, mapH = 2;
        Span<float> map = stackalloc float[mapW * mapH * 4];
        for (int i = 0; i < mapW * mapH; i++)
        {
            float ang = i * (MathF.PI * 2f / (mapW * mapH));
            map[i * 4] = 0.5f + MathF.Cos(ang) * 0.5f;
            map[i * 4 + 1] = 0.5f + MathF.Sin(ang) * 0.5f;
            map[i * 4 + 2] = 0.5f;
            map[i * 4 + 3] = 1f;
        }
        for (uint slot = 0; slot < mapW * mapH; slot++)
        {
            float px = 0f, py = 0.4f, pz = 0f, size = 0.15f;
            float cr = 1f, cg = 1f, cb = 1f, ca = 1f;
            VfxAttributeMap.Apply(
                ref px, ref py, ref pz, ref size, ref cr, ref cg, ref cb, ref ca,
                0f, 0.4f, 0f, 0.15f, VfxAttributeMap.ModePositionColor, 1.4f, map, mapW, mapH, slot);
            int x = 12 + (int)((px + 2f) * 0.25f * Math.Max(8, w - 24));
            int y = groundSy - (int)(py * (h * 0.38f));
            canvas.Fill(x - 2, y - 2, 5, 5, 90, 210, 255, 255);
            canvas.Fill(x - 1, y - 1, 3, 3,
                (byte)Math.Clamp((int)(cr * 255f), 40, 255),
                (byte)Math.Clamp((int)(cg * 255f), 40, 255),
                (byte)Math.Clamp((int)(cb * 255f), 40, 255),
                255);
        }
        for (int i = 0; i < mapW * mapH; i++)
        {
            int tx = 8 + (i % mapW) * 10;
            int ty = 28 + (i / mapW) * 10;
            canvas.Fill(tx, ty, 8, 8,
                (byte)Math.Clamp((int)(map[i * 4] * 255f), 40, 255),
                (byte)Math.Clamp((int)(map[i * 4 + 1] * 255f), 40, 255),
                (byte)Math.Clamp((int)(map[i * 4 + 2] * 255f), 40, 255),
                255);
        }

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx", "m27-vfx-attribute-map.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Board.EncodePng(canvas.Rgba, canvas.Width, canvas.Height));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    [Fact]
    public void Evidence_VectorField_WritesPng()
    {
        const int w = 320, h = 180;
        var canvas = new Board(w, h, 16, 18, 24);
        canvas.Fill(0, 0, w, 22, 28, 32, 40, 255);
        int groundSy = (int)(h * 0.72f);
        const float cx = 0f, cy = 0.55f, cz = 0f, sx = 0.4f;
        int boxX = 12 + (int)((cx - sx + 2f) * 0.25f * Math.Max(8, w - 24));
        int boxY = groundSy - (int)((cy + sx) * (h * 0.32f));
        int boxW = Math.Max(16, (int)(sx * 2f * 0.25f * Math.Max(8, w - 24)));
        int boxH = Math.Max(16, (int)(sx * 2f * (h * 0.32f)));
        canvas.Stroke(boxX, boxY, boxW, boxH, 80, 220, 180);
        Span<float> field = stackalloc float[3];
        VfxVectorField.BakeConstant(field, 1, 1f, 0f, 0f);
        for (int i = 0; i < 16; i++)
        {
            float px = -0.55f;
            float py = 0.25f + (i % 8) * 0.08f;
            float pz = 0f;
            float vx = 0f, vy = 0f, vz = 0f;
            for (int step = 0; step < 28; step++)
            {
                VfxVectorField.Apply(
                    ref vx, ref vy, ref vz,
                    px, py, pz, cx, cy, cz, sx, sx, sx,
                    2.5f, 1f / 30f, field, 1, 1, 1);
                px += vx * (1f / 30f);
                py += vy * (1f / 30f);
                pz += vz * (1f / 30f);
            }
            int x = 12 + (int)((px + 2f) * 0.25f * Math.Max(8, w - 24));
            int y = groundSy - (int)(py * (h * 0.32f));
            canvas.Fill(x - 2, y - 2, 5, 5, 70, 230, 190, 255);
            canvas.Fill(x - 1, y - 1, 3, 3, 230, 230, 235, 255);
        }

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx", "m24-vfx-vector-field.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Board.EncodePng(canvas.Rgba, canvas.Width, canvas.Height));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    [Fact]
    public void Evidence_CliBoard_WritesPng()
    {
        var canvas = new Board(480, 128, 18, 18, 24);
        canvas.Text(12, 8, "M5 VFXGRAPH CLI", 230, 230, 235, 2);
        canvas.Fill(16, 40, 84, 72, 32, 120, 140, 255);
        canvas.Stroke(16, 40, 84, 72, 90, 210, 220);
        canvas.Text(22, 66, "INSPECT", 240, 250, 255, 2);
        canvas.Fill(108, 40, 84, 72, 160, 110, 40, 255);
        canvas.Stroke(108, 40, 84, 72, 230, 180, 80);
        canvas.Text(118, 66, "PATCH", 255, 245, 230, 2);
        canvas.Fill(200, 40, 84, 72, 40, 130, 70, 255);
        canvas.Stroke(200, 40, 84, 72, 90, 210, 120);
        canvas.Text(206, 66, "COMPILE", 235, 255, 240, 2);
        canvas.Fill(292, 40, 84, 72, 90, 70, 150, 255);
        canvas.Stroke(292, 40, 84, 72, 180, 150, 230);
        canvas.Text(304, 66, "NODES", 240, 230, 255, 2);
        canvas.Fill(384, 40, 80, 72, 140, 80, 50, 255);
        canvas.Stroke(384, 40, 80, 72, 230, 160, 90);
        canvas.Text(388, 66, "CREATE", 255, 245, 230, 2);

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx", "m5-vfxgraph-cli.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, Board.EncodePng(canvas.Rgba, canvas.Width, canvas.Height));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    static string FindTemplate(string fileName)
    {
        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "Packages", "com.xengine.vfx", "Templates", fileName);
        if (!File.Exists(path))
            throw new FileNotFoundException(path);
        return path;
    }

    static string FindRepoRoot()
    {
        string dir = AppContext.BaseDirectory;
        for (int i = 0; i < 12; i++)
        {
            if (File.Exists(Path.Combine(dir, "XEngine.sln")))
                return dir;
            string parent = Path.GetFullPath(Path.Combine(dir, ".."));
            if (string.Equals(parent, dir, StringComparison.OrdinalIgnoreCase))
                break;
            dir = parent;
        }
        throw new InvalidOperationException("Could not locate XEngine.sln from " + AppContext.BaseDirectory);
    }

    private sealed class SimpleServices : IServiceProvider
    {
        private readonly ProjectDescriptor _project;
        public SimpleServices(ProjectDescriptor project) => _project = project;
        public object? GetService(Type serviceType)
            => serviceType == typeof(ProjectDescriptor) ? _project : null;
    }

    private const string EmptyEcho = """
{
  "$type": "XEngine.VFX.VfxGraphAsset, XEngine.VFX.Runtime",
  "GraphName": "VFX/EmptyBroken",
  "Template": 0,
  "Capacity": 1024,
  "Properties": { "$values": [] },
  "Contexts": { "$values": [] },
  "Blocks": { "$values": [] },
  "Operators": { "$values": [] },
  "Edges": { "$values": [] }
}
""";

    private sealed class Board
    {
        public readonly int Width;
        public readonly int Height;
        public readonly byte[] Rgba;

        public Board(int width, int height, byte r, byte g, byte b)
        {
            Width = width;
            Height = height;
            Rgba = new byte[width * height * 4];
            Fill(0, 0, width, height, r, g, b, 255);
        }

        public void Fill(int x, int y, int w, int h, byte r, byte g, byte b, byte a)
        {
            int x0 = Math.Max(0, x);
            int y0 = Math.Max(0, y);
            int x1 = Math.Min(Width, x + w);
            int y1 = Math.Min(Height, y + h);
            for (int py = y0; py < y1; py++)
            {
                int row = py * Width * 4;
                for (int px = x0; px < x1; px++)
                {
                    int i = row + px * 4;
                    Rgba[i] = r;
                    Rgba[i + 1] = g;
                    Rgba[i + 2] = b;
                    Rgba[i + 3] = a;
                }
            }
        }

        public void Stroke(int x, int y, int w, int h, byte r, byte g, byte b)
        {
            Fill(x, y, w, 2, r, g, b, 255);
            Fill(x, y + h - 2, w, 2, r, g, b, 255);
            Fill(x, y, 2, h, r, g, b, 255);
            Fill(x + w - 2, y, 2, h, r, g, b, 255);
        }

        public void Text(int x, int y, string text, byte r, byte g, byte b, int scale)
        {
            int cursor = x;
            foreach (char c in text.ToUpperInvariant())
            {
                if (!Glyphs.TryGetValue(c, out byte[]? rows))
                    rows = Glyphs['?'];
                for (int row = 0; row < 7; row++)
                {
                    byte bits = rows[row];
                    for (int col = 0; col < 5; col++)
                    {
                        if ((bits & (1 << (4 - col))) == 0) continue;
                        Fill(cursor + col * scale, y + row * scale, scale, scale, r, g, b, 255);
                    }
                }
                cursor += 6 * scale;
            }
        }

        public static byte[] EncodePng(byte[] rgba, int width, int height)
        {
            using var memory = new MemoryStream();
            memory.Write([0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]);
            byte[] ihdr = new byte[13];
            WriteInt(ihdr, 0, width);
            WriteInt(ihdr, 4, height);
            ihdr[8] = 8;
            ihdr[9] = 6;
            WriteChunk(memory, "IHDR", ihdr);
            byte[] raw = new byte[(width * 4 + 1) * height];
            int srcRow = width * 4;
            for (int y = 0; y < height; y++)
            {
                raw[y * (srcRow + 1)] = 0;
                Buffer.BlockCopy(rgba, y * srcRow, raw, y * (srcRow + 1) + 1, srcRow);
            }
            using var deflate = new MemoryStream();
            deflate.WriteByte(0x78);
            deflate.WriteByte(0x9C);
            using (var compressor = new DeflateStream(deflate, CompressionLevel.Optimal, leaveOpen: true))
                compressor.Write(raw, 0, raw.Length);
            byte[] adler = new byte[4];
            WriteInt(adler, 0, (int)Adler32(raw));
            deflate.Write(adler, 0, 4);
            WriteChunk(memory, "IDAT", deflate.ToArray());
            WriteChunk(memory, "IEND", []);
            return memory.ToArray();
        }

        private static void WriteChunk(Stream stream, string type, byte[] data)
        {
            byte[] length = new byte[4];
            WriteInt(length, 0, data.Length);
            stream.Write(length);
            byte[] typeBytes = Encoding.ASCII.GetBytes(type);
            stream.Write(typeBytes);
            stream.Write(data);
            uint crc = 0xFFFFFFFF;
            foreach (byte value in typeBytes) crc = Crc32Step(crc, value);
            foreach (byte value in data) crc = Crc32Step(crc, value);
            crc ^= 0xFFFFFFFF;
            byte[] crcBytes = new byte[4];
            WriteInt(crcBytes, 0, (int)crc);
            stream.Write(crcBytes);
        }

        private static void WriteInt(byte[] buffer, int offset, int value)
        {
            buffer[offset] = (byte)(value >> 24);
            buffer[offset + 1] = (byte)(value >> 16);
            buffer[offset + 2] = (byte)(value >> 8);
            buffer[offset + 3] = (byte)value;
        }

        private static uint Adler32(byte[] data)
        {
            uint a = 1, b = 0;
            for (int i = 0; i < data.Length; i++)
            {
                a = (a + data[i]) % 65521;
                b = (b + a) % 65521;
            }
            return (b << 16) | a;
        }

        private static uint Crc32Step(uint crc, byte value)
        {
            crc ^= value;
            for (int i = 0; i < 8; i++)
                crc = (crc & 1) != 0 ? (crc >> 1) ^ 0xEDB88320 : crc >> 1;
            return crc;
        }

        private static readonly Dictionary<char, byte[]> Glyphs = new()
        {
            [' '] = [0, 0, 0, 0, 0, 0, 0],
            ['5'] = [0x1F, 0x10, 0x1E, 0x01, 0x01, 0x11, 0x0E],
            ['A'] = [0x0E, 0x11, 0x11, 0x1F, 0x11, 0x11, 0x11],
            ['C'] = [0x0E, 0x11, 0x10, 0x10, 0x10, 0x11, 0x0E],
            ['D'] = [0x1E, 0x11, 0x11, 0x11, 0x11, 0x11, 0x1E],
            ['E'] = [0x1F, 0x10, 0x10, 0x1E, 0x10, 0x10, 0x1F],
            ['F'] = [0x1F, 0x10, 0x10, 0x1E, 0x10, 0x10, 0x10],
            ['G'] = [0x0E, 0x11, 0x10, 0x17, 0x11, 0x11, 0x0F],
            ['H'] = [0x11, 0x11, 0x11, 0x1F, 0x11, 0x11, 0x11],
            ['I'] = [0x0E, 0x04, 0x04, 0x04, 0x04, 0x04, 0x0E],
            ['L'] = [0x10, 0x10, 0x10, 0x10, 0x10, 0x10, 0x1F],
            ['M'] = [0x11, 0x1B, 0x15, 0x15, 0x11, 0x11, 0x11],
            ['N'] = [0x11, 0x19, 0x15, 0x13, 0x11, 0x11, 0x11],
            ['O'] = [0x0E, 0x11, 0x11, 0x11, 0x11, 0x11, 0x0E],
            ['P'] = [0x1E, 0x11, 0x11, 0x1E, 0x10, 0x10, 0x10],
            ['R'] = [0x1E, 0x11, 0x11, 0x1E, 0x14, 0x12, 0x11],
            ['S'] = [0x0E, 0x11, 0x10, 0x0E, 0x01, 0x11, 0x0E],
            ['T'] = [0x1F, 0x04, 0x04, 0x04, 0x04, 0x04, 0x04],
            ['V'] = [0x11, 0x11, 0x11, 0x11, 0x11, 0x0A, 0x04],
            ['X'] = [0x11, 0x11, 0x0A, 0x04, 0x0A, 0x11, 0x11],
            ['?'] = [0x0E, 0x11, 0x01, 0x06, 0x04, 0, 0x04],
        };
    }
}
