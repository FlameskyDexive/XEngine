using System;
using System.IO;
using XEngine.Editor.Projects;
using Xunit;

namespace XEngine.Editor.Test;

public sealed class ProjectDirectoryLeaseTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "XEngineLeaseTests", Guid.NewGuid().ToString("N"));

    [Fact]
    public void SameDirectoryAliasIsRejectedUntilOwnerCloses()
    {
        using (ProjectDirectoryLease owner = ProjectDirectoryLease.Acquire(_root))
        {
            var error = Assert.Throws<InvalidOperationException>(() =>
                ProjectDirectoryLease.Acquire(Path.Combine(_root, ".")));
            Assert.Contains("保存并关闭", error.Message);
        }
        // The marker remains after close; ownership follows the live OS handle, not existence.
        using ProjectDirectoryLease reopened = ProjectDirectoryLease.Acquire(_root);
    }

    [Fact]
    public void DifferentProjectsMayOpenTogether()
    {
        using ProjectDirectoryLease first = ProjectDirectoryLease.Acquire(Path.Combine(_root, "one"));
        using ProjectDirectoryLease second = ProjectDirectoryLease.Acquire(Path.Combine(_root, "two"));
    }

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}
