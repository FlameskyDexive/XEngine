// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;

using PaintDotNet.Data.PhotoshopFileType;
using PDNWrapper;

using XEngine.Ai.Protocol;

namespace XEngine.Ai.Host;

/// <summary>
/// Offline PSD tools: the layer tree without an editor, and pixels an agent can look at.
/// </summary>
/// <remarks>
/// <para>
/// This is the front door of the PSD→UI pipeline. A generated document's first problems are
/// structural — wrong grouping, unnamed layers, a stray artboard — and every one of them is
/// visible in the inspect output seconds after the file lands, without booting anything.
/// </para>
/// <para>
/// Render exists because layer names lie: the only way an agent can decide "this layer is the
/// button background, that one is a stray effect" is to look at the pixels. The composite render
/// also becomes the reference image a built screen is later diffed against.
/// </para>
/// </remarks>
internal sealed class PsdService
{
    /// <summary>A hard ceiling on rendered output, matching the pxl renderer's guard.</summary>
    private const int MaxRenderPixels = 8192;

    private readonly ProjectDescriptor _project;

    public PsdService(ProjectDescriptor project)
    {
        _project = project;
    }

    // ------------------------------------------------------------------
    //  Inspect
    // ------------------------------------------------------------------

    public PsdInspectResult Inspect(string documentPath)
    {
        string absolute = Resolve(documentPath);
        byte[] bytes = File.ReadAllBytes(absolute);

        Document document = Load(bytes, out PhotoshopFile.PsdFile file);

        var roots = new List<PsdLayerInfo>();
        int total = 0;
        foreach (BitmapLayer layer in document.Layers)
            roots.Add(Describe(layer, ref total));

        return new PsdInspectResult(
            documentPath,
            document.width,
            document.height,
            file.BitDepth,
            file.ColorMode.ToString(),
            total,
            roots,
            Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant());
    }

    private static PsdLayerInfo Describe(BitmapLayer layer, ref int total)
    {
        total++;
        List<PsdLayerInfo>? children = null;
        if (layer.IsGroup)
        {
            children = new List<PsdLayerInfo>();
            foreach (BitmapLayer child in layer.ChildLayer)
                children.Add(Describe(child, ref total));
        }

        return new PsdLayerInfo(
            layer.Name ?? string.Empty,
            layer.IsGroup,
            layer.documentRect.X,
            layer.documentRect.Y,
            layer.documentRect.Width,
            layer.documentRect.Height,
            layer.Visible,
            layer.Opacity,
            layer.LayerID,
            children);
    }

    // ------------------------------------------------------------------
    //  Render
    // ------------------------------------------------------------------

