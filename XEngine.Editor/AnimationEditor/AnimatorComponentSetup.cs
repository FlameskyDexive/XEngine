// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.IO;

using XEngine.Animation;
using XEngine.Editor.Projects;
using XEngine.Runtime;

namespace XEngine.Editor.AnimationEditor;

/// <summary>
/// Create an AnimatorController from a clip, persist it next to that clip, and migrate
/// legacy <see cref="AnimationComponent"/> instances to <see cref="Animator"/>.
/// </summary>
public static class AnimatorComponentSetup
{
    public static AnimatorController CreateControllerForClip(AnimationClip? clip, string? assetName = null)
    {
        string stateName = !string.IsNullOrEmpty(clip?.Name) ? clip!.Name : "New State";
        var controller = new AnimatorController
        {
            Name = !string.IsNullOrEmpty(assetName) ? assetName : stateName,
            DefaultStateName = stateName,
        };
        controller.States.Add(new AnimatorController.State
        {
            Name = stateName,
            Speed = 1f,
            IsLooping = true,
            Motion = clip != null ? new AssetRef<AnimationClip>(clip) : default,
        });
        return controller;
    }

    /// <summary>
    /// Writes a .controller next to the animator's clip (or at the Assets root) and binds it.
    /// Returns the project-relative path, or null if the asset database is unavailable.
    /// </summary>
    public static string? CreateAndBindController(Animator animator)
    {
        if (animator == null) return null;
        var db = EditorAssetBackend.Instance;
        if (db == null || Project.Current == null) return null;

        AnimationClip? clip = animator.Clip.Res;
        var controller = CreateControllerForClip(clip, animator.GameObject?.Name);
        string relative = NextControllerPath(clip, controller.Name);
        db.CreateAsset(controller, relative);
        animator.Controller = new AssetRef<AnimatorController>(controller);
        return relative;
    }

    public static Animator ConvertLegacy(GameObject go)
    {
        var legacy = go.GetComponent<AnimationComponent>();
        var animator = go.GetComponent<Animator>() ?? go.AddComponent<Animator>();
        if (legacy != null)
        {
            if (legacy.DefaultClip.Res != null || legacy.DefaultClip.AssetID != Guid.Empty)
                animator.Clip = legacy.DefaultClip;
            else if (legacy.Clips.Count > 0)
                animator.Clip = legacy.Clips[0];
            go.RemoveComponent(legacy);
        }
        return animator;
    }

    private static string NextControllerPath(AnimationClip? clip, string baseName)
    {
        string folder = "";
        if (!string.IsNullOrEmpty(clip?.AssetPath))
            folder = Path.GetDirectoryName(clip!.AssetPath)?.Replace('\\', '/') ?? "";

        string stem = SanitizeFileName(baseName);
        string relative = Combine(folder, stem + ".controller");
        var db = EditorAssetBackend.Instance;
        if (db != null && db.GetEntry(relative) == null)
            return relative;

        for (int i = 1; i < 100; i++)
        {
            relative = Combine(folder, $"{stem} {i}.controller");
            if (db == null || db.GetEntry(relative) == null)
                return relative;
        }

        return Combine(folder, $"{stem} {Guid.NewGuid():N}.controller");
    }

    private static string Combine(string folder, string file)
        => string.IsNullOrEmpty(folder) ? file : $"{folder}/{file}";

    private static string SanitizeFileName(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "New Animator Controller";
        char[] invalid = Path.GetInvalidFileNameChars();
        var chars = name.Trim().ToCharArray();
        for (int i = 0; i < chars.Length; i++)
        {
            for (int j = 0; j < invalid.Length; j++)
            {
                if (chars[i] == invalid[j])
                {
                    chars[i] = '_';
                    break;
                }
            }
        }
        return new string(chars);
    }
}
