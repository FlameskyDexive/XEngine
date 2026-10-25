// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Collections.Generic;

using XEngine.Runtime;
using XEngine.Vector;

namespace XEngine.Animation;

/// <summary>How a <see cref="BlendTree"/> interpolates between its children.</summary>
public enum BlendType : byte
{
    /// <summary>Single parameter; children placed on a 1D threshold axis.</summary>
    OneD = 0,
    /// <summary>Two parameters; children at 2D positions, barycentric blend.</summary>
    TwoDCartesian = 1,
    /// <summary>Two parameters; children at directional positions, polar blend.</summary>
    TwoDDirectional = 2,
    /// <summary>Children weighted directly (no parameter interpolation).</summary>
    Direct = 3,
}

/// <summary>M60 — how a blend-tree child's clock aligns with its siblings (Unity time-sync modes).</summary>
public enum BlendTimeSync : byte
{
    /// <summary>Each child runs on its own clock (historical default).</summary>
    None = 0,
    /// <summary>Children are speed-scaled at build so all complete a cycle together (longest wins).</summary>
    Duration = 1,
    /// <summary>Children start at an authored normalized offset; per-frame drift correction keeps
    /// the offsets stable while blending.</summary>
    StartTime = 2,
    /// <summary>Foot-phase sync: like <see cref="StartTime"/> with a per-child phase offset
    /// (e.g. left/right clips 0 / 0.5) so contact frames line up; drift-corrected each frame.</summary>
    PhaseFoot = 3,
}

/// <summary>
/// A motion that blends N <see cref="AnimationClip"/> children by a parameter (e.g. Speed → idle/walk/run).
/// Stored as an Echo asset (<c>.motion</c>); used as a state's motion in an <see cref="AnimatorController"/>.
/// M8 implements 1D blend; 2D Cartesian/Directional/Direct are sketched for later.
/// </summary>
[CreateAssetMenu("Animation/Blend Tree", Extension = ".motion", Order = 91)]
public sealed class BlendTree : EngineObject
{
    /// <summary>How children are interpolated.</summary>
    public BlendType Type = BlendType.OneD;

    /// <summary>The parameter name driving the blend (resolved to an index by the compiler).</summary>
    public string ParameterName = string.Empty;

    /// <summary>★ M43 — Second parameter for 2D Freeform Cartesian / Directional.</summary>
    public string ParameterNameY = string.Empty;

    /// <summary>★ M43 — When true, 1D thresholds are evenly spaced in [0, 1] at play/compile time.</summary>
    public bool UseAutomaticThresholds;

    /// <summary>The child clips and their blend positions.</summary>
    public List<Child> Children = new();

    /// <summary>One child of a blend tree: a clip or nested tree plus its blend position.</summary>
    public class Child
    {
        public AssetRef<AnimationClip> Clip;
        /// <summary>★ M43 — Nested child BlendTree (takes precedence over <see cref="Clip"/> when set).</summary>
        public AssetRef<BlendTree> Tree;
        /// <summary>1D threshold (for <see cref="BlendType.OneD"/>). Direct preview weight when Type is Direct.</summary>
        public float Threshold;
        /// <summary>2D position (for Cartesian/Directional).</summary>
        public Float2 Position;
        /// <summary>★ M43 — Parameter that supplies this child's weight when Type is Direct.</summary>
        public string DirectParameterName = string.Empty;
        /// <summary>M60 — Playback speed multiplier for this child (1 = authored).</summary>
        public float Speed = 1f;
        /// <summary>M60 — Clock alignment with siblings.</summary>
        public BlendTimeSync TimeSync = BlendTimeSync.None;
        /// <summary>M60 — Normalized start/phase offset used by StartTime/PhaseFoot sync.</summary>
        public float Phase;
    }
}
