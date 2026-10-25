// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Runtime.InteropServices;
using System.Collections.Generic;

using XEngine.Echo;
using XEngine.Runtime.Audio;
using XEngine.Runtime.Audio.Effects;
using XEngine.Runtime.Audio.Native;
using XEngine.Runtime.Resources;
using XEngine.Vector;

namespace XEngine.Runtime;

public delegate void AudioEndEvent();
public delegate void AudioProcessEvent(NativeArray<float> framesIn, UInt32 frameCountIn, NativeArray<float> framesOut, ref UInt32 frameCountOut, UInt32 channels);
public delegate void AudioReadEvent(NativeArray<float> framesOut, UInt64 frameCount, Int32 channels);

/// <summary>
/// AudioSource component for playing audio in the scene.
/// Supports spatial audio, effects, procedural generation, and serialization.
/// </summary>
[AddComponentMenu("Audio/Audio Source")]
[ComponentIcon("\uf028")] // VolumeHigh
public sealed class AudioSource : MonoBehaviour, ISerializable
{
    /// <summary>
    /// Static flag to control whether AudioSources should resume their playback position on deserialization.
    /// If false, all AudioSources will start from the beginning when loaded.
    /// </summary>
    public static bool ResumePositionOnLoad = true;

    private class SourceInfo
    {
        public IntPtr handle;
        public bool atEnd;
    }

    // Audio clip and playback settings
    private AssetRef<AudioClip> _clip;
    private bool _playOnStart = false;
    // Set when OnEnable wanted to auto-play but the clip was still streaming in (async loading);
    // Update performs the play once the clip arrives.
    private bool _pendingAutoPlay = false;
    private bool _loop = false;
    private float _volume = 1.0f;
    private float _pitch = 1.0f;
    private float _pan = 0.0f;
    private PanMode _panMode = PanMode.Balance;

    // Spatial audio settings
    private bool _spatial = true;
    private float _dopplerFactor = 1.0f;
    private float _minDistance = 1.0f;
    private float _maxDistance = 10.0f;
    private AttenuationModel _attenuationModel = AttenuationModel.Linear;

    // Playback state (serialized for resume support)
    private ulong _savedCursor = 0;
    private bool _wasPlaying = false;

    // Mixer routing (prowl-sync M6.2, N1): the source optionally targets a group in an AudioMixer
    // asset; the group chain's linear gain is folded into the sound-group volume. The group may be
    // referenced by the persistent group id (survives renames) or, legacy, by name.
    [SerializeField] private AssetRef<AudioMixer> _mixer = new();
    [SerializeField] private string _mixerGroup = AudioMixer.MasterGroupName;
    [SerializeField] private string _mixerGroupId = "";
    private float _mixerGroupGain = 1f;
    private AudioMixerInstance? _mixerInstance;

    // Native handles
    private SourceInfo _mainSource;
    private ma_sound_group_ptr _soundGroup;
    private ma_effect_node_ptr _effectNode;
    private Float3 _previousPosition;
    private ma_effect_node_process_proc _onEffectNodeProcess;
    private ma_procedural_data_source_proc _proceduralProcessCallback;
    // Keeps the effect-node delegate alive for the whole native node lifetime: the device
    // thread invokes it every read, and without the pin a GC collect between the node
    // attach and the delegate's next managed touch fast-fails the audio thread.
    private GCHandle? _onEffectNodeProcessPin;

    // Effects and buffers
    private ConcurrentList<IAudioEffect> _effects = new();
    private AudioBuffer _outputBuffer;

    // Events
    public event AudioEndEvent End;
    public event AudioProcessEvent Process;
    public event AudioReadEvent Read;

    #region Properties

    /// <summary>
    /// The AudioClip to play.
    /// </summary>
    public AudioClip? Clip
    {
        get => _clip.Res;
        set
        {
            _clip = value;
            if (_mainSource != null && _clip.Res != null && _playOnStart)
            {
                Play();
            }
        }
    }

