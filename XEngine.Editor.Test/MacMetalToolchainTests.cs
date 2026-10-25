using System;

using XEngine.Editor.Build;

using Xunit;

namespace XEngine.Editor.Test;

public class MacMetalToolchainTests
{
    [Fact]
    public void InstalledMacToolchain_CompilesAndLinksMetalLibrary()
    {
        bool available = MacMetalToolchain.TryPrepare(out string diagnostic);
        if (OperatingSystem.IsMacOS())
        {
            Assert.True(available, diagnostic);
            Assert.Contains("Metal compiler verified:", diagnostic, StringComparison.Ordinal);
            Assert.Contains("; metallib:", diagnostic, StringComparison.Ordinal);
        }
        else
            Assert.False(available);
    }
}
