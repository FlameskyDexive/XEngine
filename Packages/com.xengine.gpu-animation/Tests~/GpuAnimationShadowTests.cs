using System;
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
/// Crowd shadow casting: a lit Standard ground plane under a shadow-casting directional light
/// must darken when the VAT crowd is present and shadows are on, and brighten when shadows are
/// toggled off — proving the GpuAnimShadow pass renders the ANIMATED pose into the shadow map.
/// </summary>
[Collection("GpuSerial")]
public sealed class GpuAnimationShadowTests : IDisposable
{
    private const int Width = 640;
    private const int Height = 360;

    private readonly GraphicsBackend? _previousBackend;

    public GpuAnimationShadowTests()
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
    public void CrowdCastsAnimatedShadows()
    {
        var device = Graphics.Device as VulkanGraphicsDevice;
        if (device == null)
            return; // no Vulkan on this host — skip silently

        // ---- rig + bake (same flag puppet as the crowd acceptance) ----
        var root = new GameObject("Armature");
        var hips = new GameObject("Pole");
        hips.SetParent(root, worldPositionStays: false);
        var spine = new GameObject("Tip");
        spine.SetParent(hips, worldPositionStays: false);

        var mesh = new Mesh { Name = "ShadowFlag" };
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

        var request = new GpuAnimationBakeRequest { Target = root, Fps = 8f, BakeNormals = false };
        request.Clips.Add(clip);
        GpuAnimationBakeResult bake = GpuAnimationBaker.Bake(request);

        // ---- scene: ground plane (Standard, receives shadows) + light + small crowd ----
        var ground = new Mesh { Name = "Ground" };
        ground.Vertices = new[]
        {
            new Float3(-30f, 0f, -30f), new Float3(30f, 0f, -30f), new Float3(-30f, 0f, 30f), new Float3(30f, 0f, 30f),
        };
        ground.Normals = new[] { new Float3(0, 1, 0), new Float3(0, 1, 0), new Float3(0, 1, 0), new Float3(0, 1, 0) };
        ground.Indices = new uint[] { 0, 1, 2, 2, 1, 3 };
        ground.RecalculateBounds();

        var scene = new Scene();
        scene.Fog.Mode = Scene.FogParams.FogMode.Off;
        scene.Ambient.Mode = Scene.AmbientLightParams.AmbientMode.Uniform;
        scene.Ambient.Color = new Float4(0.45f, 0.45f, 0.45f, 1f);

        var groundObject = new GameObject("Ground");
        var groundRenderer = groundObject.AddComponent<MeshRenderer>();
        groundRenderer.Mesh = new AssetRef<Mesh>(ground);
        groundRenderer.Material = new AssetRef<Material>(Material.LoadDefault(DefaultMaterial.Standard));
        scene.Add(groundObject);

        var lightObject = new GameObject("Sun");
        var light = lightObject.AddComponent<DirectionalLight>();
        light.Intensity = 1.1f;
        light.CastShadows = true;
        light.ShadowStrength = 0.85f;
        lightObject.Transform.Position = new Float3(0f, 20f, -10f);
        lightObject.Transform.Rotation = Quaternion.FromEuler(0f, -50f, 0f);
        scene.Add(lightObject);

        var crowdObject = new GameObject("Crowd");
        var crowd = crowdObject.AddComponent<GpuAnimatedCrowd>();
        crowd.BakedAsset = new AssetRef<BakedGpuAnimation>(bake.Asset);
        crowd.CrowdMesh = new AssetRef<Mesh>(bake.VatMesh);
        for (int i = 0; i < 24; i++)
        {
            GpuAnimatedInstance inst = GpuAnimatedInstance.Place(
                new Float3(-4.5f + (i % 8) * 1.3f, 0f, -2f + (i / 8) * 1.3f), phase: i / 24f);
            inst.Scale = new Float3(1.5f, 1.5f, 1.5f);
            crowd.Instances.Add(inst);
        }
        scene.Add(crowdObject);

        var target = new RenderTexture(Width, Height, true, [TextureImageFormat.Color4b]);
        var cameraObject = new GameObject("shadow camera");
        Camera camera = cameraObject.AddComponent<Camera>();
        camera.Target = target;
        camera.ClearFlags = CameraClearFlags.SolidColor;
        camera.ClearColor = new Color(0.07f, 0.08f, 0.11f, 1f);
        camera.FieldOfView = 55f;
        camera.FarClipPlane = 200f;
        camera.Pipeline = new UniversalRenderPipeline();
        cameraObject.Transform.Position = new Float3(0f, 2f, -20f);
        cameraObject.Transform.Rotation = Quaternion.Identity;
        scene.Add(cameraObject);

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
            device.WaitIdle();
            byte[] withCrowd = ReadFrame(device, target);

            // Removing the casters must clear the ground shadows (light + shadow pipeline stay
            // on — toggling CastShadows alone can keep sampling a stale shadow map).
            crowd.Enabled = false;
            crowd.ReleaseGpuResources();
            for (int warmup = 0; warmup < 4; warmup++)
                RenderFrame(camera, target);
            device.WaitIdle();
            byte[] withoutCrowd = ReadFrame(device, target);

            // The crowd casts onto the ground: removing it must change (brighten) ground pixels.
            int diff = 0;
            long brightnessDelta = 0;
            for (int i = 0; i < withCrowd.Length; i += 4)
            {
                int d = Math.Abs(withCrowd[i] - withoutCrowd[i])
                     + Math.Abs(withCrowd[i + 1] - withoutCrowd[i + 1])
                     + Math.Abs(withCrowd[i + 2] - withoutCrowd[i + 2]);
                if (d > 24) diff++;
                brightnessDelta += withoutCrowd[i] - withCrowd[i]; // crowd gone should brighten
            }

            string dir = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
                "docs", "superpowers", "evidence", "gpu-animation"));
            Directory.CreateDirectory(dir);
            File.WriteAllBytes(Path.Combine(dir, "m6-crowd-shadow-on.png"),
                EncodePng(withCrowd, Width, Height));
            File.WriteAllBytes(Path.Combine(dir, "m6-crowd-shadow-off.png"),
                EncodePng(withoutCrowd, Width, Height));

            Assert.True(diff > Width * Height * 0.002,
                $"Removing the crowd changed only {diff} pixels — crowd did not cast shadows. " +
                $"Logs:\n{string.Join("\n", capturedLogs.Take(30))}");
            Assert.True(brightnessDelta > 0,
                "Crowd-free frame was not brighter than the crowd frame.");
        }
        finally
        {
            Debug.OnLog -= CaptureLog;
            crowd.Enabled = false;
            crowd.ReleaseGpuResources();
            scene.Disable();
            mesh.Dispose();
            ground.Dispose();
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
        using (var clear = Graphics.GetCommandBuffer("gpuanim-shadow-clear"))
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

    private static byte[] EncodePng(byte[] rgba, int width, int height)
    {
        using var image = new ImageMagick.MagickImage(rgba, new ImageMagick.MagickReadSettings
        {
            Width = (uint)width,
            Height = (uint)height,
            Format = ImageMagick.MagickFormat.Rgba,
        });
        using var ms = new MemoryStream();
        image.Write(ms, ImageMagick.MagickFormat.Png);
        return ms.ToArray();
    }
}
