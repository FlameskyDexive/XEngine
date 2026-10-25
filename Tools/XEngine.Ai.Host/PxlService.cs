// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Ai.Protocol;
using XEngine.XUI.Core;

namespace XEngine.Ai.Host;

/// <summary>
/// Offline validation and rendering for <c>.pxl</c> pixel-grid sprites.
/// </summary>
/// <remarks>
/// <c>pxl_render</c> is the point of the format. Checking the characters proves the file parses;
/// only looking at the render catches "the bevel points the wrong way". The loop the format is built
/// for is write → render → look → revise, and this is the render step.
/// </remarks>
internal sealed class PxlService
{
    private const int MaxRenderScale = 32;
    private const int MaxRenderPixels = 4096;

    private readonly ProjectDescriptor _project;

    public PxlService(ProjectDescriptor project) => _project = project;

    public PxlValidateResult Validate(string documentPath)
    {
        string absolute = Resolve(documentPath);
        string text = File.ReadAllText(absolute);

        PxlDocument document;
        try
        {
            document = PxlParser.Parse(text, documentPath);
        }
        catch (PxlParseException ex)
        {
            return new PxlValidateResult(
                documentPath, false, 0, 0f, null,
                Array.Empty<PxlSectionInfo>(), Array.Empty<string>(),
                new[] { new PxlIssueInfo("PXL-PARSE", ex.RawMessage, ex.Line) });
        }

        var issues = new List<PxlIssueInfo>();

        // A name that no palette resolved is not a parse error — the file is well formed — but the
        // pixels would come out transparent, so it has to be reported before someone renders and
        // wonders where their art went.
        foreach (KeyValuePair<char, string> unresolved in document.UnresolvedNames)
        {
            issues.Add(new PxlIssueInfo(
                "PXL-COLOR-UNRESOLVED",
                "Character '" + unresolved.Key + "' uses the colour name \"" + unresolved.Value
                + "\", which is not a hex literal or a CSS colour name. "
                + (document.PaletteName == null
                    ? "Declare a palette with \"palette: @name\", or use a hex value."
                    : "Palette \"" + document.PaletteName + "\" does not define it."),
                0));
        }

        var sections = new List<PxlSectionInfo>(document.Sections.Count);
        var used = new HashSet<char>();
        foreach (PxlSection section in document.Sections)
        {
            for (int y = 0; y < section.Rows.Count; y++)
            {
                string row = section.Rows[y];
                for (int x = 0; x < row.Length; x++)
                {
                    char c = row[x];
                    used.Add(c);
                    if (c == PxlDocument.TransparentChar
                        || document.Chars.ContainsKey(c)
                        || document.UnresolvedNames.ContainsKey(c))
                    {
                        continue;
                    }

                    issues.Add(new PxlIssueInfo(
                        "PXL-CHAR-UNDECLARED",
                        "Section \"" + section.Name + "\" row "
                        + (y + 1).ToString(System.Globalization.CultureInfo.InvariantCulture)
                        + " uses '" + c + "', which the chars block does not declare. It renders transparent.",
                        section.Span.Line + y));
                }
            }

            sections.Add(new PxlSectionInfo(
                section.Name,
                section.Width,
                section.Height,
                section.HasBorder
                    ? section.BorderTop + "," + section.BorderRight + ","
                      + section.BorderBottom + "," + section.BorderLeft
                    : null,
                section.Span.Line));
        }

        // A declared character nobody uses is dead weight in the palette, worth saying but not an error.
        var unused = new List<string>();
        foreach (char declared in document.Chars.Keys)
        {
            if (!used.Contains(declared)) unused.Add(declared.ToString());
        }

        return new PxlValidateResult(
            documentPath,
            issues.Count == 0,
            document.Sections.Count,
            document.PixelsPerUnit,
            document.PaletteName,
            sections,
            unused,
            issues);
    }

    /// <summary>Renders each section to a PNG the caller can open and look at.</summary>
    public PxlRenderResult Render(string documentPath, string? sectionName, int scale, string? outputFolder)
    {
        string absolute = Resolve(documentPath);
        PxlDocument document = PxlParser.Parse(File.ReadAllText(absolute), documentPath);

        int renderScale = Math.Clamp(scale, 1, MaxRenderScale);

        var targets = new List<PxlSection>();
        if (sectionName == null)
        {
            targets.AddRange(document.Sections);
        }
        else
        {
            PxlSection section = document.FindSection(sectionName)
                ?? throw new KeyNotFoundException(
                    "Section '" + sectionName + "' is not declared in " + documentPath
                    + ". Call pxl_validate to list the sections it has.");
            targets.Add(section);
        }

        string folder = outputFolder != null
            ? _project.ResolveAssetPath(outputFolder)
            : Path.Combine(_project.ProjectRoot, ".xengine", "pxl");
        Directory.CreateDirectory(folder);

        string baseName = Path.GetFileNameWithoutExtension(absolute);
        var written = new List<PxlRenderedImage>(targets.Count);

        foreach (PxlSection section in targets)
        {
            int width = section.Width * renderScale;
            int height = section.Height * renderScale;
            if (width > MaxRenderPixels || height > MaxRenderPixels)
            {
                throw new InvalidOperationException(
                    "Section \"" + section.Name + "\" at scale "
                    + renderScale.ToString(System.Globalization.CultureInfo.InvariantCulture)
                    + " would be " + width + "x" + height + ", over the "
                    + MaxRenderPixels + "px limit. Lower the scale.");
            }

            byte[] rgba = PxlRasterizer.Rasterize(document, section, renderScale);
            byte[] png = PngRgba.Encode(rgba, width, height);

            string fileName = targets.Count == 1 && sectionName != null
                ? baseName + "-" + section.Name + ".png"
                : baseName + "-" + section.Name + ".png";
            string path = Path.Combine(folder, fileName);
            File.WriteAllBytes(path, png);

            written.Add(new PxlRenderedImage(section.Name, Relative(path), width, height));
        }

        return new PxlRenderResult(documentPath, renderScale, written);
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

    private string Resolve(string documentPath)
    {
        if (!documentPath.EndsWith(".pxl", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("pxl tools require a .pxl path, got '" + documentPath + "'.");

        string absolute = _project.ResolveAssetPath(documentPath);
        if (!File.Exists(absolute))
            throw new FileNotFoundException("Pixel document not found: " + documentPath, absolute);
        return absolute;
    }
}
