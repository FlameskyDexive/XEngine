using System;
using System.IO;

using XEngine.Runtime.Video;

using Xunit;

namespace XEngine.Runtime.Test.Video;

/// <summary>Missing payloads on supported RIDs must fail inside the test body.</summary>
public sealed class FFmpegFactAttribute : FactAttribute
{
    public FFmpegFactAttribute()
    {
        string rid = FFmpegNativeLoader.GetRuntimeIdentifier();
        if (rid is not ("win-x64" or "linux-x64" or "osx-arm64" or "osx-x64" or "openharmony-arm64")
            && string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(FFmpegNativeLoader.PathEnvironmentVariable)))
        {
            Skip = $"No vendored FFmpeg payload for {rid}; provide {FFmpegNativeLoader.PathEnvironmentVariable} to run native video tests.";
        }
    }
}
