// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Runtime.Audio;
using XEngine.Runtime.Platforms.Browser;

using Xunit;

namespace XEngine.Runtime.Test.Audio;

[CollectionDefinition("AudioBackendSerial", DisableParallelization = true)]
public sealed class AudioBackendSerialCollection
{
}

[Collection("AudioBackendSerial")]
public sealed class AudioBackendTests : IDisposable
{
    public AudioBackendTests()
    {
        AudioContext.ResetBackendForTests();
        BrowserAudioRuntime.ResetForTests();
    }

    public void Dispose()
    {
        BrowserAudioRuntime.ResetForTests();
        AudioContext.ResetBackendForTests();
    }

    [Fact]
    public void WebAudioBackend_InitVolumePauseAndPump()
    {
        using WebAudioBackend backend = new();
        Assert.Equal("WebAudio", backend.Name);
        Assert.Equal(IntPtr.Zero, backend.NativeHandle);
        Assert.True(backend.Initialize(48000, 2, 2048, deviceInfo: null));
        Assert.True(backend.IsInitialized);
        Assert.Equal(48000, backend.SampleRate);
        backend.MasterVolume = 0.25f;
        Assert.Equal(0.25f, backend.MasterVolume);
        Assert.True(backend.Pump(256));
        Assert.Equal(1, backend.PumpCount);
        Assert.Equal(256u, backend.LastPumpedFrames);
        Assert.True(backend.SetPlaybackActive(false));
        Assert.False(backend.Pump(256));
        Assert.True(backend.SetPlaybackActive(true));
        backend.Shutdown();
        Assert.False(backend.IsInitialized);
        Assert.False(backend.Pump(256));
    }

    [Fact]
    public void InnerAudioBackend_PlayPauseWithoutGpu()
    {
        var commands = new List<string>();
        using InnerAudioBackend backend = new(
            play: src =>
            {
                commands.Add("play|" + src);
                return true;
            },
            pause: () =>
            {
                commands.Add("pause");
                return true;
            },
            stop: () =>
            {
                commands.Add("stop");
                return true;
            });

        Assert.Equal("InnerAudio", backend.Name);
        Assert.True(backend.Initialize(44100, 2, 2048, deviceInfo: null));
        Assert.True(backend.Play("https://example.com/a.mp3"));
        Assert.Equal("https://example.com/a.mp3", backend.LastSrc);
        Assert.Equal(1, backend.PlayCount);
        Assert.True(backend.SetPlaybackActive(false));
        Assert.Equal(1, backend.PauseCount);
        Assert.True(backend.SetPlaybackActive(true));
        Assert.Equal(new[] { "play|https://example.com/a.mp3", "pause", "play|https://example.com/a.mp3" }, commands);
        Assert.True(backend.Stop());
        Assert.Contains("stop", commands);
    }

    [Fact]
    public void BrowserAudioHandler_PausesOnSuspend()
    {
        var transitions = new List<bool>();
        var audio = new BrowserAudioHandler(active =>
        {
            transitions.Add(active);
            return true;
        });

        audio.SetBackendReady(true);
        Assert.Equal([true], transitions);
        Assert.True(audio.IsPlaybackRunning);

        audio.Suspend();
        Assert.Equal([true, false], transitions);
        Assert.False(audio.IsPlaybackRunning);

        audio.Resume();
        Assert.Equal([true, false, true], transitions);
        Assert.Equal(2, audio.PlaybackStartCount);
        Assert.Equal(1, audio.PlaybackStopCount);
    }

    [Fact]
    public void AudioContext_ConfigureSwapsBackendWithoutMiniaudio()
    {
        using WebAudioBackend backend = new();
        AudioContext.Configure(backend);
        AudioContext.Initialize(22050, 1, 512);
        Assert.True(AudioContext.IsInitialized);
        Assert.Same(backend, AudioContext.Backend);
        Assert.Equal(IntPtr.Zero, AudioContext.NativeContext);
        AudioContext.MasterVolume = 0.5f;
        Assert.Equal(0.5f, backend.MasterVolume);
        Assert.True(AudioContext.SetPlaybackActive(false));
        Assert.False(backend.IsPlaybackActive);
        AudioContext.Deinitialize();
        Assert.False(AudioContext.IsInitialized);
    }

    [Fact]
    public void AudioListener_OnEnable_SkipsNativeInitWhenAudioIsNotInitialized()
    {
        var listener = new AudioListener();

        Assert.False(AudioContext.IsInitialized);
        listener.OnEnable();

        Assert.Equal(IntPtr.Zero, listener.Handle);
    }

    [Fact]
    public void AudioListener_OnEnable_SkipsNativeInitWithoutNativeAudioContext()
    {
        using WebAudioBackend backend = new();
        AudioContext.Configure(backend);
        AudioContext.Initialize(44100, 2, 2048);

        var listener = new AudioListener();

        // WebAudio is initialized but deliberately has no MiniAudio native handle. This must
        // follow the same safe path as headless mode, where AudioContext is not initialized.
        listener.OnEnable();

        Assert.Equal(IntPtr.Zero, listener.Handle);
        listener.OnDisable();
    }

    [Fact]
    public void DesktopDefaultBackendIsMiniAudioAndWebCompilePathUsesWebAudio()
    {
        Assert.Equal("MiniAudio", AudioContext.Backend.Name);
        string root = FindRepositoryRoot();
        string audioContext = File.ReadAllText(Path.Combine(root, "XEngine.Runtime", "Audio", "AudioContext.cs"));
        string web = File.ReadAllText(Path.Combine(root, "XEngine.Runtime", "Audio", "WebAudioBackend.cs"));
        string inner = File.ReadAllText(Path.Combine(root, "XEngine.Runtime", "Audio", "InnerAudioBackend.cs"));
        Assert.Contains("return new WebAudioBackend();", audioContext, StringComparison.Ordinal);
        Assert.Contains("#if XENGINE_WEB", audioContext, StringComparison.Ordinal);
        Assert.DoesNotContain("MiniAudioExNative", web, StringComparison.Ordinal);
        Assert.DoesNotContain("MiniAudioExNative", inner, StringComparison.Ordinal);
        Assert.DoesNotContain("LibraryImport", web, StringComparison.Ordinal);
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Directory.Build.props"))
                && File.Exists(Path.Combine(directory.FullName, "XEngine.Runtime", "XEngine.Runtime.csproj")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }

        directory = new DirectoryInfo(Environment.CurrentDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "Directory.Build.props"))
                && File.Exists(Path.Combine(directory.FullName, "XEngine.Runtime", "XEngine.Runtime.csproj")))
            {
                return directory.FullName;
            }
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate the XEngine repository root.");
    }
}