    /// <summary>
    /// If true, the audio will start playing automatically when OnEnable is called.
    /// </summary>
    public bool PlayOnStart
    {
        get => _playOnStart;
        set => _playOnStart = value;
    }

    /// <summary>
    /// If true, the audio will loop continuously.
    /// </summary>
    public bool Loop
    {
        get => _loop;
        set
        {
            _loop = value;
            if (_mainSource != null && _mainSource.handle != IntPtr.Zero)
                MiniAudioExNative.ma_ex_audio_source_set_loop(_mainSource.handle, value ? (uint)1 : 0);
        }
    }

    /// <summary>
    /// Volume of the audio source (0.0 to 1.0+).
    /// </summary>
    public float Volume
    {
        get => _volume;
        set
        {
            _volume = value;
            if (_soundGroup.pointer != IntPtr.Zero)
                MiniAudioNative.ma_sound_group_set_volume(_soundGroup, value);
        }
    }

    /// <summary>
    /// Pitch of the audio source. 1.0 is normal pitch.
    /// </summary>
    public float Pitch
    {
        get => _pitch;
        set
        {
            _pitch = value;
            if (_soundGroup.pointer != IntPtr.Zero)
                MiniAudioNative.ma_sound_group_set_pitch(_soundGroup, value);
        }
    }

    /// <summary>
    /// Stereo pan of the audio source (-1.0 left, 0.0 center, 1.0 right).
    /// </summary>
    public float Pan
    {
        get => _pan;
        set
        {
            _pan = value;
            if (_soundGroup.pointer != IntPtr.Zero)
                MiniAudioNative.ma_sound_group_set_pan(_soundGroup, value);
        }
    }

    /// <summary>
    /// Pan mode (Balance or Pan).
    /// </summary>
    public PanMode PanMode
    {
        get => _panMode;
        set
        {
            _panMode = value;
            if (_soundGroup.pointer != IntPtr.Zero)
                MiniAudioNative.ma_sound_group_set_pan_mode(_soundGroup, (ma_pan_mode)value);
        }
    }

    /// <summary>
    /// If true, spatial audio (3D positioning) is enabled.
    /// </summary>
    public bool Spatial
    {
        get => _spatial;
        set
        {
            _spatial = value;
            if (_soundGroup.pointer != IntPtr.Zero)
                MiniAudioNative.ma_sound_group_set_spatialization_enabled(_soundGroup, value ? (uint)1 : 0);
        }
    }

    /// <summary>
    /// Doppler effect intensity for spatial audio.
    /// </summary>
    public float DopplerFactor
    {
        get => _dopplerFactor;
        set
        {
            _dopplerFactor = value;
            if (_soundGroup.pointer != IntPtr.Zero)
                MiniAudioNative.ma_sound_group_set_doppler_factor(_soundGroup, value);
        }
    }

    /// <summary>
    /// Minimum distance for spatial audio attenuation.
    /// </summary>
    public float MinDistance
    {
        get => _minDistance;
        set
        {
            _minDistance = value;
            if (_soundGroup.pointer != IntPtr.Zero)
                MiniAudioNative.ma_sound_group_set_min_distance(_soundGroup, value);
        }
    }

    /// <summary>
    /// Maximum distance for spatial audio attenuation.
    /// </summary>
    public float MaxDistance
    {
        get => _maxDistance;
        set
        {
            _maxDistance = value;
            if (_soundGroup.pointer != IntPtr.Zero)
                MiniAudioNative.ma_sound_group_set_max_distance(_soundGroup, value);
        }
    }

    /// <summary>
    /// Attenuation model for spatial audio distance falloff.
    /// </summary>
    public AttenuationModel AttenuationModel
    {
        get => _attenuationModel;
        set
        {
            _attenuationModel = value;
            if (_soundGroup.pointer != IntPtr.Zero)
                MiniAudioNative.ma_sound_group_set_attenuation_model(_soundGroup, (ma_attenuation_model)value);
        }
    }

