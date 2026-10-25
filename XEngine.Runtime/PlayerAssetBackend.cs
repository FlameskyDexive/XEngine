using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Reflection;

using XEngine.Echo;
using XEngine.Runtime.AssetStreaming;

using Scene = XEngine.Runtime.Resources.Scene;

namespace XEngine.Runtime;

/// <summary>
/// Asset database for built standalone players.
/// Loads binary Echo cache files from loose files, XEnginePak archives, or embedded resources.
/// </summary>
public class PlayerAssetBackend : PlayerAssetBackendBase
{
    private readonly AssetPackagingMode _mode;
    private readonly string _basePath;
    private readonly string _providerBasePath;
    private readonly IAssetStreamProvider? _streamProvider;
    private readonly bool _ownsStreamProvider;
    private readonly Dictionary<Guid, string> _guidToPath = new();
    private readonly List<ZipArchive> _pakArchives = new();

    public override Dictionary<string, Guid> ResourcesMap { get; } = new(StringComparer.OrdinalIgnoreCase);
    public override Guid DefaultSceneGuid { get; protected set; }

    public PlayerAssetBackend(AssetPackagingMode mode, string basePath = "Content")
    {
        _mode = mode;
        _basePath = Path.Combine(Application.DataPath, basePath);
        _providerBasePath = string.Empty;
        if (mode != AssetPackagingMode.Embedded)
        {
            _streamProvider = new DirectoryAssetStreamProvider(_basePath);
            _ownsStreamProvider = true;
        }
        Initialize();
    }

    /// <summary>
    /// Creates a player backend over a platform stream provider, such as an Android package VFS.
    /// The caller retains ownership of <paramref name="streamProvider"/>.
    /// </summary>
    public PlayerAssetBackend(
        AssetPackagingMode mode,
        IAssetStreamProvider streamProvider,
        string basePath = "")
    {
        ArgumentNullException.ThrowIfNull(streamProvider);
        if (mode == AssetPackagingMode.Embedded)
            throw new ArgumentException("Embedded assets use assembly resources, not a stream provider.", nameof(mode));
        _mode = mode;
        _providerBasePath = AssetStreamPath.Normalize(basePath, allowEmpty: true);
        _basePath = _providerBasePath.Length == 0
            ? streamProvider.Description
            : streamProvider.Description + "/" + _providerBasePath;
        _streamProvider = streamProvider;
        Initialize();
    }

    private void Initialize()
    {
        switch (_mode)
        {
            case AssetPackagingMode.LooseFiles:
            case AssetPackagingMode.XEnginePak:
                LoadManifestFromProvider("asset_manifest.bin");
                if (_mode == AssetPackagingMode.XEnginePak) LoadPakArchives();
                break;
            case AssetPackagingMode.Embedded:
                LoadManifestFromEmbedded();
                break;
        }
    }

    protected override EngineObject? LoadFresh(Guid assetId)
    {
        try
        {
            byte[]? data = LoadRawAsset(assetId);
            if (data == null) return null;

            var echo = ReadEchoBinary(data);
            if (echo == null) return null;

            var obj = Serializer.Deserialize<EngineObject>(echo);
            if (obj != null)
            {
                obj.AssetID = assetId;
                SetLoaded(assetId, obj);
            }
            return obj;
        }
        catch (Exception ex)
        {
            Debug.LogError($"[PlayerAssetBackend] Failed to load asset {assetId}: {ex.Message}");
            return null;
        }
    }

    /// <summary>Load a scene by GUID.</summary>
    public override Scene? LoadScene(Guid sceneGuid)
    {
        byte[]? data = LoadRawAsset(sceneGuid);
        if (data == null) { Debug.LogError($"[PlayerAssetBackend] Scene not found: {sceneGuid}"); return null; }

        try
        {
            var echo = ReadEchoBinary(data);
            if (echo == null) return null;

            return Serializer.Deserialize<Scene>(echo);
        }
        catch (Exception ex)
        {
            Debug.LogError($"[PlayerAssetBackend] Failed to load scene {sceneGuid}: {ex.Message}");
            return null;
        }
    }

    // ================================================================
    //  Raw asset loading
    // ================================================================

    private byte[]? LoadRawAsset(Guid guid)
    {
        string fileName = $"{guid}.asset";

        return _mode switch
        {
            AssetPackagingMode.LooseFiles => LoadFromProvider(fileName),
            AssetPackagingMode.XEnginePak => LoadFromPak(fileName),
            AssetPackagingMode.Embedded => LoadFromEmbedded($"Assets.{fileName}"),
            _ => null,
        };
    }

