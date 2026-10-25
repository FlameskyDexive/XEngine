// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.IO;

using XEngine.Echo;
using XEngine.Runtime.Resources;

namespace XEngine.Runtime.Video;

/// <summary>
/// Imported video media asset. Produced by the editor's <c>VideoImporter</c> from
/// <c>.mp4/.mov/.m4v/.webm/.mkv</c> source files.
///
/// The VideoClip itself stays small: it serializes probe metadata, a lazy-decoded PNG
/// cover, and a source reference addressed by the source asset's GUID + content hash —
/// never the media bytes and never a developer-machine absolute path. The source file is
/// a streaming payload the build copies to <c>Content/payload/{guid}{ext}</c>; playback
/// resolves it through <see cref="TryGetPayloadPath"/>.
/// </summary>
public sealed class VideoClip : EngineObject, ISerializable
{
    private Guid _sourceGuid = Guid.Empty;
    private string _sourceExtension = string.Empty;
    private string _contentHash = string.Empty;

    private string _container = string.Empty;
    private double _durationSeconds;
    private double _startTimeSeconds;
    private long _bitRate;

    private int _codedWidth;
    private int _codedHeight;
    private int _displayWidth;
    private int _displayHeight;
    private double _fps;
    private string _videoCodec = string.Empty;
    private string? _videoProfile;
    private int _rotationDegrees;
    private int _sampleAspectNum = 1;
    private int _sampleAspectDen = 1;
    private bool _canSeek;

    private bool _hasAudio;
    private string _audioCodec = string.Empty;
    private int _audioSampleRate;
    private int _audioChannels;

    private byte[] _coverPng = [];
    private Texture2D? _cover;

    /// <summary>GUID of the source media asset (payload address, stable across machines).</summary>
    public Guid SourceGuid => _sourceGuid;

    /// <summary>Source file extension including the dot (e.g. ".mp4").</summary>
    public string SourceExtension => _sourceExtension;

    /// <summary>SHA-256 of the source payload, hex-encoded. Build/dedup identity of the media.</summary>
    public string ContentHash => _contentHash;

    /// <summary>Demuxer container name (e.g. "mov,mp4,m4a,3gp,3g2,mj2").</summary>
    public string Container => _container;

    public double DurationSeconds => _durationSeconds;

    public double StartTimeSeconds => _startTimeSeconds;

    public long BitRate => _bitRate;

    /// <summary>Coded (storage) frame size — rotation NOT applied.</summary>
    public (int Width, int Height) CodedSize => (_codedWidth, _codedHeight);

    /// <summary>Display size — sample aspect ratio and rotation applied. Output textures size to this.</summary>
    public (int Width, int Height) DisplaySize => (_displayWidth, _displayHeight);

    /// <summary>Average frame rate in fps; 0 when unknown.</summary>
    public double Fps => _fps;

    public string VideoCodec => _videoCodec;

    public string? VideoProfile => _videoProfile;

    /// <summary>Rotation metadata normalized to 0 / 90 / 180 / 270.</summary>
    public int RotationDegrees => _rotationDegrees;

    public bool CanSeek => _canSeek;

    public bool HasAudio => _hasAudio;

    public string AudioCodec => _audioCodec;

    public int AudioSampleRate => _audioSampleRate;

    public int AudioChannels => _audioChannels;

    /// <summary>
    /// Cover frame decoded lazily from the imported PNG (kept out of GPU memory until a
    /// consumer actually shows it). Null when the source could not produce a first frame.
    /// </summary>
    public Texture2D? Cover
    {
        get
        {
            if (_cover is not null || _coverPng.Length == 0)
            {
                return _cover;
            }

            using var stream = new MemoryStream(_coverPng);
            _cover = Texture2D.FromStream(stream, generateMipmaps: false, srgbSampling: true);
            _cover.Name = Name + "_Cover";
            return _cover;
        }
    }

    /// <summary>True when this clip and <paramref name="other"/> carry the same media payload.</summary>
    public bool SameMedia(VideoClip other) =>
        !string.IsNullOrEmpty(_contentHash)
        && string.Equals(_contentHash, other._contentHash, StringComparison.Ordinal);

    /// <summary>
    /// Resolves the source media file for decoding. Players look in
    /// <c>Content/payload/{guid}{ext}</c> next to the executable; the editor falls back to
    /// the backend's current source file, then the recorded source path. Returns false
    /// when the media is unavailable.
    /// </summary>
    public bool TryGetPayloadPath(out string path)
    {
        string fileName = _sourceGuid.ToString("N") + _sourceExtension;

        string playerPath = Path.Combine(
            Application.DataPath, "Content", "payload", fileName);
        if (File.Exists(playerPath))
        {
            path = playerPath;
            return true;
        }

        // Editor caches normalize AssetPath to a logical path. Ask the owning backend
        // so cache reloads, asset moves and mounted packages resolve the current file.
        if (AssetDatabase.Current is { } backend && backend.TryGetSourcePath(_sourceGuid, out path))
        {
            return true;
        }

        // Compatibility for directly imported clips without an installed backend.
        if (!string.IsNullOrEmpty(AssetPath) && File.Exists(AssetPath))
        {
            path = AssetPath;
            return true;
        }

        path = string.Empty;
        return false;
    }

    /// <summary>Payload file name as the build writes it into Content/payload/.</summary>
    public string PayloadFileName => _sourceGuid.ToString("N") + _sourceExtension;

    // ---------------------------------------------------------------- import-time writes

