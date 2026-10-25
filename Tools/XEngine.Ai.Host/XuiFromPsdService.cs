// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;

using PaintDotNet.Data.PhotoshopFileType;
using PDNWrapper;

using XEngine.Ai.Protocol;
using XEngine.Echo;
using XEngine.XUI.Core;

namespace XEngine.Ai.Host;

/// <summary>
/// Turns a layered PSD into a <c>.xui</c> screen plus exported sprite PNGs.
/// </summary>
/// <remarks>
/// <para>
/// The mapping is driven by <b>layer naming</b>, the same contract every PSD-to-UI pipeline ends up
/// with (Unity, FairyGUI, Cocos): <c>btn_login</c> becomes a button, <c>txt_title=登录</c> becomes a
/// text with that content, <c>input_account=账号</c> an input field with that placeholder,
/// <c>img_*</c> or any unprefixed pixel layer an image, an unprefixed group a frame. The convention
/// is cheap to follow in Photoshop and — decisive for this pipeline — cheap to *ask an AI for*: a
/// generation prompt simply includes the naming rules.
/// </para>
/// <para>
/// Everything is positioned absolutely, in document coordinates. Layout containers would be the
/// prettier output, but inferring "these five layers are really a VStack" from pixel positions is a
/// heuristic that fails silently; absolute rects reproduce the design exactly, and a human (or the
/// agent) can promote structure afterwards with <c>xui_patch</c>.
/// </para>
/// <para>
/// Nothing is silently dropped. Hidden layers, <c>#skip</c>-marked layers, and anything the
/// converter could not map are all in the report — a generated screen that quietly lost a layer
/// reads correct and is wrong.
/// </para>
/// </remarks>
internal sealed class XuiFromPsdService
{
    private readonly ProjectDescriptor _project;

    public XuiFromPsdService(ProjectDescriptor project)
    {
        _project = project;
    }

    public XuiFromPsdResult Convert(
        string documentPath,
        string? screenName,
        string? outputFolder,
        bool includeHidden,
        bool dryRun)
    {
        string absolute = ResolvePsd(documentPath);
        Document document;
        PhotoshopFile.PsdFile file;
        using (FileStream stream = File.OpenRead(absolute))
            document = PsdLoad.Load(stream, out file);

        string screen = string.IsNullOrWhiteSpace(screenName)
            ? ToPascal(Path.GetFileNameWithoutExtension(absolute))
            : screenName!;

        string folderRelative = string.IsNullOrWhiteSpace(outputFolder)
            ? "UI/" + screen.ToLowerInvariant()
            : outputFolder!.Replace('\\', '/').TrimEnd('/');
        string assets = _project.AssetsRoot() ?? Path.Combine(_project.ProjectRoot, "Assets");
        string folderAbsolute = Path.IsPathRooted(folderRelative)
            ? folderRelative
            : Path.Combine(assets, folderRelative);

        var state = new BuildState(folderRelative, includeHidden);

        // PSD lists layers top-most first; a UI document draws in declaration order (later = on
        // top), so children emit bottom-to-top.
        var rootElements = new List<string>();
        var roots = document.Layers.ToList();
        for (int i = roots.Count - 1; i >= 0; i--)
            EmitLayer(roots[i], parentX: 0, parentY: 0, indent: 2, rootElements, state);

        string xui = ComposeDocument(screen, document.width, document.height, rootElements);

        // Lint before anything touches disk: a converter that can write an invalid document has a
        // bug, and the bug should abort the write, not ship inside it.
        XuiDocument parsed = XuiDocumentParser.Parse(xui, screen + ".xui");
        IReadOnlyList<XuiLintIssue> issues = XuiLinter.Lint(parsed);
        var issueInfos = issues
            .Select(i => new XuiIssueInfo(i.Code, i.Severity.ToString(), i.Message, i.Span.Line, i.Span.Column, i.ElementId))
            .ToList();

        string xuiRelative = folderRelative + "/" + screen + ".xui";
        if (!dryRun && !XuiLinter.HasErrors(issues))
        {
            Directory.CreateDirectory(folderAbsolute);
            string spritesFolder = Path.Combine(folderAbsolute, "sprites");
            if (state.Sprites.Count > 0) Directory.CreateDirectory(spritesFolder);
            foreach ((string name, byte[] png) in state.Sprites)
            {
                string pngPath = Path.Combine(spritesFolder, name + ".png");
                File.WriteAllBytes(pngPath, png);
                WriteSpriteMeta(pngPath);
            }
            File.WriteAllText(Path.Combine(folderAbsolute, screen + ".xui"), xui);
        }

        return new XuiFromPsdResult(
            documentPath,
            "Assets/" + xuiRelative,
            screen,
            document.width,
            document.height,
            state.Elements,
            state.Sprites.Count,
            state.Skipped,
            !XuiLinter.HasErrors(issues),
            issueInfos,
            dryRun);
    }

