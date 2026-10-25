// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Security.Cryptography;
using System.Text;
using System.Xml;
using System.Xml.Linq;

using XEngine.Ai.Protocol;
using XEngine.Data.Provenance;
using XEngine.Echo;
using XEngine.XUI.Core;

// Both namespaces declare ChangeProvenance; the journal's is the one that gets written.
using ChangeProvenance = XEngine.Data.Provenance.ChangeProvenance;

namespace XEngine.Ai.Host;

/// <summary>
/// The offline half of the XUI tool surface: parse, lint, summarise, and patch <c>.xui</c> documents
/// with no editor running.
/// </summary>
/// <remarks>
/// <para>
/// This is the point of keeping <c>Packages/com.xengine.xui/Runtime/Core</c> free of engine types —
/// the parser and lint rules an agent is judged against here are byte-for-byte the ones the runtime
/// assembler uses, so "it validates" and "it will build" cannot drift apart.
/// </para>
/// <para>
/// Patches go through the same provenance journal as every other write tool. A <c>.xui</c> is text
/// rather than Echo, so the before/after text is wrapped in a one-field Echo record purely to reuse
/// that machinery; the journal then holds UI edits next to scene and asset edits instead of in a
/// second place nobody thinks to look.
/// </para>
/// </remarks>
internal sealed class XuiService
{
    /// <summary>Attribute patch: <c>XBtnLogin/@color</c>.</summary>
    private const char AttributeSigil = '@';

    /// <summary>Inline-text patch: <c>XTxtTitle/#text</c>. Distinct from the <c>text</c> attribute.</summary>
    private const char TextSigil = '#';

    private const string ScreenPrefix = "screen:";
    private const string TextTarget = "#text";
    private const string SourceField = "Source";

    private readonly ProjectDescriptor _project;
    private readonly ChangeJournal _journal;

    public XuiService(ProjectDescriptor project, ChangeJournal journal)
    {
        _project = project;
        _journal = journal;
    }

    // ------------------------------------------------------------------
    //  Validate
    // ------------------------------------------------------------------

    public XuiValidateResult Validate(string documentPath, bool includeWarnings)
    {
        string absolute = Resolve(documentPath);
        string text = File.ReadAllText(absolute);

        XuiDocument document;
        try
        {
            document = XuiDocumentParser.Parse(text, documentPath);
        }
        catch (XuiParseException ex)
        {
            // A parse failure is a single blocking finding: nothing downstream can run, and reporting
            // it in the same shape as lint keeps the caller's handling uniform.
            return new XuiValidateResult(
                documentPath,
                Valid: false,
                Errors: 1,
                Warnings: 0,
                Issues: new[]
                {
                    new XuiIssueInfo("XUI-PARSE", "Error", ex.RawMessage, ex.Span.Line, ex.Span.Column, null),
                },
                ContentHash: HashText(text));
        }

        IReadOnlyList<XuiLintIssue> issues = XuiLinter.Lint(document);

        var reported = new List<XuiIssueInfo>(issues.Count);
        int errors = 0, warnings = 0;
        for (int i = 0; i < issues.Count; i++)
        {
            XuiLintIssue issue = issues[i];
            bool isError = issue.Severity == XuiLintSeverity.Error;
            if (isError) errors++;
            else warnings++;

            if (!isError && !includeWarnings) continue;
            reported.Add(new XuiIssueInfo(
                issue.Code,
                isError ? "Error" : "Warning",
                issue.Message,
                issue.Span.Line,
                issue.Span.Column,
                issue.ElementId));
        }

        return new XuiValidateResult(
            documentPath, errors == 0, errors, warnings, reported, HashText(text));
    }

    // ------------------------------------------------------------------
    //  Inspect
    // ------------------------------------------------------------------

