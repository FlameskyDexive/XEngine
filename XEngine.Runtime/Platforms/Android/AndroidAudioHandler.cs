// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

namespace XEngine.Runtime.Platforms.Android;

public enum AndroidAudioFocusState
{
    None,
    Pending,
    Granted,
    LostTransient,
    LostPermanent,
}

public enum AndroidAudioRoute
{
    Unknown,
    BuiltIn,
    Wired,
    Bluetooth,
    Usb,
    Hdmi,
    Other,
}

public readonly record struct AndroidAudioStats(
    AndroidAudioFocusState Focus,
    AndroidAudioRoute Route,
    int SampleRate,
    int FramesPerBuffer,
    int FocusChangeCount,
    int RouteChangeCount,
    int PlaybackStartCount,
    int PlaybackStopCount,
    int RouteRestartCount,
    long UnderrunCount,
    bool IsAppResumed,
    bool IsNativeBackendReady,
    bool IsPlaybackRunning);

/// <summary>
/// Allocation-free lifecycle state for the Android audio host. Android framework callbacks are
/// reduced to primitive values so this policy can be tested without loading Android assemblies.
/// </summary>
public sealed class AndroidAudioHandler
{
    internal const int FocusGain = 1;
    internal const int FocusLoss = -1;
    internal const int FocusLossTransient = -2;
    internal const int FocusLossTransientCanDuck = -3;

    private readonly Func<bool, bool> _setPlaybackActive;
    private readonly Func<long> _getUnderrunCount;
    private AndroidAudioFocusState _focus;
    private AndroidAudioRoute _route;
    private int _sampleRate;
    private int _framesPerBuffer;
    private int _focusChangeCount;
    private int _routeChangeCount;
    private int _playbackStartCount;
    private int _playbackStopCount;
    private int _routeRestartCount;
    private bool _appResumed;
    private bool _nativeBackendReady;
    private bool _playbackRunning;

    public AndroidAudioHandler(
        Func<bool, bool>? setPlaybackActive = null,
        Func<long>? getUnderrunCount = null)
    {
        _setPlaybackActive = setPlaybackActive ?? (static _ => true);
        _getUnderrunCount = getUnderrunCount ?? (static () => 0);
    }

    public AndroidAudioStats Stats => new(
        _focus,
        _route,
        _sampleRate,
        _framesPerBuffer,
        _focusChangeCount,
        _routeChangeCount,
        _playbackStartCount,
        _playbackStopCount,
        _routeRestartCount,
        _getUnderrunCount(),
        _appResumed,
        _nativeBackendReady,
        _playbackRunning);

    public void SetNativeBackendReady(bool ready)
    {
        if (_nativeBackendReady == ready)
            return;
        _nativeBackendReady = ready;
        ApplyPlaybackState();
    }

    public void Resume()
    {
        _appResumed = true;
        if (_focus == AndroidAudioFocusState.None || _focus == AndroidAudioFocusState.LostPermanent)
            _focus = AndroidAudioFocusState.Pending;
        ApplyPlaybackState();
    }

    public void Pause()
    {
        _appResumed = false;
        ApplyPlaybackState();
    }

    public void FocusChanged(int focusChange)
    {
        AndroidAudioFocusState next = focusChange switch
        {
            FocusGain => AndroidAudioFocusState.Granted,
            FocusLoss => AndroidAudioFocusState.LostPermanent,
            FocusLossTransient or FocusLossTransientCanDuck => AndroidAudioFocusState.LostTransient,
            _ => _focus,
        };
        if (next == _focus)
            return;
        _focus = next;
        _focusChangeCount++;
        ApplyPlaybackState();
    }

    public void RouteChanged(int androidDeviceType, int sampleRate, int framesPerBuffer)
    {
        AndroidAudioRoute route = MapRoute(androidDeviceType);
        int normalizedSampleRate = Math.Max(0, sampleRate);
        int normalizedFrames = Math.Max(0, framesPerBuffer);
        if (_route == route && _sampleRate == normalizedSampleRate && _framesPerBuffer == normalizedFrames)
            return;

        bool restart = _playbackRunning;
        if (restart)
            SetPlayback(false);
        _route = route;
        _sampleRate = normalizedSampleRate;
        _framesPerBuffer = normalizedFrames;
        _routeChangeCount++;
        if (restart)
        {
            _routeRestartCount++;
            ApplyPlaybackState();
        }
    }

    internal static AndroidAudioRoute MapRoute(int deviceType) => deviceType switch
    {
        1 or 2 or 15 or 24 => AndroidAudioRoute.BuiltIn,
        3 or 4 => AndroidAudioRoute.Wired,
        7 or 8 or 23 or 26 or 27 or 30 => AndroidAudioRoute.Bluetooth,
        11 or 12 or 22 => AndroidAudioRoute.Usb,
        9 or 10 or 29 => AndroidAudioRoute.Hdmi,
        0 => AndroidAudioRoute.Unknown,
        _ => AndroidAudioRoute.Other,
    };

    private void ApplyPlaybackState()
    {
        bool shouldRun = _nativeBackendReady &&
            _appResumed &&
            _focus == AndroidAudioFocusState.Granted;
        if (shouldRun != _playbackRunning)
            SetPlayback(shouldRun);
    }

    private void SetPlayback(bool active)
    {
        bool applied = _setPlaybackActive(active);
        if (!applied && active)
            return;
        _playbackRunning = active;
        if (active)
            _playbackStartCount++;
        else
            _playbackStopCount++;
    }
}
