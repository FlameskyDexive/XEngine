using XEngine.Editor.UI;
using XEngine.Editor.UI.Rendering;
using XEngine.PaperUI;
using XEngine.Runtime;
using XEngine.Runtime.Resources;
using XEngine.Vector;

namespace XEngine.UiPrototype;

internal sealed class PartialUploadValidationGame(string destination) : Game
{
    private struct Vertex(float x, float y, uint color)
    {
        public float X = x, Y = y, U = .5f, V = .5f;
        public uint Color = color;
    }
    public override void OnGui(Scene? scene, Paper paper)
    {
        try { Validate(); }
        catch (Exception error) { Console.Error.WriteLine(error); Environment.ExitCode = 2; }
        Quit();
    }

    private void Validate()
    {
        Directory.CreateDirectory(destination);
        using var nodes = new UiNodeStore(3, 3);
        UiNodeHandle[] quadNodes = [nodes.Create(), nodes.Create(), nodes.Create()];
        using var draws = new UiDrawList(nodes, 3, 3, 1);
        for (int i = 0; i < 3; i++)
        {
            nodes.SetRootBounds(quadNodes[i], new(0, 0, 300, 100));
            draws.TrySet(quadNodes[i], [new(i * 6, 6, 0, 0)]);
        }
        nodes.Layout();
        using var geometry = new UiGeometryStore<Vertex>(nodes, 3, 3, 4);
        using var vertices = new UiRhiUploadBuffer<Vertex>(BufferType.VertexBuffer, geometry);
        using var indices = new UiRhiUploadBuffer<uint>(BufferType.ElementsBuffer, 18, 3);
        ValidateTickets();
        var format = new VertexFormat([
            new(VertexFormat.VertexSemantic.Position, VertexFormat.VertexType.Float, 2, 0),
            new(VertexFormat.VertexSemantic.TexCoord0, VertexFormat.VertexType.Float, 2, 0),
            new(VertexFormat.VertexSemantic.Color, VertexFormat.VertexType.UnsignedByte, 4, 0, true)]);
        using var vao = Graphics.CreateVertexArray(format, vertices.Buffer, indices.Buffer);
        using var shader = Shader.LoadFromFile(Path.Combine(AppContext.BaseDirectory, "ImGui.shader"));
        if (!shader.GetPass(0).TryGetVariant(null, out var variant)) throw new InvalidOperationException("Fixture shader failed.");
        using var white = new Texture2D(1, 1);
        white.SetData(new Memory<byte>([255, 255, 255, 255]), 0, 0, 1, 1);
        using var target = new RenderTexture(300, 100, true, [TextureImageFormat.Color4b]);
        var results = new List<object>();
        for (int i = 0; i < 3; i++)
        {
            WriteQuad(i, i == 0 ? 0xff0000ffu : i == 1 ? 0xff00ff00u : 0xffff0000u);
            uint start = (uint)i * 4;
            indices.Write(i * 6, [start, start + 1, start + 2, start, start + 2, start + 3]);
        }
        Capture("initial", 240, 72, 0xff00ff00);
        Capture("idle", 0, 0, 0xff00ff00);
        WriteQuad(1, 0xff00ffff);
        Capture("middle-yellow", 80, 0, 0xff00ffff);
        WriteQuad(1, 0xffff0000);
        using (var abandoned = Graphics.GetCommandBuffer("N3 abandoned upload"))
        {
            var ticket = vertices.EncodePending(abandoned);
            vertices.CompleteSubmission(ticket, false); // Deliberately do not submit this command.
        }
        if (vertices.DirtyPageCount == 0) throw new InvalidOperationException("Canceled upload lost dirty data.");
        Capture("retry-blue", 80, 0, 0xffff0000);
        WriteQuad(1, 0xff0000ff);
        using (var stale = Graphics.GetCommandBuffer("N3 superseded upload"))
        {
            var ticket = vertices.EncodePending(stale);
            WriteQuad(1, 0xff00ff00);
            bool success = false;
            try { Graphics.Submit(stale); success = true; }
            finally { vertices.CompleteSubmission(ticket, success); }
        }
        if (vertices.DirtyPageCount == 0) throw new InvalidOperationException("Old submission erased newer CPU data.");
        Capture("newer-green", 80, 0, 0xff00ff00);
        nodes.SetStyle(quadNodes[1], UiStyle.Default with { Visible = false }); nodes.Layout();
        Capture("middle-hidden", 0, 0, 0xff000000, 2);
        nodes.SetStyle(quadNodes[1], UiStyle.Default); nodes.Layout();
        Capture("middle-restored", 0, 0, 0xff00ff00);
        nodes.SetRootBounds(quadNodes[1], new(120, 25, 60, 35)); nodes.Layout();
        Capture("middle-clipped", 0, 0, 0xff00ff00, checkClip: true);
        File.WriteAllText(Path.Combine(destination, "validation.json"), System.Text.Json.JsonSerializer.Serialize(results,
            new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine("N3 retained draw fixture: eight pixel/byte/draw checks passed.");

        void WriteQuad(int index, uint color)
        {
            if (!geometry.TrySet(quadNodes[index], Quad(index, color), out var range) || range.Offset != index * 4)
                throw new InvalidOperationException("Node geometry changed a stable range.");
        }

        void Capture(string name, long expectedVertexBytes, long expectedIndexBytes, uint expectedMiddle,
            int expectedDraws = 3, bool checkClip = false)
        {
            var visible = draws.Prepare();
            if (visible.Length != expectedDraws) throw new InvalidOperationException("Unexpected visible draw count.");
            using var command = Graphics.GetCommandBuffer("N3 partial upload fixture");
            UiUploadTicket vertexTicket = default, indexTicket = default;
            bool success = false;
            try
            {
                vertexTicket = vertices.EncodePending(command);
                indexTicket = indices.EncodePending(command);
                command.SetRenderTarget(target.frameBuffer); command.SetViewport(0, 0, 300, 100); command.DisableScissor();
                command.ClearRenderTarget(ClearFlags.Color | ClearFlags.Depth | ClearFlags.Stencil, new Color(0f, 0f, 0f, 1f));
                command.SetRasterState(new RasterizerState { DepthTest = false, DepthWrite = false, DoBlend = true,
                    BlendSrc = RasterizerState.Blending.One, BlendDst = RasterizerState.Blending.OneMinusSrcAlpha,
                    Blend = RasterizerState.BlendMode.Add, CullFace = RasterizerState.PolyFace.None });
                command.SetShader(variant);
                var projection = Float4x4.CreateOrthoOffCenter(0, 300, 100, 0, -1, 1);
                command.SetMatrix("projection", in projection); command.SetTexture("texture0", white);
                for (int i = 0; i < visible.Length; i++)
                {
                    var draw = visible[i]; var clip = draw.Clip;
                    command.SetScissor((int)clip.X, (int)clip.Y, (uint)clip.Width, (uint)clip.Height);
                    command.DrawIndexed(vao, Topology.Triangles, (uint)draw.Command.IndexCount,
                        (uint)draw.Command.IndexOffset, draw.Command.VertexOffset, true);
                }
                Graphics.Submit(command); success = true;
            }
            finally
            {
                if (vertexTicket.IsValid) vertices.CompleteSubmission(vertexTicket, success);
                if (indexTicket.IsValid) indices.CompleteSubmission(indexTicket, success);
            }
            if (vertexTicket.Bytes != expectedVertexBytes || indexTicket.Bytes != expectedIndexBytes)
                throw new InvalidOperationException("Unexpected partial upload size.");
            using (var flush = Graphics.GetCommandBuffer("N3 pixel readback")) Graphics.SubmitAndWait(flush);
            var pixels = new byte[target.MainTexture.GetSize()]; target.MainTexture.GetData<byte>(pixels.AsMemory());
            Pixel(50, 50, 0xff0000ff); Pixel(150, 50, expectedMiddle); Pixel(250, 50, 0xffff0000); Pixel(0, 0, 0xff000000);
            if (checkClip) { Pixel(115, 50, 0xff000000); Pixel(185, 50, 0xff000000); Pixel(150, 30, 0xff00ff00); Pixel(150, 70, 0xff000000); }
            using var picture = new ImageMagick.MagickImage();
            picture.ReadPixels(pixels, new ImageMagick.PixelReadSettings(300, 100, ImageMagick.StorageType.Char, ImageMagick.PixelMapping.RGBA));
            picture.Write(Path.Combine(destination, name + ".png"), ImageMagick.MagickFormat.Png32);
            results.Add(new { Stage = name, VertexBytes = vertexTicket.Bytes, IndexBytes = indexTicket.Bytes,
                DrawCount = visible.Length, PixelsPassed = true });
            void Pixel(int x, int y, uint expected)
            {
                int offset = (y * 300 + x) * 4;
                for (int channel = 0; channel < 4; channel++)
                    if (pixels[offset + channel] != (byte)(expected >> (channel * 8)))
                        throw new InvalidOperationException($"{name}: pixel {x},{y} channel {channel} mismatch.");
            }
        }
    }
    private static void ValidateTickets()
    {
        using var first = new UiRhiUploadBuffer<uint>(BufferType.VertexBuffer, 4, 1);
        using var second = new UiRhiUploadBuffer<uint>(BufferType.VertexBuffer, 4, 1);
        using var command = Graphics.GetCommandBuffer("N3 ticket lifecycle checks");
        first.Write(0, [1u]); second.Write(0, [2u]);
        var ticket = first.EncodePending(command);
        var foreign = second.EncodePending(command);
        try
        {
            Reject<ArgumentException>(() => first.CompleteSubmission(foreign, true));
            Reject<ArgumentException>(() => first.CompleteSubmission(default, true));
            Reject<InvalidOperationException>(() => first.EncodePending(command));
            Reject<InvalidOperationException>(() => first.Dispose());
        }
        finally
        {
            first.CompleteSubmission(ticket, false);
            second.CompleteSubmission(foreign, false);
        }
        Reject<ArgumentException>(() => first.CompleteSubmission(ticket, true));
        if (first.DirtyPageCount != 1 || second.DirtyPageCount != 1)
            throw new InvalidOperationException("Rejected or canceled tickets lost dirty data.");
        first.Dispose(); first.Dispose();
        Reject<ObjectDisposedException>(() => first.Write(0, [3u]));
        Console.WriteLine("N3 ticket lifecycle: foreign/null/stale/open/disposed checks passed.");

        static void Reject<TException>(Action action) where TException : Exception
        {
            try { action(); }
            catch (TException) { return; }
            throw new InvalidOperationException($"Expected {typeof(TException).Name}.");
        }
    }
    private static Vertex[] Quad(int index, uint color)
    {
        float x = index * 100 + 10;
        return [new(x, 10, color), new(x + 80, 10, color), new(x + 80, 90, color), new(x, 90, color)];
    }
}