    public XuiInspectResult Inspect(string documentPath, string? screenName)
    {
        string absolute = Resolve(documentPath);
        string text = File.ReadAllText(absolute);
        XuiDocument document = XuiDocumentParser.Parse(text, documentPath);

        var templates = new XuiTemplateTable();
        templates.Add(document);

        var screens = new List<XuiScreenInfo>(document.Screens.Count);
        for (int i = 0; i < document.Screens.Count; i++)
        {
            ScreenDef screen = document.Screens[i];
            if (screenName != null && !string.Equals(screen.Name, screenName, StringComparison.Ordinal))
                continue;

            var elements = new List<XuiElementInfo>();
            ElementNode expanded = XuiTemplateExpander.Expand(screen.Root, templates, documentPath);
            for (int c = 0; c < expanded.Children.Count; c++)
                CollectElements(expanded.Children[c], depth: 0, elements);

            screens.Add(new XuiScreenInfo(
                screen.Name,
                screen.Canvas.ToString(),
                screen.Layer.ToString(),
                screen.ScaleMode.ToString(),
                screen.Reference?.ToString(),
                screen.Span.Line,
                elements));
        }

        if (screenName != null && screens.Count == 0)
        {
            throw new KeyNotFoundException(
                "Screen '" + screenName + "' is not declared in " + documentPath
                + ". Call xui_inspect without a screen name to list what is.");
        }

        var themes = new List<XuiThemeInfo>(document.Themes.Count);
        for (int i = 0; i < document.Themes.Count; i++)
        {
            ThemeBlock theme = document.Themes[i];
            var tokens = new List<string>(theme.Colors.Count);
            for (int c = 0; c < theme.Colors.Count; c++)
                tokens.Add(theme.Colors[c].Name + "=" + theme.Colors[c].Value);
            themes.Add(new XuiThemeInfo(theme.Name, theme.Base, tokens));
        }

        var templateNames = new List<string>(document.Templates.Count);
        for (int i = 0; i < document.Templates.Count; i++)
            templateNames.Add(document.Templates[i].Name);

        var imports = new List<string>(document.Imports.Count);
        for (int i = 0; i < document.Imports.Count; i++)
        {
            ImportRef import = document.Imports[i];
            imports.Add(import.Alias == null ? import.Src : import.Src + " as " + import.Alias);
        }

        XuiDocumentReferences references = XuiDocumentReferences.Collect(document);

        return new XuiInspectResult(
            documentPath,
            document.Version,
            screens,
            templateNames,
            themes,
            imports,
            new List<string>(references.Sprites),
            new List<string>(references.Fonts),
            HashText(text));
    }

    private static void CollectElements(ElementNode node, int depth, List<XuiElementInfo> into)
    {
        XuiTagInfo? tag = XuiTagLibrary.Find(node.Tag);
        var attributes = new List<string>(node.Attributes.Count);
        for (int i = 0; i < node.Attributes.Count; i++)
        {
            XuiAttribute attribute = node.Attributes[i];
            if (string.Equals(attribute.Name, "id", StringComparison.Ordinal)) continue;
            attributes.Add(attribute.Name + "=" + attribute.Value);
        }

        into.Add(new XuiElementInfo(
            node.Tag,
            node.Id,
            depth,
            tag?.BindingType,
            node.Id == null ? null : XuiTagLibrary.PropertyNameFor(node.Id),
            attributes,
            node.Text,
            node.Span.Line));

        for (int i = 0; i < node.Children.Count; i++)
            CollectElements(node.Children[i], depth + 1, into);
    }

    // ------------------------------------------------------------------
    //  Scaffold
    // ------------------------------------------------------------------

