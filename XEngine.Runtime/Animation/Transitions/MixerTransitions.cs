// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Collections.Generic;

using XEngine.Runtime;
using XEngine.Runtime.Resources;
using XEngine.Vector;

namespace XEngine.Animation.Transitions
{
    /// <summary>
    /// One serialized child of an inline mixer transition: a clip plus its blend metadata
    /// (1D threshold and/or 2D position and/or Direct parameter name).
    /// </summary>
    public class MixerChild
    {
        /// <summary>The clip this child plays. Must be resident at state-creation time.</summary>
        public AssetRef<AnimationClip> Clip = default;

        /// <summary>1D blend threshold (Linear mixers).</summary>
        public float Threshold;

        /// <summary>2D blend position (Cartesian / Directional mixers).</summary>
        public Float2 Position;

        /// <summary>Per-child weight parameter (Manual/Direct mixers).</summary>
        public string DirectParameterName = "";
    }

    /// <summary>Cold-path queries over <see cref="MixerChild"/> lists shared by all mixer transitions.</summary>
    internal static class MixerChildren
    {
        internal static bool AnyLooping(List<MixerChild> children)
        {
            for (int i = 0; i < children.Count; i++)
            {
                AnimationClip? c = children[i].Clip.Res;
                if (c != null && c.Wrap == AnimationWrapMode.Loop)
                    return true;
            }
            return false;
        }

        internal static float LongestDuration(List<MixerChild> children)
        {
            float max = 0f;
            for (int i = 0; i < children.Count; i++)
            {
                AnimationClip? c = children[i].Clip.Res;
                if (c != null && c.Duration > max)
                    max = (float)c.Duration;
            }
            return max;
        }
    }

    /// <summary>
    /// M48 — Animancer <c>LinearMixerTransition</c> equivalent: N clips blended by one float
    /// parameter through thresholds (idle/walk/run). Builds a code-side
    /// <see cref="BlendTreePlayable"/>; no .motion asset needed.
    /// </summary>
    public class LinearMixerTransition : AnimationTransition
    {
        /// <summary>Parameter name resolved against <c>Animator.Parameters</c> each frame.</summary>
        public string ParameterName = "Speed";

        /// <summary>The children, in threshold order.</summary>
        public List<MixerChild> Children = new();

        public override bool IsLooping => MixerChildren.AnyLooping(Children);
        public override float MaximumDuration => MixerChildren.LongestDuration(Children);

        public override AnimationPlayable CreateState(Animator animator)
            => animator.CreateTransitionMixerState(BlendType.OneD, Children);

        public override void Apply(AnimationPlayable state, Animator animator)
        {
            base.Apply(state, animator);
            animator.RegisterMixerParameters((BlendTreePlayable)state, ParameterName, null, null);
        }
    }

    /// <summary>
    /// M48 — 2D freeform Cartesian mixer (Animancer <c>CartesianMixerTransition</c> equivalent):
    /// children at (x, y) positions blended by two parameters, gradient-band weights.
    /// </summary>
    public class CartesianMixerTransition : AnimationTransition
    {
        /// <summary>X parameter name resolved against <c>Animator.Parameters</c>.</summary>
        public string ParameterName = "VelocityX";

        /// <summary>Y parameter name resolved against <c>Animator.Parameters</c>.</summary>
        public string ParameterNameY = "VelocityZ";

        /// <summary>The children with 2D positions.</summary>
        public List<MixerChild> Children = new();

        public override bool IsLooping => MixerChildren.AnyLooping(Children);
        public override float MaximumDuration => MixerChildren.LongestDuration(Children);

        public override AnimationPlayable CreateState(Animator animator)
            => animator.CreateTransitionMixerState(BlendType.TwoDCartesian, Children);

        public override void Apply(AnimationPlayable state, Animator animator)
        {
            base.Apply(state, animator);
            animator.RegisterMixerParameters((BlendTreePlayable)state, ParameterName, ParameterNameY, null);
        }
    }

    /// <summary>
    /// M48 — 2D directional mixer (Animancer <c>DirectionalMixerTransition</c> equivalent): children
    /// at angle+magnitude positions; the parameter is interpreted as (atan2-style angle, magnitude).
    /// </summary>
    public class DirectionalMixerTransition : AnimationTransition
    {
        /// <summary>X = angle input, Y = magnitude input (both parameter names).</summary>
        public string ParameterName = "MoveX";

        /// <summary>Y parameter name resolved against <c>Animator.Parameters</c>.</summary>
        public string ParameterNameY = "MoveY";