    /// <summary>
    /// Returns true if the audio source is currently playing.
    /// </summary>
    public bool IsPlaying
    {
        get
        {
            if (_mainSource == null || _mainSource.handle == IntPtr.Zero)
                return false;
            return MiniAudioExNative.ma_ex_audio_source_get_is_playing(_mainSource.handle) > 0;
        }
    }

    /// <summary>
    /// Gets or sets the current playback position in PCM samples.
    /// </summary>
    public ulong Cursor
    {
        get
        {
            if (_mainSource == null || _mainSource.handle == IntPtr.Zero)
                return 0;
            return MiniAudioExNative.ma_ex_audio_source_get_pcm_position(_mainSource.handle);
        }
        set
        {
            if (_mainSource != null && _mainSource.handle != IntPtr.Zero)
                MiniAudioExNative.ma_ex_audio_source_set_pcm_position(_mainSource.handle, value);
        }
    }

    /// <summary>
    /// Gets the total length of the current audio clip in PCM samples.
    /// </summary>
    public ulong Length
    {
        get
        {
            if (_mainSource == null || _mainSource.handle == IntPtr.Zero)
                return 0;
            return MiniAudioExNative.ma_ex_audio_source_get_pcm_length(_mainSource.handle);
        }
    }

    #endregion

    #region MonoBehaviour Lifecycle

    public override void OnEnable()
    {
        // Initialize native resources
        _previousPosition = Transform.Position;
        _outputBuffer = new AudioBuffer(8192);
        _proceduralProcessCallback = OnProceduralProcess;

        // Register with the mixer instance before applying settings so the chain gain comes
        // from the compiled runtime state and future edits propagate to this source.
        RegisterMixerInstance();

        // Create sound group
        _soundGroup.pointer = MiniAudioExNative.ma_ex_sound_group_init(AudioContext.NativeContext);

        if (_soundGroup.pointer != IntPtr.Zero)
        {
            // Create main audio source
            _mainSource = new SourceInfo();
            _mainSource.handle = MiniAudioExNative.ma_ex_audio_source_init(AudioContext.NativeContext);
            _mainSource.atEnd = false;
            MiniAudioExNative.ma_ex_audio_source_set_group(_mainSource.handle, _soundGroup.pointer);

            // Setup effect node
            _effectNode = new ma_effect_node_ptr(true);
            _onEffectNodeProcess = OnEffectProcess;
            _onEffectNodeProcessPin?.Free();
            _onEffectNodeProcessPin = GCHandle.Alloc(_onEffectNodeProcess);

            ma_effect_node_config effectNodeConfig = MiniAudioNative.ma_effect_node_config_init(
                (UInt32)AudioContext.Channels,
                (UInt32)AudioContext.SampleRate,
                _onEffectNodeProcess,
                IntPtr.Zero
            );

            ma_engine_ptr pEngine = new ma_engine_ptr(MiniAudioExNative.ma_ex_context_get_engine(AudioContext.NativeContext));

            if (MiniAudioNative.ma_effect_node_init(MiniAudioNative.ma_engine_get_node_graph(pEngine), ref effectNodeConfig, _effectNode) == ma_result.success)
            {
                MiniAudioNative.ma_node_attach_output_bus(new ma_node_ptr(_effectNode.pointer), 0, MiniAudioNative.ma_engine_get_endpoint(pEngine), 0);
                MiniAudioNative.ma_node_attach_output_bus(new ma_node_ptr(_soundGroup.pointer), 0, new ma_node_ptr(_effectNode.pointer), 0);
            }

            // Apply all serialized settings, then re-route the effect chain into the mixer bus
            // when a native graph is available (falls back to the endpoint attachment above).
            ApplySettings();
            RecomputeMixerVolume();

            // Handle playback. If the clip is still streaming in (async loading), defer the
            // auto-play until it arrives (see Update) instead of silently never playing.
            if (!TryAutoPlay() && (_playOnStart || (ResumePositionOnLoad && _wasPlaying && _savedCursor > 0)))
                _pendingAutoPlay = true;
        }
    }

