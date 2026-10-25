// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Diagnostics;
using XEngine.Editor.Build.Ios;
using Xunit;

namespace XEngine.Editor.Test;

public sealed class IosRuntimePayloadProcessTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "XEngineIosPipeTests", Guid.NewGuid().ToString("N"));
    private readonly string? _previousNodeReuse = Environment.GetEnvironmentVariable("MSBUILDDISABLENODEREUSE");

    [UnixPublishPipeTheory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Publish_DoesNotWaitForReusableWorkerHoldingOutputPipes(bool fail)
    {
        string bin = Path.Combine(_root, "bin");
        string search = Path.Combine(_root, "search");
        string project = Path.Combine(search, "XEngine.Runtime");
        string output = Path.Combine(_root, "payload");
        string workerPid = Path.Combine(bin, "worker.pid");
        Directory.CreateDirectory(bin);
        Directory.CreateDirectory(project);
        File.WriteAllText(Path.Combine(project, "XEngine.Runtime.csproj"), "<Project />");
        string script = Path.Combine(bin, "dotnet");
        File.WriteAllText(script, """
            #!/bin/sh
            bin_dir="${0%/*}"
            out=""
            while [ "$#" -gt 0 ]; do
              if [ "$1" = "--output" ]; then shift; out="$1"; fi
              shift
            done
            /bin/mkdir -p "$out"
            /usr/bin/printf 'payload' > "$out/XEngine.Runtime.dll"
            /usr/bin/printf '{}' > "$out/XEngine.Runtime.deps.json"
            if [ "$MSBUILDDISABLENODEREUSE" != "1" ]; then
              /bin/sleep 30 &
              /usr/bin/printf '%s' "$!" > "$bin_dir/worker.pid"
            fi
            /usr/bin/printf 'PIPE_STDOUT_MARKER\n'
            /usr/bin/printf 'PIPE_STDERR_MARKER\n' >&2
            """ + (fail ? "\nexit 7\n" : "\nexit 0\n"));
        File.SetUnixFileMode(script, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        // The builder must configure its own child; do not inherit a test-runner workaround.
        Environment.SetEnvironmentVariable("MSBUILDDISABLENODEREUSE", null);
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var elapsed = Stopwatch.StartNew();
        try
        {
            if (fail)
            {
                var error = await Assert.ThrowsAsync<InvalidOperationException>(() =>
                    IosRuntimePayloadBuilder.BuildAsync(search, output, "ios-arm64", cancellation.Token, script));
                Assert.Contains("PIPE_STDOUT_MARKER", error.Message);
                Assert.Contains("PIPE_STDERR_MARKER", error.Message);
            }
            else
            {
                string payload = await IosRuntimePayloadBuilder.BuildAsync(search, output, "ios-arm64", cancellation.Token, script);
                Assert.Equal(Path.GetFullPath(output), payload);
                Assert.Equal("payload", File.ReadAllText(Path.Combine(payload, "XEngine.Runtime.dll")));
                Assert.Equal("{}", File.ReadAllText(Path.Combine(payload, "XEngine.Runtime.deps.json")));
            }
            Assert.False(File.Exists(workerPid), "The publish child allowed a reusable worker to inherit its pipes.");
        }
        finally
        {
            string? evidence = Environment.GetEnvironmentVariable("XENGINE_TEST_SCREENSHOT_DIR");
            if (!string.IsNullOrWhiteSpace(evidence))
            {
                Directory.CreateDirectory(evidence);
                File.WriteAllText(Path.Combine(evidence, "ios-publish-pipe-" + (fail ? "failure" : "success") + ".json"),
                    System.Text.Json.JsonSerializer.Serialize(new { fail, elapsedMs = elapsed.Elapsed.TotalMilliseconds,
                        workerSpawned = File.Exists(workerPid), cancellation = cancellation.IsCancellationRequested }));
            }
            if (File.Exists(workerPid) && int.TryParse(File.ReadAllText(workerPid), out int pid))
            {
                try { using var worker = Process.GetProcessById(pid); worker.Kill(); }
                catch (ArgumentException) { }
                catch (InvalidOperationException) { }
            }
        }
    }

    public void Dispose()
    {
        Environment.SetEnvironmentVariable("MSBUILDDISABLENODEREUSE", _previousNodeReuse);
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }
}

public sealed class UnixPublishPipeTheoryAttribute : TheoryAttribute
{
    public UnixPublishPipeTheoryAttribute()
    {
        if (OperatingSystem.IsWindows()) Skip = "Exercises Unix redirected-output pipe inheritance.";
    }
}
