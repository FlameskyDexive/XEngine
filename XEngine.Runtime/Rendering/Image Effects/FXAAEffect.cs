// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Runtime.Resources;
using XEngine.Vector;

using Material = XEngine.Runtime.Resources.Material;
using Shader = XEngine.Runtime.Resources.Shader;

namespace XEngine.Runtime.Rendering;

public sealed class FXAAEffect : ImageEffect
{
    public float EdgeThresholdMax = 0.0625f;  // 0.063 - 0.333 (lower = more AA, slower)
    public float EdgeThresholdMin = 0.0312f;  // 0.0312 - 0.0833 (trims dark edges)
    public float SubpixelQuality = 0.75f;     // 0.0 - 1.0 (subpixel AA amount)

    private Material _mat;

    public override void OnRenderEffect(RenderContext context)
    {
        _mat ??= new Material(Shader.LoadDefault(DefaultShader.FXAA));

        // Set shader parameters
        _mat.SetFloat("_EdgeThresholdMax", EdgeThresholdMax);
        _mat.SetFloat("_EdgeThresholdMin", EdgeThresholdMin);
        _mat.SetFloat("_SubpixelQuality", SubpixelQuality);
        _mat.SetVector("_Resolution", new Float2(context.Width, context.Height));

        // Blit to a temporary RT to avoid reading and writing the same texture simultaneously
        var temp = RenderTexture.GetTemporaryRT(context.Width, context.Height, false, [context.SceneColor.MainTexture.ImageFormat]);
        using var cmd = Graphics.GetCommandBuffer("FXAA");
        cmd.Blit(context.SceneColor, temp, _mat, 0);
        cmd.SetRenderTargets(context.SceneColor.frameBuffer, temp.frameBuffer);
        cmd.BlitFramebuffer(
            0, 0, temp.Width, temp.Height,
            0, 0, context.SceneColor.Width, context.SceneColor.Height,
            ClearFlags.Color,
            BlitFilter.Linear);
        Graphics.Submit(cmd);
        RenderTexture.ReleaseTemporaryRT(temp);
    }

    public override void OnDisable()
    {
        _mat?.Dispose();
        _mat = null;
    }
}
