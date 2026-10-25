using XHub.Services;
using Xunit;

namespace XHub.Test;

public class EngineVersionTests
{
    [Theory]
    [InlineData("V1.0.0a1", "1.0.0-a1")]
    [InlineData("1.0.0a1", "1.0.0-a1")]
    [InlineData("1.0.0-a1", "1.0.0-a1")]
    [InlineData("v1.0.0-alpha.1", "1.0.0-a.1")]
    [InlineData("V1.0.0a1+build.42", "1.0.0-a1")]
    [InlineData("V1.0.0", "1.0.0")]
    [InlineData("1.0.0", "1.0.0")]
    [InlineData("1.2", "1.2.0")]
    [InlineData("0.0.1", "0.0.1")]
    public void TryParse_ProducesCanonicalForm(string input, string expectedCanonical)
    {
        Assert.True(EngineVersion.TryParse(input, out EngineVersion version));
        Assert.Equal(expectedCanonical, version.Canonical);
        Assert.Equal(input, version.Display); // display keeps the original text
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("hello")]
    [InlineData("1.x.0")]
    [InlineData("1.0.0-")]
    [InlineData("a1.0.0")]
    public void TryParse_RejectsGarbage(string input)
    {
        Assert.False(EngineVersion.TryParse(input, out _));
    }

    [Fact]
    public void Channel_PreviewOnlyWhenPrereleasePresent()
    {
        Assert.True(EngineVersion.TryParse("V1.0.0a1", out EngineVersion preview));
        Assert.True(preview.IsPreview);
        Assert.Equal("preview", preview.Channel);

        Assert.True(EngineVersion.TryParse("1.0.0", out EngineVersion stable));
        Assert.False(stable.IsPreview);
        Assert.Equal("stable", stable.Channel);
    }

    [Fact]
    public void CompareTo_SemanticOrdering()
    {
        var v100a1 = P("1.0.0-a1");
        var v100a2 = P("1.0.0-a2");
        var v100b1 = P("1.0.0-b1");
        var v100rc1 = P("1.0.0-rc1");
        var v100 = P("1.0.0");
        var v101 = P("1.0.1");
        var v110 = P("1.1.0");
        var v200 = P("2.0.0");

        Assert.True(v100a1.CompareTo(v100a2) < 0);
        Assert.True(v100a2.CompareTo(v100b1) < 0);
        Assert.True(v100b1.CompareTo(v100rc1) < 0);
        Assert.True(v100rc1.CompareTo(v100) < 0);      // prerelease < release
        Assert.True(v100.CompareTo(v101) < 0);
        Assert.True(v101.CompareTo(v110) < 0);
        Assert.True(v110.CompareTo(v200) < 0);
        Assert.True(v200.CompareTo(v100a1) > 0);

        // The display form and the canonical form denote the same version.
        Assert.Equal(0, P("V1.0.0a1").CompareTo(P("1.0.0-a1")));
    }

    [Fact]
    public void IsDowngrade_DetectsOlderEditor()
    {
        var project = P("1.0.1");      // stamped by a newer editor
        var editor = P("1.0.0-a1");    // candidate older editor
        Assert.True(EngineVersion.IsDowngrade(project, editor));
        Assert.False(EngineVersion.IsDowngrade(P("1.0.0-a1"), P("1.0.0-a1")));
        Assert.False(EngineVersion.IsDowngrade(P("1.0.0-a1"), P("1.0.1")));
    }

    [Fact]
    public void Sorting_IsNotStringOrdering()
    {
        // String sort would order "1.0.10" < "1.0.9"; semantic ordering must not.
        Assert.True(P("1.0.10").CompareTo(P("1.0.9")) > 0);
    }

    private static EngineVersion P(string text)
    {
        Assert.True(EngineVersion.TryParse(text, out EngineVersion version));
        return version;
    }
}
