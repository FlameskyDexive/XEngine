// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.IO;

using XEngine.Editor.Importers;
using XEngine.Runtime;
using XEngine.XUI.Core;

namespace XEngine.XUI.Editor;

/// <summary>
/// Imports a gettext <c>.po</c> file as a translation catalog, and registers it with <see cref="Xui"/>.
/// </summary>
/// <remarks>
/// <para>
/// The locale comes from the file name — <c>Assets/UI/Locales/zh-Hans.po</c> is <c>zh-Hans</c> — so a
/// file cannot claim one language in its name and another in its header. Registration happens at import
/// rather than being left to game code: saving a translation then shows up on screen immediately, which
/// is the only way a translator can check their own work in context.
/// </para>
/// <para>
/// A malformed file is reported and produces an empty catalog rather than failing the import. An asset
/// that vanishes mid-edit takes every reference to it along, and a missing translation only ever means
/// the source string shows through.
/// </para>
/// </remarks>
[ImporterFor(".po")]
public sealed class PoImporter : AssetImporter
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
            Debug.LogError("Failed to read translation catalog '" + ctx.AbsolutePath + "': " + ex.Message);
            return false;
        }

        string locale = Path.GetFileNameWithoutExtension(ctx.AbsolutePath);
        var asset = new XuiCatalogAsset { Source = text, Locale = locale };

        if (asset.ParseError is { } error)
        {
            Debug.LogError(error);
        }
        else if (asset.Catalog is { } catalog)
        {
            Xui.RegisterCatalog(catalog);
            Debug.Log(
                "XUI: loaded " + catalog.Count.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " translation(s) for locale '" + locale + "'.");
        }

        ctx.SetMainAsset(asset);
        return true;
    }
}
