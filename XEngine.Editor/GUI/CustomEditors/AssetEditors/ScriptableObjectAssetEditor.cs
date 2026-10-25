// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.IO;

using XEngine.Echo;
using XEngine.Editor.GUI;
using XEngine.Editor.Projects;
using XEngine.Editor.Theming;
using XEngine.OrigamiUI;
using XEngine.PaperUI;
using XEngine.Runtime;

using PropertyGridUtils = XEngine.Editor.GUI.PropertyGridUtils;

namespace XEngine.Editor.Inspector;

/// <summary>
/// Inspector for <see cref="ScriptableObject"/> (and any <c>.asset</c> data asset): shows the asset
/// header (path, type, AssetID), a reflection-driven property grid for the serialized fields, and
/// a Save button that writes the asset back to disk in Echo text format and reimports it. This is
/// the default editor selected via <c>[CustomAssetEditor(typeof(ScriptableObject))]</c> - because
/// <see cref="EditorRegistries.LookupEditor"/> walks the base-type chain, every ScriptableObject
/// subclass lands here unless it registers its own editor.
/// </summary>
[CustomAssetEditor(typeof(ScriptableObject))]
public class ScriptableObjectAssetEditor : AssetImporterEditor
{
    private Guid _currentGuid;
    private bool _dirty;

    public override void OnGUI(Paper paper, string id, AssetEntry entry, EngineObject? asset)
    {
        // Reset per-asset transient state when the selection changes.
        if (_currentGuid != entry.Guid)
        {
            _currentGuid = entry.Guid;
            _dirty = false;
        }

        // Embed the GUID in element IDs so Paper UI state is unique per asset.
        id = $"{id}_{entry.Guid:N}";

        var so = asset as ScriptableObject;

        Origami.Header(paper, $"{id}_h_info", $"{EditorIcons.FileCode}  {(so?.GetType().Name ?? "ScriptableObject")}").Show();
        Origami.Label(paper, $"{id}_path", $"Path: {entry.Path}").Show();
        Origami.Label(paper, $"{id}_type", $"Type: {entry.MainAssetType?.FullName ?? "(unknown)"}").Show();
        Origami.Label(paper, $"{id}_guid", $"Asset ID: {entry.Guid}").Show();

        if (so == null)
        {
            Origami.Label(paper, $"{id}_none", "ScriptableObject failed to load.").Show();
            return;
        }

        // Serialized fields (public + [SerializeField]) and [ShowInInspector] members. The grid's
        // OnFieldChanged hook calls OnValidate after any edit, so derived state recomputes inline.
        Origami.Separator(paper, $"{id}_sep_fields").Show();
        PropertyGridUtils.Draw(paper, $"{id}_props", so, _ => _dirty = true);

        if (_dirty)
        {
            Origami.Separator(paper, $"{id}_sep_save").Show();
            Origami.Button(paper, $"{id}_save", $"{EditorIcons.FloppyDisk}  Save Asset",
                () => SaveAsset(so, entry)).Show();
        }
    }

    /// <summary>
    /// Serialize the ScriptableObject back to its <c>.asset</c> source file in Echo text format and
    /// reimport. Mirrors <c>MaterialAssetEditor.SaveMaterial</c>: the AssetID is briefly cleared so
    /// the serializer writes the full object graph rather than an <c>$extern</c> reference stub.
    /// </summary>
    private void SaveAsset(ScriptableObject so, AssetEntry entry)
    {
        if (Project.Current == null) return;

        string absolutePath = Path.Combine(Project.Current.AssetsPath, entry.Path);
        try
        {
            var echo = SerializeFullAsset(so);
            File.WriteAllText(absolutePath, echo.WriteToString());
            _dirty = false;
            EditorAssetBackend.Instance?.Reimport(entry.Guid);
        }
        catch (Exception ex)
        {
            Runtime.Debug.LogError($"Failed to save ScriptableObject '{entry.Path}': {ex.Message}");
        }
    }

    /// <summary>Serialize a complete asset while preserving its live database identity.</summary>
    internal static EchoObject SerializeFullAsset(ScriptableObject scriptableObject)
    {
        ArgumentNullException.ThrowIfNull(scriptableObject);

        Guid savedId = scriptableObject.AssetID;
        try
        {
            scriptableObject.AssetID = Guid.Empty;
            // typeof(object) forces a $type envelope so the importer recovers the concrete subtype.
            return Serializer.Serialize(typeof(object), scriptableObject);
        }
        finally
        {
            scriptableObject.AssetID = savedId;
        }
    }
}