    public PsdRenderResult Render(string documentPath, string? layerPath, string? outputFolder)
    {
        string absolute = Resolve(documentPath);
        Document document = Load(File.ReadAllBytes(absolute), out PhotoshopFile.PsdFile file);

        Surface surface;
        Rectangle rect;
        string suffix;
        if (layerPath == null)
        {
            if (document.width <= 0 || document.height <= 0
                || (long)document.width * document.height > (long)MaxRenderPixels * MaxRenderPixels)
            {
                throw new InvalidOperationException(
                    "Document is " + document.width + "x" + document.height + "; renderable size is 1.."
                    + MaxRenderPixels + " per axis.");
            }

            file.BaseLayer.CreateMissingChannels();
            BitmapLayer flattened = PDNWrapper.Layer.CreateBackgroundLayer(document.width, document.height);
            ImageDecoderPdn.DecodeImage(flattened, file.BaseLayer);
            surface = flattened.Surface;
            rect = new Rectangle(0, 0, document.width, document.height);
            suffix = "composite";
        }
        else
        {
            BitmapLayer layer = FindLayer(document.Layers, layerPath, string.Empty)
                ?? throw new KeyNotFoundException(
                    "Layer '" + layerPath + "' is not in " + documentPath
                    + ". Call psd_inspect to list the tree; the path is group names joined with '/'.");
            if (layer.IsGroup)
            {
                throw new InvalidOperationException(
                    "'" + layerPath + "' is a group. Render one of its layers, or the whole composite.");
            }
            if (layer.IsEmpty)
                throw new InvalidOperationException("Layer '" + layerPath + "' has no pixels.");

            surface = layer.Surface;
            rect = layer.documentRect;
            suffix = SafeFileName(layerPath);
        }

        var rgba = new byte[surface.width * surface.height * 4];
        Color32[] pixels = surface.color;
        for (int i = 0; i < pixels.Length; i++)
        {
            rgba[i * 4] = pixels[i].r;
            rgba[i * 4 + 1] = pixels[i].g;
            rgba[i * 4 + 2] = pixels[i].b;
            rgba[i * 4 + 3] = pixels[i].a;
        }

        string folder = outputFolder != null
            ? _project.ResolveAssetPath(outputFolder)
            : Path.Combine(_project.ProjectRoot, ".xengine", "psd");
        Directory.CreateDirectory(folder);

        string fileName = Path.GetFileNameWithoutExtension(absolute) + "-" + suffix + ".png";
        string outputPath = Path.Combine(folder, fileName);
        File.WriteAllBytes(outputPath, PngRgba.Encode(rgba, surface.width, surface.height));

        return new PsdRenderResult(
            documentPath,
            layerPath,
            Relative(outputPath),
            surface.width,
            surface.height,
            rect.X,
            rect.Y);
    }

    private static BitmapLayer? FindLayer(IEnumerable<BitmapLayer> layers, string wanted, string prefix)
    {
        foreach (BitmapLayer layer in layers)
        {
            string name = string.IsNullOrWhiteSpace(layer.Name) ? "Layer" : layer.Name.Trim().Replace('/', '_');
            string path = prefix.Length == 0 ? name : prefix + "/" + name;
            if (string.Equals(path, wanted, StringComparison.Ordinal) && !layer.IsGroup) return layer;
            if (layer.IsGroup)
            {
                if (string.Equals(path, wanted, StringComparison.Ordinal)) return layer;
                if (FindLayer(layer.ChildLayer, wanted, path) is { } found) return found;
            }
        }
        return null;
    }

    // ------------------------------------------------------------------
    //  Plumbing
    // ------------------------------------------------------------------

    private static Document Load(byte[] bytes, out PhotoshopFile.PsdFile file)
    {
        using var stream = new MemoryStream(bytes);
        try
        {
            return PsdLoad.Load(stream, out file);
        }
        catch (Exception ex) when (ex is EndOfStreamException or ArgumentException)
        {
            // The parser throws PsdInvalidException for format errors it recognises; truncated or
            // hostile files can surface as raw stream errors, which deserve the same clear message.
            throw new PhotoshopFile.PsdInvalidException("The file is not a readable PSD: " + ex.Message);
        }
    }

    private string Resolve(string documentPath)
    {
        bool isPsd = documentPath.EndsWith(".psd", StringComparison.OrdinalIgnoreCase)
            || documentPath.EndsWith(".psb", StringComparison.OrdinalIgnoreCase);
        if (!isPsd)
            throw new ArgumentException("psd tools require a .psd or .psb path, got '" + documentPath + "'.");

        string absolute = _project.ResolveAssetPath(documentPath);
        if (!File.Exists(absolute))
            throw new FileNotFoundException("PSD document not found: " + documentPath, absolute);
        return absolute;
    }

    private static string SafeFileName(string layerPath)
    {
        var builder = new System.Text.StringBuilder(layerPath.Length);
        foreach (char c in layerPath)
            builder.Append(char.IsLetterOrDigit(c) ? c : '-');
        return builder.ToString();
    }

    private string Relative(string absolute)
    {
        try
        {
            return Path.GetRelativePath(_project.ProjectRoot, absolute).Replace(Path.DirectorySeparatorChar, '/');
        }
        catch (ArgumentException)
        {
            return absolute;
        }
    }
}
