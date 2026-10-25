// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using XEngine.Runtime.Resources;

namespace XEngine.Runtime.Rendering;

/// <summary>
/// Persistent camera textures shared by TAA / STP / SSR / SSGI / volumetric fog (U17-M2).
/// Distinct from per-effect local history: one slot per <see cref="CameraHistoryId"/>.
/// </summary>
public enum CameraHistoryId : byte
{
    Color = 0,
    Depth = 1,
    Taa = 2,
    Ssgi = 3,
}

/// <summary>
/// Persistent RTHandles for one camera. Reuses GPU targets while size and format match.
/// </summary>
public sealed class CameraHistory : IDisposable
{
    public const int SlotCount = 4;

    private readonly RTHandle[] _slots =
    [
        new("_CameraHistoryColor"),
        new("_CameraHistoryDepth"),
        new("_CameraHistoryTaa"),
        new("_CameraHistorySsgi"),
    ];

    public RTHandle Get(CameraHistoryId id)
    {
        int index = (int)id;
        if ((uint)index >= SlotCount)
            throw new ArgumentOutOfRangeException(nameof(id));
        return _slots[index];
    }

    public RenderTexture Alloc(
        CameraHistoryId id,
        int width,
        int height,
        bool hasDepth,
        TextureImageFormat[] formats)
        => Get(id).Alloc(width, height, hasDepth, formats);

    public void Release(CameraHistoryId id) => Get(id).Release();

    public void ReleaseAll()
    {
        for (int i = 0; i < _slots.Length; i++)
            _slots[i].Release();
    }

    public void Dispose()
    {
        for (int i = 0; i < _slots.Length; i++)
            _slots[i].Dispose();
    }
}
