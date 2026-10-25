// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using XEngine.Runtime;
using XEngine.Runtime.Resources;

namespace XEngine.XUI;

/// <summary>
/// Resolves the sprite and font keys a document names to real asset references.
/// <para>
/// Documents reference art by key, never by GUID — a GUID in a hand-authored file is unreadable and
/// unmergeable. The importer resolves every key while the asset database is available and stores the
/// GUIDs on the imported document, so the runtime build path never touches paths and the build's
/// dependency graph sees the edges.
/// </para>
/// </summary>
public interface IXuiAssetResolver
{
    bool TryResolveSprite(string key, out AssetRef<Sprite> sprite);
    bool TryResolveFont(string key, out AssetRef<FontAsset> font);
}

/// <summary>A resolver that knows nothing; every key misses and the element keeps its default look.</summary>
public sealed class XuiNullAssetResolver : IXuiAssetResolver
{
    public static XuiNullAssetResolver Instance { get; } = new();

    public bool TryResolveSprite(string key, out AssetRef<Sprite> sprite)
    {
        sprite = default;
        return false;
    }

    public bool TryResolveFont(string key, out AssetRef<FontAsset> font)
    {
        font = default;
        return false;
    }
}

/// <summary>Key → GUID tables, as produced by the <c>.xui</c> importer and carried on the asset.</summary>
public sealed class XuiAssetTable : IXuiAssetResolver
{
    private readonly Dictionary<string, Guid> _sprites = new(StringComparer.Ordinal);
    private readonly Dictionary<string, Guid> _fonts = new(StringComparer.Ordinal);

    public void AddSprite(string key, Guid guid) => _sprites[key] = guid;

    public void AddFont(string key, Guid guid) => _fonts[key] = guid;

    public IReadOnlyDictionary<string, Guid> Sprites => _sprites;

    public IReadOnlyDictionary<string, Guid> Fonts => _fonts;

    public bool TryResolveSprite(string key, out AssetRef<Sprite> sprite)
    {
        if (_sprites.TryGetValue(key, out Guid guid) && guid != Guid.Empty)
        {
            sprite = new AssetRef<Sprite>(guid);
            return true;
        }
        sprite = default;
        return false;
    }

    public bool TryResolveFont(string key, out AssetRef<FontAsset> font)
    {
        if (_fonts.TryGetValue(key, out Guid guid) && guid != Guid.Empty)
        {
            font = new AssetRef<FontAsset>(guid);
            return true;
        }
        font = default;
        return false;
    }
}