    // ------------------------------------------------------------------
    //  Layer → element
    // ------------------------------------------------------------------

    private sealed class BuildState
    {
        public BuildState(string folderRelative, bool includeHidden)
        {
            FolderRelative = folderRelative;
            IncludeHidden = includeHidden;
        }

        public string FolderRelative { get; }
        public bool IncludeHidden { get; }
        public List<(string Name, byte[] Png)> Sprites { get; } = new();
        public List<XuiFromPsdElement> Elements { get; } = new();
        public List<string> Skipped { get; } = new();
        public HashSet<string> Ids { get; } = new(StringComparer.Ordinal);
        public HashSet<string> SpriteNames { get; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private void EmitLayer(
        BitmapLayer layer,
        int parentX,
        int parentY,
        int indent,
        List<string> into,
        BuildState state)
    {
        string rawName = string.IsNullOrWhiteSpace(layer.Name) ? "layer" : layer.Name.Trim();

        if (rawName.Contains("#skip", StringComparison.OrdinalIgnoreCase))
        {
            state.Skipped.Add(rawName + " — marked #skip");
            return;
        }
        if (!layer.Visible && !state.IncludeHidden)
        {
            state.Skipped.Add(rawName + " — hidden (pass includeHidden to keep)");
            return;
        }
        if (layer.IsEmpty && !layer.IsGroup)
        {
            state.Skipped.Add(rawName + " — no pixels");
            return;
        }

        (string prefix, string id, string? content) = ParseName(rawName);
        Rectangle rect = layer.documentRect;
        int localX = rect.X - parentX;
        int localY = rect.Y - parentY;
        string pad = new string(' ', indent * 2);

        switch (prefix)
        {
            case "txt":
            {
                string elementId = UniqueId(state, "XTxt", id);
                string text = content ?? Humanize(id);
                (string color, int fontSize) = SampleText(layer);
                into.Add(pad + "<Text id=\"" + elementId + "\" anchor=\"top-left\" "
                    + Geometry(localX, localY, rect) + " font-size=\"" + fontSize
                    + "\" color=\"" + color + "\">" + Escape(text) + "</Text>");
                state.Elements.Add(new XuiFromPsdElement(rawName, "Text", elementId, null));
                return;
            }

            case "input":
            {
                string elementId = UniqueId(state, "XInput", id);
                string? sprite = ExportSprite(layer, id, state);
                into.Add(pad + "<InputField id=\"" + elementId + "\" anchor=\"top-left\" "
                    + Geometry(localX, localY, rect)
                    + (sprite != null ? " sprite=\"" + sprite + "\"" : "")
                    + (content != null ? " placeholder=\"" + Escape(content) + "\"" : "") + "/>");
                state.Elements.Add(new XuiFromPsdElement(rawName, "InputField", elementId, sprite));
                return;
            }

            case "btn":
            {
                string elementId = UniqueId(state, "XBtn", id);

                // A button group's txt_ child becomes the button's inline label rather than baked
                // pixels: baked text cannot be retyped, translated, or restyled.
                BitmapLayer? label = null;
                if (layer.IsGroup)
                {
                    label = layer.ChildLayer.FirstOrDefault(
                        c => !c.IsGroup && ParseName(c.Name ?? string.Empty).Prefix == "txt");
                }

                string? sprite = ExportSprite(layer, id, state, exclude: label);
                string labelText = label != null
                    ? ParseName(label.Name!.Trim()) is var parsed && parsed.Content != null
                        ? parsed.Content
                        : Humanize(parsed.Id)
                    : string.Empty;

                if (labelText.Length > 0)
                {
                    into.Add(pad + "<Btn id=\"" + elementId + "\" anchor=\"top-left\" "
                        + Geometry(localX, localY, rect)
                        + (sprite != null ? " sprite=\"" + sprite + "\"" : "") + ">"
                        + Escape(labelText) + "</Btn>");
                }
                else
                {
                    into.Add(pad + "<Btn id=\"" + elementId + "\" anchor=\"top-left\" "
                        + Geometry(localX, localY, rect)
                        + (sprite != null ? " sprite=\"" + sprite + "\"" : "") + "/>");
                }
                state.Elements.Add(new XuiFromPsdElement(rawName, "Btn", elementId, sprite));
                return;
            }

            default:
            {
                if (layer.IsGroup && prefix.Length == 0)
                {
                    // A plain group is structure: a Frame whose children keep their own pixels.
                    string elementId = UniqueId(state, "XRect", id);
                    var children = new List<string>();
                    var ordered = layer.ChildLayer.ToList();
                    for (int i = ordered.Count - 1; i >= 0; i--)
                        EmitLayer(ordered[i], rect.X, rect.Y, indent + 1, children, state);

                    into.Add(pad + "<Frame id=\"" + elementId + "\" anchor=\"top-left\" "
                        + Geometry(localX, localY, rect) + ">");
                    into.AddRange(children);
                    into.Add(pad + "</Frame>");
                    state.Elements.Add(new XuiFromPsdElement(rawName, "Frame", elementId, null));
                    return;
                }

                // img_ prefixed, prefix-less pixel layers, and prefixed groups (e.g. img_ on a
                // group) all become images; a group flattens to one sprite.
                string imageId = UniqueId(state, "XImg", id);
                string? imageSprite = ExportSprite(layer, id, state);
                if (imageSprite == null)
                {
                    state.Skipped.Add(rawName + " — produced no pixels to export");
                    return;
                }
                into.Add(pad + "<Image id=\"" + imageId + "\" anchor=\"top-left\" "
                    + Geometry(localX, localY, rect) + " sprite=\"" + imageSprite + "\""
                    + Opacity(layer) + "/>");
                state.Elements.Add(new XuiFromPsdElement(rawName, "Image", imageId, imageSprite));
                return;
            }
        }
    }

    private static string Geometry(int localX, int localY, Rectangle rect)
        => "pos=\"" + localX.ToString(CultureInfo.InvariantCulture) + ","
            + (-localY).ToString(CultureInfo.InvariantCulture)
            + "\" size=\"" + rect.Width.ToString(CultureInfo.InvariantCulture) + "x"
            + rect.Height.ToString(CultureInfo.InvariantCulture) + "\"";

    private static string Opacity(BitmapLayer layer)
        => layer.Opacity == 255
            ? string.Empty
            : " color=\"#FFFFFF" + layer.Opacity.ToString("X2", CultureInfo.InvariantCulture) + "\"";

    // ------------------------------------------------------------------
    //  Names
    // ------------------------------------------------------------------

    /// <summary>
    /// <c>btn_confirm=确定</c> → (btn, confirm, 确定). No recognised prefix leaves prefix empty.
    /// </summary>
    private static (string Prefix, string Id, string? Content) ParseName(string rawName)
    {
        string name = rawName;
        string? content = null;

        int equals = name.IndexOf('=');
        if (equals >= 0)
        {
            content = name.Substring(equals + 1).Trim();
            name = name.Substring(0, equals).Trim();
        }

        int underscore = name.IndexOf('_');
        if (underscore > 0)
        {
            string prefix = name.Substring(0, underscore).ToLowerInvariant();
            if (prefix is "btn" or "txt" or "img" or "input")
                return (prefix, name.Substring(underscore + 1), content);
        }

        return (string.Empty, name, content);
    }

    private static string UniqueId(BuildState state, string prefix, string rawId)
    {
        string body = ToPascal(rawId);
        if (body.Length == 0) body = "Layer";
        string id = prefix + body;
        string unique = id;
        for (int i = 2; !state.Ids.Add(unique); i++)
            unique = id + i.ToString(CultureInfo.InvariantCulture);
        return unique;
    }

    /// <summary>Anything a C# identifier cannot hold becomes a word boundary.</summary>
    private static string ToPascal(string text)
    {
        var builder = new StringBuilder(text.Length);
        bool boundary = true;
        foreach (char c in text)
        {
            if (char.IsLetterOrDigit(c) && c < 128)
            {
                builder.Append(boundary ? char.ToUpperInvariant(c) : c);
                boundary = false;
            }
            else
            {
                boundary = true;
            }
        }
        return builder.ToString();
    }

    private static string Humanize(string id)
        => string.Join(' ', id.Split(new[] { '_', '-', ' ' }, StringSplitOptions.RemoveEmptyEntries));

    private static string Escape(string text)
        => text.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;").Replace("\"", "&quot;");

