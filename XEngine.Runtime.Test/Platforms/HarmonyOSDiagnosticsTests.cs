// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Runtime.Platforms.HarmonyOS;

using Xunit;

namespace XEngine.Runtime.Test.Platforms;

public sealed class HarmonyOSDiagnosticsTests
{
    [Fact]
    public void Snapshot_ReportsRuntimeIdentityLifecycleAndOnlyRedactedErrors()
    {
        var diagnostics = new HarmonyOSDiagnostics(new HarmonyOSDiagnosticsInfo(
            "build-20260804",
            new string('a', 64),
            13,
            "arm64-v8a",
            "Vulkan"));

        diagnostics.RecordSurfaceAttached();
        diagnostics.RecordSurfaceDetached();
        diagnostics.RecordResumed();
        diagnostics.RecordPaused();
        diagnostics.RecordError("Unable to sign package: signing-password", ["signing-password"]);

        HarmonyOSDiagnosticsSnapshot snapshot = diagnostics.Snapshot();

        Assert.Equal("build-20260804", snapshot.BuildId);
        Assert.Equal(new string('a', 64), snapshot.BundleFingerprint);
        Assert.Equal(13, snapshot.ApiLevel);
        Assert.Equal("arm64-v8a", snapshot.Abi);
        Assert.Equal("Vulkan", snapshot.Renderer);
        Assert.Equal(1, snapshot.SurfaceAttachCount);
        Assert.Equal(1, snapshot.SurfaceDetachCount);
        Assert.Equal(1, snapshot.ResumeCount);
        Assert.Equal(1, snapshot.PauseCount);
        string error = Assert.Single(snapshot.Errors);
        Assert.DoesNotContain("signing-password", error, StringComparison.Ordinal);
        Assert.Contains("[REDACTED]", error, StringComparison.Ordinal);
    }
}
