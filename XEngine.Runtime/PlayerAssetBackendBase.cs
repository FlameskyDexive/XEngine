// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using Scene = XEngine.Runtime.Resources.Scene;

namespace XEngine.Runtime;

/// <summary>
/// Common player-facing contract for loose/pak and AssetBundle backends. Platform hosts use this
/// surface to initialize Resources, load the first build scene, and dispose package state without
/// branching on the selected packaging mode.
/// </summary>
public abstract class PlayerAssetBackendBase : AssetBackendBase, IDisposable
{
    public abstract Dictionary<string, Guid> ResourcesMap { get; }

    public abstract Guid DefaultSceneGuid { get; protected set; }

    public abstract Scene? LoadScene(Guid sceneGuid);

    public abstract void Dispose();
}
