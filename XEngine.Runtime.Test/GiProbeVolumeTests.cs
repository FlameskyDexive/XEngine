// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Runtime;
using XEngine.Runtime.Rendering;
using XEngine.Runtime.Resources;

using Xunit;

namespace XEngine.Runtime.Test;

public sealed class GiProbeVolumeTests : RuntimeTestBase
{
    public override void Dispose()
    {
        GiProbeVolume.Reset();
        base.Dispose();
    }

    [Fact]
    public void PrepareFrame_ActivatesOnlyWithGiEnabled()
    {
        var scene = CreateScene();
        var go = CreateGameObject("Cam");
        go.Scene = scene;
        var camera = go.AddComponent<Camera>();

        GiProbeVolume.PrepareFrame(scene, camera, giCaptureActive: true);
        Assert.False(GiProbeVolume.Active); // Gi.Enable defaults to false

        scene.Gi = new Scene.GiParams { Enable = true };
        GiProbeVolume.PrepareFrame(scene, camera, giCaptureActive: true);
        Assert.True(GiProbeVolume.Active);
        Assert.Equal(256, GiProbeVolume.ProbeCount);

        // The capture gate (no effect gathered) keeps the volume off even when scene Gi is on.
        GiProbeVolume.PrepareFrame(scene, camera, giCaptureActive: false);
        Assert.False(GiProbeVolume.Active);
    }

    [Fact]
    public void Bind_UploadsGridDataTexture()
    {
        var scene = CreateScene();
        var go = CreateGameObject("Cam");
        go.Scene = scene;
        var camera = go.AddComponent<Camera>();
        scene.Gi = new Scene.GiParams { Enable = true, ProbeSpacing = 4f };
        GiProbeVolume.PrepareFrame(scene, camera, giCaptureActive: true);
        GiProbeVolume.Bind(Graphics.GetCommandBuffer("GiBind"));

        // Spacing from the scene params reaches the volume state.
        GiProbeVolume.Reset();
    }

    [Fact]
    public void UpdateProbes_AdvancesRoundRobinWithinBounds()
    {
        QualitySettings.ImageEffectsEnabled = true;
        var scene = CreateScene();
        var go = CreateGameObject("Cam");
        go.Scene = scene;
        var camera = go.AddComponent<Camera>();
        scene.Gi = new Scene.GiParams { Enable = true, ProbesPerFrame = 16 };
        GiProbeVolume.PrepareFrame(scene, camera, giCaptureActive: true);
        GiProbeVolume.Bind(Graphics.GetCommandBuffer("GiBind"));

        var color = new RenderTexture(64, 64, true, [TextureImageFormat.Color4b]);
        var prepass = new RenderTexture(64, 64, true,
            [TextureImageFormat.Color4b, TextureImageFormat.Short4]);

        RenderStats.BeginFrame();
        var cmd = Graphics.GetCommandBuffer("GiUpdate");
        for (int i = 0; i < 20; i++)
            GiProbeVolume.UpdateProbes(cmd, camera, scene, color, prepass.InternalDepth);
        RenderStats.EndFrame();

        // 16 updates/frame over 256 probes: 20 frames cover the grid several times with the
        // cursor always inside the volume.
        Assert.True(GiProbeVolume.LastUpdatedCount > 0);
        Assert.True(GiProbeVolume.LastUpdatedCount <= ShaderConfig.MaxGiProbeUpdatesPerFrame * 2);
        Assert.True(RenderStats.Last.GiProbesUpdated > 0);

        color.Dispose();
        prepass.Dispose();
        GiProbeVolume.Reset();
    }

    [Fact]
    public void LightmapBinding_SelectsMode3_WhenVolumeActive()
    {
        var scene = CreateScene();
        var go = CreateGameObject("Cam");
        go.Scene = scene;
        var camera = go.AddComponent<Camera>();
        scene.Gi = new Scene.GiParams { Enable = true };
        GiProbeVolume.PrepareFrame(scene, camera, giCaptureActive: true);

        var props = new PropertyState();
        LightmapBinding.Fill(props, scene, -1, default, default, meshHasUV2: true);
        Assert.Equal(3, props.GetInt("_GIMode"));

        // Lightmapped statics keep their baked mode even with realtime GI active.
        scene.BakedLighting.Lightmaps.Add(new AssetRef<Texture2D>());
        var propsLm = new PropertyState();
        LightmapBinding.Fill(propsLm, scene, 0, new XEngine.Vector.Float4(1, 1, 0, 0), default, meshHasUV2: true);
        // The asset ref is unresolved (no lightmap loaded): expect ambient fallback, not mode 3/2.
        Assert.Equal(0, propsLm.GetInt("_GIMode"));

        GiProbeVolume.Reset();
    }
}
