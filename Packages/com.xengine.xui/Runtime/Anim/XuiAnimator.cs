// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using XEngine.Echo;
using XEngine.Runtime;
using XEngine.Runtime.UI;
using XEngine.Vector;
using XEngine.XUI.Core;

namespace XEngine.XUI;

/// <summary>
/// One authored transition: which channels change between which two states, over how long, on which
/// trigger.
/// </summary>
/// <remarks>
/// Every channel is an explicit <b>pair</b>, never a single target. "Move to (0,-24)" cannot express an
/// entrance and an exit with the same attribute, and picking a direction from the trigger name would be
/// magic that fails silently the first time someone names a trigger something else. A channel that is
/// not authored is left alone entirely, so two transitions on one element can own different channels.
/// </remarks>
public sealed class XuiAnimTrack
{
    /// <summary>Trigger name — <c>show</c>, <c>hide</c>, or anything a controller plays by name.</summary>
    public string Trigger { get; set; } = "show";

    /// <summary>Start offset in canvas pixels, relative to wherever layout put the element.</summary>
    public Float2 MoveFrom { get; set; }

    /// <summary>End offset. (0,0) is exactly where layout put the element.</summary>
    public Float2 MoveTo { get; set; }

    /// <summary>True when this transition owns the offset channel.</summary>
    public bool AnimatesMove { get; set; }

    public Float2 ScaleFrom { get; set; } = new(1f, 1f);

    public Float2 ScaleTo { get; set; } = new(1f, 1f);

    /// <summary>True when this transition owns the scale channel.</summary>
    public bool AnimatesScale { get; set; }

    public float FadeFrom { get; set; } = 1f;

    public float FadeTo { get; set; } = 1f;

    /// <summary>True when this transition owns the alpha channel.</summary>
    public bool AnimatesFade { get; set; }

    public float Duration { get; set; } = 0.15f;

    public float Delay { get; set; }

    public XuiEase Ease { get; set; } = XuiEase.OutQuad;

    /// <summary>True when the transition would change nothing at all.</summary>
    public bool IsEmpty => !AnimatesMove && !AnimatesScale && !AnimatesFade;
}

/// <summary>
/// Plays authored transitions on one element.
/// </summary>
/// <remarks>
/// <para>
/// Movement is a <b>relative offset</b>, never an absolute position. Layout owns where an element sits
/// — a <c>VStack</c> rewrites its children's rects every rebuild — so an animation that set a position
/// would be overwritten, or would fight the container and win in whichever order happened to run last.
/// An offset added on top of the layout result composes with any anchor and any container.
/// </para>
/// <para>
/// Time is advanced by <see cref="Tick"/> rather than read from a clock inside the component, so the
/// same code runs in play mode, in the editor, and in a test that steps time by hand. That is what
/// makes a transition verifiable at all.
/// </para>
/// </remarks>
[RequireComponent(typeof(RectTransform))]
[AddComponentMenu("UI/XUI/Animator")]
public sealed class XuiAnimator : UIBehaviour
{
    [SerializeIgnore] private readonly List<XuiAnimTrack> _tracks = new();
    [SerializeIgnore] private readonly List<Playing> _playing = new();

    [SerializeIgnore] private Float2 _restAnchored;
    [SerializeIgnore] private bool _capturedRest;

    /// <summary>Current offset from where layout put the element.</summary>
    public Float2 Offset { get; private set; }

    /// <summary>Tracks authored on this element.</summary>
    public IReadOnlyList<XuiAnimTrack> Tracks => _tracks;

    /// <summary>True while any transition is running.</summary>
    public bool IsPlaying => _playing.Count > 0;

    public void AddTrack(XuiAnimTrack track)
    {
        if (track != null) _tracks.Add(track);
    }

