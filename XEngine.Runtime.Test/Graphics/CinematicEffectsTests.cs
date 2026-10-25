// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Runtime.Rendering;

using Xunit;

namespace XEngine.Runtime.Test;

public sealed class CinematicEffectsTests
{
    [Fact]
    public void EffectFlagsRepresentAll512RuntimeCombinationsAndGateMissingLut()
    {
        var effect = new CinematicEffects();
        for (int mask = 0; mask <= (int)CinematicEffectFlags.All; mask++)
        {
            effect.EnableVignette = (mask & (int)CinematicEffectFlags.Vignette) != 0;
            effect.EnableChromaticAberration = (mask & (int)CinematicEffectFlags.ChromaticAberration) != 0;
            effect.EnableFilmGrain = (mask & (int)CinematicEffectFlags.FilmGrain) != 0;
            effect.EnableColorGrading = (mask & (int)CinematicEffectFlags.ColorGrading) != 0;
            effect.EnableSharpen = (mask & (int)CinematicEffectFlags.Sharpen) != 0;
            effect.EnableEdgeDetection = (mask & (int)CinematicEffectFlags.EdgeDetection) != 0;
            effect.EnablePixelation = (mask & (int)CinematicEffectFlags.Pixelation) != 0;
            effect.EnableGodRays = (mask & (int)CinematicEffectFlags.GodRays) != 0;

            bool useLut = (mask & (int)CinematicEffectFlags.Lut) != 0;
            Assert.Equal((CinematicEffectFlags)mask, effect.BuildEffectFlags(useLut));
        }

        Assert.Equal(
            CinematicEffectFlags.All & ~CinematicEffectFlags.Lut,
            effect.BuildEffectFlags(useLut: false));
    }
}
