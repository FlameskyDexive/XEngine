using XEngine.Ai.Protocol;

namespace XEngine.Ai.Host;

/// <summary>
/// ★ Layer 1 (B1) — Reads the Layer 0 operation journal (written by the editor's
/// <c>ChangeJournalService</c>) and surfaces it to AI agents as <see cref="ChangeRecord"/> entries.
/// This is the tool that answers "how did this scene evolve to its current state" — the core
/// process-awareness capability. Reads the on-disk NDJSON directly (deltas are stored as EchoText
/// strings), so the Host needs no Provenance-layer dependency and no running engine.
/// </summary>
internal static class JournalReader
{
    /// <summary>
    /// Query the project's journal under <c>{projectRoot}/.xengine/journal/</c>. Returns changes
    /// newest-first, filtered by asset/author/intent, paged.
    /// </summary>
    public static JournalPage Query(string projectRoot, JournalReadQuery query)
    {
        string journalDir = Path.Combine(projectRoot, ".xengine", "journal");
        var all = new List<ChangeRecord>();

        if (Directory.Exists(journalDir))
        {
            string[] files = Directory.GetFiles(journalDir, "*.jsonl");
            Array.Sort(files, StringComparer.Ordinal);   // yyyy-MM-dd ascending
            Array.Reverse(files);                         // newest first

            foreach (string file in files)
            {
                foreach (string line in File.ReadLines(file))
                {
                    if (string.IsNullOrWhiteSpace(line)) continue;
                    ChangeRecord? rec = ParseLine(line);
                    if (rec is null) continue;
                    if (!Matches(rec, query)) continue;
                    all.Add(rec);
                }
            }
        }

        int total = all.Count;
        var page = all.Skip(query.Offset).Take(query.Limit).ToList();
        bool hasMore = query.Offset + page.Count < total;
        return new JournalPage(query.TargetAsset, query.Offset, query.Limit, total, page, hasMore);
    }

    private static bool Matches(ChangeRecord r, JournalReadQuery q)
    {
        if (q.TargetAsset is not null && !string.Equals(r.TargetAsset, q.TargetAsset, StringComparison.Ordinal))
            return false;
        if (q.SinceUtc is not null && r.RecordedAtUtc < q.SinceUtc) return false;
        if (q.UntilUtc is not null && r.RecordedAtUtc > q.UntilUtc) return false;
        if (q.ByAuthor is not null && !string.Equals(r.Author, q.ByAuthor, StringComparison.Ordinal))
            return false;
        if (q.IntentContains is not null
            && (r.Intent is null
                || r.Intent.IndexOf(q.IntentContains, StringComparison.OrdinalIgnoreCase) < 0))
        {
            return false;
        }

        return true;
    }

    private static ChangeRecord? ParseLine(string line)
    {
        try
        {
            using JsonDocument doc = JsonDocument.Parse(line);
            JsonElement root = doc.RootElement;
            JsonElement p = root.GetProperty("provenance");
            return new ChangeRecord(
                ChangeId: root.GetProperty("changeId").GetString() ?? "",
                ProjectId: root.GetProperty("projectId").GetString() ?? "",
                TargetAsset: root.GetProperty("targetAsset").GetString() ?? "",
                BeforeHash: root.GetProperty("beforeHash").GetString() ?? "",
                AfterHash: root.GetProperty("afterHash").GetString() ?? "",
                DeltaText: root.GetProperty("delta").GetString() ?? "",
                Author: p.GetProperty("author").GetString() ?? "editor",
                Intent: p.TryGetProperty("intent", out JsonElement intent) && intent.ValueKind == JsonValueKind.String
                    ? intent.GetString() : null,
                Source: p.GetProperty("source").GetString() ?? "editor",
                TimestampUtc: p.TryGetProperty("timestampUtcTicks", out JsonElement ticks) && ticks.TryGetInt64(out long t)
                    ? new DateTimeOffset(t, TimeSpan.Zero) : DateTimeOffset.UtcNow,
                RecordedAtUtc: root.GetProperty("recordedAtUtc").GetDateTimeOffset());
        }
        catch (JsonException)
        {
            return null; // skip malformed line
        }
        catch (KeyNotFoundException)
        {
            return null; // skip incomplete line
        }
    }
}

/// <summary>Query filters for the journal-query tool (Host-side twin of Protocol.JournalQuery).</summary>
internal sealed record JournalReadQuery(
    string? TargetAsset = null,
    DateTimeOffset? SinceUtc = null,
    DateTimeOffset? UntilUtc = null,
    string? ByAuthor = null,
    string? IntentContains = null,
    int Offset = 0,
    int Limit = 100);
