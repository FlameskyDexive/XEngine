// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Collections.Generic;

using XEngine.Runtime;

namespace XEngine.Animation;

/// <summary>
/// M50 — a data-driven transition tuning table (Animancer <c>TransitionLibrary</c> equivalent):
/// per (fromKey → toKey) overrides for fade duration / normalized start time / speed, plus wildcard
/// entries. Designers retune how two animations hand off from one asset instead of every call site;
/// explicit arguments at a play call always win over the library.
/// </summary>
[CreateAssetMenu("Animation/Transition Library", Extension = ".transitionlibrary", Order = 92)]
public class TransitionLibraryAsset : EngineObject
{
    /// <summary>One (from → to) override. Empty keys act as wildcards; unset values don't override.</summary>
    public class Modifier
    {
        /// <summary>Source key (clip/transition name). Empty = any source.</summary>
        public string FromKey = "";

        /// <summary>Destination key (clip/transition name). Empty = any destination.</summary>
        public string ToKey = "";

        /// <summary>Override fade duration in seconds; negative = leave the caller's value.</summary>
        public float FadeDuration = -1f;

        /// <summary>Override normalized start time; NaN = leave the caller's value.</summary>
        public float NormalizedStartTime = float.NaN;

        /// <summary>Override playback speed; 0 or negative = leave the caller's value.</summary>
        public float Speed;

        /// <summary>Breaks ties between equally specific matches (lower first).</summary>
        public int Order;
    }

    /// <summary>All modifiers. Consultation picks the most specific match.</summary>
    public List<Modifier> Modifiers = new();

    /// <summary>
    /// Resolves the best modifier for (<paramref name="fromKey"/>, <paramref name="toKey"/>):
    /// both keys match beats one key beats wildcards; <see cref="Modifier.Order"/> breaks ties.
    /// Null keys count as wildcards on the caller's side. Returns null when nothing matches.
    /// </summary>
    public Modifier? FindBest(string? fromKey, string? toKey)
    {
        Modifier? best = null;
        int bestScore = -1;
        var modifiers = Modifiers;
        for (int i = 0; i < modifiers.Count; i++)
        {
            Modifier m = modifiers[i];
            bool fromMatch = string.IsNullOrEmpty(m.FromKey)
                || (fromKey != null && m.FromKey == fromKey);
            bool toMatch = string.IsNullOrEmpty(m.ToKey)
                || (toKey != null && m.ToKey == toKey);
            if (!fromMatch || !toMatch)
                continue;

            int score = (string.IsNullOrEmpty(m.FromKey) ? 0 : 2) + (string.IsNullOrEmpty(m.ToKey) ? 0 : 1);
            if (score < bestScore)
                continue;
            if (score == bestScore && best != null && m.Order >= best.Order)
                continue;
            best = m;
            bestScore = score;
        }
        return best;
    }
}
