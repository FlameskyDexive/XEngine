// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.IO;

using XEngine.Editor;
using XEngine.Editor.Importers;
using XEngine.Editor.Projects;
using XEngine.Runtime;
using XEngine.Runtime.Resources;
using XEngine.XUI;
using XEngine.XUI.Core;

namespace XEngine.XUI.Editor;

/// <summary>
/// Imports a <c>.xui</c> screen document.
/// </summary>
/// <remarks>
/// <para>
/// Import does three things: keep the text, resolve the art keys the document names into GUIDs, and
/// register those as build dependencies. Parsing happens here too, but only to report a broken
/// document in the console — the asset is produced either way, because an asset that disappears
/// mid-edit takes every reference to it along.
/// </para>
/// <para>
/// A <c>sprite=</c> / <c>font=</c> value is a project-relative asset path
/// (<c>UI/panel.png</c>, resolved under <c>Assets/</c>). Paths, not GUIDs: a GUID in a hand-authored
/// document is unreadable and unmergeable.
/// </para>
/// </remarks>
[ImporterFor(".xui")]
public sealed class XuiImporter : AssetImporter
{
    public override int Version => 1;

    public override bool Import(ImportContext ctx)
    {
        string text;
        try
        {
            text = File.ReadAllText(ctx.AbsolutePath);
        }
        catch (IOException ex)
        {
            Debug.LogError("Failed to read XUI document '" + ctx.AbsolutePath + "': " + ex.Message);
            return false;
        }

        var asset = new XuiDocumentAsset { Source = text };

        XuiDocument? document = null;
        try
        {
            document = XuiDocumentParser.Parse(text, DisplayPath(ctx.AbsolutePath));
        }
        catch (XuiParseException ex)
        {
            // Reported, not fatal: the asset still carries the source and its ParseError, so the
            // inspector and runtime_xui_* can say exactly what is wrong instead of showing nothing.
            Debug.LogError(ex.Message);
        }

        if (document != null)
            ResolveReferences(document, ctx, asset);

        ctx.SetMainAsset(asset);
        return true;
    }

    private static void ResolveReferences(XuiDocument document, ImportContext ctx, XuiDocumentAsset asset)
    {
        XuiDocumentReferences references = XuiDocumentReferences.Collect(document);

        var sprites = new List<XuiAssetBinding>();
        foreach (string key in references.Sprites)
        {
            if (TryResolve(key, ctx, nameof(Sprite), out Guid guid))
                sprites.Add(new XuiAssetBinding { Key = key, Guid = guid });
            else
                Debug.LogWarning("XUI document references sprite '" + key + "', which resolved to no asset.");
        }

        var fonts = new List<XuiAssetBinding>();
        foreach (string key in references.Fonts)
        {
            if (TryResolve(key, ctx, nameof(FontAsset), out Guid guid))
                fonts.Add(new XuiAssetBinding { Key = key, Guid = guid });
            else
                Debug.LogWarning("XUI document references font '" + key + "', which resolved to no asset.");
        }

        asset.Sprites = sprites;
        asset.Fonts = fonts;
    }

    private static bool TryResolve(string key, ImportContext ctx, string role, out Guid guid)
    {
        guid = Guid.Empty;

        string? absolute = ResolveAssetPath(key, ctx.AbsolutePath);
        if (absolute == null || !File.Exists(absolute)) return false;

        string metaPath = MetaFile.GetMetaPath(absolute);
        if (!File.Exists(metaPath)) return false;

        try
        {
            MetaFileData meta = MetaFile.Read(metaPath);
            if (meta.Guid == Guid.Empty) return false;
            guid = meta.Guid;

            // An image file's main asset is the bare Texture2D; the Sprite an AssetRef<Sprite> can
            // actually resolve is a sub-asset the TextureImporter only emits in sprite mode. Resolve
            // to that sub-asset's deterministic GUID — pointing the reference at the texture would
            // fail silently at build time, with the element falling back to the default sprite.
            if (role == nameof(Sprite) && IsImageFile(absolute) && SingleSpriteMode(meta))
            {
                guid = AssetEntry.DeriveSubAssetGuid(
                    meta.Guid, Path.GetFileNameWithoutExtension(absolute));
            }

            ctx.AddDependency(guid, role);
            return true;
        }
        catch (Exception)
        {
            return false;
        }
    }

    private static readonly string[] ImageExtensions = { ".png", ".jpg", ".jpeg", ".bmp", ".tga" };

    private static bool IsImageFile(string path)
    {
        string extension = Path.GetExtension(path);
        foreach (string known in ImageExtensions)
        {
            if (string.Equals(extension, known, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>
    /// True when the meta declares sprite mode Single. Multiple-mode sheets keep resolving to the
    /// main texture (and warn at build) — which of many slices a bare file path means is not a
    /// guess this importer should make.
    /// </summary>
    private static bool SingleSpriteMode(MetaFileData meta)
    {
        if (meta.Settings is null) return false;
        if (!meta.Settings.TryGet("sprite", out Echo.EchoObject? sprite) || sprite is null) return false;
        return sprite.TryGet("Mode", out Echo.EchoObject? mode) && mode is { IntValue: 1 };
    }

    /// <summary>
    /// Resolves a key against <c>Assets/</c> first, then against the document's own folder. The second
    /// form is what makes a screen and the art it owns movable as one folder.
    /// </summary>
    private static string? ResolveAssetPath(string key, string documentPath)
    {
        if (key.Length == 0 || key.IndexOfAny(Path.GetInvalidPathChars()) >= 0) return null;
        if (key.Contains("..", StringComparison.Ordinal)) return null;

        Project? project = Project.Current;
        if (project != null)
        {
            string underAssets = Path.Combine(project.AssetsPath, key);
            if (File.Exists(underAssets)) return underAssets;
        }

        string? folder = Path.GetDirectoryName(documentPath);
        if (folder == null) return null;

        string sibling = Path.Combine(folder, key);
        return File.Exists(sibling) ? sibling : null;
    }

    private static string DisplayPath(string absolutePath)
    {
        Project? project = Project.Current;
        if (project == null) return absolutePath;
        try
        {
            return Path.GetRelativePath(project.RootPath, absolutePath).Replace('\\', '/');
        }
        catch (ArgumentException)
        {
            return absolutePath;
        }
    }
}
