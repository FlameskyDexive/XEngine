// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

// The AssetBundles subsystem provides a Unity-AssetBundle-style packaging pipeline plus a
// YooAsset-style resource management layer (reference-counted handles, manifests, downloaders,
// decryption, and pluggable storage). It coexists with the original LooseFiles / XEnginePak /
// Embedded packaging modes via AssetPackagingMode.AssetBundle.
//
// Layering (bottom -> top):
//   AssetBundle            - single bundle container (echo-blob payloads behind an offset table)
//   PackageManifest        - per-package asset/bundle/dependency metadata
//   IBundleStore           - byte-level storage (builtin / sandbox / editor / web)
//   IAssetStorage          - storage-strategy orchestration (builtin / sandbox / editor / web)
//   BundleLoader           - per-bundle reference-counted loader (Tier B)
//   AssetProvider          - per-logical-load reference-counted provider (Tier A)
//   AssetHandle            - caller-facing handle (explicit Release())
//   AssetManager           - provider/loader pool + refcount-driven unload
//   AssetPackage           - public per-package API (load/version/download)
//   XAssets                - global facade wired into the engine's main loop
//
// Attribution: the architecture and several generic algorithms (LEB128 buffer codec, CRC32,
// the provider/handle refcount model, the manifest version flow) are informed by YooAsset
// (Apache 2.0, Copyright (c) 2018-2021 何冠峰 / TuYoo Games). XEngine's implementation is an
// independent rewrite under MIT; no YooAsset source or type names are reproduced.

using System.Runtime.CompilerServices;

namespace XEngine.Runtime.AssetBundles;

/// <summary>Internal helpers shared across the AssetBundles subsystem.</summary>
internal static class AssetBundlesNamespace
{
    /// <summary>File-scoped marker so the namespace doc lives in source rather than only in XML doc.</summary>
    [MethodImpl(MethodImplOptions.NoInlining)]
    internal static void Load() { }
}
