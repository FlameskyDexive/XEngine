// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using XEngine.Runtime.Resources;
using XEngine.Vector;

namespace XEngine.Runtime.Rendering;

/// <summary>
/// Per-scene owner of the static and dynamic light BVHs and their GPU-side mirrors. The render
/// pipeline calls <see cref="Reconcile"/> once per frame with the lights collected from the
/// scene; this class adds / removes / refits as needed and uploads only the dirty rows of each
/// texture.
///
/// <para>
/// One directional light is treated specially: it never enters either BVH and is uploaded via
/// the existing cascade-shadow uniform path on the render pipeline.
/// </para>
///
/// <para>
/// A bounded number of point + spot lights win shadow atlas slots each frame, picked by camera
/// distance. Lights that miss the cut still light surfaces; they just sample as unshadowed.
/// </para>
/// </summary>
public sealed class SceneLightSystem : IDisposable
{
    /// <summary>How many local lights (point + spot combined) can have shadow atlas slots in a
    /// single frame. Bump <see cref="ShaderConfig.MaxShadowCasters"/> (C# + ShaderLibrary) together.</summary>
    public const int MaxShadowCasters = ShaderConfig.MaxShadowCasters;

    /// <summary>
    /// Master switch for the clustered light-list path (P3.2). When false (or when a camera's
    /// cluster build is skipped) the shaders fall back to the per-fragment BVH walk via the
    /// runtime enabled flag in <c>_ClusterGridParams.w</c>.
    ///
    /// <para>
    /// Defaults to <c>false</c>: measured on Vulkan the clustered path is a wash-to-slightly-worse
    /// against the existing rope-BVH once the per-camera CPU build (0.35-1.7 ms depending on light
    /// count) is counted, so enabling it by default would be a small net regression. See
    /// <c>docs/benchmarks/2026-08-19-p32-clustered-light-lists/</c> for the A/B data and the
    /// analysis of why (XEngine's BVH already does tight sphere leaf tests with early-out
    /// traversal, unlike the naive per-light loop that Forward+ clustering normally replaces).
    /// The path is kept, verified pixel-identical, and flag-switchable so a future GPU-side
    /// (compute shader) cluster build can be dropped in without redoing the shader work.
    /// </para>
    /// </summary>
    public static bool ClusteredLightingEnabled;

    private readonly LightBVH _staticBVH = new();
    private readonly LightBVH _dynamicBVH = new();
    private readonly LightBVHTextures _staticTex = new();
    private readonly LightBVHTextures _dynamicTex = new();
    private readonly ClusterLightList _clusters = new();
    private Texture2D _emptyBvhTexture;
    private RenderTexture _emptyShadowTarget;

    // Tracking which BVH each registered light currently lives in so we can detect static<->dynamic
    // transitions and removals.
    private readonly Dictionary<IRenderableLight, Membership> _membership = new();
    private readonly HashSet<IRenderableLight> _seenThisFrame = new();

    // Per-frame results.
    private IRenderableLight _directional;
    private readonly List<IRenderableLight> _shadowCasters = new();
    private readonly List<(IRenderableLight light, float distSq)> _localCandidates = new();
    private readonly List<IRenderableLight> _toRemove = new();
    private readonly List<Light> _cookieLights = new();
    private readonly List<Light> _cookieLightsLast = new();
    private static readonly Comparison<(IRenderableLight light, float distSq)> s_nearestFirst =
        static (a, b) => a.distSq.CompareTo(b.distSq);

    public LightBVH StaticBVH => _staticBVH;
    public LightBVH DynamicBVH => _dynamicBVH;
    public LightBVHTextures StaticTextures => _staticTex;
    public LightBVHTextures DynamicTextures => _dynamicTex;
    public ClusterLightList Clusters => _clusters;

    /// <summary>The directional light selected this frame, or null. Render pipeline takes this
    /// for cascade shadows + the directional uniform slot.</summary>
    public IRenderableLight Directional => _directional;

    /// <summary>Lights that won shadow atlas slots this frame, in order. The pipeline calls
    /// <c>RenderShadows</c> on each.</summary>
    public IReadOnlyList<IRenderableLight> ShadowCasters => _shadowCasters;

