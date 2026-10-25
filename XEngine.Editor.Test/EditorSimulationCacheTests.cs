using System;
using System.IO;
using System.Security.Cryptography;
using XEngine.Editor.AssetBundles;
using Xunit;

namespace XEngine.Editor.Test;

public sealed class EditorSimulationCacheTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "XEngineSimulationCacheTests", Guid.NewGuid().ToString("N"));
    private static void Build(string output) => File.WriteAllText(Path.Combine(output, "bundle"), "good");

    [Fact]
    public void ReusesOnlyUnchangedCompleteBuild()
    {
        Assert.False(EditorSimulationCache.Prepare(_root, "configuration-1", Build));
        Assert.True(EditorSimulationCache.Prepare(_root, "configuration-1", Build));
        Assert.False(EditorSimulationCache.Prepare(_root, "configuration-2", Build));
        string file = Path.Combine(_root, "current", "bundle");
        var stamp = File.GetLastWriteTimeUtc(file);
        File.WriteAllText(file, "evil");
        File.SetLastWriteTimeUtc(file, stamp);
        Assert.False(EditorSimulationCache.Prepare(_root, "configuration-2", Build));
        File.Delete(file);
        Assert.False(EditorSimulationCache.Prepare(_root, "configuration-2", Build));
    }

    [Fact]
    public void FailedBuildCannotReusePartialFiles()
    {
        Assert.Throws<IOException>(() => EditorSimulationCache.Prepare(_root, "input", output =>
        {
            File.WriteAllText(Path.Combine(output, "partial"), "bad");
            throw new IOException("interrupted");
        }));
        Assert.False(EditorSimulationCache.Prepare(_root, "input", Build));
        Assert.False(File.Exists(Path.Combine(_root, "current", "partial")));
    }

    [Fact]
    public void InputFingerprintDetectsSameSizeSameTimestampRewrite()
    {
        Directory.CreateDirectory(_root);
        string file = Path.Combine(_root, "asset");
        File.WriteAllText(file, "aaaa");
        var stamp = File.GetLastWriteTimeUtc(file);
        string Hash()
        {
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            EditorSimulationCache.AppendFile(hash, file);
            return Convert.ToHexString(hash.GetHashAndReset());
        }
        string before = Hash();
        File.WriteAllText(file, "bbbb");
        File.SetLastWriteTimeUtc(file, stamp);
        Assert.NotEqual(before, Hash());
    }

    public void Dispose()
    {
        string parent = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "XEngineSimulationCacheTests"));
        if (Path.GetDirectoryName(Path.GetFullPath(_root)) != parent) throw new InvalidOperationException();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
