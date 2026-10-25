// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Threading;
using System.Threading.Tasks;

using XEngine.Runtime;
using XEngine.Runtime.Platforms.Android;

using Xunit;

namespace XEngine.Runtime.Test.Platforms;

public sealed class AndroidPlatformServicesTests
{
    [Fact]
    public async Task ConfiguredServicesForwardDataCommandsAndPermissions()
    {
        string? openedUrl = null;
        bool keepScreenOn = false;
        TimeSpan vibration = TimeSpan.Zero;
        string? requestedPermission = null;
        string? rationalePermission = null;
        bool openedApplicationSettings = false;
        var services = new AndroidPlatformServices(
            localeName: static () => "zh-CN",
            timeZoneId: static () => "Asia/Shanghai",
            networkReachability: static () => PlatformNetworkReachability.ReachableViaLocalAreaNetwork,
            openUrl: url =>
            {
                openedUrl = url;
                return true;
            },
            setKeepScreenOn: value =>
            {
                keepScreenOn = value;
                return true;
            },
            vibrate: duration =>
            {
                vibration = duration;
                return true;
            },
            getPermissionStatus: static _ => PlatformPermissionStatus.Denied,
            shouldShowPermissionRationale: permission =>
            {
                rationalePermission = permission;
                return true;
            },
            requestPermissionAsync: (permission, _) =>
            {
                requestedPermission = permission;
                return ValueTask.FromResult(PlatformPermissionStatus.Granted);
            },
            openApplicationSettings: () =>
            {
                openedApplicationSettings = true;
                return true;
            });

        using IDisposable registration = PlatformServices.Configure(services);
        Assert.Equal("zh-CN", PlatformServices.LocaleName);
        Assert.Equal("Asia/Shanghai", PlatformServices.TimeZoneId);
        Assert.Equal(PlatformNetworkReachability.ReachableViaLocalAreaNetwork,
            PlatformServices.NetworkReachability);

        PlatformServices.KeepScreenOn = true;
        Assert.True(keepScreenOn);
        Assert.True(PlatformServices.KeepScreenOn);
        Assert.True(PlatformServices.TryOpenUrl("https://example.com/docs"));
        Assert.Equal("https://example.com/docs", openedUrl);
        Assert.False(PlatformServices.TryOpenUrl("file:///private/data"));
        Assert.True(PlatformServices.TryVibrate(TimeSpan.FromMilliseconds(20)));
        Assert.Equal(TimeSpan.FromMilliseconds(20), vibration);
        Assert.False(PlatformServices.TryVibrate(TimeSpan.Zero));
        Assert.Equal(PlatformPermissionStatus.Denied,
            PlatformServices.GetPermissionStatus("android.permission.CAMERA"));
        Assert.True(PlatformServices.ShouldShowPermissionRationale("android.permission.CAMERA"));
        Assert.Equal("android.permission.CAMERA", rationalePermission);
        Assert.Equal(PlatformPermissionStatus.Granted,
            await PlatformServices.RequestPermissionAsync("android.permission.CAMERA"));
        Assert.Equal("android.permission.CAMERA", requestedPermission);
        Assert.True(PlatformServices.TryOpenApplicationSettings());
        Assert.True(openedApplicationSettings);
    }

    [Fact]
    public void ServiceRegistrationRestoresPortableFallback()
    {
        var services = new AndroidPlatformServices(
            static () => "test",
            static () => "test",
            static () => PlatformNetworkReachability.NotReachable,
            static _ => false,
            static _ => true,
            static _ => false,
            static _ => PlatformPermissionStatus.Denied,
            static _ => false,
            static (_, _) => ValueTask.FromResult(PlatformPermissionStatus.Denied),
            static () => false);

        using (PlatformServices.Configure(services))
            Assert.Equal(PlatformNetworkReachability.NotReachable, PlatformServices.NetworkReachability);

        Assert.Equal(PlatformNetworkReachability.Unknown, PlatformServices.NetworkReachability);
        Assert.Equal(PlatformPermissionStatus.Unsupported,
            PlatformServices.GetPermissionStatus("android.permission.CAMERA"));
        Assert.False(PlatformServices.ShouldShowPermissionRationale("android.permission.CAMERA"));
        Assert.False(PlatformServices.TryOpenApplicationSettings());
    }
}