    /// <summary>
    /// Writes the view binding and, when absent, the controller skeleton for one screen.
    /// </summary>
    /// <remarks>
    /// The <c>.g.cs</c> is derived and always rewritten. The controller is written <b>once</b> and never
    /// touched again: it is the file logic lives in, and a generator that overwrote it would lose work
    /// the first time someone re-ran the tool after an id change.
    /// </remarks>
    public XuiScaffoldResult Scaffold(
        string documentPath,
        string screenName,
        string outputFolder,
        string? namespaceName,
        bool overwriteController,
        bool dryRun)
    {
        string absolute = Resolve(documentPath);
        XuiDocument document = XuiDocumentParser.Parse(File.ReadAllText(absolute), documentPath);

        ScreenDef screen = document.FindScreen(screenName)
            ?? throw new KeyNotFoundException(
                "Screen '" + screenName + "' is not declared in " + documentPath
                + ". Call xui_inspect to list the screens it declares.");

        // Generating from an invalid document would bake its mistakes into C#, where they cost far more
        // to unpick than a lint message does.
        IReadOnlyList<XuiLintIssue> issues = XuiLinter.Lint(document);
        if (XuiLinter.HasErrors(issues))
        {
            throw new InvalidOperationException(
                "Refusing to scaffold from a document with lint errors. Run xui_validate on "
                + documentPath + " and fix them first.");
        }

        var templates = new XuiTemplateTable();
        templates.Add(document);
        ElementNode expanded = XuiTemplateExpander.Expand(screen.Root, templates, documentPath);

        XuiScaffoldOutput output = XuiViewCodeGenerator.Generate(
            screen, expanded, namespaceName ?? string.Empty, documentPath);

        string folderAbsolute = ResolveOutputFolder(outputFolder);
        string viewPath = Path.Combine(folderAbsolute, output.ViewFileName);
        string controllerPath = Path.Combine(folderAbsolute, output.ControllerFileName);
        bool controllerExists = File.Exists(controllerPath);
        bool writeController = overwriteController || !controllerExists;

        if (!dryRun)
        {
            Directory.CreateDirectory(folderAbsolute);
            File.WriteAllText(viewPath, output.ViewSource);
            if (writeController) File.WriteAllText(controllerPath, output.ControllerSource);
        }

        var members = new List<XuiBindingInfo>(output.Members.Count);
        for (int i = 0; i < output.Members.Count; i++)
        {
            XuiBindingMember member = output.Members[i];
            members.Add(new XuiBindingInfo(member.Id, member.Property, member.Type));
        }

        return new XuiScaffoldResult(
            documentPath,
            screenName,
            Relative(viewPath),
            writeController ? Relative(controllerPath) : null,
            controllerExists && !overwriteController
                ? "Controller left untouched; it already exists."
                : null,
            members,
            dryRun);
    }

