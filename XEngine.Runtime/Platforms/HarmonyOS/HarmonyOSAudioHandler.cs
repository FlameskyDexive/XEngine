// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

namespace XEngine.Runtime.Platforms.HarmonyOS;

public enum HarmonyOSAudioFocusState
{
    None,
    Pending,
    Granted,
    Lost,
}

public enum HarmonyOSAudioRoute
{
    Unknown,
    BuiltIn,
    Wired,
    Bluetooth,
    Usb,
    Hdmi,
    Other,
}

public readonly record struct HarmonyOSAudioStats(
    HarmonyOSAudioFocusState Focus,
    HarmonyOSAudioRoute Route,
    int SampleRate,
    int FramesPerBuffer,
    int FocusChangeCount,
    int RouteChangeCount,
    int PlaybackStartCount,
    int PlaybackStopCount,
    int RouteRestartCount,
    bool IsAppResumed,
    bool IsHostReady,
    bool IsPlaybackRunning);

/// <summary>
/// Lifecycle state for a HarmonyOS audio host. The native host owns the output callback while this
/// class ensures playback runs only when application lifecycle, focus, and host readiness agree.
/// </summary>
public sealed class HarmonyOSAudioHandler
{
    private readonly Func<bool, bool> _setPlaybackActive;
    private HarmonyOSAudioFocusState _focus;
    private HarmonyOSAudioRoute _route;
    private int _sampleRate;
    private int _framesPerBuffer;
    private int _focusChangeCount;
    private int _routeChangeCount;
    private int _playbackStartCount;
    private int _playbackStopCount;
    private int _routeRestartCount;
    private bool _appResumed;
    private bool _hostReady;
    private bool _playbackRunning;

    public HarmonyOSAudioHandler(Func<bool, bool>? setPlaybackActive = null)
    {
        _setPlaybackActive = setPlaybackActive ?? (static _ => true);
    }

    public HarmonyOSAudioStats Stats => new(
        _focus,
        _route,
        _sampleRate,
        _framesPerBuffer,
        _focusChangeCount,
        _routeChangeCount,
        _playbackStartCount,
        _playbackStopCount,
        _routeRestartCount,
        _appResumed,
        _hostReady,
        _playbackRunning);

    public void SetHostReady(bool ready)
    {
        if (_hostReady == ready)
            return;
        _hostReady = ready;
        ApplyPlaybackState();
    }

    public void Resume()
    {
        _appResumed = true;
        if (_focus is HarmonyOSAudioFocusState.None or HarmonyOSAudioFocusState.Lost)
            _focus = HarmonyOSAudioFocusState.Pending;
        ApplyPlaybackState();
    }

    public void Pause()
    {
        _appResumed = false;
        ApplyPlaybackState();
    }

    public void FocusChanged(bool hasFocus)
    {
        HarmonyOSAudioFocusState next = hasFocus ? HarmonyOSAudioFocusState.Granted : HarmonyOSAudioFocusState.Lost;
        if (next == _focus)
            return;
        _focus = next;
        _focusChangeCount++;
        ApplyPlaybackState();
    }

    public void RouteChanged(HarmonyOSAudioRoute route, int sampleRate, int framesPerBuffer)
    {
        int normalizedSampleRate = Math.Max(0, sampleRate);
        int normalizedFramesPerBuffer = Math.Max(0, framesPerBuffer);
        if (_route == route && _sampleRate == normalizedSampleRate && _framesPerBuffer == normalizedFramesPerBuffer)
            return;

        bool restart = _playbackRunning;
        if (restart)
            SetPlayback(false);
        _route = route;
        _sampleRate = normalizedSampleRate;
        _framesPerBuffer = normalizedFramesPerBuffer;
        _routeChangeCount++;
        if (restart)
        {
            _routeRestartCount++;
            ApplyPlaybackState();
        }
    }

    private void ApplyPlaybackState()
    {
        bool shouldRun = _hostReady && _appResumed && _focus == HarmonyOSAudioFocusState.Granted;
        if (shouldRun != _playbackRunning)
            SetPlayback(shouldRun);
    }

    private void SetPlayback(bool active)
    {
        if (!_setPlaybackActive(active) && active)
            return;
        _playbackRunning = active;
        if (active)
            _playbackStartCount++;
        else
            _playbackStopCount++;
    }
}