    /// <summary>Perform the OnEnable auto-play (resume or play-on-start) if the clip is loaded.
    /// Returns false if the clip hasn't streamed in yet so the caller can defer.</summary>
    private bool TryAutoPlay()
    {
        if (_clip.Res == null) return false;

        if (ResumePositionOnLoad && _wasPlaying && _savedCursor > 0)
        {
            Play();
            Cursor = _savedCursor;
        }
        else if (_playOnStart)
        {
            Play();
        }
        return true;
    }

    public override void Update()
    {
        if (_soundGroup.pointer == IntPtr.Zero) return;

        // Deferred auto-play: the clip was still streaming in at OnEnable; play once it arrives.
        if (_pendingAutoPlay && TryAutoPlay())
            _pendingAutoPlay = false;

        // The mixer asset may have finished streaming in after OnEnable; register lazily.
        if (_mixerInstance == null && _mixer.Res != null)
        {
            RegisterMixerInstance();
            RecomputeMixerVolume();
        }

        // Update spatial audio properties based on transform
        if (_spatial)
        {
            var pos = Transform.Position;
            MiniAudioNative.ma_sound_group_set_position(_soundGroup, (float)pos.X, (float)pos.Y, (float)pos.Z);

            var forward = Transform.Forward;
            MiniAudioNative.ma_sound_group_set_direction(_soundGroup, (float)forward.X, (float)forward.Y, (float)forward.Z);

            // Calculate velocity based on position change
            float deltaTime = Time.DeltaTime;
            if (deltaTime > 0)
            {
                Float3 velocity = (pos - _previousPosition) / deltaTime;
                MiniAudioNative.ma_sound_group_set_velocity(_soundGroup, (float)velocity.X, (float)velocity.Y, (float)velocity.Z);
            }

            _previousPosition = pos;
        }

        // Check for end of playback
        if (_mainSource != null && _mainSource.handle != IntPtr.Zero)
        {
            if (MiniAudioExNative.ma_ex_audio_source_get_is_at_end(_mainSource.handle) > 0)
            {
                if (!_mainSource.atEnd)
                {
                    _mainSource.atEnd = true;
                    End?.Invoke();
                }
            }
        }
    }

    public override void OnDisable()
    {
        // Save state for serialization
        if (_mainSource != null && _mainSource.handle != IntPtr.Zero)
        {
            _wasPlaying = IsPlaying;
            _savedCursor = Cursor;

            // Stop playback
            MiniAudioExNative.ma_ex_audio_source_stop(_mainSource.handle);
            MiniAudioExNative.ma_ex_audio_source_uninit(_mainSource.handle);
            _mainSource = null;
        }

        // Stop receiving mixer change notifications.
        UnregisterMixerInstance();

        // Cleanup effect node (uninit first — the device callback must be detached before
        // the process delegate is released)
        if (_effectNode.pointer != IntPtr.Zero)
        {
            MiniAudioNative.ma_effect_node_uninit(_effectNode);
            _effectNode.Free();
        }
        if (_onEffectNodeProcessPin?.IsAllocated == true)
        {
            _onEffectNodeProcessPin.Value.Free();
            _onEffectNodeProcessPin = null;
        }

        // Cleanup sound group
        if (_soundGroup.pointer != IntPtr.Zero)
        {
            MiniAudioExNative.ma_ex_sound_group_uninit(_soundGroup.pointer);
            _soundGroup.pointer = IntPtr.Zero;
        }

        // Cleanup effects
        for (int i = 0; i < _effects.Count; i++)
            _effects[i].OnDestroy();
    }

    public override void DrawGizmos()
    {
        var icon = Resources.Texture2D.LoadDefault(Resources.DefaultTexture.IconAudioSource);
        if (icon != null) Debug.DrawIcon(icon, Transform.Position, 0.5f, Color.White);
    }

    public override void DrawGizmosSelected()
    {
        // Attenuation range, only meaningful for a spatialized source.
        if (!_spatial) return;

        var inner = new Color(0.4f, 0.8f, 1f, 1f);
        Debug.DrawWireSphere(Transform.Position, _minDistance, inner);
        Debug.DrawWireSphere(Transform.Position, _maxDistance, new Color(0.4f, 0.8f, 1f, 0.4f));
    }

