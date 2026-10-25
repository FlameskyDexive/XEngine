// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Runtime.Platforms.Browser;

using Xunit;

namespace XEngine.Runtime.Test.Platforms.Browser;

public sealed class MiniGameSettingsStoreTests : IDisposable
{
    public MiniGameSettingsStoreTests() => MiniGameSettingsStore.ResetForTests();

    [Fact]
    public void TrySet_AcceptsSmallSettingsKeysAndQueuesWxWrite()
    {
        Assert.True(MiniGameSettingsStore.TrySet("xengine.settings.volume", "0.5"));
        Assert.Equal("0.5", MiniGameSettingsStore.Get("xengine.settings.volume"));
        Assert.Equal("xengine.settings.volume\n0.5", MiniGameSettingsStore.TakePendingWrite());
        Assert.Null(MiniGameSettingsStore.TakePendingWrite());
    }

    [Fact]
    public void TrySet_RejectsNonSettingsKeysAndOversizedValues()
    {
        Assert.False(MiniGameSettingsStore.TrySet("save.slot0", "nope"));
        Assert.False(MiniGameSettingsStore.TrySet("xengine.settings.blob", new string('a', MiniGameSettingsStore.MaxValueBytes + 1)));
        Assert.Equal(string.Empty, MiniGameSettingsStore.Get("save.slot0"));
        Assert.Null(MiniGameSettingsStore.TakePendingWrite());
    }

    [Fact]
    public void Hydrate_LoadsFromHostStorageWithoutQueuingAWrite()
    {
        MiniGameSettingsStore.Hydrate("xengine.settings.locale", "zh");
        Assert.Equal("zh", MiniGameSettingsStore.Get("xengine.settings.locale"));
        Assert.Null(MiniGameSettingsStore.TakePendingWrite());
    }

    public void Dispose() => MiniGameSettingsStore.ResetForTests();
}
