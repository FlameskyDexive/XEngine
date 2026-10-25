// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

namespace XEngine.Runtime;

/// <summary>
/// Logical screen orientation, matching Unity's <c>ScreenOrientation</c> semantics.
/// <para>
/// On phones the natural (rotation 0) orientation is portrait: <see cref="LandscapeLeft"/> is the
/// device rotated so the right-hand side of the device points up (home button/gesture bar on the
/// right on a portrait-natural device), <see cref="LandscapeRight"/> is the opposite rotation, and
/// <see cref="PortraitUpsideDown"/> is portrait rotated 180°.
/// </para>
/// </summary>
public enum ScreenOrientation
{
    /// <summary>The orientation cannot be determined (no host report and no window yet).</summary>
    Unknown = 0,
    /// <summary>Portrait: the device's natural upright direction (rotation 0 on phones).</summary>
    Portrait = 1,
    /// <summary>Portrait rotated 180° (upside down).</summary>
    PortraitUpsideDown = 2,
    /// <summary>Landscape with the device rotated 90° counter-clockwise (home on the right).</summary>
    LandscapeLeft = 3,
    /// <summary>Landscape with the device rotated 90° clockwise (home on the left).</summary>
    LandscapeRight = 4,
    /// <summary>Not a physical orientation: the window follows the device sensor, restricted by
    /// <see cref="Screen.AutoRotateToPortrait"/> and its siblings.</summary>
    AutoRotation = 5,
}

/// <summary>
/// The set of physical orientations the window may rotate to while
/// <see cref="Screen.Orientation"/> is <see cref="ScreenOrientation.AutoRotation"/>. Mirrors
/// Unity's four <c>Screen.autorotateTo*</c> flags as one value.
/// </summary>
public readonly record struct ScreenAutoRotatePolicy(
    bool AutoRotateToPortrait,
    bool AutoRotateToPortraitUpsideDown,
    bool AutoRotateToLandscapeLeft,
    bool AutoRotateToLandscapeRight)
{
    /// <summary>Every orientation allowed (Unity's default).</summary>
    public static ScreenAutoRotatePolicy All { get; } = new(true, true, true, true);

    /// <summary>Whether <paramref name="orientation"/> is one of the allowed physical orientations.</summary>
    public bool Allows(ScreenOrientation orientation) => orientation switch
    {
        ScreenOrientation.Portrait => AutoRotateToPortrait,
        ScreenOrientation.PortraitUpsideDown => AutoRotateToPortraitUpsideDown,
        ScreenOrientation.LandscapeLeft => AutoRotateToLandscapeLeft,
        ScreenOrientation.LandscapeRight => AutoRotateToLandscapeRight,
        _ => false,
    };

    /// <summary>Number of allowed physical orientations (0–4).</summary>
    public int AllowedCount =>
        (AutoRotateToPortrait ? 1 : 0) +
        (AutoRotateToPortraitUpsideDown ? 1 : 0) +
        (AutoRotateToLandscapeLeft ? 1 : 0) +
        (AutoRotateToLandscapeRight ? 1 : 0);
}

/// <summary>
/// Screen orientation control, mirroring Unity's <c>Screen</c> orientation surface:
/// <list type="bullet">
/// <item><see cref="Orientation"/> locks the window to a fixed orientation or enables sensor-driven
/// <see cref="ScreenOrientation.AutoRotation"/>.</item>
/// <item>The four <c>AutoRotateTo*</c> flags restrict which orientations auto-rotation may settle
/// on; changing them while auto-rotating immediately re-applies the policy.</item>
/// <item><see cref="OrientationChanged"/> fires whenever the resolved physical orientation
/// changes (device rotation, or a resize on hosts that only report window size).</item>
/// </list>
/// The request reaches the OS through <see cref="PlatformServices"/>; desktop/headless hosts keep
/// the state but cannot rotate a window, so requests are recorded and forgotten there.
/// </summary>
public static class Screen
{
    private static ScreenOrientation s_requestedOrientation = ScreenOrientation.Unknown;
    private static ScreenOrientation s_reportedOrientation = ScreenOrientation.Unknown;
    private static ScreenOrientation s_aspectOrientation = ScreenOrientation.Unknown;
    private static ScreenOrientation s_lastNotifiedOrientation = ScreenOrientation.Unknown;
    private static ScreenAutoRotatePolicy s_autoRotate = ScreenAutoRotatePolicy.All;
    private static bool s_warnedUnsupported;

    /// <summary>Raised with the new resolved orientation whenever <see cref="CurrentOrientation"/> changes.</summary>
    public static event Action<ScreenOrientation>? OrientationChanged;

    static Screen()
    {
        // Hosts that only report a resized window (no sensor data) still resolve orientation from
        // the window aspect; every platform Window facade exposes this event.
        Window.FramebufferResize += _ => RefreshAspectOrientation();
    }

    /// <summary>
    /// The requested orientation. Setting a fixed value locks the window to it; setting
    /// <see cref="ScreenOrientation.AutoRotation"/> lets the sensor rotate the window within the
    /// <c>AutoRotateTo*</c> flags. Reading returns the last requested value (including
    /// <see cref="ScreenOrientation.AutoRotation"/>, like Unity); before any request it returns the
    /// current physical orientation. See <see cref="CurrentOrientation"/> for the physical value.
    /// </summary>
    public static ScreenOrientation Orientation
    {
        get => s_requestedOrientation != ScreenOrientation.Unknown
            ? s_requestedOrientation
            : CurrentOrientation;
        set
        {
            if (value == s_requestedOrientation)
                return;
            s_requestedOrientation = value;
            ApplyToPlatform();
        }
    }

