// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using XEngine.Runtime.Rendering;
using XEngine.Runtime.Resources;
using XEngine.Runtime.RHI;
using XEngine.Vector;

using Xunit;

namespace XEngine.Runtime.Test;

/// <summary>
/// Pixel-level A/B acceptance for the clustered light list (P3.2) on a real Vulkan device:
/// renders the same multi-point-light scene through the full UniversalRenderPipeline once with
/// <see cref="SceneLightSystem.ClusteredLightingEnabled"/> on (cluster lookup path) and once off
/// (per-fragment BVH walk), and asserts the images match. The cluster list is a conservative
/// superset of the BVH result, so any divergence beyond float sum-order noise is a bug.
/// Skips when no Vulkan device is available.
/// </summary>
public sealed class ClusterLightingPixelTests : IDisposable
{
    private const int Width = 128;
    private const int Height = 128;

    private readonly GraphicsBackend? _previousBackend;

    public ClusterLightingPixelTests()
    {
        _previousBackend = GraphicsBackendSelection.Preferred;
        GraphicsBackendSelection.Preferred = GraphicsBackend.Vulkan;
        try
        {
            Graphics.Initialize(debug: false);
        }
        catch (Exception ex) when (IsExpectedGpuUnavailable(ex))
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
    public void Optional_ClusteredLighting_MatchesBvhPath_OrSkip()
    {
        if (Graphics.Device is not Backends.Vulkan.VulkanGraphicsDevice device)
            return; // Vulkan unavailable on this host — skip

        bool previousEnabled = SceneLightSystem.ClusteredLightingEnabled;
        Scene scene = null;
        RenderTexture target = null;
        Mesh cube = null;
        Material material = null;
        try
        {
            scene = new Scene();
            scene.Skybox.Mode = Scene.SkyboxMode.SolidColor;
            scene.Fog.Mode = Scene.FogParams.FogMode.Off;
            scene.Ambient.Mode = Scene.AmbientLightParams.AmbientMode.Uniform;
            scene.Ambient.Strength = 0.15f;
            scene.Ambient.Color = new Float4(1f, 1f, 1f, 1f);

            var cameraObject = new GameObject("Cluster A/B camera");
            Camera camera = cameraObject.AddComponent<Camera>();
            target = new RenderTexture(Width, Height, true, [TextureImageFormat.Color4b]);
            camera.Target = target;
            camera.ClearFlags = CameraClearFlags.SolidColor;
            camera.ClearColor = new Color(0.02f, 0.02f, 0.05f, 1f);
            camera.Pipeline = new UniversalRenderPipeline();
            cameraObject.Transform.Position = new Float3(0f, 3f, -9f);
            cameraObject.Transform.LocalEulerAngles = new Float3(15f, 0f, 0f);
            scene.Add(cameraObject);

            cube = Mesh.CreateCube(Float3.One);
            material = new Material(Shader.LoadDefault(DefaultShader.Standard));
            material.SetColor("_MainColor", new Color(0.8f, 0.8f, 0.8f, 1f));

            // A small field of cubes so many fragments receive several overlapping lights.
            for (int x = -2; x <= 2; x++)
            {
                for (int z = 0; z <= 4; z++)
                {
                    var cubeObject = new GameObject($"Cube {x} {z}");
                    MeshRenderer renderer = cubeObject.AddComponent<MeshRenderer>();
                    renderer.Mesh = cube;
                    renderer.Material = material;
                    cubeObject.Transform.Position = new Float3(x * 2.2f, 0f, z * 2.2f);
                    scene.Add(cubeObject);
                }
            }

            // Deterministic point light set (no shadows so the atlas path stays out of the A/B).
            var rng = new Random(4242);
            for (int i = 0; i < 12; i++)
            {
                var lightObject = new GameObject($"Point {i}");
                PointLight light = lightObject.AddComponent<PointLight>();
                light.CastShadows = false;
                light.Range = 2.5f + (float)rng.NextDouble() * 3.5f;
                light.Intensity = 0.4f + (float)rng.NextDouble() * 0.8f;
                light.Color = new Color(
                    0.3f + (float)rng.NextDouble() * 0.7f,
                    0.3f + (float)rng.NextDouble() * 0.7f,
                    0.3f + (float)rng.NextDouble() * 0.7f,
                    1f);
                lightObject.Transform.Position = new Float3(
                    ((float)rng.NextDouble() - 0.5f) * 10f,
                    0.8f + (float)rng.NextDouble() * 2f,
                    (float)rng.NextDouble() * 10f);
                scene.Add(lightObject);
            }

            scene.Enable();

            byte[] Render()
            {
                Graphics.BeginFrame();
                camera.Pipeline!.Render(camera, new RenderingData { SkipUI = true });
                Graphics.EndFrameAndWait();
                Graphics.Device!.WaitIdle();
                return device.ReadTexture2D(device.Images[target.MainTexture.Handle.Handle], 4);
            }

            SceneLightSystem lightSystem = UniversalRenderPipeline.GetOrCreateLightSystem(scene);

            SceneLightSystem.ClusteredLightingEnabled = true;
            byte[] clusteredFirst = Render();
            Assert.True(lightSystem.Clusters.Enabled, "cluster list must be enabled after a clustered render");
            Assert.True(lightSystem.Clusters.EntryCount > 0,
                "cluster build must emit entries when local lights are active");
            byte[] clusteredSecond = Render();

            SceneLightSystem.ClusteredLightingEnabled = false;
            byte[] bvh = Render();
            Assert.False(lightSystem.Clusters.Enabled, "cluster list must be disabled on the BVH A/B leg");

            // The clustered render must be deterministic frame to frame.
            Assert.Equal(clusteredFirst, clusteredSecond);

            // Sanity: the scene is actually lit (not the all-black failure mode).
            long luminance = 0;
            for (int i = 0; i < clusteredFirst.Length; i += 4)
                luminance += clusteredFirst[i] + clusteredFirst[i + 1] + clusteredFirst[i + 2];
            Assert.True(luminance > Width * Height * 3, $"scene appears unlit (luminance {luminance})");

            // A/B: identical modulo float sum-order (cluster list order differs from the BVH
            // DFS order, so allow 1 LSB per channel; require zero pixels beyond that).
            int mismatched = 0;
            int maxDelta = 0;
            for (int i = 0; i < bvh.Length; i++)
            {
                int delta = Math.Abs(bvh[i] - clusteredFirst[i]);
                if (delta > maxDelta) maxDelta = delta;
                if (delta > 1) mismatched++;
            }
            Assert.True(mismatched == 0,
                $"clustered vs BVH lighting diverged: {mismatched} channels beyond +/-1 (max delta {maxDelta})");
        }
        finally
        {
            SceneLightSystem.ClusteredLightingEnabled = previousEnabled;
            scene?.Dispose();
            material?.Dispose();
            cube?.Dispose();
            target?.Dispose();
        }
    }

    private static bool IsExpectedGpuUnavailable(Exception ex) =>
        ex is PlatformNotSupportedException or DllNotFoundException or InvalidOperationException or NotSupportedException;
}
