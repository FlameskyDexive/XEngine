using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

using XEngine.Editor.Projects.Scripting;
using XEngine.OrigamiUI;
using XEngine.Runtime;
using Projects = XEngine.Editor.Projects;

namespace XEngine.NodePlay.Editor;

/// <summary>
/// Task Wizard (M19): scaffolds a new action/condition task source file into the project's
/// Assets/Scripts/NodePlay/ folder with the standard attribute skeleton, then refreshes so the
/// script compiler picks it up. Pure <see cref="Scaffold"/> function is unit-tested.
/// </summary>
internal static class NodePlayTaskWizard
{
    public enum BaseKind
    {
        Action,
        Condition,
    }

    /// <summary>Pure scaffold text generator.</summary>
    public static string Scaffold(BaseKind kind, string className, string category, string description)
    {
        string safeName = Sanitize(className);
        string baseType = kind == BaseKind.Action ? "NPActionTask" : "NPConditionTask";
        var sb = new StringBuilder();
        sb.AppendLine("// Scaffolded by the NodePlay Task Wizard — edit freely.");
        sb.AppendLine("using XEngine.NodePlay;");
        sb.AppendLine();
        sb.AppendLine("namespace " + WizardNamespace() + ";");
        sb.AppendLine();
        sb.AppendLine("[NPName(\"" + safeName + "\", 0)]");
        sb.AppendLine("[NPCategory(\"" + (string.IsNullOrEmpty(category) ? "Custom" : category) + "\")]");
        if (!string.IsNullOrEmpty(description))
            sb.AppendLine("[NPDescription(\"" + description.Replace("\"", "'") + "\")]");
        sb.AppendLine("public sealed class " + safeName + " : " + baseType);
        sb.AppendLine("{");
        if (kind == BaseKind.Action)
        {
            sb.AppendLine("    /// <summary>Adjustable from the inspector (slider in 0..1).</summary>");
            sb.AppendLine("    [NPSlider(0f, 1f)]");
            sb.AppendLine("    public float Strength = 1f;");
            sb.AppendLine();
            sb.AppendLine("    protected override void OnExecute()");
            sb.AppendLine("    {");
            sb.AppendLine("        // first tick — do the work");
            sb.AppendLine("        EndAction(true);");
            sb.AppendLine("    }");
            sb.AppendLine();
            sb.AppendLine("    protected override void OnUpdate()");
            sb.AppendLine("    {");
            sb.AppendLine("        // later ticks until EndAction");
            sb.AppendLine("    }");
        }
        else
        {
            sb.AppendLine("    /// <summary>Adjustable from the inspector.</summary>");
            sb.AppendLine("    public float Threshold = 1f;");
            sb.AppendLine();
            sb.AppendLine("    protected override bool OnCheck()");
            sb.AppendLine("    {");
            sb.AppendLine("        return Threshold > 0f;");
            sb.AppendLine("    }");
        }
        sb.AppendLine("}");
        return sb.ToString();
    }

    /// <summary>Write the scaffold into Assets/Scripts/NodePlay/&lt;name&gt;.cs (editor action).</summary>
    public static string? WriteToProject(BaseKind kind, string className, string category, string description)
    {
        string text = Scaffold(kind, className, category, description);
        string? assets = Projects.Project.Current?.AssetsPath;
        if (assets == null)
            return null;

        string dir = Path.Combine(assets, "Scripts", "NodePlay");
        Directory.CreateDirectory(dir);
        string path = Path.Combine(dir, Sanitize(className) + ".cs");
        File.WriteAllText(path, text);
        return path;
    }

    /// <summary>Open the wizard menu (editor): pick base kind then a name via inline input field flow.</summary>
    public static void Show()
    {
        ContextMenu.ShowAtPointer(build =>
        {
            build.Item("Action Task…", () => OpenNameInput(NodePlayTaskWizard.BaseKind.Action));
            build.Item("Condition Task…", () => OpenNameInput(NodePlayTaskWizard.BaseKind.Condition));
        });
    }

    private static void OpenNameInput(BaseKind kind)
    {
        // v1: fixed-name flow via Toast prompt semantics — the picked name comes from the
        // most recent clipboard text if it is a valid identifier, else a timestamped default.
        string candidate = Input.Clipboard;
        if (string.IsNullOrWhiteSpace(candidate) || !IsIdentifier(candidate))
            candidate = "Custom" + kind + DateTime.UtcNow.ToString("HHmmss");
        string? path = WriteToProject(kind, candidate, "Custom", "Scaffolded task");
        Toasts.Success("NodePlay", path == null ? "Write failed (no project)" : "Scaffolded " + Path.GetFileName(path));
    }

    internal static string Sanitize(string name)
    {
        var sb = new StringBuilder();
        foreach (char c in name)
        {
            if (char.IsLetterOrDigit(c) || c == '_')
                sb.Append(c);
        }

        return sb.Length > 0 && !char.IsDigit(sb[0]) ? sb.ToString() : "Task" + sb.ToString();
    }

    private static bool IsIdentifier(string s)
    {
        if (string.IsNullOrEmpty(s) || char.IsDigit(s[0]))
            return false;
        foreach (char c in s)
        {
            if (!char.IsLetterOrDigit(c) && c != '_')
                return false;
        }

        return true;
    }

    private static string WizardNamespace() => "NodePlayCustom";
}
