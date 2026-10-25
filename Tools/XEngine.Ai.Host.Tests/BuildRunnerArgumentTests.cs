using XEngine.Ai.Host;

using Xunit;

namespace XEngine.Ai.Host.Tests;

/// <summary>
/// BP5: the host shells out to the editor, so the only thing it owns is the command line. A profile
/// name with a space in it has to survive that hand-off, and omitted options must not appear at all.
/// </summary>
public class BuildRunnerArgumentTests
{
    [Fact]
    public void Build_WithoutSelection_LeavesThePlatformToTheProject()
    {
        string args = BuildRunner.BuildArguments(@"C:\proj", @"C:\out", null, null);

        Assert.Equal("--buildmode \"C:\\proj\" -o \"C:\\out\"", args);
    }

    [Fact]
    public void Build_QuotesAProfileNameContainingSpaces()
    {
        string args = BuildRunner.BuildArguments(@"C:\proj", @"C:\out", "Android Store", null);

        Assert.Contains("--build-profile \"Android Store\"", args, StringComparison.Ordinal);
        Assert.DoesNotContain("--platform", args, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_ForwardsPlatformOnItsOwn()
    {
        string args = BuildRunner.BuildArguments(@"C:\proj", @"C:\out", null, "android");

        Assert.Contains("--platform \"android\"", args, StringComparison.Ordinal);
        Assert.DoesNotContain("--build-profile", args, StringComparison.Ordinal);
    }

    [Fact]
    public void Build_ForwardsIosPlatformOnItsOwn()
    {
        string args = BuildRunner.BuildArguments(@"C:\proj", @"C:\out", null, "ios");

        Assert.Contains("--platform \"ios\"", args, StringComparison.Ordinal);
        Assert.DoesNotContain("--build-profile", args, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Build_TreatsBlankSelectionAsAbsent(string blank)
    {
        string args = BuildRunner.BuildArguments(@"C:\proj", @"C:\out", blank, blank);

        Assert.Equal("--buildmode \"C:\\proj\" -o \"C:\\out\"", args);
    }
}
