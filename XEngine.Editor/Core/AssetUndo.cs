// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.IO;

namespace XEngine.Editor.Core;

/// <summary>
/// Undoable wrappers around asset file operations. GUIDs travel with .meta files, so a
/// move/rename is fully reversible; registering the reverse move on the Undo stack gives
/// Project-panel drags and renames Ctrl+Z the way scene edits already have it.
/// Deletion is deliberately NOT an undo step — deleted files go to the OS recycle bin
/// (see <see cref="Utils.RecycleBin"/>), matching Unity.
/// </summary>
public static class AssetUndo
{
    /// <summary>Move/rename an asset through the backend and push the reverse move as undo.</summary>
    public static bool MoveAsset(string oldRelativePath, string newRelativePath)
    {
        var db = EditorAssetBackend.Instance;
        if (db == null || !db.MoveAsset(oldRelativePath, newRelativePath)) return false;
        Undo.RegisterAction($"Move {Path.GetFileName(oldRelativePath)}",
            undo: () => EditorAssetBackend.Instance?.MoveAsset(newRelativePath, oldRelativePath),
            redo: () => EditorAssetBackend.Instance?.MoveAsset(oldRelativePath, newRelativePath));
        return true;
    }

    /// <summary>Move/rename a folder through the backend and push the reverse move as undo.</summary>
    public static bool MoveFolder(string oldRelativePath, string newRelativePath)
    {
        var db = EditorAssetBackend.Instance;
        if (db == null || !db.MoveFolder(oldRelativePath, newRelativePath)) return false;
        Undo.RegisterAction($"Move {Path.GetFileName(oldRelativePath)}",
            undo: () => EditorAssetBackend.Instance?.MoveFolder(newRelativePath, oldRelativePath),
            redo: () => EditorAssetBackend.Instance?.MoveFolder(oldRelativePath, newRelativePath));
        return true;
    }
}