    /// <summary>Fills the probe metadata. Called by the editor VideoImporter only.</summary>
    internal void ImportProbe(Guid sourceGuid, string sourceExtension, string contentHash, VideoMediaInfo info)
    {
        _sourceGuid = sourceGuid;
        _sourceExtension = sourceExtension;
        _contentHash = contentHash;

        _container = info.Container;
        _durationSeconds = info.DurationSeconds;
        _startTimeSeconds = info.StartTimeSeconds;
        _bitRate = info.BitRate;

        _codedWidth = info.Video.CodedWidth;
        _codedHeight = info.Video.CodedHeight;
        _displayWidth = info.Video.DisplaySize.Width;
        _displayHeight = info.Video.DisplaySize.Height;
        _fps = info.Video.Fps;
        _videoCodec = info.Video.Codec;
        _videoProfile = info.Video.Profile;
        _rotationDegrees = info.Video.RotationDegrees;
        _sampleAspectNum = info.Video.SampleAspectNum;
        _sampleAspectDen = info.Video.SampleAspectDen;
        _canSeek = info.CanSeek;

        _hasAudio = info.Audio is not null;
        _audioCodec = info.Audio?.Codec ?? string.Empty;
        _audioSampleRate = info.Audio?.SampleRate ?? 0;
        _audioChannels = info.Audio?.Channels ?? 0;
    }

    /// <summary>Sets the encoded cover PNG. Called by the editor VideoImporter only.</summary>
    internal void ImportCoverPng(byte[] png)
    {
        _coverPng = png;
        _cover = null;
    }

    // ---------------------------------------------------------------- serialization

    public void Serialize(ref EchoObject compound, SerializationContext ctx)
    {
        compound.Add("Name", new EchoObject(Name ?? string.Empty));
        compound.Add("AssetPath", new EchoObject(AssetPath ?? string.Empty));
        compound.Add("SourceGuid", new EchoObject(_sourceGuid.ToString("D")));
        compound.Add("SourceExtension", new EchoObject(_sourceExtension));
        compound.Add("ContentHash", new EchoObject(_contentHash));

        compound.Add("Container", new EchoObject(_container));
        compound.Add("DurationSeconds", new EchoObject(_durationSeconds));
        compound.Add("StartTimeSeconds", new EchoObject(_startTimeSeconds));
        compound.Add("BitRate", new EchoObject(_bitRate));

        compound.Add("CodedWidth", new EchoObject(_codedWidth));
        compound.Add("CodedHeight", new EchoObject(_codedHeight));
        compound.Add("DisplayWidth", new EchoObject(_displayWidth));
        compound.Add("DisplayHeight", new EchoObject(_displayHeight));
        compound.Add("Fps", new EchoObject(_fps));
        compound.Add("VideoCodec", new EchoObject(_videoCodec));
        compound.Add("VideoProfile", new EchoObject(_videoProfile ?? string.Empty));
        compound.Add("RotationDegrees", new EchoObject(_rotationDegrees));
        compound.Add("SampleAspectNum", new EchoObject(_sampleAspectNum));
        compound.Add("SampleAspectDen", new EchoObject(_sampleAspectDen));
        compound.Add("CanSeek", new EchoObject(_canSeek));

        compound.Add("HasAudio", new EchoObject(_hasAudio));
        compound.Add("AudioCodec", new EchoObject(_audioCodec));
        compound.Add("AudioSampleRate", new EchoObject(_audioSampleRate));
        compound.Add("AudioChannels", new EchoObject(_audioChannels));

        compound.Add("CoverPng", new EchoObject(_coverPng));
    }

    public void Deserialize(EchoObject value, SerializationContext ctx)
    {
        Name = value["Name"]?.StringValue ?? string.Empty;
        AssetPath = value["AssetPath"]?.StringValue ?? string.Empty;
        _ = Guid.TryParse(value["SourceGuid"]?.StringValue, out _sourceGuid);
        _sourceExtension = value["SourceExtension"]?.StringValue ?? string.Empty;
        _contentHash = value["ContentHash"]?.StringValue ?? string.Empty;

        _container = value["Container"]?.StringValue ?? string.Empty;
        _durationSeconds = value["DurationSeconds"]?.DoubleValue ?? 0;
        _startTimeSeconds = value["StartTimeSeconds"]?.DoubleValue ?? 0;
        _bitRate = value["BitRate"]?.LongValue ?? 0;

        _codedWidth = value["CodedWidth"]?.IntValue ?? 0;
        _codedHeight = value["CodedHeight"]?.IntValue ?? 0;
        _displayWidth = value["DisplayWidth"]?.IntValue ?? 0;
        _displayHeight = value["DisplayHeight"]?.IntValue ?? 0;
        _fps = value["Fps"]?.DoubleValue ?? 0;
        _videoCodec = value["VideoCodec"]?.StringValue ?? string.Empty;
        _videoProfile = value["VideoProfile"]?.StringValue;
        _rotationDegrees = value["RotationDegrees"]?.IntValue ?? 0;
        _sampleAspectNum = value["SampleAspectNum"]?.IntValue ?? 1;
        _sampleAspectDen = value["SampleAspectDen"]?.IntValue ?? 1;
        _canSeek = value["CanSeek"]?.BoolValue ?? false;

        _hasAudio = value["HasAudio"]?.BoolValue ?? false;
        _audioCodec = value["AudioCodec"]?.StringValue ?? string.Empty;
        _audioSampleRate = value["AudioSampleRate"]?.IntValue ?? 0;
        _audioChannels = value["AudioChannels"]?.IntValue ?? 0;

        _coverPng = value["CoverPng"]?.ByteArrayValue ?? [];
        _cover = null;
    }
}
