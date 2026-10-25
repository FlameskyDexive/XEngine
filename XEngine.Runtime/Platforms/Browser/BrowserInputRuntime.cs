// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

namespace XEngine.Runtime.Platforms.Browser;

/// <summary>
/// Process-wide browser input + platform services. The generated JS host feeds events here;
/// <see cref="BrowserPlatformHost.Tick"/> ends the input frame after the game has read it.
/// </summary>
public static class BrowserInputRuntime
{
    private static readonly object Gate = new();
    private static bool s_pushed;
    private static IDisposable? s_servicesScope;
    private static string s_locale = "en";
    private static bool s_online = true;
    private static bool s_userGesture;
    private static string s_clipboard = string.Empty;
    private static string? s_pendingOpenUrl;
    private static string? s_pendingClipboardWrite;
    private static string? s_pendingOrientationLock;
    private static bool s_screenOrientationSupported = true;

    public static BrowserInputHandler Handler { get; } = new(
        clipboardGetter: static () => s_clipboard,
        clipboardSetter: static value =>
        {
            s_clipboard = value ?? string.Empty;
            s_pendingClipboardWrite = s_clipboard;
        });

    public static BrowserPlatformServices Services { get; } = new(
        localeName: static () => s_locale,
        timeZoneId: static () => TimeZoneInfo.Local.Id,
        networkReachability: static () => s_online
            ? PlatformNetworkReachability.ReachableViaLocalAreaNetwork
            : PlatformNetworkReachability.NotReachable,
        openUrl: static url =>
        {
            if (!s_userGesture)
                return false;
            s_pendingOpenUrl = url;
            return true;
        },
        getClipboard: static () => s_clipboard,
        setClipboard: static text =>
        {
            if (!s_userGesture)
                return false;
            s_clipboard = text ?? string.Empty;
            s_pendingClipboardWrite = s_clipboard;
            return true;
        },
        vibrate: static _ => false,
        trySetScreenOrientation: static (requested, policy) =>
        {
            // Browsers can only lock to the coarse axis (W3C screen.orientation.lock), and
            // usually only while fullscreen; the JS host applies the pending request each frame.
            if (!s_screenOrientationSupported)
                return false;
            s_pendingOrientationLock = MapOrientationLock(requested, policy);
            return true;
        });

    public static void Ensure()
    {
        lock (Gate)
        {
            if (!s_pushed)
            {
                Input.PushHandler(Handler);
                s_pushed = true;
            }
            s_servicesScope ??= PlatformServices.Configure(Services);
        }
    }

    public static void Configure(string locale, bool online)
    {
        if (!string.IsNullOrWhiteSpace(locale))
            s_locale = locale;
        s_online = online;
    }

    public static void NoteUserGesture() => s_userGesture = true;

    public static void SetClipboardFromHost(string text) => s_clipboard = text ?? string.Empty;

    public static string? TakePendingOpenUrl()
    {
        string? url = s_pendingOpenUrl;
        s_pendingOpenUrl = null;
        return url;
    }

    public static string? TakePendingClipboardWrite()
    {
        string? text = s_pendingClipboardWrite;
        s_pendingClipboardWrite = null;
        return text;
    }

    /// <summary>
    /// JS host drains the pending <c>screen.orientation.lock()</c> target each frame:
    /// "portrait", "landscape", "any", or null when nothing is requested. The host calls
    /// <see cref="NoteScreenOrientationUnsupported"/> when the browser exposes no lock API.
    /// </summary>
    public static string? TakePendingOrientationLock()
    {
        string? lockTarget = s_pendingOrientationLock;
        s_pendingOrientationLock = null;
        return lockTarget;
    }

    /// <summary>Stops queueing orientation requests once the host proved lock() unavailable.</summary>
    public static void NoteScreenOrientationUnsupported() => s_screenOrientationSupported = false;

    /// <summary>
    /// JS host reports <c>screen.orientation.type</c> on change; mapped to the engine's
    /// physical orientation (primary/secondary map to the natural/rotated variants).
    /// </summary>
    public static void ReportScreenOrientation(string orientationType)
    {
        Screen.ReportDeviceOrientation(orientationType switch
        {
            "portrait-primary" => ScreenOrientation.Portrait,
            "portrait-secondary" => ScreenOrientation.PortraitUpsideDown,
            "landscape-primary" => ScreenOrientation.LandscapeLeft,
            "landscape-secondary" => ScreenOrientation.LandscapeRight,
            _ => ScreenOrientation.Unknown,
        });
    }

    private static string MapOrientationLock(ScreenOrientation requested, ScreenAutoRotatePolicy policy)
    {
        if (requested != ScreenOrientation.AutoRotation)
        {
            return requested switch
            {
                ScreenOrientation.Portrait or ScreenOrientation.PortraitUpsideDown => "portrait",
                ScreenOrientation.LandscapeLeft or ScreenOrientation.LandscapeRight => "landscape",
                _ => "any",
            };
        }
        bool portraitFamily = policy.AutoRotateToPortrait || policy.AutoRotateToPortraitUpsideDown;
        bool landscapeFamily = policy.AutoRotateToLandscapeLeft || policy.AutoRotateToLandscapeRight;
        if (portraitFamily && !landscapeFamily)
            return "portrait";
        if (landscapeFamily && !portraitFamily)
            return "landscape";
        return "any";
    }

    public static void ResetForTests()
    {
        lock (Gate)
        {
            s_servicesScope?.Dispose();
            s_servicesScope = null;
            if (s_pushed)
            {
                try { Input.PopHandler(); } catch { }
                s_pushed = false;
            }
        }
        s_locale = "en";
        s_online = true;
        s_userGesture = false;
        s_clipboard = string.Empty;
        s_pendingOpenUrl = null;
        s_pendingClipboardWrite = null;
        Handler.FocusLost();
        Handler.EndFrame();
    }
}