    /// <summary>
    /// Starts every track with this trigger and applies its start state at once.
    /// </summary>
    /// <remarks>
    /// Applying the start state immediately rather than on the next tick is what stops an entrance from
    /// flashing at its final position for one frame — the whole visible difference between a transition
    /// that looks intentional and one that looks like a glitch.
    /// </remarks>
    public bool Play(string trigger)
    {
        bool started = false;
        for (int i = 0; i < _tracks.Count; i++)
        {
            XuiAnimTrack track = _tracks[i];
            if (!string.Equals(track.Trigger, trigger, StringComparison.Ordinal)) continue;

            RemovePlaying(track);
            _playing.Add(new Playing(track));
            Apply(track, 0f);
            started = true;
        }

        if (started)
        {
            ApplyOffset();
            MarkDirty(UIDirtyFlags.Layout);
        }
        return started;
    }

    /// <summary>Cancels everything in flight and returns the element to its untransformed state.</summary>
    public void ResetToRest()
    {
        _playing.Clear();
        Offset = Float2.Zero;
        ApplyScale(new Float2(1f, 1f));
        ApplyAlpha(1f);
        ApplyOffset();
    }

    /// <summary>
    /// Advances every running transition. Called by <see cref="XuiAnimationTicker"/>, and directly by
    /// tests and tools that need to step time deterministically.
    /// </summary>
    public void Tick(float deltaSeconds)
    {
        if (_playing.Count == 0 || deltaSeconds <= 0f) return;

        for (int i = _playing.Count - 1; i >= 0; i--)
        {
            Playing state = _playing[i];
            state.Elapsed += deltaSeconds;

            float active = state.Elapsed - state.Track.Delay;
            if (active < 0f) continue;

            float duration = Maths.Max(0.0001f, state.Track.Duration);
            float t = Maths.Clamp(active / duration, 0f, 1f);
            Apply(state.Track, XuiEasing.Evaluate(state.Track.Ease, t));

            if (t >= 1f) _playing.RemoveAt(i);
        }

        ApplyOffset();
        MarkDirty(UIDirtyFlags.Layout);
    }

    public override void GenerateMesh(UIMeshBuilder builder, in UIContext context) { }

    /// <summary>
    /// Adds the current offset on top of whatever layout decided.
    /// </summary>
    /// <remarks>
    /// Which field to write depends on who owns the rect. A container writes its children's
    /// <c>ComputedRect</c> directly and ignores <c>AnchoredPosition</c>, so inside one the offset has to
    /// go on the computed rect. A free element's rect is computed from its anchors <em>after</em> this
    /// runs, so there the offset goes on <c>AnchoredPosition</c> instead. Picking one and hoping is how
    /// an animation ends up working everywhere except inside a stack.
    /// </remarks>
    public override void ApplyLayout(in Rect parentRect)
    {
        if (GameObject.RectTransform is not { IsDisposed: false } rect) return;

        if (ParentArrangesChildren())
        {
            Rect computed = rect.ComputedRect;
            rect.ComputedRect = new Rect(
                computed.Min.X + Offset.X,
                computed.Min.Y + Offset.Y,
                computed.Max.X + Offset.X,
                computed.Max.Y + Offset.Y);
            return;
        }

        if (!_capturedRest)
        {
            _restAnchored = rect.AnchoredPosition;
            _capturedRest = true;
        }
        rect.AnchoredPosition = new Float2(_restAnchored.X + Offset.X, _restAnchored.Y + Offset.Y);
    }

    /// <summary>Writes one track's state at an eased position between its endpoints.</summary>
    private void Apply(XuiAnimTrack track, float eased)
    {
        if (track.AnimatesMove) Offset = Lerp(track.MoveFrom, track.MoveTo, eased);
        if (track.AnimatesScale) ApplyScale(Lerp(track.ScaleFrom, track.ScaleTo, eased));
        if (track.AnimatesFade)
            ApplyAlpha(track.FadeFrom + (track.FadeTo - track.FadeFrom) * eased);
    }

