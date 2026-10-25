// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Editor.Projects.Packages;

using Xunit;

namespace XEngine.Editor.Test;

/// <summary>
/// P1 unit tests for the SemVer parser, version-range matcher, git-source parser, and the
/// package-json-declared transitive dependency closure. These are pure-logic tests (no SDK, no
/// network) — they run as part of the fast test set.
/// </summary>
public class PackageVersioningTests
{
    // ---- SemanticVersion parsing + comparison ----

    [Theory]
    [InlineData("1.2.3", 1, 2, 3)]
    [InlineData("v1.2.3", 1, 2, 3)]
    [InlineData("0.0.0", 0, 0, 0)]
    [InlineData("10.20.30", 10, 20, 30)]
    public void Parse_ReleaseVersion(string input, int major, int minor, int patch)
    {
        var v = SemanticVersion.Parse(input);
        Assert.Equal(major, v.Major);
        Assert.Equal(minor, v.Minor);
        Assert.Equal(patch, v.Patch);
        Assert.False(v.IsPreRelease);
    }

    [Theory]
    [InlineData("1.2.3-alpha", true)]
    [InlineData("1.2.3-alpha.1", true)]
    [InlineData("1.2.3", false)]
    [InlineData("1.2.3+build", false)]
    public void PreRelease_Detected(string input, bool expected)
    {
        var v = SemanticVersion.Parse(input);
        Assert.Equal(expected, v.IsPreRelease);
    }

    [Theory]
    [InlineData("1.0.0", "2.0.0", -1)]
    [InlineData("2.0.0", "1.0.0", 1)]
    [InlineData("1.2.0", "1.3.0", -1)]
    [InlineData("1.2.3", "1.2.3", 0)]
    [InlineData("1.0.0-alpha", "1.0.0", -1)]   // pre-release < release
    [InlineData("1.0.0-alpha", "1.0.0-beta", -1)]
    [InlineData("1.0.0-alpha.1", "1.0.0-alpha.2", -1)]
    [InlineData("1.0.0-rc.1", "1.0.0-alpha.1", 1)] // rc > alpha
    public void CompareTo_Precedence(string a, string b, int expectedSign)
    {
        var va = SemanticVersion.Parse(a);
        var vb = SemanticVersion.Parse(b);
        int cmp = va.CompareTo(vb);
        Assert.Equal(expectedSign, cmp < 0 ? -1 : cmp > 0 ? 1 : 0);
    }

    // ---- VersionRange matching ----

    [Theory]
    [InlineData("1.2.3", "1.2.3", true)]     // caret allows same major
    [InlineData("1.2.3", "1.9.0", true)]
    [InlineData("1.2.3", "2.0.0", false)]    // caret caps at next major
    [InlineData("1.2.3", "1.1.0", false)]    // below floor
    [InlineData("^1.2.3", "1.5.0", true)]
    [InlineData("~1.2.3", "1.2.9", true)]
    [InlineData("~1.2.3", "1.3.0", false)]   // tilde caps at next minor
    [InlineData("1.x", "1.9.9", true)]
    [InlineData("1.x", "2.0.0", false)]
    [InlineData("1.2.x", "1.2.5", true)]
    [InlineData("1.2.x", "1.3.0", false)]
    [InlineData("*", "99.99.99", true)]
    [InlineData(">=1.2.0", "1.2.0", true)]
    [InlineData(">=1.2.0", "1.1.0", false)]
    [InlineData(">1.2.0", "1.2.0", false)]
    [InlineData(">1.2.0", "1.2.1", true)]
    [InlineData("<2.0.0", "1.99.99", true)]
    [InlineData("<2.0.0", "2.0.0", false)]
    [InlineData("=1.2.3", "1.2.3", true)]
    [InlineData("=1.2.3", "1.2.4", false)]
    public void RangeSatisfies(string rangeSpec, string version, bool expected)
    {
        var range = VersionRange.TryParse(rangeSpec);
        Assert.NotNull(range);
        Assert.Equal(expected, range!.Value.Satisfies(SemanticVersion.Parse(version)));
    }

    [Theory]
    [InlineData("file:../foo", null)]   // non-version specifiers return null
    [InlineData("embed:bar", null)]
    [InlineData("git@https://x.git#v1", null)]
    public void NonVersionSpecifiers_ReturnNull(string spec, object? _)
    {
        Assert.Null(VersionRange.TryParse(spec));
    }

    // ---- 0.x caret rule (npm: ^0.2.3 only allows 0.2.x) ----

    [Theory]
    [InlineData("0.2.3", "0.2.9", true)]
    [InlineData("0.2.3", "0.3.0", false)] // 0.x caret caps at next minor
    [InlineData("0.2.3", "1.0.0", false)]
    public void CaretZeroMajor_Rules(string range, string version, bool expected)
    {
        var r = VersionRange.TryParse(range);
        Assert.NotNull(r);
        Assert.Equal(expected, r!.Value.Satisfies(SemanticVersion.Parse(version)));
    }

    // ---- Git source parser ----

    [Theory]
    [InlineData("git@https://github.com/x/y.git#v1.2.3", "https://github.com/x/y.git", "v1.2.3")]
    [InlineData("git@https://github.com/x/y.git", "https://github.com/x/y.git", "HEAD")]
    [InlineData("git://example.com/x.git#main", "git://example.com/x.git", "main")]
    public void GitParse_UrlAndRef(string spec, string expectedUrl, string expectedRef)
    {
        var parsed = GitPackageSource.Parse(spec);
        Assert.NotNull(parsed);
        Assert.Equal(expectedUrl, parsed!.Value.url);
        Assert.Equal(expectedRef, parsed.Value.@ref);
    }

    [Theory]
    [InlineData("file:../x")]
    [InlineData("1.2.3")]
    [InlineData("embed:y")]
    public void GitParse_NonGitSpecifiers_ReturnNull(string spec)
    {
        Assert.Null(GitPackageSource.Parse(spec));
    }

    // ---- Any range ----

    [Fact]
    public void AnyRange_MatchesEverything()
    {
        Assert.True(VersionRange.Any.Satisfies(SemanticVersion.Parse("0.0.1")));
        Assert.True(VersionRange.Any.Satisfies(SemanticVersion.Parse("99.99.99")));
        Assert.True(VersionRange.Any.Satisfies(SemanticVersion.Parse("1.0.0-alpha")));
    }
}
