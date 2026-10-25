namespace XEngine.GpuAnimation.Tests;

internal static class GpuAnimationTestPaths
{
    public static string RepositoryRoot()
    {
        DirectoryInfo? current = new(AppContext.BaseDirectory);
        while (current != null)
        {
            if (File.Exists(Path.Combine(current.FullName, "XEngine.sln")))
                return current.FullName;
            current = current.Parent;
        }

        throw new DirectoryNotFoundException("Could not locate the XEngine repository root.");
    }

    public static string PackageRoot()
    {
        string root = Path.Combine(RepositoryRoot(), "Packages", "com.xengine.gpu-animation");
        if (File.Exists(Path.Combine(root, "package.json")))
            return root;
        throw new DirectoryNotFoundException("Could not locate com.xengine.gpu-animation.");
    }
}
