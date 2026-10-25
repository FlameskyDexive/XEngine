// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using XEngine.Runtime.Platforms.HarmonyOS;

using Xunit;

namespace XEngine.Runtime.Test;

[Collection("ScreenState")]
public sealed class ScreenTests : IDisposable
{
    public ScreenTests()
    {
        Screen.ResetForTests();
    }

    public void Dispose()
    {
        Screen.ResetForTests();
    }

    [Fact]
    public void Orientation_DefaultsToCurrentOrientation()
    {
        Assert.Equal(ScreenOrientation.Unknown, Screen.Orientation);
        Assert.Equal(ScreenOrientation.Unknown, Screen.CurrentOrientation);
    }

    [Fact]
    public void Orientation_SetterStoresTheRequestAndForwardsItToThePlatform()
    {
        ScreenOrientation? received = null;
        using var _ = PlatformServices.Configure(new RecordingPlatformServices(
            (requested, _) =>
            {
                received = requested;
                return true;
            }));

        Screen.Orientation = ScreenOrientation.LandscapeLeft;

        Assert.Equal(ScreenOrientation.LandscapeLeft, Screen.Orientation);
        Assert.Equal(ScreenOrientation.LandscapeLeft, received);
    }

    [Fact]
    public void Orientation_SetterSkipsDuplicateRequests()
    {
        int calls = 0;
        using var _ = PlatformServices.Configure(new RecordingPlatformServices((_, _) =>
        {
            calls++;
            return true;
        }));

        Screen.Orientation = ScreenOrientation.Portrait;
        Screen.Orientation = ScreenOrientation.Portrait;

        Assert.Equal(1, calls);
    }

    [Fact]
    public void Orientation_AutoRotationGetterReportsAutoRotationLikeUnity()
    {
        using var _ = PlatformServices.Configure(new RecordingPlatformServices((_, _) => true));

        Screen.Orientation = ScreenOrientation.AutoRotation;
        Screen.ReportDeviceOrientation(ScreenOrientation.LandscapeRight);

        // Unity's getter returns the requested mode; the physical value is CurrentOrientation.
        Assert.Equal(ScreenOrientation.AutoRotation, Screen.Orientation);
        Assert.Equal(ScreenOrientation.LandscapeRight, Screen.CurrentOrientation);
    }

    [Fact]
    public void Orientation_FixedRequestWinsOverTheReportedDeviceOrientation()
    {
        using var _ = PlatformServices.Configure(new RecordingPlatformServices((_, _) => true));

        Screen.ReportDeviceOrientation(ScreenOrientation.LandscapeLeft);
        Screen.Orientation = ScreenOrientation.Portrait;

        Assert.Equal(ScreenOrientation.Portrait, Screen.Orientation);
        // CurrentOrientation still reports what the device physically does.
        Assert.Equal(ScreenOrientation.LandscapeLeft, Screen.CurrentOrientation);
    }

    [Fact]
    public void AutoRotateFlags_ChangeReappliesThePolicyOnlyWhileAutoRotating()
    {
        var requests = new System.Collections.Generic.List<(ScreenOrientation Requested, ScreenAutoRotatePolicy Policy)>();
        using var _ = PlatformServices.Configure(new RecordingPlatformServices((requested, policy) =>
        {
            requests.Add((requested, policy));
            return true;
        }));

        Screen.AutoRotateToPortraitUpsideDown = false;
        Assert.Empty(requests);

        Screen.Orientation = ScreenOrientation.AutoRotation;
        Screen.AutoRotateToPortraitUpsideDown = false; // no change, no re-issue
        Screen.AutoRotateToLandscapeRight = false;

        Assert.Equal(2, requests.Count);
        Assert.Equal(ScreenOrientation.AutoRotation, requests[0].Requested);
        Assert.Equal(ScreenOrientation.AutoRotation, requests[1].Requested);
        Assert.True(requests[1].Policy.AutoRotateToLandscapeRight == false);
        Assert.False(Screen.AutoRotateToLandscapeRight);
        Assert.True(Screen.AutoRotateToPortrait);
        Assert.Equal(2, requests[1].Policy.AllowedCount);
    }

