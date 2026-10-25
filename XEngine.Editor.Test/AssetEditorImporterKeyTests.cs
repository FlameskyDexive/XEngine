// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Editor.Inspector;

using Xunit;

namespace XEngine.Editor.Test;

/// <summary>
/// Importer-keyed asset-editor lookup: the Model Import Settings editor is registered against
/// <c>EditorModelImporter</c> (not the asset type — a model imports as a plain PrefabAsset), so
/// the inspector must resolve it through the importer name to show the tabs and wire the
/// animation preview instead of the generic prefab editor.
/// </summary>
public class AssetEditorImporterKeyTests : EditorTestHarness
{
    public AssetEditorImporterKeyTests()
    {
        EditorRegistries.Initialize();
        EditorRegistries.OnProjectOpened();
    }

    [Fact]
    public void ModelImporterName_ResolvesModelAssetEditor()
    {
        var editor = EditorRegistries.GetAssetEditorForImporter("EditorModelImporter");
        Assert.IsType<ModelAssetEditor>(editor);
    }

    [Fact]
    public void SameImporterName_ReturnsCachedInstance()
    {
        // Body and preview bar must observe one editor instance: the Animation-tab clip
        // selection is what the preview bar plays.
        var first = EditorRegistries.GetAssetEditorForImporter("EditorModelImporter");
        var second = EditorRegistries.GetAssetEditorForImporter("EditorModelImporter");
        Assert.Same(first, second);
    }

    [Fact]
    public void AssetTypeKeyedEditors_AreNotImporterKeyed()
    {
        // PrefabAssetEditor is registered against the PrefabAsset type; resolving by the
        // importer name must not surface it (models keep their import-settings editor).
        Assert.Null(EditorRegistries.GetAssetEditorForImporter("PrefabImporter"));
    }

    [Fact]
    public void UnknownOrEmptyImporterName_ReturnsNull()
    {
        Assert.Null(EditorRegistries.GetAssetEditorForImporter("NoSuchImporter"));
        Assert.Null(EditorRegistries.GetAssetEditorForImporter(""));
        Assert.Null(EditorRegistries.GetAssetEditorForImporter(null));
    }

    [Fact]
    public void PreviewClipGuard_RejectsForeignGuid()
    {
        // The preview bar passes the selected prefab's guid; a ModelAssetEditor that has never
        // drawn (or is tracking another asset) must not serve its (stale) clip list.
        var editor = new ModelAssetEditor();
        Assert.False(editor.TryGetPreviewClip(Guid.NewGuid(), out _));
    }
}
