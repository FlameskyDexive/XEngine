using System;
using System.Collections.Generic;
using System.IO;

using XEngine.Async;
using XEngine.Runtime;
using XEngine.Runtime.Resources;
using XEngine.Vector;

namespace XEngine.VoxelWorld;

/// <summary>Host: packs the atlas, then streams Flat chunks around <see cref="Focus"/>.</summary>
[AddComponentMenu("Voxel World/Voxel World")]
public sealed class VoxelWorld : MonoBehaviour
{
    public static readonly Color GrassTop = new((byte)76, (byte)175, (byte)80);
    public static readonly Color Dirt = new((byte)141, (byte)110, (byte)99);
    public static readonly Color Stone = new((byte)158, (byte)158, (byte)158);
    public static readonly Color Leaf = new((byte)56, (byte)142, (byte)60);
    public static readonly Color Sand = new((byte)228, (byte)194, (byte)122);
    public static readonly Color Snow = new((byte)245, (byte)245, (byte)245);
    public static readonly Color Water = new((byte)40, (byte)120, (byte)190);
    public static readonly Color Wood = new((byte)110, (byte)72, (byte)40);
    public static readonly Color Cloud = new((byte)248, (byte)250, (byte)252);

    public AssetRef<WorldDefinition> World;
    public ushort PreviewVoxelType = 1;
    public ushort TerrainTypeIndex = 3;
    public ushort TerrainSurfaceTypeIndex = 2;
    public int ViewDistance = 2;
    public int BuildsPerFrame = 2;
    public bool EnableStreaming = true;
    public bool UseEarthGenerator;
    public string SaveFileName = "voxelworld";
    public Float3 Focus = new(8f, 50f, 8f);

    private VoxelChunk? _previewChunk;
    private GameObject? _previewObject;
    private FlatTerrainGenerator? _demoGenerator;
    private EarthTerrainGenerator? _earthGenerator;
    private bool _streaming;
    private VoxelRegionStore? _regionStore;

    public VoxelChunk? PreviewChunk => _previewChunk;
    public VoxelAtlas? Atlas { get; private set; }
    public Material? VoxelMaterial { get; private set; }
    public VoxelChunkStreamer Streamer { get; } = new();
    public VoxelWorldGrid Grid => Streamer.Grid;
    public VoxelInventory Inventory { get; } = CreateDefaultInventory();
    public VoxelWorldSave Save { get; } = new();
    public VoxelWorldMeta Meta { get; } = new();
    public VoxelRegionStore? RegionStore => _regionStore;
    public VoxelBreakFx BreakFx { get; } = new();
    public ushort LastBrokenType { get; private set; }
    public static VoxelWorld? Active { get; private set; }

    public static VoxelInventory CreateDefaultInventory()
    {
        var inventory = new VoxelInventory();
        inventory.Add(2, 32);
        inventory.Add(3, 32);
        inventory.Add(4, 64);
        inventory.Selected = 2;
        return inventory;
    }

    public VoxelAtlas EnsureAtlas()
    {
        if (Atlas != null)
            return Atlas;

        Atlas = CreateDemoAtlas();
        VoxelMaterial = VoxelShaderLibrary.CreateOpaqueMaterial(Atlas);
        return Atlas;
    }

    /// <summary>Apply a presentation atlas/material while retaining all voxel and collider state.</summary>
    public void ApplyPresentation(VoxelAtlas atlas, Material material)
    {
        ArgumentNullException.ThrowIfNull(atlas);
        ArgumentNullException.ThrowIfNull(material);
        Atlas = atlas;
        VoxelMaterial = material;
        Streamer.Atlas = atlas;
        Streamer.SetMaterial(material);
    }

