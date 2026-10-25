// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Threading;
using System.Threading.Tasks;

namespace XEngine.Runtime.Platforms.Browser;

/// <summary>
/// Browser / mini-game service policy through host callbacks. <see cref="TryOpenUrl"/> and clipboard
/// writes must be invoked from a user-gesture callback or the browser will ignore them.
/// </summary>
public sealed class BrowserPlatformServices : IPlatformServices
{
    private readonly Func<string> _localeName;
    private readonly Func<string> _timeZoneId;
    private readonly Func<PlatformNetworkReachability> _networkReachability;
    private readonly Func<string, bool> _openUrl;
    private readonly Func<string, bool> _setClipboard;
    private readonly Func<string> _getClipboard;
    private readonly Func<TimeSpan, bool> _vibrate;
    private readonly Func<ScreenOrientation, ScreenAutoRotatePolicy, bool>? _trySetScreenOrientation;
    private bool _keepScreenOn;

    public BrowserPlatformServices(
        Func<string> localeName,
        Func<string> timeZoneId,
        Func<PlatformNetworkReachability> networkReachability,
        Func<string, bool> openUrl,
        Func<string> getClipboard,
        Func<string, bool> setClipboard,
        Func<TimeSpan, bool>? vibrate = null,
        Func<ScreenOrientation, ScreenAutoRotatePolicy, bool>? trySetScreenOrientation = null)
    {
        _localeName = localeName ?? throw new ArgumentNullException(nameof(localeName));
        _timeZoneId = timeZoneId ?? throw new ArgumentNullException(nameof(timeZoneId));
        _networkReachability = networkReachability ?? throw new ArgumentNullException(nameof(networkReachability));
        _openUrl = openUrl ?? throw new ArgumentNullException(nameof(openUrl));
        _getClipboard = getClipboard ?? throw new ArgumentNullException(nameof(getClipboard));
        _setClipboard = setClipboard ?? throw new ArgumentNullException(nameof(setClipboard));
        _vibrate = vibrate ?? (_ => false);
        _trySetScreenOrientation = trySetScreenOrientation;
    }

    public string LocaleName => _localeName();
    public string TimeZoneId => _timeZoneId();
    public PlatformNetworkReachability NetworkReachability => _networkReachability();

    public bool KeepScreenOn
    {
        get => _keepScreenOn;
        set => _keepScreenOn = value;
    }

    public bool TryOpenUrl(string url) => _openUrl(url);
    public bool TrySetClipboard(string text) => _setClipboard(text ?? string.Empty);
    public string GetClipboard() => _getClipboard();
    public bool TryVibrate(TimeSpan duration) => _vibrate(duration);
    public PlatformPermissionStatus GetPermissionStatus(string permission) => PlatformPermissionStatus.Unsupported;
    public bool ShouldShowPermissionRationale(string permission) => false;

    public ValueTask<PlatformPermissionStatus> RequestPermissionAsync(
        string permission,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromResult(PlatformPermissionStatus.Unsupported);

    public bool TryOpenApplicationSettings() => false;

    public bool TrySetScreenOrientation(ScreenOrientation requested, ScreenAutoRotatePolicy autoRotatePolicy) =>
        _trySetScreenOrientation?.Invoke(requested, autoRotatePolicy) ?? false;
}
