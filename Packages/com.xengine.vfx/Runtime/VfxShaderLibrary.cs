using System;

using XEngine.Runtime.AssetImporting;
using XEngine.Runtime.Resources;

namespace XEngine.VFX;

/// <summary>Parses the packaged <c>VfxParticles.shader</c> (Init / Update / WriteArgs / Output).</summary>
public static class VfxShaderLibrary
{
    public const string ShaderAssetName = "VfxParticles.shader";
    public const string InitPass = "Init";
    public const string UpdatePass = "Update";
    public const string WriteArgsPass = "WriteArgs";
    public const string SortPass = "Sort";
    public const string OutputPass = "Output";

    private static Shader? s_shader;

    public static Shader LoadParticlesShader()
    {
        if (s_shader != null && !s_shader.IsDisposed)
            return s_shader;

        string source = ReadShaderSource();
        if (!ShaderParser.ParseShader(ShaderAssetName, source, out Shader? shader) || shader == null)
            throw new InvalidOperationException("Failed to parse VFX/Particles shader.");

        s_shader = shader;
        return shader;
    }

    public static string ReadShaderSource() => VfxGraphSourceCompiler.ReadTemplateShader();
}
