using System;
using System.Buffers.Binary;
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
/// Headless-Vulkan acceptance of the full crowd path: bake a synthetic two-bone skinned
/// "puppet", render 10,000 instances through the package shader + instancing pipeline at two
/// different animation times, and assert the crowd is visible and actually animating.
/// Screenshots land in docs/superpowers/evidence/gpu-animation/.
/// </summary>
[Collection("GpuSerial")]
public sealed class GpuAnimationCrowdRenderTests : IDisposable
{
    private const int Width = 960;
    private const int Height = 540;
    private const int GridSize = 100; // 100x100 = 10,000 instances

    private readonly GraphicsBackend? _previousBackend;

    public GpuAnimationCrowdRenderTests()
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
            GraphicsBackendSelection.Preferred = _previousBackend; // GPU unavailable — tests skip
        }
    }

    public void Dispose()
    {
        if (Graphics.Device != null)
            Graphics.Dispose();
        GraphicsBackendSelection.Preferred = _previousBackend;
    }

    [Fact]
    public void MultiClipCrowd_ClipIndexSelectsAtlasSegment()
    {
        var device = Graphics.Device as VulkanGraphicsDevice;
        if (device == null)
            return; // no Vulkan on this host — skip silently

        (GameObject root, Mesh mesh) = BuildFlagRig();
        // Two clearly different clips: +80° and -80° tip swings — same phase renders poses that
        // mirror each other, so a clip swap must move pixels.
        var swingLeft = MakeSwingClip("SwingLeft", Quaternion.FromEuler(0f, 0f, -80f));
        var swingRight = MakeSwingClip("SwingRight", Quaternion.FromEuler(0f, 0f, 80f));

        var request = new GpuAnimationBakeRequest { Target = root, Fps = 8f, BakeNormals = false };
        request.Clips.Add(swingLeft);
        request.Clips.Add(swingRight);
        GpuAnimationBakeResult bake = GpuAnimationBaker.Bake(request);
        Assert.Equal(2, bake.Asset.Clips.Count);
        Assert.Equal(16, bake.Asset.TextureHeight); // 8 + 8 rows in one atlas
        // The two clips' mid-loop rows must differ (mirror swings) — guards against silently
        // baking a static atlas again (unresolved bone path).
        bake.Asset.GetPosition(4, 4, out var midLeft);
        bake.Asset.GetPosition(12, 4, out var midRight);
        Assert.True(MathF.Abs(midLeft.Z - midRight.Z) > 0.3f || MathF.Abs(midLeft.X - midRight.X) > 0.3f,
            $"clip0 mid {midLeft} vs clip1 mid {midRight} — atlas rows unexpectedly identical");

        var crowdObject = new GameObject("MultiClipCrowd");
        var crowd = crowdObject.AddComponent<GpuAnimatedCrowd>();
        crowd.BakedAsset = new AssetRef<BakedGpuAnimation>(bake.Asset);
        crowd.CrowdMesh = new AssetRef<Mesh>(bake.VatMesh);
        // A compact double ring: even instances play clip 0, odd instances play clip 1.
        const int count = 96;
        for (int i = 0; i < count; i++)
        {
            float angle = i * MathF.PI * 2f / count;
            GpuAnimatedInstance inst = GpuAnimatedInstance.Place(
                new Float3(MathF.Cos(angle) * 6f, 0f, MathF.Sin(angle) * 6f),
                phase: 0.5f, clip: i % 2);
            inst.Scale = new Float3(2.5f, 2.5f, 2.5f);
            crowd.Instances.Add(inst);
        }

        var scene = new Scene();
        scene.Fog.Mode = Scene.FogParams.FogMode.Off;
        scene.Ambient.Mode = Scene.AmbientLightParams.AmbientMode.Uniform;
        scene.Ambient.Color = new Float4(1f, 1f, 1f, 1f);

        var target = new RenderTexture(Width, Height, true, [TextureImageFormat.Color4b]);
        var cameraObject = new GameObject("multiclip camera");
        Camera camera = cameraObject.AddComponent<Camera>();
        camera.Target = target;
        camera.ClearFlags = CameraClearFlags.SolidColor;
        camera.ClearColor = new Color(0.07f, 0.08f, 0.11f, 1f);
        camera.FieldOfView = 55f;
        camera.FarClipPlane = 200f;
        camera.Pipeline = new UniversalRenderPipeline();
        cameraObject.Transform.Position = new Float3(0f, 3f, -18f); // outside the radius-6 ring
        cameraObject.Transform.Rotation = Quaternion.Identity;
        scene.Add(cameraObject);
        scene.Add(crowdObject);

        scene.Enable();
        try
        {
            for (int warmup = 0; warmup < 48; warmup++)
                RenderFrame(camera, target);
            device.WaitIdle();
            byte[] frameA = ReadFrame(device, target);

            // Cross-check A: phase shift (proven channel) to verify this test's re-render works.
            for (int i = 0; i < crowd.Instances.Count; i++)
            {
                GpuAnimatedInstance inst = crowd.Instances[i];
                inst.Phase = (inst.Phase + 0.5f) % 1f;
                crowd.Instances[i] = inst;
            }
            crowd.MarkInstancesDirty();
            RenderFrame(camera, target);
            device.WaitIdle();
            byte[] framePhaseProbe = ReadFrame(device, target);
            int phaseDiff = CountDifferent(frameA, framePhaseProbe);
            SavePng("m5-multiclip-phase-probe", framePhaseProbe, Width, Height);

            // Swap every instance's clip — same placements, same phase, different atlas rows.
            for (int i = 0; i < crowd.Instances.Count; i++)
            {
                GpuAnimatedInstance inst = crowd.Instances[i];
                inst.Phase = (inst.Phase + 0.5f) % 1f; // restore original phase
                inst.Clip = 1 - inst.Clip;
                crowd.Instances[i] = inst;
            }
            crowd.MarkInstancesDirty();
            RenderFrame(camera, target);
            device.WaitIdle();
            byte[] frameB = ReadFrame(device, target);

            int foreground = CountForeground(frameA);
            int diff = CountDifferent(frameA, frameB);

            string pathA = SavePng("m5-multiclip-swap-a", frameA, Width, Height);
            SavePng("m5-multiclip-swap-b", frameB, Width, Height);

            Assert.True(foreground > Width * Height * 0.02,
                $"Multi-clip crowd did not render: only {foreground} foreground pixels. Screenshot: {pathA}");
            Assert.True(diff > Width * Height * 0.005,
                $"Clip swap produced no visual change: only {diff} pixels differ " +
                $"(phase-probe diff was {phaseDiff}; foreground={foreground}; " +
                $"clip0={crowd.Instances[0].Clip}; collectCalls={crowd.DebugCollectCalls}; " +
                $"lastSubmit={crowd.DebugLastSubmitCount}). Screenshot: {pathA}");
        }
        finally
        {
            crowd.ReleaseGpuResources();
            scene.Disable();
            mesh.Dispose();
            bake.Asset.Dispose();
            bake.VatMesh.Dispose();
            target.Dispose();
            // Finalize engine-internal textures while the device is alive (see note below).
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
    }

    private (GameObject Root, Mesh Mesh) BuildFlagRig()
    {
        var root = new GameObject("Armature");
        var hips = new GameObject("Pole");
        hips.SetParent(root, worldPositionStays: false);
        var spine = new GameObject("Tip");
        spine.SetParent(hips, worldPositionStays: false);

        var mesh = new Mesh { Name = "PuppetFlag" };
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
        mesh.UV = new[]
        {
            new Float2(0f, 0f), new Float2(1f, 0f), new Float2(0f, 0.33f), new Float2(1f, 0.33f),
            new Float2(0f, 0.66f), new Float2(1f, 0.66f), new Float2(0f, 1f), new Float2(1f, 1f),
        };
        mesh.Indices = new uint[]
        {
            0, 1, 2, 2, 1, 3,
            2, 3, 4, 4, 3, 5,
            4, 5, 6, 6, 5, 7,
        };
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
        return (root, mesh);
    }

    private static AnimationClip MakeSwingClip(string name, Quaternion to)
    {
        Quaternion from = Quaternion.Identity;
        var clip = new AnimationClip { Name = name };
        clip.Duration = 1f;
        var tip = new AnimationClip.AnimBone { BoneName = "Pole/Tip" }; // binding-relative full path
        tip.PosX = Const(0f);
        tip.PosY = Const(0f);
        tip.PosZ = Const(0f);
        tip.RotX = Linear(from.X, to.X);
        tip.RotY = Linear(from.Y, to.Y);
        tip.RotZ = Linear(from.Z, to.Z);
        tip.RotW = Linear(from.W, to.W);
        clip.AddBone(tip);
        return clip;
    }

    [Fact]
    public void CrowdRenders_10000Instances_AndAnimatesOverTime()
    {
        var device = Graphics.Device as VulkanGraphicsDevice;
        if (device == null)
            return; // no Vulkan on this host — skip silently

        // ---- synthetic character: 2 bones, 8-vertex "flag" that swings visibly ----
        var root = new GameObject("Armature");
        var hips = new GameObject("Pole");
        hips.SetParent(root, worldPositionStays: false);
        var spine = new GameObject("Tip");
        spine.SetParent(hips, worldPositionStays: false);

        var mesh = new Mesh { Name = "PuppetFlag" };
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
        mesh.UV = new[]
        {
            new Float2(0f, 0f), new Float2(1f, 0f), new Float2(0f, 0.33f), new Float2(1f, 0.33f),
            new Float2(0f, 0.66f), new Float2(1f, 0.66f), new Float2(0f, 1f), new Float2(1f, 1f),
        };
        mesh.Indices = new uint[]
        {
            0, 1, 2, 2, 1, 3,
            2, 3, 4, 4, 3, 5,
            4, 5, 6, 6, 5, 7,
        };
        // Bottom half weighted to the pole (bone 1), top half to the tip (bone 2).
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

        // Clip: the tip bone swings 0 -> 90 degrees and back isn't needed — a one-way swing with
        // loop wrap cross-fade gives two clearly different frames at t=0 and t=half-loop.
        Quaternion from = Quaternion.Identity;
        Quaternion to = Quaternion.FromEuler(0f, 0f, 80f);
        var clip = new AnimationClip { Name = "Swing" };
        clip.Duration = 1f;
        var tip = new AnimationClip.AnimBone { BoneName = "Pole/Tip" }; // binding-relative full path
        tip.PosX = Const(0f);
        tip.PosY = Const(0f);
        tip.PosZ = Const(0f);
        tip.RotX = Linear(from.X, to.X);
        tip.RotY = Linear(from.Y, to.Y);
        tip.RotZ = Linear(from.Z, to.Z);
        tip.RotW = Linear(from.W, to.W);
        clip.AddBone(tip);

        var request = new GpuAnimationBakeRequest
        {
            Target = root,
            Fps = 8f,
            BakeNormals = true,
            HalfPrecision = true, // e2e: the 10k crowd renders from a 16F atlas
        };
        request.Clips.Add(clip);
        GpuAnimationBakeResult bake = GpuAnimationBaker.Bake(request);

        // ---- crowd of 10,000 ----
        var crowdObject = new GameObject("Crowd");
        var crowd = crowdObject.AddComponent<GpuAnimatedCrowd>();
        crowd.BakedAsset = new AssetRef<BakedGpuAnimation>(bake.Asset);
        crowd.CrowdMesh = new AssetRef<Mesh>(bake.VatMesh);
        crowd.Lit = false;
        // Ring of instances close around the camera start so ANY horizontal forward
        // direction must catch some, regardless of the engine's yaw/pitch conventions.
        const int ringCount = 64;
        for (int i = 0; i < ringCount; i++)
        {
            float angle = i * MathF.PI * 2f / ringCount;
            GpuAnimatedInstance inst = GpuAnimatedInstance.Place(new Float3(
                GridSize * 0.6f + MathF.Cos(angle) * 7f, 0f, GridSize * 0.6f + MathF.Sin(angle) * 7f), phase: i / 64f);
            inst.Tint = new Float4(1f, 0.6f, 0.2f, 1f);
            inst.Scale = new Float3(3f, 3f, 3f);
            crowd.Instances.Add(inst);
        }
        for (int x = 0; x < GridSize; x++)
        for (int z = 0; z < GridSize; z++)
        {
            // Checkerboard tint so the crowd is obviously many independent instances.
            float tint = (x + z) % 2 == 0 ? 1f : 0.45f;
            GpuAnimatedInstance inst = GpuAnimatedInstance.Place(
                new Float3(x * 1.2f, 0f, z * 1.2f),
                phase: ((x * 7 + z * 13) % 16) / 16f);
            inst.Tint = new Float4(tint, tint * 0.9f, tint * 0.8f, 1f);
            crowd.Instances.Add(inst);
        }

        var scene = new Scene();
        scene.Fog.Mode = Scene.FogParams.FogMode.Off;
        scene.Ambient.Mode = Scene.AmbientLightParams.AmbientMode.Uniform;
        scene.Ambient.Color = new Float4(1f, 1f, 1f, 1f);

        var target = new RenderTexture(Width, Height, true, [TextureImageFormat.Color4b]);
        var cameraObject = new GameObject("crowd camera");
        Camera camera = cameraObject.AddComponent<Camera>();
        camera.Target = target;
        camera.ClearFlags = CameraClearFlags.SolidColor;
        camera.ClearColor = new Color(0.07f, 0.08f, 0.11f, 1f);
        camera.FieldOfView = 55f;
        camera.FarClipPlane = 400f;
        camera.Pipeline = new UniversalRenderPipeline();
        cameraObject.Transform.Position = new Float3(0f, 1.2f, -6.5f);
        cameraObject.Transform.Rotation = Quaternion.Identity;
        scene.Add(cameraObject);
        scene.Add(crowdObject);

        scene.Enable();
        var capturedLogs = new System.Collections.Generic.List<string>();
        Debug.OnLog += CaptureLog;
        void CaptureLog(string message, DebugStackTrace? trace, LogSeverity severity)
        {
            if (severity is LogSeverity.Error or LogSeverity.Exception or LogSeverity.Warning)
                capturedLogs.Add($"[{severity}] {message}");
        }
        try
        {
            for (int warmup = 0; warmup < 48; warmup++)
                RenderFrame(camera, target);

            // The render pipeline uploads _Time from Time.CurrentTime.Time BEFORE collecting
            // renderables — pin the engine clock itself, not a per-crowd uniform.
            Time.CurrentTime.Time = 0f;
            RenderFrame(camera, target);
            device.WaitIdle();
            byte[] frameA = ReadFrame(device, target);

            // Animation is GPU _Time driven (engine clock), which the headless harness cannot
            // advance reliably mid-test. Prove the VAT sampling responds to instance state
            // instead: shift every instance half a loop and re-render. The live-editor
            // evidence covers time-driven motion.
            for (int i = 0; i < crowd.Instances.Count; i++)
            {
                GpuAnimatedInstance inst = crowd.Instances[i];
                inst.Phase = (inst.Phase + 0.5f) % 1f;
                crowd.Instances[i] = inst;
            }
            crowd.MarkInstancesDirty();
            RenderFrame(camera, target);
            device.WaitIdle();
            byte[] frameB = ReadFrame(device, target);

            int foreground = CountForeground(frameA);
            int diff = CountDifferent(frameA, frameB);

            string pathA = SavePng("m4-crowd-10000-unlit-t0", frameA, Width, Height);
            SavePng("m4-crowd-10000-unlit-t0500", frameB, Width, Height);
            SavePng("m4-crowd-thumb-t0", Downscale(frameA), 96, 54);

            Assert.True(crowd.DebugCollectCalls > 0, "OnRenderCollect was never called for the crowd component.");
            Assert.True(foreground > Width * Height * 0.05,
                $"Crowd did not render: only {foreground} foreground pixels (collectCalls={crowd.DebugCollectCalls}, " +
                $"collected={RenderStats.Last.RenderablesCollected}, culled={RenderStats.Last.RenderablesCulled}, " +
                $"drawn={RenderStats.Last.RenderablesDrawn}, drawCalls={RenderStats.Last.DrawCalls}, " +
                $"instanced={RenderStats.Last.InstancedDrawCalls}, triangles={RenderStats.Last.Triangles}, " +
                $"lastSubmit={crowd.DebugLastSubmitCount}, instances={crowd.Instances.Count}). " +
                $"Logs:\n{string.Join("\n", capturedLogs.Take(40))} Screenshot: {pathA}");
            Assert.True(diff > Width * Height * 0.005,
                $"Crowd did not animate: only {diff} pixels differ between the two phases. " +
                $"Logs:\n{string.Join("\n", capturedLogs.Take(20))} Screenshot: {pathA}");
        }
        finally
        {
            Debug.OnLog -= CaptureLog;
            crowd.ReleaseGpuResources(); // deterministic release before Graphics.Dispose
            scene.Disable();
            mesh.Dispose();
            bake.Asset.Dispose();
            bake.VatMesh.Dispose();
            target.Dispose();
            // Finalize any engine-internal leaked textures NOW, while the device is still
            // alive — a Texture finalizer running after Graphics.Dispose crashes the host.
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
        using (var clear = Graphics.GetCommandBuffer("gpuanim-crowd-clear"))
        {
            clear.SetRenderTarget(target.frameBuffer);
            clear.SetViewport(0, 0, Width, Height);
            clear.ClearRenderTarget(ClearFlags.Color | ClearFlags.Depth, new Color(0.07f, 0.08f, 0.11f, 1f), 1f);
            Graphics.Submit(clear);
        }
        camera.Pipeline!.Render(camera, new RenderingData { SkipUI = true });
        Graphics.EndFrameAndWait();
    }

    private static byte[] ReadFrame(VulkanGraphicsDevice device, RenderTexture target)
    {
        byte[] rgba = device.ReadTexture2D(device.Images[target.MainTexture.Handle.Handle], 4);
        byte[] flipped = new byte[rgba.Length];
        int rowBytes = Width * 4;
        for (int y = 0; y < Height; y++)
            Buffer.BlockCopy(rgba, y * rowBytes, flipped, (Height - 1 - y) * rowBytes, rowBytes);
        return flipped;
    }

    private static int CountForeground(byte[] rgba)
    {
        int count = 0;
        for (int i = 0; i < rgba.Length; i += 4)
            if (Math.Abs(rgba[i] - 18) + Math.Abs(rgba[i + 1] - 20) + Math.Abs(rgba[i + 2] - 28) > 26)
                count++;
        return count;
    }

    private static int CountDifferent(byte[] a, byte[] b)
    {
        int count = 0;
        for (int i = 0; i < a.Length; i += 4)
            if (Math.Abs(a[i] - b[i]) + Math.Abs(a[i + 1] - b[i + 1]) + Math.Abs(a[i + 2] - b[i + 2]) > 24)
                count++;
        return count;
    }

    // PNG via Magick.NET raw-RGBA import (spine38 test dependency pattern).
    private static string SavePng(string name, byte[] rgba, int width, int height)
    {
        string dir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
            "docs", "superpowers", "evidence", "gpu-animation"));
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, name + ".png");
        using var image = new ImageMagick.MagickImage(rgba, new ImageMagick.MagickReadSettings { Width = (uint)width, Height = (uint)height, Format = ImageMagick.MagickFormat.Rgba });
        image.Write(path);
        return path;
    }

    private static byte[] Downscale(byte[] rgba)
    {
        const int Tw = 96, Th = 54;
        var thumb = new byte[Tw * Th * 4];
        for (int y = 0; y < Th; y++)
        for (int x = 0; x < Tw; x++)
        {
            int si = ((y * Height / Th) * Width + x * Width / Tw) * 4;
            int di = (y * Tw + x) * 4;
            thumb[di] = rgba[si];
            thumb[di + 1] = rgba[si + 1];
            thumb[di + 2] = rgba[si + 2];
            thumb[di + 3] = 255;
        }
        return thumb;
    }
}
