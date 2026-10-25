// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Echo;

namespace XEngine.Runtime;

/// <summary>Selects a sprite id from a <see cref="SpriteLibrary"/> by category and label.</summary>
[AddComponentMenu("2D/Sprite Resolver")]
[ComponentIcon("")]
public sealed class SpriteResolver : MonoBehaviour
{
    public SpriteLibrary? Library;
    public string Category = string.Empty;
    public string Label = string.Empty;

    public string? ResolvedId => Library?.Get(Category, Label);
}