    // ------------------------------------------------------------------
    //  Pixels
    // ------------------------------------------------------------------

    /// <summary>Exports a layer (or a group flattened src-over) as a PNG, named after its id.</summary>
    private string? ExportSprite(BitmapLayer layer, string rawId, BuildState state, BitmapLayer? exclude = null)
    {
        Rectangle bounds = layer.documentRect;
        if (bounds.IsEmpty) return null;

        var rgba = new byte[bounds.Width * bounds.Height * 4];
        bool any = Flatten(layer, bounds, rgba, exclude);
        if (!any) return null;

        string baseName = SafeFileName(rawId);
        string name = baseName;
        for (int i = 2; !state.SpriteNames.Add(name); i++)
            name = baseName + "-" + i.ToString(CultureInfo.InvariantCulture);

        state.Sprites.Add((name, PngRgba.Encode(rgba, bounds.Width, bounds.Height)));
        return state.FolderRelative + "/sprites/" + name + ".png";
    }

    /// <summary>
    /// Src-over composite of a layer subtree into a document-space rect, bottom layer first.
    /// </summary>
    /// <remarks>
    /// Normal blending only. PSD blend modes beyond Normal would need the full compositor; UI art
    /// is overwhelmingly normal-blended, and a mis-blended flatten is visible the moment the agent
    /// looks at the exported PNG.
    /// </remarks>
    private static bool Flatten(BitmapLayer layer, Rectangle bounds, byte[] rgba, BitmapLayer? exclude)
    {
        if (ReferenceEquals(layer, exclude)) return false;

        if (layer.IsGroup)
        {
            bool any = false;
            // ChildLayer lists top-most first; src-over wants bottom first.
            foreach (BitmapLayer child in layer.ChildLayer.Reverse())
                any |= Flatten(child, bounds, rgba, exclude);
            return any;
        }

        if (layer.IsEmpty) return false;

        Rectangle rect = layer.documentRect;
        Color32[] pixels = layer.Surface.color;
        double layerAlpha = layer.Opacity / 255.0;
        bool wrote = false;

        for (int y = 0; y < rect.Height; y++)
        {
            int docY = rect.Y + y - bounds.Y;
            if (docY < 0 || docY >= bounds.Height) continue;
            for (int x = 0; x < rect.Width; x++)
            {
                int docX = rect.X + x - bounds.X;
                if (docX < 0 || docX >= bounds.Width) continue;

                Color32 src = pixels[y * rect.Width + x];
                double alpha = src.a / 255.0 * layerAlpha;
                if (alpha <= 0) continue;
                wrote = true;

                int dst = (docY * bounds.Width + docX) * 4;
                double inverse = (rgba[dst + 3] / 255.0) * (1 - alpha);
                double outAlpha = alpha + inverse;
                rgba[dst] = (byte)Math.Round((src.r * alpha + rgba[dst] * inverse) / outAlpha);
                rgba[dst + 1] = (byte)Math.Round((src.g * alpha + rgba[dst + 1] * inverse) / outAlpha);
                rgba[dst + 2] = (byte)Math.Round((src.b * alpha + rgba[dst + 2] * inverse) / outAlpha);
                rgba[dst + 3] = (byte)Math.Round(outAlpha * 255.0);
            }
        }

        return wrote;
    }

