// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace XEngine.XUI.Core;

/// <summary>One control the generated binding exposes.</summary>
public sealed class XuiBindingMember
{
    public XuiBindingMember(string id, string property, string field, string type)
    {
        Id = id;
        Property = property;
        Field = field;
        Type = type;
    }

    /// <summary>The document id, e.g. <c>XBtnLogin</c>.</summary>
    public string Id { get; }

    /// <summary>The exposed property, e.g. <c>BtnLogin</c>.</summary>
    public string Property { get; }

    /// <summary>The backing field, e.g. <c>_btnLogin</c>.</summary>
    public string Field { get; }

    /// <summary>The XGUI component type the property returns.</summary>
    public string Type { get; }
}

/// <summary>The files a scaffold run would write.</summary>
public sealed class XuiScaffoldOutput
{
    public XuiScaffoldOutput(
        string screenName,
        string viewFileName,
        string viewSource,
        string controllerFileName,
        string controllerSource,
        IReadOnlyList<XuiBindingMember> members)
    {
        ScreenName = screenName;
        ViewFileName = viewFileName;
        ViewSource = viewSource;
        ControllerFileName = controllerFileName;
        ControllerSource = controllerSource;
        Members = members;
    }

    public string ScreenName { get; }
    public string ViewFileName { get; }
    public string ViewSource { get; }
    public string ControllerFileName { get; }
    public string ControllerSource { get; }
    public IReadOnlyList<XuiBindingMember> Members { get; }
}

/// <summary>
/// Emits the view binding and the controller skeleton for a screen.
/// </summary>
/// <remarks>
/// <para>
/// XEngine's script pipeline shells out to <c>dotnet build</c> and wires no analyzers, so an
/// incremental source generator is not an option here. Writing real files turns out to be the better
/// answer for an agent anyway: the generated binding is greppable, diffable, and indexed by
/// <c>code_symbols</c>, so "what is this screen's API" is answerable without running anything.
/// </para>
/// <para>
/// The split matters. The <c>.g.cs</c> is derived and always overwritten; the controller is written
/// once and never touched again, because it is the file a human puts logic in.
/// </para>
/// </remarks>
public static class XuiViewCodeGenerator
{
    private const string Indent = "    ";

    /// <summary>
    /// Collects the bindable ids of an expanded screen, in document order.
    /// </summary>
    public static List<XuiBindingMember> CollectMembers(ElementNode expandedScreenRoot)
    {
        var members = new List<XuiBindingMember>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (int i = 0; i < expandedScreenRoot.Children.Count; i++)
            Collect(expandedScreenRoot.Children[i], members, seen);
        return members;
    }

    private static void Collect(ElementNode node, List<XuiBindingMember> members, HashSet<string> seen)
    {
        string? id = node.Id;
        XuiTagInfo? tag = XuiTagLibrary.Find(node.Tag);

        // A behaviour attaches to its parent instead of creating a node, so a binding for its id would
        // resolve to nothing at runtime. Better to emit no property than a property that returns null.
        if (tag is { Kind: XuiTagKind.Behaviour }) tag = null;

        if (id != null && tag != null && seen.Add(id))
        {
            members.Add(new XuiBindingMember(
                id,
                XuiTagLibrary.PropertyNameFor(id),
                XuiTagLibrary.FieldNameFor(id),
                tag.BindingType));

            // A Btn or Toggle with inline text also builds a label child, and reaching it is the usual
            // reason someone would otherwise go hunting through the hierarchy by hand. A <Text> carries
            // its own text, so it gets no label binding.
            if (tag.BuildsTextChild && !string.IsNullOrEmpty(node.Text))
            {
                string labelId = id + "Label";
                if (seen.Add(labelId))
                {
                    members.Add(new XuiBindingMember(
                        labelId,
                        XuiTagLibrary.PropertyNameFor(labelId),
                        XuiTagLibrary.FieldNameFor(labelId),
                        "Text"));
                }
            }
        }

        for (int i = 0; i < node.Children.Count; i++)
            Collect(node.Children[i], members, seen);
    }

