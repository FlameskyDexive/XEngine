// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

namespace XEngine.Runtime.AssetBundles;

/// <summary>
/// How a package resolves its storage at init time. Maps to a combination of <see cref="IAssetStorage"/>
/// instances; the mapping is done by <see cref="AssetPackage"/>'s initialization options.
/// Mirrors YooAsset's EPlayMode (None/EditorSimulate/Offline/Host/Web/Custom).
/// </summary>
public enum AssetPlayMode
{
    /// <summary>Not initialized.</summary>
    None = 0,

    /// <summary>
    /// Editor simulate mode: no bundles are built or read. The EditorStorage talks directly to the
    /// project's asset database (EditorAssetBackend). Only valid in the editor.
    /// </summary>
    EditorSimulate = 1,

    /// <summary>
    /// Offline mode: all bundles ship in the app package (BuiltinStorage). No downloads. Used for
    /// shipped games without live updates.
    /// </summary>
    Offline = 2,

    /// <summary>
    /// Host mode: builtin bundles for the baseline + sandbox bundles for downloaded updates. The
    /// manifest is fetched from a remote version endpoint; missing bundles are downloaded.
    /// </summary>
    Host = 3,

    /// <summary>
    /// Web mode: bundles are served over HTTP (WebStorage). Used for browser/WASM builds where no
    /// app-package bundles exist.
    /// </summary>
    Web = 4,

    /// <summary>Custom: the caller supplies an arbitrary list of storages.</summary>
    Custom = 5,
}
