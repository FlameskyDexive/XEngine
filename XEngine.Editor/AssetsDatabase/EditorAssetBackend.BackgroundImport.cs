using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;

using XEngine.Echo;
using System.Linq;

using XEngine.Editor.Importers;
using XEngine.Editor.Thumbnails;
using XEngine.Runtime;

namespace XEngine.Editor;

// ═══════════════════════════════════════════════════════════════════════════════════════
//  Background import (Unity-style): the EXPENSIVE pure half of an import — file IO, CPU
//  parse/decode (Magick/FBX/Echo), Library cache serialization — runs on one dedicated worker
//  thread; registration (SetLoaded + user callbacks OnEnable/OnValidate, dependency graph,
//  sub-asset index, thumbnails, entry flags) is applied on the MAIN thread in time-sliced
//  batches. GPU uploads inside importers are safe cross-thread: Graphics.Submit goes through
//  RenderThreadGraphicsDevice's job queue (same guarantee the background AssetLoader relies on).
//
//  Importers opting out declare <see cref="AssetImporter.RequiresMainThread"/> and keep running
//  inline (shader/scripts/asmdef/plugins/scenes). Synchronous entry points (LoadFresh, Reimport,
//  SaveAsset, CreateAsset, watcher single-file) keep the original inline semantics.
// ═══════════════════════════════════════════════════════════════════════════════════════

public partial class EditorAssetBackend
{
    private sealed class ImportJob
    {
        public AssetEntry Entry = null!;
        public string AbsolutePath = null!;
    }

    /// <summary>Everything a completed import needs for main-thread application.</summary>
    private sealed class ImportCoreResult
    {
        public AssetEntry Entry = null!;
        public bool Success;
        public bool SourceOnly;
        public string? Error;          // exception message (logged on apply, like RunImport did)
        public string? ErrorStack;
        public EngineObject? MainAsset;
        public List<EngineObject>? SubAssets;                        // AssetPath pre-set
        public List<SubAssetEntry>? SubAssetEntries;                 // pre-built incl. deps
        public List<(Guid SubGuid, DependencyEdge[] Edges)>? SubEdges;
        public DependencyEdge[]? MainEdges;
        public Guid[]? Dependencies;   // ctx.Dependencies (guid-only list, as the old entry field carried)
        public Type? MainAssetType;
        public Guid[]? PreviousSubGuids;
        public HashSet<Guid>? NewSubGuids;
        public long LastModifiedTicks;
        public int ImporterVersion;
        public string MetaFingerprint = "";
    }

    private readonly BlockingCollection<ImportJob> _importJobs = new(boundedCapacity: 4096);
    private readonly ConcurrentQueue<ImportCoreResult> _completedImports = new();
    /// <summary>GUIDs with an import in flight (enqueued → applied). Main-thread only.</summary>
    private readonly HashSet<Guid> _importsInFlight = new();
    private Thread? _importWorker;
    private long _applyLastReclaimAllocated = GC.GetTotalAllocatedBytes(precise: false);

    public bool HasPendingBackgroundImports =>
        _importJobs.Count > 0 || _importsInFlight.Count > 0 || !_completedImports.IsEmpty;

    /// <summary>
    /// Queue one dirty entry for background import (bulk paths: project open, watcher batches,
    /// forced refresh). Main-only importers and missing files execute inline right here so the
    /// caller-visible semantics of "everything is handled once this returns+flushes" hold.
    /// </summary>
    private void EnqueueBackgroundImport(AssetEntry entry)
    {
        string absolutePath = ResolveAssetAbsolutePath(entry.Path);
        if (!File.Exists(absolutePath))
        {
            entry.NeedsReimport = false;
            return;
        }

        // Classification must instantiate the importer — Activator on the main thread keeps the
        // registry dictionaries comfortably single-threaded.
        var resolved = EditorRegistries.CreateImporterByName(entry.ImporterType) ?? new DefaultImporter();
        if (resolved.RequiresMainThread || !_importsInFlight.Add(entry.Guid))
        {
            // Inline import must carry the same dependent re-import the queued path gets in
            // ApplyCompletedImports (e.g. an .hlsl change must recompile dependent shaders) —
            // the init machine enqueues through here too, and its old inline loop did this.
            RunImport(entry);
            ReenqueueDirectDependents(entry.Guid);
            return;
        }

        EnsureImportWorker();
        try
        {
            _importJobs.Add(new ImportJob { Entry = entry, AbsolutePath = absolutePath });
        }
        catch (InvalidOperationException)
        {
            // Collection marked complete during shutdown — run inline as a fallback.
            _importsInFlight.Remove(entry.Guid);
            RunImport(entry);
        }
    }

