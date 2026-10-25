// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

namespace XEngine.Data.Provenance;

/// <summary>
/// ★ Layer 0 (A2) — The on-disk operation journal. Every provenance-bearing change captured by
/// <c>ProvenancedDelta</c> is appended here as one JSON line, so the record survives process and
/// engine restarts. This is what lets an AI agent read "how did this project evolve to its current
/// state" — the gap Tuanjie 2.0 closes with "assets carry process information".
///
/// <para>Format: <c>{journalRoot}/{yyyy-MM-dd}.jsonl</c> (newline-delimited JSON, one ChangeRecord per
/// line). NDJSON is chosen over a single JSON array for append-safety: a crash mid-write corrupts at
/// most the last line, never the whole journal. The journal is append-only; trimming by age is the
/// only mutation (see <see cref="TrimAsync"/>).</para>
///
/// <para>Thread-safety: writes are serialized by a lock; reads snapshot the in-memory index. The
/// journal is independent of the live Undo stack (which is in-memory and cleared on scene load) —
/// the journal persists across all of that.</para>
/// </summary>
public sealed class ChangeJournal : IDisposable
{
    private readonly string _journalRoot;
    private readonly string _projectId;
    private readonly object _writeLock = new();
    private bool _disposed;

    /// <param name="journalRoot">Directory under which <c>{date}.jsonl</c> files live
    /// (typically <c>{projectRoot}/.xengine/journal</c>).</param>
    /// <param name="projectId">Stable project fingerprint (from ProjectIdentity) stamped on each record.</param>
    public ChangeJournal(string journalRoot, string projectId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(journalRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(projectId);
        _journalRoot = journalRoot;
        _projectId = projectId;
        Directory.CreateDirectory(_journalRoot);
    }

    /// <summary>
    /// ★ P0-1: Look up an existing record by idempotency key + source WITHOUT appending. Returns the
    /// original record if a duplicate exists in today's journal, or null if none. Callers should
    /// invoke this BEFORE mutating any file so an idempotent replay short-circuits cleanly (file
    /// untouched, journal untouched). The same dedup is also enforced inside <see cref="AppendAsync"/>
    /// as a second line of defense, but checking first avoids a clobbered-file / deduped-journal
    /// inconsistency on retry.
    /// </summary>
    public ChangeJournalRecord? FindExisting(string idempotencyKey, string source)
    {
        if (string.IsNullOrEmpty(idempotencyKey)) return null;
        ObjectDisposedException.ThrowIf(_disposed, this);

        string path = Path.Combine(_journalRoot, TodayFileName());
        if (!File.Exists(path)) return null;

        foreach (string existingLine in File.ReadLines(path))
        {
            if (string.IsNullOrWhiteSpace(existingLine)) continue;
            ChangeJournalRecord? existing = DeserializeRecord(existingLine);
            if (existing is null) continue;
            if (existing.Provenance.IdempotencyKey == idempotencyKey
                && existing.Provenance.Source == source)
            {
                return existing;
            }
        }
        return null;
    }

    /// <summary>
    /// Append a change to today's journal file. Computes before/after content hashes for drift
    /// detection and assigns a stable change id from the delta. Returns the persisted record.
    /// ★ P0-1: idempotency is ENFORCED — if a record with the same IdempotencyKey already exists in
    /// today's journal, the original record is returned without re-writing (deduplication).
    /// </summary>
    public Task<ChangeJournalRecord> AppendAsync(
        string targetAsset,
        EchoObject delta,
        ChangeProvenance provenance,
        string beforeHash,
        string afterHash,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        // Enrich the delta with provenance if not already attached (Undo may have attached it; a
        // direct caller may not). WithProvenance is idempotent.
        EchoObject enriched = ProvenancedDelta.WithProvenance(delta, provenance);
        string changeId = ProvenancedDelta.GetChangeId(enriched) ?? Guid.NewGuid().ToString("N");

        var record = new ChangeJournalRecord(
            changeId,
            _projectId,
            targetAsset,
            beforeHash,
            afterHash,
            enriched,
            provenance,
            DateTimeOffset.UtcNow);

        string line = SerializeRecord(record);

        lock (_writeLock)
        {
            cancellationToken.ThrowIfCancellationRequested();
            string path = Path.Combine(_journalRoot, TodayFileName());

            // ★ P0-1: Idempotency enforcement. If the caller supplied an IdempotencyKey and a record
            // with that key already exists in today's journal, return it without re-appending.
            // This prevents duplicate writes from AI retries / network blips.
            if (!string.IsNullOrEmpty(provenance.IdempotencyKey) && File.Exists(path))
            {
                foreach (string existingLine in File.ReadLines(path))
                {
                    if (string.IsNullOrWhiteSpace(existingLine)) continue;
                    ChangeJournalRecord? existing = DeserializeRecord(existingLine);
                    if (existing is null) continue;
                    if (existing.Provenance.IdempotencyKey == provenance.IdempotencyKey
                        && existing.Provenance.Source == provenance.Source)
                    {
                        // Idempotent hit: return the original record, skip the append.
                        return Task.FromResult(existing);
                    }
                }
            }

            // Append + newline; flush ensures durability before the call returns.
            // ★ P1-5: FileShare.None prevents cross-process write interleaving (Host + Editor).
            using var stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.None);
            using var writer = new StreamWriter(stream, Encoding.UTF8) { NewLine = "\n" };
            writer.WriteLine(line);
            writer.Flush();
        }

        return Task.FromResult(record);
    }