    #endregion

    #region Playback Control

    /// <summary>
    /// Plays the assigned AudioClip.
    /// </summary>
    public void Play()
    {
        if (_soundGroup.pointer == IntPtr.Zero || _clip.Res == null) return;
        if (_mainSource == null || _mainSource.handle == IntPtr.Zero) return;

        _mainSource.atEnd = false;
        MiniAudioExNative.ma_ex_audio_source_set_loop(_mainSource.handle, _loop ? (uint)1 : 0);

        if (_clip.Res.Handle != IntPtr.Zero)
            MiniAudioExNative.ma_ex_audio_source_play_from_memory(_mainSource.handle, _clip.Res.Handle, _clip.Res.DataSize);
        else
            MiniAudioExNative.ma_ex_audio_source_play_from_file(_mainSource.handle, _clip.Res.FilePath, _clip.Res.StreamFromDisk ? (uint)1 : 0);
    }

    /// <summary>
    /// Plays procedurally generated audio using the Read event callback.
    /// </summary>
    public void PlayProcedural()
    {
        if (_soundGroup.pointer == IntPtr.Zero) return;
        if (_mainSource == null || _mainSource.handle == IntPtr.Zero) return;

        _mainSource.atEnd = false;
        MiniAudioExNative.ma_ex_audio_source_play_from_callback(_mainSource.handle, _proceduralProcessCallback, IntPtr.Zero);
    }

    /// <summary>
    /// Stops playback. The cursor position is maintained (can be used as pause).
    /// </summary>
    public void Stop()
    {
        if (_mainSource != null && _mainSource.handle != IntPtr.Zero)
        {
            MiniAudioExNative.ma_ex_audio_source_stop(_mainSource.handle);
            _mainSource.atEnd = false;
        }
    }

    /// <summary>
    /// Plays the given clip as a one-shot (fire and forget).
    /// Note: This uses the main source, so it will interrupt currently playing audio.
    /// </summary>
    public void PlayOneShot(AudioClip clip)
    {
        if (_soundGroup.pointer == IntPtr.Zero || clip == null) return;
        if (_mainSource == null || _mainSource.handle == IntPtr.Zero) return;

        // Stop current playback and reset
        if (IsPlaying)
        {
            MiniAudioExNative.ma_ex_audio_source_stop(_mainSource.handle);
            MiniAudioExNative.ma_ex_audio_source_set_pcm_position(_mainSource.handle, 0);
        }

        _mainSource.atEnd = false;

        if (clip.Handle != IntPtr.Zero)
            MiniAudioExNative.ma_ex_audio_source_play_from_memory(_mainSource.handle, clip.Handle, clip.DataSize);
        else
            MiniAudioExNative.ma_ex_audio_source_play_from_file(_mainSource.handle, clip.FilePath, clip.StreamFromDisk ? (uint)1 : 0);
    }

    #endregion

    #region Effects Management

    /// <summary>
    /// Adds an audio effect to the processing chain.
    /// </summary>
    public void AddEffect(IAudioEffect effect)
    {
        if (effect == null) return;
        _effects.Add(effect);
    }

    /// <summary>
    /// Removes an audio effect from the processing chain.
    /// </summary>
    public void RemoveEffect(IAudioEffect effect)
    {
        if (effect == null) return;
        _effects.Remove(effect);
    }

    /// <summary>
    /// Removes an audio effect by index.
    /// </summary>
    public void RemoveEffect(int index)
    {
        if (index >= 0 && index < _effects.Count)
        {
            var target = _effects[index];
            _effects.Remove(target);
        }
    }

    /// <summary>
    /// Removes all audio effects.
    /// </summary>
    public void ClearEffects()
    {
        List<IAudioEffect> targets = new List<IAudioEffect>();
        for (int i = 0; i < _effects.Count; i++)
        {
            targets.Add(_effects[i]);
        }
        if (targets.Count > 0)
        {
            _effects.Remove(targets);
        }
    }

