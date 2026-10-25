// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Runtime;
using XEngine.Runtime.Rendering;
using XEngine.Runtime.RHI;
using XEngine.Vector;

using Xunit;

namespace XEngine.Runtime.Test;

public sealed class DirectionalShadowAtlasBudgetTests : IDisposable
{
    private readonly GraphicsBackend? _previousBackend = GraphicsBackendSelection.Preferred;
    private readonly int _previousAtlasSize = QualitySettings.ShadowAtlasSize;
    private readonly bool _previousShadows = QualitySettings.ShadowsEnabled;
    private readonly float _previousDistance = QualitySettings.ShadowDistance;

    public DirectionalShadowAtlasBudgetTests()
    {
        GraphicsBackendSelection.Preferred = GraphicsBackend.Null;
        Graphics.Initialize(debug: false);
        ShadowAtlas.Dispose();
        QualitySettings.ShadowsEnabled = true;
        QualitySettings.ShadowDistance = 100f;
    }

    [Theory]
    [InlineData(512, 4096, 4, 256)]
    [InlineData(1024, 2048, 4, 512)]
    [InlineData(2048, 2048, 4, 1024)]
    [InlineData(2048, 4096, 4, 1024)]
    [InlineData(4096, 2048, 4, 2048)]
    [InlineData(4096, 4096, 4, 2048)]
    [InlineData(2048, 2048, 2, 1024)]
    [InlineData(2048, 2048, 1, 2048)]
    [InlineData(512, 4096, 1, 512)]
    public void EveryCascadeGetsAnIndependentTileWithinTheAtlas(
        int atlasSize, int authoredResolution, int cascadeCount, int expectedFirstResolution)
    {
        QualitySettings.ShadowAtlasSize = atlasSize;
        using var lightObject = new GameObject("sun");
        DirectionalLight light = lightObject.AddComponent<DirectionalLight>();
        light.ShadowResolution = (DirectionalLight.Resolution)authoredResolution;
        light.Cascades = (DirectionalLight.CascadeCount)cascadeCount;
        light.ShadowDistance = 55f;
        light.CastShadows = true;
        using var pipeline = new UniversalRenderPipeline();

        // Exercise the actual allocation path, starting with no atlas, rather than duplicating
        // its sizing arithmetic. In the regression only cascade zero received a tile.
        light.RenderShadows(pipeline, new Float3(0f, 7f, -8.5f), Array.Empty<IRenderable>());

        ForwardLightData data = light.GetForwardLightData();
        Assert.Equal(cascadeCount, data.CascadeCount);
        Assert.Equal(expectedFirstResolution, data.CascadeAtlasParams[0].Z);
        for (int i = 0; i < cascadeCount; i++)
        {
            Float4 tile = data.CascadeAtlasParams[i];
            Assert.True(tile.Z >= 32f, $"cascade {i} has no shadow tile");
            Assert.InRange(tile.X, 0f, atlasSize - tile.Z);
            Assert.InRange(tile.Y, 0f, atlasSize - tile.Z);
            Assert.Equal(55f * (i + 1) / cascadeCount, tile.W);
            for (int j = 0; j < i; j++)
            {
                Float4 other = data.CascadeAtlasParams[j];
                Assert.True(tile.X + tile.Z <= other.X || other.X + other.Z <= tile.X
                    || tile.Y + tile.Z <= other.Y || other.Y + other.Z <= tile.Y,
                    $"cascades {j} and {i} overlap");
            }
        }
        Assert.Equal((DirectionalLight.Resolution)authoredResolution, light.ShadowResolution);
    }

    public void Dispose()
    {
        Graphics.Dispose();
        GraphicsBackendSelection.Preferred = _previousBackend;
        QualitySettings.ShadowAtlasSize = _previousAtlasSize;
        QualitySettings.ShadowsEnabled = _previousShadows;
        QualitySettings.ShadowDistance = _previousDistance;
    }
}