    private byte[]? LoadFromProvider(string path)
    {
        using Stream? stream = _streamProvider!.OpenRead(GetProviderPath(path));
        if (stream == null)
            return null;
        using var output = new MemoryStream();
        stream.CopyTo(output);
        return output.ToArray();
    }

    private byte[]? LoadFromPak(string entryName)
    {
        // ZipArchive is not thread-safe. Serialize all pak reads on the same lock Get() uses (it is
        // re-entrant, so Get -> LoadRawAsset -> LoadFromPak is fine) so a background Get and a
        // main-thread LoadScene can't read the shared archive/stream concurrently.
        lock (_loadLock)
        {
            foreach (var pak in _pakArchives)
            {
                var entry = pak.GetEntry(entryName);
                if (entry != null)
                {
                    using var stream = entry.Open();
                    using var ms = new MemoryStream();
                    stream.CopyTo(ms);
                    return ms.ToArray();
                }
            }
            return null;
        }
    }

    private static byte[]? LoadFromEmbedded(string resourceName)
    {
        var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream(resourceName);
        if (stream == null) return null;
        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        return ms.ToArray();
    }

    // ================================================================
    //  Manifest loading
    // ================================================================

    private void LoadManifestFromProvider(string manifestPath)
    {
        using Stream? stream = _streamProvider!.OpenRead(GetProviderPath(manifestPath));
        if (stream == null)
        {
            Debug.LogError($"[PlayerAssetBackend] Manifest not found in {_basePath}: {manifestPath}");
            return;
        }

        try
        {
            ParseManifest(ReadEchoBinary(stream));
        }
        catch (Exception ex)
        {
            Debug.LogError($"[PlayerAssetBackend] Failed to parse manifest: {ex.Message}");
        }
    }

    private void LoadPakArchives()
    {
        IReadOnlyList<string> pakFiles = _streamProvider!.GetFiles(_providerBasePath, ".xenginepak");
        for (int i = 0; i < pakFiles.Count; i++)
        {
            Stream? stream = null;
            try
            {
                stream = _streamProvider.OpenRead(pakFiles[i]);
                if (stream == null)
                    continue;
                _pakArchives.Add(new ZipArchive(stream, ZipArchiveMode.Read, leaveOpen: false));
                stream = null;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[PlayerAssetBackend] Failed to open pak {pakFiles[i]}: {ex.Message}");
            }
            finally
            {
                stream?.Dispose();
            }
        }
    }

    private void LoadManifestFromEmbedded()
    {
        var assembly = Assembly.GetEntryAssembly() ?? Assembly.GetExecutingAssembly();
        using var stream = assembly.GetManifestResourceStream("Assets._manifest.bin");
        if (stream == null) { Debug.LogError("[PlayerAssetBackend] Embedded manifest not found."); return; }

        using var ms = new MemoryStream();
        stream.CopyTo(ms);
        ParseManifest(ReadEchoBinary(ms.ToArray()));
    }

    private void ParseManifest(EchoObject? echo)
    {
        if (echo == null) return;

        if (echo.TryGet("defaultScene", out var dsTag) && Guid.TryParse(dsTag.StringValue, out var ds))
            DefaultSceneGuid = ds;

        if (echo.TryGet("assets", out var assetsTag) && assetsTag.TagType == EchoType.Compound)
            foreach (var kvp in assetsTag.Tags)
                if (Guid.TryParse(kvp.Key, out var guid))
                    _guidToPath[guid] = kvp.Value.StringValue;

        if (echo.TryGet("resources", out var resTag) && resTag.TagType == EchoType.Compound)
            foreach (var kvp in resTag.Tags)
                if (Guid.TryParse(kvp.Value.StringValue, out var guid))
                    ResourcesMap[kvp.Key] = guid;
    }

    /// <summary>Read Echo binary from byte array.</summary>
    private static EchoObject? ReadEchoBinary(byte[] data)
    {
        using var ms = new MemoryStream(data);
        return ReadEchoBinary(ms);
    }

    private static EchoObject? ReadEchoBinary(Stream stream)
    {
        using var reader = new BinaryReader(stream);
        return EchoObject.ReadFromBinary(reader);
    }

    private string GetProviderPath(string fileName) =>
        AssetStreamPath.Combine(_providerBasePath, fileName);

    public override void Dispose()
    {
        foreach (var pak in _pakArchives) pak.Dispose();
        _pakArchives.Clear();
        if (_ownsStreamProvider)
            _streamProvider?.Dispose();
        _loadedAssets.Clear();
    }
}
