// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Echo;
using XEngine.Runtime.Resources;
using XEngine.Vector;

using Xunit;

namespace XEngine.Runtime.Test;

public sealed class CameraViewrectTests : RuntimeTestBase
{
    [Fact]
    public void FullViewrect_MatchesLegacyPixelSize()
    {
        Camera.ComputeViewrectPixels(
            new Rect(0f, 0f, 1f, 1f),
            1920,
            1080,
            1f,
            out int x,
            out int y,
            out uint rectW,
            out uint rectH,
            out uint pixelW,
            out uint pixelH);

        Assert.Equal(0, x);
        Assert.Equal(0, y);
        Assert.Equal(1920u, rectW);
        Assert.Equal(1080u, rectH);
        Assert.Equal(1920u, pixelW);
        Assert.Equal(1080u, pixelH);
    }

    [Fact]
    public void FullViewrect_RenderScale_OnlyShrinksInternalPixels()
    {
        Camera.ComputeViewrectPixels(
            new Rect(0f, 0f, 1f, 1f),
            1920,
            1080,
            0.5f,
            out _,
            out _,
            out uint rectW,
            out uint rectH,
            out uint pixelW,
            out uint pixelH);

        Assert.Equal(1920u, rectW);
        Assert.Equal(1080u, rectH);
        Assert.Equal(960u, pixelW);
        Assert.Equal(540u, pixelH);
    }

    [Fact]
    public void LeftAndRightHalves_DoNotOverlap()
    {
        Camera.ComputeViewrectPixels(
            new Rect(0f, 0f, 0.5f, 1f),
            1920,
            1080,
            1f,
            out int leftX,
            out _,
            out uint leftW,
            out uint leftH,
            out uint leftPixels,
            out _);
        Camera.ComputeViewrectPixels(
            new Rect(0.5f, 0f, 1f, 1f),
            1920,
            1080,
            1f,
            out int rightX,
            out _,
            out uint rightW,
            out uint rightH,
            out _,
            out _);

        Assert.Equal(0, leftX);
        Assert.Equal(960u, leftW);
        Assert.Equal(1080u, leftH);
        Assert.Equal(960u, leftPixels);
        Assert.Equal(960, rightX);
        Assert.Equal(960u, rightW);
        Assert.Equal(1080u, rightH);
        Assert.Equal(leftX + (int)leftW, rightX);
        Assert.Equal(1920, rightX + (int)rightW);
    }

    [Fact]
    public void ClampViewrect_RejectsOutOfRange()
    {
        Rect clamped = Camera.ClampViewrect(new Rect(-1f, -0.5f, 2f, 3f));
        Assert.Equal(0f, clamped.Min.X);
        Assert.Equal(0f, clamped.Min.Y);
        Assert.Equal(1f, clamped.Max.X);
        Assert.Equal(1f, clamped.Max.Y);
    }

    [Fact]
    public void EchoRoundTrip_PreservesViewrect()
    {
        var go = CreateGameObject("SplitCam");
        var camera = go.AddComponent<Camera>();
        camera.Viewrect = new Rect(0.5f, 0f, 1f, 1f);

        var clone = Serializer.Deserialize<GameObject>(Serializer.Serialize(go));
        var cloneCam = clone.GetComponent<Camera>();

        Assert.NotNull(cloneCam);
        Assert.Equal(0.5f, cloneCam.Viewrect.Min.X);
        Assert.Equal(0f, cloneCam.Viewrect.Min.Y);
        Assert.Equal(1f, cloneCam.Viewrect.Max.X);
        Assert.Equal(1f, cloneCam.Viewrect.Max.Y);
    }
}
