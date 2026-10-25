// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Runtime.Resources;
using XEngine.Vector;

namespace XEngine.Runtime.Rendering;

/// <summary>
/// Draws each active <see cref="DecalProjector"/> as a unit cube after opaques.
/// Samples camera depth/normals; idle when the system list is empty (the feature does not enqueue).
/// </summary>
public sealed class ScreenSpaceDecalPass : RenderPass
{
    private static Mesh? s_cube;
    private static Material? s_material;
    private static Texture2D? s_white;

    private readonly PropertyState _instance = new();

    public ScreenSpaceDecalPass()
        : base(RenderPassEvent.AfterRenderingOpaques)
    {
        ConfigureInput(ScriptableRenderPassInput.Depth | ScriptableRenderPassInput.Normal);
    }

    public override void Execute(ScriptableRenderContext context)
    {
        DecalSystem system = DecalSystem.Instance;
        if (system.Count == 0)
            return;

        RenderTexture? color = context.ColorRT;
        if (color == null || color.IsDisposed || color.frameBuffer == null)
            return;
        if (context.Prepass == null || context.Prepass.IsDisposed)
            return;

        EnsureResources();
        if (s_cube == null || s_material == null || s_white == null)
            return;

        using var cmd = Graphics.GetCommandBuffer("ScreenSpaceDecals");
        cmd.SetRenderTarget(color.frameBuffer);
        cmd.SetViewport(0, 0, (uint)color.Width, (uint)color.Height);

        for (int i = 0; i < system.Count; i++)
        {
            DecalProjector projector = system[i];
            if (projector == null || projector.IsDisposed)
                continue;
            if (projector.GameObject == null || !projector.GameObject.EnabledInHierarchy)
                continue;

            projector.OnValidate();
            Texture2D texture = projector.Texture.Res ?? s_white;
            _instance.Clear();
            _instance.SetTexture("_MainTex", texture);
            _instance.SetColor("_MainColor", projector.Color);
            _instance.SetFloat("_Opacity", projector.Opacity);
            _instance.SetInt("_DecalRenderingLayers", unchecked((int)projector.RenderingLayers.Mask));
            cmd.DrawMesh(s_cube, s_material, 0, projector.GetVolumeMatrix(), _instance);
        }

        Graphics.Submit(cmd);
    }

    internal static void EnsureResources()
    {
        s_cube ??= Mesh.CreateCube(Float3.One);
        s_white ??= Texture2D.LoadDefault(DefaultTexture.White);
        s_material ??= new Material(Shader.LoadDefault(DefaultShader.ScreenSpaceDecal));
    }

    internal static void DisposeResources()
    {
        s_cube?.Dispose();
        s_cube = null;
        s_material?.Dispose();
        s_material = null;
        s_white = null;
    }
}
