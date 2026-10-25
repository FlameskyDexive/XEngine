// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Runtime.Resources;
using XEngine.Vector;

using Material = XEngine.Runtime.Resources.Material;
using Shader = XEngine.Runtime.Resources.Shader;

namespace XEngine.Runtime.Rendering;

/// <summary>
/// Screen-space global illumination (Surface Cache GI, screen-space tier). Per-pixel
/// cosine-distributed rays gather 1-bounce diffuse irradiance from the opaque colour +
/// prepass (Depth/Normal/Motion), temporal accumulation reuses a persistent history, an
/// edge-aware bilateral denoises, and the composite re-modulates by the pipeline-captured
/// albedo/AO (<see cref="RenderContext.GiAlbedoTexture"/>) with a (1-metallic) diffuse factor.
/// See docs/superpowers/specs/2026-08-16-realtime-gi-surface-cache.md.
/// </summary>
public sealed class ScreenSpaceGlobalIlluminationEffect : ImageEffect
{
    public override RenderStage Stage => RenderStage.AfterOpaques;

    public override RenderFeatureRequirements Requirements =>
        RenderFeatureRequirements.Depth | RenderFeatureRequirements.Normals |
        (UseTemporal ? RenderFeatureRequirements.MotionVectors : RenderFeatureRequirements.None);

    /// <summary>GI contribution multiplier at composite.</summary>
    public float Intensity = 1f;

    /// <summary>Maximum bounce distance in world units. Larger = wider light spread, more leaks.</summary>
    public float Radius = 2.5f;

    /// <summary>Cosine-distributed rays per pixel (blue-noise dithered, temporally jittered).</summary>
    public int Rays = 2;

    /// <summary>Maximum depth-march steps per ray.</summary>
    public int Steps = 24;

    /// <summary>Accumulate irradiance over frames using motion vectors to converge the noise.</summary>
    public bool UseTemporal = true;

    /// <summary>How much irradiance history to keep (higher = cleaner, more ghosting on motion).</summary>
    [Range(0f, 1f)] public float TemporalResponse = 0.9f;

    /// <summary>Spatial bilateral blur radius. With temporal on, a small value is enough.</summary>
    public float BlurRadius = 1.0f;

    /// <summary>Resolution the irradiance is gathered at; composited back at full resolution.</summary>
    public EffectResolution Resolution = EffectResolution.Half;

    /// <summary>Debug overlay: 0 = off, 1 = irradiance, 2 = albedo capture, 3 = AO + ray hit rate.</summary>
    public int DebugView;

    private Material _mat;
    private Texture2D _noise;
    private RenderTexture _history;
    private bool _historyValid;
    private int _frameIndex;
    private Float2 _jitter;

    // Settings resolution (Bloom pattern, extended): active .volumeprofile overrides win for
    // spatial blending, then the camera-scene Gi params (EnvironmentPanel), then instance fields.
    internal GlobalIlluminationVolumeComponent VolumeStack =>
        UseVolumeOverrides ? VolumeManager.Instance.Get<GlobalIlluminationVolumeComponent>() : null!;

    private static Scene.GiParams SceneGi(Camera camera) => camera?.GameObject?.Scene?.Gi ?? default;

    internal float ResolvedIntensity(Camera camera)
    {
        var vol = VolumeStack;
        if (vol != null && vol.OverrideIntensity) return vol.Intensity;
        var gi = SceneGi(camera);
        if (gi.Enable) return gi.Intensity;
        return Intensity;
    }

    internal float ResolvedRadius(Camera camera)
    {
        var vol = VolumeStack;
        if (vol != null && vol.OverrideRadius) return vol.Radius;
        var gi = SceneGi(camera);
        if (gi.Enable) return gi.Radius;
        return Radius;
    }

    internal bool ResolvedUseTemporal(Camera camera)
    {
        var vol = VolumeStack;
        if (vol != null && vol.OverrideTemporal) return vol.UseTemporal;
        var gi = SceneGi(camera);
        if (gi.Enable) return gi.UseTemporal;
        return UseTemporal;
    }

    internal float ResolvedTemporalResponse(Camera camera)
    {
        var vol = VolumeStack;
        if (vol != null && vol.OverrideTemporalResponse) return vol.TemporalResponse;
        var gi = SceneGi(camera);
        if (gi.Enable) return gi.TemporalResponse;
        return TemporalResponse;
    }

