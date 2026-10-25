// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Echo;

namespace XEngine.Runtime;

/// <summary>A 2D skeleton joint. SpriteSkin deforms vertices from this transform.</summary>
[AddComponentMenu("2D/Bone 2D")]
[ComponentIcon("\uf1b3")]
public sealed class Bone2D : MonoBehaviour
{
    [SerializeField] private float length = 1f;

    public float Length
    {
        get => length;
        set => length = value < 0f ? 0f : value;
    }
}
