// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.IO;
using XEngine.Runtime.Resources;
using XEngine.Runtime.UI;
using XEngine.Vector;
using XEngine.Vector.Geometry;
using Xunit;

namespace XEngine.Runtime.Test;

public sealed class GraphicNativeSizeTests : RuntimeTestBase
{
    [Fact]
    public void TryFromSprite_UsesPixelRectAndMultiplier()
    {
        using Texture2D texture = new(48, 24);
        Sprite sprite = Sprite.Create(texture, new SpriteRect(0, 0, 48, 24), new Float2(0.5f, 0.5f), 100f, "Chip");

        Assert.True(GraphicNativeSize.TryFromSprite(sprite, 1f, out Float2 native));
        Assert.Equal(new Float2(48f, 24f), native);

        Assert.True(GraphicNativeSize.TryFromSprite(sprite, 2f, out Float2 scaled));
        Assert.Equal(new Float2(96f, 48f), scaled);

        Assert.False(GraphicNativeSize.TryFromSprite(null, 1f, out _));
    }

    [Fact]
    public void TryFromTexture_UsesAbsoluteUvSize()
    {
        using Texture2D texture = new(64, 32);
        Assert.True(GraphicNativeSize.TryFromTexture(texture, new Rect(0f, 0f, 1f, 1f), out Float2 full));
        Assert.Equal(new Float2(64f, 32f), full);

        Assert.True(GraphicNativeSize.TryFromTexture(texture, new Rect(0.25f, 0f, 0.75f, 0.5f), out Float2 sliced));
        Assert.Equal(new Float2(32f, 16f), sliced);

        Assert.False(GraphicNativeSize.TryFromTexture(null, new Rect(0f, 0f, 1f, 1f), out _));
    }

    [Fact]
    public void UIImage_SetNativeSize_CollapsesAnchorsAndMatchesSpritePixels()
    {
        using Scene scene = CreateScene(enable: false);
        GameObject canvasObject = CreateGameObject("Canvas");
        GameCanvas canvas = canvasObject.AddComponent<GameCanvas>();
        GameObject imageObject = CreateGameObject("Image");
        imageObject.SetParent(canvasObject, worldPositionStays: false);
        imageObject.EnsureRectTransform();
        UIImage image = imageObject.AddComponent<UIImage>();

        using Texture2D texture = new(48, 24);
        Sprite sprite = Sprite.Create(texture, new SpriteRect(0, 0, 48, 24), new Float2(0.5f, 0.5f), 100f, "Chip");
        image.Sprite = sprite;
        image.PixelsPerUnitMultiplier = 2f;

        RectTransform rt = imageObject.RectTransform!;
        rt.AnchorMin = Float2.Zero;
        rt.AnchorMax = Float2.One;
        rt.SizeDelta = new Float2(-20f, -20f);

        scene.Add(canvasObject);
        scene.Enable();
        GameCanvas.ScreenSizeOverride = new Float2(400f, 240f);
        try
        {
            image.SetNativeSize();
            canvas.RebuildIfDirty();

            Assert.Equal(rt.AnchorMin, rt.AnchorMax);
            Assert.Equal(new Float2(96f, 48f), rt.SizeDelta);
            Assert.Equal(new Float2(96f, 48f), rt.ComputedRect.Size);
        }
        finally
        {
            GameCanvas.ScreenSizeOverride = null;
        }
    }

    [Fact]
    public void UIImage_SetNativeSize_WithoutSprite_LeavesAuthoredSize()
    {
        using Scene scene = CreateScene(enable: false);
        GameObject canvasObject = CreateGameObject("Canvas");
        canvasObject.AddComponent<GameCanvas>();
        GameObject imageObject = CreateGameObject("Image");
        imageObject.SetParent(canvasObject, worldPositionStays: false);
        imageObject.EnsureRectTransform();
        UIImage image = imageObject.AddComponent<UIImage>();
        image.Sprite = default;
        RectTransform rt = imageObject.RectTransform!;
        rt.SizeDelta = new Float2(80f, 40f);

        scene.Add(canvasObject);
        scene.Enable();

        image.SetNativeSize();
        Assert.Equal(new Float2(80f, 40f), rt.SizeDelta);
    }