    private void EnsureImportWorker()
    {
        if (_importWorker != null && _importWorker.IsAlive)
            return;
        _importWorker = new Thread(ImportWorkerLoop)
        {
            IsBackground = true,
            Name = "XEngine Import Worker",
            Priority = ThreadPriority.BelowNormal,
        };
        _importWorker.Start();
    }

    private void ImportWorkerLoop()
    {
        try
        {
            foreach (var job in _importJobs.GetConsumingEnumerable())
            {
                var result = ImportCore(job.Entry, job.AbsolutePath);
                _completedImports.Enqueue(result);
            }
        }
        catch (ObjectDisposedException)
        {
            // Shutdown race — exit quietly.
        }
    }

    /// <summary>
    /// Drain completed background imports onto main-thread state within a time budget.
    /// Returns the number of successfully applied entries; fires OnAssetsImported per batch.
    /// </summary>
    public int ApplyCompletedImports(float budgetMs)
    {
        var succeeded = new List<string>();
        int appliedCount = 0;
        long start = Stopwatch.GetTimestamp();
        long budgetTicks = Math.Max(1, (long)(budgetMs * Stopwatch.Frequency / 1000));

        while (_completedImports.TryDequeue(out var result))
        {
            bool ok = ApplyImportResult(result);
            if (ok)
            {
                succeeded.Add(result.Entry.Path);
                appliedCount++;
                // Mirror the historical batch loop: imports look "just used" to the idle tracker
                // (SetLoaded touches), so reset + sweep per entry to bound batch peak memory.
                AssetDatabase.ForceIdle(result.Entry.Guid);
                ForceIdleSweep();
            }
            // UNCONDITIONAL (old ImportDirty/InitImportOne semantics): tracker-only importers
            // (DefaultImporter, e.g. bare .hlsl includes) "fail" with no MainAsset by design,
            // yet their dependents (the shader) must still recompile.
            ReenqueueDirectDependents(result.Entry.Guid);
            _importsInFlight.Remove(result.Entry.Guid);
            if (ok && result.Entry.NeedsReimport)
                EnqueueBackgroundImport(result.Entry);

            const long ReclaimAllocationThreshold = 256L * 1024 * 1024;
            long allocatedNow = GC.GetTotalAllocatedBytes(precise: false);
            if (allocatedNow - _applyLastReclaimAllocated >= ReclaimAllocationThreshold)
            {
                _applyLastReclaimAllocated = allocatedNow;
                ReclaimMemory();
            }

            if (Stopwatch.GetTimestamp() - start >= budgetTicks)
                break;
        }

        if (succeeded.Count > 0)
        {
            OnAssetsImported?.Invoke(succeeded.ToArray());
            // Do not force two full blocking collections after each time slice. On a fast
            // importer this can be one asset per slice: 128 small files induced 257 Gen2
            // collections, stopping the import worker between writes. The allocation-budget
            // gate above still bounds reclamation during large model/texture batches.
        }
        return appliedCount;
    }

    /// <summary>Synchronous drain used by forced refreshes and tests: apply until nothing pending.</summary>
    public void FlushBackgroundImports(int timeoutMs)
    {
        long deadline = Stopwatch.GetTimestamp() + timeoutMs * Stopwatch.Frequency / 1000;
        while (HasPendingBackgroundImports)
        {
            ApplyCompletedImports(16f);
            if (Stopwatch.GetTimestamp() >= deadline)
                break;
            if (!_completedImports.IsEmpty)
                continue;
            Thread.Sleep(2);
        }
    }