    /// <summary>
    /// Generates both files for one screen.
    /// </summary>
    /// <param name="screen">The screen definition, for its name and layer.</param>
    /// <param name="expandedRoot">The screen's element tree after template expansion.</param>
    /// <param name="namespaceName">Namespace for both files. Empty emits none.</param>
    /// <param name="documentPath">Project-relative document path, quoted in the generated header.</param>
    public static XuiScaffoldOutput Generate(
        ScreenDef screen,
        ElementNode expandedRoot,
        string namespaceName,
        string documentPath)
    {
        ArgumentNullException.ThrowIfNull(screen);
        ArgumentNullException.ThrowIfNull(expandedRoot);

        List<XuiBindingMember> members = CollectMembers(expandedRoot);
        string viewType = screen.Name + "View";

        return new XuiScaffoldOutput(
            screen.Name,
            viewType + ".g.cs",
            GenerateView(screen, viewType, members, namespaceName, documentPath),
            screen.Name + ".cs",
            GenerateController(screen, viewType, members, namespaceName, documentPath),
            members);
    }

    // ------------------------------------------------------------------
    //  View binding (generated, always overwritten)
    // ------------------------------------------------------------------

    private static string GenerateView(
        ScreenDef screen,
        string viewType,
        List<XuiBindingMember> members,
        string namespaceName,
        string documentPath)
    {
        var builder = new StringBuilder();
        builder.Append("// <auto-generated />\n");
        builder.Append("// Generated by xui_scaffold from ").Append(documentPath).Append(".\n");
        builder.Append("// Edits are lost on the next run — change the document and re-scaffold.\n\n");
        builder.Append("using XEngine.Runtime.UI;\n");
        // RectTransform (the Frame binding type) lives in XEngine.Vector, not Runtime.UI.
        builder.Append("using XEngine.Vector;\n");
        builder.Append("using XEngine.XUI;\n");
        builder.Append("using XEngine.XUI.Core;\n\n");

        bool scoped = !string.IsNullOrEmpty(namespaceName);
        if (scoped) builder.Append("namespace ").Append(namespaceName).Append(";\n\n");

        builder.Append("/// <summary>Typed access to every id declared by the <c>").Append(screen.Name)
            .Append("</c> screen.</summary>\n");
        builder.Append("public sealed partial class ").Append(viewType).Append(" : XuiView\n{\n");
        builder.Append(Indent).Append("public ").Append(viewType)
            .Append("(XuiScreenInstance screen) : base(screen) { }\n");

        for (int i = 0; i < members.Count; i++)
        {
            XuiBindingMember member = members[i];
            builder.Append('\n');
            builder.Append(Indent).Append("private ").Append(member.Type).Append("? ")
                .Append(member.Field).Append(";\n");
            builder.Append(Indent).Append("/// <summary>&lt;").Append(member.Id).Append("&gt;</summary>\n");
            builder.Append(Indent).Append("public ").Append(member.Type).Append(' ').Append(member.Property)
                .Append(" => ").Append(member.Field).Append(" ??= Require<").Append(member.Type)
                .Append(">(\"").Append(member.Id).Append("\");\n");
        }

        builder.Append('\n').Append(Indent)
            .Append("/// <summary>Dropped on hot reload so no reference outlives the objects it points at.</summary>\n");
        builder.Append(Indent).Append("protected override void ClearCache()\n").Append(Indent).Append("{\n");
        for (int i = 0; i < members.Count; i++)
            builder.Append(Indent).Append(Indent).Append(members[i].Field).Append(" = null;\n");
        builder.Append(Indent).Append("}\n");
        builder.Append("}\n\n");

        // The CreateView override and the registration live here rather than in the hand-written file:
        // both are derived from the document, and neither is something anyone should have to maintain.
        builder.Append("public partial class ").Append(screen.Name).Append('\n').Append("{\n");
        builder.Append(Indent).Append("protected override ").Append(viewType)
            .Append(" CreateView(XuiScreenInstance screen) => new ").Append(viewType).Append("(screen);\n");
        builder.Append("}\n\n");

        builder.Append("/// <summary>Registers the screen when this assembly loads, so there is no central list.</summary>\n");
        builder.Append("internal static class ").Append(screen.Name).Append("Registration\n{\n");
        builder.Append(Indent).Append("[System.Runtime.CompilerServices.ModuleInitializer]\n");
        builder.Append(Indent).Append("[XEngine.Runtime.OnAssemblyLoad]\n");
        builder.Append(Indent).Append("internal static void Register()\n");
        builder.Append(Indent).Append(Indent).Append("=> Xui.Register<").Append(screen.Name)
            .Append(">(\"").Append(screen.Name).Append("\", XuiLayerKind.")
            .Append(screen.Layer.ToString()).Append(");\n");
        builder.Append("}\n");

        return builder.ToString();
    }

