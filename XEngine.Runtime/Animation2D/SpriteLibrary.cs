// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Collections.Generic;

using XEngine.Echo;

namespace XEngine.Runtime;

/// <summary>Maps (category, label) to a sprite id so a <see cref="SpriteResolver"/> can swap looks.</summary>
[CreateAssetMenu("2D/Sprite Library", Extension = ".spritelibrary", Order = 70)]
public sealed class SpriteLibrary : ScriptableObject
{
    private readonly Dictionary<(string Category, string Label), string> _entries = [];

    public void Add(string category, string label, string spriteId)
    {
        _entries[(category, label)] = spriteId;
    }

    public string? Get(string category, string label)
        => _entries.TryGetValue((category, label), out string? id) ? id : null;
}