    internal float ResolvedBlurRadius(Camera camera)
    {
        var vol = VolumeStack;
        if (vol != null && vol.OverrideBlur) return vol.BlurRadius;
        var gi = SceneGi(camera);
        if (gi.Enable) return gi.BlurRadius;
        return BlurRadius;
    }

    internal EffectResolution ResolvedResolution(Camera camera)
    {
        var vol = VolumeStack;
        int resolution;
        if (vol != null && vol.OverrideResolution)
            resolution = vol.Resolution;
        else
        {
            var gi = SceneGi(camera);
            if (!gi.Enable) return Resolution;
            resolution = gi.Resolution;
        }
        return resolution switch
        {
            0 => EffectResolution.Full,
            2 => EffectResolution.Quarter,
            _ => EffectResolution.Half,
        };
    }

    internal int ResolvedDebugView(Camera camera)
    {
        var vol = VolumeStack;
        if (vol != null && vol.OverrideDebugView) return vol.DebugView;
        var gi = SceneGi(camera);
        if (gi.Enable) return gi.DebugView;
        return DebugView;
    }

    public override void OnPreCull(Camera camera)
    {
        // Per-frame Halton offset scrolls the blue-noise dither so temporal accumulation converges.
        _jitter = new Float2(Halton(_frameIndex + 1, 2), Halton(_frameIndex + 1, 3));
        _frameIndex = (_frameIndex + 1) % 64;
    }

    public override void OnRenderEffect(RenderContext context)
    {
        // The pipeline only captures albedo while a realtime-GI effect is active; without it the
        // composite has nothing to modulate, so stay a no-op instead of rendering black GI.
        if (!context.DepthNormals.IsValid() || context.GiAlbedoTexture == null)
            return;

        _mat ??= new Material(Shader.LoadDefault(DefaultShader.SSGI));
        _noise ??= Texture2D.LoadDefault(DefaultTexture.Noise);

        var camera = context.Camera;
        bool useTemporal = ResolvedUseTemporal(camera);
        float scale = ResolvedResolution(camera).Scale();
        int width = Maths.Max(1, (int)(context.Width * scale));
        int height = Maths.Max(1, (int)(context.Height * scale));
        var formats = new[] { context.SceneColor.MainTexture.ImageFormat };

        // Persistent history at gather resolution; freed when temporal is off or on resize.
        if ((!useTemporal || (_history != null && (_history.Width != width || _history.Height != height))))
            ReleaseHistory();
        if (useTemporal)
            _history ??= new RenderTexture(width, height, false, formats);

        RenderTexture gatherRT = RenderTexture.GetTemporaryRT(width, height, false, formats);

        // Pass 0: gather — cosine rays march the depth buffer and sample the scene colour.
        _mat.SetFloat("_NumSteps", Steps);
        _mat.SetFloat("_Radius", ResolvedRadius(camera));
        _mat.SetInt("_GiRays", Rays);
        _mat.SetTexture("_Noise", _noise);
        _mat.SetVector("_NoiseScale", new Float2(width / (float)_noise.Width, height / (float)_noise.Height));
        _mat.SetVector("_JitterOffset", _jitter);
        using var cmd = Graphics.GetCommandBuffer("RealtimeGI");
        cmd.SetGlobalTexture("_CameraDepthTexture", context.DepthNormals.InternalDepth);
        cmd.SetGlobalTexture("_CameraNormalsTexture", context.DepthNormals.InternalTextures[0]);
        cmd.SetGlobalTexture("_GiAlbedoTexture", context.GiAlbedoTexture);
        cmd.Blit(context.SceneColor, gatherRT, _mat, 0);
        RenderStats.RecordGiPass();

        // Pass 1: temporal — reproject + neighbourhood-clamp against the history, then store
        // the (pre-blur) result for the next frame.
        RenderTexture gi = gatherRT;
        RenderTexture temporalRT = null;
        if (useTemporal)
        {
            if (_historyValid)
            {
                temporalRT = RenderTexture.GetTemporaryRT(width, height, false, formats);
                _mat.SetTexture("_PreviousBuffer", _history.MainTexture);
                _mat.SetTexture("_CameraMotionVectorsTexture", context.MotionVectors);
                _mat.SetFloat("_TResponse", ResolvedTemporalResponse(camera));
                cmd.Blit(gatherRT, temporalRT, _mat, 1);
                gi = temporalRT;
                RenderStats.RecordGiPass();
            }
            cmd.Blit(gi, _history, null, 0);
        }

        // Pass 2: spatial bilateral blur (edge-aware via depth + view-normal weights).
        RenderTexture blurTempRT = null;
        float blurRadius = ResolvedBlurRadius(camera);
        if (blurRadius > 0.01f)
        {
            blurTempRT = RenderTexture.GetTemporaryRT(width, height, false, formats);
            _mat.SetVector("_BlurDirection", new Float2(1.0f, 0.0f));
            _mat.SetFloat("_BlurRadius", blurRadius);
            cmd.Blit(gi, blurTempRT, _mat, 2);
            _mat.SetVector("_BlurDirection", new Float2(0.0f, 1.0f));
            cmd.Blit(blurTempRT, gi, _mat, 2);
            RenderStats.RecordGiPass();
        }

        // Pass 3: composite at full res — add irradiance * albedo * AO * (1-metallic) * Intensity.
        _mat.SetTexture("_GiTex", gi.MainTexture);
        _mat.SetFloat("_Intensity", ResolvedIntensity(camera));
        _mat.SetInt("_GiDebugView", ResolvedDebugView(camera));
        var temp = RenderTexture.GetTemporaryRT(context.Width, context.Height, false, formats);
        cmd.Blit(context.SceneColor, temp, _mat, 3);
        cmd.Blit(temp, context.SceneColor, null, 0);
        RenderStats.RecordGiPass();

        cmd.ClearGlobalTexture("_CameraDepthTexture");
        cmd.ClearGlobalTexture("_CameraNormalsTexture");
        cmd.ClearGlobalTexture("_GiAlbedoTexture");

        // Probe-volume refresh rides the same command buffer after the composite so its rays
        // sample the GI-composited colour of the current frame (one frame of latency at shade
        // time, which the EMA absorbs).
        GiProbeVolume.UpdateProbes(cmd, camera, camera?.GameObject?.Scene, context.SceneColor, context.DepthNormals.InternalDepth);

        Graphics.Submit(cmd);
        if (useTemporal) _historyValid = true;

        RenderTexture.ReleaseTemporaryRT(temp);
        if (blurTempRT != null) RenderTexture.ReleaseTemporaryRT(blurTempRT);
        if (temporalRT != null) RenderTexture.ReleaseTemporaryRT(temporalRT);
        RenderTexture.ReleaseTemporaryRT(gatherRT);
    }

