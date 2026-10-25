// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Runtime.Rendering;
using XEngine.Vector;

using Xunit;

namespace XEngine.Runtime.Test;

public sealed class AdaptiveProbeVolumeTests
{
    [Fact]
    public void AdaptiveProbeVolume_SamplesConstantIrradiance()
    {
        var volume = new AdaptiveProbeVolume
        {
            Origin = Float3.Zero,
            Size = new Float3(2f, 2f, 2f),
            ProbeSpacing = 1f,
        };
        volume.FillConstant(new Float3(0.2f, 0.4f, 0.6f));
        Assert.Equal(3 * 3 * 3, volume.ProbeCount);

        Float3 atOrigin = volume.SampleIrradiance(Float3.Zero, Float3.UnitY);
        Assert.True(atOrigin.X > 0.1);
        Assert.True(atOrigin.Y > 0.2);
        Assert.True(atOrigin.Z > 0.3);

        var empty = new AdaptiveProbeVolume();
        Assert.Equal(Float3.Zero, empty.SampleIrradiance(Float3.Zero, Float3.UnitY));
    }
}