    /// <summary>
    /// Resolves the generated-code folder under <c>Assets/</c>. ResolveAssetPath cannot be used here:
    /// it falls back to the project root for any path that does not already exist, and a folder about
    /// to be created never does — which would scatter generated scripts outside the asset tree.
    /// </summary>
    private string ResolveOutputFolder(string outputFolder)
    {
        if (string.IsNullOrWhiteSpace(outputFolder))
            throw new ArgumentException("outputFolder is required.");
        if (Path.IsPathRooted(outputFolder)) return outputFolder;

        string? assets = _project.AssetsRoot();
        return Path.Combine(assets ?? _project.ProjectRoot, outputFolder);
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

    // ------------------------------------------------------------------
    //  Patch
    // ------------------------------------------------------------------

    public async Task<SceneWriteResult> PatchAsync(
        string documentPath,
        IReadOnlyList<SceneFieldPatch> patches,
        IReadOnlyList<string>? removePaths,
        ChangeProvenance provenance,
        bool dryRun,
        string? expectedBeforeHash,
        CancellationToken cancellationToken)
    {
        string absolute = Resolve(documentPath);
        string before = File.ReadAllText(absolute);

        string beforeHash = HashText(before);
        if (!string.IsNullOrEmpty(expectedBeforeHash)
            && !string.Equals(expectedBeforeHash, beforeHash, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "The document changed since it was read (expected " + expectedBeforeHash + ", found "
                + beforeHash + "). Re-run xui_inspect and re-apply the patch against the current text.");
        }

        // The XDocument is only used to LOCATE things; the edits are spliced into the original text by
        // XuiTextEdit, so everything the patch did not name comes out byte-for-byte unchanged.
        XDocument xml;
        try
        {
            xml = XDocument.Parse(before, LoadOptions.PreserveWhitespace | LoadOptions.SetLineInfo);
        }
        catch (XmlException ex)
        {
            throw new InvalidOperationException(
                "Cannot patch " + documentPath + ": the file is not well-formed XML (" + ex.Message + ").");
        }

        var summary = new StringBuilder();
        var editor = new XuiTextEdit(before);
        int applied = 0;

        for (int i = 0; i < patches.Count; i++)
        {
            SceneFieldPatch patch = patches[i];
            ApplySet(xml, editor, documentPath, patch.Path, patch.Value ?? string.Empty, summary);
            applied++;
        }

        if (removePaths != null)
        {
            for (int i = 0; i < removePaths.Count; i++)
            {
                ApplyRemove(xml, editor, documentPath, removePaths[i], summary);
                applied++;
            }
        }

        string after = editor.Apply();
        string afterHash = HashText(after);

        if (beforeHash == afterHash)
        {
            return new SceneWriteResult(
                "", documentPath, beforeHash, afterHash, dryRun,
                "No change (" + applied.ToString(System.Globalization.CultureInfo.InvariantCulture)
                + " patch(es) produced identical text).");
        }

        // Parse the result before committing. A patch that produces a document the parser rejects is a
        // failed patch, not a saved file plus a broken project.
        try
        {
            XuiDocumentParser.Parse(after, documentPath);
        }
        catch (XuiParseException ex)
        {
            throw new InvalidOperationException(
                "The patch would make " + documentPath + " invalid and was not written: " + ex.Message);
        }

        return await CommitAsync(
            before, after, absolute, documentPath, provenance, dryRun,
            summary.ToString().TrimEnd(), beforeHash, afterHash, cancellationToken);
    }

    private static void ApplySet(
        XDocument xml,
        XuiTextEdit editor,
        string documentPath,
        string path,
        string value,
        StringBuilder summary)
    {
        (XElement element, string target, string owner) = ResolveTarget(xml, documentPath, path);

        if (string.Equals(target, TextTarget, StringComparison.Ordinal))
        {
            if (element.HasElements)
            {
                throw new InvalidOperationException(
                    "Cannot set inline text on " + owner + ": it has child elements, and mixing the two is invalid.");
            }
            editor.SetText(element, value);
            summary.AppendLine(owner + " text = \"" + value + "\"");
            return;
        }

        editor.SetAttribute(element, target, value);
        summary.AppendLine(owner + " " + target + " = \"" + value + "\"");
    }

    private static void ApplyRemove(
        XDocument xml,
        XuiTextEdit editor,
        string documentPath,
        string path,
        StringBuilder summary)
    {
        (XElement element, string target, string owner) = ResolveTarget(xml, documentPath, path);

        if (string.Equals(target, TextTarget, StringComparison.Ordinal))
        {
            editor.SetText(element, string.Empty);
            summary.AppendLine(owner + " text removed");
            return;
        }

        if (element.Attribute(target) == null)
        {
            throw new KeyNotFoundException(
                owner + " has no attribute \"" + target + "\" to remove. Call xui_inspect to see what it has.");
        }

        editor.RemoveAttribute(element, target);
        summary.AppendLine(owner + " " + target + " removed");
    }

    /// <summary>
    /// Resolves a patch path to an element plus the thing on it being changed.
    /// <c>XBtnLogin/@color</c> targets an attribute by element id; <c>XTxtTitle/#text</c> targets the
    /// inline text; <c>screen:Login/@layer</c> targets a screen's own attribute.
    /// </summary>
    private static (XElement Element, string Target, string Owner) ResolveTarget(
        XDocument xml,
        string documentPath,
        string path)
    {
        int slash = path.LastIndexOf('/');
        if (slash <= 0 || slash == path.Length - 1)
        {
            throw new ArgumentException(
                "Patch path \"" + path + "\" must be <id>/@<attribute>, <id>/#text, or screen:<Name>/@<attribute>.");
        }

        string head = path.Substring(0, slash);
        string tail = path.Substring(slash + 1);

        string target;
        if (tail.Length > 1 && tail[0] == AttributeSigil)
        {
            target = tail.Substring(1);
        }
        else if (string.Equals(tail, TextTarget, StringComparison.Ordinal))
        {
            target = TextTarget;
        }
        else
        {
            throw new ArgumentException(
                "Patch path \"" + path + "\" must end in @<attribute> or " + TextSigil + "text.");
        }

        if (head.StartsWith(ScreenPrefix, StringComparison.Ordinal))
        {
            string screenName = head.Substring(ScreenPrefix.Length);
            XElement? screen = FindScreen(xml, screenName);
            if (screen == null)
            {
                throw new KeyNotFoundException(
                    "Screen '" + screenName + "' is not declared in " + documentPath + ".");
            }
            return (screen, target, "<Screen " + screenName + ">");
        }

        XElement? found = FindById(xml, head);
        if (found == null)
        {
            throw new KeyNotFoundException(
                "No element with id '" + head + "' in " + documentPath
                + ". Call xui_inspect to list the ids this document declares. "
                + "Note that ids produced by template expansion are not patchable here — edit the template instead.");
        }

        return (found, target, "<" + found.Name.LocalName + " " + head + ">");
    }

    private static XElement? FindScreen(XDocument xml, string name)
    {
        foreach (XElement element in xml.Descendants())
        {
            if (element.Name.LocalName != "Screen") continue;
            if (string.Equals(element.Attribute("name")?.Value, name, StringComparison.Ordinal)) return element;
        }
        return null;
    }

    private static XElement? FindById(XDocument xml, string id)
    {
        foreach (XElement element in xml.Descendants())
        {
            if (string.Equals(element.Attribute("id")?.Value, id, StringComparison.Ordinal)) return element;
        }
        return null;
    }

    private async Task<SceneWriteResult> CommitAsync(
        string before,
        string after,
        string absolutePath,
        string targetAsset,
        ChangeProvenance provenance,
        bool dryRun,
        string summary,
        string beforeHash,
        string afterHash,
        CancellationToken cancellationToken)
    {
        if (!dryRun
            && !string.IsNullOrEmpty(provenance.IdempotencyKey)
            && _journal.FindExisting(provenance.IdempotencyKey, provenance.Source) is { } existing)
        {
            return new SceneWriteResult(
                existing.ChangeId, targetAsset, existing.BeforeHash, existing.AfterHash, dryRun,
                "Idempotent replay of change " + existing.ChangeId + " (no write).");
        }

        EchoObject delta = ProvenancedDelta.Create(WrapText(before), WrapText(after), provenance);
        string changeId = ProvenancedDelta.GetChangeId(delta) ?? Guid.NewGuid().ToString("N");

        if (!dryRun)
        {
            File.WriteAllText(absolutePath, after);
            await _journal.AppendAsync(targetAsset, delta, provenance, beforeHash, afterHash, cancellationToken)
                .ConfigureAwait(false);
        }

        return new SceneWriteResult(changeId, targetAsset, beforeHash, afterHash, dryRun, summary);
    }

    /// <summary>Wraps document text so the Echo-shaped delta/journal path can carry it unchanged.</summary>
    private static EchoObject WrapText(string text)
    {
        var compound = EchoObject.NewCompound();
        compound[SourceField] = new EchoObject(text);
        return compound;
    }

    private string Resolve(string documentPath)
    {
        if (!documentPath.EndsWith(".xui", StringComparison.OrdinalIgnoreCase))
            throw new ArgumentException("xui tools require a .xui path, got '" + documentPath + "'.");

        string absolute = _project.ResolveAssetPath(documentPath);
        if (!File.Exists(absolute))
            throw new FileNotFoundException("XUI document not found: " + documentPath, absolute);
        return absolute;
    }

    internal static string HashText(string text)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
}
