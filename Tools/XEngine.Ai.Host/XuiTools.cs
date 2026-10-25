// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.ComponentModel;

using Microsoft.Extensions.DependencyInjection;
using ModelContextProtocol.Server;
using XEngine.Ai.Protocol;

namespace XEngine.Ai.Host;

/// <summary>
/// Offline MCP tools for the <c>com.xengine.xui</c> screen language. No editor required: the parser and
/// lint rules are the package's own sources, compiled into this Host, so a document that validates here
/// is the document the runtime assembler will accept.
/// </summary>
[McpServerToolType]
public static class XuiTools
{
    [McpServerTool(Name = "xui_validate", UseStructuredContent = true)]
    [Description("Parse and lint a .xui screen document offline. Returns every finding at once with line/column and a stable rule code (XUI-TAG-UNKNOWN, XUI-ID-PREFIX, XUI-ID-DUPLICATE, XUI-ATTR-UNKNOWN, XUI-ATTR-VALUE, XUI-COLOR-TOKEN, XUI-LAYOUT-CHILD-RECT, ...). Valid=false means the screen will not do what the document says. Run this after every .xui write — it needs no running editor.")]
    public static XuiValidateResult Validate(
        IServiceProvider services,
        [Description("Project-relative .xui path (e.g. UI/login/Login.xui) or absolute.")] string documentPath,
        [Description("Include warnings as well as errors. Default true.")] bool includeWarnings = true)
        => services.GetRequiredService<XuiService>().Validate(documentPath, includeWarnings);

    [McpServerTool(Name = "xui_inspect", UseStructuredContent = true)]
    [Description("Summarise a .xui document: its screens (with canvas/layer/scale-mode/reference), the element tree after template expansion with each id's generated binding property and type, declared templates, theme tokens, imports, and the sprite/font keys it references. Returns a contentHash to pass to xui_patch as expectedBeforeHash.")]
    public static XuiInspectResult Inspect(
        IServiceProvider services,
        [Description("Project-relative .xui path or absolute.")] string documentPath,
        [Description("Optional screen name to restrict the element listing to.")] string? screenName = null)
        => services.GetRequiredService<XuiService>().Inspect(documentPath, screenName);

    [McpServerTool(Name = "xui_scaffold", UseStructuredContent = true)]
    [Description("Generate the C# view binding for a .xui screen, plus a controller skeleton. Writes {Screen}View.g.cs (always overwritten: it is derived from the document) and {Screen}.cs (written once, never overwritten — that is where logic lives). The binding exposes one typed property per id, so business code reads View.BtnLogin and a renamed id becomes a compile error instead of a runtime null. Refuses to run on a document with lint errors.")]
    public static XuiScaffoldResult Scaffold(
        IServiceProvider services,
        [Description("Project-relative .xui path or absolute.")] string documentPath,
        [Description("Screen name declared in the document.")] string screenName,
        [Description("Project-relative folder for the generated files (e.g. Scripts/UI/Login).")] string outputFolder,
        [Description("Namespace for both files. Omit for none.")] string? namespaceName = null,
        [Description("Overwrite an existing controller. Default false — it holds hand-written logic.")] bool overwriteController = false,
        [Description("If true, report what would be written without writing.")] bool dryRun = false)
        => services.GetRequiredService<XuiService>().Scaffold(
            documentPath, screenName, outputFolder, namespaceName, overwriteController, dryRun);

