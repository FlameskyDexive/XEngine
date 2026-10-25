// This file is part of the XEngine Game Engine (com.xengine.timeline package)
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Editor.GUI;
using XEngine.OrigamiUI;
using XEngine.PaperUI;

namespace XEngine.Timeline.Editor;

/// <summary>
/// Inspector for <see cref="SignalReceiver"/>: default fields plus a readable binding-name list.
/// </summary>
[CustomEditor(typeof(SignalReceiver))]
public class SignalReceiverEditor : CustomEditor
{
    public override void OnGUI(Paper paper, string id, object target)
    {
        DrawDefaultInspector(paper, id, target);
        var receiver = (SignalReceiver)target;
        Origami.Header(paper, id + "_bh", "Signal Bindings").Show();
        var bindings = receiver.Bindings;
        if (bindings.Count == 0)
        {
            Origami.Label(paper, id + "_empty", "(no bindings)").Show();
            return;
        }

        for (int i = 0; i < bindings.Count; i++)
        {
            var b = bindings[i];
            string label = string.IsNullOrEmpty(b.Name) ? "(unnamed)" : b.Name;
            string wired = b.Callback != null ? "wired" : "unwired";
            Origami.Label(paper, $"{id}_b{i}", $"{label}  ({wired})").Show();
        }
    }
}
