using System;

using XEngine.Editor.GUI;
using XEngine.Editor.GUI.Widgets;
using XEngine.PaperUI;

using Gradient = XEngine.Runtime.Gradient;
namespace XEngine.Editor.Inspector;

// ================================================================
//  Gradient Property Editor
// ================================================================

[CustomPropertyEditor(typeof(Gradient))]
public class GradientPropertyEditor : PropertyEditor
{
    public override void OnGUI(Paper paper, string id, string label, object? value, Action<object?> onChange, int depth)
    {
        var gradient = value as Gradient ?? new Gradient();

        EditorGUI.Row(paper, id, label, () =>
            GradientField.Create(paper, $"{id}_gf", gradient,
                v => onChange(v)).Show());
    }
}