    /// <summary>
    /// True when this frame needs a shadow atlas clear + shadow pass (directional with shadows
    /// and/or any selected local shadow casters). Empty scenes skip the 4K/8K depth clear.
    /// </summary>
    public bool NeedsShadowAtlas
    {
        get
        {
            if (!QualitySettings.ShadowsEnabled)
                return false;
            if (_shadowCasters.Count > 0)
                return true;
            return _directional != null && _directional.DoCastShadows();
        }
    }

    private enum Membership { Static, Dynamic }

    /// <summary>
    /// Walk this frame's lights, register / unregister with the appropriate BVH, refit dynamics,
    /// pick the directional + closest-N shadow casters, and upload only the dirty rows of each
    /// texture. Cheap when nothing changed.
    ///
    /// <para>
    /// Note on <paramref name="cullingMask"/>: per-camera light filtering by GameObject layer is
    /// not applied here. The BVH is a per-scene structure. Lighting membership uses the separate
    /// 8-bit <see cref="RenderingLayerMask"/> packed into each leaf (shader AND with the
    /// object's mask). Camera <see cref="LayerMask"/> still culls renderers, not lights.
    /// </para>
    /// </summary>
    public void Reconcile(IReadOnlyList<IRenderableLight> lights, Float3 cameraPos, LayerMask cullingMask)
    {
        _ = cullingMask; // see remark above
        _seenThisFrame.Clear();
        _directional = null;
        _shadowCasters.Clear();
        _localCandidates.Clear();
        _toRemove.Clear();
        _cookieLights.Clear();

        IRenderableLight bestDirectional = null;

        for (int i = 0; i < lights.Count; i++)
        {
            var light = lights[i];
            if (light == null) continue;

            // Fully-baked lights live entirely in the lightmap + probes excluded from the realtime
            // set. (Mixed lights stay realtime only their indirect bounce is baked.)
            if (light is Light bakedLight && bakedLight.BakeMode == LightBakeMode.Baked)
                continue;

            if (light.GetLightType() == LightType.Directional)
            {
                bestDirectional ??= light;
                continue;
            }

            _seenThisFrame.Add(light);
            var data = light.GetForwardLightData();
            data.RenderingLayerMask = light.GetRenderingLayerMask();

            if (light is Light cookieLight && cookieLight.ResolvedCookie != null)
                _cookieLights.Add(cookieLight);

            bool isStatic = IsStaticLight(light);
            if (_membership.TryGetValue(light, out Membership current))
            {
                // Handle static<->dynamic transition.
                if (current == Membership.Static && !isStatic)
                {
                    _staticBVH.Remove(light);
                    _dynamicBVH.Add(light, in data);
                    _membership[light] = Membership.Dynamic;
                }
                else if (current == Membership.Dynamic && isStatic)
                {
                    _dynamicBVH.Remove(light);
                    _staticBVH.Add(light, in data);
                    _membership[light] = Membership.Static;
                }
                else if (current == Membership.Dynamic)
                {
                    // Refit / topology check happens inside Update; unchanged data is a no-op.
                    _dynamicBVH.Update(light, in data);
                }
                else
                {
                    // Static + still static: pose is frozen; layer bits can still change.
                    _staticBVH.SetRenderingLayers(light, data.RenderingLayerMask);
                }
            }
            else
            {
                if (isStatic)
                {
                    _staticBVH.Add(light, in data);
                    _membership[light] = Membership.Static;
                }
                else
                {
                    _dynamicBVH.Add(light, in data);
                    _membership[light] = Membership.Dynamic;
                }
            }

            // Track for shadow-caster selection.
            if (light.DoCastShadows())
            {
                float dSq = (float)Float3.DistanceSquared(cameraPos, light.GetLightPosition());
                _localCandidates.Add((light, dSq));
            }
        }

        _directional = bestDirectional;

        // Second pass: drop registrations for lights that didn't show up.
        // Iterate over a snapshot since we mutate _membership inside the loop.
        if (_membership.Count > _seenThisFrame.Count)
        {
            foreach (var kv in _membership)
                if (!_seenThisFrame.Contains(kv.Key))
                    _toRemove.Add(kv.Key);
            foreach (var light in _toRemove)
            {
                if (_membership[light] == Membership.Static) _staticBVH.Remove(light);
                else _dynamicBVH.Remove(light);
                _membership.Remove(light);
            }
        }

        // Pick closest-N shadow casters. Reset every registered light's slot to -1 first so
        // anything that lost its slot this frame samples as unshadowed. We only need to touch
        // slots whose current value disagrees with the new assignment.
        _localCandidates.Sort(s_nearestFirst);

        int casterCount = Math.Min(MaxShadowCasters, _localCandidates.Count);
        for (int i = 0; i < casterCount; i++)
        {
            var l = _localCandidates[i].light;
            _shadowCasters.Add(l);
            int slot = i;
            if (_membership.TryGetValue(l, out var m))
            {
                var bvh = m == Membership.Static ? _staticBVH : _dynamicBVH;
                bvh.SetShadowSlot(l, slot);
            }
        }
        // Clear stale slots on lights that were shadow casters last frame but aren't now.
        for (int i = casterCount; i < _localCandidates.Count; i++)
        {
            var l = _localCandidates[i].light;
            if (_membership.TryGetValue(l, out var m))
                (m == Membership.Static ? _staticBVH : _dynamicBVH).SetShadowSlot(l, -1);
        }

        AssignLightCookies();

        // Build / refit and upload. Static rebuild happens only when add/remove/transition fired
        // above; dynamic rebuilds when refit invariant breaks.
        _staticBVH.Sync();
        _dynamicBVH.Sync();
        _staticTex.Sync(_staticBVH);
        _dynamicTex.Sync(_dynamicBVH);
    }

