// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

namespace XEngine.Runtime.Audio;

/// <summary>Backend feature flags (plan §3.2). Portable code must gate new audio paths on these
/// and keep a managed fallback for backends that report none.</summary>
[Flags]
public enum AudioBackendCapabilities
{
    None = 0,
    /// <summary>Per-mixer-group audio nodes exist in the device graph; sources can attach to them
    /// and group gains are applied once on the bus.</summary>
    BusRouting = 1 << 0,
    /// <summary>Per-bus effect processing is available (N4).</summary>
    BusEffects = 1 << 1,
    /// <summary>Sample-accurate start times against the device clock (N7).</summary>
    ScheduledPlayback = 1 << 2,
    /// <summary>Real Peak/RMS metering taps are available on bus nodes.</summary>
    Metering = 1 << 3,
    /// <summary>Sidechain input for bus compression (N4).</summary>
    Sidechain = 1 << 4,
}

/// <summary>
/// Platform audio device. Desktop/Android/iOS use miniaudio; the browser uses Web Audio;
/// WeChat/Douyin wrap InnerAudio. Portable code talks to <see cref="AudioContext"/>, not this type.
/// </summary>
public interface IAudioBackend : IDisposable
{
    string Name { get; }
    bool IsInitialized { get; }
    int SampleRate { get; }
    int Channels { get; }
    float MasterVolume { get; set; }
    bool IsPlaybackActive { get; }
    IntPtr NativeHandle { get; }

    /// <summary>Feature flags for the current backend. Constant after construction.</summary>
    AudioBackendCapabilities Capabilities { get; }

    bool Initialize(uint sampleRate, uint channels, uint periodSizeInFrames, DeviceInfo? deviceInfo);
    void Shutdown();
    bool SetPlaybackActive(bool active);
    void Update();
}
