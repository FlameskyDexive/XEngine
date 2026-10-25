using System;

using Xunit;

using XEngine.GpuAnimation.Samples;
using XEngine.Runtime;
using XEngine.Runtime.Backends.Vulkan;
using XEngine.Runtime.RHI;
using XEngine.Runtime.Rendering;
using XEngine.Runtime.Resources;
using XEngine.Vector;

namespace XEngine.GpuAnimation.Tests;

/// <summary>
/// Drives the SHIPPED sample (Samples~/Crowd/Scripts/GpuAnimationCrowdSample.cs): its
/// procedural bake must produce a sane atlas, and its crowd must render through the real
/// pipeline. This pins the sample to the package's public API so it can't rot silently.
/// </summary>
[Collection("GpuSerial")]
public sealed class GpuAnimationSampleTests : IDisposable
{
    private const int Width = 640;
    private const int Height = 360;

    private readonly GraphicsBackend? _previousBackend;

    public GpuAnimationSampleTests()
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
    public void SampleBake_ProducesSaneAtlas()
    {
        var (asset, vatMesh, rigMesh, rig) = GpuAnimationCrowdSample.BuildFlagBake();
        using (asset)
        using (vatMesh)
        using (rigMesh)
        {
            Assert.Equal(8, asset.VertexCount);
            Assert.Equal(8, asset.TextureHeight); // 8 fps x 1 s
            Assert.Single(asset.Clips);
            // The tip swing must actually animate: first and last baked rows differ.
            asset.GetPosition(0, 4, out var row0);
            asset.GetPosition(7, 4, out var row7);
            Assert.True(
                MathF.Abs(row0.X - row7.X) > 0.2f || MathF.Abs(row0.Z - row7.Z) > 0.2f,
                $"sample clip rows unexpectedly static: {row0} vs {row7}");
            Assert.Equal(8, vatMesh.Vertices.Length);
            Assert.Equal(0.5f / 8f, vatMesh.UV2![0].X, 5); // texel-center UV2
        }
    }

    [Fact]
    public void SampleCrowd_RendersThroughPipeline()
    {
        var device = Graphics.Device as VulkanGraphicsDevice;
        if (device == null)
            return; // no Vulkan on this host — skip silently

        var sampleObject = new GameObject("Sample");
        var sample = sampleObject.AddComponent<GpuAnimationCrowdSample>();
        sample.GridSize = 40; // 1,600 instances keeps the test fast

        var scene = new Scene();
        scene.Fog.Mode = Scene.FogParams.FogMode.Off;
        scene.Ambient.Mode = Scene.AmbientLightParams.AmbientMode.Uniform;
        scene.Ambient.Color = new Float4(1f, 1f, 1f, 1f);

        var target = new RenderTexture(Width, Height, true, [TextureImageFormat.Color4b]);
        var cameraObject = new GameObject("sample camera");
        Camera camera = cameraObject.AddComponent<Camera>();
        camera.Target = target;
        camera.ClearFlags = CameraClearFlags.SolidColor;
        camera.ClearColor = new Color(0.07f, 0.08f, 0.11f, 1f);
        camera.FieldOfView = 55f;
        camera.FarClipPlane = 200f;
        camera.Pipeline = new UniversalRenderPipeline();
        cameraObject.Transform.Position = new Float3(24f, 2f, -12f);
        cameraObject.Transform.Rotation = Quaternion.Identity;
        scene.Add(cameraObject);
        scene.Add(sampleObject);
        scene.Enable();
        try
        {
            // Start() only runs in the game loop — invoke it directly (reflection keeps the
            // sample's lifecycle methods private instead of tying the test to a messaging API).
            var startMi = typeof(GpuAnimationCrowdSample).GetMethod("Start",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            startMi!.Invoke(sample, null);
            for (int warmup = 0; warmup < 48; warmup++)
                RenderFrame(camera, target);
            device.WaitIdle();
            byte[] frameA = ReadFrame(device, target);

            int foreground = 0;
            for (int i = 0; i < frameA.Length; i += 4)
                if (Math.Abs(frameA[i] - 18) + Math.Abs(frameA[i + 1] - 20) + Math.Abs(frameA[i + 2] - 28) > 26)
                    foreground++;

            SavePng("m9-sample-crowd", frameA);

            Assert.True(foreground > Width * Height * 0.05,
                $"sample crowd did not render: only {foreground} foreground pixels");
        }
        finally
        {
            var destroyMi = typeof(GpuAnimationCrowdSample).GetMethod("OnDestroy",
                System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance);
            destroyMi?.Invoke(sample, null);
            scene.Disable();
            target.Dispose();
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
    }

    private static void RenderFrame(Camera camera, RenderTexture target)
    {
        using (var clear = Graphics.GetCommandBuffer("gpuanim-sample-clear"))
        {
            clear.SetRenderTarget(target.frameBuffer);
            clear.SetViewport(0, 0, Width, Height);
            clear.ClearRenderTarget(ClearFlags.Color | ClearFlags.Depth, new Color(0.07f, 0.08f, 0.11f, 1f), 1f);
            Graphics.Submit(clear);
        }
        camera.Pipeline!.Render(camera, new RenderingData { SkipUI = true });
        Graphics.EndFrameAndWait();
    }

    private static void SavePng(string name, byte[] rgba)
    {
        string dir = System.IO.Path.GetFullPath(System.IO.Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..",
            "docs", "superpowers", "evidence", "gpu-animation"));
        System.IO.Directory.CreateDirectory(dir);
        using var image = new ImageMagick.MagickImage(rgba, new ImageMagick.MagickReadSettings
        {
            Width = (uint)Width,
            Height = (uint)Height,
            Format = ImageMagick.MagickFormat.Rgba,
        });
        image.Write(System.IO.Path.Combine(dir, name + ".png"));
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
}