    private static bool IsStaticLight(IRenderableLight light)
    {
        // Honour the GameObject.IsStatic flag when the light is a MonoBehaviour-backed Light.
        // Custom IRenderableLight implementations default to dynamic.
        return light is Light l && l.GameObject != null && l.GameObject.IsStatic;
    }

    /// <summary>
    /// Render shadow maps for the directional light and the selected closest-N point / spot
    /// shadow casters into the shared shadow atlas. The pipeline calls this after binding the
    /// shadow framebuffer.
    /// </summary>
    public void RenderShadows(RenderPipeline pipeline, Float3 cameraPosition, IReadOnlyList<IRenderable> renderables,
        ShadowCasterSet casterSet = ShadowCasterSet.All, bool reuseTiles = false)
    {
        // Each light manages its own CommandBuffer(s) internally point lights submit
        // one per face, directional submits one per cascade, spot submits a single CB
        // so per-face matrix uploads via AssignCameraMatrices are ordered correctly
        // against that face's draws.
        if (_directional is Light dl)
            dl.RenderShadows(pipeline, cameraPosition, renderables, casterSet, reuseTiles);

        for (int i = 0; i < _shadowCasters.Count; i++)
        {
            if (_shadowCasters[i] is Light sc)
                sc.RenderShadows(pipeline, cameraPosition, renderables, casterSet, reuseTiles);
        }
    }

    /// <summary>
    /// Upload all uniforms touched by <c>Lighting.glsl</c> and <c>LightBVH.glsl</c>: the four
    /// BVH textures, the directional light slot, the cascade shadow data, and the shadow atlas
    /// arrays for the selected closest-N point + spot lights. Call after <see cref="Reconcile"/>
    /// and <see cref="RenderShadows"/>, before any forward draws.
    /// </summary>
    public void UploadGlobalUniforms()
    {
        EnsureFallbackTextures();

        LightCookieManager.Instance.BlitIfNeeded();

        // All of these are global-uniform writes. Routing each through its own one-op
        // CommandBuffer (the PropertyState.SetGlobalX helpers) meant ~80-100 rent/submit
        // cycles per camera per frame. Encode them all into a single buffer and submit once.
        using var cmd = Graphics.GetCommandBuffer("LightUniforms");
        UploadBVHTextures(cmd);
        _clusters.BindUniforms(cmd, _emptyBvhTexture);
        UploadDirectionalLight(cmd);
        cmd.SetGlobalInt("_SoftShadowTaps", SoftShadowQualityUtil.TapCount(QualitySettings.SoftShadowQuality));
        UploadLocalShadowSlots(cmd);
        LightCookieManager.Instance.BindGlobals(cmd);
        ScreenSpaceShadowsRendererFeature.BindDisabled(cmd);
        Graphics.Submit(cmd);
    }