    public static VoxelAtlas CreateDemoAtlas() =>
        TextureArrayPacker.PackPixelFaces(
        [
            (1, VoxelFaceTextures.White(), VoxelFaceTextures.White(), VoxelFaceTextures.White()),
            (2, VoxelFaceTextures.GrassTop(), VoxelFaceTextures.GrassSide(), VoxelFaceTextures.Dirt()),
            (3, VoxelFaceTextures.Dirt(), VoxelFaceTextures.Dirt(), VoxelFaceTextures.Dirt()),
            (4, VoxelFaceTextures.Stone(), VoxelFaceTextures.Stone(), VoxelFaceTextures.Stone()),
            (5, VoxelFaceTextures.Leaves(), VoxelFaceTextures.Leaves(), VoxelFaceTextures.Leaves()),
            (6, VoxelFaceTextures.Sand(), VoxelFaceTextures.Sand(), VoxelFaceTextures.Sand()),
            (7, VoxelFaceTextures.Snow(), VoxelFaceTextures.Snow(), VoxelFaceTextures.Snow()),
            (8, VoxelFaceTextures.Water(), VoxelFaceTextures.Water(), VoxelFaceTextures.Water()),
            (9, VoxelFaceTextures.Wood(), VoxelFaceTextures.Wood(), VoxelFaceTextures.Wood()),
            (10, VoxelFaceTextures.Cloud(), VoxelFaceTextures.Cloud(), VoxelFaceTextures.Cloud()),
            (14, VoxelFaceTextures.Forge(), VoxelFaceTextures.Forge(), VoxelFaceTextures.Stone()),
            (15, VoxelFaceTextures.Bed(), VoxelFaceTextures.Bed(), VoxelFaceTextures.Wood()),
            (16, VoxelFaceTextures.Campfire(), VoxelFaceTextures.Campfire(), VoxelFaceTextures.Wood()),
            (23, VoxelFaceTextures.Obelisk(), VoxelFaceTextures.Obelisk(), VoxelFaceTextures.Stone()),
        ], VoxelFaceTextures.Size);

    public void ConfigureStreamer()
    {
        EnsureAtlas();
        Streamer.Atlas = Atlas;
        Streamer.Material = VoxelMaterial;
        Streamer.Parent = GameObject;
        Streamer.ViewDistance = ViewDistance;
        Streamer.BuildsPerTick = BuildsPerFrame;
        Streamer.TerrainTypeIndex = TerrainTypeIndex;
        Streamer.Generator = ResolveGenerator();
        Streamer.MinChunkY = 0;
        int top = Streamer.Generator switch
        {
            FlatTerrainGenerator flat => flat.Altitude,
            EarthTerrainGenerator earth => Math.Max(earth.BaseHeight + earth.Amplitude + 4, earth.CloudAltitude + 8),
            _ => 50,
        };
        Streamer.MaxChunkY = VoxelCoords.FloorDiv(Math.Max(0, top - 1), VoxelCoords.ChunkSize);
    }

    public int TickStreaming(int maxBuilds)
    {
        ConfigureStreamer();
        Streamer.SetFocus(Focus);
        return Streamer.Tick(maxBuilds);
    }

    public bool TryRaycast(Float3 origin, Float3 direction, float maxDistance, out VoxelHit hit) =>
        VoxelDda.Cast(Streamer.IsSolid, origin, direction, maxDistance, out hit);

    public bool TryBreak(Float3 origin, Float3 direction, float maxDistance = 8f)
    {
        if (!TryRaycast(origin, direction, maxDistance, out VoxelHit hit))
            return false;
        if (!Streamer.TryGetVoxel(hit.Cell, out Voxel before) || before.IsEmpty)
            return false;
        if (!Streamer.TrySetVoxel(hit.Cell, Voxel.Empty))
            return false;
        LastBrokenType = before.TypeIndex;
        Inventory.Add(before.TypeIndex);
        BreakFx.Burst(
            new Float3(hit.Cell.X + 0.5f, hit.Cell.Y + 0.5f, hit.Cell.Z + 0.5f),
            FacePreview(before.TypeIndex),
            seed: hit.Cell.X * 31 + hit.Cell.Y * 17 + hit.Cell.Z);
        return true;
    }

    public bool TryPlace(Float3 origin, Float3 direction, ushort typeIndex, float maxDistance = 8f)
    {
        if (typeIndex == Voxel.EmptyTypeIndex)
            return false;
        if (!TryRaycast(origin, direction, maxDistance, out VoxelHit hit))
            return false;
        if (Streamer.TryGetVoxel(hit.PlacementCell, out Voxel existing) && !existing.IsEmpty)
            return false;
        if (Inventory.SelectedType != 0 && Inventory.SelectedType != typeIndex)
            typeIndex = Inventory.SelectedType;
        if (Inventory.SelectedType != 0 && Inventory.Counts[Inventory.Selected] <= 0)
            return false;
        if (!Streamer.TrySetVoxel(hit.PlacementCell, Voxel.Solid(typeIndex)))
            return false;
        if (Inventory.SelectedType != 0)
            Inventory.TryConsumeSelected();
        return true;
    }

    public string ResolveSavePath(string? path = null) =>
        path ?? Path.Combine(Application.PersistentDataPath, SaveFileName);

