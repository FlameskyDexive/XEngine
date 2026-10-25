using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;

using XEngine.Echo;
using XEngine.Editor.Thumbnails;
using XEngine.Editor.Importers;
using XEngine.Runtime;
using XEngine.Editor.GUI.Popups;
using XEngine.Editor.Projects.Scripting;
using XEngine.Editor.Projects;
using XEngine.Rosetta;

namespace XEngine.Editor;

/// <summary>
/// Central asset database for the editor. Manages the full asset lifecycle: scanning, importing,
/// caching, file watching.
/// </summary>
public partial class EditorAssetBackend : AssetBackendBase
{
    public static EditorAssetBackend? Instance { get; private set; }

    /// <summary>★ AI tooling — Number of assets currently tracked. Surfaced for runtime_refresh's result.</summary>
    public int AssetCount => _guidToEntry.Count;

    private readonly Project _project;
    // Concurrent so the background AssetLoader thread can read entries / write loaded assets
    // while the main thread imports/scans. (_pathToGuid is main-thread-only the loader never touches it.)
    private readonly ConcurrentDictionary<Guid, AssetEntry> _guidToEntry = new();
    private readonly Dictionary<string, Guid> _pathToGuid = new(StringComparer.OrdinalIgnoreCase);
    private readonly ConcurrentDictionary<Guid, (Guid parentGuid, int index)> _subAssetIndex = new();
    // GPU-uploaded thumbnail cache. Main-thread-only (texture creation isn't thread-safe), same
    // as _pathToGuid every UI that shows asset thumbnails shares this instead of keeping its own.
    private readonly Dictionary<Guid, Runtime.Resources.Texture2D?> _thumbnailTextures = new();
    // Importing (and the file writes / GPU work it implies) stays on the main thread; the
    // background loader only deserializes already-imported on-disk cache files.
    private int _mainThreadId = -1;
    private readonly DependencyGraph _dependencies = new();
    private AssetWatcher? _watcher;
    private FileSystemWatcher? _buildPropsWatcher;
    private FileSystemWatcher? _manifestWatcher;
    private FileSystemWatcher? _packagesRootWatcher;
    private FileSystemWatcher? _editorModulesWatcher;
    private readonly List<FileSystemWatcher> _packageMountWatchers = new();
    private int _packageRootsDirty;

    // FileSystemWatcher reports writes performed by the editor itself. Keep a short-lived,
    // content-aware record for those writes so the watcher cannot replay the synchronous import
    // performed by CreateAsset/SaveAsset. The record intentionally remains after the first match:
    // Windows can emit Created + Changed (or multiple Changed) events for one write. A fingerprint
    // mismatch immediately hands the event back to the normal external-edit path.
    private const int MaxInternalWriteRecords = 4096;
    private const long InternalWriteRecordLifetimeTicks = TimeSpan.TicksPerMinute * 2;

    private readonly record struct FileFingerprint(long Length, long LastWriteUtcTicks, ulong ContentHash);
    private readonly record struct InternalWriteRecord(FileFingerprint Fingerprint, long ExpiresUtcTicks);

    private readonly Dictionary<string, InternalWriteRecord> _internalWriteRecords = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _expiredInternalWritePaths = new();

    // Cached folder/file structure - the single source of truth the Project Panel reads instead of
    // hitting Directory.GetDirectories/GetFiles every frame. Rebuilt from one tree walk whenever the
    // watcher reports a structural change (see ProcessFileChanges), otherwise served from memory.
    // Main-thread-only (the panel and ProcessFileChanges both run there), so no locking is needed.
    private sealed class FolderContents
    {
        public readonly List<FolderRecord> SubFolders = new();
        public readonly List<FileRecord> Files = new();
    }
    private readonly Dictionary<string, FolderContents> _folderIndex = new(StringComparer.OrdinalIgnoreCase);
    private bool _folderIndexDirty = true;

    /// <summary>Bumps when the Project panel's folder/file index is invalidated.</summary>
    public int FolderIndexVersion { get; private set; }

    private void MarkFolderIndexDirty()
    {
        if (!_folderIndexDirty)
            FolderIndexVersion++;
        _folderIndexDirty = true;
    }

    // Events
    public event Action<string[]>? OnAssetsImported;
    public event Action<string[]>? OnAssetsDeleted;
    public event Action<string, string>? OnAssetMoved;

    public EditorAssetBackend(Project project)
    {
        _project = project;
    }

    // ================================================================
    //  Initialization
    // ================================================================

    public void Initialize()
        => Core.StartupTrace.Time("AssetDb.Initialize", () => { while (!InitializeStep(float.PositiveInfinity)) { } });

    // ================================================================
    //  Incremental initialization (frame-sliced project open)
    // ================================================================

    private enum InitPhase { LoadCache, Scan, FinalizeScan, Import, Finalize, Done }

    private readonly struct ScanItem(string root, string path, string relativePath, bool isReadOnlyPackage, bool isFolder)
    {
        public readonly string Root = root;
        public readonly string Path = path;
        public readonly string RelativePath = relativePath;
        public readonly bool IsReadOnlyPackage = isReadOnlyPackage;
        public readonly bool IsFolder = isFolder;
    }

    private InitPhase _initPhase = InitPhase.LoadCache;
    private IEnumerator<ScanItem>? _initScanEnumerator;
    private HashSet<string>? _initFoundPaths;
    private List<KeyValuePair<Guid, AssetEntry>>? _initRemovals;
    private int _initRemovalIndex;
    private List<AssetEntry>? _initDirty;
    private int _initDirtyIndex;
    private int _initApplied;
    private long _initLastReclaimAllocated;
    private int _initScannedCount;

    /// <summary>True while the frame-sliced initialization is mid-flight. Gates the script
    /// recompile poller (a finished background compile must not hot-reload a half-initialized
    /// database) and MCP/status surfaces.</summary>
    public bool IsInitializing => _initPhase != InitPhase.Done;

    /// <summary>
    /// Advances the incremental initialization by up to <paramref name="budgetMs"/> of work and
    /// returns true once the whole initialize finished. The phases mirror the historical
    /// synchronous InitializeCore exactly (cache load → scan → stale removal → dirty import →
    /// save + watchers); slicing keeps the editor frame loop — and its OS message pump — alive
    /// during big imports so the window never freezes. Main thread only, like everything here.
    /// </summary>
    public bool InitializeStep(float budgetMs)
    {
        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        long budgetTicks = Math.Max(1, (long)(budgetMs * System.Diagnostics.Stopwatch.Frequency / 1000));
        while (true)
        {
            bool done = InitializeOneUnit();
            if (done)
                return true;
            if (System.Diagnostics.Stopwatch.GetTimestamp() - start >= budgetTicks)
                return false;
        }
    }

    private bool InitializeOneUnit()
    {
        switch (_initPhase)
        {
            case InitPhase.LoadCache:
                InitLoadCache();
                _initPhase = InitPhase.Scan;
                return false;

            case InitPhase.Scan:
                return InitScanOne();

            case InitPhase.FinalizeScan:
                return InitFinalizeScanOne();

            case InitPhase.Import:
                return InitImportOne();

            case InitPhase.Finalize:
                InitFinalize();
                _initPhase = InitPhase.Done;
                return true;

            default:
                return true;
        }
    }

    private void InitLoadCache()
    {
        _mainThreadId = Thread.CurrentThread.ManagedThreadId;

        Instance = this;
        Runtime.AssetDatabase.Current = this;

        // Independently streamed sub-assets still share residency ownership with their family.
        AssetDatabase.ResolveFamily = guid =>
            _subAssetIndex.TryGetValue(guid, out var info) ? info.parentGuid : guid;

        // Importers (and every other EditorRegistries scanner) must be ready before anything below
        // tries to import a file - idempotent, so this is cheap on every call after the first.
        EditorRegistries.Initialize();

        // Remove any ".meta.tmp" files left behind by a crash/power-loss mid-write before the scan
        // runs, so it doesn't pick them up and import them as real asset files.
        CleanupOrphanedMetaTempFiles();

        // Try loading cached index for fast startup
        var cached = MetadataCache.Load(_project.MetadataDbPath);
        if (cached.Count > 0)
        {
            foreach (var (guid, entry) in cached)
            {
                _guidToEntry[guid] = entry;
                _pathToGuid[entry.Path] = guid;

                // Seed the dependency graph from persisted dependencies. Without this, unchanged
                // assets (not reimported on startup) have no graph edges after a reopen, so
                // GetDependents/GetDependencies return empty until something is reimported.
                if (entry.Dependencies is { Length: > 0 })
                    _dependencies.SetDependencies(guid, entry.Dependencies);

                // Sub-assets have their own dependency-graph entry too (see RunImport) - needs the
                // same seeding or a later DependenciesOnly build silently drops what they reference.
                if (entry.SubAssets is { Length: > 0 })
                {
                    foreach (var sub in entry.SubAssets)
                        if (sub.Dependencies is { Length: > 0 })
                            _dependencies.SetDependencies(sub.Guid, sub.Dependencies);
                }
            }
            Runtime.Debug.Log($"Loaded {cached.Count} entries from metadata cache.");
        }

        // Rebuild sub-asset index
        RebuildSubAssetIndex();

        _initScanEnumerator = BuildScanEnumerator();
    }

    /// <summary>Files then folders of every asset root (Assets/ + each resolved package root), in
    /// the same order the old synchronous ScanAssets walked them.</summary>
    private IEnumerator<ScanItem> BuildScanEnumerator()
    {
        HashSet<string> found = new(StringComparer.OrdinalIgnoreCase);
        _initFoundPaths = found;
        _initScannedCount = 0;

        // 1. Project Assets/.
        var assetsPath = _project.AssetsPath;
        if (Directory.Exists(assetsPath))
            foreach (var item in EnumerateScanRoot(assetsPath, "", isReadOnlyPackage: false))
                yield return item;

        // 2. Each resolved package root. Package assets retain a stable logical Packages/<name>/ path
        // regardless of whether the physical source is embed, local, git, or registry cache.
        var packages = Projects.Packages.PackageManager.PeekCached(_project)
                       ?? Projects.Packages.PackageManager.Resolve(_project);
        foreach (var pkg in packages)
        {
            string assetRoot = pkg.ResolvedRoot;
            if (!Directory.Exists(assetRoot)) continue;
            // Embed and Local packages live inside (or beside) the project and are project-authored:
            // a missing .meta self-heals exactly like Assets/. Registry, Git, and BuiltIn packages
            // resolve to read-only provider content (caches that can be wiped and re-resolved),
            // where writing metas would churn GUIDs on refresh — those keep warn-and-skip.
            bool readOnly = pkg.Source is Projects.Packages.PackageSource.Registry
                or Projects.Packages.PackageSource.Git
                or Projects.Packages.PackageSource.BuiltIn;
            foreach (var item in EnumerateScanRoot(assetRoot, pkg.LogicalRoot + "/", readOnly))
                yield return item;
        }
    }

    private IEnumerable<ScanItem> EnumerateScanRoot(string rootAbsolutePath, string pathPrefix, bool isReadOnlyPackage)
    {
        foreach (var file in Directory.EnumerateFiles(rootAbsolutePath, "*", SearchOption.AllDirectories))
            yield return new ScanItem(rootAbsolutePath, file, NormalizePath(pathPrefix + Path.GetRelativePath(rootAbsolutePath, file)), isReadOnlyPackage, isFolder: false);

        foreach (var dir in Directory.EnumerateDirectories(rootAbsolutePath, "*", SearchOption.AllDirectories))
            yield return new ScanItem(rootAbsolutePath, dir, NormalizePath(pathPrefix + Path.GetRelativePath(rootAbsolutePath, dir)), isReadOnlyPackage, isFolder: true);
    }