    /// <summary>
    /// Camera-aware variant of <see cref="UploadGlobalUniforms()"/>: additionally builds this
    /// camera's clustered light list (P3.2) from the reconciled BVHs and binds it. The build is
    /// per camera because clusters live in the camera's NDC / view-depth space. When
    /// <see cref="ClusteredLightingEnabled"/> is false, or the scene has no local lights, the
    /// enabled flag uploads as 0 and shaders take the BVH fallback path.
    /// </summary>
    public void UploadGlobalUniforms(in RenderPipeline.CameraSnapshot css)
    {
        if (ClusteredLightingEnabled)
        {
            long start = System.Diagnostics.Stopwatch.GetTimestamp();
            Float4x4 viewProjection = css.Projection * css.View;
            _clusters.Build(
                _staticBVH, _dynamicBVH, in viewProjection,
                css.CameraPosition, css.CameraForward,
                css.NearClipPlane, css.FarClipPlane);
            RenderStats.RecordLightClusterBuild(
                (float)((System.Diagnostics.Stopwatch.GetTimestamp() - start) * 1000.0 / System.Diagnostics.Stopwatch.Frequency),
                _clusters.EntryCount);
        }
        else
        {
            _clusters.Disable();
        }

        UploadGlobalUniforms();
    }

    private void UploadBVHTextures(CommandBuffer cmd)
    {
        // Explicit APIs require every reflected texture descriptor to be valid even when the
        // corresponding root is -1 and the shader will not sample it.
        cmd.SetGlobalTexture("_StaticLightData", _staticTex.LightDataTexture ?? _emptyBvhTexture);
        cmd.SetGlobalTexture("_StaticLightNodes", _staticTex.NodeDataTexture ?? _emptyBvhTexture);
        cmd.SetGlobalTexture("_DynamicLightData", _dynamicTex.LightDataTexture ?? _emptyBvhTexture);
        cmd.SetGlobalTexture("_DynamicLightNodes", _dynamicTex.NodeDataTexture ?? _emptyBvhTexture);

        cmd.SetGlobalInt("_StaticLightTexSize", _staticTex.LightTextureSize);
        cmd.SetGlobalInt("_StaticLightTexShift", Log2(_staticTex.LightTextureSize));
        cmd.SetGlobalInt("_StaticNodeTexSize", _staticTex.NodeTextureSize);
        cmd.SetGlobalInt("_StaticNodeTexShift", Log2(_staticTex.NodeTextureSize));
        cmd.SetGlobalInt("_DynamicLightTexSize", _dynamicTex.LightTextureSize);
        cmd.SetGlobalInt("_DynamicLightTexShift", Log2(_dynamicTex.LightTextureSize));
        cmd.SetGlobalInt("_DynamicNodeTexSize", _dynamicTex.NodeTextureSize);
        cmd.SetGlobalInt("_DynamicNodeTexShift", Log2(_dynamicTex.NodeTextureSize));

        cmd.SetGlobalInt("_StaticLightRoot", _staticTex.RootNode);
        cmd.SetGlobalInt("_DynamicLightRoot", _dynamicTex.RootNode);
    }

    private void EnsureFallbackTextures()
    {
        if (_emptyBvhTexture == null)
        {
            _emptyBvhTexture = new Texture2D(1, 1, false, TextureImageFormat.Float4);
            _emptyBvhTexture.SetData(new Memory<Float4>([Float4.Zero]));
        }

        if (_emptyShadowTarget == null)
        {
            _emptyShadowTarget = new RenderTexture(1, 1, true, []);
            ShadowAtlas.ConfigureDepthSampler(_emptyShadowTarget.InternalDepth);
            using var initializeShadow = Graphics.GetCommandBuffer("Initialize empty shadow texture");
            initializeShadow.SetRenderTarget(_emptyShadowTarget.frameBuffer);
            initializeShadow.ClearRenderTarget(ClearFlags.Depth | ClearFlags.Stencil, new Color(0, 0, 0, 1));
            Graphics.Submit(initializeShadow);
        }
    }

