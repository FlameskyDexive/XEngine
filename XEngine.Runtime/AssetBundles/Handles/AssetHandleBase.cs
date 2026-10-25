// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

namespace XEngine.Runtime.AssetBundles;

/// <summary>
/// Base class for the caller-facing handles returned by <see cref="AssetPackage"/>. Each handle holds
/// one reference to its owning provider; <see cref="Release"/> decrements it. Game code must Release
/// every handle it obtains, or the underlying asset will never unload.
/// </summary>
public abstract class AssetHandleBase : IDisposable
{
    private int _released;

    /// <summary>The owning provider (internal - the implementation type is an engine-internal pool
    /// type, but the handle exposes the public surface game code needs).</summary>
    internal AssetProviderBase Provider { get; }

    // Constructor is internal (not protected) so a public handle subclass cannot expose the
    // internal AssetProviderBase type in its own constructor signature. Handle subclasses are all
    // engine-internal; the public surface is the handle itself, created via factory methods.
    internal AssetHandleBase(AssetProviderBase provider)
    {
        Provider = provider ?? throw new ArgumentNullException(nameof(provider));
    }

    /// <summary>True once the provider has finished loading (success or failure).</summary>
    public bool IsDone => Provider.IsDone;

    /// <summary>True if the load succeeded.</summary>
    public bool IsValid => Provider.Status == EOperationStatus.Succeeded;

    /// <summary>The load error, or null.</summary>
    public string? Error => Provider.Error;

    /// <summary>Progress of the underlying load (0..1).</summary>
    public float Progress => Provider.Progress;

    /// <summary>Release the reference. Safe to call multiple times (only the first decrements).</summary>
    public void Release()
    {
        if (System.Threading.Interlocked.Exchange(ref _released, 1) != 0) return;
        Provider.ReleaseHandle(this);
    }

    /// <summary>Block until the load completes.</summary>
    public void WaitForAsyncComplete() => Provider.WaitForCompletion();

    void IDisposable.Dispose() => Release();
}
