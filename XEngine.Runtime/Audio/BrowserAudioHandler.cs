// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

namespace XEngine.Runtime.Audio;

/// <summary>Pauses Web/Inner audio when the tab or mini-game goes to background.</summary>
public sealed class BrowserAudioHandler
{
    private readonly Func<bool, bool> _setPlaybackActive;
    private bool _appResumed = true;
    private bool _backendReady;
    private bool _playbackRunning;
    private int _playbackStartCount;
    private int _playbackStopCount;

    public BrowserAudioHandler(Func<bool, bool>? setPlaybackActive = null)
    {
        _setPlaybackActive = setPlaybackActive ?? (static _ => true);
    }

    public bool IsPlaybackRunning => _playbackRunning;
    public int PlaybackStartCount => _playbackStartCount;
    public int PlaybackStopCount => _playbackStopCount;

    public void SetBackendReady(bool ready)
    {
        _backendReady = ready;
        Apply();
    }

    public void Resume()
    {
        _appResumed = true;
        Apply();
    }

    public void Suspend()
    {
        _appResumed = false;
        Apply();
    }

    private void Apply()
    {
        bool shouldRun = _backendReady && _appResumed;
        if (shouldRun == _playbackRunning)
            return;
        if (!_setPlaybackActive(shouldRun))
            return;
        _playbackRunning = shouldRun;
        if (shouldRun)
            _playbackStartCount++;
        else
            _playbackStopCount++;
    }
}
