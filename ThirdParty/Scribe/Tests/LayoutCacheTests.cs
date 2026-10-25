using XEngine.Scribe;

namespace Tests;

public sealed class LayoutCacheTests
{
    [Fact]
    public void LayoutFromAnotherFontSystemIsStaleEvenAtTheSameAtlasVersion()
    {
        var first = new FontSystem(new Renderer());
        var second = new FontSystem(new Renderer());
        var font = new FontFile(FontPath);
        var layout = first.CreateLayout("Cross-atlas", Settings(font));
        second.CreateLayout("Different packing", Settings(font));
        Assert.Equal(first.AtlasVersion, second.AtlasVersion);
        Assert.True(layout.IsStale(second));
        layout.EnsureUpToDate(second);
        Assert.False(layout.IsStale(second));
        Assert.True(layout.IsStale(first));
        layout.EnsureUpToDate(first);
        Assert.False(layout.IsStale(first));
    }

    [Fact]
    public void FallbackSynchronizationPreservesOrderAndStableCallsDoNotInvalidateOrAllocate()
    {
        var source = new FontSystem(new Renderer());
        var target = new FontSystem(new Renderer());
        var first = new FontFile(FontPath); var second = new FontFile(FontPath);
        source.AddFallbackFont(first); source.AddFallbackFont(second);
        target.SynchronizeFallbackFonts(source);
        Assert.Equal(new[] { first, second }, target.FallbackFonts);
        int revision = target.AtlasVersion;
        for (int i = 0; i < 100; i++) target.SynchronizeFallbackFonts(source);
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 1000; i++) target.SynchronizeFallbackFonts(source);
        Assert.Equal(0, GC.GetAllocatedBytesForCurrentThread() - before);
        Assert.Equal(revision, target.AtlasVersion);
        var reversed = new FontSystem(new Renderer());
        reversed.AddFallbackFont(second); reversed.AddFallbackFont(first);
        target.SynchronizeFallbackFonts(reversed);
        Assert.Equal(new[] { second, first }, target.FallbackFonts);
        Assert.True(target.AtlasVersion > revision);
        target.SynchronizeFallbackFonts(new FontSystem(new Renderer()));
        Assert.Empty(target.FallbackFonts);
    }

    [Fact]
    public void ClearingCacheReleasesItsLastStrongReferenceToLayout()
    {
        var system = new FontSystem(new Renderer()) { CacheLayouts = true };
        var weak = CreateWeakCachedLayout(system);
        system.ClearLayoutCache();
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); // Diagnostic ownership check only.
        Assert.False(weak.TryGetTarget(out _));
        GC.KeepAlive(system);
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static WeakReference<TextLayout> CreateWeakCachedLayout(FontSystem system)
        => new(system.CreateLayout("Release cache ownership", Settings(new FontFile(FontPath))));

    [Fact]
    public void EstimatedByteBudgetEvictsLruAndOversizedLayoutsDoNotFlushWorkingSet()
    {
        var system = new FontSystem(new Renderer()) { CacheLayouts = true };
        var settings = Settings(new FontFile(FontPath));
        var a = system.CreateLayout("A", settings);
        var b = system.CreateLayout("B", settings);
        long charge = a.EstimateCacheBytes();
        Assert.Equal(charge, b.EstimateCacheBytes());
        system.MaxLayoutCacheBytes = charge * 2;
        Assert.Same(a, system.CreateLayout("A", settings));
        var c = system.CreateLayout("C", settings); // B is least recently used.
        Assert.Equal(2, system.CachedLayoutCount);
        Assert.Same(a, system.CreateLayout("A", settings));
        Assert.Same(c, system.CreateLayout("C", settings));
        var huge = system.CreateLayout(new string('x', 4096), settings);
        Assert.True(huge.EstimateCacheBytes() > system.MaxLayoutCacheBytes);
        Assert.Equal(2, system.CachedLayoutCount);
        Assert.Same(a, system.CreateLayout("A", settings));
        Assert.Same(c, system.CreateLayout("C", settings));
        Assert.NotSame(b, system.CreateLayout("B", settings));
        Assert.InRange(system.CachedLayoutEstimatedBytes, 0, system.MaxLayoutCacheBytes);
    }

    [Fact]
    public void BudgetShrinkDisableAndClearReleaseCacheOwnershipWithoutInvalidatingCallerLayouts()
    {
        var renderer = new Renderer();
        var system = new FontSystem(renderer) { CacheLayouts = true };
        var settings = Settings(new FontFile(FontPath));
        var retained = system.CreateLayout("Caller-owned label", settings);
        Assert.True(system.CachedLayoutEstimatedBytes > 0);
        system.MaxLayoutCacheBytes = 0;
        Assert.Equal(0, system.CachedLayoutCount); Assert.Equal(0, system.CachedLayoutEstimatedBytes);
        Assert.Equal("Caller-owned label", retained.Text);
        Assert.True(retained.Lines.Count > 0);
        system.MaxLayoutCacheBytes = 1024 * 1024;
        system.CreateLayout("Another label", settings);
        system.CacheLayouts = false;
        Assert.Equal(0, system.CachedLayoutCount); Assert.Equal(0, system.CachedLayoutEstimatedBytes);
        system.CacheLayouts = true; system.CreateLayout("One more", settings);
        system.ClearLayoutCache();
        Assert.Equal(0, system.CachedLayoutCount); Assert.Equal(0, system.CachedLayoutEstimatedBytes);
        Assert.Throws<ArgumentOutOfRangeException>(() => system.MaxLayoutCacheBytes = -1);
    }

    [Fact]
    public void ReservedQuadChargeCoversLazyDrawAndRefreshKeepsBudgetBounded()
    {
        var system = new FontSystem(new Renderer()) { CacheLayouts = true };
        var settings = Settings(new FontFile(FontPath));
        var layout = system.CreateLayout("Glyph quads built lazily", settings);
        long charge = system.CachedLayoutEstimatedBytes;
        system.DrawLayout(layout, XEngine.Vector.Float2.Zero, new FontColor(255, 255, 255, 255));
        Assert.Equal(charge, system.CachedLayoutEstimatedBytes);
        Assert.Equal(layout.EstimateCacheBytes(), system.CachedLayoutEstimatedBytes);
    }

    [Fact]
    public void EmptyLabelsReuseLayoutAndCountersReflectRealCacheWork()
    {
        var system = new FontSystem(new Renderer()) { CacheLayouts = true };
        var settings = TextLayoutSettings.Default;
        var first = system.CreateLayout(string.Empty, settings);
        Assert.Same(first, system.CreateLayout(string.Empty, settings));
        Assert.Equal(1, system.LayoutCreationCount);
        Assert.Equal(1, system.LayoutCacheHitCount);
        Assert.Equal(1, system.CachedLayoutCount);
    }

    [Fact]
    public void RetainedMarkdownRenderingDoesNotRequestNewSelectorLayouts()
    {
        var renderer = new Renderer();
        var system = new FontSystem(renderer) { CacheLayouts = true };
        var settings = MarkdownLayoutSettings.Default(new FontFile(FontPath), 400);
        var display = MarkdownLayoutEngine.Layout(Markdown.Parse("A **bold** label and a *second* span."), system, settings);
        Assert.True(system.LayoutCreationCount > 0);
        long creations = system.LayoutCreationCount, hits = system.LayoutCacheHitCount;
        for (int i = 0; i < 10; i++)
            MarkdownLayoutEngine.Render(display, system, renderer, XEngine.Vector.Float2.Zero, settings);
        Assert.Equal(creations, system.LayoutCreationCount);
        Assert.Equal(hits, system.LayoutCacheHitCount);
    }

    [Fact]
    public void WarmCacheHitsAllocateNoManagedBytes()
    {
        var system = new FontSystem(new Renderer()) { CacheLayouts = true };
        var settings = Settings(new FontFile(FontPath));
        var expected = system.CreateLayout("Cached editor label", settings);
        for (int i = 0; i < 10000; i++) system.CreateLayout("Cached editor label", settings);
        long before = GC.GetAllocatedBytesForCurrentThread();
        TextLayout? result = null;
        for (int i = 0; i < 10000; i++) result = system.CreateLayout("Cached editor label", settings);
        long allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.Equal(0, allocated);
        Assert.Same(expected, result);
    }

    [Fact]
    public void ExplicitSelectorRevisionEnablesReuseAndInvalidatesWhenChanged()
    {
        var system = new FontSystem(new Renderer()) { CacheLayouts = true };
        var settings = Settings(new FontFile(FontPath));
        var font = settings.Font;
        settings.FontSelector = _ => font;
        settings.FontSelectorRevision = 0;
        var first = system.CreateLayout("Hello", settings);
        Assert.Same(first, system.CreateLayout("Hello", settings));
        settings.FontSelectorRevision = 1;
        var changed = system.CreateLayout("Hello", settings);
        Assert.NotSame(first, changed);
        settings.FontSelector = _ => font;
        Assert.NotSame(changed, system.CreateLayout("Hello", settings));
    }

    private sealed class Renderer : IFontRenderer
    {
        public object CreateTexture(int width, int height) => new byte[width * height];
        public void UpdateTextureRegion(object texture, AtlasRect bounds, byte[] data) { }
        public void DrawQuads(object texture, ReadOnlySpan<IFontRenderer.Vertex> vertices, ReadOnlySpan<int> indices) { }
    }
    private sealed class CollidingFont(string path) : FontFile(path)
    { public override int GetHashCode() => 7; }
    private static string FontPath => Path.Combine(AppContext.BaseDirectory, "Geist-Regular.ttf");
    private static TextLayoutSettings Settings(FontFile font)
    { var settings = TextLayoutSettings.Default; settings.Font = font; return settings; }

    [Fact]
    public void SameFontAndSettingsReuseButCollidingFontIdentitiesDoNot()
    {
        var system = new FontSystem(new Renderer()) { CacheLayouts = true };
        var a = Settings(new CollidingFont(FontPath));
        var b = Settings(new CollidingFont(FontPath));
        var first = system.CreateLayout("Hello", a);
        Assert.Same(first, system.CreateLayout("Hello", a));
        Assert.NotSame(first, system.CreateLayout("Hello", b));
    }

    [Fact]
    public void SubpixelWidthAndFontSizeChangesAreDistinctKeys()
    {
        var system = new FontSystem(new Renderer()) { CacheLayouts = true };
        var settings = Settings(new FontFile(FontPath));
        settings.MaxWidth = 100; settings.WrapMode = TextWrapMode.Wrap;
        var initial = system.CreateLayout("Hello world", settings);
        settings.MaxWidth = 100.01f;
        Assert.NotSame(initial, system.CreateLayout("Hello world", settings));
        settings.MaxWidth = 100; settings.PixelSize += .01f;
        Assert.NotSame(initial, system.CreateLayout("Hello world", settings));
    }

    [Fact]
    public void MutableSelectorIsReevaluatedEvenWhenTheDelegateIsUnchanged()
    {
        var system = new FontSystem(new Renderer()) { CacheLayouts = true };
        var settings = Settings(new FontFile(FontPath));
        int calls = 0;
        var font = settings.Font;
        settings.FontSelector = _ => { calls++; return font; };
        var first = system.CreateLayout("Hello", settings);
        int before = calls;
        var second = system.CreateLayout("Hello", settings);
        Assert.NotSame(first, second);
        Assert.True(calls > before);
    }

    [Fact]
    public void CapacityEvictsLeastRecentlyUsedLayout()
    {
        var system = new FontSystem(new Renderer()) { CacheLayouts = true, MaxLayoutCacheSize = 2 };
        var settings = Settings(new FontFile(FontPath));
        var a = system.CreateLayout("A", settings);
        var b = system.CreateLayout("B", settings);
        Assert.Same(a, system.CreateLayout("A", settings));
        system.CreateLayout("C", settings);
        Assert.Same(a, system.CreateLayout("A", settings));
        Assert.NotSame(b, system.CreateLayout("B", settings));
    }
}
