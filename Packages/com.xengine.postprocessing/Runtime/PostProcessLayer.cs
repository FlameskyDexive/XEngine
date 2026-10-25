// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Collections.Generic;

using XEngine.Echo;
using XEngine.PostProcessing.Effects;
using XEngine.Runtime;
using XEngine.Runtime.Rendering;
using XEngine.Vector;

namespace XEngine.PostProcessing;

/// <summary>
/// Camera component that blends <see cref="PostProcessVolume"/> profiles and injects the
/// matching engine <see cref="ImageEffect"/> instances into <see cref="UniversalRenderPipeline"/>.
/// </summary>
[AddComponentMenu("Rendering/Post-process Layer")]
[ExecuteAlways]
[Preserve]
[RequireComponent(typeof(Camera))]
[ComponentIcon("\uf03e")]
public sealed class PostProcessLayer : MonoBehaviour, ICameraImageEffectSource
{
    public enum Antialiasing
    {
        None,
        FastApproximateAntialiasing,
        SubpixelMorphologicalAntialiasing,
        TemporalAntialiasing,
    }

    [Tooltip("Transform that drives local volume blending. Null uses this camera.")]
    [SerializeField] public Transform? volumeTrigger;

    [Tooltip("Which GameObject layers contribute volumes.")]
    [SerializeField] public LayerMask volumeLayer = LayerMask.Everything;

    [SerializeField] public Antialiasing antialiasingMode = Antialiasing.None;

    [Tooltip("Stop applying the stack just before tonemap / color grading (EXR export).")]
    [SerializeField] public bool breakBeforeColorGrading;

    private readonly BloomEffect _bloom = new() { Enabled = false, UseVolumeOverrides = false };
    private readonly TonemapperEffect _tonemap = new() { Enabled = false, UseVolumeOverrides = false };
    private readonly BokehDepthOfFieldEffect _dof = new() { Enabled = false, UseVolumeOverrides = false };
    private readonly MotionBlurEffect _motionBlur = new() { Enabled = false, UseVolumeOverrides = false };
    private readonly CinematicEffects _cinematic = new() { Enabled = false, UseVolumeOverrides = false };
    private readonly AutoExposureEffect _autoExposure = new() { Enabled = false, UseVolumeOverrides = false };
    private readonly GTAOEffect _ao = new() { Enabled = false, UseVolumeOverrides = false };
    private readonly ScreenSpaceReflectionEffect _ssr = new() { Enabled = false, UseVolumeOverrides = false };
    private readonly FXAAEffect _fxaa = new() { Enabled = false, UseVolumeOverrides = false };
    private readonly SMAAEffect _smaa = new() { Enabled = false, UseVolumeOverrides = false };
    private readonly TAAEffect _taa = new() { Enabled = false, UseVolumeOverrides = false };

    public void Prepare(Camera camera)
    {
        if (!Enabled || camera == null)
        {
            DisableOwnedEffects();
            return;
        }

        Transform trigger = volumeTrigger != null ? volumeTrigger : camera.Transform;
        PostProcessManager.instance.Update(trigger.Position, volumeLayer);
        ApplyStack();
    }

    public void CollectImageEffects(List<ImageEffect> destination)
    {
        if (!Enabled)
            return;

        AddIfEnabled(destination, _ao);
        AddIfEnabled(destination, _ssr);
        AddIfEnabled(destination, _autoExposure);
        AddIfEnabled(destination, _dof);
        AddIfEnabled(destination, _motionBlur);
        AddIfEnabled(destination, _bloom);
        AddIfEnabled(destination, _cinematic);
        if (!breakBeforeColorGrading)
            AddIfEnabled(destination, _tonemap);
        switch (antialiasingMode)
        {
            case Antialiasing.FastApproximateAntialiasing:
                _fxaa.Enabled = true;
                destination.Add(_fxaa);
                break;
            case Antialiasing.SubpixelMorphologicalAntialiasing:
                _smaa.Enabled = true;
                destination.Add(_smaa);
                break;
            case Antialiasing.TemporalAntialiasing:
                _taa.Enabled = true;
                destination.Add(_taa);
                break;
        }
    }

    public override void OnDisable()
    {
        DisableOwnedEffects();
        _bloom.OnDisable();
        _tonemap.OnDisable();
        _dof.OnDisable();
        _motionBlur.OnDisable();
        _cinematic.OnDisable();
        _autoExposure.OnDisable();
        _ao.OnDisable();
        _ssr.OnDisable();
        _fxaa.OnDisable();
        _smaa.OnDisable();
        _taa.OnDisable();
    }