    /// <summary>
    /// The resolved physical orientation right now: the last host-reported orientation when the
    /// host reports sensor rotation, otherwise derived from the window aspect (portrait vs
    /// landscape only, without left/right/upside-down distinction). <see cref="ScreenOrientation.Unknown"/>
    /// until a window exists.
    /// </summary>
    public static ScreenOrientation CurrentOrientation =>
        s_reportedOrientation != ScreenOrientation.Unknown
            ? s_reportedOrientation
            : s_aspectOrientation != ScreenOrientation.Unknown
                ? s_aspectOrientation
                : DeriveAspectOrientation();

    /// <summary>Allow auto-rotation to settle on <see cref="ScreenOrientation.Portrait"/> (default true).</summary>
    public static bool AutoRotateToPortrait
    {
        get => s_autoRotate.AutoRotateToPortrait;
        set => SetAutoRotateFlag(s_autoRotate with { AutoRotateToPortrait = value });
    }

    /// <summary>Allow auto-rotation to settle on <see cref="ScreenOrientation.PortraitUpsideDown"/> (default true).</summary>
    public static bool AutoRotateToPortraitUpsideDown
    {
        get => s_autoRotate.AutoRotateToPortraitUpsideDown;
        set => SetAutoRotateFlag(s_autoRotate with { AutoRotateToPortraitUpsideDown = value });
    }

    /// <summary>Allow auto-rotation to settle on <see cref="ScreenOrientation.LandscapeLeft"/> (default true).</summary>
    public static bool AutoRotateToLandscapeLeft
    {
        get => s_autoRotate.AutoRotateToLandscapeLeft;
        set => SetAutoRotateFlag(s_autoRotate with { AutoRotateToLandscapeLeft = value });
    }

    /// <summary>Allow auto-rotation to settle on <see cref="ScreenOrientation.LandscapeRight"/> (default true).</summary>
    public static bool AutoRotateToLandscapeRight
    {
        get => s_autoRotate.AutoRotateToLandscapeRight;
        set => SetAutoRotateFlag(s_autoRotate with { AutoRotateToLandscapeRight = value });
    }

    /// <summary>The full auto-rotation policy currently in effect.</summary>
    public static ScreenAutoRotatePolicy AutoRotatePolicy => s_autoRotate;

    /// <summary>
    /// Pushes the physical orientation observed by the host (device sensor rotation). Platform
    /// hosts — including generated player glue outside this assembly — call this from their
    /// rotation notifications; it overrides the aspect-derived guess.
    /// <see cref="ScreenOrientation.Unknown"/> reports are ignored — hosts simply stop reporting
    /// when sensor data is unavailable.
    /// </summary>
    public static void ReportDeviceOrientation(ScreenOrientation orientation)
    {
        if (orientation == ScreenOrientation.Unknown || orientation == s_reportedOrientation)
            return;
        s_reportedOrientation = orientation;
        NotifyIfResolvedChanged();
    }

    /// <summary>Test seam: resets requested/reported state between cases.</summary>
    internal static void ResetForTests()
    {
        s_requestedOrientation = ScreenOrientation.Unknown;
        s_reportedOrientation = ScreenOrientation.Unknown;
        s_aspectOrientation = ScreenOrientation.Unknown;
        s_lastNotifiedOrientation = ScreenOrientation.Unknown;
        s_autoRotate = ScreenAutoRotatePolicy.All;
        s_warnedUnsupported = false;
    }

    private static void SetAutoRotateFlag(ScreenAutoRotatePolicy policy)
    {
        if (policy == s_autoRotate)
            return;
        s_autoRotate = policy;
        // Unity re-applies the OS constraint as soon as an autorotate flag changes while the
        // window is in auto-rotation mode.
        if (s_requestedOrientation == ScreenOrientation.AutoRotation)
            ApplyToPlatform();
    }

    private static void ApplyToPlatform()
    {
        if (!PlatformServices.TrySetScreenOrientation(s_requestedOrientation, s_autoRotate) &&
            ShouldWarnUnsupported() && !s_warnedUnsupported)
        {
            s_warnedUnsupported = true;
            Debug.LogWarning(
                $"[Screen] This host cannot control window orientation; request {s_requestedOrientation} was recorded but not applied.");
        }
    }

    private static bool ShouldWarnUnsupported() => Application.RuntimePlatform switch
    {
        ApplicationRuntimePlatform.Android
            or ApplicationRuntimePlatform.iOS
            or ApplicationRuntimePlatform.HarmonyOS
            or ApplicationRuntimePlatform.Browser => true,
        _ => false,
    };

    private static void RefreshAspectOrientation()
    {
        ScreenOrientation derived = DeriveAspectOrientation();
        if (derived == s_aspectOrientation)
            return;
        s_aspectOrientation = derived;
        NotifyIfResolvedChanged();
    }

    private static void NotifyIfResolvedChanged()
    {
        ScreenOrientation current = CurrentOrientation;
        if (current == s_lastNotifiedOrientation)
            return;
        s_lastNotifiedOrientation = current;
        OrientationChanged?.Invoke(current);
    }

    private static ScreenOrientation DeriveAspectOrientation()
    {
        try
        {
            var size = Window.Size;
            if (size.X <= 0 || size.Y <= 0)
                return ScreenOrientation.Unknown;
            return size.X >= size.Y ? ScreenOrientation.LandscapeLeft : ScreenOrientation.Portrait;
        }
        catch
        {
            // Headless hosts may not have a window facade behind the size getter.
            return ScreenOrientation.Unknown;
        }
    }
}
