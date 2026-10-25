using XEngine.Scribe;

namespace Tests;

public sealed class WeightedCacheTests
{
    [Fact]
    public void LargeWeightsCannotOverflowAndReplacementMaintainsAccounting()
    {
        var cache = new LruCache<int, object>(4) { MaximumWeight = long.MaxValue };
        var a = new object(); var b = new object();
        cache.Add(1, a, long.MaxValue - 1); cache.Add(2, b, 2);
        Assert.Equal(2, cache.Weight); Assert.False(cache.TryGetValue(1, out _));
        cache.Add(2, b, long.MaxValue);
        Assert.Equal(long.MaxValue, cache.Weight); Assert.Equal(1, cache.Count);
        cache.MaximumWeight = 1;
        Assert.Equal(0, cache.Weight); Assert.Equal(0, cache.Count);
    }

    [Fact]
    public void RefreshRequiresSameInstanceAndOversizedReplacementDropsOnlyItsOwnEntry()
    {
        var cache = new LruCache<int, object>(4) { MaximumWeight = 10 };
        var a = new object(); var b = new object();
        cache.Add(1, a, 3); cache.Add(2, b, 3);
        cache.UpdateWeight(1, new object(), 100);
        Assert.Equal(6, cache.Weight);
        cache.UpdateWeight(1, a, 100);
        Assert.False(cache.TryGetValue(1, out _));
        Assert.True(cache.TryGetValue(2, out var result)); Assert.Same(b, result);
        Assert.Equal(3, cache.Weight);
        cache.Clear(); Assert.Equal(0, cache.Weight);
    }
}