    /// <summary>
    /// Dependent re-import for the background path: enqueue instead of recursing inline. Dependents
    /// surface through OnAssetsImported when their own result applies — counting them here (enqueue
    /// time) made batch counters and progress numerators double-count past the denominator.
    /// </summary>
    private void ReenqueueDirectDependents(Guid changedGuid)
    {
        IReadOnlySet<Guid> dependentSet = _dependencies.GetDependents(changedGuid);
        var dependents = new Guid[dependentSet.Count];
        int i = 0;
        foreach (Guid g in dependentSet)
            dependents[i++] = g;

        for (int d = 0; d < dependents.Length; d++)
        {
            Guid dependentGuid = dependents[d];
            if (dependentGuid == changedGuid || !_guidToEntry.TryGetValue(dependentGuid, out var entry))
                continue;

            entry.NeedsReimport = true;
            EnqueueBackgroundImport(entry);
        }
    }

    private void ShutdownBackgroundImport()
    {
        try { _importJobs.CompleteAdding(); } catch { }
        _importWorker?.Join(2000);
    }

    // ── ImportCore / ApplyImportResult: the split of RunImport ─────────────────────────

    /// <summary>
    /// Pure, worker-safe half of an import: IO + parse/decode + Library cache writes. Touches no
    /// main-thread-only state; user callbacks never run here (OnEnable/OnValidate fire in
    /// <see cref="ApplyImportResult"/> on the main thread, preserving RunImport's contract).
    /// </summary>
    private ImportCoreResult ImportCore(AssetEntry entry, string absolutePath)
    {
        var result = new ImportCoreResult { Entry = entry };
        try
        {
            // Stale ImporterType recovery (importer registered after the entry was created).
            var resolved = EditorRegistries.CreateImporterByName(entry.ImporterType);
            if (resolved == null)
            {
                string freshName = EditorRegistries.GetImporterTypeNameForFile(absolutePath);
                if (freshName != entry.ImporterType)
                {
                    Runtime.Debug.Log($"[AssetDatabase] '{entry.Path}': updating stale ImporterType '{entry.ImporterType}' -> '{freshName}'");
                    entry.ImporterType = freshName;
                    resolved = EditorRegistries.CreateImporterByName(freshName);
                }
            }

            var importer = resolved ?? new DefaultImporter();
            Runtime.Debug.Log($"[AssetDatabase] Importing '{entry.Path}' via {importer.GetType().Name}");

            EchoObject? settings = null;
            string metaPath = MetaFile.GetMetaPath(absolutePath);
            if (File.Exists(metaPath))
            {
                try
                {
                    MetaFileData readMeta = MetaFile.Read(metaPath);
                    settings = readMeta.Settings;
                    result.MetaFingerprint = ComputeMetaFingerprint(readMeta);
                }
                catch { }
            }

            var defaults = importer.DefaultSettings();
            if (defaults != null)
            {
                if (settings == null)
                {
                    settings = defaults.Clone();
                }
                else
                {
                    foreach (var kvp in defaults.Tags)
                        if (!settings.TryGet(kvp.Key, out _))
                            settings[kvp.Key] = kvp.Value.Clone();
                }
            }

            var ctx = new ImportContext(entry.Guid, absolutePath, settings);
            bool success = importer.Import(ctx);

            if (!success || ctx.Error != null || ctx.MainAsset == null)
            {
                result.Success = false;
                result.SourceOnly = success && ctx.Error == null && ctx.MainAsset == null;
                if (!result.SourceOnly)
                    result.Error = ctx.Error ?? $"{importer.GetType().Name} reported an import failure.";
                return result;
            }

            ctx.MainAsset.AssetPath = entry.Path;
            if (string.IsNullOrEmpty(ctx.MainAsset.Name))
                ctx.MainAsset.Name = Path.GetFileNameWithoutExtension(entry.Path);

            result.MainAsset = ctx.MainAsset;
            result.MainEdges = Enumerable.Select(ctx.DependencyEdges,
                e => new DependencyEdge(e.Guid, e.Role)).ToArray();
            result.Dependencies = ctx.Dependencies.ToArray();
            result.MainAssetType = ctx.MainAsset.GetType();
            result.ImporterVersion = importer.Version;
            result.LastModifiedTicks = File.GetLastWriteTimeUtc(absolutePath).Ticks;

            result.PreviousSubGuids = entry.SubAssets?.Select(s => s.Guid).ToArray() ?? Array.Empty<Guid>();
            result.NewSubGuids = new HashSet<Guid>();

            if (ctx.SubAssets.Count > 0)
            {
                result.SubAssets = new List<EngineObject>(ctx.SubAssets.Count);
                result.SubAssetEntries = new List<SubAssetEntry>(ctx.SubAssets.Count);
                result.SubEdges = new List<(Guid, DependencyEdge[])>(ctx.SubAssets.Count);

                for (int i = 0; i < ctx.SubAssets.Count; i++)
                {
                    var sub = ctx.SubAssets[i];
                    if (sub == null) continue;

                    sub.AssetPath = $"{entry.Path}#{sub.Name}";

                    var subCtx = new DependencySerializationContext();
                    SerializeToCache(sub.AssetID, sub, subCtx);
                    result.SubEdges.Add((sub.AssetID,
                        Enumerable.Select(subCtx.Dependencies,
                            d => new DependencyEdge(d.Guid, d.Role)).ToArray()));

                    result.NewSubGuids.Add(sub.AssetID);
                    result.SubAssets.Add(sub);
                    result.SubAssetEntries.Add(new SubAssetEntry
                    {
                        Guid = sub.AssetID,
                        Name = sub.Name,
                        Type = sub.GetType(),
                        Dependencies = subCtx.GuidsOnly.ToArray()
                    });
                }
            }

            SerializeToCache(entry.Guid, ctx.MainAsset);
            result.Success = true;
            return result;
        }
        catch (Exception ex)
        {
            result.Success = false;
            result.Error = $"{ex.GetType().Name}: {ex.Message}";
            result.ErrorStack = ex.StackTrace;
            return result;
        }
    }

