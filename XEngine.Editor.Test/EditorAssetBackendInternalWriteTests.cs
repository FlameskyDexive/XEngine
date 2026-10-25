using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;

using XEngine.Editor;
using XEngine.Runtime;
using XEngine.Runtime.Resources;

using Xunit;

namespace XEngine.Editor.Test;

/// <summary>Focused coverage for editor-owned writes and persisted .meta invalidation.</summary>
public sealed class EditorAssetBackendInternalWriteTests : EditorTestHarness
{
    [Fact]
    public void CreateAsset_InternalWrite_DoesNotEchoThroughWatcher()
    {
        var imported = new List<string>();
        Assets.OnAssetsImported += paths => imported.AddRange(paths);

        Assets.CreateAsset(new Scene(), "Watcher/Create.scene");
        PumpFileChanges();

        Assert.DoesNotContain("Watcher/Create.scene", imported);
    }

    [Fact]
    public void SaveAsset_InternalWrite_DoesNotEchoThroughWatcher()
    {
        var scene = new Scene();
        Assets.CreateAsset(scene, "Watcher/Save.scene");
        PumpFileChanges();

        var imported = new List<string>();
        Assets.OnAssetsImported += paths => imported.AddRange(paths);

        scene.Add(new GameObject("Changed"));
        Assets.SaveAsset(scene);
        PumpFileChanges();

        Assert.DoesNotContain("Watcher/Save.scene", imported);
    }

    [Fact]
    public void InternalWriteFingerprint_SuppressesDuplicateEvents_AndReleasesOnExternalEdit()
    {
        string path = AssetAbsolutePath("Watcher/Internal.asset");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "abc");

        Assets.RegisterInternalWrite(path);

        // One write can yield Created + multiple Changed notifications. The record must survive
        // the first matching event so every duplicate is suppressed.
        Assert.True(Assets.TrySuppressInternalWrite(path));
        Assert.True(Assets.TrySuppressInternalWrite(path));

        // Keep the length identical: suppression must compare content/mtime, not just the path or
        // file size, so a real external edit cannot be swallowed by the short-lived record.
        DateTime originalWriteTime = File.GetLastWriteTimeUtc(path);
        File.WriteAllText(path, "xyz");
        // Preserve both mtime and length: content fingerprinting must still identify this as an
        // external edit rather than relying on a path/mtime/size-only token.
        File.SetLastWriteTimeUtc(path, originalWriteTime);

        Assert.False(Assets.TrySuppressInternalWrite(path));
        Assert.False(Assets.TrySuppressInternalWrite(path));
    }

    [Fact]
    public void InternalWriteFingerprint_ExpiresWithoutSleeping()
    {
        string path = AssetAbsolutePath("Watcher/Expiring.asset");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllText(path, "abc");

        long nowTicks = DateTime.UtcNow.Ticks;
        Assets.RegisterInternalWrite(path, nowTicks);

        Assert.False(Assets.TrySuppressInternalWrite(
            path,
            nowTicks + TimeSpan.TicksPerMinute * 2 + 1));
    }

    [Fact]
    public void MetaSettingsChange_IsTrackedByAssetEntryFingerprint()
    {
        var scene = new Scene();
        Assets.CreateAsset(scene, "Watcher/Settings.scene");
        Guid guid = scene.AssetID;

        AssetEntry before = Assets.GetEntry(guid)!;
        Assert.NotEmpty(before.MetaFingerprint);

        string metaPath = AssetAbsolutePath("Watcher/Settings.scene.meta");
        MetaFileData meta = MetaFile.Read(metaPath);
        var settings = XEngine.Echo.EchoObject.NewCompound();
        settings["probeSetting"] = new XEngine.Echo.EchoObject(2);
        meta.Settings = settings;
        MetaFile.Write(metaPath, meta);

        // Reopen runs the same scan path used for package remount/startup. The source file is
        // unchanged, so only the persisted .meta fingerprint can make this asset dirty.
        ReopenDatabase();

        AssetEntry after = Assets.GetEntry(guid)!;
        Assert.NotEmpty(after.MetaFingerprint);
        Assert.NotEqual(before.MetaFingerprint, after.MetaFingerprint);
    }

    [Fact]
    public void LegacyEntryWithFreshCache_BaselinesMetaFingerprintWithoutReimport()
    {
        var scene = new Scene();
        Assets.CreateAsset(scene, "Watcher/Legacy.scene");
        Guid guid = scene.AssetID;
        string expectedFingerprint = Assets.GetEntry(guid)!.MetaFingerprint;

        // Simulate a pre-fingerprint metadata row while keeping the cache newer than both source
        // and .meta. Startup may safely establish the baseline without decoding the whole project.
        Assets.GetEntry(guid)!.MetaFingerprint = "";
        MetadataCache.Save(Project.MetadataDbPath, Assets.GetAllEntries());

        var imported = new List<string>();
        ReopenDatabase(db => db.OnAssetsImported += paths => imported.AddRange(paths));

        Assert.Equal(expectedFingerprint, Assets.GetEntry(guid)!.MetaFingerprint);
        Assert.DoesNotContain("Watcher/Legacy.scene", imported);
    }

    private void PumpFileChanges(int milliseconds = 1800)
    {
        var stopwatch = Stopwatch.StartNew();
        while (stopwatch.ElapsedMilliseconds < milliseconds)
        {
            Assets.ProcessFileChanges();
            Thread.Sleep(40);
        }

        Assets.ProcessFileChanges();
    }
}
