using System.Reflection;

using XEngine.Editor.GUI.Panels;
using XEngine.Editor.GUI.SceneView;
using XEngine.Editor.Inspector;
using XEngine.Runtime;

using Xunit;

namespace XEngine.Editor.Test;

public sealed class EditorIdleHotPathTests : EditorTestHarness
{
    [Fact]
    public void BeginPrefabOverrideDetect_RunsOnceUntilSceneChanges()
    {
        EditorSceneManager.MarkDirty();
        Assert.True(GameObjectInspector.BeginPrefabOverrideDetect());
        Assert.False(GameObjectInspector.BeginPrefabOverrideDetect());
        EditorSceneManager.MarkDirty();
        Assert.True(GameObjectInspector.BeginPrefabOverrideDetect());
        Assert.False(GameObjectInspector.BeginPrefabOverrideDetect());
    }

    [Fact]
    public void FolderIndexVersion_BumpsAfterRebuildThenInvalidate()
    {
        _ = Assets.GetSubFolders("");
        int version = Assets.FolderIndexVersion;
        Assets.InvalidateFolderIndex();
        Assert.Equal(version + 1, Assets.FolderIndexVersion);
    }

    [Fact]
    public void ProjectContentStamp_ChangesWhenFolderIndexInvalidates()
    {
        var panel = new ProjectPanel();
        _ = Assets.GetSubFolders("");
        long a = panel.ComputeContentCacheStamp(Assets);
        Assert.Equal(a, panel.ComputeContentCacheStamp(Assets));
        Assets.InvalidateFolderIndex();
        Assert.NotEqual(a, panel.ComputeContentCacheStamp(Assets));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ConsoleLogCounts_IncrementOnNewWarning_AfterClear(bool startWithFullWarningStore)
    {
        ConsolePanel.EnsureSubscribed();
        if (startWithFullWarningStore)
        {
            int capacity = (int)typeof(ConsolePanel)
                .GetField("MaxMessages", BindingFlags.Static | BindingFlags.NonPublic)!
                .GetRawConstantValue()!;
            for (int i = 0; i < capacity; i++)
                Debug.LogWarning($"editor-idle-hotpath-seed-{i}");
        }

        // Counts describe the bounded current store. At capacity a new warning can evict an old
        // warning, so start this increment contract with the same Clear action used by the UI.
        var panel = new ConsolePanel();
        typeof(ConsolePanel).GetMethod("ClearMessages", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(panel, null);
        var before = ConsolePanel.LogCounts();
        Debug.LogWarning("editor-idle-hotpath-count");
        var after = ConsolePanel.LogCounts();

        // >= rather than ==: a background component (watcher callbacks, audio threads) may log its
        // own warning between the two reads; the invariant under test is that the new warning
        // incremented the counter, not that nothing else logged.
        Assert.True(after.warn >= before.warn + 1, $"warn count did not increment: {before.warn} -> {after.warn}");
    }
}
