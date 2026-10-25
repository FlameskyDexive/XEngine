using XEngine.PaperUI;
using XEngine.PaperUI.LayoutEngine;
using XEngine.Quill;
using XEngine.Vector;
using ImGuiNET;
using System.Numerics;

namespace XEngine.UiPrototype;

/// <summary>Identical decorative geometry specification for all three adapters.
/// Six rounded cards, inset borders and eight translucent shadow layers each.
/// Shadows are a layered approximation, not a Gaussian blur implementation.</summary>
internal static class PrototypeVisualEffects
{
    private const int LayersPerCard = 10, CardCount = 6;
    private readonly record struct Rect(float X, float Y, float Width, float Height, float Radius,
        byte R, byte G, byte B, byte A)
    {
        public Color32 Color => new(R, G, B, A);
        public uint PackedColor => (uint)(R | G << 8 | B << 16 | A << 24);
    }

    private static Rect Geometry(int index, float width, float height)
    {
        int card = index / LayersPerCard, layer = index % LayersPerCard;
        float viewportTop = (height - 88) * .6f + 80;
        float cardWidth = (width - 64) / 3, cardHeight = (height - viewportTop - 40) / 2;
        float x = 16 + card % 3 * (cardWidth + 16), y = viewportTop + 12 + card / 3 * (cardHeight + 16);
        if (layer < 8)
        {
            float spread = (8 - layer) * .6f;
            return new(x - spread, y + 3 - spread, cardWidth + spread * 2, cardHeight + spread * 2,
                6 + spread, 0, 0, 0, 10);
        }
        if (layer == 8) return new(x, y, cardWidth, cardHeight, 6, 110, 130, 156, 230);
        return new(x + 1, y + 1, cardWidth - 2, cardHeight - 2, 5, 42, 50, 64, 230);
    }

    public static void Draw(Paper paper, float width, float height)
    {
        for (int i = 0; i < CardCount * LayersPerCard; i++)
        {
            var r = Geometry(i, width, height);
            paper.Box("n1-effect", i).PositionType(PositionType.SelfDirected).Position(r.X, r.Y)
                .Size(r.Width, r.Height).Rounded(r.Radius).BackgroundColor(r.Color).IsNotInteractable();
        }
    }

    public static void Draw(Canvas canvas, float width, float height)
    {
        for (int i = 0; i < CardCount * LayersPerCard; i++)
        {
            var r = Geometry(i, width, height);
            canvas.RoundedRectFilled(r.X, r.Y, r.Width, r.Height, r.Radius, r.Color);
        }
    }

    public static void Draw(ImDrawListPtr draw, float width, float height)
    {
        for (int i = 0; i < CardCount * LayersPerCard; i++)
        {
            var r = Geometry(i, width, height);
            draw.AddRectFilled(new Vector2(r.X, r.Y), new Vector2(r.X + r.Width, r.Y + r.Height), r.PackedColor, r.Radius);
        }
    }
}
