// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Threading.Tasks;

using XEngine.Runtime;
using XEngine.Runtime.Platforms.iOS;

using Xunit;

namespace XEngine.Runtime.Test.Platforms;

public sealed class IosPlatformServicesTests
{
    [Fact]
    public async Task ConfiguredServicesForwardDataCommandsAndHaptics()
    {
        string? openedUrl = null;
        bool keepScreenOn = false;
        TimeSpan vibration = TimeSpan.Zero;
        bool openedApplicationSettings = false;
        var services = new IosPlatformServices(
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
            openApplicationSettings: () =>
            {
                openedApplicationSettings = true;
                return true;
            });

        using IDisposable registration = PlatformServices.Configure(services);
        Assert.Equal("zh-CN", PlatformServices.LocaleName);
        Assert.Equal("Asia/Shanghai", PlatformServices.TimeZoneId);
        Assert.Equal(
            PlatformNetworkReachability.ReachableViaLocalAreaNetwork,
            PlatformServices.NetworkReachability);

        PlatformServices.KeepScreenOn = true;
        Assert.True(keepScreenOn);
        Assert.True(PlatformServices.KeepScreenOn);
        Assert.True(PlatformServices.TryOpenUrl("https://example.com/docs"));
        Assert.Equal("https://example.com/docs", openedUrl);
        Assert.False(PlatformServices.TryOpenUrl("file:///private/data"));
        Assert.True(PlatformServices.TryVibrate(TimeSpan.FromMilliseconds(20)));
        Assert.Equal(TimeSpan.FromMilliseconds(20), vibration);
        Assert.Equal(PlatformPermissionStatus.Unsupported,
            PlatformServices.GetPermissionStatus("ios.permission.camera"));
        Assert.True(PlatformServices.TryOpenApplicationSettings());
        Assert.True(openedApplicationSettings);
        await Task.CompletedTask;
    }

    [Fact]
    public void ServiceRegistrationRestoresPortableFallback()
    {
        var services = new IosPlatformServices(
            static () => "test",
            static () => "test",
            static () => PlatformNetworkReachability.NotReachable,
            static _ => false,
            static _ => true,
            static _ => false);

        using (PlatformServices.Configure(services))
            Assert.Equal(PlatformNetworkReachability.NotReachable, PlatformServices.NetworkReachability);

        Assert.Equal(PlatformNetworkReachability.Unknown, PlatformServices.NetworkReachability);
    }
}
