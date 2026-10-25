// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.ComponentModel;

using Microsoft.Extensions.DependencyInjection;

using ModelContextProtocol.Server;

namespace XEngine.Ai.Host;

/// <summary>
/// Offline MCP tools for Adobe Photoshop documents. The layer tree without an editor, and pixels
/// an agent can look at — the front door of the PSD→UI pipeline.
/// </summary>
[McpServerToolType]
public static class PsdTools
{
    [McpServerTool(Name = "psd_inspect", UseStructuredContent = true)]
    [Description("Parse a .psd/.psb offline and report the layer tree: names (unicode), document-space rects, groups as nested children, visibility, opacity, and layer ids — plus document size, bit depth, color mode, and a contentHash for drift detection. This is the structural read that decides how a design maps to UI: run it the moment a generated PSD lands, before anything else.")]
    public static PsdInspectResult Inspect(
        IServiceProvider services,
        [Description("Project-relative .psd/.psb path (e.g. Art/shop.psd) or absolute.")] string documentPath)
        => services.GetRequiredService<PsdService>().Inspect(documentPath);

    [McpServerTool(Name = "psd_render", UseStructuredContent = true)]
    [Description("Render a .psd/.psb to PNG so you can LOOK at it — read the returned path back as an image. Renders the flattened composite by default (also the reference image a built screen is later diffed against), or one layer's own pixels via layerPath (group names joined with '/', as psd_inspect reports them). Layer names lie; only the pixels say which layer is the button and which is a stray effect.")]
    public static PsdRenderResult Render(
        IServiceProvider services,
        [Description("Project-relative .psd/.psb path or absolute.")] string documentPath,
        [Description("Layer to render, as a group path like Header/title. Omit for the flattened composite.")] string? layerPath = null,
        [Description("Project-relative output folder. Defaults to .xengine/psd/.")] string? outputFolder = null)
        => services.GetRequiredService<PsdService>().Render(documentPath, layerPath, outputFolder);

    [McpServerTool(Name = "xui_from_psd", UseStructuredContent = true)]
    [Description("Convert a layered .psd/.psb into a .xui screen plus exported sprite PNGs, driven by layer naming: btn_login → <Btn id=\"XBtnLogin\">, txt_title=登录 → <Text> with that content, input_account=账号 → <InputField> with that placeholder, img_* or any unprefixed pixel layer → <Image>, an unprefixed group → <Frame> with children. A btn_ group's txt_ child becomes the button's inline label instead of baked pixels. #skip anywhere in a name drops the layer. Everything is positioned absolutely in document coordinates — exact reproduction first; promote rows to VStack/HStack afterwards with xui_patch. Text color is sampled from the layer's dominant pixel and font-size approximated from its height (stated, not hidden). The result is linted before writing; nothing is silently dropped — hidden, #skip, and unmappable layers are all in the report. Run xui_scaffold next for the view binding.")]
    public static XuiFromPsdResult FromPsd(
        IServiceProvider services,
        [Description("Project-relative .psd/.psb path or absolute.")] string documentPath,
        [Description("Screen name. Default: the file name, PascalCased.")] string? screenName = null,
        [Description("Output folder under Assets/. Default UI/<screen-lowercase>/.")] string? outputFolder = null,
        [Description("Also convert hidden layers. Default false: hidden layers are reported and skipped.")] bool includeHidden = false,
        [Description("If true, report everything without writing any file.")] bool dryRun = false)
        => services.GetRequiredService<XuiFromPsdService>().Convert(
            documentPath, screenName, outputFolder, includeHidden, dryRun);
}

/// <summary>One layer in the tree. <c>Children</c> is null for anything that is not a group.</summary>
public sealed record PsdLayerInfo(
    string Name,
    bool IsGroup,
    int X,
    int Y,
    int Width,
    int Height,
    bool Visible,
    byte Opacity,
    int LayerId,
    IReadOnlyList<PsdLayerInfo>? Children);

public sealed record PsdInspectResult(
    string DocumentPath,
    int Width,
    int Height,
    int BitDepth,
    string ColorMode,
    int LayerCount,
    IReadOnlyList<PsdLayerInfo> Layers,
    string ContentHash);

/// <summary>
/// Where a render landed. <c>DocumentX</c>/<c>DocumentY</c> are the pixels' position in the
/// document, so a layer render can be placed back where it came from.
/// </summary>
public sealed record PsdRenderResult(
    string DocumentPath,
    string? LayerPath,
    string OutputPath,
    int Width,
    int Height,
    int DocumentX,
    int DocumentY);
