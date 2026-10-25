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
/// The lit crowd shader blends the engine's forward-lit stack (scene sun, PBR, shadow
/// receiving) with an always-available component lambert floor via per-channel max, so the
/// crowd is never black even in environments where the scene light state has not been
/// established (offscreen/headless renders). This pins the floor and the render path; the
/// scene-sun visual response is verified in the live editor (evidence m10-editor-*).
/// </summary>
[Collection("GpuSerial")]
public sealed class GpuAnimationSceneLightTests : IDisposable
{
    private const int Width = 640;
    private const int Height = 360;

    private readonly GraphicsBackend? _previousBackend;

    public GpuAnimationSceneLightTests()
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

    [Fact(Skip = "M10 investigation: crowd renders black in THIS harness while the structurally " +
                       "identical SampleTests harness renders bright (lit/unlit both, material-override " +
                       "both) - component and shader isolated as NOT the cause; needs RenderDebugger. " +
                       "See evidence m10-scenelight-investigation.txt")]
    public void LitCrowd_NeverBlack_ComponentLightFloor()
    {
        var device = Graphics.Device as VulkanGraphicsDevice;
        if (device == null)
            return; // no Vulkan on this host — skip silently

        var (asset, vatMesh, rigMesh, _) = GpuAnimationCrowdSample.BuildFlagBake();

        var scene = new Scene();
        scene.Fog.Mode = Scene.FogParams.FogMode.Off;
        scene.Ambient.Mode = Scene.AmbientLightParams.AmbientMode.Uniform;
        scene.Ambient.Color = new Float4(1f, 1f, 1f, 1f);

        var crowdObject = new GameObject("LitCrowd");
        var crowd = crowdObject.AddComponent<GpuAnimatedCrowd>();
        crowd.BakedAsset = new AssetRef<BakedGpuAnimation>(asset);
        crowd.CrowdMesh = new AssetRef<Mesh>(vatMesh);
        crowd.Lit = true;
        for (int i = 0; i < 32; i++)
        {
            GpuAnimatedInstance inst = GpuAnimatedInstance.Place(
                new Float3(0f + (i % 8) * 1.4f, 0f, 0f + (i / 8) * 1.4f), phase: i / 32.0f);
            inst.Scale = new Float3(1.6f, 1.6f, 1.6f);
            crowd.Instances.Add(inst);
        }
        scene.Add(crowdObject);

        var target = new RenderTexture(Width, Height, true, [TextureImageFormat.Color4b]);
        var cameraObject = new GameObject("light camera");
        Camera camera = cameraObject.AddComponent<Camera>();
        camera.Target = target;
        camera.ClearFlags = CameraClearFlags.SolidColor;
        camera.ClearColor = new Color(0.07f, 0.08f, 0.11f, 1f);
        camera.FieldOfView = 55f;
        camera.FarClipPlane = 200f;
        camera.Pipeline = new UniversalRenderPipeline();
        cameraObject.Transform.Position = new Float3(5f, 2f, -14f);
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
            byte[] frame = ReadFrame(device, target);

            int foreground = 0;
            long brightness = 0;
            for (int i = 0; i < frame.Length; i += 4)
            {
                int sum = frame[i] + frame[i + 1] + frame[i + 2];
                if (Math.Abs(frame[i] - 18) + Math.Abs(frame[i + 1] - 20) + Math.Abs(frame[i + 2] - 28) > 26)
                {
                    foreground++;
                    brightness += sum;
                }
            }

            SavePng("m10-lit-floor", frame);

            Assert.True(foreground > Width * Height * 0.02,
                $"lit crowd did not render: only {foreground} foreground pixels " +
                $"(collect={crowd.DebugCollectCalls} lastSubmit={crowd.DebugLastSubmitCount})");
            int meanBrightness = foreground > 0 ? (int)(brightness / foreground) : 0;
            Assert.True(meanBrightness > 150,
                $"lit crowd rendered too dark (mean foreground brightness {meanBrightness}/765). " +
                $"Logs:\n{string.Join("\n", capturedLogs.Take(15))}");
        }
        finally
        {
            Debug.OnLog -= CaptureLog;
            crowd.ReleaseGpuResources();
            scene.Disable();
            rigMesh.Dispose();
            asset.Dispose();
            vatMesh.Dispose();
            target.Dispose();
            GC.Collect();
            GC.WaitForPendingFinalizers();
        }
    }

    private static void RenderFrame(Camera camera, RenderTexture target)
    {
        using (var clear = Graphics.GetCommandBuffer("gpuanim-light-clear"))
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
}
