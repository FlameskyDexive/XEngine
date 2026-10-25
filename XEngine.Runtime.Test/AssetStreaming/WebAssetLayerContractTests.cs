// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Runtime;
using XEngine.Runtime.MeshFeatures.Generation;

using Xunit;

namespace XEngine.Runtime.Test.AssetStreaming;

public sealed class WebAssetLayerContractTests
{
    [Fact]
    public void DesktopRuntimeKeepsBackgroundLoaderAndFullSdfParallelism()
    {
        Assert.True(AssetLoader.UsesBackgroundThread);
        Assert.Equal(-1, SDFGenerator.VoxelMaxDegreeOfParallelism);
        AssetLoader.Tick();
    }

    [Fact]
    public void WebCompilePathsForbidSyncMaterializeBackgroundThreadAndSdfParallelism()
    {
        string root = FindRepositoryRoot();
        string materializer = File.ReadAllText(
            Path.Combine(root, "XEngine.Runtime", "AssetStreaming", "MaterializingAssetStreamProvider.cs"));
        string loader = File.ReadAllText(Path.Combine(root, "XEngine.Runtime", "AssetLoader.cs"));
        string sdf = File.ReadAllText(
            Path.Combine(root, "XEngine.Runtime", "Resources", "MeshFeatures", "Generation", "SDFGenerator.cs"));
        string http = File.ReadAllText(
            Path.Combine(root, "XEngine.Runtime", "AssetStreaming", "HttpAssetStreamProvider.cs"));

        Assert.Contains("OpenReadAsync", materializer, StringComparison.Ordinal);
        Assert.Contains("Synchronous cache materialization is not allowed on WASM", materializer, StringComparison.Ordinal);
        Assert.DoesNotContain(
            "GetAwaiter().GetResult()",
            WebIfBody(materializer),
            StringComparison.Ordinal);

        Assert.Contains("#if XENGINE_WEB", loader, StringComparison.Ordinal);
        Assert.Contains("#if !XENGINE_WEB", loader, StringComparison.Ordinal);
        int notWeb = loader.IndexOf("#if !XENGINE_WEB", StringComparison.Ordinal);
        int thread = loader.IndexOf("new Thread", StringComparison.Ordinal);
        Assert.True(thread > notWeb, "new Thread must stay on the desktop (#if !XENGINE_WEB) path.");
        Assert.Contains("Drain(", loader, StringComparison.Ordinal);

        Assert.Contains("MaxDegreeOfParallelism = VoxelMaxDegreeOfParallelism", sdf, StringComparison.Ordinal);
        Assert.Contains("#if XENGINE_WEB", sdf, StringComparison.Ordinal);
        Assert.Contains("VoxelMaxDegreeOfParallelism =>", sdf, StringComparison.Ordinal);

        Assert.Contains("Use OpenReadAsync (fetch)", http, StringComparison.Ordinal);
        Assert.Contains("async XTask<Stream?> OpenReadAsync", http, StringComparison.Ordinal);
    }

    private static string WebIfBody(string source)
    {
        const string start = "#if XENGINE_WEB";
        const string end = "#else";
        int from = source.IndexOf(start, StringComparison.Ordinal);
        Assert.True(from >= 0, "missing #if XENGINE_WEB");
        int to = source.IndexOf(end, from, StringComparison.Ordinal);
        Assert.True(to > from, "missing #else after #if XENGINE_WEB");
        return source.Substring(from, to - from);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Directory.Build.props"))
                && File.Exists(Path.Combine(directory.FullName, "XEngine.Runtime", "XEngine.Runtime.csproj")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }

        directory = new DirectoryInfo(Environment.CurrentDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Directory.Build.props"))
                && File.Exists(Path.Combine(directory.FullName, "XEngine.Runtime", "XEngine.Runtime.csproj")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate the XEngine repository root.");
    }
}
