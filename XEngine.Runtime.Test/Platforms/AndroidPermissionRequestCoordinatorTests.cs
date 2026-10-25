// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

using XEngine.Runtime;
using XEngine.Runtime.Platforms.Android;

using Xunit;

namespace XEngine.Runtime.Test.Platforms;

public sealed class AndroidPermissionRequestCoordinatorTests
{
    [Fact]
    public async Task ConcurrentRequestsAreSerializedAndCompletedInOrder()
    {
        var states = new Dictionary<string, PlatformPermissionStatus>(StringComparer.Ordinal)
        {
            ["android.permission.CAMERA"] = PlatformPermissionStatus.Denied,
            ["android.permission.RECORD_AUDIO"] = PlatformPermissionStatus.Denied,
        };
        var dispatched = new List<string>();
        using var coordinator = new AndroidPermissionRequestCoordinator(
            permission => states[permission],
            dispatched.Add);

        ValueTask<PlatformPermissionStatus> camera =
            coordinator.RequestAsync("android.permission.CAMERA");
        ValueTask<PlatformPermissionStatus> microphone =
            coordinator.RequestAsync("android.permission.RECORD_AUDIO");

        Assert.Equal(["android.permission.CAMERA"], dispatched);
        Assert.False(camera.IsCompleted);
        Assert.False(microphone.IsCompleted);

        Assert.True(coordinator.Complete(
            "android.permission.CAMERA",
            PlatformPermissionStatus.Granted));
        Assert.Equal(PlatformPermissionStatus.Granted, await camera);
        Assert.Equal(
            ["android.permission.CAMERA", "android.permission.RECORD_AUDIO"],
            dispatched);

        Assert.True(coordinator.Complete(
            "android.permission.RECORD_AUDIO",
            PlatformPermissionStatus.DeniedPermanently));
        Assert.Equal(PlatformPermissionStatus.DeniedPermanently, await microphone);
    }

    [Fact]
    public async Task QueuedRequestRechecksStatusBeforeOpeningAnotherDialog()
    {
        PlatformPermissionStatus status = PlatformPermissionStatus.Denied;
        var dispatched = new List<string>();
        using var coordinator = new AndroidPermissionRequestCoordinator(
            _ => status,
            dispatched.Add);

        ValueTask<PlatformPermissionStatus> first =
            coordinator.RequestAsync("android.permission.CAMERA");
        ValueTask<PlatformPermissionStatus> second =
            coordinator.RequestAsync("android.permission.CAMERA");
        status = PlatformPermissionStatus.Granted;

        Assert.True(coordinator.Complete(
            "android.permission.CAMERA",
            PlatformPermissionStatus.Granted));

        Assert.Equal(PlatformPermissionStatus.Granted, await first);
        Assert.Equal(PlatformPermissionStatus.Granted, await second);
        Assert.Equal(["android.permission.CAMERA"], dispatched);
    }

    [Fact]
    public async Task CanceledQueuedRequestNeverDispatches()
    {
        var dispatched = new List<string>();
        using var coordinator = new AndroidPermissionRequestCoordinator(
            static _ => PlatformPermissionStatus.Denied,
            dispatched.Add);
        using var cancellation = new CancellationTokenSource();

        ValueTask<PlatformPermissionStatus> active =
            coordinator.RequestAsync("android.permission.CAMERA");
        ValueTask<PlatformPermissionStatus> queued =
            coordinator.RequestAsync("android.permission.RECORD_AUDIO", cancellation.Token);
        cancellation.Cancel();

        Assert.True(coordinator.Complete(
            "android.permission.CAMERA",
            PlatformPermissionStatus.Denied));

        Assert.Equal(PlatformPermissionStatus.Denied, await active);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => queued.AsTask());
        Assert.Equal(["android.permission.CAMERA"], dispatched);
    }

    [Theory]
    [InlineData(PlatformPermissionStatus.Granted)]
    [InlineData(PlatformPermissionStatus.DeniedPermanently)]
    [InlineData(PlatformPermissionStatus.NotDeclared)]
    [InlineData(PlatformPermissionStatus.Unsupported)]
    public async Task NonRequestableStatusReturnsWithoutDispatch(PlatformPermissionStatus status)
    {
        var dispatched = new List<string>();
        using var coordinator = new AndroidPermissionRequestCoordinator(
            _ => status,
            dispatched.Add);

        PlatformPermissionStatus result = await coordinator.RequestAsync("android.permission.CAMERA");

        Assert.Equal(status, result);
        Assert.Empty(dispatched);
    }

    [Fact]
    public async Task MismatchedCallbackDoesNotCompleteActiveRequest()
    {
        using var coordinator = new AndroidPermissionRequestCoordinator(
            static _ => PlatformPermissionStatus.Denied,
            static _ => { });
        ValueTask<PlatformPermissionStatus> request =
            coordinator.RequestAsync("android.permission.CAMERA");

        Assert.False(coordinator.Complete(
            "android.permission.RECORD_AUDIO",
            PlatformPermissionStatus.Granted));
        Assert.False(request.IsCompleted);
        Assert.True(coordinator.Complete(
            "android.permission.CAMERA",
            PlatformPermissionStatus.Granted));
        Assert.Equal(PlatformPermissionStatus.Granted, await request);
    }

    [Fact]
    public async Task DisposeCompletesActiveAndQueuedRequestsAsUnsupported()
    {
        var coordinator = new AndroidPermissionRequestCoordinator(
            static _ => PlatformPermissionStatus.Denied,
            static _ => { });
        ValueTask<PlatformPermissionStatus> active =
            coordinator.RequestAsync("android.permission.CAMERA");
        ValueTask<PlatformPermissionStatus> queued =
            coordinator.RequestAsync("android.permission.RECORD_AUDIO");

        coordinator.Dispose();

        Assert.Equal(PlatformPermissionStatus.Unsupported, await active);
        Assert.Equal(PlatformPermissionStatus.Unsupported, await queued);
        Assert.Equal(
            PlatformPermissionStatus.Unsupported,
            await coordinator.RequestAsync("android.permission.CAMERA"));
    }
}
