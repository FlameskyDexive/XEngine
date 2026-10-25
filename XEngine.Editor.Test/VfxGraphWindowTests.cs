using System.IO;
using System.IO.Compression;
using System.Text;

using XEngine.OrigamiUI;
using XEngine.PaperUI;
using XEngine.Quill;
using XEngine.Runtime;
using XEngine.VFX;
using XEngine.VFX.Editor;
using XEngine.Vector;

using Xunit;

namespace XEngine.Editor.Test;

public sealed class VfxGraphWindowTests
{
    [Fact]
    public void SetGraph_BuildsContextsBlocksOperatorsAndWires()
    {
        var window = new VfxGraphWindow();
        window.SetGraph(VfxGraphTemplates.CreateLoop());
        Assert.True(window.GraphNodes.Count >= 4);
        Assert.Contains(window.GraphNodes, n => n.Id == "ctx_spawn");
        Assert.Contains(window.GraphNodes, n => n.Id == "b_rate");
        Assert.Contains(window.GraphNodes, n => n.Id == "n_color");
        Assert.Contains(window.Wires, w => w.FromNode == "ctx_spawn" && w.ToNode == "ctx_init");
        Assert.Contains(window.Wires, w => w.FromNode == "n_color" && w.ToNode == "b_color");
        Assert.NotNull(window.PreviewSettings);
        Assert.Equal(64f, window.PreviewSettings!.SpawnRate);
        Assert.NotNull(window.Graph!.FindProperty(VfxExposedNames.SpawnRate));
    }

    [Fact]
    public void ApplyNodePosition_WritesContextCoordinates()
    {
        VfxGraphAsset graph = VfxGraphTemplates.CreateLoop();
        var window = new VfxGraphWindow();
        window.SetGraph(graph);
        window.ApplyNodePosition("ctx_spawn", 50f, 60f);
        Assert.Equal(50f, graph.Contexts[0].PosX);
        Assert.Equal(60f, graph.Contexts[0].PosY);
    }

    [Fact]
    public void TryConnect_ReplacesIncomingEdge()
    {
        VfxGraphAsset graph = VfxGraphTemplates.CreateLoop();
        var window = new VfxGraphWindow();
        window.SetGraph(graph);
        int before = graph.Edges.Count;
        Assert.True(window.TryConnect("n_color", "Out", "b_size", "Size"));
        Assert.Equal(before + 1, graph.Edges.Count);
        Assert.Contains(graph.Edges, e => e.FromNode == "n_color" && e.ToPort == "Size");
    }

    [Fact]
    public void CompilePreview_EmitsComputePasses()
    {
        var window = new VfxGraphWindow();
        window.SetGraph(VfxGraphTemplates.CreateLoop());
        string source = window.CompilePreview();
        Assert.Contains("Shader \"VFX/Loop\"", source);
        Assert.Contains("SLANGPROGRAM", source);
        Assert.Contains("Pass \"Init\"", source);
        Assert.True(string.IsNullOrEmpty(window.CompileError));
    }

    [Fact]
    public void OnGUI_HeadlessPaper_DoesNotThrow()
    {
        var paper = new Paper(new NullRenderer(), 960, 540, new FontAtlasSettings());
        paper.BeginFrame(0.016f);
        Origami.BeginFrame(paper, 0.016f);
        var window = new VfxGraphWindow();
        window.SetGraph(VfxGraphTemplates.CreateTrail());
        window.OnGUI(paper, 960, 540);
        Origami.EndFrame(paper);
        paper.EndFrame();
        Assert.Contains(window.GraphNodes, n => n.Id == "b_overlife");
    }

    [Fact]
    public void Blackboard_AddRemoveProperty_UpdatesPreviewSettings()
    {
        var window = new VfxGraphWindow();
        window.SetGraph(VfxGraphTemplates.CreateEmpty());
        Assert.True(window.TryAddProperty(VfxExposedNames.SpawnRate, VfxValueType.Float, "48"));
        Assert.Equal(48f, window.PreviewSettings!.SpawnRate);
        Assert.True(window.TryRemoveProperty(VfxExposedNames.SpawnRate));
        Assert.Equal(0f, window.PreviewSettings!.SpawnRate);
    }

