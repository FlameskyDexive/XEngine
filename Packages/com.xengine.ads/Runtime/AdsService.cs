// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Threading;

using XEngine.Async;

namespace XEngine.Ads;

/// <summary>
/// Game-facing static facade over the configured <see cref="IAdProvider"/> (registration
/// style mirrors <c>PlatformServices.Configure</c>). Load precedes show; both fail with a
/// result object instead of throwing for expected ad-flow conditions (no fill, nothing
/// loaded). Cancellation still surfaces as an canceled/exceptional task.
/// </summary>
public static class AdsService
{
    private static IAdProvider? _provider;

    /// <summary>The configured provider, or null before <see cref="Configure"/>.</summary>
    public static IAdProvider? Provider => _provider;

    /// <summary>Whether the configured provider finished initialization.</summary>
    public static bool IsInitialized => _provider is { IsInitialized: true };

    /// <summary>
    /// Activates <paramref name="provider"/>. Replaces any previous provider without closing
    /// it — call <see cref="Close"/> first to release vendor resources deterministically.
    /// </summary>
    public static void Configure(IAdProvider provider)
    {
        ArgumentNullException.ThrowIfNull(provider);
        _provider = provider;
    }

    public static XTask Initialize(AdsSettings settings, CancellationToken cancellationToken = default)
    {
        IAdProvider provider = RequireProvider();
        return provider.InitializeAsync(settings, cancellationToken);
    }

    public static XTask<AdLoadResult> Load(AdType type, CancellationToken cancellationToken = default)
    {
        IAdProvider provider = RequireProvider();
        return provider.LoadAsync(type, cancellationToken);
    }

    public static bool IsLoaded(AdType type) => _provider?.IsLoaded(type) == true;

    public static XTask<AdShowResult> Show(AdType type, CancellationToken cancellationToken = default)
    {
        IAdProvider provider = RequireProvider();
        return provider.ShowAsync(type, cancellationToken);
    }

    /// <summary>Releases vendor resources on the configured provider and detaches it.</summary>
    public static void Close()
    {
        _provider?.Close();
        _provider = null;
    }

    private static IAdProvider RequireProvider() =>
        _provider ?? throw new InvalidOperationException("AdsService.Configure must be called before use.");
}
