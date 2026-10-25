// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using XEngine.Runtime.Resources;

namespace XEngine.Runtime.Rendering;

/// <summary>One registered <see cref="Volume"/> as shown by the Rendering Debugger (M20).</summary>
public readonly struct VolumeDebugEntry
{
    public readonly string Name;
    public readonly bool IsGlobal;
    public readonly float Weight;
    public readonly int Priority;
    public readonly bool Enabled;

    public VolumeDebugEntry(string name, bool isGlobal, float weight, int priority, bool enabled)
    {
        Name = name ?? "";
        IsGlobal = isGlobal;
        Weight = weight;
        Priority = priority;
        Enabled = enabled;
    }
}

/// <summary>
/// CPU snapshot of the Universal pipeline for the editor debugger and <c>runtime_render_debug</c>.
/// Not a GPU overdraw view — <see cref="OverdrawProxy"/> is extra color draws vs objects drawn.
/// </summary>
public sealed class RenderingDebugSnapshot
{
    public int DrawCalls;
    public int RenderablesDrawn;
    public int OverdrawProxy;
    public int Lights;
    public int DirectionalLights;
    public int PointLights;
    public int SpotLights;
    public int ShadowCasters;
    public int ShadowCascades;
    public int EnqueuedPasses;
    public int ExecutedPasses;
    public int CulledGraphPasses;
    public int CopyColorPasses;
    public int CopyDepthPasses;
    public int RtWidth;
    public int RtHeight;
    public bool OpaqueAllocated;
    public bool PrepassAllocated;
    public string[] PassEvents = [];
    public string[] Features = [];
    public string[] VolumeStack = [];
    public VolumeDebugEntry[] Volumes = [];

    // Virtual geometry.
    public int VgVisibleClusters;
    public long VgTriangles;
    public int VgDrawCalls;
    public long VgScratchUploadBytes;
    public long VgResidentBytes;
}

/// <summary>Captures pass list, Features, RTHandles, lighting, and Volume stack for M20.</summary>
public static class RenderingDebugger
{
    private static readonly RenderPassEvent[] s_events = new RenderPassEvent[32];

    internal static RenderingDebugSnapshot Capture(UniversalRenderPipeline? pipeline = null)
    {
        pipeline ??= UniversalRenderPipeline.Default;
        pipeline.CompileRenderGraphForTest(out int culled, out int executed);

        RenderStats.Frame frame = RenderStats.Last;
        int eventCount = pipeline.CopyLastPassEvents(s_events);
        var passEvents = new string[eventCount];
        for (int i = 0; i < eventCount; i++)
            passEvents[i] = s_events[i].ToString();

        IReadOnlyList<RendererFeature> features = pipeline.RendererFeatures;
        var featureNames = new string[features.Count];
        for (int i = 0; i < features.Count; i++)
        {
            RendererFeature feature = features[i];
            string name = feature.GetType().Name;
            featureNames[i] = feature.Enabled ? name : name + " disabled";
        }

        VolumeManager manager = VolumeManager.Instance;
        VolumeStack stack = manager.Stack;
        var stackNames = new string[stack.ComponentCount];
        for (int i = 0; i < stack.ComponentCount; i++)
            stackNames[i] = stack.GetComponent(i).GetType().Name;

        var volumes = new VolumeDebugEntry[manager.RegisteredCount];
        for (int i = 0; i < manager.RegisteredCount; i++)
        {
            Volume volume = manager.GetRegistered(i);
            string name = volume.GameObject != null ? volume.GameObject.Name : "";
            volumes[i] = new VolumeDebugEntry(
                name, volume.IsGlobal, volume.Weight, volume.Priority, volume.Enabled);
        }

        return new RenderingDebugSnapshot
        {
            DrawCalls = frame.DrawCalls,
            RenderablesDrawn = frame.RenderablesDrawn,
            OverdrawProxy = frame.OverdrawProxy,
            Lights = frame.Lights,
            DirectionalLights = frame.DirectionalLights,
            PointLights = frame.PointLights,
            SpotLights = frame.SpotLights,
            ShadowCasters = frame.ShadowCasters,
            ShadowCascades = frame.ShadowCascades,
            EnqueuedPasses = eventCount,
            ExecutedPasses = executed,
            CulledGraphPasses = culled,
            CopyColorPasses = frame.CopyColorPasses,
            CopyDepthPasses = frame.CopyDepthPasses,
            RtWidth = pipeline.RTHandles.ReferenceWidth,
            RtHeight = pipeline.RTHandles.ReferenceHeight,
            OpaqueAllocated = pipeline.RTHandles.CameraOpaque.RenderTexture != null,
            PrepassAllocated = pipeline.RTHandles.CameraPrepass.RenderTexture != null,
            PassEvents = passEvents,
            Features = featureNames,
            VolumeStack = stackNames,
            Volumes = volumes,
            VgVisibleClusters = frame.VgVisibleClusters,
            VgTriangles = frame.VgTriangles,
            VgDrawCalls = frame.VgDrawCalls,
            VgScratchUploadBytes = frame.VgScratchUploadBytes,
            VgResidentBytes = frame.VgResidentBytes,
        };
    }
}
