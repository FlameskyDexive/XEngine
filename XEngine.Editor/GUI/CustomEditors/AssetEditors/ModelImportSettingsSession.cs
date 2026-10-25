using System;
using System.IO;
using System.Text;
using XEngine.Echo;

namespace XEngine.Editor.Inspector;

/// <summary>
/// One Inspector draft owns one existing model meta. Disk IO occurs only on load,
/// Apply and Revert; drawing never rewrites or polls metadata. External changes
/// require an explicit reload rather than silently overwriting another writer.
/// </summary>
internal sealed class ModelImportSettingsSession
{
    private readonly string _assetsRoot;
    private readonly string _relativePath;
    private readonly string _metaPath;
    private readonly Guid _guid;
    private readonly string _importer;
    private byte[] _bytes;
    private EchoObject _document;
    private EchoObject? _displayBaseline;

    private ModelImportSettingsSession(string assetsRoot, AssetEntry entry, byte[] bytes, EchoObject document)
    {
        _assetsRoot = assetsRoot;
        _relativePath = entry.Path;
        _metaPath = MetaFile.GetMetaPath(Path.Combine(assetsRoot, entry.Path));
        _guid = entry.Guid;
        _importer = entry.ImporterType;
        _bytes = bytes;
        _document = document;
    }

    public EchoObject? Settings => _document.TryGet("settings", out var settings) ? settings : null;
    public Guid AssetGuid => _guid;

    public bool Matches(string assetsRoot, AssetEntry entry)
        => _guid == entry.Guid && _relativePath == entry.Path && _importer == entry.ImporterType
            && _assetsRoot == assetsRoot;

    public static ModelImportSettingsSession Load(string assetsRoot, AssetEntry entry)
    {
        if (entry.Guid == Guid.Empty) throw new InvalidDataException("The asset has no valid GUID.");
        string sourcePath = Path.GetFullPath(Path.Combine(assetsRoot, entry.Path));
        string root = Path.GetFullPath(assetsRoot).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
        if (!sourcePath.StartsWith(root, StringComparison.OrdinalIgnoreCase) || !File.Exists(sourcePath))
            throw new IOException("The model source is missing or outside this project's Assets folder.");
        byte[] bytes = File.ReadAllBytes(MetaFile.GetMetaPath(sourcePath));
        // StreamReader preserves the same BOM handling as MetaFile.Read.
        using var reader = new StreamReader(new MemoryStream(bytes), Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        EchoObject document = EchoObject.ReadFromString(reader.ReadToEnd());
        if (document.TagType != EchoType.Compound
            || !document.TryGet("guid", out var guid) || !Guid.TryParse(guid.StringValue, out var parsed) || parsed != entry.Guid
            || !document.TryGet("importer", out var importer) || importer.StringValue != entry.ImporterType)
            throw new InvalidDataException("The meta GUID or importer does not match the selected model.");
        if (document.TryGet("settings", out var settings) && settings.TagType != EchoType.Compound)
            throw new InvalidDataException("Model settings must be a compound document.");
        return new ModelImportSettingsSession(assetsRoot, entry, bytes, document);
    }

    public void CaptureDisplay(EchoObject displayed) => _displayBaseline = displayed.Clone();
    public bool IsDirty(EchoObject displayed) => _displayBaseline == null || !_displayBaseline.Equals(displayed);

    public bool Apply(EchoObject displayed)
    {
        if (_displayBaseline == null) throw new InvalidOperationException("Settings were not loaded.");
        if (!File.Exists(Path.Combine(_assetsRoot, _relativePath)))
            throw new IOException("The model source is missing. Restore it before applying settings.");
        RequireUnchanged();
        if (!IsDirty(displayed)) return false;

        EchoObject document = _document.Clone();
        EchoObject settings = Settings?.Clone() ?? EchoObject.NewCompound();
        PatchChangedFields(settings, _displayBaseline, displayed);
        document["settings"] = settings;
        byte[] savedBytes = Encoding.UTF8.GetBytes(document.WriteToString());
        string tempPath = _metaPath + ".model-settings-" + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(tempPath, savedBytes);
            RequireUnchanged();
            File.Move(tempPath, _metaPath, overwrite: true);
        }
        finally
        {
            if (File.Exists(tempPath)) File.Delete(tempPath);
        }
        _bytes = savedBytes;
        _document = document;
        CaptureDisplay(displayed);
        return true;
    }

    public void RequireUnchanged()
    {
        if (!_bytes.AsSpan().SequenceEqual(File.ReadAllBytes(_metaPath)))
            throw new IOException("The meta changed outside this Inspector. Revert to reload it before applying.");
    }

    private static void PatchChangedFields(EchoObject destination, EchoObject before, EchoObject after)
    {
        foreach (var pair in after.Tags)
        {
            before.TryGet(pair.Key, out var old);
            if (pair.Value.Equals(old)) continue;
            if (pair.Value.TagType == EchoType.Compound && old?.TagType == EchoType.Compound)
            {
                if (!destination.TryGet(pair.Key, out var nested) || nested.TagType != EchoType.Compound)
                    nested = EchoObject.NewCompound();
                else nested = nested.Clone();
                PatchChangedFields(nested, old, pair.Value);
                destination[pair.Key] = nested;
            }
            else destination[pair.Key] = pair.Value.Clone();
        }
    }
}
