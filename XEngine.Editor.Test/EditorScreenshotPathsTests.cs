using XEngine.Editor.Core;

using Xunit;

namespace XEngine.Editor.Test;

public sealed class EditorScreenshotPathsTests : IDisposable
{
    private readonly string _previousDirectory = Directory.GetCurrentDirectory();
    private readonly string _root = Path.Combine(Path.GetTempPath(), "XEngineScreenshotPaths", Guid.NewGuid().ToString("N"));
    private string BundleResources => Path.Combine(_root, "安装 路径", "XEngine.app", "Contents", "Resources");
    private string ProjectRoot => Path.Combine(_root, "中文 视频工程");

    public EditorScreenshotPathsTests()
    {
        Directory.CreateDirectory(BundleResources);
        Directory.SetCurrentDirectory(BundleResources);
    }

    [Theory]
    [InlineData("gameview-frame.png")]
    [InlineData("InspectorPanel-frame.png")]
    public void DefaultsFollowProject_WhileWorkingDirectoryIsInsideBundle(string fileName)
    {
        string path = EditorScreenshotPaths.ResolveOutputPath(fileName, ProjectRoot);

        Assert.Equal(Path.Combine(ProjectRoot, ".xengine", "screenshots", fileName), path);
        Assert.True(Path.IsPathFullyQualified(path));
        Assert.Empty(Directory.EnumerateFileSystemEntries(BundleResources));
    }

    [Fact]
    public void NoProject_DefaultUsesUserDataOutsideBundle()
    {
        string path = EditorScreenshotPaths.ResolveOutputPath("Launcher.png", null);

        Assert.True(Path.IsPathFullyQualified(path));
        Assert.False(path.StartsWith(BundleResources + Path.DirectorySeparatorChar, StringComparison.Ordinal));
        Assert.Equal("Launcher.png", Path.GetFileName(path));
        Assert.Empty(Directory.EnumerateFileSystemEntries(BundleResources));
    }

    [Fact]
    public void ExplicitAbsoluteOutput_IsPreserved()
    {
        string requested = Path.Combine(_root, "外部 证据", "explicit.png");

        Assert.Equal(requested, EditorScreenshotPaths.ResolveOutputPath("ignored.png", ProjectRoot, requested));
    }

    [Fact]
    public void ExplicitRelativeOutput_RemainsRelativeToCallerWorkingDirectory()
    {
        Directory.SetCurrentDirectory(_root);

        string path = EditorScreenshotPaths.ResolveOutputPath("ignored.png", ProjectRoot, Path.Combine("证据", "explicit.png"));

        Assert.Equal(Path.Combine(_root, "证据", "explicit.png"), path);
    }

    public void Dispose()
    {
        Directory.SetCurrentDirectory(_previousDirectory);
        Directory.Delete(_root, recursive: true);
    }
}
