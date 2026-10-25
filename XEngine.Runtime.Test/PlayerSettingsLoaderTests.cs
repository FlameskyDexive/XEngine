using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

using XEngine.Runtime.AssetStreaming;

using Xunit;

namespace XEngine.Runtime.Test;

[Collection("ScreenState")]
public sealed class PlayerSettingsLoaderTests : IDisposable
{
    public PlayerSettingsLoaderTests() => Screen.ResetForTests();

    public void Dispose() => Screen.ResetForTests();

    [Theory]
    [InlineData("TargetFrameRate: 30\n", false, 30)]
    [InlineData("TargetFrameRate: 60\n", false, 60)]
    [InlineData("TargetFrameRate: -1\n", false, 0)]
    [InlineData("KeepScreenOn: false\n", false, 0)]
    [InlineData("TargetFrameRate: 30\n", true, 144)]
    public void FrameLimitLoadsForPlayerButDoesNotChangeEditor(string yaml, bool editor, int expected)
    {
        bool previousEditor = Application.IsEditor;
        int previousLimit = Application.TargetFrameRate;
        try
        {
            Application.IsEditor = editor;
            Application.TargetFrameRate = 144;
            using var provider = new CallbackAssetStreamProvider("frame settings",
                path => path == "Settings/Player.yaml" ? new MemoryStream(Encoding.UTF8.GetBytes(yaml)) : null,
                static (_, _) => Array.Empty<string>());
            PlayerSettingsLoader.ApplyOrientation(provider);
            Assert.Equal(expected, Application.TargetFrameRate);
        }
        finally
        {
            Application.IsEditor = previousEditor;
            Application.TargetFrameRate = previousLimit;
        }
    }

    [Fact]
    public void ApplyOrientation_ReadsPlayerSettingsFromPackagedProviderBeforeSurfaceExists()
    {
        ScreenOrientation? requested = null;
        ScreenAutoRotatePolicy? policy = null;
        using IDisposable registration = PlatformServices.Configure(new RecordingServices(
            (value, flags) =>
            {
                requested = value;
                policy = flags;
                return true;
            }));

        using var provider = new CallbackAssetStreamProvider(
            "test package",
            path => path == "Settings/Player.yaml"
                ? new MemoryStream(Encoding.UTF8.GetBytes(
                    "DefaultOrientation: 3\n" +
                    "AutoRotateToPortrait: false\n" +
                    "AutoRotateToPortraitUpsideDown: true\n" +
                    "AutoRotateToLandscapeLeft: true\n" +
                    "AutoRotateToLandscapeRight: false\n"))
                : null,
            static (_, _) => Array.Empty<string>());

        PlayerSettingsLoader.ApplyOrientation(provider);

        Assert.Equal(ScreenOrientation.LandscapeLeft, requested);
        Assert.Equal(ScreenOrientation.LandscapeLeft, Screen.Orientation);
        Assert.Equal(
            new ScreenAutoRotatePolicy(false, true, true, false),
            policy);
    }

    [Theory]
    [InlineData("KeepScreenOn: true\n", true)]
    [InlineData("KeepScreenOn: false\n", false)]
    [InlineData("DefaultOrientation: 3\n", false)]
    public void PackagedPlayerSettings_ApplyKeepAwakeAndLegacyDefault(string yaml, bool expected)
    {
        var services = new RecordingServices(static (_, _) => true) { KeepScreenOn = !expected };
        using var registration = PlatformServices.Configure(services);
        using var provider = new CallbackAssetStreamProvider("test package",
            path => path == "Settings/Player.yaml" ? new MemoryStream(Encoding.UTF8.GetBytes(yaml)) : null,
            static (_, _) => Array.Empty<string>());

        PlayerSettingsLoader.ApplyOrientation(provider);

        Assert.Equal(expected, services.KeepScreenOn);
        PlatformServices.KeepScreenOn = !expected;
        Assert.Equal(!expected, services.KeepScreenOn);
    }

    private sealed class RecordingServices(
        Func<ScreenOrientation, ScreenAutoRotatePolicy, bool> apply) : IPlatformServices
    {
        public string LocaleName => "test";
        public string TimeZoneId => "UTC";
        public PlatformNetworkReachability NetworkReachability => PlatformNetworkReachability.Unknown;
        public bool KeepScreenOn { get; set; }
        public bool TryOpenUrl(string url) => false;
        public bool TrySetKeepScreenOn(bool enabled) => false;
        public bool TryVibrate(TimeSpan duration) => false;
        public PlatformPermissionStatus GetPermissionStatus(string permission) => PlatformPermissionStatus.Unsupported;
        public bool ShouldShowPermissionRationale(string permission) => false;
        public ValueTask<PlatformPermissionStatus> RequestPermissionAsync(
            string permission,
            System.Threading.CancellationToken cancellationToken) =>
            ValueTask.FromResult(PlatformPermissionStatus.Unsupported);
        public bool TryOpenApplicationSettings() => false;
        public bool TrySetScreenOrientation(ScreenOrientation requested, ScreenAutoRotatePolicy autoRotatePolicy) =>
            apply(requested, autoRotatePolicy);
    }
}
