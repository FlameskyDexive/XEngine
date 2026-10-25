// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using XEngine.Runtime.Resources;
using XEngine.Vector;

namespace XEngine.Runtime.Rendering;

/// <summary>
/// 2D Universal renderer (M22). UniversalRenderPipeline enqueues the same 7 built-in events as
/// Forward; this type owns Light2D accumulation, 2D shadow volumes, and sprite-lit globals.
/// Does not use LightBVH or the 3D shadow atlas.
/// </summary>
public static class Renderer2D
{
    public const string LightTextureName = "_LightTexture2D";
    public const string ShadowMaskName = "_ShadowMask2D";

    private static Material? s_lightMaterial;
    private static Material? s_shadowMaterial;
    private static Texture2D? s_white;
    private static readonly PropertyState s_instance = new();
    private static int[] s_order = [];

    internal static void EnsureResources()
    {
        s_white ??= Texture2D.LoadDefault(DefaultTexture.White);
        s_lightMaterial ??= new Material(Shader.LoadDefault(DefaultShader.Light2D));
        s_shadowMaterial ??= new Material(Shader.LoadDefault(DefaultShader.Shadow2D));
    }

    internal static void DisposeResources()
    {
        s_lightMaterial?.Dispose();
        s_lightMaterial = null;
        s_shadowMaterial?.Dispose();
        s_shadowMaterial = null;
        s_white = null;
    }

    internal static bool NeedsShadowMask()
    {
        Light2DSystem lights = Light2DSystem.Instance;
        if (ShadowCaster2DSystem.Instance.Count == 0)
            return false;
        for (int i = 0; i < lights.Count; i++)
        {
            Light2D light = lights[i];
            if (light == null || light.IsDisposed || !light.Enabled)
                continue;
            if (light.CastShadows && light.LightType != Light2DType.Global)
                return true;
        }

        return false;
    }

    internal static void BindWhiteLight(CommandBuffer cmd)
    {
        EnsureResources();
        if (s_white != null)
            cmd.SetGlobalTexture(LightTextureName, s_white);
    }

    internal static void ExecuteShadows(CommandBuffer cmd, RenderTexture shadowRt)
    {
        EnsureResources();
        if (s_shadowMaterial == null || s_white == null)
            return;

        cmd.SetRenderTarget(shadowRt.frameBuffer);
        cmd.SetViewport(0, 0, (uint)shadowRt.Width, (uint)shadowRt.Height);
        cmd.ClearRenderTarget(ClearFlags.Color, Color.White);

        Mesh quad = Mesh.GetFullscreenQuad();
        Light2DSystem lights = Light2DSystem.Instance;
        ShadowCaster2DSystem casters = ShadowCaster2DSystem.Instance;
        for (int i = 0; i < lights.Count; i++)
        {
            Light2D light = lights[i];
            if (light == null || light.IsDisposed || !light.Enabled)
                continue;
            if (!light.CastShadows || light.LightType == Light2DType.Global)
                continue;
            if (light.GameObject == null || !light.GameObject.EnabledInHierarchy)
                continue;

            light.OnValidate();
            Float2 lightXy = new(light.Transform.Position.X, light.Transform.Position.Y);
            float extrude = MathF.Max(light.OuterRadius * 8f, 32f);
            for (int c = 0; c < casters.Count; c++)
            {
                ShadowCaster2D caster = casters[c];
                if (caster == null || caster.IsDisposed || !caster.CastsShadows)
                    continue;
                if (caster.GameObject == null || !caster.GameObject.EnabledInHierarchy)
                    continue;
                caster.GetWorldAabb(out Float2 min, out Float2 max);
                DrawShadowEdge(cmd, quad, lightXy, extrude, new Float2(min.X, min.Y), new Float2(max.X, min.Y));
                DrawShadowEdge(cmd, quad, lightXy, extrude, new Float2(max.X, min.Y), new Float2(max.X, max.Y));
                DrawShadowEdge(cmd, quad, lightXy, extrude, new Float2(max.X, max.Y), new Float2(min.X, max.Y));
                DrawShadowEdge(cmd, quad, lightXy, extrude, new Float2(min.X, max.Y), new Float2(min.X, min.Y));
            }
        }

        cmd.SetGlobalTexture(ShadowMaskName, shadowRt.MainTexture);
    }

