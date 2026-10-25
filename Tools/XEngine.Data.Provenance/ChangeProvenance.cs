// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

namespace XEngine.Data.Provenance;

/// <summary>
/// ★ Layer 0 — Who/why/when/what-source for a single recorded change. This is the engine-side
/// twin of <c>XEngine.Ai.Protocol.ChangeProvenance</c>; it lives in the engine layer (no AI
/// dependency) so editor operations and the on-disk journal both carry it. Without provenance an
/// AI can read a scene's current state but never WHY it evolved to it — exactly the gap Tuanjie 2.0
/// targets with "from designed-for-humans to designed-for-human-AI-collaboration".
/// </summary>
public sealed class ChangeProvenance
{
    /// <summary>Who made the change: a username, "editor", or an AI tool name.</summary>
    public string Author { get; set; } = "editor";

    /// <summary>Natural-language intent ("darken the scene to dusk for cutscene X").</summary>
    public string? Intent { get; set; }

    /// <summary>What produced the change: "editor.gizmo", "xengine-cli.scene-object-patch", ...</summary>
    public string Source { get; set; } = "editor";

    public DateTimeOffset TimestampUtc { get; set; } = DateTimeOffset.UtcNow;

    /// <summary>Optional stable id of the parent change this one builds on (delta-chain link).</summary>
    public string? ParentDeltaId { get; set; }

    /// <summary>For AI-driven writes, the idempotency key that produced this change.</summary>
    public string? IdempotencyKey { get; set; }

    /// <summary>Convenience factory for an interactive editor change (default author/source).</summary>
    public static ChangeProvenance ForEditor(string? intent = null, string? reason = null)
        => new() { Author = "editor", Source = "editor", Intent = intent ?? reason };

    /// <summary>Factory for an AI-tool-driven change.</summary>
    public static ChangeProvenance ForTool(string toolName, string? intent, string? idempotencyKey)
        => new() { Author = toolName, Source = toolName, Intent = intent, IdempotencyKey = idempotencyKey };
}
