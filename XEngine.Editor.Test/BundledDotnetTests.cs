using XEngine.Editor.Projects.Scripting;
using Xunit;

namespace XEngine.Editor.Test;

public class BundledDotnetTests
{
    [Fact]
    public void FindRoot_RequiresHostAndSdk_HandlesSpaces()
    {
        string editor = Path.Combine(Path.GetTempPath(), "XEngine SDK " + Guid.NewGuid());
        string root = Path.Combine(editor, "dotnet");
        Directory.CreateDirectory(root);
        try
        {
            Assert.Null(BundledDotnet.FindRoot(editor));
            File.WriteAllText(Path.Combine(root, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"), "");
            Assert.Null(BundledDotnet.FindRoot(editor));
            Directory.CreateDirectory(Path.Combine(root, "sdk"));
            Assert.Equal(root, BundledDotnet.FindRoot(editor));
        }
        finally { Directory.Delete(editor, true); }
    }

    [Theory]
    [InlineData(true, true, true)]
    [InlineData(true, false, false)]
    [InlineData(false, true, false)]
    public void FindRoot_MacosBundleResourcesRequireCompleteSdk(bool host, bool sdk, bool expected)
    {
        string testRoot = Path.Combine(Path.GetTempPath(), "XEngine SDK bundle " + Guid.NewGuid());
        string editor = Path.Combine(testRoot, "独立 安装", "XEngine.app", "Contents", "MacOS");
        string resources = Path.Combine(testRoot, "独立 安装", "XEngine.app", "Contents", "Resources", "dotnet");
        Directory.CreateDirectory(editor);
        Directory.CreateDirectory(resources);
        try
        {
            if (host) File.WriteAllText(Path.Combine(resources, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"), "");
            if (sdk) Directory.CreateDirectory(Path.Combine(resources, "sdk"));
            Assert.Equal(expected ? resources : null, BundledDotnet.FindRoot(editor + Path.DirectorySeparatorChar));
        }
        finally { Directory.Delete(testRoot, true); }
    }

    [Fact]
    public void FindRoot_DoesNotUseResourcesNextToAnOrdinaryEditorDirectory()
    {
        string testRoot = Path.Combine(Path.GetTempPath(), "XEngine SDK ordinary " + Guid.NewGuid());
        string editor = Path.Combine(testRoot, "Editor");
        string resources = Path.Combine(testRoot, "Resources", "dotnet");
        Directory.CreateDirectory(editor);
        Directory.CreateDirectory(Path.Combine(resources, "sdk"));
        try
        {
            File.WriteAllText(Path.Combine(resources, OperatingSystem.IsWindows() ? "dotnet.exe" : "dotnet"), "");
            Assert.Null(BundledDotnet.FindRoot(editor));
        }
        finally { Directory.Delete(testRoot, true); }
    }
}