    private void ApplyStack()
    {
        PostProcessManager manager = PostProcessManager.instance;

        Bloom bloom = manager.Get<Bloom>();
        _bloom.Enabled = bloom.IsEnabledAndSupported();
        if (_bloom.Enabled)
        {
            _bloom.Intensity = bloom.intensity.value;
            _bloom.Threshold = bloom.threshold.value;
            _bloom.Iterations = Maths.Clamp((int)MathF.Round(bloom.diffusion.value), 1, 8);
        }

        ColorGrading grading = manager.Get<ColorGrading>();
        bool gradingOn = grading.IsEnabledAndSupported();
        _tonemap.Enabled = gradingOn && grading.tonemapper.value != ColorGradingTonemapper.None
            && !breakBeforeColorGrading;
        if (_tonemap.Enabled)
        {
            _tonemap.Type = MapTonemapper(grading.tonemapper.value);
            _tonemap.Contrast = grading.tonemapContrast.value;
            _tonemap.Saturation = grading.tonemapSaturation.value;
        }

        Vignette vignette = manager.Get<Vignette>();
        ChromaticAberration chromatic = manager.Get<ChromaticAberration>();
        Grain grain = manager.Get<Grain>();
        bool vignetteOn = vignette.IsEnabledAndSupported();
        bool chromaticOn = chromatic.IsEnabledAndSupported();
        bool grainOn = grain.IsEnabledAndSupported();
        _cinematic.Enabled = vignetteOn || chromaticOn || grainOn || gradingOn;
        _cinematic.EnableVignette = vignetteOn;
        _cinematic.VignetteIntensity = vignette.intensity.value;
        _cinematic.VignetteSmoothness = vignette.smoothness.value;
        _cinematic.VignetteRoundness = vignette.roundness.value;
        _cinematic.EnableChromaticAberration = chromaticOn;
        _cinematic.ChromaticIntensity = chromatic.intensity.value;
        _cinematic.ChromaticDistortion = chromatic.distortion.value;
        _cinematic.EnableFilmGrain = grainOn;
        _cinematic.GrainIntensity = grain.intensity.value;
        _cinematic.GrainResponse = grain.response.value;
        _cinematic.EnableColorGrading = gradingOn;
        _cinematic.PostExposure = grading.postExposure.value;
        _cinematic.Contrast = grading.contrast.value;
        _cinematic.Saturation = grading.saturation.value;
        _cinematic.Temperature = grading.temperature.value;
        _cinematic.Lift = grading.lift.value;
        _cinematic.Gamma = grading.gamma.value;
        _cinematic.Gain = grading.gain.value;

        DepthOfField dof = manager.Get<DepthOfField>();
        _dof.Enabled = dof.IsEnabledAndSupported();
        if (_dof.Enabled)
        {
            _dof.UseAutoFocus = dof.useAutoFocus.value;
            _dof.ManualFocusPoint = dof.focusDistance.value;
            _dof.FocusStrength = dof.aperture.value;
            _dof.MaxBlurRadius = dof.focalLength.value;
        }

        MotionBlur motion = manager.Get<MotionBlur>();
        _motionBlur.Enabled = motion.IsEnabledAndSupported();
        if (_motionBlur.Enabled)
        {
            _motionBlur.Intensity = motion.shutterAngle.value / 270f;
            _motionBlur.Samples = motion.sampleCount.value;
            _motionBlur.MaxBlurRadius = motion.maxBlurRadius.value;
        }

        AutoExposure exposure = manager.Get<AutoExposure>();
        _autoExposure.Enabled = exposure.IsEnabledAndSupported();
        if (_autoExposure.Enabled)
        {
            _autoExposure.ExposureCompensation = exposure.keyValue.value;
            _autoExposure.AdaptSpeedUp = exposure.speedUp.value;
            _autoExposure.AdaptSpeedDown = exposure.speedDown.value;
            _autoExposure.MinExposure = exposure.minLuminance.value;
            _autoExposure.MaxExposure = exposure.maxLuminance.value;
        }

        AmbientOcclusion ao = manager.Get<AmbientOcclusion>();
        _ao.Enabled = ao.IsEnabledAndSupported();
        if (_ao.Enabled)
        {
            _ao.Intensity = ao.intensity.value;
            _ao.Radius = ao.radius.value;
            _ao.Slices = ao.slices.value;
            _ao.DirectionSamples = ao.samples.value;
            _ao.UseTemporal = ao.useTemporal.value;
        }

        ScreenSpaceReflections ssr = manager.Get<ScreenSpaceReflections>();
        _ssr.Enabled = ssr.IsEnabledAndSupported();
        if (_ssr.Enabled)
        {
            _ssr.RayDistance = ssr.maximumIterationCount.value;
            _ssr.UseTemporal = ssr.useTemporal.value;
            _ssr.ScreenFadeSize = ssr.vignette.value;
        }

        _fxaa.Enabled = false;
        _smaa.Enabled = false;
        _taa.Enabled = false;
    }

    private void DisableOwnedEffects()
    {
        _bloom.Enabled = false;
        _tonemap.Enabled = false;
        _dof.Enabled = false;
        _motionBlur.Enabled = false;
        _cinematic.Enabled = false;
        _autoExposure.Enabled = false;
        _ao.Enabled = false;
        _ssr.Enabled = false;
        _fxaa.Enabled = false;
        _smaa.Enabled = false;
        _taa.Enabled = false;
    }

    private static void AddIfEnabled(List<ImageEffect> destination, ImageEffect effect)
    {
        if (effect.Enabled)
            destination.Add(effect);
    }

    private static TonemapperType MapTonemapper(ColorGradingTonemapper type) => type switch
    {
        ColorGradingTonemapper.ACES => TonemapperType.ACES,
        ColorGradingTonemapper.Neutral => TonemapperType.ReinhardLuma,
        _ => TonemapperType.AgX,
    };
}