    /// <summary>Main-thread half: registration, user callbacks, graphs, thumbnails, entry flags.
    /// Exact behavioral mirror of the old synchronous RunImport tail.</summary>
    private bool ApplyImportResult(ImportCoreResult result)
    {
        AssetEntry entry = result.Entry;

        if (result.Error != null)
        {
            Runtime.Debug.LogError($"Import failed for '{entry.Path}': {result.Error}\n{result.ErrorStack}");
            entry.LastModifiedTicks = 0; // keep NeedsReimport so changes retry
            return false;
        }

        if (!result.Success || result.MainAsset == null)
        {
            // Source-only inputs have no new runtime output to replace the previous family.
            entry.NeedsReimport = false;
            return false;
        }

        // Importer rejection never reaches this publication boundary. Retire the whole
        // previous family here so every entry point (including bulk background apply) agrees.
        RetireImportedFamily(entry, result);
        SetLoaded(entry.Guid, result.MainAsset);

        // Unity invokes OnValidate for ScriptableObjects while importing — main thread only.
        if (result.MainAsset is ScriptableObject scriptableObject)
        {
            try { scriptableObject.OnValidate(); }
            catch (Exception ex)
            {
                Runtime.Debug.LogError(
                    $"OnValidate threw on ScriptableObject '{scriptableObject.Name}' ({scriptableObject.GetType().Name}): {ex}");
            }
        }

        entry.MainAssetType = result.MainAssetType!;
        entry.Dependencies = result.Dependencies!;

        if (result.SubAssets is { Count: > 0 })
        {
            for (int i = 0; i < result.SubAssets.Count; i++)
            {
                var sub = result.SubAssets[i];
                var (subGuid, subEdges) = result.SubEdges![i];

                _dependencies.SetDependencies(subGuid, subEdges, typed: true);
                // Index BEFORE SetLoaded: ResolveFamily must route this sub's first touch correctly.
                _subAssetIndex[subGuid] = (entry.Guid, i);
                SetLoaded(subGuid, sub);
            }
            entry.SubAssets = result.SubAssetEntries!.ToArray();
        }
        else
        {
            entry.SubAssets = Array.Empty<SubAssetEntry>();
        }

        // Clean up sub-assets that disappeared this import (renamed/removed).
        foreach (var oldGuid in result.PreviousSubGuids!)
        {
            if (result.NewSubGuids!.Contains(oldGuid)) continue;
            RemoveSubAsset(oldGuid, includeThumbnails: true, notify: false);
        }

        entry.LastModifiedTicks = result.LastModifiedTicks;
        entry.ImporterVersion = result.ImporterVersion;
        entry.MetaFingerprint = result.MetaFingerprint;

        // The worker may spend seconds decoding a model/texture while an editor or package
        // process changes its .meta. Do not label the old cache clean with the newer settings;
        // retain the imported fingerprint and request a follow-up import instead.
        bool metaStillMatches = false;
        try
        {
            string absolutePath = ResolveAssetAbsolutePath(entry.Path);
            if (TryReadMetaFingerprint(absolutePath, out string currentMetaFingerprint))
                metaStillMatches = string.Equals(
                    currentMetaFingerprint,
                    result.MetaFingerprint,
                    StringComparison.Ordinal);
        }
        catch
        {
            // An unavailable package mount is conservatively dirty.
        }

        entry.NeedsReimport = !metaStillMatches;

        _dependencies.SetDependencies(entry.Guid, result.MainEdges!, typed: true);

        string? sourceFile = result.MainAsset is Runtime.Resources.Texture2D
            ? ResolveAssetAbsolutePath(entry.Path)
            : null;
        ThumbnailGenerator.Enqueue(entry.Guid, result.MainAsset, sourceFile);
        InvalidateThumbnailTexture(entry.Guid);

        // Every replacement is resident and removed sub-assets are unindexed before any live
        // graph refreshes. Never emit while retiring just part of the previous family.
        var changes = new Dictionary<Guid, EngineObject?> { [entry.Guid] = result.MainAsset };
        foreach (Guid oldGuid in result.PreviousSubGuids!) changes[oldGuid] = null;
        if (result.SubAssets != null)
            for (int i = 0; i < result.SubAssets.Count; i++)
                changes[result.SubAssets[i].AssetID] = result.SubAssets[i];
        NotifyAssetsPublished(changes);

        return true;
    }

    private void RetireImportedFamily(AssetEntry entry, ImportCoreResult result)
    {
        if (!TryGetLoaded(entry.Guid, out var main) || !ReferenceEquals(main, result.MainAsset))
            DisposeAndRemove(entry.Guid, notify: false);
        ThumbnailGenerator.DeleteThumbnail(entry.Guid, _project.ThumbnailsPath);
        InvalidateThumbnailTexture(entry.Guid);

        if (entry.SubAssets == null) return;
        var replacements = new Dictionary<Guid, EngineObject>(result.SubAssets?.Count ?? 0);
        if (result.SubAssets != null)
            for (int i = 0; i < result.SubAssets.Count; i++)
                replacements[result.SubAssets[i].AssetID] = result.SubAssets[i];
        for (int i = 0; i < entry.SubAssets.Length; i++)
        {
            Guid guid = entry.SubAssets[i].Guid;
            bool reused = TryGetLoaded(guid, out var old)
                && replacements.TryGetValue(guid, out var replacement) && ReferenceEquals(old, replacement);
            if (!reused) DisposeAndRemove(guid, notify: false);
            ThumbnailGenerator.DeleteThumbnail(guid, _project.ThumbnailsPath);
            InvalidateThumbnailTexture(guid);
        }
    }
}