    [McpServerTool(Name = "xui_i18n_extract", UseStructuredContent = true)]
    [Description("Extract every translatable string from .xui documents and Xui.Tr(\"...\") calls in C#, and merge them into one .po catalog per locale. Merging never destroys work: existing translations are kept, strings no source mentions any more are marked obsolete rather than deleted, and a string that comes back is revived with its translation. The source string is the key, so a project that never runs this still displays correctly — the catalog is an overlay, not a prerequisite. Use translate=\"false\" on an element (inherited by its subtree) to keep a product name or a debug label out of the catalog.")]
    public static XuiI18nExtractResult I18nExtract(
        IServiceProvider services,
        [Description("Locales to write, e.g. [\"zh-Hans\", \"ja\"]. One .po per locale.")] string[] locales,
        [Description("Folder under Assets/ holding the .po files. Default UI/Locales.")] string outputFolder = "UI/Locales",
        [Description("Folder under Assets/ to scan for .xui documents. Default: all of Assets/.")] string? documentFolder = null,
        [Description("Folder under Assets/ to scan for C# calling Xui.Tr. Default: all of Assets/.")] string? scriptFolder = null,
        [Description("If true, report the merge without writing any file.")] bool dryRun = false)
        => services.GetRequiredService<XuiI18nService>().Extract(
            documentFolder, scriptFolder, locales, outputFolder, dryRun);

    [McpServerTool(Name = "xui_patch", UseStructuredContent = true)]
    [Description("Patch a .xui document in place, preserving indentation and comments. Patch paths are '<id>/@<attribute>' (e.g. XBtnLogin/@color), '<id>/#text' for inline text, or 'screen:<Name>/@<attribute>' (e.g. screen:Login/@layer). Ids created by template expansion are not addressable — edit the template instead. The result is re-parsed before writing, so a patch that would break the document is rejected rather than saved. Same drift protection (expectedBeforeHash), idempotency, and provenance journal as asset_data_patch.")]
    public static Task<SceneWriteResult> Patch(
        IServiceProvider services,
        [Description("Project-relative .xui path or absolute.")] string documentPath,
        [Description("Attribute/text sets: each has a Path (e.g. XBtnLogin/@color) and a Value.")] SceneFieldPatch[] patches,
        [Description("Stable idempotency key (1-128 chars).")] string idempotencyKey,
        [Description("Natural-language intent of the change, recorded in the journal.")] string? intent = null,
        [Description("Paths to remove, same grammar as patches. Use for clearing a dead attribute such as an anchor under a layout container.")] string[]? removePaths = null,
        [Description("Optional SHA-256 contentHash from xui_inspect. Rejects the write if the document changed since it was read.")] string? expectedBeforeHash = null,
        [Description("If true, compute and report without writing.")] bool dryRun = false,
        CancellationToken cancellationToken = default)
        => services.GetRequiredService<XuiService>().PatchAsync(
            documentPath,
            patches,
            removePaths,
            XEngine.Data.Provenance.ChangeProvenance.ForTool("xengine-cli.xui-patch", intent, idempotencyKey),
            dryRun,
            expectedBeforeHash,
            cancellationToken);
}

/// <summary>
/// Offline MCP tools for the <c>.pxl</c> pixel-grid sprite format. Text in, PNG out — so an agent can
/// author UI art and then look at what it authored.
/// </summary>
[McpServerToolType]
public static class PxlTools
{
    [McpServerTool(Name = "pxl_validate", UseStructuredContent = true)]
    [Description("Parse and check a .pxl pixel-grid sprite file offline. Reports each section's size and 9-slice border, characters used but never declared (they render transparent), colour names no palette resolved, and declared characters nothing uses. Parse errors name the line.")]
    public static PxlValidateResult Validate(
        IServiceProvider services,
        [Description("Project-relative .pxl path (e.g. UI/art/button.pxl) or absolute.")] string documentPath)
        => services.GetRequiredService<PxlService>().Validate(documentPath);

    [McpServerTool(Name = "pxl_render", UseStructuredContent = true)]
    [Description("Render a .pxl file to PNG so you can LOOK at it — read the returned path back as an image. Checking the characters proves the file parses; only the render catches a bevel pointing the wrong way. Upscaling is nearest-neighbour, so single-pixel mistakes stay visible. Renders every section unless sectionName is given.")]
    public static PxlRenderResult Render(
        IServiceProvider services,
        [Description("Project-relative .pxl path or absolute.")] string documentPath,
        [Description("Section to render. Omit to render all.")] string? sectionName = null,
        [Description("Nearest-neighbour upscale, 1-32. Default 8 — small sprites are unreadable at 1:1.")] int scale = 8,
        [Description("Project-relative output folder. Defaults to .xengine/pxl/.")] string? outputFolder = null)
        => services.GetRequiredService<PxlService>().Render(documentPath, sectionName, scale, outputFolder);
}

