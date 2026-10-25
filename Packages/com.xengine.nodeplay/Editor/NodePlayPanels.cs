using System;
using System.Collections.Generic;

using XEngine.Editor.GUI;
using XEngine.NodePlay;
using XEngine.NodePlay.BT;
using XEngine.OrigamiUI;
using XEngine.PaperUI;
using XEngine.Runtime;

namespace XEngine.NodePlay.Editor;

/// <summary>
/// Edits the graph-local blackboard (right panel top): rows per variable — name, typed value
/// field, public toggle (exposure surface for subgraph mapping / parameter presets) and remove;
/// an add-row with the preferred primitive types.
/// </summary>
internal static class NodePlayBlackboardPanel
{
    public const string SeparatorType = "— separator —";

    public static void Draw(Paper paper, string idPrefix, NPGraphAsset graph, Action onChanged, float height)
    {
        using (paper.Column(idPrefix + "_bb").Height(height).Enter())
        {
            Origami.Label(paper, idPrefix + "_bbh", $"Blackboard ({graph.LocalBlackboard.All.Count})")
                .Show();

            var variables = graph.LocalBlackboard.All;
            for (int i = 0; i < variables.Count; i++)
            {
                NPVariable variable = variables[i];
                using (paper.Column($"{idPrefix}_var{i}").Height(30).Enter())
                {
                    int row = i;
                    Origami.TextField(paper, $"{idPrefix}_vn{i}", variable.Name, v =>
                    {
                        string newName = v ?? "";
                        if (newName.Length > 0 && newName != variable.Name)
                            NodePlayRefactor.RenameVariable(graph, variable.Name, newName);
                        onChanged();
                    }).Show();

                    DrawValueField(paper, $"{idPrefix}_vv{i}", variable, onChanged);

                    Origami.Button(paper, $"{idPrefix}_vp{i}", variable.IsPublic ? "pub" : "priv", () =>
                    {
                        variable.IsPublic = !variable.IsPublic;
                        onChanged();
                    }).Show();

                    Origami.Button(paper, $"{idPrefix}_vx{i}", "x", () =>
                    {
                        graph.LocalBlackboard.Remove(variables[row].Name);
                        onChanged();
                    }).Show();
                }
            }

        }
    }

    /// <summary>Add-row used by the Vars tab (keeps the row list itself lean).</summary>
    public static void DrawAddRow(Paper paper, string idPrefix, NPGraphAsset graph, Action onChanged)
    {
        Origami.Button(paper, idPrefix + "_addf", "+ float", () =>
        {
            graph.LocalBlackboard.Add(NewName(graph, "value"), 0f, isPublic: true);
            onChanged();
        }).Show();
        Origami.Button(paper, idPrefix + "_addi", "+ int", () =>
        {
            graph.LocalBlackboard.Add(NewName(graph, "count"), 0, isPublic: true);
            onChanged();
        }).Show();
        Origami.Button(paper, idPrefix + "_adds", "+ string", () =>
        {
            graph.LocalBlackboard.Add(NewName(graph, "text"), "", isPublic: true);
            onChanged();
        }).Show();
        Origami.Button(paper, idPrefix + "_addb", "+ bool", () =>
        {
            graph.LocalBlackboard.Add(NewName(graph, "flag"), false, isPublic: true);
            onChanged();
        }).Show();
    }

    private static string NewName(NPGraphAsset graph, string stem)
    {
        int n = 2;
        string name = stem;
        while (graph.LocalBlackboard.Get(name) != null)
            name = stem + n++;
        return name;
    }

    /// <summary>Typed value editor routed by the stored value's runtime type.</summary>
    private static void DrawValueField(Paper paper, string id, NPVariable variable, Action onChanged)
    {
        switch (variable.Value)
        {
            case bool b:
                Origami.Button(paper, id, b ? "true" : "false", () =>
                {
                    variable.SetRawValue(!b);
                    onChanged();
                }).Show();
                break;
            case float f:
                Origami.TextField(paper, id, f.ToString(System.Globalization.CultureInfo.InvariantCulture), v =>
                {
                    if (float.TryParse(v, System.Globalization.CultureInfo.InvariantCulture, out float parsed))
                    {
                        variable.SetRawValue(parsed);
                        onChanged();
                    }
                }).Show();
                break;
            case int integer:
                Origami.TextField(paper, id, integer.ToString(), v =>
                {
                    if (int.TryParse(v, out int parsed))
                    {
                        variable.SetRawValue(parsed);
                        onChanged();
                    }
                }).Show();
                break;
            case string:
                Origami.TextField(paper, id, (string)variable.Value, v =>
                {
                    variable.SetRawValue(v ?? "");
                    onChanged();
                }).Show();
                break;
            default:
                // Generic payload (List<Vector>…): edit as Echo text round-trip.
                Origami.TextField(paper, id, Echo.Serializer.Serialize(variable.Value).WriteToString(), v =>
                {
                    try
                    {
                        object? parsed = Echo.Serializer.Deserialize(
                            Echo.EchoObject.ReadFromString(v ?? ""), variable.ValueType);
                        if (parsed != null)
                        {
                            variable.SetRawValue(parsed);
                            onChanged();
                        }
                    }
                    catch
                    {
                        // keep old value on malformed text — console surfaces parse errors in M13
                    }
                }).Show();
                break;
        }
    }
}

/// <summary>Edits an NPParameter (variable name / inline literal pair) inside any inspector.</summary>
[CustomPropertyEditor(typeof(NPParameter))]
public sealed class NPParameterPropertyEditor : PropertyEditor
{
    public override void OnGUI(Paper paper, string id, string label,
        object? value, Action<object?> onChange, int depth)
    {
        if (value is not NPParameter parameter)
            return;

        Origami.TextField(paper, id + "_n", parameter.VariableName, v =>
        {
            parameter.VariableName = v ?? "";
            onChange(parameter);
        }).Show();

        Origami.TextField(paper, id + "_v", parameter.InlineValue?.ToString() ?? "", v =>
        {
            parameter.InlineValue = v ?? "";
            onChange(parameter);
        }).Show();
    }
}

/// <summary>Edits the task on action/condition leaf nodes: label + a recursive field grid.</summary>
[CustomPropertyEditor(typeof(NPActionTask))]
public sealed class NPActionTaskPropertyEditor : PropertyEditor
{
    public override void OnGUI(Paper paper, string id, string label,
        object? value, Action<object?> onChange, int depth)
    {
        if (value is not NPTask task)
            return;

        Origami.TextField(paper, id + "_label", task.Label, v =>
        {
            task.Label = v ?? "";
            onChange(task);
        }).Show();

        Origami.PropertyGrid(paper, id + "_fields", task, null).Show();
    }
}

/// <summary>Condition tasks share the same editor shape as actions.</summary>
[CustomPropertyEditor(typeof(NPConditionTask))]
public sealed class NPConditionTaskPropertyEditor : PropertyEditor
{
    public override void OnGUI(Paper paper, string id, string label,
        object? value, Action<object?> onChange, int depth)
        => new NPActionTaskPropertyEditor().OnGUI(paper, id, label, value, onChange, depth);
}