    /// <summary>
    /// Gets the number of active effects.
    /// </summary>
    public int EffectCount => _effects.Count;

    #endregion

    #region Mixer Routing

    /// <summary>The mixer asset this source routes through, or null to bypass mixing.</summary>
    public AssetRef<AudioMixer> Mixer
    {
        get => _mixer;
        set
        {
            UnregisterMixerInstance();
            _mixer = value;
            RegisterMixerInstance();
            RecomputeMixerVolume();
        }
    }

    /// <summary>Name of the group inside the mixer this source feeds. "Master" targets the mix output.</summary>
    public string MixerGroup
    {
        get => _mixerGroup;
        set
        {
            _mixerGroup = value ?? AudioMixer.MasterGroupName;
            RecomputeMixerVolume();
        }
    }

    /// <summary>Persistent id of the target group (survives renames). When empty, the name in
    /// <see cref="MixerGroup"/> is used.</summary>
    public string MixerGroupId
    {
        get => _mixerGroupId;
        set
        {
            _mixerGroupId = value ?? "";
            RecomputeMixerVolume();
        }
    }

    /// <summary>
    /// Recomputes the mixer group's chain gain (volume dB product, honouring mute and solo
    /// silencing) and re-applies settings. Called when the mixer, group or asset contents change —
    /// including automatically, via the mixer instance's change notification, while playing.
    /// </summary>
    public void RecomputeMixerVolume()
    {
        if (_mixerInstance != null)
        {
            if (_mixerInstance.EnsureNativeGraph())
            {
                // Native bus path: the group node applies the chain gain exactly once, so the
                // source stops folding it in — and this source's chain-end node (re-)attaches
                // to its group's bus (covers graph rebuilds and late group resolution).
                _mixerGroupGain = 1f;
                if (_effectNode.pointer != IntPtr.Zero)
                    _mixerInstance.AttachSourceEffect(_effectNode.pointer, ResolveGroupReference());
            }
            else
            {
                // Managed fallback (Web Audio, headless): chain gain folds into the source volume.
                _mixerGroupGain = _mixerInstance.GetChainGain(ResolveGroupReference());
            }
        }
        else
        {
            var mixer = _mixer.Res;
            if (mixer == null || string.IsNullOrEmpty(_mixerGroup))
                _mixerGroupGain = 1f;
            else
                _mixerGroupGain = mixer.GetGroupLinearVolume(_mixerGroup);
        }

        ApplySettings();
    }

    private string ResolveGroupReference()
        => string.IsNullOrEmpty(_mixerGroupId) ? _mixerGroup : _mixerGroupId;

    private void RegisterMixerInstance()
    {
        var mixer = _mixer.Res;
        if (mixer == null) return;
        _mixerInstance = AudioMixerInstance.Get(mixer);
        _mixerInstance.Changed += OnMixerInstanceChanged;
    }

    private void UnregisterMixerInstance()
    {
        if (_mixerInstance == null) return;
        _mixerInstance.Changed -= OnMixerInstanceChanged;
        _mixerInstance = null;
    }

    private void OnMixerInstanceChanged() => RecomputeMixerVolume();

    #endregion

    #region Utility Methods

    /// <summary>
    /// Gets the calculated velocity based on position changes.
    /// </summary>
    public Float3 GetCalculatedVelocity()
    {
        if (_soundGroup.pointer == IntPtr.Zero)
            return new Float3(0, 0, 0);

        float deltaTime = AudioContext.DeltaTime;
        var result = MiniAudioNative.ma_sound_group_get_position(_soundGroup);
        Float3 currentPosition = new Float3(result.x, result.y, result.z);

        float dx = currentPosition.X - _previousPosition.X;
        float dy = currentPosition.Y - _previousPosition.Y;
        float dz = currentPosition.Z - _previousPosition.Z;

        return new Float3(dx / deltaTime, dy / deltaTime, dz / deltaTime);
    }

