// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Collections.Concurrent;
using XEngine.Editor.Build;
using XEngine.Runtime;

namespace XEngine.Editor.Test;

internal static class IosPipelineTestLog
{
    internal static async Task<(BuildResult Result, string Diagnostics)> CaptureAsync(Func<Task<BuildResult>> build)
    {
        var errors = new ConcurrentQueue<string>();
        void Capture(string message, DebugStackTrace? trace, LogSeverity severity)
        {
            if (severity is LogSeverity.Error or LogSeverity.Exception)
                errors.Enqueue(message);
        }
        Debug.OnLog += Capture;
        try
        {
            BuildResult result = await build();
            return (result, string.Join(Environment.NewLine, errors));
        }
        finally
        {
            Debug.OnLog -= Capture;
        }
    }
}
