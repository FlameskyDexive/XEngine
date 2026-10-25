// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using XEngine.Echo;
using XEngine.Editor.Core;
using XEngine.Editor.Projects;
using XEngine.Editor.Utils;
using XEngine.OrigamiUI;
using XEngine.Rosetta;
using XEngine.Runtime;

namespace XEngine.Editor.Presets;

/// <summary>
/// Create / find / apply <see cref="Preset"/> assets (Unity's preset workflow). Presets capture a
/// component's or asset's serialized field values as Echo text in a <c>.preset</c> file; applying
/// copies those fields back onto a same-type target, honoring the preset's excluded-field list.
/// A per-type default preset (Preset Manager) is auto-applied to newly added components.
/// </summary>
public static class PresetUtility
{
    public const string Extension = ".preset";

    // ── Create ───────────────────────────────────────────────

    /// <summary>
    /// Capture <paramref name="target"/>'s current values as a new .preset asset in
    /// <paramref name="relativeFolder"/> (project-relative; empty = Assets root).
    /// Returns the created path, or null.
    /// </summary>
    public static string? CreatePreset(object target, string relativeFolder)
    {
        var type = target.GetType();
        var echo = Serializer.Serialize(type, target);
        if (echo == null) return null;

        var preset = new Preset
        {
            Name = $"{type.Name} Preset",
            TargetType = type.AssemblyQualifiedName,
            Data = echo.WriteToString(),
        };

        string? absFolder = ResolveAbsoluteFolder(relativeFolder);
        if (absFolder == null) return null;

        string fileName = UniqueNames.ForFile(absFolder, $"{type.Name}", Extension);
        string filePath = Path.Combine(absFolder, fileName);

        try
        {
            var wrapped = Serializer.Serialize(typeof(object), preset);
            if (wrapped == null) return null;
            File.WriteAllText(filePath, wrapped.WriteToString());
            // Mint the .meta so the folder scan imports the new asset immediately
            // (same importer as .asset; the $type envelope recovers the Preset type).
            MetaFile.EnsureMeta(filePath, "ScriptableObjectImporter");
            EditorAssetBackend.Instance?.InvalidateFolderIndex();
            Debug.Log($"[Presets] Created '{fileName}' from {type.Name}");
            return Path.Combine(relativeFolder, fileName).Replace('\\', '/');
        }
        catch (Exception ex)
        {
            Debug.LogError($"[Presets] Failed to create preset: {ex.Message}");
            return null;
        }
    }

    private static string? ResolveAbsoluteFolder(string relativeFolder)
    {
        if (Project.Current == null) return null;
        string abs = string.IsNullOrEmpty(relativeFolder)
            ? Project.Current.AssetsPath
            : Path.Combine(Project.Current.AssetsPath, relativeFolder);
        return Directory.Exists(abs) ? abs : null;
    }

    // ── Find ─────────────────────────────────────────────────

    /// <summary>All preset assets whose target type matches <paramref name="type"/> exactly.</summary>
    public static List<(AssetEntry entry, Preset preset)> FindPresets(Type type)
    {
        var result = new List<(AssetEntry, Preset)>();
        var db = EditorAssetBackend.Instance;
        if (db == null) return result;

        string aqn = type.AssemblyQualifiedName;
        foreach (var entry in db.GetAllEntries())
        {
            if (!entry.Path.EndsWith(Extension, StringComparison.OrdinalIgnoreCase)) continue;
            if (entry.MainAssetType != typeof(Preset)) continue;

            if (Runtime.AssetDatabase.Get(entry.Guid) is Preset p && p.TargetType == aqn)
                result.Add((entry, p));
        }
        return result;
    }

    // ── Apply ────────────────────────────────────────────────

    /// <summary>Apply the preset's captured values onto a component (undoable, honors exclusions).</summary>
    public static bool ApplyToComponent(Preset preset, MonoBehaviour comp)
    {
        if (preset.ResolveTargetType() != comp.GetType()) return false;
        var source = DeserializeSource(preset, comp.GetType());
        if (source == null) return false;
        return ComponentClipboard.ApplyFieldsFrom(comp, source, Loc.Get("presets.apply"), BuildExcludeSet(preset));
    }

    /// <summary>
    /// Apply the preset's captured values onto an in-memory asset (e.g. a selected Material).
    /// The asset is validated; persisting to disk follows the asset's own save pipeline.
    /// </summary>
    public static bool ApplyToAsset(Preset preset, EngineObject asset)
    {
        var type = asset.GetType();
        if (preset.ResolveTargetType() != type) return false;
        var source = DeserializeSource(preset, type);
        if (source == null) return false;

        var exclude = BuildExcludeSet(preset);
        foreach (var field in PropertyGridRenderer.GetSerializableFields(type))
        {
            if (exclude.Contains(field.Name)) continue;
            try { field.SetValue(asset, field.GetValue(source)); } catch { }
        }
        asset.OnValidate();
        Debug.Log($"[Presets] Applied '{preset.Name}' to {asset.Name}");
        return true;
    }

