using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

using XEngine.Ai.Protocol;
using XEngine.Echo;

using ChangeProvenance = XEngine.Data.Provenance.ChangeProvenance;

namespace XEngine.Ai.Host;

/// <summary>
/// ★ Gen-P2.3 — Grid-slice a PNG into TextureImporter Multiple sprites (same naming + GUID
/// scheme the editor uses) and bind <c>SpriteRenderer</c> + <c>SpriteFlipbook</c> on a GameObject.
/// Host cannot run the editor importer; GUIDs are derived offline so scenes can wire before
/// <c>--refresh-assets</c> materializes Library/cache sub-assets.
/// </summary>
internal sealed class SpriteWireService
{
    private readonly ProjectDescriptor _project;
    private readonly SceneWriter _sceneWriter;

    public SpriteWireService(ProjectDescriptor project, SceneWriter sceneWriter)
    {
        _project = project;
        _sceneWriter = sceneWriter;
    }

    public async Task<SpriteSliceResult> SliceAsync(
        string assetPath,
        int columns,
        int rows,
        int cellWidth,
        int cellHeight,
        float pixelsPerUnit,
        string idempotencyKey,
        string? intent,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        string abs = _project.ResolveAssetPath(assetPath);
        if (!File.Exists(abs))
        {
            throw new FileNotFoundException($"Texture not found: {assetPath}");
        }

        if (!TryReadPngSize(abs, out int texW, out int texH))
        {
            throw new InvalidOperationException(
                $"'{assetPath}' is not a PNG with a readable IHDR. sprite_slice only grids PNG sheets.");
        }

        string metaPath = abs + ".meta";
        if (!File.Exists(metaPath))
        {
            throw new FileNotFoundException(
                $"No .meta for '{assetPath}'. Import first (gen_fetch / asset_import mints the sidecar).");
        }

        EchoObject meta = EchoObject.ReadFromString(File.ReadAllText(metaPath));
        string guidText = meta.TryGet("guid", out EchoObject? g) && g!.TagType == EchoType.String ? g.StringValue : "";
        if (!Guid.TryParse(guidText, out Guid parentGuid))
        {
            throw new InvalidOperationException($"The .meta of '{assetPath}' has no parseable guid ('{guidText}').");
        }

        int cols = Math.Max(1, columns);
        int rowCount = Math.Max(1, rows);
        int cellW = cellWidth > 0 ? cellWidth : Math.Max(1, texW / cols);
        int cellH = cellHeight > 0 ? cellHeight : Math.Max(1, texH / rowCount);
        if (cellWidth <= 0)
            cols = Math.Max(1, texW / cellW);
        if (cellHeight <= 0)
            rowCount = Math.Max(1, texH / cellH);

        string baseName = Path.GetFileNameWithoutExtension(assetPath);
        var frames = SpriteGridSlicer.Slice(texW, texH, cellW, cellH, baseName, parentGuid);
        var warnings = new List<string>();
        if (frames.Count == 0)
        {
            warnings.Add("Grid produced zero slices — check columns/rows/cell size against the PNG dimensions.");
        }

        if (dryRun)
        {
            return new SpriteSliceResult(assetPath, parentGuid.ToString(), texW, texH,
                cols, rowCount, cellW, cellH, frames, true, warnings);
        }

        await _sceneWriter.MutateAssetDataAsync(
            metaPath, assetPath + ".meta",
            root => WriteSpriteSettings(root, frames, cellW, cellH, cols, rowCount, pixelsPerUnit),
            $"Sliced {frames.Count} sprite(s) on {assetPath} ({cols}×{rowCount} grid).",
            ChangeProvenance.ForTool("xengine-cli.sprite-slice",
                intent ?? $"grid-slice {assetPath}", idempotencyKey),
            dryRun: false, expectedBeforeHash: null, cancellationToken).ConfigureAwait(false);

        return new SpriteSliceResult(assetPath, parentGuid.ToString(), texW, texH,
            cols, rowCount, cellW, cellH, frames, false, warnings);
    }

    public async Task<SpriteWireResult> WireAsync(
        string scenePath,
        string objectIdentifier,
        string assetPath,
        int columns,
        int rows,
        int cellWidth,
        int cellHeight,
        float pixelsPerUnit,
        float framesPerSecond,
        string idempotencyKey,
        string? intent,
        bool dryRun,
        CancellationToken cancellationToken)
    {
        SpriteSliceResult slice = await SliceAsync(
            assetPath, columns, rows, cellWidth, cellHeight, pixelsPerUnit,
            idempotencyKey + "-slice", intent, dryRun, cancellationToken).ConfigureAwait(false);

        float fps = framesPerSecond > 0f ? framesPerSecond : 12f;
        if (dryRun || slice.Frames.Count == 0)
        {
            return new SpriteWireResult(scenePath, objectIdentifier, "", "", fps, slice, dryRun);
        }

        SceneWriteResult renderer = await _sceneWriter.AddComponentAsync(
            _project.ResolveAssetPath(scenePath), scenePath, objectIdentifier, "SpriteRenderer",
            new[] { new SceneFieldPatch("Sprite/AssetID", slice.Frames[0].Guid) },
            ChangeProvenance.ForTool("xengine-cli.sprite-wire",
                intent ?? $"sprite renderer from {assetPath}", idempotencyKey + "-sr"),
            dryRun: false, expectedBeforeHash: null, cancellationToken).ConfigureAwait(false);

        var fields = new List<SceneFieldPatch>(slice.Frames.Count + 2)
        {
            new("FramesPerSecond", fps.ToString("0.###", CultureInfo.InvariantCulture)),
            new("Loop", "true"),
        };
        for (int i = 0; i < slice.Frames.Count; i++)
        {
            fields.Add(new SceneFieldPatch($"Frames/$values/{i}/AssetID", slice.Frames[i].Guid));
        }

        SceneWriteResult flipbook = await _sceneWriter.AddComponentAsync(
            _project.ResolveAssetPath(scenePath), scenePath, objectIdentifier, "SpriteFlipbook",
            fields,
            ChangeProvenance.ForTool("xengine-cli.sprite-wire",
                intent ?? $"flipbook from {assetPath}", idempotencyKey + "-fb"),
            dryRun: false, expectedBeforeHash: null, cancellationToken).ConfigureAwait(false);

        return new SpriteWireResult(
            scenePath, objectIdentifier,
            renderer.CreatedIdentifier ?? "", flipbook.CreatedIdentifier ?? "",
            fps, slice, false);
    }

