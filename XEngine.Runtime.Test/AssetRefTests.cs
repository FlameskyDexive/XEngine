// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Runtime.Resources;
using XEngine.Runtime.Rendering;

using Xunit;

namespace XEngine.Runtime.Test;

/// <summary>Tests for <see cref="AssetRef{T}"/> identity semantics.</summary>
public class AssetRefTests
{
    private sealed class SingleAssetBackend(EngineObject asset) : AssetBackendBase
    {
        protected override EngineObject? LoadFresh(Guid assetId)
        {
            SetLoaded(assetId, asset);
            return asset;
        }
    }

    // Equality/hash must use the resolved AssetID, so a ref built from an instance whose AssetID is
    // assigned after construction still equals a ref built from that GUID.
    [Fact]
    public void Equality_ConsistentAfterInstanceAssetIdAssigned()
    {
        var mesh = new Mesh();
        var fromInstance = new AssetRef<Mesh>(mesh);

        var id = Guid.NewGuid();
        mesh.AssetID = id; // assigned after the ref was created

        var fromGuid = new AssetRef<Mesh>(id);

        Assert.True(fromInstance == fromGuid, "Refs denoting the same asset must be equal.");
        Assert.Equal(fromInstance.GetHashCode(), fromGuid.GetHashCode());
    }

    [Fact]
    public void PropertyStateSnapshot_TouchesTextureReferencesBeforeIdleSweep()
    {
        AssetBackendBase? previousBackend = AssetDatabase.Current;
        Guid id = Guid.NewGuid();
        var texture = new Texture2D { AssetID = id };
        var backend = new SingleAssetBackend(texture);
        AssetDatabase.Current = backend;

        try
        {
            Assert.Same(texture, AssetDatabase.Get(id));

            var properties = new PropertyState();
            properties.SetTexture("_MainTex", texture);
            AssetDatabase.ForceIdle(id);

            PropertyState snapshot = PropertyStatePool.RentSnapshot(properties);
            try
            {
                backend.ForceIdleSweep();
                Assert.False(texture.IsDisposed);
            }
            finally
            {
                PropertyStatePool.Return(snapshot);
            }
        }
        finally
        {
            if (!texture.IsDisposed)
                texture.Dispose();
            AssetDatabase.Forget(id);
            AssetDatabase.Current = previousBackend;
        }
    }
}
