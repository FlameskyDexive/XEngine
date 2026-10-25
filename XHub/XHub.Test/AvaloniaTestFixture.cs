using System;
using System.Threading;
using Avalonia;
using Avalonia.Headless;
using Xunit;

namespace XHub.Test;

/// <summary>
/// Initializes a headless Avalonia platform once for the test assembly so view-model tests can
/// touch Avalonia types (geometry parsing, brushes) without a display.
/// </summary>
public sealed class AvaloniaTestFixture : IDisposable
{
    private static int _initialized;

    public AvaloniaTestFixture()
    {
        if (Interlocked.CompareExchange(ref _initialized, 1, 0) == 0)
        {
            AppBuilder.Configure<App>()
                .UseHeadless(new AvaloniaHeadlessPlatformOptions())
                .SetupWithoutStarting();
        }
    }

    public void Dispose() { }
}

[CollectionDefinition("Avalonia")]
public sealed class AvaloniaTestCollection : ICollectionFixture<AvaloniaTestFixture>;