    /// <summary>log2 of a power of 2; returns 0 for size &lt;= 1.</summary>
    private static int Log2(int size)
    {
        int n = 0;
        while ((1 << n) < size) n++;
        return n;
    }

    private void UploadDirectionalLight(CommandBuffer cmd)
    {
        // The shadow atlas is rasterized with a positive viewport height on Mali Vulkan but a
        // flipped viewport elsewhere. Keep the shader's projected Y convention in lockstep.
        cmd.SetGlobalFloat("_ShadowAtlasProjectedYScale", ShadowAtlas.GetProjectedYScale());

        if (_directional == null)
        {
            cmd.SetGlobalInt("_DirectionalLightEnabled", 0);
            cmd.SetGlobalVector("_DirectionalLightDirection", Float3.Zero);
            cmd.SetGlobalVector("_DirectionalLightColor", Float3.Zero);
            cmd.SetGlobalFloat("_DirectionalLightIntensity", 0f);
            cmd.SetGlobalInt("_DirectionalLightShadowEnabled", 0);
            cmd.SetGlobalInt("_DirectionalLightRenderingLayers", unchecked((int)RenderingLayerMask.Everything.Mask));
            cmd.SetGlobalInt("_RenderingLayerMask", unchecked((int)RenderingLayerMask.Everything.Mask));
            cmd.SetGlobalInt("_CascadeCount", 0);
            RenderStats.RecordShadowCascades(0);
            return;
        }

        var data = _directional.GetForwardLightData();
        // Direct lighting wants the raw direction; intensity applies the same * 8 scaling the
        // legacy ForwardLightManager did so existing scenes look identical at low light counts.
        cmd.SetGlobalInt("_DirectionalLightEnabled", 1);
        cmd.SetGlobalVector("_DirectionalLightDirection", data.Direction);
        cmd.SetGlobalVector("_DirectionalLightColor", data.Color);
        cmd.SetGlobalFloat("_DirectionalLightIntensity", data.Intensity);
        cmd.SetGlobalInt("_DirectionalLightShadowEnabled", data.ShadowEnabled ? 1 : 0);
        cmd.SetGlobalFloat("_DirectionalLightShadowBias", data.ShadowBias);
        cmd.SetGlobalFloat("_DirectionalLightShadowNormalBias", data.ShadowNormalBias);
        cmd.SetGlobalFloat("_DirectionalLightShadowStrength", data.ShadowStrength);
        cmd.SetGlobalFloat("_DirectionalLightShadowQuality", data.ShadowQuality);
        cmd.SetGlobalInt("_DirectionalLightRenderingLayers", unchecked((int)_directional.GetRenderingLayerMask()));
        cmd.SetGlobalInt("_RenderingLayerMask", unchecked((int)RenderingLayerMask.Everything.Mask));

        cmd.SetGlobalInt("_CascadeCount", data.ShadowEnabled ? data.CascadeCount : 0);
        RenderStats.RecordShadowCascades(data.ShadowEnabled ? data.CascadeCount : 0);
        if (data.CascadeShadowMatrices != null && data.CascadeAtlasParams != null)
        {
            for (int c = 0; c < 4; c++)
            {
                cmd.SetGlobalMatrix($"_CascadeShadowMatrix{c}",
                    c < data.CascadeCount ? data.CascadeShadowMatrices[c] : Float4x4.Identity);
                cmd.SetGlobalVector($"_CascadeAtlasParams{c}",
                    c < data.CascadeCount ? data.CascadeAtlasParams[c] : Float4.Zero);
            }
        }
    }

    // What occupied each shadow slot last frame (0 = empty, 1 = point, 2 = spot). Lets us clear
    // only the slots that actually held data instead of resetting all MaxShadowCasters*7 uniforms
    // every frame.
    private readonly int[] _slotKind = new int[MaxShadowCasters];