    [Fact]
    public void ReportDeviceOrientation_RaisesOrientationChangedOnlyOnActualChange()
    {
        var events = new System.Collections.Generic.List<ScreenOrientation>();
        Screen.OrientationChanged += events.Add;

        Screen.ReportDeviceOrientation(ScreenOrientation.Portrait);
        Screen.ReportDeviceOrientation(ScreenOrientation.Portrait);
        Screen.ReportDeviceOrientation(ScreenOrientation.LandscapeLeft);
        Screen.ReportDeviceOrientation(ScreenOrientation.Unknown);

        Assert.Equal(new[] { ScreenOrientation.Portrait, ScreenOrientation.LandscapeLeft }, events);
        // Unknown reports are ignored; the last physical report stays in effect.
        Assert.Equal(ScreenOrientation.LandscapeLeft, Screen.CurrentOrientation);
    }

    [Fact]
    public void AutoRotatePolicy_AllowsOnlyItsEnabledOrientations()
    {
        var policy = new ScreenAutoRotatePolicy(
            AutoRotateToPortrait: true,
            AutoRotateToPortraitUpsideDown: false,
            AutoRotateToLandscapeLeft: true,
            AutoRotateToLandscapeRight: false);

        Assert.True(policy.Allows(ScreenOrientation.Portrait));
        Assert.False(policy.Allows(ScreenOrientation.PortraitUpsideDown));
        Assert.True(policy.Allows(ScreenOrientation.LandscapeLeft));
        Assert.False(policy.Allows(ScreenOrientation.LandscapeRight));
        Assert.False(policy.Allows(ScreenOrientation.AutoRotation));
        Assert.Equal(2, policy.AllowedCount);
        Assert.Equal(4, ScreenAutoRotatePolicy.All.AllowedCount);
    }

    [Fact]
    public void PlatformServices_RejectingTheRequestKeepsTheRequestedValueLikeUnity()
    {
        using var _ = PlatformServices.Configure(new RecordingPlatformServices((_, _) => false));

        Screen.Orientation = ScreenOrientation.LandscapeRight;

        Assert.Equal(ScreenOrientation.LandscapeRight, Screen.Orientation);
    }

    [Fact]
    public void PlatformServices_UnsupportedHostsReturnFalseWithoutThrowing()
    {
        Assert.False(PlatformServices.TrySetScreenOrientation(
            ScreenOrientation.Portrait,
            ScreenAutoRotatePolicy.All));
    }

    [Fact]
    public void PlatformImplementations_DelegateOrientationRequestsToTheirCallbacks()
    {
        ScreenOrientation? received = null;
        var services = new HarmonyOSPlatformServices(
            () => "zh-CN",
            () => "Asia/Shanghai",
            () => PlatformNetworkReachability.Unknown,
            _ => false,
            _ => false,
            _ => false,
            _ => PlatformPermissionStatus.Unsupported,
            _ => false,
            (_, _) => System.Threading.Tasks.ValueTask.FromResult(PlatformPermissionStatus.Unsupported),
            () => false,
            (requested, _) =>
            {
                received = requested;
                return true;
            });

        bool applied = services.TrySetScreenOrientation(
            ScreenOrientation.AutoRotation,
            ScreenAutoRotatePolicy.All);

        Assert.True(applied);
        Assert.Equal(ScreenOrientation.AutoRotation, received);
    }

    private sealed class RecordingPlatformServices(Func<ScreenOrientation, ScreenAutoRotatePolicy, bool> trySetOrientation)
        : IPlatformServices
    {
        public string LocaleName => string.Empty;
        public string TimeZoneId => string.Empty;
        public PlatformNetworkReachability NetworkReachability => PlatformNetworkReachability.Unknown;
        public bool KeepScreenOn { get; set; }
        public bool TryOpenUrl(string url) => false;
        public bool TryVibrate(TimeSpan duration) => false;
        public PlatformPermissionStatus GetPermissionStatus(string permission) => PlatformPermissionStatus.Unsupported;
        public bool ShouldShowPermissionRationale(string permission) => false;
        public System.Threading.Tasks.ValueTask<PlatformPermissionStatus> RequestPermissionAsync(
            string permission,
            System.Threading.CancellationToken cancellationToken = default) =>
            System.Threading.Tasks.ValueTask.FromResult(PlatformPermissionStatus.Unsupported);
        public bool TryOpenApplicationSettings() => false;
        public bool TrySetScreenOrientation(ScreenOrientation requested, ScreenAutoRotatePolicy autoRotatePolicy) =>
            trySetOrientation(requested, autoRotatePolicy);
    }
}
