// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Threading;
using System.Threading.Tasks;

namespace XEngine.Runtime.Platforms.iOS;

/// <summary>
/// iOS service policy expressed through host callbacks. UIKit types stay in the generated player.
/// </summary>
public sealed class IosPlatformServices : IPlatformServices
{
    private readonly Func<string> _localeName;
    private readonly Func<string> _timeZoneId;
    private readonly Func<PlatformNetworkReachability> _networkReachability;
    private readonly Func<string, bool> _openUrl;
    private readonly Func<bool, bool> _setKeepScreenOn;
    private readonly Func<TimeSpan, bool> _vibrate;
    private readonly Func<string, PlatformPermissionStatus> _getPermissionStatus;
    private readonly Func<string, bool> _shouldShowPermissionRationale;
    private readonly Func<string, CancellationToken, ValueTask<PlatformPermissionStatus>> _requestPermissionAsync;
    private readonly Func<bool> _openApplicationSettings;
    private readonly Func<ScreenOrientation, ScreenAutoRotatePolicy, bool>? _trySetScreenOrientation;
    private bool _keepScreenOn;

    public IosPlatformServices(
        Func<string> localeName,
        Func<string> timeZoneId,
        Func<PlatformNetworkReachability> networkReachability,
        Func<string, bool> openUrl,
        Func<bool, bool> setKeepScreenOn,
        Func<TimeSpan, bool> vibrate,
        Func<string, PlatformPermissionStatus>? getPermissionStatus = null,
        Func<string, bool>? shouldShowPermissionRationale = null,
        Func<string, CancellationToken, ValueTask<PlatformPermissionStatus>>? requestPermissionAsync = null,
        Func<bool>? openApplicationSettings = null,
        Func<ScreenOrientation, ScreenAutoRotatePolicy, bool>? trySetScreenOrientation = null)
    {
        _localeName = localeName ?? throw new ArgumentNullException(nameof(localeName));
        _timeZoneId = timeZoneId ?? throw new ArgumentNullException(nameof(timeZoneId));
        _networkReachability = networkReachability ?? throw new ArgumentNullException(nameof(networkReachability));
        _openUrl = openUrl ?? throw new ArgumentNullException(nameof(openUrl));
        _setKeepScreenOn = setKeepScreenOn ?? throw new ArgumentNullException(nameof(setKeepScreenOn));
        _vibrate = vibrate ?? throw new ArgumentNullException(nameof(vibrate));
        _getPermissionStatus = getPermissionStatus ?? (static _ => PlatformPermissionStatus.Unsupported);
        _shouldShowPermissionRationale = shouldShowPermissionRationale ?? (static _ => false);
        _requestPermissionAsync = requestPermissionAsync ??
            (static (_, _) => ValueTask.FromResult(PlatformPermissionStatus.Unsupported));
        _openApplicationSettings = openApplicationSettings ?? (static () => false);
        _trySetScreenOrientation = trySetScreenOrientation;
    }

    public string LocaleName => _localeName();
    public string TimeZoneId => _timeZoneId();
    public PlatformNetworkReachability NetworkReachability => _networkReachability();

    public bool KeepScreenOn
    {
        get => _keepScreenOn;
        set
        {
            if (_keepScreenOn == value || !_setKeepScreenOn(value))
                return;
            _keepScreenOn = value;
        }
    }

    public bool TryOpenUrl(string url) => _openUrl(url);
    public bool TryVibrate(TimeSpan duration) => _vibrate(duration);
    public PlatformPermissionStatus GetPermissionStatus(string permission) => _getPermissionStatus(permission);
    public bool ShouldShowPermissionRationale(string permission) =>
        _shouldShowPermissionRationale(permission);

    public ValueTask<PlatformPermissionStatus> RequestPermissionAsync(
        string permission,
        CancellationToken cancellationToken = default) =>
        _requestPermissionAsync(permission, cancellationToken);

    public bool TryOpenApplicationSettings() => _openApplicationSettings();
    public bool TrySetScreenOrientation(ScreenOrientation requested, ScreenAutoRotatePolicy autoRotatePolicy) =>
        _trySetScreenOrientation?.Invoke(requested, autoRotatePolicy) ?? false;
}
