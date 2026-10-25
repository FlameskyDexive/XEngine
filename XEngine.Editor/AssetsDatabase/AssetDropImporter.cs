// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.IO;

using XEngine.Editor.Core;
using XEngine.Editor.GUI.Panels;
using XEngine.Editor.Projects;
using XEngine.Editor.Utils;
using XEngine.Runtime;

namespace XEngine.Editor;

/// <summary>
/// Bridges OS-level file drops (dragging a file from the OS file explorer onto the editor window)
/// into the asset import pipeline. Files released while the pointer is over the Project panel are
/// copied into the project's <c>Assets/</c> folder under the panel's current location; the
/// <see cref="AssetWatcher"/> then detects and imports them automatically.
/// <para/>
/// Mirrors Unity: external files dropped on the Project view are imported, while Hierarchy objects
/// dropped on the Project view become prefabs (handled separately by the in-editor Origami drag
/// system in <see cref="ProjectPanel.DispatchProjectDrop"/>).
/// </summary>
public static class AssetDropImporter
{
    /// <summary>
    /// Handle files dropped onto the editor window (forwarded from Silk.NET via
    /// <see cref="Window.FileDrop"/> as absolute paths). Only imports when the pointer is over the
    /// Project panel; drops elsewhere on the window are ignored.
    /// </summary>
    public static void HandleFileDrop(string[] files)
    {
        if (files == null || files.Length == 0) return;
        Project? project = Project.Current;
        if (project == null || EditorAssetBackend.Instance == null) return;

        // Gate on the pointer being over the Project panel so drops on Scene/Hierarchy/empty space
        // do nothing, matching the expectation of "drag into the Project view to import".
        if (EditorApplication.Instance?.IsMouseOverPanel(typeof(ProjectPanel)) != true) return;

        // Resolve the target folder from the Project panel's current location (empty = Assets root).
        string relativeTarget = ProjectPanel.Instance?.CurrentFolder ?? "";
        string assetsRoot = Path.GetFullPath(project.AssetsPath);
        string targetDir = string.IsNullOrEmpty(relativeTarget)
            ? assetsRoot
            : Path.Combine(assetsRoot, relativeTarget);
        Directory.CreateDirectory(targetDir);

        int imported = 0;
        foreach (string source in files)
        {
            if (string.IsNullOrWhiteSpace(source)) continue;

            string fullSource;
            try { fullSource = Path.GetFullPath(source); }
            catch (Exception ex)
            {
                Debug.LogError($"Invalid dropped path '{source}': {ex.Message}");
                continue;
            }

            // We mint our own .meta sidecars per-asset - never copy external ones in.
            if (fullSource.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) continue;

            // If the file is already inside Assets/ it's already part of the project; the watcher
            // handles any reimport. Don't copy it over itself.
            if (IsInsideAssets(fullSource, assetsRoot)) continue;

            try
            {
                if (File.Exists(fullSource))
                {
                    string destName = UniqueNames.ForFile(targetDir,
                        Path.GetFileNameWithoutExtension(fullSource), Path.GetExtension(fullSource));
                    File.Copy(fullSource, Path.Combine(targetDir, destName), overwrite: false);
                    imported++;
                }
                else if (Directory.Exists(fullSource))
                {
                    string folderName = Path.GetFileName(fullSource.TrimEnd(
                        Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
                    string destName = UniqueNames.ForFile(targetDir, folderName, "");
                    CopyDirectory(fullSource, Path.Combine(targetDir, destName));
                    imported++;
                }
            }
            catch (Exception ex)
            {
                Debug.LogError($"Failed to import dropped file '{fullSource}': {ex.Message}");
            }
        }

        // Refresh the folder index now so the panel reflects the new files immediately, without
        // waiting on the watcher's debounce. The watcher + ProcessFileChanges handle the import.
        EditorAssetBackend.Instance?.InvalidateFolderIndex();

        if (imported > 0)
        {
            string where = string.IsNullOrEmpty(relativeTarget) ? "" : "/" + relativeTarget;
            Debug.Log($"Imported {imported} file(s) into Assets{where}.");
        }
    }

    /// <summary>True when <paramref name="fullPath"/> is already located inside Assets/.</summary>
    private static bool IsInsideAssets(string fullPath, string assetsRoot)
    {
        string root = assetsRoot.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                    + Path.DirectorySeparatorChar;
        return fullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>Recursively copy a directory tree, skipping <c>.meta</c> sidecars we own.</summary>
    private static void CopyDirectory(string sourceDir, string destDir)
    {
        Directory.CreateDirectory(destDir);
        foreach (string file in Directory.EnumerateFiles(sourceDir))
        {
            if (file.EndsWith(".meta", StringComparison.OrdinalIgnoreCase)) continue;
            File.Copy(file, Path.Combine(destDir, Path.GetFileName(file)), overwrite: false);
        }
        foreach (string sub in Directory.EnumerateDirectories(sourceDir))
            CopyDirectory(sub, Path.Combine(destDir, Path.GetFileName(sub)));
    }
}
