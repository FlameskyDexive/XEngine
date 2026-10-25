// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.IO;

using XEngine.Animation;
using XEngine.Echo;
using XEngine.Editor.Core;
using XEngine.Editor.GUI.Panels;
using XEngine.Editor.Projects;
using XEngine.Runtime;

namespace XEngine.Editor.AnimationEditor;

/// <summary>
/// Shared save / open helpers for animation asset inspectors (M17).
/// </summary>
public static class AnimationAssetEditorUtil
{
    public static bool Save(EngineObject asset, AssetEntry entry)
    {
        if (Project.Current == null) return false;

        string absolutePath = Path.Combine(Project.Current.AssetsPath, entry.Path);
        try
        {
            Guid savedId = asset.AssetID;
            asset.AssetID = Guid.Empty;
            EchoObject echo = Serializer.Serialize(typeof(object), asset);
            asset.AssetID = savedId;
            File.WriteAllText(absolutePath, echo.WriteToString());
            EditorAssetBackend.Instance?.Reimport(entry.Guid);
            return true;
        }
        catch (Exception ex)
        {
            Debug.LogError($"Failed to save '{entry.Path}': {ex.Message}");
            return false;
        }
    }

    public static void OpenAnimatorWindow(AnimatorController? controller)
    {
        var app = EditorApplication.Instance;
        if (app == null) return;
        app.OpenPanel(typeof(AnimatorWindow));
        if (app.FindOpenPanel(typeof(AnimatorWindow)) is AnimatorWindow win)
            win.SetController(controller);
    }

    public static void OpenAnimationWindow(AnimationClip? clip)
    {
        var app = EditorApplication.Instance;
        if (app == null) return;
        app.OpenPanel(typeof(AnimationClipWindow));
        if (app.FindOpenPanel(typeof(AnimationClipWindow)) is AnimationClipWindow win)
        {
            GameObject? root = Selection.ActiveObject as GameObject;
            if (root == null)
            {
                var selected = Selection.Selected;
                for (int i = 0; i < selected.Count; i++)
                {
                    if (selected[i] is GameObject g)
                    {
                        root = g;
                        break;
                    }
                }
            }
            win.Bind(AnimationWindowBinding.FindAnimationRoot(root), clip);
        }
    }
}