    private bool InitScanOne()
    {
        if (_initScanEnumerator == null)
        {
            _initPhase = InitPhase.FinalizeScan;
            return false;
        }

        if (!_initScanEnumerator.MoveNext())
        {
            _initScanEnumerator.Dispose();
            _initScanEnumerator = null;
            _initPhase = InitPhase.FinalizeScan;
            return false;
        }

        ScanItem item = _initScanEnumerator.Current;

        // Skip .meta files, hidden files, and files inside ~/-prefixed or .-prefixed package dirs.
        string fileName = Path.GetFileName(item.Path);
        if (fileName.StartsWith('.'))
            return false;
        if (item.IsFolder && fileName.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
            return false;
        if (!item.IsFolder && fileName.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
            return false;
        if (item.IsReadOnlyPackage
            && Projects.Packages.PackageManager.IsUnderIgnoredPackageDir(item.Path, item.Root))
            return false;

        if (item.IsFolder)
        {
            // Folder GUIDs: Assets/ folders get .meta minted (writable); package folders are
            // read-only so we only index pre-existing .meta files, never create them.
            if (!item.IsReadOnlyPackage)
            {
                MetaFile.EnsureMeta(item.Path, "DefaultImporter");
                return false;
            }
            if (MetaFile.Exists(item.Path))
            {
                _initFoundPaths!.Add(item.RelativePath);
                try { ScanAssetFolder(item.Path, item.RelativePath); }
                catch (Exception ex)
                {
                    Runtime.Debug.LogError($"Failed to scan package folder '{item.RelativePath}': {ex.Message}");
                }
            }
            return false;
        }

        _initFoundPaths!.Add(item.RelativePath);
        try
        {
            ScanAssetFile(item.Root, item.Path, item.RelativePath, item.IsReadOnlyPackage);
        }
        catch (Exception ex)
        {
            Runtime.Debug.LogError($"Failed to scan asset '{item.RelativePath}': {ex.Message}");
        }

        if ((++_initScannedCount & 15) == 0)
            GUI.ProjectOpenProgress.SetDetail(Loc.Get("import.progress.scanning", new { count = _initScannedCount }));
        return false;
    }

    private bool InitFinalizeScanOne()
    {
        // First unit of the phase: collect entries whose files vanished from disk.
        if (_initRemovals == null)
        {
            _initRemovals = new List<KeyValuePair<Guid, AssetEntry>>();
            foreach (var kv in _guidToEntry)
                if (!_initFoundPaths!.Contains(kv.Value.Path))
                    _initRemovals.Add(kv);
            _initRemovalIndex = 0;
            if (_initRemovals.Count == 0)
            {
                Runtime.Debug.Log("Removed 0 stale entries.");
                BeginImportPhase();
                return false;
            }
            return false;
        }

        if (_initRemovalIndex < _initRemovals.Count)
        {
            var (guid, entry) = _initRemovals[_initRemovalIndex++];

            // Dispose main + sub-assets so AssetRefs detect invalidation
            DisposeAndRemove(guid);
            RemoveSubAssets(entry, includeThumbnails: false);

            _pathToGuid.Remove(entry.Path);
            _guidToEntry.TryRemove(guid, out _);
            _dependencies.RemoveAsset(guid);

            // Clean main cache file
            string cachePath = GetCachePath(guid);
            if (File.Exists(cachePath))
                try { File.Delete(cachePath); } catch { }
            return false;
        }

        Runtime.Debug.Log($"Removed {_initRemovals.Count} stale entries.");
        _initRemovals = null;
        BeginImportPhase();
        return false;
    }

    private void BeginImportPhase()
    {
        _initPhase = InitPhase.Import;
        List<AssetEntry> dirty = new();
        foreach (var e in _guidToEntry.Values)
            if (e.NeedsReimport)
                dirty.Add(e);

        // Deterministic import order (path-sorted) matches the historical scan order —
        // dependency importers (shader includes) need their dependencies current when they run.
        dirty.Sort((a, b) => string.CompareOrdinal(a.Path, b.Path));
        _initDirty = dirty;
        _initDirtyIndex = 0;
        _initApplied = 0;
        _initLastReclaimAllocated = GC.GetTotalAllocatedBytes(precise: false);

        if (dirty.Count > 0)
        {
            Runtime.Debug.Log($"Importing {dirty.Count} assets (background)...");
            GUI.ProjectOpenProgress.SetDetail(Loc.Get("import.progress.assets") + " 0/" + dirty.Count);
            // Worker-safe importers go to the background worker; main-only ones (shader/scripts/
            // scenes) already ran inline inside EnqueueBackgroundImport below.
            foreach (var entry in dirty)
            {
                if (entry.NeedsReimport)
                    EnqueueBackgroundImport(entry);
            }
            _initDirtyIndex = dirty.Count;
        }
    }
    private bool InitImportOne()
    {
        var dirty = _initDirty!;
        // Apply completed background imports in a small slice; the frame loop keeps the progress
        // UI alive while the worker chews through the queue.
        ApplyCompletedImports(8f);

        // Progress = distinct dirty entries that finished (NeedsReimport cleared), not a running
        // sum of applied batches: dependent re-enqueues and meta-race re-imports apply the same
        // entry more than once, which pushed the counter past dirty.Count ("1946/861").
        int remaining = 0;
        for (int i = 0; i < dirty.Count; i++)
            if (dirty[i].NeedsReimport)
                remaining++;
        _initApplied = dirty.Count - remaining;

        // Everything was enqueued in BeginImportPhase (main-only ran inline there), so
        // "no queue, no in-flight, no completed results" IS completion. (_initDirtyIndex only
        // counts skip-ahead entries and must gate nothing here — indexing it against dirty.Count
        // spun forever.)
        bool drained = !HasPendingBackgroundImports;
        if (drained)
        {
            EndImportPhase();
            _initPhase = InitPhase.Finalize;
            return false;
        }

        GUI.ProjectOpenProgress.SetDetail(
            Loc.Get("import.progress.assets") + " " + _initApplied + "/" + dirty.Count);
        return false;
    }
    private void EndImportPhase()
    {
        var dirty = _initDirty;
        int total = dirty?.Count ?? 0;
        Runtime.Debug.Log($"Import complete: {_initApplied}/{total} succeeded.");
        _initDirty = null;
    }

    private void InitFinalize()
    {
        // Save updated cache
        MetadataCache.Save(_project.MetadataDbPath, _guidToEntry.Values);

        // Start file watching
        _watcher = new AssetWatcher();
        _watcher.Start(_project.AssetsPath);

        // Watch Directory.Build.props (project root, outside Assets) so adding a NuGet package there
        // triggers a recompile / package restore, the same as editing the Packages UI used to.
        StartBuildPropsWatcher();

        // Watch Packages/manifest.json, packages-lock.json and every resolved package root so adding,
        // removing, or version-bumping a package triggers a PackageManager cache invalidation +
        // recompile, exactly like a changed asmdef would.
        StartPackagesWatcher();

        Runtime.Debug.Log($"Asset database initialized: {_guidToEntry.Count} assets tracked.");

        // Initialize GameResources mapping for editor play mode
        RefreshResourcesMap();
    }

    /// <summary>
    /// Watch the project's Directory.Build.props (which lives at the root, outside Assets/, so the
    /// AssetWatcher never sees it) and request a recompile when it changes. That is what makes an
    /// added NuGet PackageReference take effect without the user manually rebuilding.
    /// </summary>
    private void StartBuildPropsWatcher()
    {
        if (!Directory.Exists(_project.RootPath)) return;

        _buildPropsWatcher = new FileSystemWatcher(_project.RootPath, "Directory.Build.props")
        {
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
        };
        void OnPropsChanged(object? _, FileSystemEventArgs __)
            => ScriptAssemblyManager.RequestRecompile();

        _buildPropsWatcher.Changed += OnPropsChanged;
        _buildPropsWatcher.Created += OnPropsChanged;
        _buildPropsWatcher.Deleted += OnPropsChanged;
        _buildPropsWatcher.Renamed += OnPropsChanged;
        _buildPropsWatcher.EnableRaisingEvents = true;
    }

    /// <summary>
    /// Watch <c>Packages/manifest.json</c>, the <c>Packages/</c> root, and externally-mounted
    /// package roots (all outside Assets/, so the AssetWatcher never sees them). Any change —
    /// adding a package folder, editing the manifest, bumping a version — invalidates the
    /// PackageManager cache and requests a recompile so the new package's asmdefs/plugins/scripts
    /// are picked up. For embed packages the same watcher also catches content edits (the
    /// .cs/.asmdef/asset files inside Packages/&lt;name&gt;/).
    /// The lock file is an output of <c>PackageManager.Resolve</c>, not an input: watching it
    /// immediately dropped the cache on every resolve and forced a UI-thread rehash next frame.
    /// </summary>
    private void StartPackagesWatcher()
    {
        var packagesPath = _project.PackagesPath;
        if (!Directory.Exists(packagesPath)) return;

        // manifest.json watcher — fires on the project's dependency declaration changing.
        _manifestWatcher = new FileSystemWatcher(packagesPath, "manifest.json")
        {
            IncludeSubdirectories = false,
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
        };
        void OnManifestChanged(object? _, FileSystemEventArgs __)
            => MarkPackageRootsDirty();
        _manifestWatcher.Changed += OnManifestChanged;
        _manifestWatcher.Created += OnManifestChanged;
        _manifestWatcher.Deleted += OnManifestChanged;
        _manifestWatcher.Renamed += OnManifestChanged;
        _manifestWatcher.EnableRaisingEvents = true;

        // Packages/ root watcher — embed add/remove/rename, package.json, and script/plugin
        // create/delete remount. In-place .cs edits only request a recompile. Arbitrary .json
        // must not remount (that walked Spine/VFX on the UI thread).
        _packagesRootWatcher = new FileSystemWatcher(packagesPath)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.LastWrite | NotifyFilters.Size,
        };
        void OnPackageContentChanged(object? sender, FileSystemEventArgs e)
            => HandlePackageWatcherEvent(e);
        _packagesRootWatcher.Changed += OnPackageContentChanged;
        _packagesRootWatcher.Created += OnPackageContentChanged;
        _packagesRootWatcher.Deleted += OnPackageContentChanged;
        _packagesRootWatcher.Renamed += OnPackageContentChanged;
        _packagesRootWatcher.EnableRaisingEvents = true;

        StartEditorModulesWatcher();

        ConfigurePackageMountWatchers();
    }

    /// <summary>
    /// Watch the per-user editor module store (launcher Settings installs/uninstalls) so a module
    /// change remounts packages exactly like a project manifest edit: PackageManager cache drop +
    /// recompile. Any structural change under the store cache counts — installs are rare, so the
    /// coarse filter is fine.
    /// </summary>
    private void StartEditorModulesWatcher()
    {
        string cacheRoot = Projects.Packages.EditorModules.EditorModuleStore.CacheRoot;
        try
        {
            Directory.CreateDirectory(cacheRoot);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            return; // unwritable store root: module installs will still invalidate on their own
        }

        _editorModulesWatcher = new FileSystemWatcher(cacheRoot)
        {
            IncludeSubdirectories = true,
            NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName
                | NotifyFilters.LastWrite | NotifyFilters.Size,
        };
        void OnEditorModuleChanged(object? _, FileSystemEventArgs __)
            => MarkPackageRootsDirty();
        _editorModulesWatcher.Changed += OnEditorModuleChanged;
        _editorModulesWatcher.Created += OnEditorModuleChanged;
        _editorModulesWatcher.Deleted += OnEditorModuleChanged;
        _editorModulesWatcher.Renamed += OnEditorModuleChanged;
        _editorModulesWatcher.EnableRaisingEvents = true;
    }

    private void ConfigurePackageMountWatchers()
    {
        for (int i = 0; i < _packageMountWatchers.Count; i++)
            _packageMountWatchers[i].Dispose();
        _packageMountWatchers.Clear();

        string packagesRoot = Path.GetFullPath(_project.PackagesPath)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var watchedRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var packages = Projects.Packages.PackageManager.PeekCached(_project)
                       ?? Projects.Packages.PackageManager.Resolve(_project);
        foreach (var package in packages)
        {
            string root = Path.GetFullPath(package.ResolvedRoot)
                .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
            if (root.Equals(packagesRoot, StringComparison.OrdinalIgnoreCase)
                || root.StartsWith(packagesRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
                || !watchedRoots.Add(root)
                || !Directory.Exists(root))
                continue;

            var watcher = new FileSystemWatcher(root)
            {
                IncludeSubdirectories = true,
                NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName
                    | NotifyFilters.LastWrite | NotifyFilters.Size,
            };
            watcher.Changed += OnExternalPackageRootChanged;
            watcher.Created += OnExternalPackageRootChanged;
            watcher.Deleted += OnExternalPackageRootChanged;
            watcher.Renamed += OnExternalPackageRootChanged;
            watcher.EnableRaisingEvents = true;
            _packageMountWatchers.Add(watcher);
        }
    }

    private void MarkPackageRootsDirty()
    {
        Projects.Packages.PackageManager.Invalidate();
        Interlocked.Exchange(ref _packageRootsDirty, 1);
        ScriptAssemblyManager.RequestRecompile();
    }

    internal enum PackageWatcherAction
    {
        Ignore,
        RecompileOnly,
        Remount,
    }

    internal static PackageWatcherAction ClassifyPackageWatcherEvent(FileSystemEventArgs e)
    {
        if (IsPackageLockOrManifestName(e.Name))
            return PackageWatcherAction.Ignore;

        if (IsStructuralPackageInput(e))
            return PackageWatcherAction.Remount;

        if (IsPackageSourceEdit(e))
            return PackageWatcherAction.RecompileOnly;

        return PackageWatcherAction.Ignore;
    }

    internal static bool IsPackageLockOrManifestName(string? name)
    {
        if (string.IsNullOrEmpty(name)) return false;
        string file = Path.GetFileName(name);
        return file.Equals("manifest.json", StringComparison.OrdinalIgnoreCase)
            || file.Equals(Projects.Packages.PackageLockFile.DefaultFileName, StringComparison.OrdinalIgnoreCase);
    }

    internal static bool IsPackageSourceExtension(string ext)
        => ext.Equals(".cs", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".asmdef", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".dll", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".so", StringComparison.OrdinalIgnoreCase)
            || ext.Equals(".dylib", StringComparison.OrdinalIgnoreCase);

    internal static bool IsPackageSourceEdit(FileSystemEventArgs e)
        => e.ChangeType == WatcherChangeTypes.Changed
            && IsPackageSourceExtension(Path.GetExtension(e.Name ?? string.Empty));

    /// <summary>
    /// Package add/remove/rename, <c>package.json</c>, or create/delete of scripts/plugins.
    /// Nested folders (e.g. <c>Runtime/</c>) and arbitrary <c>.json</c> edits are not remounts —
    /// those used to Invalidate + ScanAssets every built-in package (Spine/VFX) on the UI thread.
    /// </summary>
    internal static bool IsStructuralPackageInput(FileSystemEventArgs e)
    {
        if (IsPackageLockOrManifestName(e.Name))
            return false;

        string name = e.Name ?? string.Empty;
        string file = Path.GetFileName(name);
        if (file.Equals("package.json", StringComparison.OrdinalIgnoreCase))
            return true;

        if (e.ChangeType is not (WatcherChangeTypes.Created or WatcherChangeTypes.Deleted or WatcherChangeTypes.Renamed))
            return false;

        if (IsPackageSourceExtension(Path.GetExtension(file)))
            return true;

        // Top-level Packages/<name> only. Dotted ids like com.foo look like a file extension;
        // nested Runtime/ Editor/ creates must not remount the whole tree.
        return IsTopLevelPackagesEntry(name);
    }

    internal static bool IsTopLevelPackagesEntry(string? name)
        => !string.IsNullOrEmpty(name) && name.IndexOfAny(['/', '\\']) < 0;

    internal static bool IsRelevantPackageContentChange(FileSystemEventArgs e)
        => ClassifyPackageWatcherEvent(e) != PackageWatcherAction.Ignore;

    private void HandlePackageWatcherEvent(FileSystemEventArgs e)
    {
        switch (ClassifyPackageWatcherEvent(e))
        {
            case PackageWatcherAction.Remount:
                MarkPackageRootsDirty();
                break;
            case PackageWatcherAction.RecompileOnly:
                ScriptAssemblyManager.RequestRecompile();
                break;
        }
    }

    private void OnExternalPackageRootChanged(object? _, FileSystemEventArgs e)
        => HandlePackageWatcherEvent(e);

    /// <summary>
    /// Delete leftover ".meta.tmp" files from a previous session's write that never got
    /// renamed into place (e.g. the editor crashed mid-write). These are always incomplete
    /// data never valid on their own so ScanAssets must not pick them up as real asset files.
    /// </summary>
    private void CleanupOrphanedMetaTempFiles()
    {
        var assetsPath = _project.AssetsPath;
        if (!Directory.Exists(assetsPath)) return;

        foreach (var tmp in Directory.EnumerateFiles(assetsPath, "*.meta.tmp", SearchOption.AllDirectories))
        {
            try { File.Delete(tmp); }
            catch (Exception ex) { Runtime.Debug.LogWarning($"Failed to delete orphaned meta temp file '{tmp}': {ex.Message}"); }
        }
    }

    /// <summary>Scan all assets under Resources/ folders and update GameResources mapping.</summary>
    public void RefreshResourcesMap()
    {
        var map = new Dictionary<string, Guid>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in _guidToEntry.Values)
        {
            string path = entry.Path.Replace('\\', '/');

            // Load path = everything after the last "Resources/" segment (or a leading "Resources/").
            string? afterResources = null;
            int idx = path.LastIndexOf("/Resources/", StringComparison.OrdinalIgnoreCase);
            if (idx >= 0)
                afterResources = path[(idx + "/Resources/".Length)..];
            else if (path.StartsWith("Resources/", StringComparison.OrdinalIgnoreCase))
                afterResources = path["Resources/".Length..];

            if (afterResources == null) continue;

            int dotIdx = afterResources.LastIndexOf('.');
            if (dotIdx >= 0) afterResources = afterResources[..dotIdx];
            if (string.IsNullOrEmpty(afterResources)) continue;

            if (map.ContainsKey(afterResources))
                Runtime.Debug.LogWarning($"[Resources] Duplicate load path '{afterResources}': '{entry.Path}' overrides another asset.");
            map[afterResources] = entry.Guid;
        }
        Runtime.GameResources.Initialize(map);
    }

    // ================================================================
    //  Asset Loading
    // ================================================================

    /// <summary>Drop old edit-scene resource cache ownership before switching to a separate
    /// bundle backend. Pins for other scenes/previews survive, and direct references remain valid.
    /// Do not Dispose: an inspector may still use these objects. Unreferenced Mesh/Texture GPU
    /// handles are released by their render-thread-safe finalizers. Must run after Scene.Unload,
    /// before Activate, while game-backend GUID pins cannot protect duplicate editor instances.</summary>
    internal int ReleaseUnpinnedSceneCache()
    {
        int released = 0;
        lock (_loadLock)
        {
            foreach (var entry in _loadedAssets)
            {
                if (entry.Value is not (AnimationClip or Runtime.Resources.Texture2D or Runtime.Resources.Mesh or Runtime.Resources.Material)
                    || AssetDatabase.IsLocked(entry.Key)) continue;
                if (_loadedAssets.TryRemove(entry.Key, out _)) released++;
            }
        }
        return released;
    }

    /// <summary>Prepare imported animation dependencies while editing, not inside Animator.OnEnable.
    /// Uses the persisted dependency graph (no synchronous reads/imports). Scene ownership keeps
    /// inactive attack/skill clips resident even after the editor has been idle for a minute.</summary>
    public void PrepareSceneAnimationAssets(Runtime.Resources.Scene scene)
    {
        var roots = new HashSet<Guid>();
        foreach (var go in scene.AllObjects)
        {
            var animator = go.GetComponent<Animator>();
            if (animator == null) continue;
            if (animator.Controller.AssetID != Guid.Empty) roots.Add(animator.Controller.AssetID);
            if (animator.Clip.AssetID != Guid.Empty) roots.Add(animator.Clip.AssetID);
        }
        foreach (Guid id in _dependencies.GetTransitiveDependencies(roots))
        {
            AssetDatabase.LockToScene(id, scene);
            AssetLoader.Request(id);
        }
    }

    protected override EngineObject? LoadFresh(Guid assetId)
    {
        // Importing writes files / creates GPU resources and mutates the index, so it must run
        // on the main thread. The background loader only deserializes existing cache files; if a
        // (re)import is required it bails and leaves it to the main-thread import flow.
        bool onMainThread = Thread.CurrentThread.ManagedThreadId == _mainThreadId;

        // Check if this is a sub-asset
        if (_subAssetIndex.TryGetValue(assetId, out var subInfo))
        {
            var parentEntry = GetEntry(subInfo.parentGuid);
            if (parentEntry == null || (uint)subInfo.index >= (uint)parentEntry.SubAssets.Length)
                return null;
            // A sub-asset has its own typed cache. Loading a clip must not deserialize
            // the FBX hierarchy, meshes and every sibling animation first.
            var sub = parentEntry.SubAssets[subInfo.index];
            if (onMainThread && CacheImporterIsStale(parentEntry))
            {
                RunImport(parentEntry);
                return TryGetLoaded(assetId, out var importedSub) ? importedSub : null;
            }
            return LoadSubAssetFromCache(parentEntry, sub, onMainThread);
        }

        // Try loading main asset from disk cache
        string cachePath = GetCachePath(assetId);
        var entry = GetEntry(assetId);

        // Validate cache check importer version matches current importer
        if (onMainThread && entry != null && !string.IsNullOrEmpty(entry.ImporterType))
        {
            var importer = EditorRegistries.CreateImporterByName(entry.ImporterType);
            if (importer != null && importer.Version != entry.ImporterVersion)
            {
                // Cache is stale importer was updated since last import
                Runtime.Debug.Log($"Cache stale for '{entry.Path}': importer v{entry.ImporterVersion} -> v{importer.Version}. Reimporting.");
                entry.NeedsReimport = true;
                RunImport(entry);
                return TryGetLoaded(assetId, out var reimported) ? reimported : null;
            }
        }

        if (File.Exists(cachePath))
        {
            try
            {
                var echo = EchoObject.ReadFromBinary(new FileInfo(cachePath));

                var targetType = entry?.MainAssetType ?? typeof(EngineObject);

                var obj = Serializer.Deserialize(echo, targetType) as EngineObject;
                if (obj != null)
                {
                    obj.AssetID = assetId;
                    if (entry != null) obj.AssetPath = entry.Path;
                    // Legacy Mesh caches omitted Name. Recover it without reimporting or
                    // rewriting the cache; explicitly serialized names (including empty) win.
                    if (obj is Runtime.Resources.Mesh && entry != null && !echo.TryGet("Name", out _))
                        obj.Name = Path.GetFileNameWithoutExtension(entry.Path);
                    SetLoaded(assetId, obj);
                    if (onMainThread) EnqueueThumbnailIfMissing(assetId, obj);

                    return obj;
                }
            }
            catch (Exception ex)
            {
                Runtime.Debug.LogWarning($"Failed to load cached asset {assetId}: {ex.Message}");
            }
        }

        // Cache miss try importing on demand (main thread only the loader leaves importing
        // to the main-thread flow and simply reports the asset as not-yet-available).
        if (onMainThread && _guidToEntry.TryGetValue(assetId, out var e) && !string.IsNullOrEmpty(e.Path))
        {
            RunImport(e);
            return TryGetLoaded(assetId, out var justImported) ? justImported : null;
        }

        return null;
    }

    private static bool CacheImporterIsStale(AssetEntry entry)
    {
        if (string.IsNullOrEmpty(entry.ImporterType)) return false;
        var importer = EditorRegistries.CreateImporterByName(entry.ImporterType);
        return importer != null && importer.Version != entry.ImporterVersion;
    }

    private EngineObject? LoadSubAssetFromCache(AssetEntry parentEntry, SubAssetEntry sub, bool onMainThread)
    {
        string subCachePath = GetCachePath(sub.Guid);
        if (!File.Exists(subCachePath))
        {
            // Import is main-thread work; never perform it on the streaming worker.
            if (onMainThread)
            {
                RunImport(parentEntry);
                return TryGetLoaded(sub.Guid, out var imported) ? imported : null;
            }
            Runtime.Debug.LogWarning($"Sub-asset cache missing for '{sub.Name}' in '{parentEntry.Path}'. Reimport the parent asset.");
            return null;
        }
        try
        {
            long loadStart = System.Diagnostics.Stopwatch.GetTimestamp();
            var echo = EchoObject.ReadFromBinary(new FileInfo(subCachePath));
            double readMs = System.Diagnostics.Stopwatch.GetElapsedTime(loadStart).TotalMilliseconds;
            var obj = Serializer.Deserialize(echo, sub.Type ?? typeof(EngineObject)) as EngineObject;
            double totalMs = System.Diagnostics.Stopwatch.GetElapsedTime(loadStart).TotalMilliseconds;
            if (totalMs >= 100)
                Runtime.Debug.Log($"[AssetLoad] {parentEntry.Path}#{sub.Name}: read={readMs:F1}ms deserialize={totalMs - readMs:F1}ms bytes={new FileInfo(subCachePath).Length}");
            if (obj != null)
            {
                obj.AssetID = sub.Guid;
                obj.AssetPath = $"{parentEntry.Path}#{sub.Name}";
                if (obj is Runtime.Resources.Mesh && !echo.TryGet("Name", out _)) obj.Name = sub.Name;
                SetLoaded(sub.Guid, obj);
                if (onMainThread) EnqueueThumbnailIfMissing(sub.Guid, obj);
                return obj;
            }
            Runtime.Debug.LogWarning($"Failed to deserialize sub-asset '{sub.Name}' from '{parentEntry.Path}' (type: {sub.TypeName}).");
        }
        catch (Exception ex)
        {
            Runtime.Debug.LogWarning($"Error loading sub-asset '{sub.Name}' from '{parentEntry.Path}': {ex.Message}");
        }
        return null;
    }

    /// <summary>Lazily backfill a thumbnail the first time an asset is actually loaded (import/reimport
    /// already enqueue directly - this only catches the gap where a cache-file deserialize resolves an
    /// asset whose thumbnail is missing, e.g. a fresh checkout where thumbnails aren't checked into
    /// source control, or a manually deleted thumbnail file). Main-thread only: ThumbnailGenerator's
    /// queue isn't thread-safe, and this runs from the same disk-deserialize path the background
    /// loader also uses.</summary>
    private void EnqueueThumbnailIfMissing(Guid guid, EngineObject obj)
    {
        if (File.Exists(ThumbnailGenerator.GetThumbnailPath(guid, _project.ThumbnailsPath)))
            return;

        string? sourceFile = obj is Runtime.Resources.Texture2D && !_subAssetIndex.ContainsKey(guid)
            ? ResolveAssetAbsolutePath(obj.AssetPath)
            : null;
        ThumbnailGenerator.Enqueue(guid, obj, sourceFile);
    }

    // ================================================================
    //  Scanning
    // ================================================================

    private void ScanAssets()
    {
        var assetsPath = _project.AssetsPath;
        if (!Directory.Exists(assetsPath)) return;

        // Track which paths we find on disk (across Assets/ and every resolved package root)
        var foundPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        // 1. Project Assets/.
        ScanAssetRoot(assetsPath, "", foundPaths);

        // 2. Each resolved package root. Package assets retain a stable logical Packages/<name>/ path
        // regardless of whether the physical source is embed, local, git, or registry cache.
        var packages = Projects.Packages.PackageManager.PeekCached(_project)
                       ?? Projects.Packages.PackageManager.Resolve(_project);
        foreach (var pkg in packages)
        {
            string assetRoot = pkg.ResolvedRoot;
            if (!Directory.Exists(assetRoot)) continue;
            ScanAssetRoot(assetRoot, pkg.LogicalRoot + "/", foundPaths);
        }

        // Remove entries for files that no longer exist
        var toRemove = _guidToEntry.Where(kv => !foundPaths.Contains(kv.Value.Path))
            .Select(kv => kv.Key).ToList();

        foreach (var guid in toRemove)
        {
            var entry = _guidToEntry[guid];

            // Dispose main + sub-assets so AssetRefs detect invalidation
            DisposeAndRemove(guid);
            RemoveSubAssets(entry, includeThumbnails: false);

            _pathToGuid.Remove(entry.Path);
            _guidToEntry.TryRemove(guid, out _);
            _dependencies.RemoveAsset(guid);

            // Clean main cache file
            string cachePath = GetCachePath(guid);
            if (File.Exists(cachePath))
                try { File.Delete(cachePath); } catch { }
        }

        if (toRemove.Count > 0)
            Runtime.Debug.Log($"Removed {toRemove.Count} stale entries.");
    }

    /// <summary>Scan one asset root (Assets/ or a package root), reconciling every file against the
    /// index. <paramref name="pathPrefix"/> is "" for Assets/ or "Packages/&lt;name&gt;/" for a package,
    /// and is prepended to the stored relative path so the resolver routes it back correctly.</summary>
    private void ScanAssetRoot(string rootAbsolutePath, string pathPrefix, HashSet<string> foundPaths)
    {
        if (!Directory.Exists(rootAbsolutePath)) return;

        foreach (var file in Directory.EnumerateFiles(rootAbsolutePath, "*", SearchOption.AllDirectories))
        {
            // Skip .meta files, hidden files, and files inside ~/-prefixed or .-prefixed package dirs.
            if (file.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) continue;
            string fileName = Path.GetFileName(file);
            if (fileName.StartsWith('.')) continue;
            if (pathPrefix.Length > 0
                && Projects.Packages.PackageManager.IsUnderIgnoredPackageDir(file, rootAbsolutePath))
                continue;

            string relativePath = NormalizePath(pathPrefix + Path.GetRelativePath(rootAbsolutePath, file));
            foundPaths.Add(relativePath);

            try
            {
                ScanAssetFile(rootAbsolutePath, file, relativePath, isReadOnlyPackage: pathPrefix.Length > 0);
            }
            catch (Exception ex)
            {
                Runtime.Debug.LogError($"Failed to scan asset '{relativePath}': {ex.Message}");
            }
        }

        // Folder GUIDs: Assets/ folders get .meta minted (writable); package folders are read-only so
        // we only index pre-existing .meta files, never create them.
        foreach (var dir in Directory.EnumerateDirectories(rootAbsolutePath, "*", SearchOption.AllDirectories))
        {
            string dirName = Path.GetFileName(dir);
            if (dirName.StartsWith('.')) continue;
            if (pathPrefix.Length > 0
                && Projects.Packages.PackageManager.IsUnderIgnoredPackageDir(dir, rootAbsolutePath))
                continue;

            if (pathPrefix.Length == 0)
            {
                // Project Assets/ — ensure folder .meta exists (writable).
                MetaFile.EnsureMeta(dir, "DefaultImporter");
            }
            else
            {
                // Package — only index if a .meta already exists; never write into a package.
                if (MetaFile.Exists(dir))
                {
                    string relativePath = NormalizePath(pathPrefix + Path.GetRelativePath(rootAbsolutePath, dir));
                    foundPaths.Add(relativePath);
                    try { ScanAssetFolder(dir, relativePath); }
                    catch (Exception ex)
                    {
                        Runtime.Debug.LogError($"Failed to scan package folder '{relativePath}': {ex.Message}");
                    }
                }
            }
        }
    }

    /// <summary>Register a single folder asset (by its pre-existing .meta) without writing anything
    /// to disk. Used for package folders, which are read-only.</summary>
    private void ScanAssetFolder(string absoluteDir, string relativePath)
    {
        var meta = MetaFile.Read(MetaFile.GetMetaPath(absoluteDir));
        if (_pathToGuid.TryGetValue(relativePath, out _)) return; // already tracked
        if (_guidToEntry.ContainsKey(meta.Guid)) return;          // GUID already in use by another asset
        _guidToEntry[meta.Guid] = new AssetEntry
        {
            Guid = meta.Guid,
            Path = relativePath,
            ImporterType = "DefaultImporter",
            ImporterVersion = meta.ImporterVersion,
            MetaFingerprint = ComputeMetaFingerprint(meta),
            NeedsReimport = true,
        };
        _pathToGuid[relativePath] = meta.Guid;
    }

    /// <summary>Package-borne doc/config payloads that are not engine assets and ship without
    /// .meta on purpose: markdown docs, license/readme/changelog, package.json itself.</summary>
    private static bool IsPackageDocumentationFile(string file)
    {
        string name = Path.GetFileName(file);
        if (Path.GetExtension(name).ToLowerInvariant() == ".md") return true;
        return name.Equals("package.json", StringComparison.OrdinalIgnoreCase)
            || name.Equals("license", StringComparison.OrdinalIgnoreCase)
            || name.Equals("license.txt", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Reconcile a single asset file against the index: register new assets, flag changed ones
    /// for reimport, and handle GUID collisions/regeneration. Isolated per-file so ScanAssets can skip a
    /// broken file without losing the rest of the scan. <paramref name="isReadOnlyPackage"/> marks
    /// package-provided files, which never get a .meta written or rewritten (they are owned by the
    /// package author); a missing .meta on a package asset is a warning, not auto-created.</summary>
    private void ScanAssetFile(string assetsPath, string file, string relativePath, bool isReadOnlyPackage = false)
    {
        // Determine importer
        string importerName = EditorRegistries.GetImporterTypeNameForFile(file);

        // Get default settings from the importer (for new meta files)
        var importer = EditorRegistries.CreateImporterByName(importerName);
        var defaultSettings = importer?.DefaultSettings();

        // Ensure .meta exists. For read-only package assets, only read an existing .meta — never
        // create or rewrite one (the package owns its .meta files). A missing .meta is a warning.
        // Documentation payloads ship inside packages without .meta by design (Unity skips
        // Documentation~ entirely). Skipping them silently keeps the scan quiet and the import
        // queue short; they are never engine assets.
        if (isReadOnlyPackage && IsPackageDocumentationFile(file))
            return;

        MetaFileData meta;
        if (isReadOnlyPackage)
        {
            var metaPath = MetaFile.GetMetaPath(file);
            if (!File.Exists(metaPath))
            {
                Runtime.Debug.LogWarning($"[Packages] Asset '{relativePath}' has no .meta — package assets must ship with one. Skipping.");
                return;
            }
            try { meta = MetaFile.Read(metaPath); }
            catch (Exception ex)
            {
                Runtime.Debug.LogWarning($"[Packages] Asset '{relativePath}' has an unreadable .meta ({ex.Message}). Skipping.");
                return;
            }
            if (meta.Guid == Guid.Empty)
            {
                Runtime.Debug.LogWarning($"[Packages] Asset '{relativePath}' .meta has no valid GUID. Skipping.");
                return;
            }
        }
        else
        {
            meta = MetaFile.EnsureMeta(file, importerName, importer?.Version ?? 1, defaultSettings);
        }

        // Package import settings live in the companion .meta, so source-file mtime alone cannot
        // invalidate a cached import when (for example) alphaSource changes. Persist a compact
        // semantic fingerprint with the entry and compare it on every scan.
        string metaFingerprint = ComputeMetaFingerprint(meta);

        if (_pathToGuid.TryGetValue(relativePath, out var existingGuid))
        {
            // Already tracked check if needs reimport
            var entry = _guidToEntry[existingGuid];
            long currentTicks = File.GetLastWriteTimeUtc(file).Ticks;

            // Importer selection changed (importer added/removed, or a registry initialized before
            // package Editor assemblies were available stamped DefaultImporter): re-dispatch through
            // the new importer so the entry self-heals on the next scan instead of staying poisoned.
            if (!string.Equals(entry.ImporterType, importerName, StringComparison.Ordinal))
            {
                Runtime.Debug.Log($"Importer for '{relativePath}' changed: {entry.ImporterType} -> {importerName}. Reimporting.");
                entry.ImporterType = importerName;
                entry.ImporterVersion = importer?.Version ?? 1;
                entry.NeedsReimport = true;
            }

            // Reimport if the file or its .meta changed, its cache is missing, OR the importer's
            // version was bumped (a new editor build with changed import logic must re-run stale
            // caches). A legacy entry has no meta fingerprint; baseline it from a cache that is
            // newer than both source and .meta so introducing this field does not force a project-
            // wide reimport on the first launch after the upgrade.
            bool metaFingerprintMatches = string.Equals(entry.MetaFingerprint, metaFingerprint, StringComparison.Ordinal);
            if (!metaFingerprintMatches
                && CanBaselineLegacyMetaFingerprint(entry, existingGuid, file, currentTicks, MetaFile.GetMetaPath(file)))
            {
                entry.MetaFingerprint = metaFingerprint;
                metaFingerprintMatches = true;
            }

            if (!metaFingerprintMatches
                || entry.LastModifiedTicks != currentTicks
                || !File.Exists(GetCachePath(existingGuid))
                || (importer != null && entry.ImporterVersion != importer.Version))
                entry.NeedsReimport = true;

            // Update GUID if meta was regenerated with different GUID
            if (meta.Guid != existingGuid)
            {
                // Every existing reference to this asset (and any of its sub-assets) by the old
                // GUID now silently resolves to null - warn so a lost/corrupted .meta doesn't
                // look like an unrelated mystery bug later.
                Runtime.Debug.LogWarning(
                    $"Asset '{relativePath}' has a new GUID ({existingGuid} -> {meta.Guid}), " +
                    "likely from a regenerated .meta file. Any existing references to the old " +
                    "GUID (including its sub-assets, if any) are now broken.");

                // Clean up old GUID references
                _guidToEntry.TryRemove(existingGuid, out _);
                _pathToGuid.Remove(relativePath);
                DisposeAndRemove(existingGuid);
                _dependencies.RemoveAsset(existingGuid);

                // Remove old sub-asset index entries
                if (entry.SubAssets != null)
                {
                    foreach (var sub in entry.SubAssets)
                    {
                        _subAssetIndex.TryRemove(sub.Guid, out _);
                        DisposeAndRemove(sub.Guid);
                        _dependencies.RemoveAsset(sub.Guid);
                    }
                }

                // Delete old cache file
                string oldCachePath = GetCachePath(existingGuid);
                if (File.Exists(oldCachePath))
                    try { File.Delete(oldCachePath); } catch { }

                entry.Guid = meta.Guid;
                entry.SubAssets = Array.Empty<SubAssetEntry>();
                _guidToEntry[meta.Guid] = entry;
                _pathToGuid[relativePath] = meta.Guid;
                entry.NeedsReimport = true;
            }
        }
        else if (_guidToEntry.ContainsKey(meta.Guid))
        {
            var entry = _guidToEntry[meta.Guid];
            string oldPath = entry.Path;
            // oldPath may itself be a Packages/ path; resolve it through the same chokepoint.
            bool originalStillExists = File.Exists(ResolveAssetAbsolutePath(oldPath))
                && !oldPath.Equals(relativePath, StringComparison.OrdinalIgnoreCase);

            if (originalStillExists)
            {
                // Two files share a GUID (the asset + its .meta were copied). A GUID must be unique,
                // so mint a fresh one for the copy and rewrite its .meta — but only for writable
                // (non-package) assets. A package asset that collides is skipped with a warning so
                // we never mutate the package.
                if (isReadOnlyPackage)
                {
                    Runtime.Debug.LogWarning($"[Packages] Asset '{relativePath}' GUID {meta.Guid} collides with '{oldPath}'. Skipping the package copy.");
                    return;
                }
                var newGuid = Guid.NewGuid();
                meta.Guid = newGuid;
                MetaFile.Write(MetaFile.GetMetaPath(file), meta);

                var copyEntry = new AssetEntry
                {
                    Guid = newGuid,
                    Path = relativePath,
                    ImporterType = importerName,
                    ImporterVersion = meta.ImporterVersion,
                    MetaFingerprint = metaFingerprint,
                    NeedsReimport = true
                };
                _guidToEntry[newGuid] = copyEntry;
                _pathToGuid[relativePath] = newGuid;
            }
            else
            {
                // The original is gone: this is a move/rename - just repoint the entry.
                _pathToGuid.Remove(oldPath);
                entry.Path = relativePath;
                _pathToGuid[relativePath] = meta.Guid;
            }
        }
        else
        {
            // New asset
            var entry = new AssetEntry
            {
                Guid = meta.Guid,
                Path = relativePath,
                ImporterType = importerName,
                ImporterVersion = meta.ImporterVersion,
                MetaFingerprint = metaFingerprint,
                NeedsReimport = true
            };
            _guidToEntry[meta.Guid] = entry;
            _pathToGuid[relativePath] = meta.Guid;
        }
    }

    // ================================================================
    //  Importing
    // ================================================================

    private void ImportDirty()
    {
        var dirty = _guidToEntry.Values.Where(e => e.NeedsReimport).ToList();
        if (dirty.Count == 0) return;

        Runtime.Debug.Log($"Importing {dirty.Count} assets (background)...");
        foreach (var entry in dirty)
        {
            if (!entry.NeedsReimport)
                continue;
            EnqueueBackgroundImport(entry);
        }
        // Main-only importers ran inline above; worker results are applied in time-sliced
        // batches by ApplyCompletedImports (editor tick / init pump / FlushBackgroundImports).
    }
    // Importing (especially models/textures) transiently allocates a lot - raw file bytes,
    // per-polygon-vertex mesh unpacking, texture decode buffers - none of which is reachable once
    // disposed, but the CLR doesn't eagerly decommit freed Large Object Heap segments back to the OS
    // on its own. Left alone, a heavy import batch's peak memory (and the page file backing it)
    // lingers for the rest of the session instead of shrinking back down once the garbage is
    // actually collectible. Force a compacting collection to reclaim it immediately instead of
    // waiting on the GC's own heuristics, which are tuned for throughput, not for promptly returning
    // memory after a one-off spike.
    private static void ReclaimMemory()
    {
        System.Runtime.GCSettings.LargeObjectHeapCompactionMode = System.Runtime.GCLargeObjectHeapCompactionMode.CompactOnce;
        GC.Collect();
        GC.WaitForPendingFinalizers();
        GC.Collect();
    }

    /// <summary>Synchronous import (main thread): core + apply composed inline. Used by every
    /// on-demand path (LoadFresh/Reimport/SaveAsset/CreateAsset/watcher single-file); bulk paths
    /// go through EnqueueBackgroundImport instead.</summary>
    private bool RunImport(AssetEntry entry) => RunImport(entry, out _, out _);

    private bool RunImport(AssetEntry entry, out string? error, out bool sourceOnly)
    {
        error = null;
        sourceOnly = false;
        string absolutePath = ResolveAssetAbsolutePath(entry.Path);
        if (!File.Exists(absolutePath))
        {
            entry.NeedsReimport = false;
            error = "The source file is missing: " + entry.Path;
            return false;
        }
        var result = ImportCore(entry, absolutePath);
        bool imported = ApplyImportResult(result);
        error = result.Error;
        sourceOnly = result.SourceOnly;
        return imported;
    }
    private void SerializeToCache(Guid guid, EngineObject obj, SerializationContext? context = null)
    {
        string cachePath = GetCachePath(guid);
        Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);

        try
        {
            // Temporarily clear AssetID so the serializer writes the full object
            // instead of short-circuiting to a $assetId reference
            var savedId = obj.AssetID;
            EchoObject echo;
            try
            {
                obj.AssetID = Guid.Empty;

                // Force base type info: built players do not know an asset's concrete type upfront.
                echo = context != null
                    ? Serializer.Serialize(typeof(object), obj, context)
                    : Serializer.Serialize(typeof(object), obj);
            }
            finally
            {
                obj.AssetID = savedId;
            }

            // Write to a temp file and rename into place (matching MetaFile.Write) so a
            // crash/power-loss mid-write can't leave a truncated cache file behind.
            string tempPath = cachePath + ".tmp";
            echo.WriteToBinary(new FileInfo(tempPath));
            File.Move(tempPath, cachePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Runtime.Debug.LogWarning($"Failed to cache asset {guid}: {ex.Message}");
        }
    }

    // ================================================================
    //  Query API
    // ================================================================

    public AssetEntry? GetEntry(Guid guid)
        => _guidToEntry.GetValueOrDefault(guid);

    public AssetEntry? GetEntry(string relativePath)
        => _pathToGuid.TryGetValue(relativePath, out var guid) ? _guidToEntry.GetValueOrDefault(guid) : null;

    public override bool TryGetSourcePath(Guid assetId, out string path)
    {
        AssetEntry? entry = GetEntry(assetId);
        if (entry is not null && TryResolveAssetPath(entry.Path, out string resolved) && File.Exists(resolved))
        {
            path = resolved;
            return true;
        }

        path = string.Empty;
        return false;
    }

    public Guid PathToGuid(string relativePath)
        => _pathToGuid.GetValueOrDefault(relativePath);

    public string? GuidToPath(Guid guid)
        => _guidToEntry.TryGetValue(guid, out var entry) ? entry.Path : null;

    /// <summary>
    /// Resolve a GUID to a file path, including sub-assets (returns the parent asset's path).
    /// </summary>
    public string? GuidToPathIncludingSubAssets(Guid guid)
    {
        // Try main asset first
        if (_guidToEntry.TryGetValue(guid, out var entry))
            return entry.Path;
        // Try sub-asset -> parent
        if (_subAssetIndex.TryGetValue(guid, out var subInfo))
            return _guidToEntry.TryGetValue(subInfo.parentGuid, out var parentEntry) ? parentEntry.Path : null;
        return null;
    }

    /// <summary>If <paramref name="guid"/> is a sub-asset, returns its parent's GUID. False for a
    /// main asset or an unknown GUID.</summary>
    public bool TryGetParentGuid(Guid guid, out Guid parentGuid)
    {
        if (_subAssetIndex.TryGetValue(guid, out var subInfo))
        {
            parentGuid = subInfo.parentGuid;
            return true;
        }
        parentGuid = Guid.Empty;
        return false;
    }

    public IEnumerable<AssetEntry> GetAllEntries() => _guidToEntry.Values;

    public IEnumerable<AssetEntry> FindAssetsOfType<T>() where T : EngineObject
        => FindAssetsOfType(typeof(T));

    /// <summary>
    /// Find all assets (main + sub) matching the given type via inheritance.
    /// Returns (Guid, Name, ParentPath) tuples for display.
    /// </summary>
    public IEnumerable<AssetEntry> FindAssetsOfType(Type type)
        => _guidToEntry.Values.Where(e => e.MainAssetType != null && type.IsAssignableFrom(e.MainAssetType));

    /// <summary>
    /// Find all assets AND sub-assets matching the given type.
    /// Includes built-in assets (embedded in runtime).
    /// Returns tuples of (guid, displayName, parentPath, type).
    /// </summary>
    public IEnumerable<(Guid guid, string name, string parentPath, Type assetType)> FindAllOfType(Type type)
    {
        // Built-in assets first
        foreach (var item in Runtime.BuiltInAssets.FindAllOfType(type))
            yield return item;

        // Main assets
        foreach (var entry in _guidToEntry.Values)
        {
            if (entry.MainAssetType != null && type.IsAssignableFrom(entry.MainAssetType))
                yield return (entry.Guid, Path.GetFileNameWithoutExtension(entry.Path), entry.Path, entry.MainAssetType);
        }

        // Sub-assets
        foreach (var entry in _guidToEntry.Values)
        {
            if (entry.SubAssets == null) continue;
            foreach (var sub in entry.SubAssets)
            {
                var subType = sub.Type;
                if (subType != null && type.IsAssignableFrom(subType))
                    yield return (sub.Guid, sub.Name, entry.Path, subType);
            }
        }
    }

    /// <summary>Get sub-assets of a parent asset.</summary>
    public SubAssetEntry[] GetSubAssets(Guid parentGuid)
        => _guidToEntry.TryGetValue(parentGuid, out var entry) ? entry.SubAssets : Array.Empty<SubAssetEntry>();

    public string[] GetAllAssetPaths()
        => _pathToGuid.Keys.ToArray();

    // ================================================================
    //  Folder / file structure (cached tree the Project Panel reads)
    // ================================================================

    /// <summary>An immediate child folder of some folder, relative to the Assets root.</summary>
    public readonly struct FolderRecord
    {
        public readonly string RelativePath;
        public readonly string Name;
        public FolderRecord(string relativePath, string name) { RelativePath = relativePath; Name = name; }
    }

    /// <summary>An asset file (non-.meta) within a folder, with its cached size and modified time.</summary>
    public readonly struct FileRecord
    {
        public readonly string RelativePath;
        public readonly string Name;
        public readonly long Size;
        public readonly DateTime Modified;
        public FileRecord(string relativePath, string name, long size, DateTime modified)
        { RelativePath = relativePath; Name = name; Size = size; Modified = modified; }
    }

    /// <summary>Immediate subfolders of the given folder (""=Assets root). Served from the cached index.</summary>
    public IReadOnlyList<FolderRecord> GetSubFolders(string folderRelativePath)
    {
        EnsureFolderIndex();
        return _folderIndex.TryGetValue(NormalizePath(folderRelativePath ?? ""), out var c)
            ? c.SubFolders : Array.Empty<FolderRecord>();
    }

    /// <summary>Immediate asset files of the given folder (""=Assets root). Served from the cached index.</summary>
    public IReadOnlyList<FileRecord> GetFolderFiles(string folderRelativePath)
    {
        EnsureFolderIndex();
        return _folderIndex.TryGetValue(NormalizePath(folderRelativePath ?? ""), out var c)
            ? c.Files : Array.Empty<FileRecord>();
    }

    /// <summary>
    /// Whether the folder ("" = Assets root, or a package logical root path) is present in the
    /// cached index — i.e. still exists on disk as of the last rebuild. The virtual "Packages"
    /// root counts as existing while any package is resolved.
    /// </summary>
    public bool FolderExists(string folderRelativePath)
    {
        EnsureFolderIndex();
        return _folderIndex.ContainsKey(NormalizePath(folderRelativePath ?? ""));
    }

    /// <summary>Force the folder/file index to rebuild on next query. Driven by the asset watcher.</summary>
    public void InvalidateFolderIndex() => MarkFolderIndexDirty();

    private void EnsureFolderIndex()
    {
        if (!_folderIndexDirty) return;
        _folderIndex.Clear();
        _folderIndexDirty = false;

        // Assets/ root.
        var assetsPath = _project.AssetsPath;
        if (Directory.Exists(assetsPath))
            BuildFolderIndex(assetsPath, "", isPackage: false);

        // Each resolved package root is exposed through its stable logical package root.
        // Peek only — Resolve() used to walk/hash every package from folder-index rebuilds
        // (any non-.meta watcher event marks the index dirty).
        var packages = Projects.Packages.PackageManager.PeekCached(_project);
        if (packages == null) return;
        foreach (var pkg in packages)
        {
            string root = pkg.ResolvedRoot;
            if (!Directory.Exists(root)) continue;
            BuildFolderIndex(root, pkg.LogicalRoot, isPackage: true);
        }

        // Virtual "Packages" root aggregating every resolved package as a child folder
        // (Unity-style): label = DisplayName (fallback technical Name), path = LogicalRoot.
        var packagesRoot = new FolderContents();
        foreach (var pkg in packages)
        {
            if (!Directory.Exists(pkg.ResolvedRoot)) continue;
            string label = string.IsNullOrWhiteSpace(pkg.DisplayName) ? pkg.Name : pkg.DisplayName;
            packagesRoot.SubFolders.Add(new FolderRecord(pkg.LogicalRoot, label));
        }
        if (packagesRoot.SubFolders.Count > 0)
            _folderIndex["Packages"] = packagesRoot;
    }

    private void BuildFolderIndex(string absolutePath, string relativePath, bool isPackage)
    {
        var contents = new FolderContents();
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(absolutePath))
            {
                string name = Path.GetFileName(dir);
                // Skip ~ -suffixed (samples/archive) and .-prefixed (hidden) directories inside packages.
                if (isPackage && (name.EndsWith("~", StringComparison.OrdinalIgnoreCase) || name.StartsWith('.')))
                    continue;
                string childRel = relativePath.Length == 0 ? name : relativePath + "/" + name;
                contents.SubFolders.Add(new FolderRecord(childRel, name));

                // List hidden folders (so the content view can show them under "Show Hidden") but
                // don't descend into them - matches the folder tree's existing '.'-prefix skip and
                // avoids walking large hidden trees like .git.
                if (!name.StartsWith('.'))
                    BuildFolderIndex(dir, childRel, isPackage);
            }

            foreach (var file in Directory.EnumerateFiles(absolutePath))
            {
                string name = Path.GetFileName(file);
                if (name.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) continue;

                string childRel = relativePath.Length == 0 ? name : relativePath + "/" + name;
                long size = 0; DateTime mod = DateTime.MinValue;
                try { var fi = new FileInfo(file); size = fi.Length; mod = fi.LastWriteTimeUtc; } catch { }
                contents.Files.Add(new FileRecord(childRel, name, size, mod));
            }
        }
        catch { }

        _folderIndex[relativePath] = contents;
    }

    public DependencyGraph Dependencies => _dependencies;
    public string ThumbnailsPath => _project.ThumbnailsPath;

    /// <summary>
    /// Clear the cache for <see cref="_loadedAssets"/> on assembly reload so that scenes/prefabs that might hold
    /// user-defined scripts won't stop the ALC from reloading
    /// </summary>
    [OnAssemblyUnload]
    internal static void ClearScenesAndPrefabForReload()
    {
        var db = Instance;
        if (db == null) return;

        foreach (var kv in db._loadedAssets.ToArray())
        {
            var asset = kv.Value;
            if (asset is null) continue;

            bool sensitive = asset is Runtime.Resources.Scene
                          || asset is Runtime.Resources.PrefabAsset
                          || asset.GetType().Assembly.IsCollectible;

            if (!sensitive) continue;

            if (db._loadedAssets.TryRemove(kv.Key, out _))
            {
                AssetDatabase.Forget(kv.Key);
                try
                {
                    asset.Dispose();
                }
                catch(Exception e)
                {
                    Debug.LogException(e);
                }
            }
        }
    }

    /// <summary>Load a cached thumbnail for an asset. Returns (width, height, pixels) or null.</summary>
    public (int width, int height, byte[] pixels)? LoadThumbnail(Guid guid) => ThumbnailGenerator.LoadThumbnail(guid, _project.ThumbnailsPath);

    /// <summary>
    /// Resolve an asset GUID to its cached GPU thumbnail texture, building it from the on-disk
    /// pixel cache (<see cref="LoadThumbnail"/>) on first use. Returns null when no thumbnail
    /// exists yet. Shared by every UI that shows asset thumbnails (Project panel, asset pickers,
    /// Terrain layer tiles, etc.) so there's one GPU copy per asset, invalidated automatically
    /// whenever this database reimports or deletes that asset.
    /// </summary>
    public Runtime.Resources.Texture2D? GetThumbnailTexture(Guid guid)
    {
        if (guid == Guid.Empty) return null;

        if (_thumbnailTextures.TryGetValue(guid, out var cached))
            return cached;

        var thumb = LoadThumbnail(guid);
        if (thumb == null) return null;

        try
        {
            var (w, h, pixels) = thumb.Value;
            if (Thumbnails.ThumbnailGenerator.IsUnusablePreview(pixels))
            {
                _thumbnailTextures[guid] = null;
                return null;
            }

            var tex = new Runtime.Resources.Texture2D((uint)w, (uint)h, false, TextureImageFormat.Color4b);
            tex.SetData<byte>(pixels);
            tex.SetTextureFilters(TextureMin.Linear, TextureMag.Linear);
            _thumbnailTextures[guid] = tex;
            return tex;
        }
        catch
        {
            _thumbnailTextures[guid] = null;
            return null;
        }
    }

    /// <summary>Dispose and drop a single cached thumbnail texture so it's rebuilt from disk on next access.</summary>
    public void InvalidateThumbnailTexture(Guid guid)
    {
        if (_thumbnailTextures.TryGetValue(guid, out var tex))
        {
            tex?.Dispose();
            _thumbnailTextures.Remove(guid);
        }
    }

    /// <summary>Dispose and clear every cached thumbnail texture (e.g. after the thumbnail size setting changes).</summary>
    public void ClearThumbnailTextureCache()
    {
        foreach (var tex in _thumbnailTextures.Values)
            tex?.Dispose();
        _thumbnailTextures.Clear();
    }

    // ================================================================
    //  Asset CRUD
    // ================================================================

    /// <summary>
    /// Create a new asset file on disk from an EngineObject.
    /// </summary>
    public void CreateAsset(EngineObject obj, string relativePath)
    {
        relativePath = NormalizePath(relativePath);
        if (!TryResolveAssetPath(relativePath, out string absolutePath)) return;
        Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);

        // Serialize to the file (typeof(object) forces $type inclusion)
        var echo = Serializer.Serialize(typeof(object), obj);
        if (echo != null)
        {
            File.WriteAllText(absolutePath, echo.WriteToString());
            RegisterInternalWrite(absolutePath);
        }

        // Create meta with correct importer version
        string importerName = EditorRegistries.GetImporterTypeNameForFile(absolutePath);
        var importer = EditorRegistries.CreateImporterByName(importerName);
        var meta = MetaFile.CreateNew(importerName, importer?.Version ?? 1);
        MetaFile.Write(MetaFile.GetMetaPath(absolutePath), meta);

        // Assign the GUID and path to the original instance so any existing
        // AssetRef holding this instance picks up the asset ID immediately,
        obj.AssetID = meta.Guid;
        obj.AssetPath = relativePath;

        // Add to index and import
        var entry = new AssetEntry
        {
            Guid = meta.Guid,
            Path = relativePath,
            ImporterType = importerName,
            NeedsReimport = true
        };
        _guidToEntry[meta.Guid] = entry;
        _pathToGuid[relativePath] = meta.Guid;
        MarkFolderIndexDirty();

        RunImport(entry);
        MetadataCache.Save(_project.MetadataDbPath, _guidToEntry.Values);
    }