    // ------------------------------------------------------------------
    //  Controller skeleton (written once, never overwritten)
    // ------------------------------------------------------------------

    private static string GenerateController(
        ScreenDef screen,
        string viewType,
        List<XuiBindingMember> members,
        string namespaceName,
        string documentPath)
    {
        var builder = new StringBuilder();
        builder.Append("using XEngine.XUI;\n");
        builder.Append("using XEngine.XUI.Core;\n\n");

        bool scoped = !string.IsNullOrEmpty(namespaceName);
        if (scoped) builder.Append("namespace ").Append(namespaceName).Append(";\n\n");

        builder.Append("/// <summary>\n");
        builder.Append("/// Business logic for the <c>").Append(screen.Name)
            .Append("</c> screen (").Append(documentPath).Append(").\n");
        builder.Append("/// Reach controls through <c>View</c>; this class never touches a GameObject.\n");
        builder.Append("/// </summary>\n");
        builder.Append("[XuiScreen(\"").Append(screen.Name).Append("\")]\n");
        builder.Append("public partial class ").Append(screen.Name)
            .Append(" : XuiController<").Append(viewType).Append(">\n{\n");

        builder.Append(Indent).Append("/// <summary>Runs once. Subscribe here, not in OnShow.</summary>\n");
        builder.Append(Indent).Append("protected override void OnRegisterEvents()\n");
        builder.Append(Indent).Append("{\n");
        AppendEventHints(builder, members);
        builder.Append(Indent).Append("}\n\n");

        builder.Append(Indent).Append("/// <summary>Runs every time the screen becomes visible.</summary>\n");
        builder.Append(Indent).Append("protected override void OnShow(object? context)\n");
        builder.Append(Indent).Append("{\n");
        builder.Append(Indent).Append(Indent).Append("// TODO: populate the screen from context.\n");
        builder.Append(Indent).Append("}\n");
        builder.Append("}\n");

        return builder.ToString();
    }

    /// <summary>
    /// Seeds the handler block with the controls that actually raise events, commented out. A blank
    /// method body tells the next reader nothing; a list of what this screen can do tells them where
    /// to start.
    /// </summary>
    private static void AppendEventHints(StringBuilder builder, List<XuiBindingMember> members)
    {
        int hints = 0;
        for (int i = 0; i < members.Count; i++)
        {
            XuiBindingMember member = members[i];
            string? evt = member.Type switch
            {
                "Button" => "View." + member.Property + ".OnClick += () => { };",
                "Toggle" => "View." + member.Property + ".OnValueChanged += isOn => { };",
                "Slider" => "View." + member.Property + ".OnValueChanged += value => { };",
                "InputField" => "View." + member.Property + ".OnSubmitted += text => { };",
                _ => null,
            };
            if (evt == null) continue;

            builder.Append(Indent).Append(Indent).Append("// ").Append(evt).Append('\n');
            hints++;
        }

        if (hints == 0)
        {
            builder.Append(Indent).Append(Indent)
                .Append("// This screen declares no interactive controls yet.\n");
        }
    }

    /// <summary>Formats a member list for a tool result.</summary>
    public static string DescribeMembers(IReadOnlyList<XuiBindingMember> members)
    {
        if (members.Count == 0) return "no bindable ids";

        var builder = new StringBuilder();
        builder.Append(members.Count.ToString(CultureInfo.InvariantCulture)).Append(" binding(s): ");
        for (int i = 0; i < members.Count; i++)
        {
            if (i > 0) builder.Append(", ");
            builder.Append(members[i].Type).Append(' ').Append(members[i].Property);
        }
        return builder.ToString();
    }
}
