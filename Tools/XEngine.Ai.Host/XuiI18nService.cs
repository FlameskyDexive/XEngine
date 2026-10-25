// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

using XEngine.Ai.Protocol;
using XEngine.XUI.Core;

namespace XEngine.Ai.Host;

/// <summary>
/// Extracts the strings a player reads into <c>.po</c> catalogs.
/// </summary>
/// <remarks>
/// <para>
/// Extraction is a <b>merge</b>, never a rewrite. A translator's work outlives any single scan: an
/// existing translation is kept, a string that no source mentions any more is marked obsolete rather
/// than deleted, and a string that comes back is revived with its translation intact. That is what makes
/// it safe to run on every build.
/// </para>
/// <para>
/// The source string is the key, so a project that never runs this still displays correctly — the
/// catalog is an overlay, not a prerequisite.
/// </para>
/// </remarks>
internal sealed class XuiI18nService
{
    private readonly ProjectDescriptor _project;

    public XuiI18nService(ProjectDescriptor project)
    {
        _project = project;
    }

    public XuiI18nExtractResult Extract(
        string? documentFolder,
        string? scriptFolder,
        IReadOnlyList<string> locales,
        string outputFolder,
        bool dryRun)
    {
        string assets = _project.AssetsRoot() ?? _project.ProjectRoot;
        string documentsRoot = ResolveFolder(documentFolder, assets);
        string scriptsRoot = ResolveFolder(scriptFolder, assets);

        var extracted = new List<XuiExtractedString>();
        var scannedFiles = new List<string>();
        var failures = new List<string>();

        foreach (string path in EnumerateFiles(documentsRoot, "*.xui"))
        {
            scannedFiles.Add(Relative(path));
            try
            {
                XuiDocument document = XuiDocumentParser.Parse(File.ReadAllText(path), Relative(path));
                extracted.AddRange(XuiTextExtractor.Extract(document, Relative(path)));
            }
            catch (XuiParseException ex)
            {
                // A broken document is reported and skipped: the other documents still have strings a
                // translator can be working on, and refusing to extract anything helps nobody.
                failures.Add(ex.Message);
            }
        }

        foreach (string path in EnumerateFiles(scriptsRoot, "*.cs"))
        {
            // Generated bindings hold no strings of their own; scanning them would attribute a screen's
            // text to a file nobody edits.
            if (path.EndsWith(".g.cs", StringComparison.OrdinalIgnoreCase)) continue;
            scannedFiles.Add(Relative(path));
            extracted.AddRange(XuiTextExtractor.ExtractFromCSharp(File.ReadAllText(path), Relative(path)));
        }

        extracted.Sort(static (a, b) =>
        {
            int byPath = string.CompareOrdinal(a.SourcePath, b.SourcePath);
            return byPath != 0 ? byPath : a.Line.CompareTo(b.Line);
        });

        string outputAbsolute = ResolveFolder(outputFolder, assets);
        var written = new List<XuiI18nLocaleResult>();

        foreach (string locale in locales)
        {
            string poPath = Path.Combine(outputAbsolute, locale + ".po");
            PoCatalog catalog = ReadOrCreate(poPath, locale);
            XuiMergeReport report = XuiTextExtractor.Merge(catalog, extracted);

            if (!dryRun)
            {
                Directory.CreateDirectory(outputAbsolute);
                File.WriteAllText(poPath, catalog.Write());
            }

            written.Add(new XuiI18nLocaleResult(
                locale,
                Relative(poPath),
                report.Total,
                report.Added,
                report.Obsoleted,
                report.Revived,
                report.Translated));
        }

        return new XuiI18nExtractResult(
            scannedFiles.Count,
            extracted.Count,
            extracted.Select(e => e.Text).Distinct(StringComparer.Ordinal).Count(),
            written,
            failures,
            dryRun);
    }

    /// <summary>
    /// Reads an existing catalog, or starts one with a header. A file that does not parse is a hard
    /// error: overwriting it would destroy the translations it still contains.
    /// </summary>
    private static PoCatalog ReadOrCreate(string path, string locale)
    {
        if (File.Exists(path))
        {
            try
            {
                return PoCatalog.Parse(File.ReadAllText(path), path);
            }
            catch (XuiParseException ex)
            {
                throw new InvalidOperationException(
                    "Refusing to overwrite " + path + ", which does not parse: " + ex.Message, ex);
            }
        }

        var created = new PoCatalog();
        created.Header.Add(new KeyValuePair<string, string>("Project-Id-Version", "XUI"));
        created.Header.Add(new KeyValuePair<string, string>("Language", locale));
        created.Header.Add(new KeyValuePair<string, string>("MIME-Version", "1.0"));
        created.Header.Add(new KeyValuePair<string, string>("Content-Type", "text/plain; charset=UTF-8"));
        created.Header.Add(new KeyValuePair<string, string>("Content-Transfer-Encoding", "8bit"));
        return created;
    }

    private static IEnumerable<string> EnumerateFiles(string root, string pattern)
        => Directory.Exists(root)
            ? Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal)
            : Array.Empty<string>();

    private string ResolveFolder(string? folder, string assets)
    {
        if (string.IsNullOrWhiteSpace(folder)) return assets;
        return Path.IsPathRooted(folder) ? folder : Path.Combine(assets, folder);
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