/// <summary>One problem found in a .pxl file. Line 0 means the file as a whole.</summary>
public sealed record PxlIssueInfo(string Code, string Message, int Line);

/// <summary>One grid in a .pxl file. Border is "top,right,bottom,left", or null when there is none.</summary>
public sealed record PxlSectionInfo(string Name, int Width, int Height, string? Border, int Line);

public sealed record PxlValidateResult(
    string DocumentPath,
    bool Valid,
    int SectionCount,
    float PixelsPerUnit,
    string? PaletteName,
    IReadOnlyList<PxlSectionInfo> Sections,
    IReadOnlyList<string> UnusedChars,
    IReadOnlyList<PxlIssueInfo> Issues);

public sealed record PxlRenderedImage(string Section, string Path, int Width, int Height);

public sealed record PxlRenderResult(
    string DocumentPath,
    int Scale,
    IReadOnlyList<PxlRenderedImage> Images);

/// <summary>One lint or parse finding, with the source position that produced it.</summary>
public sealed record XuiIssueInfo(
    string Code,
    string Severity,
    string Message,
    int Line,
    int Column,
    string? ElementId);

/// <summary>What one locale's catalog looked like after the merge.</summary>
public sealed record XuiI18nLocaleResult(
    string Locale,
    string CatalogPath,
    int Strings,
    int Added,
    int Obsoleted,
    int Revived,
    int Translated);

/// <summary>
/// What an extraction run found and wrote. <c>Failures</c> holds documents that did not parse — they
/// are skipped rather than aborting the run, because the other documents still have strings a
/// translator can work on.
/// </summary>
public sealed record XuiI18nExtractResult(
    int FilesScanned,
    int Occurrences,
    int DistinctStrings,
    IReadOnlyList<XuiI18nLocaleResult> Locales,
    IReadOnlyList<string> Failures,
    bool DryRun);

public sealed record XuiValidateResult(
    string DocumentPath,
    bool Valid,
    int Errors,
    int Warnings,
    IReadOnlyList<XuiIssueInfo> Issues,
    string ContentHash);

/// <summary>
/// One element after template expansion. <see cref="BindingType"/> and <see cref="BindingProperty"/>
/// are what a generated view binding will call this element, so the document and the code it implies
/// can be read side by side.
/// </summary>
public sealed record XuiElementInfo(
    string Tag,
    string? Id,
    int Depth,
    string? BindingType,
    string? BindingProperty,
    IReadOnlyList<string> Attributes,
    string? Text,
    int Line);

public sealed record XuiScreenInfo(
    string Name,
    string Canvas,
    string Layer,
    string ScaleMode,
    string? Reference,
    int Line,
    IReadOnlyList<XuiElementInfo> Elements);

public sealed record XuiThemeInfo(string Name, string? Base, IReadOnlyList<string> Tokens);

/// <summary>One generated binding: the document id, the property it becomes, and its type.</summary>
public sealed record XuiBindingInfo(string Id, string Property, string Type);

/// <summary>
/// What a scaffold run wrote. <c>ControllerFile</c> is null when an existing controller was left
/// alone, and <c>Note</c> says why anything was deliberately not written.
/// </summary>
public sealed record XuiScaffoldResult(
    string DocumentPath,
    string ScreenName,
    string ViewFile,
    string? ControllerFile,
    string? Note,
    IReadOnlyList<XuiBindingInfo> Bindings,
    bool DryRun);

public sealed record XuiInspectResult(
    string DocumentPath,
    int Version,
    IReadOnlyList<XuiScreenInfo> Screens,
    IReadOnlyList<string> Templates,
    IReadOnlyList<XuiThemeInfo> Themes,
    IReadOnlyList<string> Imports,
    IReadOnlyList<string> SpriteKeys,
    IReadOnlyList<string> FontKeys,
    string ContentHash);
