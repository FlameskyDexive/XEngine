// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.
//
// The context objects the AssetBundle build tasks read/write. These are the pipeline's data contracts
// (mirrors SBP's IBuildParameters / IBundleBuildContent / IDependencyData / IBundleWriteData /
// IBuildResults family). Each is a plain mutable type the tasks populate as the run progresses.

using System;
using System.Collections.Generic;

using XEngine.Editor.AssetBundles.Builder;
using XEngine.Editor.AssetBundles.Collector;
using XEngine.Runtime.AssetBundles;

using EditorAssetBackend = XEngine.Editor.EditorAssetBackend;

namespace XEngine.Editor.AssetBundles.BuildPipeline;

// ================================================================
//  Input: parameters + content
// ================================================================

/// <summary>Build parameters (input). The caller populates this before running the pipeline.</summary>
public sealed class BundleBuildParameters : IBuildContextObject
{
    public string PackageName = "default";
    public string Version = "1.0.0";
    public AssetBundlePlatform Platform = AssetBundlePlatform.Any;
    public BundleCompressionMode Compression = BundleCompressionMode.Lz4Block;
    public int Lz4BlockSize = BundleCompression.DefaultBlockSize;
    public Func<Guid, byte[], byte[]>? PayloadTransform;
    public bool EnableAddressable = true;
    /// <summary>Output directory. Bundles land in {OutputRoot}/{PackageName}/.</summary>
    public string OutputRoot = "";
    public bool WriteReport = true;
}

/// <summary>What to build (input): the collector configuration + the editor asset database to resolve it against.</summary>
public sealed class BundleBuildContent : IBuildContextObject
{
    /// <summary>The collector package (groups/collectors/rules) describing what to pack.</summary>
    public AssetBundleCollectorPackage Package = new();

    /// <summary>The editor asset database to resolve collector paths against.</summary>
    public EditorAssetBackend? AssetDatabase;

    /// <summary>
    /// Optional pre-collected closure shared by the caller's preflight and the write phase. Keeping
    /// the exact bundle assignment avoids a second dependency walk observing a database import in
    /// flight and producing a different AB closure.
    /// </summary>
    public BundleBuildCollection? PrecollectedCollection;
}

// ================================================================
//  Intermediate: collected assets + bundle assignments + dependency graph
// ================================================================

/// <summary>
/// Per-asset collection result, populated by the Collect task. Maps each asset GUID to its assigned
/// bundle index, addressable address, source asset path, and the bundle's display name.
/// </summary>
public sealed class BundleBuildCollection : IBuildContextObject
{
    /// <summary>
    /// Assets named directly by a collector. Dependencies are intentionally not included here:
    /// build callers use this set to validate required roots (most importantly the startup scene)
    /// without accidentally treating every dependency as an authored collector entry point.
    /// </summary>
    public HashSet<Guid> ExplicitRootAssets = new();

    /// <summary>Ordered list of bundles; index is referenced by <see cref="AssetToBundle"/>.</summary>
    public List<string> BundleNames = new();

    /// <summary>The GUIDs packed into each bundle (parallel to <see cref="BundleNames"/>).</summary>
    public List<List<Guid>> BundleAssets = new();

    /// <summary>Asset GUID -> the bundle index it lands in.</summary>
    public Dictionary<Guid, int> AssetToBundle = new();

    /// <summary>Asset GUID -> its addressable address.</summary>
    public Dictionary<Guid, string> AssetAddress = new();

    /// <summary>Asset GUID -> its original project path (parent#subName for sub-assets, for
    /// diagnostics + manifest AssetPath).</summary>
    public Dictionary<Guid, string> AssetPath = new();

    /// <summary>Asset GUID -> its tags.</summary>
    public Dictionary<Guid, List<string>> AssetTags = new();

    /// <summary>
    /// Duplicate addressable names found during collect. These are reported as errors instead of
    /// throwing so the editor can show a prompt without crashing.
    /// </summary>
    public List<string> AddressErrors = new();
}

/// <summary>
/// Bundle dependency graph, populated by the ComputeDependency task. For each bundle (by index),
/// the set of other bundle indices it directly depends on.
/// </summary>
public sealed class BundleDependencyData : IBuildContextObject
{
    /// <summary>bundle index -> direct dependency bundle indices.</summary>
    public List<HashSet<int>> DirectDependencies = new();
}

// ================================================================
//  Intermediate: written bundle files (their on-disk hashes/CRCs)
// ================================================================

/// <summary>Per-bundle write result, populated by the WriteBundles task. Parallel to BundleNames.</summary>
public sealed class BundleWriteData : IBuildContextObject
{
    /// <summary>bundle index -> metadata of the written bundle file (hash/CRC/size/fileName).</summary>
    public List<BundleInfo> BundleInfos = new();
}

// ================================================================
//  Output: final manifest + summary
// ================================================================

/// <summary>The built package manifest, populated by the WriteManifest task.</summary>
public sealed class BundleBuildResults : IBuildContextObject
{
    public XEngine.Runtime.AssetBundles.PackageManifest? Manifest;
    public Builder.AssetBundleBuildResult Summary = new();
}
