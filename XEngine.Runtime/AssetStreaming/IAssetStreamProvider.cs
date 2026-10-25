// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

using XEngine.Async;

namespace XEngine.Runtime.AssetStreaming;

/// <summary>
/// Read-only logical asset source. Paths are relative, case-sensitive, and use forward slashes on
/// every platform so package layout does not inherit host path rules.
/// </summary>
public interface IAssetStreamProvider : IDisposable
{
    string Description { get; }

    /// <summary>
    /// Synchronous open. Desktop / Android / packaged mini-game files may use this. WASM must not
    /// call a blocking open (HTTP fetch, cache materialize); use <see cref="OpenReadAsync"/> instead.
    /// </summary>
    Stream? OpenRead(string path);

    /// <summary>
    /// Asynchronous open. Default wraps <see cref="OpenRead"/> for seekable local sources. Browser
    /// fetch and WASM cache materialization override this and must not block the rAF thread.
    /// </summary>
    XTask<Stream?> OpenReadAsync(string path, CancellationToken cancellationToken = default)
    {
        try
        {
            cancellationToken.ThrowIfCancellationRequested();
            return XTask.FromResult(OpenRead(path));
        }
        catch (OperationCanceledException)
        {
            return XTask.FromCanceled<Stream?>(cancellationToken);
        }
        catch (Exception exception)
        {
            return XTask.FromException<Stream?>(exception);
        }
    }

    IReadOnlyList<string> GetFiles(string directory, string extension);
}

/// <summary>Asset source that also exposes sandboxed file mutation within its provider root.</summary>
public interface IWritableAssetStreamProvider : IAssetStreamProvider
{
    Stream OpenWrite(string path, bool overwrite = true);

    bool Delete(string path);

    void CreateDirectory(string directory);
}

internal static class AssetStreamPath
{
    internal static string Normalize(string path, bool allowEmpty = false)
    {
        ArgumentNullException.ThrowIfNull(path);
        if (path.Length == 0)
        {
            if (allowEmpty)
                return string.Empty;
            throw new ArgumentException("Asset path cannot be empty.", nameof(path));
        }
        if (path[0] == '/' || path[^1] == '/' || path.Contains('\\'))
            throw new ArgumentException($"Asset path must be a relative POSIX path: {path}", nameof(path));

        int segmentStart = 0;
        while (segmentStart < path.Length)
        {
            int separator = path.IndexOf('/', segmentStart);
            int segmentLength = (separator < 0 ? path.Length : separator) - segmentStart;
            if (segmentLength == 0 ||
                (segmentLength == 1 && path[segmentStart] == '.') ||
                (segmentLength == 2 && path[segmentStart] == '.' && path[segmentStart + 1] == '.'))
            {
                throw new ArgumentException($"Asset path contains an invalid segment: {path}", nameof(path));
            }
            segmentStart = separator < 0 ? path.Length : separator + 1;
        }
        return path;
    }

    internal static string NormalizeExtension(string extension)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(extension);
        if (extension.Length < 2 || extension[0] != '.' || extension.Contains('/') || extension.Contains('\\'))
            throw new ArgumentException($"Invalid asset extension: {extension}", nameof(extension));
        return extension;
    }

    internal static string Combine(string directory, string fileName) =>
        directory.Length == 0 ? Normalize(fileName) : Normalize(directory + "/" + fileName);
}
