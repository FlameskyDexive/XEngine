using System;

using Xunit;

using XEngine.Runtime;
using XEngine.Runtime.RHI;

namespace XEngine.GpuAnimation.Tests;

/// <summary>
/// GPU round-trip of the lazily built Float4 animation texture (hudmesh/VirtualGeometry
/// headless-Vulkan acceptance pattern). Silently skips when no Vulkan device is available.
/// </summary>
[Collection("GpuSerial")]
public sealed class GpuAnimationTextureTests : IDisposable
{
    private readonly GraphicsBackend? _previousBackend;

    public GpuAnimationTextureTests()
    {
        _previousBackend = GraphicsBackendSelection.Preferred;
        GraphicsBackendSelection.Preferred = GraphicsBackend.Vulkan;
        try
        {
            Graphics.Initialize(debug: false);
        }
        catch (Exception)
        {
            GraphicsBackendSelection.Preferred = _previousBackend; // GPU unavailable — tests skip
        }
    }

    public void Dispose()
    {
        if (Graphics.Device != null)
            Graphics.Dispose();
        GraphicsBackendSelection.Preferred = _previousBackend;
    }

    [Fact]
    public void PositionTexture_RoundTripsTexelsExactly()
    {
        if (Graphics.Device == null)
            return; // no Vulkan on this host — skip silently

        const int width = 4;
        const int height = 8;
        var asset = new BakedGpuAnimation("tex.gpuanim")
        {
            TextureWidth = width,
            TextureHeight = height,
            VertexCount = 3,
            HasNormals = false,
        };
        var positions = new float[width * height * 4];
        for (int row = 0; row < height; row++)
        for (int col = 0; col < width; col++)
        {
            int i = (row * width + col) * 4;
            positions[i] = row * 10f + col;
            positions[i + 1] = -1.5f * row;
            positions[i + 2] = col * 0.5f;
            positions[i + 3] = 1f;
        }
        asset.Positions = positions;

        var texture = asset.GetPositionTexture();
        Assert.NotNull(texture);
        var readBack = new float[width * height * 4];
        texture!.GetData(new Memory<float>(readBack));

        for (int i = 0; i < positions.Length; i++)
            Assert.Equal(positions[i].ToString("R"), readBack[i].ToString("R"));

        // Lazy reuse: a second fetch returns the same instance while unmodified.
        Assert.Same(texture, asset.GetPositionTexture());

        asset.Dispose();
    }

    [Fact]
    public void HalfPrecisionTexture_RoundTripsWithinHalfEpsilon()
    {
        if (Graphics.Device == null)
            return; // no Vulkan on this host — skip silently

        const int width = 4;
        const int height = 8;
        var asset = new BakedGpuAnimation("tex16.gpuanim")
        {
            TextureWidth = width,
            TextureHeight = height,
            VertexCount = 3,
            HasNormals = false,
            Precision = GpuAnimationPrecision.Half16,
        };
        var positions = new float[width * height * 4];
        for (int row = 0; row < height; row++)
        for (int col = 0; col < width; col++)
        {
            int i = (row * width + col) * 4;
            positions[i] = row * 3.25f + col * 0.125f;
            positions[i + 1] = -row * 0.5f;
            positions[i + 2] = col * 0.25f;
            positions[i + 3] = 1f;
        }
        asset.Positions = positions;

        var texture = asset.GetPositionTexture();
        Assert.NotNull(texture);
        var readBack = new XEngine.Vector.Half4[width * height];
        texture!.GetData(new Memory<XEngine.Vector.Half4>(readBack));

        // half epsilon at these magnitudes (<= 26) is 1/128 ≈ 0.0078
        for (int i = 0; i < readBack.Length; i++)
        {
            Assert.Equal(positions[i * 4], readBack[i].X, 2);
            Assert.Equal(positions[i * 4 + 1], readBack[i].Y, 2);
            Assert.Equal(positions[i * 4 + 2], readBack[i].Z, 2);
            Assert.Equal(1f, readBack[i].W, 2);
        }
        Assert.Same(texture, asset.GetPositionTexture());
        asset.Dispose();
    }
}
