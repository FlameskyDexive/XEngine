// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.PackageRegistry;

namespace XEngine.PackageRegistry.Tests;

public class NpmPackageNameTests
{
    [Theory]
    [InlineData("com.xengine.spine", "@inxep/spine")]
    [InlineData("com.xengine.2d.sprite", "@inxep/2d.sprite")]
    public void ToNpmName_MapsReverseDomainToScope(string engine, string npm)
        => Assert.Equal(npm, NpmPackageName.ToNpmName(engine));

    [Theory]
    [InlineData("@inxep/spine", "com.xengine.spine")]
    [InlineData("@inxep/2d.sprite", "com.xengine.2d.sprite")]
    public void FromNpmName_MapsScopeToReverseDomain(string npm, string engine)
        => Assert.Equal(engine, NpmPackageName.FromNpmName(npm));

    [Theory]
    [InlineData("com.xengine.spine")]
    [InlineData("com.other.tools")]
    [InlineData("@myteam/lib")]
    public void NamesRoundTrip_ThroughNpmForm(string name)
        => Assert.Equal(name, NpmPackageName.FromNpmName(NpmPackageName.ToNpmName(name)));

    [Fact]
    public void NonEngineNames_PassThroughUnchanged()
    {
        Assert.Equal("com.myteam.tools", NpmPackageName.ToNpmName("com.myteam.tools"));
        Assert.Equal("com.myteam.tools", NpmPackageName.FromNpmName("com.myteam.tools"));
        Assert.Equal("@myteam/lib", NpmPackageName.FromNpmName("@myteam/lib"));
        Assert.Equal("@myteam/lib", NpmPackageName.ToNpmName("@myteam/lib"));
    }

    [Theory]
    [InlineData("com.xengine.spine", true)]
    [InlineData("com.xengine", false)]
    [InlineData("com.xengine.", false)]
    [InlineData("org.xengine.spine", false)]
    public void IsInternalName_ClassifiesCorrectly(string name, bool expected)
        => Assert.Equal(expected, NpmPackageName.IsInternalName(name));

    [Fact]
    public void ToUrlSegment_EncodesScopedSlash()
    {
        Assert.Equal("@inxep%2fspine", NpmPackageName.ToUrlSegment("@inxep/spine"));
        Assert.Equal("com.myteam.tools", NpmPackageName.ToUrlSegment("com.myteam.tools"));
    }

    [Fact]
    public void TarballFileName_FlattensScope()
    {
        Assert.Equal("inxep-spine-0.2.0.tgz", NpmPackageName.TarballFileName("@inxep/spine", "0.2.0"));
        Assert.Equal("myteam-tools-1.0.0.tgz", NpmPackageName.TarballFileName("@myteam/tools", "1.0.0"));
    }
}
