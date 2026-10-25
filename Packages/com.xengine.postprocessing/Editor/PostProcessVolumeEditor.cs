// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Editor.GUI;
using XEngine.PaperUI;

namespace XEngine.PostProcessing.Editor;

[CustomEditor(typeof(PostProcessVolume))]
public sealed class PostProcessVolumeEditor : CustomEditor
{
    public override void OnGUI(Paper paper, string id, object target)
        => DrawDefaultInspector(paper, id, target);
}
