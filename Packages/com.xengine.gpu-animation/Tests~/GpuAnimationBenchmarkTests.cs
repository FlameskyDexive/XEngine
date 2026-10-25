using System;
using System.Diagnostics;
using System.IO;

using Xunit;

using XEngine.Runtime;
using XEngine.Runtime.Backends.Vulkan;
using XEngine.Runtime.RHI;
using XEngine.Runtime.Rendering;
using XEngine.Runtime.Resources;
using XEngine.Vector;

using AnimationCurve = XEngine.Runtime.AnimationCurve;

namespace XEngine.GpuAnimation.Tests;

/// <summary>
/// Warm frame-time benchmark for the VAT crowd at several sizes (0 / 10k / 30k instances) plus
/// bake cost and per-frame upload bytes. Writes a machine-readable report into the package
/// evidence folder; numbers feed docs/benchmarks/rendering/.
/// </summary>
[Collection("GpuSerial")]
public sealed class GpuAnimationBenchmarkTests : IDisposable
{
    private const int Width = 960;
    private const int Height = 540;

    private readonly GraphicsBackend? _previousBackend;

    public GpuAnimationBenchmarkTests()
    {
        _previousBackend = GraphicsBackendSelection.Preferred;
        GraphicsBackendSelection.Preferred = GraphicsBackend.Vulkan;
        try
        {
            Graphics.Initialize(debug: false);
            GlobalUniforms.Initialize();
        }
        catch (Exception)
        {
            GraphicsBackendSelection.Preferred = _previousBackend;
        }
    }

    public void Dispose()
    {
        if (Graphics.Device != null)
            Graphics.Dispose();
        GraphicsBackendSelection.Preferred = _previousBackend;
    }

