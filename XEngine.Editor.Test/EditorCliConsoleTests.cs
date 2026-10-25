using System.IO;

using XEngine.Editor.GUI;

using Xunit;

namespace XEngine.Editor.Test;

public sealed class ProjectOpenProgressTests : IDisposable
{
    public ProjectOpenProgressTests()
    {
        ProjectOpenProgress.Cancel();
    }

    public void Dispose() => ProjectOpenProgress.Cancel();

    [Fact]
    public void Begin_ShowsFirstStepWithoutRunningWork()
    {
        ProjectOpenProgress.Begin();

        Assert.True(ProjectOpenProgress.IsActive);
        Assert.Equal(0, ProjectOpenProgress.DisplayedStep);
        // Steps were reordered scripts-first (c92da5a18): step 0 is the script compile, matching
        // ExecuteProjectOpenStep — the old assertion expected the pre-reorder "assets" label.
        Assert.Equal("launcher.progress.scripts", ProjectOpenProgress.StepKey);
        Assert.InRange(ProjectOpenProgress.Fraction, 0.08f, 0.4f);
        Assert.False(ProjectOpenProgress.TryBeginWork());
    }

    [Fact]
    public void Tick_RunsEachDisplayedStepThenCompletes()
    {
        ProjectOpenProgress.Begin();
        Assert.False(ProjectOpenProgress.TryBeginWork());

        float previous = ProjectOpenProgress.Fraction;
        for (int i = 0; i < ProjectOpenProgress.StepCount; i++)
        {
            Assert.True(ProjectOpenProgress.IsActive);
            Assert.Equal(i, ProjectOpenProgress.DisplayedStep);
            Assert.True(ProjectOpenProgress.TryBeginWork());
            bool done = ProjectOpenProgress.CompleteCurrentStep();
            Assert.Equal(i == ProjectOpenProgress.StepCount - 1, done);
            if (!done)
            {
                Assert.True(ProjectOpenProgress.Fraction > previous);
                previous = ProjectOpenProgress.Fraction;
            }
        }

        Assert.False(ProjectOpenProgress.IsActive);
        Assert.False(ProjectOpenProgress.TryBeginWork());
    }

    [Fact]
    public void Cancel_ResetsLoadingState()
    {
        ProjectOpenProgress.Begin();
        Assert.False(ProjectOpenProgress.TryBeginWork());
        Assert.True(ProjectOpenProgress.TryBeginWork());

        ProjectOpenProgress.Cancel();

        Assert.False(ProjectOpenProgress.IsActive);
        Assert.Equal(0, ProjectOpenProgress.DisplayedStep);
        Assert.Equal(0f, ProjectOpenProgress.Fraction);
    }

    [Fact]
    public void StepKeys_CoverTheFiveProjectOpenPhases()
    {
        Assert.Equal(5, ProjectOpenProgress.StepCount);
        Assert.Contains("launcher.progress.assets", ProjectOpenProgress.StepKeys);
        Assert.Contains("launcher.progress.scripts", ProjectOpenProgress.StepKeys);
        Assert.Contains("launcher.progress.registries", ProjectOpenProgress.StepKeys);
        Assert.Contains("launcher.progress.layout", ProjectOpenProgress.StepKeys);
        Assert.Contains("launcher.progress.scene", ProjectOpenProgress.StepKeys);
    }
}

public sealed class EditorCliConsoleTests
{
    [Fact]
    public void NeedsCliConsole_EmptyOrGuiArgs_AreFalse()
    {
        Assert.False(Program.NeedsCliConsole([]));
        Assert.False(Program.NeedsCliConsole(["--project", @"D:\game"]));
        Assert.False(Program.NeedsCliConsole(["--serve"]));
    }

    [Theory]
    [InlineData("--help")]
    [InlineData("-h")]
    [InlineData("-help")]
    [InlineData("--refresh-assets")]
    [InlineData("--buildmode")]
    [InlineData("-b")]
    [InlineData("--import-android-toolchain")]
    [InlineData("--resolve-android-toolchain")]
    [InlineData("--verify-android-toolchain-lock")]
    [InlineData("--import-harmonyos-bundle")]
    [InlineData("--resolve-harmonyos-sdk")]
    public void NeedsCliConsole_StdoutCommands_AreTrue(string flag)
    {
        Assert.True(Program.NeedsCliConsole([flag]));
    }

    [Fact]
    public void ReleaseEditor_UsesWinExeSoDoubleClickHasNoConsole()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        string? repo = null;
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "XEngine.sln")))
            {
                repo = current.FullName;
                break;
            }
            current = current.Parent;
        }
        Assert.False(string.IsNullOrEmpty(repo), "Could not locate the XEngine repository root.");
        string csproj = Path.Combine(repo!, "XEngine.Editor", "XEngine.Editor.csproj");
        Assert.True(File.Exists(csproj), csproj);

        string xml = File.ReadAllText(csproj);
        Assert.Contains("<OutputType>Exe</OutputType>", xml);
        Assert.Contains("<OutputType>WinExe</OutputType>", xml);
        Assert.Contains("'$(Configuration)' == 'Release'", xml);
    }
}
