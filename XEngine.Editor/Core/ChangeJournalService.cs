// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Threading;
using System.Threading.Tasks;

using XEngine.Data.Provenance;
using XEngine.Editor.GUI.SceneView;
using XEngine.Echo;

namespace XEngine.Editor.Core;

/// <summary>
/// ★ Layer 0 (A2) — Bridges the editor's live <see cref="Undo.OnPropertyChanged"/> stream into the
/// on-disk <see cref="ChangeJournal"/>. The journal is the durable record that lets an AI agent
/// (or the journal-query tool, B1) read "how did this scene evolve to its current state".
///
/// <para>Attach on editor startup, detach on shutdown. Targets are keyed by the open scene's relative
/// path (the common case); non-scene targets are recorded under "unknown" until asset-path resolution
/// is added in B2.</para>
///
/// <para>This service owns the single journal instance per project. It is the editor-side counterpart
/// of the offline journal tools the AI Host (Layer 1) exposes.</para>
/// </summary>
public sealed class ChangeJournalService : IDisposable
{
    private readonly ChangeJournal _journal;
    private readonly Action<Undo.PropertyChangedEventArgs>? _handler;
    private bool _disposed;

    /// <param name="projectRoot">Project root; the journal lives under <c>{root}/.xengine/journal</c>.</param>
    /// <param name="projectId">Stable project fingerprint (ProjectIdentity), stamped on each record.</param>
    public ChangeJournalService(string projectRoot, string projectId)
    {
        string journalRoot = System.IO.Path.Combine(projectRoot, ".xengine", "journal");
        _journal = new ChangeJournal(journalRoot, projectId);
        _handler = OnPropertyChanged;
        Undo.OnPropertyChanged += _handler;
    }

    /// <summary>The underlying journal, for direct query/trim by tooling (B1 journal-query).</summary>
    public ChangeJournal Journal => _journal;

    private void OnPropertyChanged(Undo.PropertyChangedEventArgs e)
    {
        if (_disposed) return;

        // Resolve a best-effort target asset path. The most common change source is the open scene.
        string targetAsset = EditorSceneManager.CurrentScenePath ?? "unknown";

        // The delta already carries provenance (Undo attaches CurrentProvenance via ProvenancedDelta.Create).
        ChangeProvenance provenance =
            ProvenancedDelta.GetProvenance(e.Delta) ?? ChangeProvenance.ForEditor(e.Description);

        // before/after hashes aren't tracked by the Undo event today; record the delta's content hash
        // as both so a later, richer Undo path can fill in real before/after. This still lets the
        // journal detect that *something* changed and is replayable.
        string hash = ProvenancedDelta.ContentHash(e.Delta);

        // Fire-and-forget append: the journal write is serialized and fast; we don't block the editor
        // frame on disk I/O. Errors are swallowed here (a failed journal write must never break the
        // editor) — production wiring would log them via Debug.LogError.
        _ = _journal.AppendAsync(targetAsset, e.Delta, provenance, beforeHash: hash, afterHash: hash)
            .ContinueWith(static t =>
            {
                if (t.IsFaulted) { /* swallow; see note above */ _ = t.Exception; }
            }, TaskContinuationOptions.ExecuteSynchronously);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        if (_handler is not null)
        {
            Undo.OnPropertyChanged -= _handler;
        }

        _journal.Dispose();
    }
}