    [Fact]
    public void SetNativeSize_AcceptanceScreenshot()
    {
        using Scene scene = CreateScene(enable: false);
        GameObject canvasObject = CreateGameObject("Canvas");
        GameCanvas canvas = canvasObject.AddComponent<GameCanvas>();

        using Texture2D smallTex = new(48, 24);
        using Texture2D squareTex = new(32, 32);
        Sprite chip = Sprite.Create(smallTex, new SpriteRect(0, 0, 48, 24), new Float2(0.5f, 0.5f), 100f, "Chip");
        Sprite tile = Sprite.Create(squareTex, new SpriteRect(0, 0, 32, 32), new Float2(0.5f, 0.5f), 100f, "Tile");

        var before = new XGuiAcceptanceScreenshot.Canvas(760, 360, 18, 19, 26);
        var after = new XGuiAcceptanceScreenshot.Canvas(760, 360, 18, 19, 26);

        Float2[] beforeSizes = new Float2[3];
        Float2[] afterSizes = new Float2[3];
        UIImage[] images = new UIImage[3];
        float[] multipliers = [1f, 2f, 1f];
        Sprite[] sprites = [chip, chip, tile];
        Float2[] positions = [new(-220f, 20f), new(0f, 20f), new(220f, 20f)];

        for (int i = 0; i < 3; i++)
        {
            GameObject imageObject = CreateGameObject("Image" + i);
            imageObject.SetParent(canvasObject, worldPositionStays: false);
            imageObject.EnsureRectTransform();
            UIImage image = imageObject.AddComponent<UIImage>();
            image.Sprite = sprites[i];
            image.PixelsPerUnitMultiplier = multipliers[i];
            RectTransform rt = imageObject.RectTransform!;
            rt.AnchorMin = new Float2(0.5f, 0.5f);
            rt.AnchorMax = new Float2(0.5f, 0.5f);
            rt.AnchoredPosition = positions[i];
            rt.SizeDelta = new Float2(40f, 40f);
            images[i] = image;
        }

        scene.Add(canvasObject);
        scene.Enable();
        GameCanvas.ScreenSizeOverride = new Float2(760f, 360f);
        try
        {
            canvas.RebuildIfDirty();
            for (int i = 0; i < 3; i++)
                beforeSizes[i] = images[i].GameObject.RectTransform!.ComputedRect.Size;

            for (int i = 0; i < 3; i++)
                images[i].SetNativeSize();
            canvas.RebuildIfDirty();
            for (int i = 0; i < 3; i++)
                afterSizes[i] = images[i].GameObject.RectTransform!.ComputedRect.Size;
        }
        finally
        {
            GameCanvas.ScreenSizeOverride = null;
        }

        Assert.Equal(new Float2(48f, 24f), afterSizes[0]);
        Assert.Equal(new Float2(96f, 48f), afterSizes[1]);
        Assert.Equal(new Float2(32f, 32f), afterSizes[2]);

        DrawBoard(before, "BEFORE SETNATIVESIZE", beforeSizes, wrong: true);
        DrawBoard(after, "AFTER SETNATIVESIZE", afterSizes, wrong: false);
        string beforePath = XGuiAcceptanceScreenshot.Save("xgui-image-set-native-size-before", before);
        string afterPath = XGuiAcceptanceScreenshot.Save("xgui-image-set-native-size", after);
        Assert.True(File.Exists(beforePath));
        Assert.True(File.Exists(afterPath));
        Assert.True(new FileInfo(afterPath).Length > 200);
    }

    private static void DrawBoard(XGuiAcceptanceScreenshot.Canvas canvas, string title, Float2[] sizes, bool wrong)
    {
        canvas.Fill(24, 24, 712, 312, 28, 30, 40, 255);
        canvas.Stroke(24, 24, 712, 312, 70, 74, 90, 2);
        canvas.Text(40, 40, title, 230, 232, 240, 2);
        canvas.Text(40, 68, "IMAGE.SETNATIVESIZE ACCEPTANCE", 140, 146, 160, 1);

        byte[][] colors =
        [
            [80, 170, 220],
            [90, 200, 150],
            [230, 170, 80],
        ];
        string[] labels = ["CHIP 48X24 X1", "CHIP 48X24 X2", "TILE 32X32 X1"];
        int[] xs = [56, 280, 504];
        for (int i = 0; i < sizes.Length; i++)
        {
            int w = Math.Max(1, (int)MathF.Round(sizes[i].X));
            int h = Math.Max(1, (int)MathF.Round(sizes[i].Y));
            int x = xs[i];
            int y = 180 - h / 2;
            byte[] c = colors[i];
            canvas.Fill(x, y, w, h, c[0], c[1], c[2], 255);
            canvas.Stroke(x - 2, y - 2, w + 4, h + 4, 12, 12, 16, 2);
            canvas.Text(x, 250, labels[i], 210, 214, 220, 1);
            canvas.Text(x, 268, $"{w}X{h}", wrong ? (byte)230 : (byte)120, wrong ? (byte)120 : (byte)210, wrong ? (byte)90 : (byte)140, 2);
        }
    }
}