    /// <summary>
    /// Query the journal. Filters by target asset, time window, author, or intent substring; paged.
    /// Reads files newest-first so recent history is returned first.
    /// </summary>
    public Task<List<ChangeJournalRecord>> QueryAsync(JournalQuery query, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        var results = new List<ChangeJournalRecord>();
        string[] files = Directory.Exists(_journalRoot)
            ? Directory.GetFiles(_journalRoot, "*.jsonl")
            : Array.Empty<string>();

        // Newest date files first (file name is sortable yyyy-MM-dd).
        Array.Sort(files, StringComparer.Ordinal);
        Array.Reverse(files);

        foreach (string file in files)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (string line in File.ReadLines(file))
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                ChangeJournalRecord? record = DeserializeRecord(line);
                if (record is null) continue;
                if (!Matches(record, query)) continue;

                results.Add(record);
                if (results.Count >= query.Offset + query.Limit)
                {
                    // Collected enough for the page; stop. (Caller applies offset.)
                    goto done;
                }
            }
        }

    done:
        // Apply offset + limit (page window) after collecting newest-first.
        var page = results.Skip(query.Offset).Take(query.Limit).ToList();
        return Task.FromResult(page);
    }

    /// <summary>Drop journal files older than <paramref name="maxAge"/>. Returns count of files removed.</summary>
    public Task<int> TrimAsync(TimeSpan maxAge, CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var cutoff = DateTimeOffset.UtcNow - maxAge;
        int removed = 0;

        foreach (string file in Directory.GetFiles(_journalRoot, "*.jsonl"))
        {
            cancellationToken.ThrowIfCancellationRequested();
            string name = Path.GetFileNameWithoutExtension(file);
            // Parse the yyyy-MM-dd date from the file name; fall back to file write time.
            if (DateTimeOffset.TryParse(name, out DateTimeOffset fileDate))
            {
                if (fileDate < cutoff)
                {
                    File.Delete(file);
                    removed++;
                }
            }
            else if (File.GetLastWriteTimeUtc(file) < cutoff.UtcDateTime)
            {
                File.Delete(file);
                removed++;
            }
        }

        return Task.FromResult(removed);
    }

    public void Dispose()
    {
        _disposed = true;
    }

    private static string TodayFileName() => DateTimeOffset.UtcNow.ToString("yyyy-MM-dd") + ".jsonl";

    private static bool Matches(ChangeJournalRecord r, JournalQuery q)
    {
        if (q.TargetAsset is not null && !string.Equals(r.TargetAsset, q.TargetAsset, StringComparison.Ordinal))
        {
            return false;
        }

        if (q.SinceUtc is not null && r.RecordedAtUtc < q.SinceUtc) return false;
        if (q.UntilUtc is not null && r.RecordedAtUtc > q.UntilUtc) return false;

        if (q.ByAuthor is not null && !string.Equals(r.Provenance.Author, q.ByAuthor, StringComparison.Ordinal))
        {
            return false;
        }

        if (q.IntentContains is not null
            && (r.Provenance.Intent is null
                || r.Provenance.Intent.IndexOf(q.IntentContains, StringComparison.OrdinalIgnoreCase) < 0))
        {
            return false;
        }

        return true;
    }

    private static string SerializeRecord(ChangeJournalRecord r)
    {
        // Serialize the delta to its Echo text form and embed as a string, plus flatten provenance,
        // so the journal line is plain JSON readable by any tool (and by the AI Host) without Echo.
        return JsonSerializer.Serialize(new
        {
            changeId = r.ChangeId,
            projectId = r.ProjectId,
            targetAsset = r.TargetAsset,
            beforeHash = r.BeforeHash,
            afterHash = r.AfterHash,
            delta = r.Delta.WriteToString(),
            provenance = new
            {
                author = r.Provenance.Author,
                intent = r.Provenance.Intent,
                source = r.Provenance.Source,
                timestampUtcTicks = r.Provenance.TimestampUtc.UtcTicks,
                parentDeltaId = r.Provenance.ParentDeltaId,
                idempotencyKey = r.Provenance.IdempotencyKey,
            },
            recordedAtUtc = r.RecordedAtUtc,
        }, JsonOpts);
    }

    private static ChangeJournalRecord? DeserializeRecord(string line)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(line);
            JsonElement root = doc.RootElement;

            string changeId = root.GetProperty("changeId").GetString() ?? "";
            string projectId = root.GetProperty("projectId").GetString() ?? "";
            string targetAsset = root.GetProperty("targetAsset").GetString() ?? "";
            string beforeHash = root.GetProperty("beforeHash").GetString() ?? "";
            string afterHash = root.GetProperty("afterHash").GetString() ?? "";
            string deltaText = root.GetProperty("delta").GetString() ?? "";
            EchoObject delta = EchoObject.ReadFromString(deltaText);

            JsonElement p = root.GetProperty("provenance");
            var provenance = new ChangeProvenance
            {
                Author = TryGet(p, "author", "editor"),
                Intent = TryGet(p, "intent", null),
                Source = TryGet(p, "source", "editor"),
                TimestampUtc = p.TryGetProperty("timestampUtcTicks", out JsonElement ticks) && ticks.TryGetInt64(out long t)
                    ? new DateTimeOffset(t, TimeSpan.Zero)
                    : DateTimeOffset.UtcNow,
                ParentDeltaId = TryGet(p, "parentDeltaId", null),
                IdempotencyKey = TryGet(p, "idempotencyKey", null),
            };

            DateTimeOffset recordedAt = root.GetProperty("recordedAtUtc").GetDateTimeOffset();
            return new ChangeJournalRecord(changeId, projectId, targetAsset, beforeHash, afterHash, delta, provenance, recordedAt);
        }
        catch (JsonException)
        {
            return null; // skip malformed line
        }
    }

    private static string? TryGet(JsonElement obj, string name, string? defaultValue)
        => obj.TryGetProperty(name, out JsonElement el) && el.ValueKind == JsonValueKind.String
            ? el.GetString() : defaultValue;

    private static readonly JsonSerializerOptions JsonOpts = new(JsonSerializerDefaults.Web);
}

/// <summary>One persisted change record (engine-side twin of the AI Protocol ChangeRecord).</summary>
public sealed record ChangeJournalRecord(
    string ChangeId,
    string ProjectId,
    string TargetAsset,
    string BeforeHash,
    string AfterHash,
    EchoObject Delta,
    ChangeProvenance Provenance,
    DateTimeOffset RecordedAtUtc);

/// <summary>Filters for <see cref="ChangeJournal.QueryAsync"/> (engine-side twin of Protocol.JournalQuery).</summary>
public sealed record JournalQuery(
    string? TargetAsset = null,
    DateTimeOffset? SinceUtc = null,
    DateTimeOffset? UntilUtc = null,
    string? ByAuthor = null,
    string? IntentContains = null,
    int Offset = 0,
    int Limit = 100);
