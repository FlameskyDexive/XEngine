// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using XEngine.Runtime.Audio;

namespace XEngine.Runtime.Platforms.Browser;

/// <summary>
/// Process-wide web audio. Browser uses Web Audio; WeChat/Douyin use InnerAudio pending buffers
/// that the JS adapter drains (no miniaudio, no runtime-wasm fetch).
/// </summary>
public static class BrowserAudioRuntime
{
    private static readonly object Gate = new();
    private static string? s_pendingInnerCommand;
    private static float? s_pendingVolume;
    private static bool s_ensured;

    public static WebAudioBackend Web { get; } = new();

    public static InnerAudioBackend Inner { get; } = new(
        play: src =>
        {
            s_pendingInnerCommand = "play|" + src;
            return true;
        },
        pause: () =>
        {
            s_pendingInnerCommand = "pause";
            return true;
        },
        stop: () =>
        {
            s_pendingInnerCommand = "stop";
            return true;
        },
        setVolume: value => s_pendingVolume = value);

    public static BrowserAudioHandler Handler { get; } = new(AudioContext.SetPlaybackActive);

    public static void Ensure(string kind = "webaudio")
    {
        lock (Gate)
        {
            if (!s_ensured)
            {
                IAudioBackend backend = IsInner(kind) ? Inner : Web;
                AudioContext.Configure(backend);
                if (!AudioContext.IsInitialized)
                    AudioContext.Initialize(44100, 2, 2048);
                s_ensured = true;
            }

            Handler.SetBackendReady(true);
            Handler.Resume();
        }
    }

    public static void Pump(uint frameCount)
    {
        if (AudioContext.Backend is WebAudioBackend web)
            web.Pump(frameCount);
    }

    public static string? TakePendingInnerAudio()
    {
        string? command = s_pendingInnerCommand;
        s_pendingInnerCommand = null;
        return command;
    }

    public static float TakePendingMasterVolume(float fallback)
    {
        if (s_pendingVolume is not float volume)
            return fallback;
        s_pendingVolume = null;
        return volume;
    }

    public static void ResetForTests()
    {
        lock (Gate)
        {
            Handler.Suspend();
            Handler.SetBackendReady(false);
            s_pendingInnerCommand = null;
            s_pendingVolume = null;
            s_ensured = false;
            AudioContext.ResetBackendForTests();
        }
    }

    private static bool IsInner(string kind) =>
        kind.Equals("inner", StringComparison.OrdinalIgnoreCase)
        || kind.Equals("inneraudio", StringComparison.OrdinalIgnoreCase);
}