    /// <summary>
    /// Import a file that already exists on disk under Assets/ (e.g. a baked lightmap PNG written by
    /// the lightmapper), creating/refreshing its metadata + cache, and return its asset GUID. If the
    /// path is already tracked it's reimported in place (so a re-bake replaces the previous asset).
    /// </summary>
    public Guid ImportFile(string relativePath)
    {
        relativePath = NormalizePath(relativePath);
        if (!TryResolveAssetPath(relativePath, out string absolutePath)) return Guid.Empty;
        if (!File.Exists(absolutePath)) return Guid.Empty;

        string importerName = EditorRegistries.GetImporterTypeNameForFile(absolutePath);
        var importer = EditorRegistries.CreateImporterByName(importerName);
        var meta = MetaFile.EnsureMeta(absolutePath, importerName, importer?.Version ?? 1, importer?.DefaultSettings());

        // If we still track this path under a guid that no longer matches the on-disk .meta, the file
        // was replaced out-of-band (e.g. the lightmapper deletes + rewrites its whole folder via the
        // filesystem, bypassing the database, so EnsureMeta minted a fresh guid). The .meta is the
        // authoritative source after a restart, so drop the stale mapping and re-register under
        // meta.Guid. Otherwise the reimport-in-place branch below returns the old guid, which the
        // caller persists (e.g. into the scene's lightmap refs) yet nothing resolves to it on reload.
        if (_pathToGuid.TryGetValue(relativePath, out var staleGuid) && staleGuid != meta.Guid)
        {
            DisposeAndRemove(staleGuid);
            _guidToEntry.TryRemove(staleGuid, out _);
            _pathToGuid.Remove(relativePath);
        }

        // Already tracked at this path -> reimport in place (re-bake replacement).
        if (_pathToGuid.TryGetValue(relativePath, out var existingGuid) && _guidToEntry.TryGetValue(existingGuid, out var existing))
        {
            existing.NeedsReimport = true;
            RunImport(existing);
            ReimportDirectDependents(existing.Guid);
            MetadataCache.Save(_project.MetadataDbPath, _guidToEntry.Values);
            return existing.Guid;
        }

        var entry = new AssetEntry
        {
            Guid = meta.Guid,
            Path = relativePath,
            ImporterType = importerName,
            NeedsReimport = true,
        };
        _guidToEntry[meta.Guid] = entry;
        _pathToGuid[relativePath] = meta.Guid;
        MarkFolderIndexDirty();
        RunImport(entry);
        MetadataCache.Save(_project.MetadataDbPath, _guidToEntry.Values);
        return meta.Guid;
    }

