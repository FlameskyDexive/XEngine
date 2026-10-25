// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Threading;
using System.Threading.Tasks;

namespace XEngine.Runtime.Platforms.Android;

/// <summary>
/// Android service policy expressed through host callbacks. Keeping Android framework types out
/// of this class makes the contract testable from the regular Runtime target.
/// </summary>
public sealed class AndroidPlatformServices : IPlatformServices
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

    public AndroidPlatformServices(
        Func<string> localeName,
        Func<string> timeZoneId,
        Func<PlatformNetworkReachability> networkReachability,
        Func<string, bool> openUrl,
        Func<bool, bool> setKeepScreenOn,
        Func<TimeSpan, bool> vibrate,
        Func<string, PlatformPermissionStatus> getPermissionStatus,
        Func<string, bool> shouldShowPermissionRationale,
        Func<string, CancellationToken, ValueTask<PlatformPermissionStatus>> requestPermissionAsync,
        Func<bool> openApplicationSettings,
        Func<ScreenOrientation, ScreenAutoRotatePolicy, bool>? trySetScreenOrientation = null)
    {
        _localeName = localeName ?? throw new ArgumentNullException(nameof(localeName));
        _timeZoneId = timeZoneId ?? throw new ArgumentNullException(nameof(timeZoneId));
        _networkReachability = networkReachability ?? throw new ArgumentNullException(nameof(networkReachability));
        _openUrl = openUrl ?? throw new ArgumentNullException(nameof(openUrl));
        _setKeepScreenOn = setKeepScreenOn ?? throw new ArgumentNullException(nameof(setKeepScreenOn));
        _vibrate = vibrate ?? throw new ArgumentNullException(nameof(vibrate));
        _getPermissionStatus = getPermissionStatus ?? throw new ArgumentNullException(nameof(getPermissionStatus));
        _shouldShowPermissionRationale = shouldShowPermissionRationale ??
            throw new ArgumentNullException(nameof(shouldShowPermissionRationale));
        _requestPermissionAsync = requestPermissionAsync ?? throw new ArgumentNullException(nameof(requestPermissionAsync));
        _openApplicationSettings = openApplicationSettings ??
            throw new ArgumentNullException(nameof(openApplicationSettings));
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