    public static Color FacePreview(ushort type) => type switch
    {
        2 => VoxelFaceTextures.Sample(VoxelFaceTextures.GrassTop(), VoxelFaceTextures.Size, 0.3f, 0.3f),
        3 => VoxelFaceTextures.Sample(VoxelFaceTextures.Dirt(), VoxelFaceTextures.Size, 0.3f, 0.3f),
        4 => VoxelFaceTextures.Sample(VoxelFaceTextures.Stone(), VoxelFaceTextures.Size, 0.3f, 0.3f),
        5 => VoxelFaceTextures.Sample(VoxelFaceTextures.Leaves(), VoxelFaceTextures.Size, 0.3f, 0.3f),
        6 => VoxelFaceTextures.Sample(VoxelFaceTextures.Sand(), VoxelFaceTextures.Size, 0.3f, 0.3f),
        7 => VoxelFaceTextures.Sample(VoxelFaceTextures.Snow(), VoxelFaceTextures.Size, 0.3f, 0.3f),
        8 => VoxelFaceTextures.Sample(VoxelFaceTextures.Water(), VoxelFaceTextures.Size, 0.3f, 0.3f),
        9 => VoxelFaceTextures.Sample(VoxelFaceTextures.Wood(), VoxelFaceTextures.Size, 0.3f, 0.3f),
        10 => VoxelFaceTextures.Sample(VoxelFaceTextures.Cloud(), VoxelFaceTextures.Size, 0.3f, 0.3f),
        _ => Color.White,
    };

    public void WriteSave(string? path = null)
    {
        string root = ResolveSavePath(path);
        EnsureRegionStore(root);
        Meta.Version = VoxelWorldMeta.FormatVersion;
        Meta.MinChunkY = Grid.MinChunkY;
        Meta.MaxChunkY = Grid.MaxChunkY;
        Meta.GeneratorKind = UseEarthGenerator ? "earth" : "flat";
        if (World.ResWeak is WorldDefinition def)
            Meta.Seed = def.Seed;
        Directory.CreateDirectory(root);
        VoxelWorldMeta.WriteFile(Path.Combine(root, "world.echo"), Meta);
        Grid.FlushEdited();
    }

    public void ReadSave(string? path = null) => ReadSave(path, preload: true);

    public void ReadSave(string? path, bool preload)
    {
        string root = ResolveSavePath(path);
        if (File.Exists(root) && root.EndsWith(".echo", StringComparison.OrdinalIgnoreCase))
        {
            MigrateLegacyEchoFile(root);
            return;
        }

        string metaPath = Path.Combine(root, "world.echo");
        string regionDir = Path.Combine(root, "region");
        if (Directory.Exists(regionDir) || File.Exists(metaPath))
        {
            if (File.Exists(metaPath))
            {
                VoxelWorldMeta loaded = VoxelWorldMeta.ReadFile(metaPath);
                Meta.Version = loaded.Version;
                Meta.Seed = loaded.Seed;
                Meta.MinChunkY = loaded.MinChunkY;
                Meta.MaxChunkY = loaded.MaxChunkY;
                Meta.GeneratorKind = loaded.GeneratorKind;
                Grid.MinChunkY = loaded.MinChunkY;
                Grid.MaxChunkY = loaded.MaxChunkY;
            }

            EnsureRegionStore(root);
            Float3 focus = Focus;
            Streamer.UnloadAll();
            Streamer.SetFocus(focus);
            if (!preload) return;
            int budget = Math.Max(8, Streamer.WantedCount);
            while (Streamer.QueueCount > 0 || Streamer.LoadedCount < Streamer.WantedCount)
            {
                if (Streamer.Tick(budget) == 0 && Streamer.QueueCount == 0)
                    break;
            }
            Streamer.SyncViews();
            return;
        }

        if (File.Exists(root))
            MigrateLegacyEchoFile(root);
    }

    private void EnsureRegionStore(string root)
    {
        _regionStore = new VoxelRegionStore(root)
        {
            MinChunkY = Grid.MinChunkY,
            MaxChunkY = Grid.MaxChunkY,
        };
        Grid.Store = _regionStore;
    }

    private void MigrateLegacyEchoFile(string echoPath)
    {
        VoxelWorldSave loaded = VoxelWorldSave.ReadFile(echoPath);
        Save.Version = loaded.Version;
        Save.Edits.Clear();
        Save.Edits.AddRange(loaded.Edits);
        Save.Apply(Streamer);

        string root = Path.Combine(Path.GetDirectoryName(Path.GetFullPath(echoPath))!,
            Path.GetFileNameWithoutExtension(echoPath));
        if (root.Equals(echoPath, StringComparison.OrdinalIgnoreCase))
            root = echoPath + ".world";
        EnsureRegionStore(root);
        Meta.Version = VoxelWorldMeta.FormatVersion;
        Meta.MinChunkY = Grid.MinChunkY;
        Meta.MaxChunkY = Grid.MaxChunkY;
        Meta.GeneratorKind = UseEarthGenerator ? "earth" : "flat";
        Directory.CreateDirectory(root);
        VoxelWorldMeta.WriteFile(Path.Combine(root, "world.echo"), Meta);
        Grid.FlushEdited();
    }