    private void ReleaseHistory()
    {
        if (_history != null) _history.Dispose();
        _history = null;
        _historyValid = false;
    }

    public override void OnDisable()
    {
        if (_mat != null) _mat.Dispose();
        _mat = null;
        ReleaseHistory();
        _frameIndex = 0;
    }

    private static float Halton(int index, int b)
    {
        float r = 0f, f = 1f / b;
        int i = index;
        while (i > 0) { r += f * (i % b); i /= b; f /= b; }
        return r;
    }
}

/// <summary>
/// Toggles screen-space GI as a renderer feature. Requests Color+Depth+Normal/(Motion)
/// only while enabled. Does not replace LightBVH or Adaptive Probe Volumes.
/// </summary>
public sealed class ScreenSpaceGlobalIlluminationRendererFeature : RendererFeature
{
    public ScreenSpaceGlobalIlluminationEffect Effect { get; } = new();

    public override void Create() { }

    public override void AddRenderPasses(ScriptableRenderer renderer, ScriptableRenderContext context)
    {
        if (!QualitySettings.ImageEffectsEnabled)
            return;
        ConfigureInput(RequiredInputs(Effect.UseTemporal));
    }

    internal static ScriptableRenderPassInput RequiredInputs(bool useTemporal)
    {
        ScriptableRenderPassInput input =
            ScriptableRenderPassInput.Color
            | ScriptableRenderPassInput.Depth
            | ScriptableRenderPassInput.Normal;
        if (useTemporal)
            input |= ScriptableRenderPassInput.Motion;
        return input;
    }
}