    internal static void ExecuteLights(CommandBuffer cmd, RenderTexture lightRt, Texture2D? shadowMask)
    {
        EnsureResources();
        if (s_lightMaterial == null || s_white == null)
            return;

        cmd.SetRenderTarget(lightRt.frameBuffer);
        cmd.SetViewport(0, 0, (uint)lightRt.Width, (uint)lightRt.Height);
        cmd.ClearRenderTarget(ClearFlags.Color, Color.Black);
        cmd.SetGlobalTexture(ShadowMaskName, shadowMask ?? s_white);

        Mesh quad = Mesh.GetFullscreenQuad();
        Light2DSystem lights = Light2DSystem.Instance;
        SortLights(lights);
        for (int n = 0; n < lights.Count; n++)
        {
            Light2D light = lights[s_order[n]];
            if (light == null || light.IsDisposed || !light.Enabled)
                continue;
            if (light.GameObject == null || !light.GameObject.EnabledInHierarchy)
                continue;

            light.OnValidate();
            s_instance.Clear();
            s_instance.SetColor("_MainColor", new Color(
                light.Color.R * light.Intensity,
                light.Color.G * light.Intensity,
                light.Color.B * light.Intensity,
                1f + light.Falloff * 3f));
            s_instance.SetVector("_Tiling", new Float2(light.InnerRadius, light.OuterRadius));
            s_instance.SetVector("_Offset", new Float2((float)light.LightType, light.CastShadows ? light.ShadowIntensity : 0f));
            Texture2D cookie = s_white;
            if (light.LightType == Light2DType.Sprite)
            {
                Sprite? sprite = light.Cookie.Res;
                Texture2D? tex = sprite?.Texture.Res;
                if (tex != null)
                    cookie = tex;
            }

            s_instance.SetTexture("_MainTex", cookie);

            if (light.LightType == Light2DType.Global)
            {
                cmd.DrawMesh(quad, s_lightMaterial, 0, default, s_instance);
                continue;
            }

            float outer = MathF.Max(light.OuterRadius, 0.01f);
            Float4x4 model = Float4x4.CreateTRS(
                light.Transform.Position,
                Quaternion.Identity,
                new Float3(outer, outer, 1f));
            cmd.DrawMesh(quad, s_lightMaterial, 0, model, s_instance);
        }

        cmd.SetGlobalTexture(LightTextureName, lightRt.MainTexture);
    }

    private static void DrawShadowEdge(
        CommandBuffer cmd,
        Mesh quad,
        Float2 lightXy,
        float extrude,
        Float2 a,
        Float2 b)
    {
        s_instance.Clear();
        s_instance.SetVector("_Tiling", a);
        s_instance.SetVector("_Offset", b);
        s_instance.SetColor("_MainColor", new Color(lightXy.X, lightXy.Y, extrude, 1f));
        cmd.DrawMesh(quad, s_shadowMaterial, 0, default, s_instance);
    }

    private static void SortLights(Light2DSystem lights)
    {
        int n = lights.Count;
        if (s_order.Length < n)
            s_order = new int[n];
        for (int i = 0; i < n; i++)
            s_order[i] = i;
        for (int i = 1; i < n; i++)
        {
            int key = s_order[i];
            int keyOrder = lights[key].LightOrder;
            int j = i - 1;
            while (j >= 0 && lights[s_order[j]].LightOrder > keyOrder)
            {
                s_order[j + 1] = s_order[j];
                j--;
            }

            s_order[j + 1] = key;
        }
    }
}
