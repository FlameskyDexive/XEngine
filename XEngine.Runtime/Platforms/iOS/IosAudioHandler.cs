// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

namespace XEngine.Runtime.Platforms.iOS;

public readonly record struct IosAudioStats(
    int InterruptCount,
    int PlaybackStartCount,
    int PlaybackStopCount,
    bool IsAppResumed,
    bool IsInterrupted,
    bool IsNativeBackendReady,
    bool IsPlaybackRunning);

/// <summary>
/// Pauses and resumes managed playback for UIApplication background and
/// <c>AVAudioSession</c> interruptions (call / Siri). The generated host configures
/// <c>AVAudioSessionCategoryPlayback</c> (mute switch does not silence game audio) and
/// does not request the <c>audio</c> background mode — an interrupt pauses.
/// </summary>
public sealed class IosAudioHandler
{
    public const string PlaybackCategory = "AVAudioSessionCategoryPlayback";

    private readonly Func<bool, bool> _setPlaybackActive;
    private bool _appResumed;
    private bool _interrupted;
    private bool _nativeBackendReady = true;
    private bool _playbackRunning;
    private int _interruptCount;
    private int _playbackStartCount;
    private int _playbackStopCount;

    public IosAudioHandler(Func<bool, bool>? setPlaybackActive = null)
    {
        _setPlaybackActive = setPlaybackActive ?? (static _ => true);
    }

    public bool IsAppResumed => _appResumed;
    public bool IsPlaybackRunning => _playbackRunning;
    public IosAudioStats Stats => new(
        _interruptCount,
        _playbackStartCount,
        _playbackStopCount,
        _appResumed,
        _interrupted,
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
        _interrupted = false;
        ApplyPlaybackState();
    }

    public void Pause()
    {
        _appResumed = false;
        ApplyPlaybackState();
    }

    public void InterruptionBegan()
    {
        if (_interrupted)
            return;
        _interrupted = true;
        _interruptCount++;
        ApplyPlaybackState();
    }

    public void InterruptionEnded(bool shouldResume)
    {
        if (!_interrupted)
            return;
        _interrupted = false;
        _interruptCount++;
        if (shouldResume)
            ApplyPlaybackState();
    }

    private void ApplyPlaybackState()
    {
        bool shouldRun = _nativeBackendReady && _appResumed && !_interrupted;
        if (shouldRun == _playbackRunning)
            return;
        bool applied = _setPlaybackActive(shouldRun);
        if (!applied && shouldRun)
            return;
        _playbackRunning = shouldRun;
        if (shouldRun)
            _playbackStartCount++;
        else
            _playbackStopCount++;
    }
}
