using XEngine.Editor.Projects.Scripting;
using XEngine.Editor.Projects.Settings;

using Xunit;

namespace XEngine.Editor.Test;

public sealed class WebPlatformFilteringTests
{
    [Fact]
    public void BuildPlatforms_RegistersCanonicalWebName()
    {
        Assert.Equal("Web", BuildPlatforms.Web);
        Assert.Contains(BuildPlatforms.Web, BuildPlatforms.Players);
        Assert.Contains(BuildPlatforms.Web, BuildPlatforms.All);
        Assert.Equal(BuildPlatforms.Web, BuildPlatforms.Normalize("web"));
        Assert.Equal(BuildPlatforms.Web, BuildPlatforms.FromBuildTarget(BuildTarget.Web));
    }

    [Fact]
    public void AssemblyDefinition_CanIncludeWebWithoutIncludingDesktop()
    {
        var definition = new AssemblyDefinition
        {
            IncludePlatforms = { BuildPlatforms.Web },
        };

        Assert.True(definition.IncludedFor(BuildPlatforms.Web));
        Assert.False(definition.IncludedFor(BuildPlatforms.Windows));
        Assert.False(definition.IncludedFor(BuildPlatforms.Android));
    }
}
