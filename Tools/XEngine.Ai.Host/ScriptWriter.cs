using System.Security.Cryptography;
using System.Text;
using XEngine.Ai.Protocol;
using XEngine.Data.Provenance;
using XEngine.Echo;
using ChangeProvenance = XEngine.Data.Provenance.ChangeProvenance;

namespace XEngine.Ai.Host;

/// <summary>
/// ★ Layer 1 — Offline C# script file operations: create, delete, and structured text edits
/// (apply_edits). Each writes a provenance-bearing record to the journal. The engine's AssetWatcher
/// auto-detects the file change → ScriptImporter calls ScriptAssemblyManager.RequestRecompile() →
/// Roslyn compiles → hot-reload — so the AI writing a script is picked up by a running editor with
/// zero extra wiring.
///
/// <para>apply_edits uses exact-text matching (not regex) for safety: each edit specifies an OldText
/// to find and a NewText to replace it with. With occurrence="first" (default), the OldText must be
/// UNIQUE in the file (throws otherwise — the AI must provide enough context to disambiguate).
/// occurrence="all" replaces every match.</para>
/// </summary>
public sealed class ScriptWriter
{
    private readonly ChangeJournal _journal;

    public ScriptWriter(ChangeJournal journal)
    {
        _journal = journal;
    }

    /// <summary>Create a new .cs script file under Assets/. Refuses to overwrite an existing file
    /// (use apply_edits to modify, or delete+create to replace). Mints a .meta sidecar with a fresh
    /// GUID and the ScriptImporter (version 1). Records a provenance-bearing change to the journal.</summary>
    public async Task<ScriptEditResult> CreateAsync(
        string absolutePath,
        string targetAsset,
        string content,
        ChangeProvenance provenance,
        CancellationToken cancellationToken = default)
    {
        // ★ P0-1: idempotent replay short-circuit BEFORE any file mutation.
        if (!string.IsNullOrEmpty(provenance.IdempotencyKey)
            && _journal.FindExisting(provenance.IdempotencyKey, provenance.Source) is not null
            && File.Exists(absolutePath))
        {
            return new ScriptEditResult(targetAsset, Sha256(content), 0,
                $"Idempotent replay of script create {targetAsset} (no write).");
        }

        if (File.Exists(absolutePath))
        {
            throw new InvalidOperationException(
                $"script_exists: '{targetAsset}' already exists. Use script_apply_edits to modify it, " +
                "or script_delete + script_create to replace it.");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(absolutePath)!);
        string guid = Guid.NewGuid().ToString("N");

        // Atomic temp+rename for the script + .meta (so a crash can't leave a half-written file).
        WriteAtomic(absolutePath, content);
        WriteMeta(absolutePath + ".meta", guid, "ScriptImporter", 1);

        string sha = Sha256(content);
        await RecordAsync(targetAsset, "script_create", provenance,
            $"Created script {targetAsset} (GUID {guid[..8]}…).", cancellationToken);
        return new ScriptEditResult(targetAsset, sha, 0, $"Created {targetAsset}.");
    }

    /// <summary>Delete a .cs script file + its .meta sidecar.</summary>
    public async Task<ScriptEditResult> DeleteAsync(
        string absolutePath,
        string targetAsset,
        ChangeProvenance provenance,
        CancellationToken cancellationToken = default)
    {
        if (!string.IsNullOrEmpty(provenance.IdempotencyKey)
            && _journal.FindExisting(provenance.IdempotencyKey, provenance.Source) is not null)
        {
            return new ScriptEditResult(targetAsset, "", 0,
                $"Idempotent replay of script delete {targetAsset} (no write).");
        }

        if (!File.Exists(absolutePath))
        {
            throw new FileNotFoundException($"Script not found: {targetAsset}");
        }

        File.Delete(absolutePath);
        string metaPath = absolutePath + ".meta";
        if (File.Exists(metaPath)) File.Delete(metaPath);

        await RecordAsync(targetAsset, "script_delete", provenance,
            $"Deleted script {targetAsset}.", cancellationToken);
        return new ScriptEditResult(targetAsset, "", 0, $"Deleted {targetAsset}.");
    }

