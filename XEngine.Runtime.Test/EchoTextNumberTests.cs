// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Echo;

using Xunit;

namespace XEngine.Runtime.Test;

/// <summary>
/// Echo text format regression: hand-authored assets use unsuffixed numbers ("0.72", "36").
/// Decimals must parse as Float (not fail an int conversion), bare digits stay Int, and
/// suffixed forms keep their exact types.
/// </summary>
public class EchoTextNumberTests
{
    [Fact]
    public void UnsuffixedDecimal_ParsesAsFloat()
    {
        var echo = EchoObject.ReadFromString("{\nAltitudeMax: 0.72\n}");
        Assert.True(echo.TryGet("AltitudeMax", out var value));
        Assert.NotNull(value);
        Assert.Equal(EchoType.Float, value!.TagType);
        Assert.Equal(0.72f, value.FloatValue, 5);
    }

    [Fact]
    public void UnsuffixedInteger_ParsesAsInt()
    {
        var echo = EchoObject.ReadFromString("{\nSeed: 36\n}");
        Assert.True(echo.TryGet("Seed", out var value));
        Assert.NotNull(value);
        Assert.Equal(EchoType.Int, value!.TagType);
        Assert.Equal(36, value.IntValue);
    }

    [Fact]
    public void SuffixedForms_KeepExactTypes()
    {
        var echo = EchoObject.ReadFromString("{\na: 5F\nb: 7B\nc: -3\n}");
        Assert.True(echo.TryGet("a", out var a));
        Assert.Equal(EchoType.Float, a!.TagType);
        Assert.Equal(5f, a.FloatValue, 5);
        Assert.True(echo.TryGet("b", out var b));
        Assert.Equal(EchoType.Byte, b!.TagType);
        Assert.Equal((byte)7, b.ByteValue);
        Assert.True(echo.TryGet("c", out var c));
        Assert.Equal(EchoType.Int, c!.TagType);
        Assert.Equal(-3, c.IntValue);
    }

    [Fact]
    public void NestedCompound_UnsuffixedDecimals_ParsesAsFloat()
    {
        var echo = EchoObject.ReadFromString(
            "{\n  \"$type\": \"XEngine.VoxelWorld.BiomeDefinition, XEngine.VoxelWorld.Runtime\",\n" +
            "  \"AltitudeMin\": 0,\n  \"AltitudeMax\": 0.72\n}");
        Assert.True(echo.TryGet("AltitudeMax", out var value));
        Assert.NotNull(value);
        Assert.Equal(EchoType.Float, value!.TagType);
        Assert.Equal(0.72f, value.FloatValue, 5);
        Assert.True(echo.TryGet("AltitudeMin", out var min));
        Assert.Equal(EchoType.Int, min!.TagType);
    }

    [Fact]
    public void Roundtrip_SuffixedFloat_IsStable()
    {
        var echo = EchoObject.ReadFromString("{\nx: 1.5F\n}");
        Assert.True(echo.TryGet("x", out var value));
        Assert.Equal(EchoType.Float, value!.TagType);
        Assert.Equal(1.5f, value.FloatValue, 5);
        var round = EchoObject.ReadFromString(EchoTextFormat.Write(echo));
        Assert.True(round.TryGet("x", out var roundValue));
        Assert.Equal(1.5f, roundValue!.FloatValue, 5);
    }
}