    /// <summary>
    /// Gets the output buffer after effect processing (useful for FFT analysis).
    /// </summary>
    public bool GetOutputBuffer(ref float[] buffer, out int length)
    {
        if (_outputBuffer != null)
        {
            length = _outputBuffer.Read(ref buffer);
            return length > 0;
        }

        length = 0;
        return false;
    }

    #endregion

    #region Private Methods

    private void ApplySettings()
    {
        if (_soundGroup.pointer == IntPtr.Zero) return;

        MiniAudioNative.ma_sound_group_set_volume(_soundGroup, _volume * _mixerGroupGain);
        MiniAudioNative.ma_sound_group_set_pitch(_soundGroup, _pitch);
        MiniAudioNative.ma_sound_group_set_pan(_soundGroup, _pan);
        MiniAudioNative.ma_sound_group_set_pan_mode(_soundGroup, (ma_pan_mode)_panMode);
        MiniAudioNative.ma_sound_group_set_spatialization_enabled(_soundGroup, _spatial ? (uint)1 : 0);
        MiniAudioNative.ma_sound_group_set_doppler_factor(_soundGroup, _dopplerFactor);
        MiniAudioNative.ma_sound_group_set_min_distance(_soundGroup, _minDistance);
        MiniAudioNative.ma_sound_group_set_max_distance(_soundGroup, _maxDistance);
        MiniAudioNative.ma_sound_group_set_attenuation_model(_soundGroup, (ma_attenuation_model)_attenuationModel);

        if (_mainSource != null && _mainSource.handle != IntPtr.Zero)
            MiniAudioExNative.ma_ex_audio_source_set_loop(_mainSource.handle, _loop ? (uint)1 : 0);
    }

    private unsafe void OnEffectProcess(ma_node_ptr pNode, IntPtr ppFramesIn, IntPtr pFrameCountIn, IntPtr ppFramesOut, IntPtr pFrameCountOut)
    {
        if (pNode.pointer == IntPtr.Zero)
            return;

        ma_effect_node* pEffectNode = (ma_effect_node*)pNode.pointer;

        UInt32* frameCountIn = (UInt32*)pFrameCountIn;
        UInt32* frameCountOut = (UInt32*)pFrameCountOut;
        UInt32 channels = pEffectNode->config.channels;

        float** framesIn = (float**)ppFramesIn;
        float** framesOut = (float**)ppFramesOut;

        NativeArray<float> bufferIn = new NativeArray<float>(framesIn[0], (int)(*frameCountIn * channels));
        NativeArray<float> bufferOut = new NativeArray<float>(framesOut[0], (int)(*frameCountOut * channels));

        // Just in case we end up with no sound at all because no effects were active (prevents silence)
        bufferIn.CopyTo(bufferOut);

        // An effect can modify the number of frames it processes so we need to keep track of this
        UInt32 countIn = *frameCountIn;
        UInt32 countOut = *frameCountOut;

        for (int i = 0; i < _effects.Count; i++)
        {
            _effects[i].OnProcess(bufferIn, countIn, bufferOut, ref countOut, channels);

            //Since effects processing is like a stack, the output needs to be copied to the input for the next effect
            bufferOut.CopyTo(bufferIn);

            countIn = countOut;
        }

        Process?.Invoke(bufferIn, countIn, bufferOut, ref countOut, pEffectNode->config.channels);

        *frameCountOut = countOut;

        _outputBuffer.Write(new NativeArray<float>(framesOut[0], (int)(*frameCountOut * channels)));
    }

    private void OnProceduralProcess(IntPtr pUserData, IntPtr pFramesOut, UInt64 frameCount, UInt32 channels)
    {
        int length = (int)(frameCount * channels);
        NativeArray<float> framesOut = new NativeArray<float>(pFramesOut, length);
        Read?.Invoke(framesOut, frameCount, (int)channels);
    }

    #endregion

    #region ISerializable Implementation

