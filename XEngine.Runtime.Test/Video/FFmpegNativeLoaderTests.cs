// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.IO;
using System.Runtime.InteropServices;

using XEngine.Runtime.Video;

using Xunit;

namespace XEngine.Runtime.Test.Video;

public class FFmpegNativeLoaderTests
{
    [FFmpegFact]
    public void EnsureLoaded_LoadsLibrariesAndReportsVersion()
    {
        FFmpegNativeLoader.EnsureLoaded();

        Assert.True(FFmpegNativeLoader.IsLoaded);
        Assert.False(string.IsNullOrEmpty(FFmpegNativeLoader.VersionInfo));
        Assert.NotNull(FFmpegNativeLoader.RootPath);
        Assert.True(Directory.Exists(FFmpegNativeLoader.RootPath));
    }

    [FFmpegFact]
    public void EnsureLoaded_IsIdempotent()
    {
        FFmpegNativeLoader.EnsureLoaded();
        var first = FFmpegNativeLoader.RootPath;
        FFmpegNativeLoader.EnsureLoaded();
        Assert.Equal(first, FFmpegNativeLoader.RootPath);
    }

    [Fact]
    public void GetRuntimeIdentifier_MatchesCurrentPlatform()
    {
        string rid = FFmpegNativeLoader.GetRuntimeIdentifier();

        if (RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
        {
            Assert.StartsWith("win-", rid);
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.Linux))
        {
            Assert.StartsWith("linux-", rid);
        }
        else if (RuntimeInformation.IsOSPlatform(OSPlatform.OSX))
        {
            Assert.StartsWith("osx-", rid);
        }
    }
}
