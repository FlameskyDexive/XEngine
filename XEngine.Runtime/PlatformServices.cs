// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Globalization;
using System.Threading;
using System.Threading.Tasks;

namespace XEngine.Runtime;

/// <summary>Current reachability reported by the active platform host.</summary>
public enum PlatformNetworkReachability
{
    Unknown,
    NotReachable,
    ReachableViaLocalAreaNetwork,
    ReachableViaCarrierDataNetwork,
}

/// <summary>Result of a platform permission query or request.</summary>
public enum PlatformPermissionStatus
{
    Unsupported,
    Denied,
    Granted,
    DeniedPermanently,
    NotDeclared,
}

/// <summary>
/// Platform-owned services that cannot be implemented by portable Runtime code. Implementations
/// must marshal framework-affine work to their host UI thread.
/// </summary>
public interface IPlatformServices
{
    string LocaleName { get; }
    string TimeZoneId { get; }
    PlatformNetworkReachability NetworkReachability { get; }
    bool KeepScreenOn { get; set; }
    bool TryOpenUrl(string url);
    bool TryVibrate(TimeSpan duration);
    PlatformPermissionStatus GetPermissionStatus(string permission);
    bool ShouldShowPermissionRationale(string permission);
    ValueTask<PlatformPermissionStatus> RequestPermissionAsync(
        string permission,
        CancellationToken cancellationToken = default);
    bool TryOpenApplicationSettings();

    /// <summary>
    /// Applies a screen-orientation request: a fixed lock (<see cref="ScreenOrientation.Portrait"/>
    /// … <see cref="ScreenOrientation.LandscapeRight"/>) or sensor-driven auto-rotation restricted
    /// by <paramref name="autoRotatePolicy"/>. Returns true when the host applied or queued the
    /// request; false means this host cannot control window orientation.
    /// </summary>
    bool TrySetScreenOrientation(ScreenOrientation requested, ScreenAutoRotatePolicy autoRotatePolicy);
}

/// <summary>
/// Process-wide access to platform services. A player host registers its implementation while it
/// is alive; desktop and headless hosts retain the inert portable fallback.
/// </summary>
public static class PlatformServices
{
    private static IPlatformServices s_current = new FallbackPlatformServices();

    public static string LocaleName => Volatile.Read(ref s_current).LocaleName;
    public static string TimeZoneId => Volatile.Read(ref s_current).TimeZoneId;
    public static PlatformNetworkReachability NetworkReachability =>
        Volatile.Read(ref s_current).NetworkReachability;

    public static bool KeepScreenOn
    {
        get => Volatile.Read(ref s_current).KeepScreenOn;
        set => Volatile.Read(ref s_current).KeepScreenOn = value;
    }

    /// <summary>Registers services until the returned scope is disposed.</summary>
    public static IDisposable Configure(IPlatformServices services)
    {
        ArgumentNullException.ThrowIfNull(services);
        IPlatformServices previous = Interlocked.Exchange(ref s_current, services);
        return new ConfigurationScope(services, previous);
    }

    /// <summary>Opens an http or https URL through the current platform host.</summary>
    public static bool TryOpenUrl(string url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) ||
            (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps))
        {
            return false;
        }
        return Volatile.Read(ref s_current).TryOpenUrl(uri.AbsoluteUri);
    }

    /// <summary>Requests a bounded haptic pulse when the platform exposes a vibrator.</summary>
    public static bool TryVibrate(TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero || duration > TimeSpan.FromSeconds(10))
            return false;
        return Volatile.Read(ref s_current).TryVibrate(duration);
    }

    public static PlatformPermissionStatus GetPermissionStatus(string permission)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(permission);
        return Volatile.Read(ref s_current).GetPermissionStatus(permission);
    }

    public static ValueTask<PlatformPermissionStatus> RequestPermissionAsync(
        string permission,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(permission);
        return Volatile.Read(ref s_current).RequestPermissionAsync(permission, cancellationToken);
    }

    public static bool ShouldShowPermissionRationale(string permission)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(permission);
        return Volatile.Read(ref s_current).ShouldShowPermissionRationale(permission);
    }

    /// <summary>Opens the current application's platform settings page when supported.</summary>
    public static bool TryOpenApplicationSettings() =>
        Volatile.Read(ref s_current).TryOpenApplicationSettings();

    /// <summary>Applies a screen-orientation request through the current platform host.</summary>
    public static bool TrySetScreenOrientation(ScreenOrientation requested, ScreenAutoRotatePolicy autoRotatePolicy) =>
        Volatile.Read(ref s_current).TrySetScreenOrientation(requested, autoRotatePolicy);

    private sealed class ConfigurationScope(IPlatformServices registered, IPlatformServices previous) : IDisposable
    {
        private IPlatformServices? _registered = registered;

        public void Dispose()
        {
            IPlatformServices? registered = Interlocked.Exchange(ref _registered, null);
            if (registered != null)
                _ = Interlocked.CompareExchange(ref s_current, previous, registered);
        }
    }

    private sealed class FallbackPlatformServices : IPlatformServices
    {
        public string LocaleName => CultureInfo.CurrentCulture.Name;
        public string TimeZoneId => TimeZoneInfo.Local.Id;
        public PlatformNetworkReachability NetworkReachability => PlatformNetworkReachability.Unknown;
        public bool KeepScreenOn { get; set; }
        public bool TryOpenUrl(string url) => false;
        public bool TryVibrate(TimeSpan duration) => false;
        public PlatformPermissionStatus GetPermissionStatus(string permission) => PlatformPermissionStatus.Unsupported;
        public bool ShouldShowPermissionRationale(string permission) => false;
        public ValueTask<PlatformPermissionStatus> RequestPermissionAsync(
            string permission,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(PlatformPermissionStatus.Unsupported);
        public bool TryOpenApplicationSettings() => false;
        public bool TrySetScreenOrientation(ScreenOrientation requested, ScreenAutoRotatePolicy autoRotatePolicy) => false;
    }
}