    /// <summary>Apply a preset captured from an importer-settings object to an asset's .meta
    /// settings, then reimport. <paramref name="settingsEcho"/> is rewritten in place.</summary>
    public static bool ApplyToImporterSettings(Preset preset, EchoObject settingsEcho)
    {
        var source = DeserializeSource(preset, typeof(object));
        if (source == null) return false;

        // Importer settings presets store the settings compound directly; overlay its tags.
        var src = Serializer.Serialize(typeof(object), source);
        if (src == null || src.TagType != EchoType.Compound) return false;
        foreach (var kvp in src.Tags.ToList())
            settingsEcho[kvp.Key] = kvp.Value;
        return true;
    }

    private static object? DeserializeSource(Preset preset, Type type)
    {
        try
        {
            var echo = EchoObject.ReadFromString(preset.Data);
            if (echo == null) return null;
            return Serializer.Deserialize(echo, type == typeof(object) ? preset.ResolveTargetType() ?? typeof(object) : type);
        }
        catch (Exception ex)
        {
            Debug.LogWarning($"[Presets] Failed to read preset data: {ex.Message}");
            return null;
        }
    }

    private static HashSet<string> BuildExcludeSet(Preset preset)
        => preset.Excluded.Count == 0 ? new HashSet<string>() : new HashSet<string>(preset.Excluded);

    // ── Preset Manager (per-type defaults) ───────────────────

    private static readonly Dictionary<string, Guid> s_defaults = new();
    private static bool s_defaultsLoaded;

    private static string DefaultsPath => Project.Current == null
        ? ""
        : Path.Combine(Project.Current.RootPath, ".xengine", "preset-defaults.json");

    private static void LoadDefaults()
    {
        s_defaultsLoaded = true;
        s_defaults.Clear();
        try
        {
            if (!File.Exists(DefaultsPath)) return;
            var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(DefaultsPath));
            foreach (var prop in doc.RootElement.EnumerateObject())
                if (prop.Value.ValueKind == System.Text.Json.JsonValueKind.String && Guid.TryParse(prop.Value.GetString(), out var guid))
                    s_defaults[prop.Name] = guid;
        }
        catch { /* corrupt defaults file: start empty */ }
    }

    private static void SaveDefaults()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(DefaultsPath)!);
            using var writer = new System.Text.Json.Utf8JsonWriter(File.Create(DefaultsPath));
            writer.WriteStartObject();
            foreach (var kvp in s_defaults)
                writer.WriteString(kvp.Key, kvp.Value);
            writer.WriteEndObject();
        }
        catch (Exception ex) { Debug.LogWarning($"[Presets] Failed to save defaults: {ex.Message}"); }
    }

    /// <summary>The preset auto-applied to newly added components of this type, if any.</summary>
    public static Preset? GetDefaultPreset(Type type)
    {
        if (!s_defaultsLoaded) LoadDefaults();
        if (!s_defaults.TryGetValue(type.AssemblyQualifiedName, out var guid)) return null;
        return Runtime.AssetDatabase.Get(guid) as Preset;
    }

    /// <summary>Set (or clear, with null) the default preset for a component type.
    /// The preset must be an imported asset (it is addressed by its asset guid).</summary>
    public static void SetDefaultPreset(Type type, Preset? preset)
    {
        if (!s_defaultsLoaded) LoadDefaults();
        string key = type.AssemblyQualifiedName;
        if (preset == null || ((EngineObject)preset).AssetID == Guid.Empty)
            s_defaults.Remove(key);
        else
            s_defaults[key] = ((EngineObject)preset).AssetID;
        SaveDefaults();
    }

    /// <summary>All registered per-type defaults (type aqn → preset), for the Preferences panel.</summary>
    public static IReadOnlyDictionary<string, Guid> AllDefaults
    {
        get { if (!s_defaultsLoaded) LoadDefaults(); return s_defaults; }
    }

    /// <summary>Apply the registered default preset (if any) to a freshly added component.</summary>
    internal static void ApplyDefaultTo(MonoBehaviour comp)
    {
        try
        {
            var preset = GetDefaultPreset(comp.GetType());
            if (preset != null) ApplyToComponent(preset, comp);
        }
        catch { /* a bad default preset must never break Add Component */ }
    }
}