    [Fact]
    public void Preview_WritesPropertyEvidencePng()
    {
        VfxGraphAsset graph = VfxGraphTemplates.CreateLoop();
        var window = new VfxGraphWindow();
        window.SetGraph(graph);
        window.TryAddProperty("Wind", VfxValueType.Float3, "1,0,0");

        int w = 320, h = 180;
        byte[] rgba = new byte[w * h * 4];
        VfxCompiledSettings settings = window.PreviewSettings!;
        VfxGraphWindow.FillPreviewRgba(rgba, w, h, settings);

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx", "m9-vfx-exposed-properties.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, EncodePng(rgba, w, h));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
        Assert.NotNull(graph.FindProperty("Wind"));
        Assert.Equal(45, graph.Properties.Count);
    }

    [Fact]
    public void Preview_WritesMainTexEvidencePng()
    {
        VfxCompiledSettings settings = VfxGraphCompiler.CompileSettings(VfxGraphTemplates.CreateLoop());
        int w = 320, h = 180;
        byte[] rgba = new byte[w * h * 4];
        VfxGraphWindow.FillTexturedPreviewRgba(rgba, w, h, settings);

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx", "m10-vfx-maintex.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, EncodePng(rgba, w, h));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    [Fact]
    public void Preview_WritesFlipbookEvidencePng()
    {
        VfxGraphAsset graph = VfxGraphTemplates.CreateLoop();
        graph.FindProperty(VfxExposedNames.FlipbookColumns)!.DefaultValue = "4";
        graph.FindProperty(VfxExposedNames.FlipbookRows)!.DefaultValue = "4";
        VfxCompiledSettings settings = VfxGraphCompiler.CompileSettings(graph);
        Assert.Equal(4f, settings.FlipbookColumns);
        Assert.Equal(4f, settings.FlipbookRows);

        int w = 320, h = 180;
        byte[] rgba = new byte[w * h * 4];
        VfxGraphWindow.FillFlipbookPreviewRgba(rgba, w, h, settings);

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx", "m11-vfx-flipbook.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, EncodePng(rgba, w, h));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    [Fact]
    public void Preview_WritesCollisionEvidencePng()
    {
        VfxCompiledSettings settings = VfxGraphCompiler.CompileSettings(VfxGraphTemplates.CreateLoop());
        Assert.Equal(2f, settings.CollisionMode);
        Assert.Equal(1f, settings.CollisionPlane.Y);

        int w = 320, h = 180;
        byte[] rgba = new byte[w * h * 4];
        VfxGraphWindow.FillCollisionPreviewRgba(rgba, w, h, settings);

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx", "m12-vfx-plane-collision.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, EncodePng(rgba, w, h));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    [Fact]
    public void Preview_WritesSortEvidencePng()
    {
        VfxCompiledSettings settings = VfxGraphCompiler.CompileSettings(VfxGraphTemplates.CreateLoop());
        settings.SortMode = 1f;

        int w = 320, h = 180;
        byte[] rgba = new byte[w * h * 4];
        VfxGraphWindow.FillSortPreviewRgba(rgba, w, h, settings);

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx", "m31-vfx-particle-sort.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, EncodePng(rgba, w, h));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    [Fact]
    public void Preview_WritesCameraFadeEvidencePng()
    {
        VfxCompiledSettings settings = VfxGraphCompiler.CompileSettings(VfxGraphTemplates.CreateLoop());
        settings.CameraFadeNear = 0.5f;
        settings.CameraFadeFar = 2.4f;

        int w = 320, h = 180;
        byte[] rgba = new byte[w * h * 4];
        VfxGraphWindow.FillCameraFadePreviewRgba(rgba, w, h, settings);

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx", "m32-vfx-camera-fade.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, EncodePng(rgba, w, h));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    [Fact]
    public void Preview_WritesFlipbookBlendEvidencePng()
    {
        VfxCompiledSettings settings = VfxGraphCompiler.CompileSettings(VfxGraphTemplates.CreateLoop());
        settings.FlipbookColumns = 2f;
        settings.FlipbookRows = 2f;
        settings.FlipbookBlend = 1f;

        int w = 320, h = 180;
        byte[] rgba = new byte[w * h * 4];
        VfxGraphWindow.FillFlipbookBlendPreviewRgba(rgba, w, h, settings);

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx", "m33-vfx-flipbook-blend.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, EncodePng(rgba, w, h));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    [Fact]
    public void Preview_WritesFlipbookMotionEvidencePng()
    {
        VfxCompiledSettings settings = VfxGraphCompiler.CompileSettings(VfxGraphTemplates.CreateLoop());
        settings.FlipbookColumns = 2f;
        settings.FlipbookRows = 2f;
        settings.FlipbookBlend = 1f;
        settings.FlipbookMotionScale = 1.25f;

        int w = 320, h = 180;
        byte[] rgba = new byte[w * h * 4];
        VfxGraphWindow.FillFlipbookMotionPreviewRgba(rgba, w, h, settings);

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx", "m46-vfx-flipbook-motion.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, EncodePng(rgba, w, h));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    [Fact]
    public void Preview_WritesOverLifeEvidencePng()
    {
        VfxCompiledSettings settings = VfxGraphCompiler.CompileSettings(VfxGraphTemplates.CreateLoop());
        Assert.Equal(1f, settings.ColorOverLife);
        Assert.Equal(0f, settings.SizeOverLifeTo);

        int w = 320, h = 180;
        byte[] rgba = new byte[w * h * 4];
        VfxGraphWindow.FillOverLifePreviewRgba(rgba, w, h, settings);

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx", "m34-vfx-over-life.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, EncodePng(rgba, w, h));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    [Fact]
    public void Preview_WritesMotionVectorEvidencePng()
    {
        VfxCompiledSettings settings = VfxGraphCompiler.CompileSettings(VfxGraphTemplates.CreateLoop());
        settings.MotionVectorMode = 1f;
        settings.MotionVectorScale = 8f;

        int w = 320, h = 180;
        byte[] rgba = new byte[w * h * 4];
        VfxGraphWindow.FillMotionVectorPreviewRgba(rgba, w, h, settings);

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx", "m35-vfx-motion-vectors.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, EncodePng(rgba, w, h));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    [Fact]
    public void Preview_WritesOrientEvidencePng()
    {
        VfxCompiledSettings settings = VfxGraphCompiler.CompileSettings(VfxGraphTemplates.CreateLoop());
        settings.OrientMode = 1f;

        int w = 320, h = 180;
        byte[] rgba = new byte[w * h * 4];
        VfxGraphWindow.FillOrientPreviewRgba(rgba, w, h, settings);

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx", "m36-vfx-orient-velocity.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, EncodePng(rgba, w, h));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    [Fact]
    public void Preview_WritesFixedAxisEvidencePng()
    {
        VfxCompiledSettings settings = VfxGraphCompiler.CompileSettings(VfxGraphTemplates.CreateLoop());
        settings.FixedAxisMode = 2f;
        settings.FixedAxisX = 0f;
        settings.FixedAxisY = 1f;
        settings.FixedAxisZ = 0f;

        int w = 320, h = 180;
        byte[] rgba = new byte[w * h * 4];
        VfxGraphWindow.FillFixedAxisPreviewRgba(rgba, w, h, settings);

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx", "m43-vfx-orient-fixed-axis.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, EncodePng(rgba, w, h));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    [Fact]
    public void Preview_WritesPositionShapeEvidencePng()
    {
        VfxCompiledSettings settings = VfxGraphCompiler.CompileSettings(VfxGraphTemplates.CreateLoop());
        settings.PositionShapeMode = 1f;
        settings.PositionShapeSx = 0.7f;
        settings.PositionShapeSy = 0.7f;
        settings.PositionShapeSz = 0.7f;

        int w = 320, h = 180;
        byte[] rgba = new byte[w * h * 4];
        VfxGraphWindow.FillPositionShapePreviewRgba(rgba, w, h, settings);

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx", "m37-vfx-position-shape.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, EncodePng(rgba, w, h));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    [Fact]
    public void Preview_WritesDragEvidencePng()
    {
        VfxCompiledSettings settings = VfxGraphCompiler.CompileSettings(VfxGraphTemplates.CreateLoop());
        settings.DragAmount = 1.4f;

        int w = 320, h = 180;
        byte[] rgba = new byte[w * h * 4];
        VfxGraphWindow.FillDragPreviewRgba(rgba, w, h, settings);

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx", "m38-vfx-drag.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, EncodePng(rgba, w, h));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    [Fact]
    public void Preview_WritesForceEvidencePng()
    {
        VfxCompiledSettings settings = VfxGraphCompiler.CompileSettings(VfxGraphTemplates.CreateLoop());
        settings.ForceX = 1.6f;

        int w = 320, h = 180;
        byte[] rgba = new byte[w * h * 4];
        VfxGraphWindow.FillForcePreviewRgba(rgba, w, h, settings);

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx", "m39-vfx-force.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, EncodePng(rgba, w, h));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    [Fact]
    public void Preview_WritesSpinEvidencePng()
    {
        VfxCompiledSettings settings = VfxGraphCompiler.CompileSettings(VfxGraphTemplates.CreateLoop());
        settings.AngularVelocityRate = 1.2f;

        int w = 320, h = 180;
        byte[] rgba = new byte[w * h * 4];
        VfxGraphWindow.FillSpinPreviewRgba(rgba, w, h, settings);

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx", "m40-vfx-angular-velocity.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, EncodePng(rgba, w, h));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    [Fact]
    public void Preview_WritesCustomHlslEvidencePng()
    {
        VfxCompiledSettings settings = VfxGraphCompiler.CompileSettings(VfxGraphTemplates.CreateLoop());
        settings.CustomHlsl = 1f;

        int w = 320, h = 180;
        byte[] rgba = new byte[w * h * 4];
        VfxGraphWindow.FillCustomHlslPreviewRgba(rgba, w, h, settings);

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx", "m41-vfx-custom-hlsl.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, EncodePng(rgba, w, h));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    [Fact]
    public void Preview_WritesSequentialEvidencePng()
    {
        VfxCompiledSettings settings = VfxGraphCompiler.CompileSettings(VfxGraphTemplates.CreateLoop());
        settings.SequentialMode = 1f;
        settings.SequentialSx = 0.7f;
        settings.SequentialSy = 0.7f;
        settings.SequentialSz = 12f;

        int w = 320, h = 180;
        byte[] rgba = new byte[w * h * 4];
        VfxGraphWindow.FillSequentialPreviewRgba(rgba, w, h, settings);

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx", "m42-vfx-sequential.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, EncodePng(rgba, w, h));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    [Fact]
    public void Preview_WritesInheritVelocityEvidencePng()
    {
        VfxCompiledSettings settings = VfxGraphCompiler.CompileSettings(VfxGraphTemplates.CreateLoop());
        settings.InheritVelocityAmount = 1f;

        int w = 320, h = 180;
        byte[] rgba = new byte[w * h * 4];
        VfxGraphWindow.FillInheritVelocityPreviewRgba(rgba, w, h, settings);

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx", "m48-vfx-inherit-velocity.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, EncodePng(rgba, w, h));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    [Fact]
    public void Preview_WritesTriggerEventRateEvidencePng()
    {
        VfxCompiledSettings settings = VfxGraphCompiler.CompileSettings(VfxGraphTemplates.CreateLoop());
        settings.TriggerEventRate = 8f;

        int w = 320, h = 180;
        byte[] rgba = new byte[w * h * 4];
        VfxGraphWindow.FillTriggerEventRatePreviewRgba(rgba, w, h, settings);

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx", "m49-vfx-trigger-event-rate.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, EncodePng(rgba, w, h));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    [Fact]
    public void Preview_WritesTriggerEventOverDistanceEvidencePng()
    {
        VfxCompiledSettings settings = VfxGraphCompiler.CompileSettings(VfxGraphTemplates.CreateLoop());
        settings.TriggerEventOverDistance = 8f;

        int w = 320, h = 180;
        byte[] rgba = new byte[w * h * 4];
        VfxGraphWindow.FillTriggerEventOverDistancePreviewRgba(rgba, w, h, settings);

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx", "m50-vfx-trigger-event-distance.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, EncodePng(rgba, w, h));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    [Fact]
    public void Preview_WritesTriggerEventOnceEvidencePng()
    {
        VfxCompiledSettings settings = VfxGraphCompiler.CompileSettings(VfxGraphTemplates.CreateLoop());
        settings.TriggerEventOnceCount = 3f;

        int w = 320, h = 180;
        byte[] rgba = new byte[w * h * 4];
        VfxGraphWindow.FillTriggerEventOncePreviewRgba(rgba, w, h, settings);

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx", "m54-vfx-trigger-event-once.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, EncodePng(rgba, w, h));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    [Fact]
    public void Preview_WritesCollisionFrictionEvidencePng()
    {
        VfxCompiledSettings settings = VfxGraphCompiler.CompileSettings(VfxGraphTemplates.CreateLoop());
        settings.CollisionFrictionAmount = 0.7f;

        int w = 320, h = 180;
        byte[] rgba = new byte[w * h * 4];
        VfxGraphWindow.FillCollisionFrictionPreviewRgba(rgba, w, h, settings);

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx", "m55-vfx-collision-friction.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, EncodePng(rgba, w, h));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    [Fact]
    public void Preview_WritesCollisionElasticityEvidencePng()
    {
        VfxCompiledSettings settings = VfxGraphCompiler.CompileSettings(VfxGraphTemplates.CreateLoop());
        settings.CollisionElasticityAmount = 0.7f;

        int w = 320, h = 180;
        byte[] rgba = new byte[w * h * 4];
        VfxGraphWindow.FillCollisionElasticityPreviewRgba(rgba, w, h, settings);

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx", "m56-vfx-collision-elasticity.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, EncodePng(rgba, w, h));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    [Fact]
    public void Preview_WritesCollisionLifetimeLossEvidencePng()
    {
        VfxCompiledSettings settings = VfxGraphCompiler.CompileSettings(VfxGraphTemplates.CreateLoop());
        settings.CollisionLifetimeLossAmount = 0.7f;

        int w = 320, h = 180;
        byte[] rgba = new byte[w * h * 4];
        VfxGraphWindow.FillCollisionLifetimeLossPreviewRgba(rgba, w, h, settings);

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx", "m57-vfx-collision-lifetime-loss.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, EncodePng(rgba, w, h));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    [Fact]
    public void Preview_WritesLimitVelocityEvidencePng()
    {
        VfxCompiledSettings settings = VfxGraphCompiler.CompileSettings(VfxGraphTemplates.CreateLoop());
        settings.LimitVelocitySpeed = 2.5f;

        int w = 320, h = 180;
        byte[] rgba = new byte[w * h * 4];
        VfxGraphWindow.FillLimitVelocityPreviewRgba(rgba, w, h, settings);

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx", "m58-vfx-limit-velocity.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, EncodePng(rgba, w, h));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    [Fact]
    public void Preview_WritesKillSphereEvidencePng()
    {
        VfxCompiledSettings settings = VfxGraphCompiler.CompileSettings(VfxGraphTemplates.CreateLoop());
        settings.KillSphereRadius = 0.8f;

        int w = 320, h = 180;
        byte[] rgba = new byte[w * h * 4];
        VfxGraphWindow.FillKillSpherePreviewRgba(rgba, w, h, settings);

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx", "m59-vfx-kill-sphere.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, EncodePng(rgba, w, h));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    [Fact]
    public void Preview_WritesKillAABoxEvidencePng()
    {
        VfxCompiledSettings settings = VfxGraphCompiler.CompileSettings(VfxGraphTemplates.CreateLoop());
        settings.KillAABoxSx = 0.7f;
        settings.KillAABoxSy = 0.4f;
        settings.KillAABoxSz = 1f;

        int w = 320, h = 180;
        byte[] rgba = new byte[w * h * 4];
        VfxGraphWindow.FillKillAABoxPreviewRgba(rgba, w, h, settings);

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx", "m60-vfx-kill-aabox.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, EncodePng(rgba, w, h));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    [Fact]
    public void Preview_WritesAttractEvidencePng()
    {
        VfxCompiledSettings settings = VfxGraphCompiler.CompileSettings(VfxGraphTemplates.CreateLoop());
        settings.AttractStrength = 2f;

        int w = 320, h = 180;
        byte[] rgba = new byte[w * h * 4];
        VfxGraphWindow.FillAttractPreviewRgba(rgba, w, h, settings);

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx", "m61-vfx-attract.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, EncodePng(rgba, w, h));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    [Fact]
    public void Preview_WritesConformToSphereEvidencePng()
    {
        VfxCompiledSettings settings = VfxGraphCompiler.CompileSettings(VfxGraphTemplates.CreateLoop());
        settings.ConformSphereStrength = 2f;
        settings.ConformSphereRadius = 1f;

        int w = 320, h = 180;
        byte[] rgba = new byte[w * h * 4];
        VfxGraphWindow.FillConformToSpherePreviewRgba(rgba, w, h, settings);

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx", "m66-vfx-conform-to-sphere.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, EncodePng(rgba, w, h));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    [Fact]
    public void Preview_WritesVortexEvidencePng()
    {
        VfxCompiledSettings settings = VfxGraphCompiler.CompileSettings(VfxGraphTemplates.CreateLoop());
        settings.VortexRotation = 2f;
        settings.VortexAttraction = 1f;

        int w = 320, h = 180;
        byte[] rgba = new byte[w * h * 4];
        VfxGraphWindow.FillVortexPreviewRgba(rgba, w, h, settings);

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx", "m67-vfx-vortex.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, EncodePng(rgba, w, h));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    [Fact]
    public void Preview_WritesConformToCylinderEvidencePng()
    {
        VfxCompiledSettings settings = VfxGraphCompiler.CompileSettings(VfxGraphTemplates.CreateLoop());
        settings.ConformCylinderStrength = 2f;
        settings.ConformCylinderRadius = 1f;

        int w = 320, h = 180;
        byte[] rgba = new byte[w * h * 4];
        VfxGraphWindow.FillConformToCylinderPreviewRgba(rgba, w, h, settings);

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx", "m68-vfx-conform-to-cylinder.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, EncodePng(rgba, w, h));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    [Fact]
    public void Preview_WritesOrientLookAtEvidencePng()
    {
        VfxCompiledSettings settings = VfxGraphCompiler.CompileSettings(VfxGraphTemplates.CreateLoop());
        settings.LookAtMode = 2f;

        int w = 320, h = 180;
        byte[] rgba = new byte[w * h * 4];
        VfxGraphWindow.FillOrientLookAtPreviewRgba(rgba, w, h, settings);

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx", "m62-vfx-orient-look-at.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, EncodePng(rgba, w, h));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    [Fact]
    public void Preview_WritesFlipbookRateEvidencePng()
    {
        VfxCompiledSettings settings = VfxGraphCompiler.CompileSettings(VfxGraphTemplates.CreateLoop());
        settings.FlipbookRate = 8f;
        settings.FlipbookColumns = 2f;
        settings.FlipbookRows = 2f;

        int w = 320, h = 180;
        byte[] rgba = new byte[w * h * 4];
        VfxGraphWindow.FillFlipbookRatePreviewRgba(rgba, w, h, settings);

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx", "m63-vfx-flipbook-rate.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, EncodePng(rgba, w, h));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    [Fact]
    public void Preview_WritesFlipbookStartEvidencePng()
    {
        VfxCompiledSettings settings = VfxGraphCompiler.CompileSettings(VfxGraphTemplates.CreateLoop());
        settings.FlipbookStart = 2f;
        settings.FlipbookColumns = 2f;
        settings.FlipbookRows = 2f;

        int w = 320, h = 180;
        byte[] rgba = new byte[w * h * 4];
        VfxGraphWindow.FillFlipbookStartPreviewRgba(rgba, w, h, settings);

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx", "m65-vfx-flipbook-start.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, EncodePng(rgba, w, h));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    [Fact]
    public void Preview_WritesKillPlaneEvidencePng()
    {
        VfxCompiledSettings settings = VfxGraphCompiler.CompileSettings(VfxGraphTemplates.CreateLoop());
        settings.KillPlaneNy = 1f;

        int w = 320, h = 180;
        byte[] rgba = new byte[w * h * 4];
        VfxGraphWindow.FillKillPlanePreviewRgba(rgba, w, h, settings);

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx", "m64-vfx-kill-plane.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, EncodePng(rgba, w, h));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    [Fact]
    public void Preview_WritesSizeOverSpeedEvidencePng()
    {
        VfxCompiledSettings settings = VfxGraphCompiler.CompileSettings(VfxGraphTemplates.CreateLoop());
        settings.SizeOverSpeedAmount = 0.35f;

        int w = 320, h = 180;
        byte[] rgba = new byte[w * h * 4];
        VfxGraphWindow.FillSizeOverSpeedPreviewRgba(rgba, w, h, settings);

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx", "m51-vfx-size-over-speed.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, EncodePng(rgba, w, h));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    [Fact]
    public void Preview_WritesMultiplySizeEvidencePng()
    {
        VfxCompiledSettings settings = VfxGraphCompiler.CompileSettings(VfxGraphTemplates.CreateLoop());
        settings.MultiplySizeAmount = 2f;

        int w = 320, h = 180;
        byte[] rgba = new byte[w * h * 4];
        VfxGraphWindow.FillMultiplySizePreviewRgba(rgba, w, h, settings);

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx", "m69-vfx-multiply-size.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, EncodePng(rgba, w, h));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    [Fact]
    public void Preview_WritesMultiplyColorEvidencePng()
    {
        VfxCompiledSettings settings = VfxGraphCompiler.CompileSettings(VfxGraphTemplates.CreateLoop());
        settings.MultiplyColorR = 1f;
        settings.MultiplyColorG = 0.45f;
        settings.MultiplyColorB = 0.15f;

        int w = 320, h = 180;
        byte[] rgba = new byte[w * h * 4];
        VfxGraphWindow.FillMultiplyColorPreviewRgba(rgba, w, h, settings);

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx", "m70-vfx-multiply-color.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, EncodePng(rgba, w, h));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    [Fact]
    public void Preview_WritesAddSizeEvidencePng()
    {
        VfxCompiledSettings settings = VfxGraphCompiler.CompileSettings(VfxGraphTemplates.CreateLoop());
        settings.AddSizeAmount = 0.08f;

        int w = 320, h = 180;
        byte[] rgba = new byte[w * h * 4];
        VfxGraphWindow.FillAddSizePreviewRgba(rgba, w, h, settings);

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx", "m71-vfx-add-size.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, EncodePng(rgba, w, h));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    [Fact]
    public void Preview_WritesAddColorEvidencePng()
    {
        VfxCompiledSettings settings = VfxGraphCompiler.CompileSettings(VfxGraphTemplates.CreateLoop());
        settings.AddColorR = 0.35f;
        settings.AddColorG = 0.1f;

        int w = 320, h = 180;
        byte[] rgba = new byte[w * h * 4];
        VfxGraphWindow.FillAddColorPreviewRgba(rgba, w, h, settings);

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx", "m72-vfx-add-color.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, EncodePng(rgba, w, h));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    [Fact]
    public void Preview_WritesAddVelocityEvidencePng()
    {
        VfxCompiledSettings settings = VfxGraphCompiler.CompileSettings(VfxGraphTemplates.CreateLoop());
        settings.AddVelocityX = 1.2f;

        int w = 320, h = 180;
        byte[] rgba = new byte[w * h * 4];
        VfxGraphWindow.FillAddVelocityPreviewRgba(rgba, w, h, settings);

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx", "m73-vfx-add-velocity.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, EncodePng(rgba, w, h));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    [Fact]
    public void Preview_WritesMultiplyVelocityEvidencePng()
    {
        VfxCompiledSettings settings = VfxGraphCompiler.CompileSettings(VfxGraphTemplates.CreateLoop());
        settings.MultiplyVelocityAmount = 2f;

        int w = 320, h = 180;
        byte[] rgba = new byte[w * h * 4];
        VfxGraphWindow.FillMultiplyVelocityPreviewRgba(rgba, w, h, settings);

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx", "m74-vfx-multiply-velocity.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, EncodePng(rgba, w, h));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    [Fact]
    public void Preview_WritesSetSizeEvidencePng()
    {
        VfxCompiledSettings settings = VfxGraphCompiler.CompileSettings(VfxGraphTemplates.CreateLoop());
        settings.OverwriteSizeAmount = 0.22f;

        int w = 320, h = 180;
        byte[] rgba = new byte[w * h * 4];
        VfxGraphWindow.FillSetSizePreviewRgba(rgba, w, h, settings);

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx", "m75-vfx-set-size.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, EncodePng(rgba, w, h));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    [Fact]
    public void Preview_WritesOverwriteColorEvidencePng()
    {
        VfxCompiledSettings settings = VfxGraphCompiler.CompileSettings(VfxGraphTemplates.CreateLoop());
        settings.OverwriteColorR = 0.95f;
        settings.OverwriteColorG = 0.35f;
        settings.OverwriteColorB = 0.15f;
        settings.OverwriteColorA = 1f;

        int w = 320, h = 180;
        byte[] rgba = new byte[w * h * 4];
        VfxGraphWindow.FillOverwriteColorPreviewRgba(rgba, w, h, settings);

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx", "m76-vfx-overwrite-color.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, EncodePng(rgba, w, h));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    [Fact]
    public void Preview_WritesOverwriteVelocityEvidencePng()
    {
        VfxCompiledSettings settings = VfxGraphCompiler.CompileSettings(VfxGraphTemplates.CreateLoop());
        settings.OverwriteVelocityX = 1.2f;

        int w = 320, h = 180;
        byte[] rgba = new byte[w * h * 4];
        VfxGraphWindow.FillOverwriteVelocityPreviewRgba(rgba, w, h, settings);

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx", "m77-vfx-overwrite-velocity.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, EncodePng(rgba, w, h));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    [Fact]
    public void Preview_WritesOverwriteLifetimeEvidencePng()
    {
        VfxCompiledSettings settings = VfxGraphCompiler.CompileSettings(VfxGraphTemplates.CreateLoop());
        settings.OverwriteLifetimeAmount = 1.5f;

        int w = 320, h = 180;
        byte[] rgba = new byte[w * h * 4];
        VfxGraphWindow.FillOverwriteLifetimePreviewRgba(rgba, w, h, settings);

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx", "m78-vfx-overwrite-lifetime.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, EncodePng(rgba, w, h));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    [Fact]
    public void Preview_WritesAddLifetimeEvidencePng()
    {
        VfxCompiledSettings settings = VfxGraphCompiler.CompileSettings(VfxGraphTemplates.CreateLoop());
        settings.AddLifetimeAmount = 0.5f;

        int w = 320, h = 180;
        byte[] rgba = new byte[w * h * 4];
        VfxGraphWindow.FillAddLifetimePreviewRgba(rgba, w, h, settings);

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx", "m79-vfx-add-lifetime.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, EncodePng(rgba, w, h));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    [Fact]
    public void Preview_WritesMultiplyLifetimeEvidencePng()
    {
        VfxCompiledSettings settings = VfxGraphCompiler.CompileSettings(VfxGraphTemplates.CreateLoop());
        settings.MultiplyLifetimeAmount = 2f;

        int w = 320, h = 180;
        byte[] rgba = new byte[w * h * 4];
        VfxGraphWindow.FillMultiplyLifetimePreviewRgba(rgba, w, h, settings);

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx", "m80-vfx-multiply-lifetime.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, EncodePng(rgba, w, h));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    [Fact]
    public void Preview_WritesAddAgeEvidencePng()
    {
        VfxCompiledSettings settings = VfxGraphCompiler.CompileSettings(VfxGraphTemplates.CreateLoop());
        settings.AddAgeAmount = 0.5f;

        int w = 320, h = 180;
        byte[] rgba = new byte[w * h * 4];
        VfxGraphWindow.FillAddAgePreviewRgba(rgba, w, h, settings);

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx", "m81-vfx-add-age.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, EncodePng(rgba, w, h));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    [Fact]
    public void Preview_WritesMultiplyAgeEvidencePng()
    {
        VfxCompiledSettings settings = VfxGraphCompiler.CompileSettings(VfxGraphTemplates.CreateLoop());
        settings.MultiplyAgeAmount = 2f;

        int w = 320, h = 180;
        byte[] rgba = new byte[w * h * 4];
        VfxGraphWindow.FillMultiplyAgePreviewRgba(rgba, w, h, settings);

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx", "m82-vfx-multiply-age.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, EncodePng(rgba, w, h));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    [Fact]
    public void Preview_WritesSetAgeEvidencePng()
    {
        VfxCompiledSettings settings = VfxGraphCompiler.CompileSettings(VfxGraphTemplates.CreateLoop());
        settings.SetAgeAmount = 0.5f;

        int w = 320, h = 180;
        byte[] rgba = new byte[w * h * 4];
        VfxGraphWindow.FillSetAgePreviewRgba(rgba, w, h, settings);

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx", "m83-vfx-set-age.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, EncodePng(rgba, w, h));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    [Fact]
    public void Preview_WritesAddPositionEvidencePng()
    {
        VfxCompiledSettings settings = VfxGraphCompiler.CompileSettings(VfxGraphTemplates.CreateLoop());
        settings.AddPositionX = 0.8f;

        int w = 320, h = 180;
        byte[] rgba = new byte[w * h * 4];
        VfxGraphWindow.FillAddPositionPreviewRgba(rgba, w, h, settings);

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx", "m84-vfx-add-position.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, EncodePng(rgba, w, h));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    [Fact]
    public void Preview_WritesSetPositionEvidencePng()
    {
        VfxCompiledSettings settings = VfxGraphCompiler.CompileSettings(VfxGraphTemplates.CreateLoop());
        settings.SetPositionX = 1.2f;

        int w = 320, h = 180;
        byte[] rgba = new byte[w * h * 4];
        VfxGraphWindow.FillSetPositionPreviewRgba(rgba, w, h, settings);

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx", "m85-vfx-set-position.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, EncodePng(rgba, w, h));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    [Fact]
    public void Preview_WritesColorOverSpeedEvidencePng()
    {
        VfxCompiledSettings settings = VfxGraphCompiler.CompileSettings(VfxGraphTemplates.CreateLoop());
        settings.ColorOverSpeedScale = 4f;

        int w = 320, h = 180;
        byte[] rgba = new byte[w * h * 4];
        VfxGraphWindow.FillColorOverSpeedPreviewRgba(rgba, w, h, settings);

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx", "m52-vfx-color-over-speed.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, EncodePng(rgba, w, h));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    [Fact]
    public void Preview_WritesScreenSpaceSizeEvidencePng()
    {
        VfxCompiledSettings settings = VfxGraphCompiler.CompileSettings(VfxGraphTemplates.CreateLoop());
        settings.ScreenSpaceSizeAmount = 2f;

        int w = 320, h = 180;
        byte[] rgba = new byte[w * h * 4];
        VfxGraphWindow.FillScreenSpaceSizePreviewRgba(rgba, w, h, settings);

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx", "m53-vfx-screen-space-size.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, EncodePng(rgba, w, h));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    [Fact]
    public void Preview_WritesDecalEvidencePng()
    {
        VfxCompiledSettings settings = VfxGraphCompiler.CompileSettings(VfxGraphTemplates.CreateDecal());
        Assert.True(settings.OutputDecal);

        int w = 320, h = 180;
        byte[] rgba = new byte[w * h * 4];
        VfxGraphWindow.FillDecalPreviewRgba(rgba, w, h, settings);

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx", "m30-vfx-particle-decals.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, EncodePng(rgba, w, h));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    [Fact]
    public void Preview_WritesMeshEvidencePng()
    {
        VfxCompiledSettings settings = VfxGraphCompiler.CompileSettings(VfxGraphTemplates.CreateMesh());
        Assert.True(settings.OutputMesh);

        int w = 320, h = 180;
        byte[] rgba = new byte[w * h * 4];
        VfxGraphWindow.FillMeshPreviewRgba(rgba, w, h, settings);

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx", "m13-vfx-mesh-output.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, EncodePng(rgba, w, h));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    [Fact]
    public void Preview_WritesSubgraphEvidencePng()
    {
        VfxCompiledSettings settings = VfxGraphCompiler.CompileSettings(VfxGraphTemplates.CreateSubgraph());
        Assert.Equal(1, settings.SubgraphCount);
        Assert.Equal(64f, settings.SpawnRate);

        int w = 320, h = 180;
        byte[] rgba = new byte[w * h * 4];
        VfxGraphWindow.FillSubgraphPreviewRgba(rgba, w, h, settings);

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx", "m14-vfx-subgraphs.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, EncodePng(rgba, w, h));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    [Fact]
    public void SetGraph_Subgraph_ShowsIncludeOperator()
    {
        var window = new VfxGraphWindow();
        window.SetGraph(VfxGraphTemplates.CreateSubgraph());
        Assert.Contains(window.GraphNodes, n => n.Id == "n_sub");
        Assert.Equal(64f, window.PreviewSettings!.SpawnRate);
        Assert.Equal(1, window.PreviewSettings.SubgraphCount);
    }

    [Fact]
    public void Preview_WritesSdfEvidencePng()
    {
        VfxCompiledSettings settings = VfxGraphCompiler.CompileSettings(VfxGraphTemplates.CreateLoop());
        Assert.Equal(2f, settings.SdfCollisionMode);
        Assert.Equal(1f, settings.SdfShape);

        int w = 320, h = 180;
        byte[] rgba = new byte[w * h * 4];
        VfxGraphWindow.FillSdfPreviewRgba(rgba, w, h, settings);

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx", "m16-vfx-sdf-collision.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, EncodePng(rgba, w, h));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    [Fact]
    public void Preview_WritesCylinderConeEvidencePng()
    {
        VfxCompiledSettings settings = VfxGraphCompiler.CompileSettings(VfxGraphTemplates.CreateLoop());
        settings.SdfCollisionMode = VfxCollision.ModeBounce;
        settings.SdfShape = VfxCollision.ShapeCylinder;

        int w = 320, h = 180;
        byte[] rgba = new byte[w * h * 4];
        VfxGraphWindow.FillCylinderConePreviewRgba(rgba, w, h, settings);

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx", "m25-vfx-cylinder-cone.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, EncodePng(rgba, w, h));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    [Fact]
    public void Preview_WritesTurbulenceEvidencePng()
    {
        VfxCompiledSettings settings = VfxGraphCompiler.CompileSettings(VfxGraphTemplates.CreateLoop());
        Assert.Equal(1.2f, settings.TurbulenceIntensity, 3);
        Assert.Equal(1.25f, settings.TurbulenceFrequency, 3);

        int w = 320, h = 180;
        byte[] rgba = new byte[w * h * 4];
        VfxGraphWindow.FillTurbulencePreviewRgba(rgba, w, h, settings);

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx", "m17-vfx-turbulence.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, EncodePng(rgba, w, h));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    [Fact]
    public void Preview_WritesVolumeSdfEvidencePng()
    {
        VfxCompiledSettings settings = VfxGraphCompiler.CompileSettings(VfxGraphTemplates.CreateLoop());
        settings.SdfShape = VfxCollision.ShapeVolume;

        int w = 320, h = 180;
        byte[] rgba = new byte[w * h * 4];
        VfxGraphWindow.FillVolumeSdfPreviewRgba(rgba, w, h, settings);

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx", "m19-vfx-sdf-volume.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, EncodePng(rgba, w, h));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    [Fact]
    public void Preview_WritesSoftParticleEvidencePng()
    {
        VfxCompiledSettings settings = VfxGraphCompiler.CompileSettings(VfxGraphTemplates.CreateLoop());
        Assert.Equal(1f, settings.SoftParticleFade);

        int w = 320, h = 180;
        byte[] rgba = new byte[w * h * 4];
        VfxGraphWindow.FillSoftParticlePreviewRgba(rgba, w, h, settings);

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx", "m18-vfx-soft-particles.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, EncodePng(rgba, w, h));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    [Fact]
    public void Preview_WritesStripEvidencePng()
    {
        VfxCompiledSettings settings = VfxGraphCompiler.CompileSettings(VfxGraphTemplates.CreateTrail());
        Assert.True(settings.OutputStrip);

        int w = 320, h = 180;
        byte[] rgba = new byte[w * h * 4];
        VfxGraphWindow.FillStripPreviewRgba(rgba, w, h, settings);

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx", "m21-vfx-output-strips.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, EncodePng(rgba, w, h));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    [Fact]
    public void Preview_WritesConformEvidencePng()
    {
        VfxCompiledSettings settings = VfxGraphCompiler.CompileSettings(VfxGraphTemplates.CreateLoop());
        settings.ConformAttraction = 4f;

        int w = 320, h = 180;
        byte[] rgba = new byte[w * h * 4];
        VfxGraphWindow.FillConformPreviewRgba(rgba, w, h, settings);

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx", "m23-vfx-conform-sdf.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, EncodePng(rgba, w, h));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    [Fact]
    public void Preview_WritesVectorFieldEvidencePng()
    {
        VfxCompiledSettings settings = VfxGraphCompiler.CompileSettings(VfxGraphTemplates.CreateLoop());
        settings.VectorFieldIntensity = 2.5f;

        int w = 320, h = 180;
        byte[] rgba = new byte[w * h * 4];
        VfxGraphWindow.FillVectorFieldPreviewRgba(rgba, w, h, settings);

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx", "m24-vfx-vector-field.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, EncodePng(rgba, w, h));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    [Fact]
    public void Preview_WritesParticleLightEvidencePng()
    {
        VfxCompiledSettings settings = VfxGraphCompiler.CompileSettings(VfxGraphTemplates.CreateLoop());
        settings.ParticleLightIntensity = 2f;
        settings.ParticleLightCount = 8f;

        int w = 320, h = 180;
        byte[] rgba = new byte[w * h * 4];
        VfxGraphWindow.FillParticleLightPreviewRgba(rgba, w, h, settings);

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx", "m29-vfx-particle-lights.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, EncodePng(rgba, w, h));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    [Fact]
    public void Preview_WritesDistortionEvidencePng()
    {
        VfxCompiledSettings settings = VfxGraphCompiler.CompileSettings(VfxGraphTemplates.CreateLoop());
        settings.DistortionIntensity = 0.35f;

        int w = 320, h = 180;
        byte[] rgba = new byte[w * h * 4];
        VfxGraphWindow.FillDistortionPreviewRgba(rgba, w, h, settings);

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx", "m28-vfx-distortion.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, EncodePng(rgba, w, h));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    [Fact]
    public void Preview_WritesAttributeMapEvidencePng()
    {
        VfxCompiledSettings settings = VfxGraphCompiler.CompileSettings(VfxGraphTemplates.CreateLoop());
        settings.AttributeMapMode = VfxAttributeMap.ModePositionColor;
        settings.AttributeMapScale = 1.4f;

        int w = 320, h = 180;
        byte[] rgba = new byte[w * h * 4];
        VfxGraphWindow.FillAttributeMapPreviewRgba(rgba, w, h, settings);

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx", "m27-vfx-attribute-map.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, EncodePng(rgba, w, h));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    [Fact]
    public void Preview_WritesSpawnOverDistanceEvidencePng()
    {
        VfxCompiledSettings settings = VfxGraphCompiler.CompileSettings(VfxGraphTemplates.CreateLoop());
        settings.SpawnOverDistance = 8f;

        int w = 320, h = 180;
        byte[] rgba = new byte[w * h * 4];
        VfxGraphWindow.FillSpawnOverDistancePreviewRgba(rgba, w, h, settings);

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx", "m26-vfx-spawn-over-distance.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, EncodePng(rgba, w, h));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    [Fact]
    public void Preview_WritesPeriodicBurstEvidencePng()
    {
        VfxCompiledSettings settings = VfxGraphCompiler.CompileSettings(VfxGraphTemplates.CreateLoop());
        settings.PeriodicBurstCount = 4f;
        settings.PeriodicBurstPeriod = 0.25f;

        int w = 320, h = 180;
        byte[] rgba = new byte[w * h * 4];
        VfxGraphWindow.FillPeriodicBurstPreviewRgba(rgba, w, h, settings);

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx", "m44-vfx-periodic-burst.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, EncodePng(rgba, w, h));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    [Fact]
    public void Preview_WritesDepthCollisionEvidencePng()
    {
        VfxCompiledSettings settings = VfxGraphCompiler.CompileSettings(VfxGraphTemplates.CreateLoop());
        settings.DepthCollisionMode = VfxDepthCollision.ModeBounce;

        int w = 320, h = 180;
        byte[] rgba = new byte[w * h * 4];
        VfxGraphWindow.FillDepthCollisionPreviewRgba(rgba, w, h, settings);

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx", "m22-vfx-depth-collision.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, EncodePng(rgba, w, h));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    [Fact]
    public void Preview_WritesGpuEventEvidencePng()
    {
        VfxCompiledSettings settings = VfxGraphCompiler.CompileSettings(VfxGraphTemplates.CreateLoop());
        Assert.Equal(1f, settings.EventKind);
        Assert.Equal(2f, settings.EventCount);

        int w = 320, h = 180;
        byte[] rgba = new byte[w * h * 4];
        VfxGraphWindow.FillGpuEventPreviewRgba(rgba, w, h, settings);

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx", "m20-vfx-gpu-events.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, EncodePng(rgba, w, h));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    [Fact]
    public void Preview_WritesGpuEventTargetEvidencePng()
    {
        VfxCompiledSettings settings = VfxGraphCompiler.CompileSettings(VfxGraphTemplates.CreateLoop());
        settings.EventTarget = 1f;
        settings.EventTargetName = "ChildFx";
        settings.EventCount = 4f;

        int w = 320, h = 180;
        byte[] rgba = new byte[w * h * 4];
        VfxGraphWindow.FillGpuEventTargetPreviewRgba(rgba, w, h, settings);

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx", "m45-vfx-gpu-event-target.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, EncodePng(rgba, w, h));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    [Fact]
    public void Preview_WritesGpuEventAlwaysEvidencePng()
    {
        VfxCompiledSettings settings = VfxGraphCompiler.CompileSettings(VfxGraphTemplates.CreateLoop());
        settings.EventKind = VfxGpuEvent.KindAlways;
        settings.EventCount = 3f;

        int w = 320, h = 180;
        byte[] rgba = new byte[w * h * 4];
        VfxGraphWindow.FillGpuEventAlwaysPreviewRgba(rgba, w, h, settings);

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx", "m47-vfx-gpu-event-always.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, EncodePng(rgba, w, h));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    [Fact]
    public void Preview_WritesLitEvidencePng()
    {
        VfxGraphAsset graph = VfxGraphTemplates.CreateMesh();
        VfxCompiledSettings settings = VfxGraphCompiler.CompileSettings(graph);
        Assert.Equal(1f, settings.LitMode);

        int w = 320, h = 180;
        byte[] rgba = new byte[w * h * 4];
        VfxGraphWindow.FillLitPreviewRgba(rgba, w, h, settings);

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx", "m15-vfx-lit-particles.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, EncodePng(rgba, w, h));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    [Fact]
    public void Preview_WritesEvidencePng()
    {
        VfxCompiledSettings settings = VfxGraphCompiler.CompileSettings(VfxGraphTemplates.CreateLoop());
        int w = 320, h = 180;
        byte[] rgba = new byte[w * h * 4];
        VfxGraphWindow.FillPreviewRgba(rgba, w, h, settings);

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx", "m4-vfx-graph-window.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, EncodePng(rgba, w, h));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
    }

    [Fact]
    public void TryFillPreviewRgba_FallsBackToCpu_WithoutComputeDevice()
    {
        VfxGraphAsset graph = VfxGraphTemplates.CreateLoop();
        VfxCompiledSettings settings = VfxGraphCompiler.CompileSettings(graph);
        int w = 64, h = 36;
        byte[] rgba = new byte[w * h * 4];
        Assert.True(VfxGraphWindow.TryFillPreviewRgba(rgba, w, h, settings, graph, out bool usedGpu));
        if (!VfxGpuPreview.DeviceReady)
            Assert.False(usedGpu);
        Assert.NotEqual(0, rgba[0] | rgba[1] | rgba[2]);
    }

    [Fact]
    public void VisualEffect_Simulate_IsSafe_WithoutComputeDevice()
    {
        var go = new GameObject("vfx-sim");
        VisualEffect vfx = go.AddComponent<VisualEffect>();
        vfx.Graph = VfxGraphTemplates.CreateBurst();
        vfx.ApplyGraph();
        vfx.Play();
        vfx.Simulate(1f / 30f);
        Assert.True(vfx.IsPlaying);
        Assert.Equal(VfxGpuPreview.DeviceReady, vfx.UsesGpuParticles);
        go.Dispose();
    }

    [Fact]
    public void Preview_WritesGpuEvidencePng()
    {
        VfxGraphAsset graph = VfxGraphTemplates.CreateLoop();
        VfxCompiledSettings settings = VfxGraphCompiler.CompileSettings(graph);
        int w = 320, h = 180;
        byte[] rgba = new byte[w * h * 4];
        VfxGraphWindow.TryFillPreviewRgba(rgba, w, h, settings, graph, out bool usedGpu);

        string repo = FindRepoRoot();
        string path = Path.Combine(repo, "docs", "superpowers", "evidence", "vfx", "m8-vfx-gpu-preview.png");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, EncodePng(rgba, w, h));
        Assert.True(File.Exists(path));
        Assert.True(new FileInfo(path).Length > 256);
        _ = usedGpu;
    }

    private static string FindRepoRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "XEngine.sln")))
                return directory.FullName;
            directory = directory.Parent;
        }
        directory = new DirectoryInfo(Environment.CurrentDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "XEngine.sln")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("XEngine.sln");
    }

    private static byte[] EncodePng(byte[] rgba, int width, int height)
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
        byte[] crcBytes = new byte[4];
        WriteInt(crcBytes, 0, (int)(crc ^ 0xFFFFFFFF));
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

    private sealed class NullRenderer : ICanvasRenderer
    {
        public void Dispose() { }
        public object CreateTexture(uint width, uint height) => new Int2((int)width, (int)height);
        public Int2 GetTextureSize(object texture) => (Int2)texture;
        public void SetTextureData(object texture, IntRect bounds, byte[] data) { }
        public void RenderCalls(Canvas canvas, IReadOnlyList<DrawCall> drawCalls) { }
    }
}