    /// <summary>
    /// Re-serialize an in-memory asset back to its source file.
    /// </summary>
    public void SaveAsset(EngineObject obj)
    {
        if (obj.AssetID == Guid.Empty || string.IsNullOrEmpty(obj.AssetPath)) return;

        // Sub-assets have paths like "Model.fbx#Mesh_0" can't save those directly
        if (obj.AssetPath.Contains('#'))
        {
            Runtime.Debug.LogWarning($"Cannot save sub-asset directly: {obj.AssetPath}");
            return;
        }

        // Package assets (prefixed Packages/) are read-only from the project's perspective — refuse
        // to create/overwrite them so an embed package's contents are only ever edited in place by
        // the package author, never silently mutated by the project.
        if (IsPackageRelativePath(obj.AssetPath))
        {
            Runtime.Debug.LogWarning($"Cannot create asset at '{obj.AssetPath}': package assets are read-only.");
            obj.Dispose();
            return;
        }

        string absolutePath = ResolveAssetAbsolutePath(obj.AssetPath);
        var echo = Serializer.Serialize(typeof(object), obj);
        if (echo != null)
        {
            File.WriteAllText(absolutePath, echo.WriteToString());
            RegisterInternalWrite(absolutePath);
        }

        // Keep the last usable family until the new import is ready to publish.
        if (_guidToEntry.TryGetValue(obj.AssetID, out var entry))
        {
            entry.NeedsReimport = true;
            RunImport(entry);
            MetadataCache.Save(_project.MetadataDbPath, _guidToEntry.Values);
        }
    }

