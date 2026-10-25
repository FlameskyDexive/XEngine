// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Threading;

using XEngine.Runtime.Platforms.HarmonyOS;

using Xunit;

namespace XEngine.Runtime.Test.Platforms;

public sealed class HarmonyOSPlatformServicesTests
{
    [Fact]
    public async Task Services_DelegateToTheNativeHostWithoutFrameworkDependencies()
    {
        bool keepScreenOn = false;
        string? rationalePermission = null;
        bool openedApplicationSettings = false;
        var services = new HarmonyOSPlatformServices(
            () => "zh-CN",
            () => "Asia/Shanghai",
            () => PlatformNetworkReachability.ReachableViaLocalAreaNetwork,
            url => url == "https://xengine.dev",
            active => { keepScreenOn = active; return true; },
            duration => duration == TimeSpan.FromMilliseconds(50),
            permission => permission == "camera" ? PlatformPermissionStatus.Granted : PlatformPermissionStatus.Unsupported,
            permission =>
            {
                rationalePermission = permission;
                return permission == "camera";
            },
            (permission, _) => ValueTask.FromResult(permission == "camera" ? PlatformPermissionStatus.Granted : PlatformPermissionStatus.Denied),
            () =>
            {
                openedApplicationSettings = true;
                return true;
            });

        services.KeepScreenOn = true;

        Assert.Equal("zh-CN", services.LocaleName);
        Assert.Equal(PlatformNetworkReachability.ReachableViaLocalAreaNetwork, services.NetworkReachability);
        Assert.True(keepScreenOn);
        Assert.True(services.TryOpenUrl("https://xengine.dev"));
        Assert.True(services.TryVibrate(TimeSpan.FromMilliseconds(50)));
        Assert.Equal(PlatformPermissionStatus.Granted, services.GetPermissionStatus("camera"));
        Assert.True(services.ShouldShowPermissionRationale("camera"));
        Assert.Equal("camera", rationalePermission);
        Assert.False(services.ShouldShowPermissionRationale("location"));
        Assert.Equal(PlatformPermissionStatus.Denied, await services.RequestPermissionAsync("location", CancellationToken.None));
        Assert.True(services.TryOpenApplicationSettings());
        Assert.True(openedApplicationSettings);
    }
}