    public void BuildSolidPreviewChunk()
    {
        EnsureAtlas();
        ClearPreview();

        _previewChunk = VoxelChunkPool.Rent();
        _previewChunk.Coord = Int3.Zero;
        _previewChunk.FillSolid(PreviewVoxelType);
        AttachPreviewObject(_previewChunk.RebuildMesh(Atlas), "VoxelChunk 0,0,0");
    }

    public void BuildMultiFacePreview()
    {
        EnsureAtlas();
        ClearPreview();

        _previewChunk = VoxelChunkPool.Rent();
        _previewChunk.Coord = Int3.Zero;
        _previewChunk.SetVoxel(VoxelCoords.Index(1, 1, 1), Voxel.Solid(2));
        _previewChunk.SetVoxel(VoxelCoords.Index(5, 1, 1), Voxel.Solid(3));
        _previewChunk.SetVoxel(VoxelCoords.Index(9, 1, 1), Voxel.Solid(4));
        AttachPreviewObject(_previewChunk.RebuildMesh(Atlas), "VoxelAtlas Preview");
    }

    public override void OnEnable()
    {
        Active = this;
        if (GameObject != null && GameObject.GetComponent<VoxelInventoryHud>() == null)
            GameObject.AddComponent<VoxelInventoryHud>().World = this;
        if (EnableStreaming)
        {
            ConfigureStreamer();
            Streamer.SetFocus(Focus);
            AimCameraAtFocus();
            StreamLoop().Forget();
            return;
        }

        if (GameObject != null && _previewChunk == null)
            BuildMultiFacePreview();
    }

    public override void Update()
    {
        if (EnableStreaming && _streaming)
            Streamer.SetFocus(Focus);
    }

    public override void OnDisable()
    {
        if (Active == this)
            Active = null;
        _streaming = false;
        Streamer.UnloadAll();
        ClearPreview();
    }

    private void AimCameraAtFocus()
    {
        if (Scene.Current == null)
            return;
        var cameras = new List<Camera>();
        Scene.Current.CollectEnabledCameras(cameras);
        if (cameras.Count == 0)
            return;
        Camera cam = cameras[0];
        Float3 dir = Focus - cam.Transform.Position;
        if (Float3.LengthSquared(dir) < 1e-4f)
            return;
        cam.Transform.Rotation = Quaternion.LookRotation(dir, new Float3(0f, 1f, 0f));
    }

    private VoxelTerrainGenerator ResolveGenerator()
    {
        if (World.ResWeak is WorldDefinition def && def.TerrainGenerator.ResWeak is VoxelTerrainGenerator packaged)
            return packaged;

        if (UseEarthGenerator)
            return _earthGenerator ??= new EarthTerrainGenerator();

        _demoGenerator ??= new FlatTerrainGenerator
        {
            Altitude = 50,
            SurfaceTypeIndex = TerrainSurfaceTypeIndex,
        };
        _demoGenerator.SurfaceTypeIndex = TerrainSurfaceTypeIndex;
        return _demoGenerator;
    }

    private async XTaskVoid StreamLoop()
    {
        _streaming = true;
        try
        {
            while (_streaming && this.IsValid())
            {
                Streamer.Tick(BuildsPerFrame);
                await XTask.Yield(FrameTiming.Update);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    private void AttachPreviewObject(VoxelMeshBuild build, string name)
    {
        _previewObject = new GameObject(name);
        _previewObject.SetParent(GameObject, worldPositionStays: false);
        var renderer = _previewObject.AddComponent<MeshRenderer>();
        renderer.Mesh = new AssetRef<Mesh>(_previewChunk!.Mesh);
        renderer.Material = new AssetRef<Material>(VoxelMaterial);
        _ = build;
    }

    private void ClearPreview()
    {
        if (_previewChunk != null)
        {
            VoxelChunkPool.Return(_previewChunk);
            _previewChunk = null;
        }

        if (_previewObject != null)
        {
            EngineObject.Destroy(_previewObject);
            _previewObject = null;
        }
    }
}