        /// <summary>The children with directional positions.</summary>
        public List<MixerChild> Children = new();

        public override bool IsLooping => MixerChildren.AnyLooping(Children);
        public override float MaximumDuration => MixerChildren.LongestDuration(Children);

        public override AnimationPlayable CreateState(Animator animator)
            => animator.CreateTransitionMixerState(BlendType.TwoDDirectional, Children);

        public override void Apply(AnimationPlayable state, Animator animator)
        {
            base.Apply(state, animator);
            animator.RegisterMixerParameters((BlendTreePlayable)state, ParameterName, ParameterNameY, null);
        }
    }

    /// <summary>
    /// M48 — manual/direct mixer (Animancer <c>ManualMixerTransition</c> equivalent): every child's
    /// weight comes from its own parameter (raw weighted sum, not renormalized), so gameplay code
    /// composes layers of clips explicitly.
    /// </summary>
    public class ManualMixerTransition : AnimationTransition
    {
        /// <summary>The children; each needs a <see cref="MixerChild.DirectParameterName"/>.</summary>
        public List<MixerChild> Children = new();

        public override bool IsLooping => MixerChildren.AnyLooping(Children);
        public override float MaximumDuration => MixerChildren.LongestDuration(Children);

        public override AnimationPlayable CreateState(Animator animator)
            => animator.CreateTransitionMixerState(BlendType.Direct, Children);

        public override void Apply(AnimationPlayable state, Animator animator)
        {
            base.Apply(state, animator);
            var mixer = (BlendTreePlayable)state;
            int n = mixer.InputCount;
            var directNames = new string?[n];
            bool any = false;
            for (int i = 0; i < n && i < Children.Count; i++)
            {
                string name = Children[i].DirectParameterName;
                if (!string.IsNullOrEmpty(name))
                {
                    directNames[i] = name;
                    any = true;
                }
            }
            animator.RegisterMixerParameters(mixer, null, null, any ? directNames : null);
        }
    }

    /// <summary>
    /// M48 — plays an authored <c>.motion</c> <see cref="BlendTree"/> asset through the code-driven
    /// parameter store instead of an FSM controller — the bridge between authored trees and
    /// Animancer-style play.
    /// </summary>
    public class BlendTreeTransition : AnimationTransition
    {
        /// <summary>The authored blend tree. Must be resident at state-creation time.</summary>
        public AssetRef<BlendTree> Tree = default;

        public override bool IsLooping
        {
            get
            {
                var tree = Tree.Res;
                if (tree == null) return false;
                var children = tree.Children;
                for (int i = 0; i < children.Count; i++)
                {
                    AnimationClip? c = children[i].Clip.Res;
                    if (c != null && c.Wrap == AnimationWrapMode.Loop)
                        return true;
                }
                return false;
            }
        }

        public override float MaximumDuration
        {
            get
            {
                var tree = Tree.Res;
                if (tree == null) return 0f;
                float max = 0f;
                var children = tree.Children;
                for (int i = 0; i < children.Count; i++)
                {
                    AnimationClip? c = children[i].Clip.Res;
                    if (c != null && c.Duration > max)
                        max = (float)c.Duration;
                }
                return max;
            }
        }

        public override AnimationPlayable CreateState(Animator animator)
        {
            BlendTree? tree = Tree.Res;
            if (tree == null)
                throw new System.InvalidOperationException(
                    $"BlendTreeTransition's tree is not loaded (AssetID {Tree.AssetID}).");
            BlendTreePlayable playable = BlendTreePlayable.Build(tree, animator.Skeleton!, author: null);
            animator.AddTransitionState(playable);
            return playable;
        }

        public override void Apply(AnimationPlayable state, Animator animator)
        {
            base.Apply(state, animator);
            BlendTree? tree = Tree.Res;
            string? nameX = tree != null && !string.IsNullOrEmpty(tree.ParameterName) ? tree.ParameterName : null;
            string? nameY = tree != null && !string.IsNullOrEmpty(tree.ParameterNameY) ? tree.ParameterNameY : null;
            string?[]? directNames = null;
            if (tree != null && tree.Type == BlendType.Direct)
            {
                var children = tree.Children;
                directNames = new string?[children.Count];
                bool any = false;
                for (int i = 0; i < children.Count; i++)
                {
                    if (!string.IsNullOrEmpty(children[i].DirectParameterName))
                    {
                        directNames[i] = children[i].DirectParameterName;
                        any = true;
                    }
                }
                if (!any)
                    directNames = null;
            }
            animator.RegisterMixerParameters((BlendTreePlayable)state, nameX, nameY, directNames);
        }
    }
}
