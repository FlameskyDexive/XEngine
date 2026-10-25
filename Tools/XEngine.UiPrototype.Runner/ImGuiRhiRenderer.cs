using ImGuiNET;
using XEngine.Runtime;
using XEngine.Runtime.GUI;
using XEngine.Runtime.RHI;
using XEngine.Runtime.RHI.Shaders;
using XEngine.Runtime.Resources;
using XEngine.Vector;

namespace XEngine.UiPrototype;

/// <summary>Isolated N1 RHI renderer. ImGui's native indexed mesh is uploaded directly.
/// Texture IDs are explicit borrowed handles; no direct OpenGL calls.</summary>
internal sealed class ImGuiRhiRenderer : IDisposable
{
    private readonly Shader _shader;
    private readonly ShaderVariant _variant;
    private readonly GraphicsBuffer _vertices, _indices;
    private readonly GraphicsVertexArray _vao;
    public GraphicsFrameBuffer? Target { get; set; }
    public UiRenderStats Stats { get; private set; }
    public bool CaptureGpuTiming { get; set; }
    private readonly PresentationSurfaceId _surface;
    public ImGuiRhiRenderer()
    {
        // Single-surface prototype: cache once, as PaperRenderer does. Vulkan's
        // lifecycle query takes the device lock and can wait for render-thread work.
        Graphics.TryGetPrimaryPresentationSurface(out _surface);
        _shader = Shader.LoadFromFile(Path.Combine(AppContext.BaseDirectory, "ImGui.shader"));
        if (!_shader.GetPass(0).TryGetVariant(null, out _variant)) throw new InvalidOperationException("ImGui shader compilation failed.");
        _vertices = Graphics.CreateBuffer<byte>(BufferType.VertexBuffer, [], true);
        _indices = Graphics.CreateBuffer<ushort>(BufferType.ElementsBuffer, [], true);
        var format = new VertexFormat([
            new(VertexFormat.VertexSemantic.Position, VertexFormat.VertexType.Float, 2, 0),
            new(VertexFormat.VertexSemantic.TexCoord0, VertexFormat.VertexType.Float, 2, 0),
            new(VertexFormat.VertexSemantic.Color, VertexFormat.VertexType.UnsignedByte, 4, 0, true)]);
        _vao = Graphics.CreateVertexArray(format, _vertices, _indices);
    }

    public unsafe void Render(ImDrawDataPtr data, Texture2D font, Texture2D viewport)
    {
        int width = (int)(data.DisplaySize.X * data.FramebufferScale.X);
        int height = (int)(data.DisplaySize.Y * data.FramebufferScale.Y);
        var stats = new UiRenderStats { FrameWidth = width, FrameHeight = height };
        if (width <= 0 || height <= 0) { Stats = stats; return; }
        using var cmd = Graphics.GetCommandBuffer("N1 ImGui UI");
        cmd.CaptureGpuTiming = CaptureGpuTiming;
        if (_surface.IsValid) cmd.SetPresentationSurface(_surface);
        cmd.SetRenderTarget(Target); cmd.SetViewport(0, 0, (uint)width, (uint)height);
        cmd.SetRasterState(new RasterizerState { DepthTest = false, DepthWrite = false, DoBlend = true,
            BlendSrc = RasterizerState.Blending.One, BlendDst = RasterizerState.Blending.OneMinusSrcAlpha,
            Blend = RasterizerState.BlendMode.Add, CullFace = RasterizerState.PolyFace.None });
        cmd.SetShader(_variant);
        var projection = Float4x4.CreateOrthoOffCenter(data.DisplayPos.X, data.DisplayPos.X + data.DisplaySize.X,
            data.DisplayPos.Y + data.DisplaySize.Y, data.DisplayPos.Y, -1, 1);
        cmd.SetMatrix("projection", in projection);
        for (int li = 0; li < data.CmdListsCount; li++)
        {
            var list = data.CmdLists[li];
            int vertexBytes = checked(list.VtxBuffer.Size * sizeof(ImDrawVert));
            cmd.UpdateBuffer<byte>(_vertices, new ReadOnlySpan<byte>((void*)list.VtxBuffer.Data, vertexBytes));
            cmd.UpdateBuffer<ushort>(_indices, new ReadOnlySpan<ushort>((void*)list.IdxBuffer.Data, list.IdxBuffer.Size));
            stats.VertexUploadBytes += vertexBytes; stats.IndexUploadBytes += list.IdxBuffer.Size * 2L;
            for (int ci = 0; ci < list.CmdBuffer.Size; ci++)
            {
                var draw = list.CmdBuffer[ci];
                if (draw.UserCallback != IntPtr.Zero) throw new NotSupportedException("N1 custom draw callbacks require an explicit adapter.");
                int x = Math.Clamp((int)Math.Floor((draw.ClipRect.X - data.DisplayPos.X) * data.FramebufferScale.X), 0, width);
                int y = Math.Clamp((int)Math.Floor((draw.ClipRect.Y - data.DisplayPos.Y) * data.FramebufferScale.Y), 0, height);
                int right = Math.Clamp((int)Math.Ceiling((draw.ClipRect.Z - data.DisplayPos.X) * data.FramebufferScale.X), 0, width);
                int bottom = Math.Clamp((int)Math.Ceiling((draw.ClipRect.W - data.DisplayPos.Y) * data.FramebufferScale.Y), 0, height);
                if (right <= x || bottom <= y || draw.ElemCount == 0) continue;
                Texture2D texture = draw.TextureId == (IntPtr)1 ? font : draw.TextureId == (IntPtr)2 ? viewport
                    : throw new InvalidOperationException("Unknown N1 texture ID.");
                cmd.SetTexture("texture0", texture);
                cmd.SetScissor(x, y, (uint)(right - x), (uint)(bottom - y));
                cmd.DrawIndexed(_vao, Topology.Triangles, draw.ElemCount, draw.IdxOffset, (int)draw.VtxOffset, false);
                stats.UiDrawCalls++;
            }
        }
        cmd.DisableScissor(); Graphics.Submit(cmd); Stats = stats;
    }

    public void Dispose() { _vao.Dispose(); _vertices.Dispose(); _indices.Dispose(); _shader.Dispose(); }
}