    /// <summary>
    /// The dominant opaque pixel colour and a font size from the layer height — honest
    /// approximations, both stated in the tool description rather than hidden.
    /// </summary>
    private static (string Color, int FontSize) SampleText(BitmapLayer layer)
    {
        var counts = new Dictionary<int, int>();
        foreach (Color32 px in layer.Surface.color)
        {
            if (px.a < 128) continue;
            int key = (px.r << 16) | (px.g << 8) | px.b;
            counts[key] = counts.TryGetValue(key, out int n) ? n + 1 : 1;
        }

        string color = "#FFFFFF";
        if (counts.Count > 0)
        {
            int dominant = counts.OrderByDescending(kv => kv.Value).First().Key;
            color = "#" + dominant.ToString("X6", CultureInfo.InvariantCulture);
        }

        int fontSize = Math.Clamp(layer.documentRect.Height, 8, 96);
        return (color, fontSize);
    }

    // ------------------------------------------------------------------
    //  Document text
    // ------------------------------------------------------------------

    private static string ComposeDocument(string screen, int width, int height, List<string> elements)
    {
        var builder = new StringBuilder();
        builder.Append("<?xml version=\"1.0\" encoding=\"utf-8\"?>\n");
        builder.Append("<XUI version=\"1\">\n");
        builder.Append("  <!-- Generated by xui_from_psd. Positions are absolute document coordinates;\n");
        builder.Append("       promote repeated rows/columns to VStack/HStack by hand or with xui_patch. -->\n");
        builder.Append("  <Screen name=\"").Append(screen).Append("\" reference=\"")
            .Append(width.ToString(CultureInfo.InvariantCulture)).Append('x')
            .Append(height.ToString(CultureInfo.InvariantCulture)).Append("\" layer=\"Normal\">\n");
        foreach (string line in elements) builder.Append(line).Append('\n');
        builder.Append("  </Screen>\n");
        builder.Append("</XUI>\n");
        return builder.ToString();
    }

