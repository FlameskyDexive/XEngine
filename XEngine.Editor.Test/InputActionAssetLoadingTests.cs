using System;
using System.Collections.Generic;
using XEngine.InputSystem;
using XEngine.Runtime;
using Xunit;

namespace XEngine.Editor.Test;

public sealed class InputActionAssetLoadingTests
{
    private sealed class MapBackend(InputActionMap map) : AssetBackendBase
    {
        protected override EngineObject? LoadFresh(Guid id)
        {
            SetLoaded(id, map);
            return map;
        }
    }

    [Fact]
    public void FindMap_LoadsColdSubAssetBeforeOneShotPlayerInputBinding()
    {
        var previous = AssetDatabase.Current;
        bool asyncEnabled = AssetLoadingConfig.AsyncEnabled;
        var id = Guid.NewGuid();
        var map = new InputActionMap { Name = "Player", AssetID = id };
        using var asset = new InputActionAsset();
        ((List<AssetRef<InputActionMap>>)asset.Maps).Add(new AssetRef<InputActionMap>(id));
        AssetDatabase.Current = new MapBackend(map);
        AssetLoadingConfig.AsyncEnabled = true;
        try
        {
            Assert.Same(map, asset.FindMap("Player"));
            Assert.Same(map, asset.FindMap("Player"));
        }
        finally
        {
            AssetDatabase.Forget(id);
            map.Dispose();
            AssetDatabase.Current = previous;
            AssetLoadingConfig.AsyncEnabled = asyncEnabled;
        }
    }
}