    /// <summary>
    /// Delete an asset and its .meta file.
    /// </summary>
    public void DeleteAsset(string relativePath)
    {
        relativePath = NormalizePath(relativePath);
        // Refuse to delete package assets through the asset API — they are owned by their package
        // and can only be removed by uninstalling the package.
        if (IsPackageRelativePath(relativePath))
        {
            Runtime.Debug.LogWarning($"Cannot delete package asset '{relativePath}': remove the package instead.");
            return;
        }
        string absolutePath = ResolveAssetAbsolutePath(relativePath);

        if (_pathToGuid.TryGetValue(relativePath, out var guid))
        {
            // Dispose main + sub-asset instances FIRST so any AssetRef holding them
            // detects IsNotValid on next access and stops returning the deleted
            // instance. Without this, materials etc. keep using the now-orphaned
            // shader (cached in _shader.instance) until the editor restarts.
            var entry = _guidToEntry.TryGetValue(guid, out var e) ? e : null;
            DisposeAndRemove(guid);
            ThumbnailGenerator.DeleteThumbnail(guid, _project.ThumbnailsPath);
            InvalidateThumbnailTexture(guid);
            if (entry != null)
                RemoveSubAssets(entry, includeThumbnails: true);

            _guidToEntry.TryRemove(guid, out _);
            _pathToGuid.Remove(relativePath);
            _dependencies.RemoveAsset(guid);

            // Clean main cache file
            string cachePath = GetCachePath(guid);
            if (File.Exists(cachePath))
                try { File.Delete(cachePath); } catch { }
        }

        // Delete files. The index/dispose state above has already been cleared, so an
        // unhandled exception here (e.g. the file is locked by an external app) would leave
        // the database thinking the asset is gone while it still exists on disk. Catch and
        // warn instead of throwing, matching the best-effort cache/thumbnail cleanup above.
        // Files go to the OS recycle bin when possible (Unity semantics); hard delete is the
        // fallback for non-Windows or a shell refusal.
        try
        {
            if (File.Exists(absolutePath) && !Utils.RecycleBin.TryDelete(absolutePath))
                File.Delete(absolutePath);
            string metaPath = MetaFile.GetMetaPath(absolutePath);
            if (File.Exists(metaPath) && !Utils.RecycleBin.TryDelete(metaPath))
                File.Delete(metaPath);
        }
        catch (Exception ex)
        {
            Runtime.Debug.LogWarning($"Failed to delete asset file '{relativePath}': {ex.Message}");
        }

        MetadataCache.Save(_project.MetadataDbPath, _guidToEntry.Values);
        OnAssetsDeleted?.Invoke(new[] { relativePath });
        MarkFolderIndexDirty();

        // Script deleted - trigger recompile
        if (relativePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
            ScriptAssemblyManager.RequestRecompile();
    }

    /// <summary>
    /// Move/rename an asset. The GUID stays the same.
    /// </summary>
    public bool MoveAsset(string oldRelativePath, string newRelativePath)
    {
        oldRelativePath = NormalizePath(oldRelativePath);
        newRelativePath = NormalizePath(newRelativePath);
        if (!TryResolveAssetPath(oldRelativePath, out string oldAbsolute)) return false;
        if (!TryResolveAssetPath(newRelativePath, out string newAbsolute)) return false;

        if (!File.Exists(oldAbsolute)) return false;

        // On a case-insensitive filesystem, a case-only rename's target path File.Exists-matches the
        // source file itself - that's not a collision with a different file.
        bool isCaseOnlyRename = string.Equals(oldRelativePath, newRelativePath, StringComparison.OrdinalIgnoreCase);

        if (!isCaseOnlyRename && File.Exists(newAbsolute))
        {
            Runtime.Debug.LogWarning($"Cannot rename: a file already exists at '{newRelativePath}'.");
            return false;
        }

        Directory.CreateDirectory(Path.GetDirectoryName(newAbsolute)!);

        // Move asset file + .meta atomically (with rollback on failure)
        string oldMeta = MetaFile.GetMetaPath(oldAbsolute);
        string newMeta = MetaFile.GetMetaPath(newAbsolute);
        bool assetMoved = false;

        try
        {
            File.Move(oldAbsolute, newAbsolute);
            assetMoved = true;

            if (File.Exists(oldMeta))
                File.Move(oldMeta, newMeta);
        }
        catch (Exception ex)
        {
            // Rollback: if asset moved but meta didn't, move asset back
            if (assetMoved && File.Exists(newAbsolute) && !File.Exists(oldAbsolute))
            {
                try { File.Move(newAbsolute, oldAbsolute); }
                catch { /* best effort rollback */ }
            }
            Runtime.Debug.LogError($"Failed to move asset '{oldRelativePath}' -> '{newRelativePath}': {ex.Message}");
            return false;
        }

        // Update index
        if (_pathToGuid.TryGetValue(oldRelativePath, out var guid))
        {
            _pathToGuid.Remove(oldRelativePath);
            _pathToGuid[newRelativePath] = guid;
            _guidToEntry[guid].Path = newRelativePath;

            if (TryGetLoaded(guid, out var obj))
                obj.AssetPath = newRelativePath;

            // Update sub-asset AssetPaths (they use "parent/path#SubName" format)
            var movedEntry = _guidToEntry.GetValueOrDefault(guid);
            if (movedEntry != null)
                UpdateSubAssetPaths(movedEntry, newRelativePath);
        }

        MetadataCache.Save(_project.MetadataDbPath, _guidToEntry.Values);
        OnAssetMoved?.Invoke(oldRelativePath, newRelativePath);
        MarkFolderIndexDirty();
        return true;
    }

    /// <summary>
    /// Move a folder and everything inside it. GUIDs are preserved the metadata index is
    /// remapped in-place, and <see cref="OnAssetMoved"/> fires for every relocated file.
    /// </summary>
    public bool MoveFolder(string oldRelativeFolder, string newRelativeFolder)
    {
        oldRelativeFolder = NormalizePath(oldRelativeFolder);
        newRelativeFolder = NormalizePath(newRelativeFolder);
        if (oldRelativeFolder == newRelativeFolder) return true;

        if (!TryResolveAssetPath(oldRelativeFolder, out string oldAbs)) return false;
        if (!TryResolveAssetPath(newRelativeFolder, out string newAbs)) return false;

        if (!Directory.Exists(oldAbs)) return false;
        if (Directory.Exists(newAbs) || File.Exists(newAbs))
        {
            Runtime.Debug.LogWarning($"Cannot move folder: '{newRelativeFolder}' already exists.");
            return false;
        }

        // Guard against moving a folder into itself or a descendant that would delete the
        // parent while its children were still mid-copy on Windows.
        string oldWithSlash = oldRelativeFolder + "/";
        if (newRelativeFolder.Equals(oldRelativeFolder, StringComparison.OrdinalIgnoreCase)
            || newRelativeFolder.StartsWith(oldWithSlash, StringComparison.OrdinalIgnoreCase))
        {
            Runtime.Debug.LogWarning($"Cannot move folder '{oldRelativeFolder}' into itself.");
            return false;
        }

        // Snapshot the set of tracked paths inside the folder before the move the files on
        // disk move atomically via Directory.Move, but the in-memory index needs per-entry
        // path rewrites afterward.
        var toRemap = new List<(string oldPath, string newPath, Guid guid)>();
        foreach (var kv in _pathToGuid)
        {
            string p = kv.Key;
            if (p.Equals(oldRelativeFolder, StringComparison.OrdinalIgnoreCase)
                || p.StartsWith(oldWithSlash, StringComparison.OrdinalIgnoreCase))
            {
                string suffix = p.Substring(oldRelativeFolder.Length);
                string newPath = newRelativeFolder + suffix;
                toRemap.Add((p, newPath, kv.Value));
            }
        }

        Directory.CreateDirectory(Path.GetDirectoryName(newAbs)!);

        try
        {
            Directory.Move(oldAbs, newAbs);
        }
        catch (Exception ex)
        {
            Runtime.Debug.LogError($"Failed to move folder '{oldRelativeFolder}' -> '{newRelativeFolder}': {ex.Message}");
            return false;
        }

        foreach (var (oldPath, newPath, guid) in toRemap)
        {
            _pathToGuid.Remove(oldPath);
            _pathToGuid[newPath] = guid;
            if (_guidToEntry.TryGetValue(guid, out var entry))
            {
                entry.Path = newPath;

                // Update sub-asset AssetPaths (they use "parent/path#SubName" format)
                UpdateSubAssetPaths(entry, newPath);
            }
            if (TryGetLoaded(guid, out var obj))
                obj.AssetPath = newPath;

            OnAssetMoved?.Invoke(oldPath, newPath);
        }

        MetadataCache.Save(_project.MetadataDbPath, _guidToEntry.Values);
        MarkFolderIndexDirty();
        return true;
    }

    /// <summary>
    /// Force reimport a specific asset.
    /// </summary>
    /// <summary>Dispose and remove a cached asset. Triggers AssetRef re-resolve on next access.</summary>
    private void DisposeAndRemove(Guid guid, bool notify = true)
    {
        if (TryGetLoaded(guid, out var old))
        {
            try { old.Dispose(); } catch { }
        }
        _loadedAssets.TryRemove(guid, out _);
        AssetDatabase.Forget(guid);
        if (notify) NotifyAssetRemoved(guid);
    }

    public void Reimport(Guid guid) => ReimportWithResult(guid);

    /// <summary>
    /// Reimport the selected target and invalidate its direct dependents, reporting their
    /// outcomes separately. Never treats reloading a previous cache as import success.
    /// Main-thread operation, like Reimport; unexpected IO/callback exceptions still propagate.
    /// </summary>
    public AssetReimportResult ReimportWithResult(Guid guid)
    {
        if (!_guidToEntry.TryGetValue(guid, out var entry))
            return new(guid, null, AssetReimportStatus.NotFound,
                "The selected asset is no longer registered.", Array.Empty<AssetReimportFailure>());
        if (!File.Exists(ResolveAssetAbsolutePath(entry.Path)))
            return new(guid, entry.Path, AssetReimportStatus.MissingSource,
                "The source file is missing: " + entry.Path, Array.Empty<AssetReimportFailure>());

        // ApplyImportResult retires the old family only after importer validation succeeds.
        entry.NeedsReimport = true;
        bool imported = RunImport(entry, out string? error, out bool sourceOnly);
        var dependentFailures = new List<AssetReimportFailure>();
        // Source-only inputs (e.g. shader includes) still invalidate their dependents.
        ReimportDirectDependents(guid, failures: dependentFailures);
        MetadataCache.Save(_project.MetadataDbPath, _guidToEntry.Values);
        if (imported) OnAssetsImported?.Invoke(new[] { entry.Path });

        // Only the newly applied object is evidence of a successful import. Get() could
        // deserialize the previous cache or trigger another import after a failed attempt.
        var reloaded = imported ? GetLoadedAsset(guid) : null;
        if (reloaded != null)
        {
            string? sourceFile = entry.MainAssetType == typeof(Runtime.Resources.Texture2D)
                ? ResolveAssetAbsolutePath(entry.Path)
                : null;
            ThumbnailGenerator.Enqueue(guid, reloaded, sourceFile);
        }

        // Also regenerate sub-asset thumbnails
        if (imported && entry.SubAssets != null)
        {
            foreach (var sub in entry.SubAssets)
            {
                var subAsset = Get(sub.Guid);
                if (subAsset != null)
                    ThumbnailGenerator.Enqueue(sub.Guid, subAsset, null);
            }
        }
        return new(guid, entry.Path,
            imported ? AssetReimportStatus.Imported : sourceOnly ? AssetReimportStatus.SourceOnly : AssetReimportStatus.Failed,
            error, dependentFailures.ToArray());
    }

    /// <summary>★ AI tooling — Force a full asset-database refresh: rescan Assets/, import anything
    /// new/changed, rebuild the resources map, and persist the metadata cache. This is the batch
    /// equivalent of <see cref="Reimport(Guid)"/> — call it after an AI agent has written many files
    /// (scripts, assets, .meta) so the editor picks them up without waiting for the debounced
    /// AssetWatcher. Safe to call on the main loop; heavy (re-enumerates the whole Assets tree).</summary>
    public void RefreshAll()
    {
        ScanAssets();
        ImportDirty();
        FlushBackgroundImports(60_000);
        RefreshResourcesMap();
        MetadataCache.Save(_project.MetadataDbPath, _guidToEntry.Values);
        OnAssetsImported?.Invoke(_guidToEntry.Values.Select(e => e.Path).ToArray());
        Runtime.Debug.Log($"[EditorAssetBackend] RefreshAll complete: {_guidToEntry.Count} assets.");
    }

    // ================================================================
    //  File Watching (per-frame update)
    // ================================================================

    private static bool TryGetFileFingerprint(string absolutePath, out FileFingerprint fingerprint)
    {
        fingerprint = default;
        try
        {
            // The file may be replaced while a watcher callback is in flight. Read with delete
            // sharing and take the size/mtime after the content pass so a transient replacement
            // simply fails the comparison instead of being mistaken for our own write.
            using var stream = new FileStream(
                absolutePath,
                FileMode.Open,
                FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete,
                bufferSize: 8192,
                options: FileOptions.SequentialScan);

            ulong hash = 14695981039346656037UL; // FNV-1a; this is a change discriminator, not security.
            Span<byte> buffer = stackalloc byte[8192];
            int read;
            while ((read = stream.Read(buffer)) > 0)
            {
                for (int i = 0; i < read; i++)
                {
                    hash ^= buffer[i];
                    hash *= 1099511628211UL;
                }
            }

            long length = stream.Length;
            long lastWriteUtcTicks = File.GetLastWriteTimeUtc(absolutePath).Ticks;
            fingerprint = new FileFingerprint(length, lastWriteUtcTicks, hash);
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private static string? NormalizeAbsolutePath(string path)
    {
        try { return Path.GetFullPath(path); }
        catch (ArgumentException) { return null; }
        catch (NotSupportedException) { return null; }
        catch (IOException) { return null; }
    }

    private static string ComputeMetaFingerprint(MetaFileData meta)
    {
        // Hash the parsed/canonical representation instead of reopening the .meta after callers
        // already parsed it. This ignores formatting, filesystem timestamps, and compound-key
        // ordering while preserving GUID/importer/version/settings changes that affect import
        // semantics.
        var echo = EchoObject.NewCompound();
        echo["guid"] = new EchoObject(meta.Guid.ToString());
        echo["importer"] = new EchoObject(meta.ImporterType);
        echo["importerVersion"] = new EchoObject(meta.ImporterVersion);
        if (meta.Settings != null)
            echo["settings"] = CanonicalizeEcho(meta.Settings);

        byte[] bytes = Encoding.UTF8.GetBytes(echo.WriteToString());
        ulong hash = 14695981039346656037UL;
        for (int i = 0; i < bytes.Length; i++)
        {
            hash ^= bytes[i];
            hash *= 1099511628211UL;
        }

        return $"{bytes.Length:X16}{hash:X16}";
    }

    private static EchoObject CanonicalizeEcho(EchoObject value)
    {
        if (value.TagType == EchoType.Compound)
        {
            var canonical = EchoObject.NewCompound();
            var keys = new string[value.Tags.Count];
            value.Tags.Keys.CopyTo(keys, 0);
            Array.Sort(keys, StringComparer.Ordinal);
            foreach (string key in keys)
                canonical[key] = CanonicalizeEcho(value.Tags[key]);
            return canonical;
        }

        if (value.TagType == EchoType.List)
        {
            var canonical = EchoObject.NewList();
            foreach (var item in value.List)
                canonical.ListAdd(CanonicalizeEcho(item));
            return canonical;
        }

        return value.Clone();
    }

    private static bool TryReadMetaFingerprint(string absoluteAssetPath, out string fingerprint)
    {
        fingerprint = "";
        try
        {
            string metaPath = MetaFile.GetMetaPath(absoluteAssetPath);
            if (!File.Exists(metaPath))
                return true;

            fingerprint = ComputeMetaFingerprint(MetaFile.Read(metaPath));
            return true;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    private bool CanBaselineLegacyMetaFingerprint(
        AssetEntry entry,
        Guid guid,
        string sourcePath,
        long currentSourceTicks,
        string metaPath)
    {
        // Entries loaded from the pre-fingerprint cache have no way to prove which .meta settings
        // produced their binary cache. Avoid a needless full-project reimport only when every
        // available timestamp says the existing cache was written after both source and .meta.
        // Otherwise the conservative path below reimports and records a trustworthy fingerprint.
        if (!string.IsNullOrEmpty(entry.MetaFingerprint)
            || entry.LastModifiedTicks != currentSourceTicks
            || !File.Exists(GetCachePath(guid)))
            return false;

        try
        {
            long cacheTicks = File.GetLastWriteTimeUtc(GetCachePath(guid)).Ticks;
            long sourceTicks = File.GetLastWriteTimeUtc(sourcePath).Ticks;
            long metaTicks = File.GetLastWriteTimeUtc(metaPath).Ticks;
            return cacheTicks >= sourceTicks && cacheTicks >= metaTicks;
        }
        catch (IOException)
        {
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            return false;
        }
    }

    /// <summary>
    /// Records a file written by the asset database. The record is bounded and expires quickly;
    /// it is not a permanent ignore list, so a later external edit still reaches the importer.
    /// </summary>
    internal void RegisterInternalWrite(string absolutePath)
        => RegisterInternalWrite(absolutePath, DateTime.UtcNow.Ticks);

    internal void RegisterInternalWrite(string absolutePath, long nowTicks)
    {
        string? key = NormalizeAbsolutePath(absolutePath);
        if (key == null || !TryGetFileFingerprint(key, out var fingerprint))
            return;

        PruneInternalWriteRecords(nowTicks);

        if (!_internalWriteRecords.ContainsKey(key) && _internalWriteRecords.Count >= MaxInternalWriteRecords)
        {
            string? oldestPath = null;
            long oldestExpiry = long.MaxValue;
            foreach (var pair in _internalWriteRecords)
            {
                if (pair.Value.ExpiresUtcTicks < oldestExpiry)
                {
                    oldestExpiry = pair.Value.ExpiresUtcTicks;
                    oldestPath = pair.Key;
                }
            }

            if (oldestPath != null)
                _internalWriteRecords.Remove(oldestPath);
        }

        _internalWriteRecords[key] = new InternalWriteRecord(
            fingerprint,
            nowTicks + InternalWriteRecordLifetimeTicks);
    }

    /// <summary>
    /// Returns true only while the current file still matches a recent internal-write fingerprint.
    /// Matching duplicate watcher events are suppressed; a changed fingerprint invalidates the
    /// record and lets the event proceed as a real external edit.
    /// </summary>
    internal bool TrySuppressInternalWrite(string absolutePath)
        => TrySuppressInternalWrite(absolutePath, DateTime.UtcNow.Ticks);

    internal bool TrySuppressInternalWrite(string absolutePath, long nowTicks)
    {
        PruneInternalWriteRecords(nowTicks);

        string? key = NormalizeAbsolutePath(absolutePath);
        if (key == null || !_internalWriteRecords.TryGetValue(key, out var record))
            return false;

        if (!TryGetFileFingerprint(key, out var current))
        {
            _internalWriteRecords.Remove(key);
            return false;
        }

        if (current != record.Fingerprint)
        {
            _internalWriteRecords.Remove(key);
            return false;
        }

        // Keep the record until its deadline so duplicate Created/Changed notifications for the
        // same write are all suppressed. Extend the deadline on each matching notification so a
        // long-running internal batch cannot outlive the first write's fixed TTL.
        _internalWriteRecords[key] = record with
        {
            ExpiresUtcTicks = nowTicks + InternalWriteRecordLifetimeTicks
        };
        return true;
    }

    private void PruneInternalWriteRecords(long nowTicks)
    {
        if (_internalWriteRecords.Count == 0)
            return;

        _expiredInternalWritePaths.Clear();
        foreach (var pair in _internalWriteRecords)
            if (pair.Value.ExpiresUtcTicks <= nowTicks)
                _expiredInternalWritePaths.Add(pair.Key);

        for (int i = 0; i < _expiredInternalWritePaths.Count; i++)
            _internalWriteRecords.Remove(_expiredInternalWritePaths[i]);
    }

    /// <summary>Import a newly created or modified file, tracking it if it isn't already.</summary>
    private void ImportFileChange(string absolutePath, string relativePath, List<string> imported)
    {
        if (GUI.SceneView.EditorSceneManager.ShouldSkipWatcherImport(relativePath))
            return;

        string importerName = EditorRegistries.GetImporterTypeNameForFile(absolutePath);
        var meta = MetaFile.EnsureMeta(absolutePath, importerName);

        if (!_guidToEntry.ContainsKey(meta.Guid))
        {
            var entry = new AssetEntry
            {
                Guid = meta.Guid,
                Path = relativePath,
                ImporterType = importerName,
                NeedsReimport = true
            };
            _guidToEntry[meta.Guid] = entry;
            _pathToGuid[relativePath] = meta.Guid;
        }
        else
        {
            _guidToEntry[meta.Guid].NeedsReimport = true;
        }

        // A bulk background import owns this guid right now: leave it dirty and let the next
        // ImportDirty sweep re-enqueue after the in-flight result applies (double-import here
        // raced the worker cache writes).
        if (_importsInFlight.Contains(meta.Guid))
        {
            _guidToEntry[meta.Guid].NeedsReimport = true;
            return;
        }

        var existingEntry = _guidToEntry[meta.Guid];
        if (RunImport(existingEntry)) imported.Add(relativePath);
        ReimportDirectDependents(existingEntry.Guid, imported);
    }

    private void ReimportDirectDependents(Guid changedGuid, List<string>? imported = null,
        List<AssetReimportFailure>? failures = null)
    {
        IReadOnlySet<Guid> dependentSet = _dependencies.GetDependents(changedGuid);
        var dependents = new Guid[dependentSet.Count];
        int dependentIndex = 0;
        foreach (Guid dependentGuid in dependentSet)
            dependents[dependentIndex++] = dependentGuid;

        for (int i = 0; i < dependents.Length; i++)
        {
            Guid dependentGuid = dependents[i];
            if (dependentGuid == changedGuid || !_guidToEntry.TryGetValue(dependentGuid, out AssetEntry? entry))
                continue;

            entry.NeedsReimport = true;
            if (RunImport(entry, out string? error, out bool sourceOnly))
                imported?.Add(entry.Path);
            else if (!sourceOnly)
                failures?.Add(new(entry.Guid, entry.Path, error ?? "The dependent import failed."));
        }
    }

    // Time-sliced file-change processing: a big batch (folder-tree drop, package remount) continues
    // across frames instead of stalling the editor for its whole duration. Events within one batch
    // stay ordered; the batch-final metadata save and events fire only once, at the end.
    private List<FileEvent>? _pendingFileEvents;
    private int _pendingFileEventIndex;
    private readonly List<string> _batchImported = new();
    private readonly List<string> _batchDeleted = new();
    private Core.Progress.ProgressHandle _batchProgress;

    public void ProcessFileChanges()
    {
        PruneInternalWriteRecords(DateTime.UtcNow.Ticks);

        if (Interlocked.Exchange(ref _packageRootsDirty, 0) != 0)
            RefreshPackageInputs();

        if (_watcher == null) return;

        if (_pendingFileEvents == null)
        {
            var events = _watcher.DrainEvents();
            if (events.Count == 0) return;
            _pendingFileEvents = events;
            _pendingFileEventIndex = 0;
            _batchImported.Clear();
            _batchDeleted.Clear();

            // Unity-style escalation: short batches live on the status-bar spinner; a batch that
            // outlives the popup delay escalates to the corner import card (input gate included).
            _batchProgress = Core.Progress.EditorProgress.Start(
                Core.Progress.EditorProgressKind.Import,
                Loc.Get("import.progress.assets"),
                total: events.Count,
                Core.Progress.EditorProgressVisibility.AutoPopup);
        }

        long start = System.Diagnostics.Stopwatch.GetTimestamp();
        long budgetTicks = Math.Max(1, (long)(Math.Max(Theming.EditorSettings.Instance.AssetWorkSliceBudgetMs, 4)
            * System.Diagnostics.Stopwatch.Frequency / 1000));

        while (_pendingFileEvents != null
               && _pendingFileEventIndex < _pendingFileEvents.Count
               && System.Diagnostics.Stopwatch.GetTimestamp() - start < budgetTicks)
        {
            ProcessOneFileEvent(_pendingFileEvents[_pendingFileEventIndex]);
            _pendingFileEventIndex++;
            Core.Progress.EditorProgress.Report(_batchProgress, _pendingFileEventIndex);
        }

        if (_pendingFileEvents == null || _pendingFileEventIndex < _pendingFileEvents.Count)
            return;

        _pendingFileEvents = null;
        Core.Progress.EditorProgress.Finish(_batchProgress, success: true,
            message: Loc.Get("progress.import.done", new { count = _batchImported.Count }));
        if (_batchImported.Count > 0 || _batchDeleted.Count > 0)
        {
            MetadataCache.Save(_project.MetadataDbPath, _guidToEntry.Values);
            if (_batchImported.Count > 0) OnAssetsImported?.Invoke(_batchImported.ToArray());
            if (_batchDeleted.Count > 0) OnAssetsDeleted?.Invoke(_batchDeleted.ToArray());

            // Refresh resources map if any changes involved Resources/ folders
            bool touchedResources = false;
            for (int i = 0; i < _batchImported.Count && !touchedResources; i++)
            {
                string p = _batchImported[i];
                touchedResources = p.Contains("/Resources/") || p.StartsWith("Resources/");
            }
            for (int i = 0; i < _batchDeleted.Count && !touchedResources; i++)
            {
                string p = _batchDeleted[i];
                touchedResources = p.Contains("/Resources/") || p.StartsWith("Resources/");
            }
            if (touchedResources)
                RefreshResourcesMap();
        }
    }

    private void ProcessOneFileEvent(FileEvent evt)
    {
        // CreateAsset/SaveAsset already imported the exact bytes synchronously. Suppress only
        // matching Created/Modified notifications from those writes; a changed fingerprint is
        // treated as an external edit and proceeds through the normal dependency path.
        if ((evt.Type == FileEventType.Created || evt.Type == FileEventType.Modified)
            && TrySuppressInternalWrite(evt.Path))
            return;

        // Any change to a real (non-.meta) file or folder can add/remove/rename an entry or
        // change a file's size/date, so the cached folder index the Project Panel reads is stale.
        // .meta files are ours and don't affect the displayed structure.
        if (!evt.Path.EndsWith(".meta", StringComparison.OrdinalIgnoreCase))
            MarkFolderIndexDirty();

        // Skip directory events - ScanAssets handles directory .meta creation
        if (Directory.Exists(evt.Path)) return;

        string relativePath = ToRelativePath(evt.Path);

        // Skip .meta files we manage them
        if (relativePath.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) return;

        switch (evt.Type)
        {
            case FileEventType.Created:
            case FileEventType.Modified:
                ImportFileChange(evt.Path, relativePath, _batchImported);
                break;

            case FileEventType.Deleted:
            {
                if (_pathToGuid.TryGetValue(relativePath, out var guid))
                {
                    var deletedEntry = _guidToEntry.GetValueOrDefault(guid);

                    // Dispose main + sub-assets so AssetRefs detect invalidation
                    DisposeAndRemove(guid);
                    if (deletedEntry != null)
                        RemoveSubAssets(deletedEntry, includeThumbnails: false);

                    _guidToEntry.TryRemove(guid, out _);
                    _pathToGuid.Remove(relativePath);
                    _dependencies.RemoveAsset(guid);

                    // Clean main cache file
                    string cachePath = GetCachePath(guid);
                    if (File.Exists(cachePath))
                        try { File.Delete(cachePath); } catch { }

                    _batchDeleted.Add(relativePath);

                    // Script deleted trigger recompile
                    if (relativePath.EndsWith(".cs", StringComparison.OrdinalIgnoreCase))
                        ScriptAssemblyManager.RequestRecompile();
                }
                break;
            }

            case FileEventType.Renamed:
            {
                if (evt.OldPath != null)
                {
                    string oldRelative = ToRelativePath(evt.OldPath);
                    if (!_pathToGuid.TryGetValue(oldRelative, out var guid))
                    {
                        // The old path was never tracked e.g. the "write-to-temp-then-rename-
                        // into-place" atomic-save pattern collapses Created+Renamed within the
                        // debounce window before the temp file is ever imported. Treat the
                        // destination as a brand-new file instead of silently dropping it until
                        // the next full rescan.
                        ImportFileChange(evt.Path, relativePath, _batchImported);
                    }
                    else
                    {
                        _pathToGuid.Remove(oldRelative);
                        _pathToGuid[relativePath] = guid;
                        var renamedEntry = _guidToEntry[guid];
                        renamedEntry.Path = relativePath;

                        // Move .meta
                        string oldMeta = MetaFile.GetMetaPath(evt.OldPath);
                        string newMeta = MetaFile.GetMetaPath(evt.Path);
                        if (File.Exists(oldMeta) && !File.Exists(newMeta))
                            try { File.Move(oldMeta, newMeta); } catch { }

                        if (TryGetLoaded(guid, out var obj))
                            obj.AssetPath = relativePath;

                        // Update sub-asset AssetPaths
                        UpdateSubAssetPaths(renamedEntry, relativePath);

                        // If extension changed, update importer and trigger reimport
                        string oldExt = Path.GetExtension(evt.OldPath);
                        string newExt = Path.GetExtension(evt.Path);
                        if (!string.Equals(oldExt, newExt, StringComparison.OrdinalIgnoreCase))
                        {
                            string newImporterName = EditorRegistries.GetImporterTypeNameForFile(evt.Path);
                            renamedEntry.ImporterType = newImporterName;
                            renamedEntry.NeedsReimport = true;

                            RunImport(renamedEntry);
                            _batchImported.Add(relativePath);
                        }

                        OnAssetMoved?.Invoke(oldRelative, relativePath);
                    }
                }
                break;
            }
        }
    }

    private void RefreshPackageInputs()
    {
        // Package remounts re-scan + re-import everything synchronously; bracket it so the corner
        // card (input gate included) covers the blocking pass.
        var progress = Core.Progress.EditorProgress.Start(
            Core.Progress.EditorProgressKind.Import,
            Loc.Get("progress.import.remount"),
            total: 0,
            Core.Progress.EditorProgressVisibility.ImmediatePopup);
        try
        {
            // MarkPackageRootsDirty already Invalidated. Do not drop a just-rebuilt cache here
            // and force a second Resolve+walk on the same tick.
            ScanAssets();
            ImportDirty();
        FlushBackgroundImports(60_000);
            MetadataCache.Save(_project.MetadataDbPath, _guidToEntry.Values);
            ConfigurePackageMountWatchers();
            RefreshResourcesMap();
        }
        finally
        {
            Core.Progress.EditorProgress.Finish(progress, success: true);
        }
    }

    // ================================================================
    //  Helpers
    // ================================================================

    private string GetCachePath(Guid guid)
        => Path.Combine(_project.CachePath, $"{guid}.asset");

    private void RemoveSubAsset(Guid subGuid, bool includeThumbnails, bool notify = true)
    {
        DisposeAndRemove(subGuid, notify);
        _subAssetIndex.TryRemove(subGuid, out _);
        _dependencies.RemoveAsset(subGuid);
        if (includeThumbnails)
        {
            ThumbnailGenerator.DeleteThumbnail(subGuid, _project.ThumbnailsPath);
            InvalidateThumbnailTexture(subGuid);
        }
        string subCachePath = GetCachePath(subGuid);
        if (File.Exists(subCachePath))
            try { File.Delete(subCachePath); } catch { }
    }

    private void RemoveSubAssets(AssetEntry entry, bool includeThumbnails)
    {
        if (entry.SubAssets == null) return;
        foreach (var sub in entry.SubAssets)
            RemoveSubAsset(sub.Guid, includeThumbnails);
    }

    private void UpdateSubAssetPaths(AssetEntry entry, string newPath)
    {
        if (entry.SubAssets == null) return;
        foreach (var sub in entry.SubAssets)
            if (TryGetLoaded(sub.Guid, out var subObj))
                subObj.AssetPath = $"{newPath}#{sub.Name}";
    }

    private void RebuildSubAssetIndex()
    {
        _subAssetIndex.Clear();
        foreach (var entry in _guidToEntry.Values)
        {
            if (entry.SubAssets == null) continue;
            for (int i = 0; i < entry.SubAssets.Length; i++)
                _subAssetIndex[entry.SubAssets[i].Guid] = (entry.Guid, i);
        }
    }

    /// <summary>Normalize a path to use forward slashes, relative to Assets/, with no trailing slash.</summary>
    public static string NormalizePath(string path)
        => path.Replace('\\', '/').TrimEnd('/');

    /// <summary>
    /// Prefix marking a package-provided asset. Package paths are resolved through PackageManager's
    /// stable logical mount table, so they do not expose a local/git/registry physical source path.
    /// </summary>
    private const string PackagesPathPrefix = "Packages/";

    /// <summary>True when <paramref name="relativePath"/> is a package-provided asset path (begins
    /// with the <see cref="PackagesPathPrefix"/>).</summary>
    private static bool IsPackageRelativePath(string relativePath)
        => relativePath.StartsWith(PackagesPathPrefix, StringComparison.OrdinalIgnoreCase);

    /// <summary>Absolute path of an entry's logical asset path. Package paths resolve through the
    /// package mount table (Library/PackageCache), so they must not be combined with the project
    /// root directly.</summary>
    internal string GetEntryAbsolutePath(AssetEntry entry) => ResolveAssetAbsolutePath(entry.Path);

    /// <summary>Resolve a stored asset relative path to its absolute location on disk. Package
    /// paths resolve through the package mount table; all others resolve under Assets/ exactly as before.</summary>
    private string ResolveAssetAbsolutePath(string relativePath)
    {
        if (IsPackageRelativePath(relativePath))
        {
            if (Projects.Packages.PackageManager.TryResolveLogicalAssetPath(_project, relativePath, out var physicalPath))
                return physicalPath;
            throw new InvalidOperationException($"Package asset '{relativePath}' is not owned by a resolved package.");
        }
        return Path.Combine(_project.AssetsPath, relativePath);
    }

    /// <summary>Resolve a relative path to its absolute form, rejecting any that escape the Assets
    /// folder (or, for package paths, the Packages folder) via ".." segments or a rooted path.</summary>
    private bool TryResolveAssetPath(string relativePath, out string absolutePath)
    {
        if (IsPackageRelativePath(relativePath))
        {
            if (Projects.Packages.PackageManager.TryResolveLogicalAssetPath(_project, relativePath, out absolutePath))
                return true;
            Runtime.Debug.LogWarning($"Rejected package path '{relativePath}': it is not inside a resolved package mount.");
            return false;
        }

        absolutePath = Path.GetFullPath(Path.Combine(_project.AssetsPath, relativePath));
        string assetsRoot = Path.GetFullPath(_project.AssetsPath) + Path.DirectorySeparatorChar;
        if (!absolutePath.StartsWith(assetsRoot, StringComparison.OrdinalIgnoreCase))
        {
            Runtime.Debug.LogWarning($"Rejected path '{relativePath}': it resolves outside the Assets folder.");
            return false;
        }
        return true;
    }

    /// <summary>Get a relative path from an absolute path, normalized.</summary>
    public string ToRelativePath(string absolutePath)
        => NormalizePath(Path.GetRelativePath(_project.AssetsPath, absolutePath));

    public void Dispose()
    {
        ShutdownBackgroundImport();
        _watcher?.Dispose();
        _watcher = null;
        _internalWriteRecords.Clear();
        _expiredInternalWritePaths.Clear();

        _buildPropsWatcher?.Dispose();
        _buildPropsWatcher = null;

        _manifestWatcher?.Dispose();
        _manifestWatcher = null;

        _packagesRootWatcher?.Dispose();
        _packagesRootWatcher = null;

        _editorModulesWatcher?.Dispose();
        _editorModulesWatcher = null;

        for (int i = 0; i < _packageMountWatchers.Count; i++)
            _packageMountWatchers[i].Dispose();
        _packageMountWatchers.Clear();

        // Closing or replacing a database unloads every resident asset. This is also the editor
        // shutdown path that pairs ScriptableObject.OnEnable with OnDisable.
        foreach (Guid guid in _loadedAssets.Keys)
            DisposeAndRemove(guid);

        ClearThumbnailTextureCache();

        // Clear the global registrations if they still point at this instance, so a torn-down
        // database (e.g. between tests) doesn't leave dangling statics behind.
        if (Instance == this) Instance = null;
        if (Runtime.AssetDatabase.Current == this) Runtime.AssetDatabase.Current = null;
        if (Instance == null) AssetDatabase.ResolveFamily = null;
    }
}