    internal static bool TryReadPngSize(string path, out int width, out int height)
    {
        width = 0;
        height = 0;
        using FileStream fs = File.OpenRead(path);
        Span<byte> hdr = stackalloc byte[24];
        int n = fs.Read(hdr);
        if (n < 24)
            return false;
        if (hdr[0] != 137 || hdr[1] != 80 || hdr[2] != 78 || hdr[3] != 71)
            return false;
        width = (hdr[16] << 24) | (hdr[17] << 16) | (hdr[18] << 8) | hdr[19];
        height = (hdr[20] << 24) | (hdr[21] << 16) | (hdr[22] << 8) | hdr[23];
        return width > 0 && height > 0;
    }

    private static void WriteSpriteSettings(
        EchoObject meta,
        IReadOnlyList<SpriteSliceFrame> frames,
        int cellW, int cellH, int cols, int rows,
        float pixelsPerUnit)
    {
        EchoObject settings = EnsureCompound(meta, "settings");
        settings["generateMipmaps"] = new EchoObject(false);
        settings["sRGB"] = new EchoObject(true);
        settings["minFilter"] = new EchoObject(1L);
        settings["magFilter"] = new EchoObject(1L);

        EchoObject sprite = EchoObject.NewCompound();
        sprite["Mode"] = new EchoObject(2L);          // SpriteMode.Multiple
        sprite["PixelsPerUnit"] = new EchoObject(pixelsPerUnit <= 0f ? 100f : pixelsPerUnit);
        sprite["GenerateTightMesh"] = new EchoObject(false);
        sprite["SlicingTool"] = new EchoObject(1L);   // GridBySize
        sprite["GeneratedPivot"] = new EchoObject(0L); // Center
        sprite["KeepEmptyRects"] = new EchoObject(true);

        EchoObject cell = EchoObject.NewCompound();
        cell["X"] = new EchoObject(cellW);
        cell["Y"] = new EchoObject(cellH);
        sprite["GridCellSize"] = cell;

        EchoObject count = EchoObject.NewCompound();
        count["X"] = new EchoObject(cols);
        count["Y"] = new EchoObject(rows);
        sprite["GridCellCount"] = count;

        EchoObject slices = EchoObject.NewList();
        for (int i = 0; i < frames.Count; i++)
        {
            SpriteSliceFrame f = frames[i];
            EchoObject slice = EchoObject.NewCompound();
            slice["Name"] = new EchoObject(f.Name);
            EchoObject rect = EchoObject.NewList();
            rect.ListAdd(new EchoObject(f.X));
            rect.ListAdd(new EchoObject(f.Y));
            rect.ListAdd(new EchoObject(f.Width));
            rect.ListAdd(new EchoObject(f.Height));
            slice["Rect"] = rect;
            slice["Alignment"] = new EchoObject(0L);
            EchoObject pivot = EchoObject.NewCompound();
            pivot["X"] = new EchoObject(0.5f);
            pivot["Y"] = new EchoObject(0.5f);
            slice["CustomPivot"] = pivot;
            slice["PivotUnit"] = new EchoObject(0L);
            slices.ListAdd(slice);
        }

        EchoObject slicesWrap = EchoObject.NewCompound();
        slicesWrap["$values"] = slices;
        sprite["Slices"] = slicesWrap;
        settings["sprite"] = sprite;
    }

    private static EchoObject EnsureCompound(EchoObject parent, string key)
    {
        if (parent.TryGet(key, out EchoObject? existing) && existing!.TagType == EchoType.Compound)
            return existing;
        EchoObject made = EchoObject.NewCompound();
        parent[key] = made;
        return made;
    }
}

/// <summary>
/// Offline clone of the editor's <c>SpriteSlicer.GridCells</c> (top-left walk, bottom-left
/// <c>SpriteRect</c>, names <c>{base}_{i}</c>). KeepEmpty is always true — Host has no GPU alpha.
/// </summary>
internal static class SpriteGridSlicer
{
    public static List<SpriteSliceFrame> Slice(
        int texW, int texH, int cellW, int cellH, string baseName, Guid parentGuid)
    {
        var result = new List<SpriteSliceFrame>();
        cellW = Math.Max(1, cellW);
        cellH = Math.Max(1, cellH);
        for (int yTL = 0; yTL + 1 <= texH; yTL += cellH)
        {
            int h = Math.Min(cellH, texH - yTL);
            if (h <= 0) break;
            for (int xTL = 0; xTL + 1 <= texW; xTL += cellW)
            {
                int w = Math.Min(cellW, texW - xTL);
                if (w <= 0) break;
                int x = xTL;
                int y = texH - yTL - h;
                string name = baseName + "_" + result.Count.ToString(CultureInfo.InvariantCulture);
                string guid = SubAssetResolver.DeriveSubAssetGuid(parentGuid, name).ToString();
                result.Add(new SpriteSliceFrame(name, guid, x, y, w, h));
            }
        }

        return result;
    }
}