    private static string SafeFileName(string text)
    {
        var builder = new StringBuilder(text.Length);
        foreach (char c in text)
            builder.Append(char.IsLetterOrDigit(c) && c < 128 ? char.ToLowerInvariant(c) : '-');
        string name = builder.ToString().Trim('-');
        return name.Length == 0 ? "layer" : name;
    }

    /// <summary>
    /// Writes the PNG's <c>.meta</c> with sprite mode Single, so the texture imports with a
    /// <c>Sprite</c> sub-asset the document's <c>sprite=</c> reference can resolve to.
    /// </summary>
    /// <remarks>
    /// A plain PNG imports as a bare <c>Texture2D</c> — an <c>AssetRef&lt;Sprite&gt;</c> pointing at
    /// it resolves to null and the element falls back to the default sprite, which is exactly the
    /// invisible failure this converter must not ship. An existing meta keeps its GUID (regenerating
    /// the skin must not break references); only the sprite settings are enforced.
    /// </remarks>
    private static void WriteSpriteMeta(string pngPath)
    {
        string metaPath = pngPath + ".meta";
        EchoObject meta;
        if (File.Exists(metaPath))
        {
            meta = EchoObject.ReadFromString(File.ReadAllText(metaPath));
        }
        else
        {
            meta = EchoObject.NewCompound();
            meta["guid"] = new EchoObject(Guid.NewGuid().ToString());
            meta["importer"] = new EchoObject("TextureImporter");
            meta["importerVersion"] = new EchoObject(3);
        }

        // Re-assigning an already-parented compound throws ("Tag already has a parent"), so an
        // existing settings tree is mutated in place; only a fresh one is attached.
        if (!meta.TryGet("settings", out EchoObject? existing)
            || existing is null
            || existing.TagType != EchoType.Compound)
        {
            existing = EchoObject.NewCompound();
            meta["settings"] = existing;
        }
        EchoObject settings = existing;

        settings["generateMipmaps"] = new EchoObject(false);
        settings["sRGB"] = new EchoObject(true);
        settings["minFilter"] = new EchoObject(1L);
        settings["magFilter"] = new EchoObject(1L);

        var sprite = EchoObject.NewCompound();
        sprite["Mode"] = new EchoObject(1L);          // SpriteMode.Single
        sprite["PixelsPerUnit"] = new EchoObject(100f);
        sprite["GenerateTightMesh"] = new EchoObject(false);
        settings["sprite"] = sprite;

        File.WriteAllText(metaPath, meta.WriteToString());
    }

    private string ResolvePsd(string documentPath)
    {
        bool isPsd = documentPath.EndsWith(".psd", StringComparison.OrdinalIgnoreCase)
            || documentPath.EndsWith(".psb", StringComparison.OrdinalIgnoreCase);
        if (!isPsd)
            throw new ArgumentException("xui_from_psd requires a .psd or .psb path, got '" + documentPath + "'.");

        string absolute = _project.ResolveAssetPath(documentPath);
        if (!File.Exists(absolute))
            throw new FileNotFoundException("PSD document not found: " + documentPath, absolute);
        return absolute;
    }
}

/// <summary>One element the converter emitted, and where it came from.</summary>
public sealed record XuiFromPsdElement(
    string LayerName,
    string Tag,
    string Id,
    string? Sprite);

public sealed record XuiFromPsdResult(
    string DocumentPath,
    string XuiPath,
    string ScreenName,
    int Width,
    int Height,
    IReadOnlyList<XuiFromPsdElement> Elements,
    int SpritesWritten,
    IReadOnlyList<string> Skipped,
    bool Valid,
    IReadOnlyList<XuiIssueInfo> Issues,
    bool DryRun);
