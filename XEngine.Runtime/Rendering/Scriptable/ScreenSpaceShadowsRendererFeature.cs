// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Runtime.Resources;

namespace XEngine.Runtime.Rendering;

/// <summary>
/// Optional directional contact shadows from camera depth. Does not replace the shadow atlas.
/// Disabled: 7 built-in passes, no prepass request, lighting unchanged (white map, enabled=0).
/// </summary>
public sealed class ScreenSpaceShadowsRendererFeature : RendererFeature
{
    public float Thickness = 0.03f;
    public float RayLength = 0.25f;

    private static readonly TextureImageFormat[] s_format = [TextureImageFormat.Color4b];
    private static Material? s_material;
    private static AssetRef<Texture2D> s_white;

    private RenderTexture? _previous;

    public override void Create() => EnsureResources();

    public override void AddRenderPasses(ScriptableRenderer renderer, ScriptableRenderContext context)
    {
        ConfigureInput(ScriptableRenderPassInput.Depth);
    }

    internal void Execute(CommandBuffer cmd, RenderTexture prepass, int width, int height)
    {
        EnsureResources();
        if (s_material == null || !White.IsValid())
        {
            BindDisabled(cmd);
            return;
        }

        if (_previous != null)
        {
            RenderTexture.ReleaseTemporaryRT(_previous);
            _previous = null;
        }

        RenderTexture ssRT = RenderTexture.GetTemporaryRT(width, height, false, s_format);
        int steps = SoftShadowQualityUtil.TapCount(QualitySettings.SoftShadowQuality);
        s_material.SetInt("_Steps", steps);
        s_material.SetFloat("_Thickness", Thickness);
        s_material.SetFloat("_RayLength", RayLength);
        cmd.Blit(ssRT, s_material);
        cmd.SetGlobalTexture("_ScreenSpaceShadowmap", ssRT.InternalTextures[0]);
        cmd.SetGlobalInt("_ScreenSpaceShadowsEnabled", 1);
        _previous = ssRT;
    }

    internal static void BindDisabled(CommandBuffer cmd)
    {
        EnsureResources();
        cmd.SetGlobalTexture("_ScreenSpaceShadowmap", White);
        cmd.SetGlobalInt("_ScreenSpaceShadowsEnabled", 0);
    }

    internal void ReleaseTransient()
    {
        if (_previous == null)
            return;
        RenderTexture.ReleaseTemporaryRT(_previous);
        _previous = null;
    }

    internal static void EnsureResources()
    {
        if (!s_white.Res.IsValid())
            s_white = new AssetRef<Texture2D>(Texture2D.LoadDefault(DefaultTexture.White));
        s_material ??= new Material(Shader.LoadDefault(DefaultShader.ScreenSpaceShadows));
    }

    internal static void DisposeResources()
    {
        s_material?.Dispose();
        s_material = null;
        s_white = default;
    }

    private static Texture2D White
    {
        get
        {
            Texture2D? texture = s_white.Res;
            if (texture.IsValid())
                return texture;
            EnsureResources();
            return s_white.Res!;
        }
    }
}
