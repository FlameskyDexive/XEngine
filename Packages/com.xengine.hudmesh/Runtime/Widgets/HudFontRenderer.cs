using System;

using XEngine.Runtime.UI;
using XEngine.Scribe;
using XEngine.Vector;

namespace XEngine.Hudmesh;

/// <summary>
/// IFontRenderer that streams Scribe glyph quads directly into the slot-0 combined HUD mesh,
/// expanding pixel coordinates against the camera basis and flipping into Y-up space.
/// Atlas management is delegated to the engine's shared <see cref="UIFontSystem"/>; only
/// DrawQuads is intercepted. Also applies the per-corner gradient (quad corner order after
/// the flip is BL, BR, TL, TR).
/// </summary>
internal sealed class HudFontRenderer : IFontRenderer
{
    private HudMeshData? _target;
    private Float3 _anchor;
    private Float3 _rightStep;
    private Float3 _upStep;
    private float _offsetX;
    private float _offsetY;
    private float _scale;
    private float _shift;
    private float _blockW;
    private float _blockH;
    private Color _color;
    private Color32 _gradBL;
    private Color32 _gradBR;
    private Color32 _gradTL;
    private Color32 _gradTR;

    public void Begin(HudMeshData target, in Float3 anchor, in HudFillContext context,
        float offsetX, float offsetY, float scale, float shift, float blockW, float blockH,
        in Color color, in Color32 gradBL, in Color32 gradBR, in Color32 gradTL, in Color32 gradTR)
    {
        _target = target;
        _anchor = anchor;
        _rightStep = context.RightStep;
        _upStep = context.UpStep;
        _offsetX = offsetX;
        _offsetY = offsetY;
        _scale = scale;
        _shift = shift;
        _blockW = blockW;
        _blockH = blockH;
        _color = color;
        _gradBL = gradBL;
        _gradBR = gradBR;
        _gradTL = gradTL;
        _gradTR = gradTR;
    }

    public object CreateTexture(int width, int height)
        => UIFontSystem.Default.CreateTexture(width, height);

    public void UpdateTextureRegion(object texture, AtlasRect bounds, byte[] data)
        => UIFontSystem.Default.UpdateTextureRegion(texture, bounds, data);

    public void DrawQuads(object texture, ReadOnlySpan<IFontRenderer.Vertex> vertices, ReadOnlySpan<int> indices)
    {
        if (_target is null)
            return;

        // Scribe emits fixed 4-vertex/6-index quads: (x0,y0) (x1,y0) (x0,y1) (x1,y1) — after the
        // Y flip these are BL, BR, TL, TR, matching AppendQuadCorners' order and triangulation.
        for (int i = 0; i + 3 < vertices.Length; i += 4)
        {
            Float3 w0 = ExpandPosition(vertices[i]);
            Float3 w1 = ExpandPosition(vertices[i + 1]);
            Float3 w2 = ExpandPosition(vertices[i + 2]);
            Float3 w3 = ExpandPosition(vertices[i + 3]);

            _target.AppendQuadCorners(
                w0, w1, w2, w3,
                vertices[i].TextureCoordinate, vertices[i + 1].TextureCoordinate,
                vertices[i + 2].TextureCoordinate, vertices[i + 3].TextureCoordinate,
                MultiplyColor(_color, _gradBL), MultiplyColor(_color, _gradBR),
                MultiplyColor(_color, _gradTL), MultiplyColor(_color, _gradTR));
        }
    }

    private Float3 ExpandPosition(in IFontRenderer.Vertex vertex)
    {
        float px = _offsetX + (vertex.Position.X - _shift * _blockW) * _scale;
        float py = _offsetY + (_blockH - vertex.Position.Y) * _scale;
        return new Float3(
            _anchor.X + _rightStep.X * px + _upStep.X * py,
            _anchor.Y + _rightStep.Y * px + _upStep.Y * py,
            _anchor.Z + _rightStep.Z * px + _upStep.Z * py);
    }

    internal static Color32 MultiplyColor(in Color color, in Color32 gradient)
        => new(
            (byte)(color.R * gradient.R / 255),
            (byte)(color.G * gradient.G / 255),
            (byte)(color.B * gradient.B / 255),
            (byte)(color.A * gradient.A / 255));
}
