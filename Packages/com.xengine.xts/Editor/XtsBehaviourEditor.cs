using System;
using XEngine.Editor.Core;
using System;
using XEngine.Editor.GUI;
using System;
using XEngine.OrigamiUI;
using System;
using XEngine.PaperUI;
using System;
using XEngine.Runtime;

namespace XEngine.XTS.Editor;

/// <summary>
/// Inspector for <see cref="XtsBehaviour"/>: default field drawing plus
/// live script status and a reload action.
/// </summary>
[CustomEditor(typeof(XtsBehaviour))]
public sealed class XtsBehaviourEditor : CustomEditor
{
    public override void OnGUI(Paper paper, string id, object target)
    {
        var behaviour = (XtsBehaviour)target;
        DrawDefaultInspector(paper, $"{id}_def", target);

        if (!string.IsNullOrEmpty(behaviour.LastScriptError))
        {
            Origami.Header(paper, $"{id}_err", "Script error");
            Origami.Label(paper, $"{id}_errmsg", behaviour.LastScriptError).Show();
        }
        else
        {
            Origami.Label(paper, $"{id}_ok", "Script OK").Show();
        }

        Origami.Button(paper, $"{id}_reload", "Reload script instance", () =>
        {
            behaviour.CreateScript();
        }).Show();
    }
}