    private void UploadLocalShadowSlots(CommandBuffer cmd)
    {
        // Clear only the slots that held data last frame so any stale matrices a shader could
        // index fall through (spot atlas params .z <= 0). Slots reused this frame are overwritten
        // below; slots that were never written keep their default-zero GPU state.
        for (int i = 0; i < MaxShadowCasters; i++)
        {
            if (_slotKind[i] == 1)
            {
                for (int f = 0; f < 6; f++)
                {
                    int idx = i * 6 + f;
                    cmd.SetGlobalMatrix($"_PointShadowMatrices[{idx}]", Float4x4.Identity);
                    cmd.SetGlobalVector($"_PointShadowFaceParams[{idx}]", Float4.Zero);
                }
            }
            else if (_slotKind[i] == 2)
            {
                cmd.SetGlobalMatrix($"_SpotShadowMatrices[{i}]", Float4x4.Identity);
                cmd.SetGlobalVector($"_SpotShadowAtlasParams[{i}]", Float4.Zero);
            }
            _slotKind[i] = 0;
        }

        for (int i = 0; i < _shadowCasters.Count && i < MaxShadowCasters; i++)
        {
            var data = _shadowCasters[i].GetForwardLightData();
            if (!data.ShadowEnabled) continue;

            int slot = i;
            if (data.Type == LightType.Point)
            {
                if (data.PointShadowMatrices != null && data.PointShadowFaceParams != null)
                {
                    for (int f = 0; f < 6; f++)
                    {
                        int idx = slot * 6 + f;
                        cmd.SetGlobalMatrix($"_PointShadowMatrices[{idx}]", data.PointShadowMatrices[f]);
                        cmd.SetGlobalVector($"_PointShadowFaceParams[{idx}]", data.PointShadowFaceParams[f]);
                    }
                    _slotKind[slot] = 1;
                }
            }
            else if (data.Type == LightType.Spot)
            {
                cmd.SetGlobalMatrix($"_SpotShadowMatrices[{slot}]", data.SpotShadowMatrix);
                cmd.SetGlobalVector($"_SpotShadowAtlasParams[{slot}]", data.SpotShadowAtlasParams);
                _slotKind[slot] = 2;
            }
        }

        // Shadow atlas texture itself.
        var shadowAtlas = ShadowAtlas.GetAtlas();
        if (shadowAtlas != null)
        {
            cmd.SetGlobalTexture("_ShadowAtlas", shadowAtlas.InternalDepth);
            cmd.SetGlobalVector("_ShadowAtlasSize", new Float2(shadowAtlas.Width, shadowAtlas.Height));
        }
        else
        {
            cmd.SetGlobalTexture("_ShadowAtlas", _emptyShadowTarget.InternalDepth);
            cmd.SetGlobalVector("_ShadowAtlasSize", Float2.Zero);
        }
    }

    public void Dispose()
    {
        _staticTex.Dispose();
        _dynamicTex.Dispose();
        _clusters.Dispose();
        _emptyBvhTexture?.Dispose();
        _emptyShadowTarget?.Dispose();
        _emptyBvhTexture = null;
        _emptyShadowTarget = null;
        _membership.Clear();
        _seenThisFrame.Clear();
        _shadowCasters.Clear();
        _cookieLights.Clear();
        _cookieLightsLast.Clear();
        _directional = null;
    }

    private void AssignLightCookies()
    {
        var manager = LightCookieManager.Instance;
        manager.BeginFrame(_cookieLights.Count > 0);
        manager.SetDirectional(_directional as Light);

        for (int i = 0; i < _cookieLights.Count; i++)
        {
            Light light = _cookieLights[i];
            if (!_membership.TryGetValue(light, out Membership membership))
                continue;
            LightBVH bvh = membership == Membership.Static ? _staticBVH : _dynamicBVH;
            manager.PackLocal(light, bvh);
        }

        for (int i = 0; i < _cookieLightsLast.Count; i++)
        {
            Light previous = _cookieLightsLast[i];
            bool stillPacked = false;
            for (int j = 0; j < _cookieLights.Count; j++)
            {
                if (_cookieLights[j] == previous)
                {
                    stillPacked = true;
                    break;
                }
            }

            if (stillPacked || !_membership.TryGetValue(previous, out Membership membership))
                continue;
            LightBVH bvh = membership == Membership.Static ? _staticBVH : _dynamicBVH;
            bvh.SetCookie(previous, false, Float4.Zero, 0f);
        }

        _cookieLightsLast.Clear();
        for (int i = 0; i < _cookieLights.Count; i++)
            _cookieLightsLast.Add(_cookieLights[i]);
    }
}
