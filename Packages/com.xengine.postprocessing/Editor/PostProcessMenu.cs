// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Editor;
using XEngine.Editor.GUI;
using XEngine.Editor.GUI.Panels;
using XEngine.Editor.Theming;
using XEngine.Runtime;

namespace XEngine.PostProcessing.Editor;

internal static class PostProcessMenu
{
    [MenuItem("GameObject/Volume/Post-process Volume", priority: 91, Icon = EditorIcons.Cube)]
    static void CreateVolume()
    {
        var go = HierarchyPanel.CreateGameObject("Post-process Volume", MenuContext.ActiveGameObject);
        var volume = go.AddComponent<PostProcessVolume>();
        volume.isGlobal = true;
    }

    [MenuItem("GameObject/Volume/Post-process Layer", priority: 92, Icon = EditorIcons.Camera)]
    static void CreateLayerOnNewCamera()
    {
        var go = HierarchyPanel.CreateGameObject("Post-process Camera", MenuContext.ActiveGameObject);
        Camera camera = go.AddComponent<Camera>();
        camera.HDR = true;
        UniversalAdditionalCameraData data = UniversalAdditionalCameraData.GetOrAdd(camera);
        data.RenderPostProcessing = true;
        go.AddComponent<PostProcessLayer>();
    }
}
