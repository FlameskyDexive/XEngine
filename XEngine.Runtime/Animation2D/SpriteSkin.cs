// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using XEngine.Echo;
using XEngine.Vector;

namespace XEngine.Runtime;

/// <summary>CPU-deforms sprite vertices with <see cref="BatchMath.SkinPositions"/> from <see cref="Bone2D"/> poses.</summary>
[AddComponentMenu("2D/Sprite Skin")]
[ComponentIcon("\uf1b3")]
public sealed class SpriteSkin : MonoBehaviour
{
    public Bone2D[] Bones = [];
    public Float4x4[] BindPoses = [];
    public Float3[] Vertices = [];
    public Float4[] Weights = [];
    public UInt4[] BoneIndices = [];

    [NonSerialized] private Float4x4[] _boneMatrices = [];

    public void CaptureBindPoses()
    {
        int count = Bones.Length;
        if (BindPoses.Length != count)
            BindPoses = new Float4x4[count];

        for (int i = 0; i < count; i++)
        {
            Transform? transform = Bones[i]?.Transform;
            BindPoses[i] = transform != null ? transform.LocalToWorldMatrix.Invert() : Float4x4.Identity;
        }
    }

    public void Deform(Span<Float3> destination)
    {
        int boneCount = Bones.Length;
        if (BindPoses.Length != boneCount)
            CaptureBindPoses();

        if (_boneMatrices.Length != boneCount)
            _boneMatrices = boneCount == 0 ? [] : new Float4x4[boneCount];

        for (int i = 0; i < boneCount; i++)
        {
            Transform? transform = Bones[i]?.Transform;
            Float4x4 world = transform != null ? transform.LocalToWorldMatrix : Float4x4.Identity;
            _boneMatrices[i] = world * BindPoses[i];
        }

        BatchMath.SkinPositions(Vertices, Weights, BoneIndices, _boneMatrices, destination);
    }
}
