using System;
using System.IO;

using XEngine.Echo;
using XEngine.Editor.GUI;
using XEngine.Editor.Projects;
using XEngine.Editor.Theming;
using XEngine.OrigamiUI;
using XEngine.PaperUI;
using XEngine.PaperUI.LayoutEngine;
using XEngine.Runtime;
using XEngine.Runtime.Resources;
using XEngine.Vector.Spatial;

namespace XEngine.Editor.Inspector;

[CustomAssetEditor(typeof(Texture2D))]
public class TextureAssetEditor : AssetImporterEditor
{
    // Cache settings across frames so changes stick until Save
    private EchoObject? _cachedSettings;
    private bool _dirty;
    private Guid _cachedForGuid;

    public override void OnGUI(Paper paper, string id, AssetEntry entry, EngineObject? asset)
    {
        var font = EditorTheme.DefaultFont;
        if (font == null) return;
        var m = Origami.Current.Metrics;
        AssetRef<Texture2D> texture = (AssetRef<Texture2D>)(Texture2D)asset;

        if (texture != null)
        {
            // Preview is rendered by the Inspector's universal preview bar.

            // Quick-facts chip strip.
            using (paper.Row($"{id}_stats").Height(UnitValue.Auto)
                .Margin(m.PaddingLarge, m.PaddingLarge, 0, m.SpacingLarge).RowBetween(m.SpacingMedium).Enter())
            {
                EditorGUI.StatChip(paper, $"{id}_st_size", $"{texture.Res!.Width} x {texture.Res.Height}", font);
                EditorGUI.StatChip(paper, $"{id}_st_fmt", texture.Res.ImageFormat.ToString(), font);
                EditorGUI.StatChip(paper, $"{id}_st_mip", texture.Res.IsMipmapped ? "Mipmapped" : "No Mipmaps", font);
                paper.Box($"{id}_st_pad").Height(1).IsNotInteractable();
            }
        }

        if (Project.Current == null) return;
        string absPath = Path.Combine(Project.Current.AssetsPath, entry.Path);
        string metaPath = MetaFile.GetMetaPath(absPath);
        if (!File.Exists(metaPath)) return;

        // Load and cache settings (only reload when asset changes)
        if (_cachedSettings == null || _cachedForGuid != entry.Guid)
        {
            var meta = MetaFile.Read(metaPath);
            _cachedSettings = meta.Settings ?? EchoObject.NewCompound();

            var defaults = new Importers.TextureImporter().DefaultSettings();
            if (defaults != null)
                foreach (var kvp in defaults.Tags)
                    if (!_cachedSettings.TryGet(kvp.Key, out _))
                        _cachedSettings[kvp.Key] = kvp.Value.Clone();

            _dirty = false;
            _cachedForGuid = entry.Guid;
        }

        var settings = _cachedSettings;

        EditorGUI.SectionHeader(paper, $"{id}_settings_hdr", "Import Settings", first: texture == null);

        bool genMips = settings.TryGet("generateMipmaps", out var mipTag) && mipTag.BoolValue;
        EditorGUI.SettingsToggle(paper, $"{id}_mips", "Generate Mipmaps", genMips,
            v => { settings["generateMipmaps"] = new EchoObject(v); _dirty = true; }, separator: false);

        bool srgb = settings.TryGet("sRGB", out var srgbTag) && srgbTag.BoolValue;
        EditorGUI.SettingsToggle(paper, $"{id}_srgb", "sRGB", srgb,
            v => { settings["sRGB"] = new EchoObject(v); _dirty = true; }, separator: false);

        var alphaSource = Importers.TextureImporter.ResolveAlphaSource(settings);
        EditorGUI.Row(paper, $"{id}_alpha", "Alpha Source", () =>
            Origami.EnumDropdown(paper, $"{id}_alpha_v", alphaSource,
                v => { settings["alphaSource"] = new EchoObject((int)v); _dirty = true; }).Show());

        var currentMin = settings.TryGet("minFilter", out var minTag)
            ? (TextureMin)minTag.IntValue : TextureMin.LinearMipmapLinear;
        EditorGUI.Row(paper, $"{id}_min", "Min Filter", () =>
            Origami.EnumDropdown(paper, $"{id}_min_v", currentMin,
                v => { settings["minFilter"] = new EchoObject((int)v); _dirty = true; }).Show());

        var currentMag = settings.TryGet("magFilter", out var magTag)
            ? (TextureMag)magTag.IntValue : TextureMag.Linear;
        EditorGUI.Row(paper, $"{id}_mag", "Mag Filter", () =>
            Origami.EnumDropdown(paper, $"{id}_mag_v", currentMag,
                v => { settings["magFilter"] = new EchoObject((int)v); _dirty = true; }).Show());

        var currentWrap = settings.TryGet("wrapMode", out var wrapTag)
            ? (TextureWrap)wrapTag.IntValue : TextureWrap.Repeat;
        EditorGUI.Row(paper, $"{id}_wrap", "Wrap Mode", () =>
            Origami.EnumDropdown(paper, $"{id}_wrap_v", currentWrap,
                v => { settings["wrapMode"] = new EchoObject((int)v); _dirty = true; }).Show());

        // Sprite settings: mode + a button to open the full Sprite Editor. The Sprite Editor edits the
        // shared settings instance and flags it dirty; persisting happens here via Save & Reimport.
        var spriteTarget = Importers.SpriteEditRegistry.Get(entry.Guid);
        Origami.Header(paper, $"{id}_sprite_hdr", "Sprite").Show();
        EditorGUI.Row(paper, $"{id}_spmode", "Sprite Mode", () =>
            Origami.EnumDropdown(paper, $"{id}_spmode_v", spriteTarget.Settings.Mode,
                v => { spriteTarget.Settings.Mode = v; spriteTarget.Dirty = true; }).Show());

        if (spriteTarget.Settings.Mode != Importers.SpriteMode.None)
            Origami.Button(paper, $"{id}_spopen", $"{EditorIcons.PenToSquare}  Open Sprite Editor",
                () => SpriteEditorWindow.OpenFor(entry.Guid)).Width(200).Show();

        // Save CTA - disabled until an import setting or the sprite config changes (and never in read-only,
        // e.g. when this editor is shown for a texture sub-asset). It's a raw Box, so it must check IsReadOnly.
        bool dirty = !Origami.IsReadOnly && (_dirty || spriteTarget.Dirty);
        paper.Box($"{id}_save").Width(UnitValue.Auto).Height(30)
            .Margin(m.PaddingLarge, m.PaddingLarge, m.SpacingLarge, m.SpacingLarge).Rounded(8).Padding(16, 16, 0, 0)
            .BackgroundColor(dirty ? EditorTheme.Accent : EditorTheme.Neutral300)
            .Hovered.BackgroundColor(dirty ? EditorTheme.AccentBright : EditorTheme.Neutral300).End()
            .Text($"{EditorIcons.FloppyDisk}  Save & Reimport", EditorTheme.FontSemiBold ?? font)
            .TextColor(dirty ? System.Drawing.Color.White : EditorTheme.Ink300).FontSize(EditorTheme.FontSizeSmall)
            .Alignment(TextAlignment.MiddleCenter)
            .OnClick(0, (_, _) =>
            {
                if (!dirty) return;
                Importers.TextureSpriteMeta.WriteInto(settings, spriteTarget.Settings);
                var meta = MetaFile.Read(metaPath);
                meta.Settings = settings;
                MetaFile.Write(metaPath, meta);
                _cachedSettings = null;
                _dirty = false;
                Importers.SpriteEditRegistry.ClearDirty(entry.Guid);
                EditorAssetBackend.Instance?.Reimport(entry.Guid);
            });
    }

}
