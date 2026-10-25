// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using XEngine.Echo;
using XEngine.Runtime.Rendering;

namespace XEngine.Runtime;

/// <summary>
/// Per-camera Universal pipeline overrides (Unity <c>UniversalAdditionalCameraData</c>).
/// Render type and camera stack stay on <see cref="Camera"/>; this component holds post,
/// shadows, and anti-aliasing. Missing component keeps historical defaults (post and
/// shadows on) so existing scenes and pixel tests stay unchanged.
/// </summary>
[AddComponentMenu("Rendering/Universal Additional Camera Data")]
[ComponentIcon("\uf03e")]
[RequireComponent(typeof(Camera))]
public sealed class UniversalAdditionalCameraData : MonoBehaviour
{
    [Header("Rendering")]
    [Tooltip("Draw this camera's shadow casters into the shadow atlas.")]
    [SerializeField] public bool RenderShadows = true;

    [Tooltip("Run post-process ImageEffects and Volume-driven Bloom / Tonemap / DoF.")]
    [SerializeField] public bool RenderPostProcessing;

    [Tooltip("Camera anti-aliasing. None defers to the pipeline asset / QualitySettings.")]
    [SerializeField] public CameraAntialiasing Antialiasing = CameraAntialiasing.None;

    [Tooltip("Force a depth prepass / CopyDepth even when no effect requested it.")]
    [SerializeField] public bool RequiresDepthTexture;

    public override void OnValidate()
    {
        int aa = (int)Antialiasing;
        if (aa < 0 || aa > 3)
            Antialiasing = CameraAntialiasing.None;
    }

    /// <summary>Existing component, or null when the camera has none.</summary>
    public static UniversalAdditionalCameraData? Find(Camera? camera)
        => camera == null ? null : camera.GetComponent<UniversalAdditionalCameraData>();

    /// <summary>Add the component if missing (editor create / inspector, Unity GetOrAdd).</summary>
    public static UniversalAdditionalCameraData GetOrAdd(Camera camera)
    {
        ArgumentNullException.ThrowIfNull(camera);
        UniversalAdditionalCameraData? existing = Find(camera);
        if (existing != null)
            return existing;

        UniversalAdditionalCameraData added = camera.AddComponent<UniversalAdditionalCameraData>();
        // Match missing-component defaults so inspector/menu GetOrAdd does not silently
        // turn post off on cameras that previously had no additional data.
        added.RenderPostProcessing = true;
        added.RenderShadows = true;
        return added;
    }

    /// <summary>True when no component is present (legacy cameras) or post is enabled.</summary>
    public static bool ShouldRenderPostProcessing(Camera? camera)
    {
        UniversalAdditionalCameraData? data = Find(camera);
        return data == null || data.RenderPostProcessing;
    }

    /// <summary>True when no component is present (legacy cameras) or shadows are enabled.</summary>
    public static bool ShouldRenderShadows(Camera? camera)
    {
        UniversalAdditionalCameraData? data = Find(camera);
        return data == null || data.RenderShadows;
    }
}

/// <summary>Camera-level AA. None uses <see cref="UniversalRenderPipelineAsset.Antialiasing"/> / QualitySettings.</summary>
public enum CameraAntialiasing : byte
{
    None = 0,
    FXAA = 1,
    SMAA = 2,
    TAA = 3,
}