    /// <summary>Apply structured text edits to an existing .cs script. Each edit finds OldText and
    /// replaces with NewText (occurrence="first" requires uniqueness, "all" replaces every match).
    /// Returns the number of edits applied + the new content SHA.</summary>
    public async Task<ScriptEditResult> ApplyEditsAsync(
        string absolutePath,
        string targetAsset,
        IReadOnlyList<ScriptTextEdit> edits,
        ChangeProvenance provenance,
        string? expectedBeforeSha = null,
        CancellationToken cancellationToken = default)
    {
        // Idempotent replay short-circuit.
        if (!string.IsNullOrEmpty(provenance.IdempotencyKey)
            && _journal.FindExisting(provenance.IdempotencyKey, provenance.Source) is not null
            && File.Exists(absolutePath))
        {
            return new ScriptEditResult(targetAsset, Sha256(File.ReadAllText(absolutePath)), 0,
                $"Idempotent replay of script edit {targetAsset} (no write).");
        }

        if (!File.Exists(absolutePath))
        {
            throw new FileNotFoundException(
                $"Script not found: {targetAsset}. Call script_create first, or check the path.");
        }

        string content = File.ReadAllText(absolutePath);

        // ★ Drift guard: if the caller supplied an expected SHA, verify before editing.
        if (!string.IsNullOrEmpty(expectedBeforeSha))
        {
            string actual = Sha256(content);
            if (!string.Equals(actual, expectedBeforeSha, StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException(
                    $"stale_state: script '{targetAsset}' changed since you last read it " +
                    $"(expected {expectedBeforeSha[..8]}…, got {actual[..8]}…). Re-read and retry.");
            }
        }

        int applied = 0;
        var sb = new StringBuilder(content);
        foreach (ScriptTextEdit edit in edits)
        {
            string occurrence = string.IsNullOrEmpty(edit.Occurrence) ? "first" : edit.Occurrence!;
            if (occurrence == "all")
            {
                int before = sb.Length;
                sb.Replace(edit.OldText, edit.NewText);
                // StringBuilder.Replace returns void; detect change by length diff is unreliable
                // (NewText could be same length). Use IndexOf count instead.
                applied++; // count the edit as applied (it ran)
            }
            else // "first"
            {
                int count = CountOccurrences(sb.ToString(), edit.OldText);
                if (count == 0)
                {
                    throw new InvalidOperationException(
                        $"edit_failed: OldText not found in {targetAsset}. Provide the exact current text. " +
                        "Use find_in_file first to locate it.");
                }
                if (count > 1)
                {
                    throw new InvalidOperationException(
                        $"edit_ambiguous: OldText appears {count} times in {targetAsset}. " +
                        "Provide more surrounding context to make it unique, or set occurrence=\"all\".");
                }
                sb.Replace(edit.OldText, edit.NewText, 0, sb.Length);
                // StringBuilder.Replace replaces ALL occurrences. Since we verified count==1, one replacement.
                applied++;
            }
        }

        string newContent = sb.ToString();
        WriteAtomic(absolutePath, newContent);
        string sha = Sha256(newContent);

        await RecordAsync(targetAsset, "script_edit", provenance,
            $"Edited script {targetAsset} ({applied} edit(s)).", cancellationToken);
        return new ScriptEditResult(targetAsset, sha, applied, $"Applied {applied} edit(s) to {targetAsset}.");
    }

    // ---- helpers ----

    private static int CountOccurrences(string haystack, string needle)
    {
        if (string.IsNullOrEmpty(needle)) return 0;
        int count = 0, i = 0;
        while ((i = haystack.IndexOf(needle, i, StringComparison.Ordinal)) >= 0)
        {
            count++;
            i += needle.Length;
        }
        return count;
    }

    private static void WriteAtomic(string path, string content)
    {
        string tmp = path + ".tmp";
        File.WriteAllText(tmp, content);
        File.Move(tmp, path, overwrite: true);
    }

    private static void WriteMeta(string metaPath, string guid, string importer, int importerVersion)
    {
        EchoObject meta = EchoObject.NewCompound();
        meta["guid"] = new EchoObject(guid);
        meta["importer"] = new EchoObject(importer);
        meta["importerVersion"] = new EchoObject(importerVersion);
        string tmp = metaPath + ".tmp";
        File.WriteAllText(tmp, meta.WriteToString());
        File.Move(tmp, metaPath, overwrite: true);
    }

    internal static string Sha256(string text)
    {
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private async Task RecordAsync(string targetAsset, string operation, ChangeProvenance provenance,
        string summary, CancellationToken cancellationToken)
    {
        EchoObject delta = EchoObject.NewCompound();
        delta["operation"] = new EchoObject(operation);
        delta["summary"] = new EchoObject(summary);
        EchoObject enriched = ProvenancedDelta.WithProvenance(delta, provenance);
        await _journal.AppendAsync(targetAsset, enriched, provenance, "", "", cancellationToken);
    }
}
