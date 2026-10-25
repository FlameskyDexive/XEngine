using System;
using System.IO;

using XEngine.Editor.Projects.Packages;

using Xunit;

namespace XEngine.Editor.Test;

public class EditorMacPackageLayoutTests
{
    [Fact]
    public void BundleUsesResourcesPackages_DevelopmentOutputUsesAdjacentPackages()
    {
        string root = Path.Combine(Path.GetTempPath(), "xengine-package-layout-" + Guid.NewGuid().ToString("N"));
        string macos = Path.Combine(root, "XEngine.app", "Contents", "MacOS");
        string resources = Path.Combine(root, "XEngine.app", "Contents", "Resources", "Packages");
        try
        {
            Directory.CreateDirectory(macos);
            Directory.CreateDirectory(resources);
            Assert.Equal(resources, PackageManager.ResolveBuiltInPackagesPath(macos));

            string developmentOutput = Path.Combine(root, "build", "net10.0");
            Directory.CreateDirectory(developmentOutput);
            Assert.Equal(Path.Combine(developmentOutput, "Packages"),
                PackageManager.ResolveBuiltInPackagesPath(developmentOutput));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