    public void Serialize(ref EchoObject compound, SerializationContext ctx)
    {
        compound.Add("Clip", Serializer.Serialize(typeof(AssetRef<AudioClip>), _clip, ctx));

        // Serialize playback settings
        compound.Add("PlayOnStart", new EchoObject(_playOnStart));
        compound.Add("Loop", new EchoObject(_loop));
        compound.Add("Volume", new EchoObject(_volume));
        compound.Add("Pitch", new EchoObject(_pitch));
        compound.Add("Pan", new EchoObject(_pan));
        compound.Add("PanMode", new EchoObject((int)_panMode));

        // Serialize spatial audio settings
        compound.Add("Spatial", new EchoObject(_spatial));
        compound.Add("DopplerFactor", new EchoObject(_dopplerFactor));
        compound.Add("MinDistance", new EchoObject(_minDistance));
        compound.Add("MaxDistance", new EchoObject(_maxDistance));
        compound.Add("AttenuationModel", new EchoObject((int)_attenuationModel));

        // Serialize playback state (for resume support)
        if (ResumePositionOnLoad && _mainSource != null && _mainSource.handle != IntPtr.Zero)
        {
            compound.Add("SavedCursor", new EchoObject((long)Cursor));
            compound.Add("WasPlaying", new EchoObject(IsPlaying));
        }
        else
        {
            compound.Add("SavedCursor", new EchoObject(0L));
            compound.Add("WasPlaying", new EchoObject(false));
        }

        // Persist the effect chain (prowl-sync M6.1 — effects were runtime-only before).
        var effectsData = AudioEffectSerialization.SerializeEffects(_effects);
        if (effectsData != null)
            compound.Add("Effects", effectsData);

        // Mixer routing (prowl-sync M6.2, N1 id reference).
        compound.Add("Mixer", Serializer.Serialize(typeof(AssetRef<AudioMixer>), _mixer, ctx));
        compound.Add("MixerGroup", new EchoObject(_mixerGroup));
        compound.Add("MixerGroupId", new EchoObject(_mixerGroupId));
    }

    public void Deserialize(EchoObject value, SerializationContext ctx)
    {
        // Deserialize audio clip reference
        if (value.TryGet("Clip", out var clipTag))
            _clip = Serializer.Deserialize<AssetRef<AudioClip>>(clipTag, ctx);

        // Deserialize playback settings
        _playOnStart = value["PlayOnStart"].BoolValue;
        _loop = value["Loop"].BoolValue;
        _volume = value["Volume"].FloatValue;
        _pitch = value["Pitch"].FloatValue;
        _pan = value["Pan"].FloatValue;
        _panMode = (PanMode)value["PanMode"].IntValue;

        // Deserialize spatial audio settings
        _spatial = value["Spatial"].BoolValue;
        _dopplerFactor = value["DopplerFactor"].FloatValue;
        _minDistance = value["MinDistance"].FloatValue;
        _maxDistance = value["MaxDistance"].FloatValue;
        _attenuationModel = (AttenuationModel)value["AttenuationModel"].IntValue;

        // Deserialize playback state
        _savedCursor = (ulong)value["SavedCursor"].LongValue;
        _wasPlaying = value["WasPlaying"].BoolValue;

        // Restore the effect chain (missing key = legacy asset that never had effects persisted;
        // unknown effect types are skipped by the serializer rather than failing the load).
        AudioEffectSerialization.DeserializeInto(value.TryGet("Effects", out var effectsTag) ? effectsTag : null, _effects);

        // Mixer routing (prowl-sync M6.2; N1 adds the persistent group id reference)
        if (value.TryGet("Mixer", out var mixerTag))
            _mixer = Serializer.Deserialize<AssetRef<AudioMixer>>(mixerTag, ctx);
        _mixerGroup = value.TryGet("MixerGroup", out var groupTag) ? groupTag.StringValue : AudioMixer.MasterGroupName;
        _mixerGroupId = value.TryGet("MixerGroupId", out var groupIdTag) ? groupIdTag.StringValue : "";
        RegisterMixerInstance();
        RecomputeMixerVolume();
    }

    #endregion
}