    [Fact]
    public void CrowdFrameTime_ByInstanceCount()
    {
        var device = Graphics.Device as VulkanGraphicsDevice;
        if (device == null)
            return; // no Vulkan on this host — skip silently

        var report = new System.Text.StringBuilder();
        report.AppendLine("# M7 gpu-animation crowd benchmark (headless Vulkan, " +
                          $"{Width}x{Height}, URP forward, unlit VAT crowd)");
        report.AppendLine("warmup=30 measured=60 frames per size; frame = clear + render + EndFrameAndWait");

        // ---- rig + bake ----
        var root = new GameObject("Armature");
        var hips = new GameObject("Pole");
        hips.SetParent(root, worldPositionStays: false);
        var spine = new GameObject("Tip");
        spine.SetParent(hips, worldPositionStays: false);

        var mesh = new Mesh { Name = "BenchFlag" };
        mesh.Vertices = new[]
        {
            new Float3(-0.5f, 0f, 0f), new Float3(0.5f, 0f, 0f), new Float3(-0.5f, 1f, 0f), new Float3(0.5f, 1f, 0f),
            new Float3(-0.5f, 2f, 0f), new Float3(0.5f, 2f, 0f), new Float3(-0.5f, 3f, 0f), new Float3(0.5f, 3f, 0f),
        };
        mesh.Normals = new[]
        {
            new Float3(0f, 0f, 1f), new Float3(0f, 0f, 1f), new Float3(0f, 0f, 1f), new Float3(0f, 0f, 1f),
            new Float3(0f, 0f, 1f), new Float3(0f, 0f, 1f), new Float3(0f, 0f, 1f), new Float3(0f, 0f, 1f),
        };
        mesh.Indices = new uint[] { 0, 1, 2, 2, 1, 3, 2, 3, 4, 4, 3, 5, 4, 5, 6, 6, 5, 7 };
        mesh.BoneIndices = new[]
        {
            new Float4(1, 0, 0, 0), new Float4(1, 0, 0, 0), new Float4(1, 0, 0, 0), new Float4(1, 0, 0, 0),
            new Float4(2, 0, 0, 0), new Float4(2, 0, 0, 0), new Float4(2, 0, 0, 0), new Float4(2, 0, 0, 0),
        };
        mesh.BoneWeights = new[]
        {
            new Float4(1, 0, 0, 0), new Float4(1, 0, 0, 0), new Float4(1, 0, 0, 0), new Float4(1, 0, 0, 0),
            new Float4(1, 0, 0, 0), new Float4(1, 0, 0, 0), new Float4(1, 0, 0, 0), new Float4(1, 0, 0, 0),
        };
        mesh.BindPoses = new[] { Float4x4.Identity, Float4x4.Identity };
        var smr = root.AddComponent<SkinnedMeshRenderer>();
        smr.SharedMesh = new AssetRef<Mesh>(mesh);
        smr.SetBones(new[] { hips.Transform, spine.Transform }, root.Transform);

        Quaternion from = Quaternion.Identity;
        Quaternion to = Quaternion.FromEuler(0f, 0f, 70f);
        var clip = new AnimationClip { Name = "Swing", Duration = 1f };
        var tip = new AnimationClip.AnimBone { BoneName = "Pole/Tip" };
        tip.PosX = Const(0f);
        tip.PosY = Const(0f);
        tip.PosZ = Const(0f);
        tip.RotX = Linear(from.X, to.X);
        tip.RotY = Linear(from.Y, to.Y);
        tip.RotZ = Linear(from.Z, to.Z);
        tip.RotW = Linear(from.W, to.W);
        clip.AddBone(tip);

        var bakeWatch = Stopwatch.StartNew();
        var request = new GpuAnimationBakeRequest { Target = root, Fps = 8f, BakeNormals = false };
        request.Clips.Add(clip);
        GpuAnimationBakeResult bake = GpuAnimationBaker.Bake(request);
        bakeWatch.Stop();
        report.AppendLine($"bake: 8-vertex rig, 1 clip @8fps (8 rows) => {bakeWatch.Elapsed.TotalMilliseconds:0.##} ms");
        report.AppendLine($"atlas: {bake.Asset.TextureWidth}x{bake.Asset.TextureHeight} RGBA32F " +
                          $"= {bake.Asset.TextureWidth * bake.Asset.TextureHeight * 16 / 1024.0:0.##} KB GPU");

        var scene = new Scene();
        scene.Fog.Mode = Scene.FogParams.FogMode.Off;
        scene.Ambient.Mode = Scene.AmbientLightParams.AmbientMode.Uniform;
        scene.Ambient.Color = new Float4(1f, 1f, 1f, 1f);

        var target = new RenderTexture(Width, Height, true, [TextureImageFormat.Color4b]);
        var cameraObject = new GameObject("bench camera");
        Camera camera = cameraObject.AddComponent<Camera>();
        camera.Target = target;
        camera.ClearFlags = CameraClearFlags.SolidColor;
        camera.ClearColor = new Color(0.07f, 0.08f, 0.11f, 1f);
        camera.FieldOfView = 55f;
        camera.FarClipPlane = 400f;
        camera.Pipeline = new UniversalRenderPipeline();
        cameraObject.Transform.Position = new Float3(0f, 2f, -30f);
        cameraObject.Transform.Rotation = Quaternion.Identity;
        scene.Add(cameraObject);

        var crowdObject = new GameObject("BenchCrowd");
        var crowd = crowdObject.AddComponent<GpuAnimatedCrowd>();
        crowd.BakedAsset = new AssetRef<BakedGpuAnimation>(bake.Asset);
        crowd.CrowdMesh = new AssetRef<Mesh>(bake.VatMesh);
        scene.Add(crowdObject);

        scene.Enable();
        try
        {
            int[] sizes = { 0, 10_000, 30_000 };
            foreach (int count in sizes)
            {
                crowd.Instances.Clear();
                int side = (int)MathF.Ceiling(MathF.Sqrt(count));
                for (int x = 0; x < side; x++)
                for (int z = 0; z < side; z++)
                {
                    if (crowd.Instances.Count >= count) break;
                    crowd.Instances.Add(GpuAnimatedInstance.Place(
                        new Float3(x * 1.2f, 0f, z * 1.2f), phase: ((x * 7 + z * 13) % 16) / 16f));
                }
                crowd.MarkInstancesDirty();

                for (int warmup = 0; warmup < 30; warmup++)
                    RenderFrame(camera, target);
                device.WaitIdle();

                var watch = Stopwatch.StartNew();
                for (int frame = 0; frame < 60; frame++)
                    RenderFrame(camera, target);
                device.WaitIdle();
                watch.Stop();

                double msPerFrame = watch.Elapsed.TotalMilliseconds / 60.0;
                int batches = (count + 1022) / 1023;
                report.AppendLine(
                    $"crowd {count,6} instances: {msPerFrame,7:0.###} ms/frame | {batches,3} instanced draw(s) | " +
                    $"instance buffer {count * 80 / 1024.0:0.#} KB/frame | fps≈{1000.0 / msPerFrame,6:0}");
            }

            // Rebuild (dirty) cost for 30k: payload build on the next collect.
            var rebuildWatch = Stopwatch.StartNew();
            var (_, _, custom, _) = GpuAnimatedCrowd.BuildInstancePayloads(bake.Asset, crowd.Instances);
            rebuildWatch.Stop();
            Assert.Equal(30_000, custom.Length);
            report.AppendLine($"CPU payload rebuild for 30k instances: {rebuildWatch.Elapsed.TotalMilliseconds:0.##} ms " +
                              "(only when MarkInstancesDirty is called; steady-state per-frame CPU = one float)");

            string dir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
                "docs", "superpowers", "evidence", "gpu-animation"));
            Directory.CreateDirectory(dir);
            File.WriteAllText(Path.Combine(dir, "m7-benchmark.txt"), report.ToString());
        }
        finally
        {
            crowd.ReleaseGpuResources();
            scene.Disable();
            mesh.Dispose();
            bake.Asset.Dispose();
            bake.VatMesh.Dispose();
            target.Dispose();
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
    }

    private static AnimationCurve Const(float v) => new(new[] { new KeyFrame(0, v) });

    private static AnimationCurve Linear(float v0, float v1)
    {
        var c = new AnimationCurve(new[] { new KeyFrame(0, v0), new KeyFrame(1f, v1) });
        c.SmoothTangents(CurveTangent.Linear);
        return c;
    }

    private static void RenderFrame(Camera camera, RenderTexture target)
    {
        using (var clear = Graphics.GetCommandBuffer("gpuanim-bench-clear"))
        {
            clear.SetRenderTarget(target.frameBuffer);
            clear.SetViewport(0, 0, Width, Height);
            clear.ClearRenderTarget(ClearFlags.Color | ClearFlags.Depth, new Color(0.07f, 0.08f, 0.11f, 1f), 1f);
            Graphics.Submit(clear);
        }
        camera.Pipeline!.Render(camera, new RenderingData { SkipUI = true });
        Graphics.EndFrameAndWait();
    }
}
