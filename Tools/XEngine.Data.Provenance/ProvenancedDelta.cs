// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

namespace XEngine.Data.Provenance;

/// <summary>
/// ★ Layer 0 (A0) — Wraps an EchoObject field-level delta with provenance, WITHOUT modifying the
/// vendored Echo library. The Echo <c>CreateDelta</c> primitive is complete and tested but has zero
/// call sites in the engine (grep-confirmed); this class is where it gets "wired in" as the single
/// operation-recording primitive.
///
/// <para>An Echo delta is itself a compound with an <c>Operations</c> list
/// (<c>EchoObject.CreateDelta</c>). We attach a sibling <c>Provenance</c> compound + a stable
/// <c>ChangeId</c>, so the whole thing is still a plain serializable <see cref="EchoObject"/>
/// (round-trips through WriteToString/ReadFromString) but now carries who/why/when.</para>
///
/// <para>This is additive: <c>ApplyDelta</c> only reads <c>Operations</c>, so the extra tags are
/// invisible to replay.</para>
/// </summary>
public static class ProvenancedDelta
{
    // Top-level tag names. An Echo delta from CreateDelta only ever puts "Operations" at the root,
    // so "Provenance"/"ChangeId" do not collide.
    private const string OperationsTag = "Operations";
    private const string ProvenanceTag = "Provenance";
    private const string ChangeIdTag = "ChangeId";

    /// <summary>
    /// Build a provenance-bearing delta from a before/after pair. Computes the structured field-level
    /// diff via <see cref="EchoObject.CreateDelta"/>, then attaches provenance and a stable id.
    /// </summary>
    public static EchoObject Create(EchoObject before, EchoObject after, ChangeProvenance provenance)
    {
        EchoObject delta = EchoObject.CreateDelta(before, after);
        // The string indexer replaces any existing tag (and re-parents cleanly); Add() would throw
        // on a duplicate key, so use the indexer for idempotency.
        delta[ChangeIdTag] = new EchoObject(NewChangeId(before, after));
        WriteProvenance(delta, provenance);
        return delta;
    }

    /// <summary>
    /// Attach provenance to an already-built delta (e.g. one captured by Undo before provenance was
    /// known). Idempotent.
    /// </summary>
    public static EchoObject WithProvenance(EchoObject delta, ChangeProvenance provenance)
    {
        if (!delta.TryGet(ChangeIdTag, out _))
        {
            delta[ChangeIdTag] = new EchoObject(NewChangeId(delta, delta));
        }

        WriteProvenance(delta, provenance);
        return delta;
    }

    /// <summary>Read the operations list of a (possibly provenance-bearing) delta for replay.</summary>
    public static EchoObject GetOperations(EchoObject delta)
    {
        if (!delta.TryGet(OperationsTag, out EchoObject? ops))
        {
            throw new ArgumentException("Delta has no Operations list.", nameof(delta));
        }

        return ops!;
    }

    /// <summary>Read attached provenance, or null when the delta predates provenance capture.</summary>
    public static ChangeProvenance? GetProvenance(EchoObject delta)
    {
        if (!delta.TryGet(ProvenanceTag, out EchoObject? prov))
        {
            return null;
        }

        return ReadProvenance(prov!);
    }

    /// <summary>The stable change id, or null when absent.</summary>
    public static string? GetChangeId(EchoObject delta)
        => delta.TryGet(ChangeIdTag, out EchoObject? id) ? id!.StringValue : null;

    /// <summary>
    /// Stable content hash of an Echo object's serialized form (UTF-8 EchoText → SHA-256 → lowercase
    /// hex, truncated to 16 chars). Used for before/after drift detection on the journal (A2) so a
    /// replay can detect when the target moved underneath a stored delta.
    /// </summary>
    public static string ContentHash(EchoObject echo)
    {
        string text = echo.WriteToString();
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(text));
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static string NewChangeId(EchoObject a, EchoObject b)
    {
        // Content-derived so the same logical change is stable across runs (not clock-dependent).
        string h = ContentHash(a) + ContentHash(b);
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(h));
        return Convert.ToHexString(hash).ToLowerInvariant()[..16];
    }

    private static void WriteProvenance(EchoObject delta, ChangeProvenance prov)
    {
        EchoObject p = EchoObject.NewCompound();
        p["Author"] = new EchoObject(prov.Author);
        // Echo has no DateTimeOffset ctor; store Utc ticks (long) for lossless round-trip.
        p["Source"] = new EchoObject(prov.Source);
        p["TimestampUtcTicks"] = new EchoObject(prov.TimestampUtc.UtcTicks);
        if (prov.Intent is not null)
        {
            p["Intent"] = new EchoObject(prov.Intent);
        }

        if (prov.ParentDeltaId is not null)
        {
            p["ParentDeltaId"] = new EchoObject(prov.ParentDeltaId);
        }

        if (prov.IdempotencyKey is not null)
        {
            p["IdempotencyKey"] = new EchoObject(prov.IdempotencyKey);
        }

        // The string indexer setter replaces an existing tag under that key (clearing the old one's
        // parent) — so re-attaching provenance is safe and idempotent.
        delta[ProvenanceTag] = p;
    }

    private static ChangeProvenance ReadProvenance(EchoObject p)
    {
        ChangeProvenance prov = new();
        if (p.TryGet("Author", out EchoObject? a) && a!.TagType == EchoType.String)
        {
            prov.Author = a.StringValue;
        }

        if (p.TryGet("Intent", out EchoObject? intent) && intent!.TagType == EchoType.String)
        {
            prov.Intent = intent.StringValue;
        }

        if (p.TryGet("Source", out EchoObject? s) && s!.TagType == EchoType.String)
        {
            prov.Source = s.StringValue;
        }

        if (p.TryGet("TimestampUtcTicks", out EchoObject? ts) && ts!.TagType == EchoType.Long)
        {
            prov.TimestampUtc = new DateTimeOffset(ts.LongValue, TimeSpan.Zero);
        }

        if (p.TryGet("ParentDeltaId", out EchoObject? parent) && parent!.TagType == EchoType.String)
        {
            prov.ParentDeltaId = parent.StringValue;
        }

        if (p.TryGet("IdempotencyKey", out EchoObject? key) && key!.TagType == EchoType.String)
        {
            prov.IdempotencyKey = key.StringValue;
        }

        return prov;
    }
}