    private void ApplyOffset()
    {
        if (GameObject.RectTransform is { IsDisposed: false } rect && !ParentArrangesChildren())
        {
            if (!_capturedRest)
            {
                _restAnchored = rect.AnchoredPosition;
                _capturedRest = true;
            }
            rect.AnchoredPosition = new Float2(_restAnchored.X + Offset.X, _restAnchored.Y + Offset.Y);
        }
    }

    private bool ParentArrangesChildren()
    {
        if (GameObject.Parent is not { IsDisposed: false } parent) return false;
        foreach (MonoBehaviour component in parent.GetComponents<MonoBehaviour>())
        {
            if (component is ILayoutGroupHost && component.EnabledInHierarchy) return true;
        }
        return false;
    }

    private void ApplyScale(Float2 scale)
    {
        if (GameObject.RectTransform is { IsDisposed: false } rect)
            rect.LocalScale = new Float3(scale.X, scale.Y, 1f);
    }

    private void ApplyAlpha(float alpha)
    {
        // Alpha goes on a CanvasGroup rather than each Graphic's colour: a transition fades the whole
        // subtree, and writing colours would clobber whatever the document set them to.
        CanvasGroup group = GameObject.GetComponent<CanvasGroup>()
            ?? GameObject.AddComponent<CanvasGroup>();
        group.Alpha = Maths.Clamp(alpha, 0f, 1f);
    }

    private void RemovePlaying(XuiAnimTrack track)
    {
        for (int i = _playing.Count - 1; i >= 0; i--)
        {
            if (ReferenceEquals(_playing[i].Track, track)) _playing.RemoveAt(i);
        }
    }

    private static Float2 Lerp(Float2 from, Float2 to, float t)
        => new(from.X + (to.X - from.X) * t, from.Y + (to.Y - from.Y) * t);

    private sealed class Playing
    {
        public Playing(XuiAnimTrack track) => Track = track;

        public XuiAnimTrack Track { get; }
        public float Elapsed { get; set; }
    }
}

/// <summary>Drives every animator on a screen. Attached to the screen root by the assembler.</summary>
public sealed class XuiAnimationTicker : MonoBehaviour
{
    private readonly List<XuiAnimator> _scratch = new();

    /// <summary>
    /// Set while a screen is hiding: the root switches off once its exit has finished playing.
    /// </summary>
    /// <remarks>
    /// A screen that disabled itself the moment <c>hide</c> was played would never render a single
    /// frame of its own exit — the transition would run correctly and be entirely invisible. The wait
    /// lives here rather than in <c>Xui</c> because this is the only thing that knows when the last
    /// track has finished.
    /// </remarks>
    public bool DisableWhenIdle { get; set; }

    public override void Update() => Tick(Time.DeltaTime);

    /// <summary>Advances every animator under this root. Exposed so a test can step time by hand.</summary>
    public void Tick(float deltaSeconds)
    {
        if (deltaSeconds <= 0f) return;
        _scratch.Clear();
        Collect(GameObject, _scratch);

        bool playing = false;
        for (int i = 0; i < _scratch.Count; i++)
        {
            XuiAnimator animator = _scratch[i];
            animator.Tick(deltaSeconds);
            playing |= animator.IsPlaying;
        }

        if (DisableWhenIdle && !playing)
        {
            DisableWhenIdle = false;
            GameObject.Enabled = false;
        }
    }

    /// <summary>True while any animator under this root still has a transition in flight.</summary>
    public bool IsPlaying
    {
        get
        {
            _scratch.Clear();
            Collect(GameObject, _scratch);
            for (int i = 0; i < _scratch.Count; i++)
            {
                if (_scratch[i].IsPlaying) return true;
            }
            return false;
        }
    }

    private static void Collect(GameObject node, List<XuiAnimator> into)
    {
        if (node.GetComponent<XuiAnimator>() is { IsDisposed: false } animator) into.Add(animator);
        IReadOnlyList<GameObject> children = node.Children;
        for (int i = 0; i < children.Count; i++) Collect(children[i], into);
    }
}
