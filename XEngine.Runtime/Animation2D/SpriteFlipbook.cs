// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

using XEngine;
using XEngine.Runtime.Resources;

namespace XEngine.Runtime;

/// <summary>
/// ★ Gen-P2.3 — Plays a list of <see cref="Sprite"/> frames on a sibling <see cref="SpriteRenderer"/>.
/// Steady-state <see cref="Update"/> is allocation-free: indexed span access, no strings, no new.
/// </summary>
[AddComponentMenu("2D/Sprite Flipbook")]
[ComponentIcon("\uf008")] // Film
public sealed class SpriteFlipbook : MonoBehaviour
{
    /// <summary>Ordered frames. Empty list is a no-op.</summary>
    public List<AssetRef<Sprite>> Frames = new();

    /// <summary>Playback rate. Values ≤ 0 freeze the current frame.</summary>
    public float FramesPerSecond = 12f;

    /// <summary>When true, wrap to frame 0 after the last frame; otherwise stop on the last frame.</summary>
    public bool Loop = true;

    /// <summary>Start playing when the component enables.</summary>
    public bool PlayOnEnable = true;

    /// <summary>Frame index applied on enable / <see cref="Stop"/>.</summary>
    public int StartFrame;

    [NonSerialized] public int CurrentFrame;
    [NonSerialized] public bool IsPlaying;

    [NonSerialized] float _accum;
    [NonSerialized] SpriteRenderer? _renderer;
    [NonSerialized] int _appliedFrame = -1;

    public override void OnEnable()
    {
        _renderer = GetComponent<SpriteRenderer>();
        _appliedFrame = -1;
        _accum = 0f;
        CurrentFrame = StartFrame;
        if (PlayOnEnable)
            IsPlaying = true;
        ApplyFrame();
    }

    [HotPath]
    public override void Update()
    {
        if (!IsPlaying)
            return;

        int count = Frames.Count;
        if (count <= 0)
            return;

        float fps = FramesPerSecond;
        if (fps <= 0f)
            return;

        _accum += Time.DeltaTime * fps;
        int advance = (int)_accum;
        if (advance <= 0)
            return;
        _accum -= advance;

        int next = CurrentFrame + advance;
        if (Loop)
        {
            next %= count;
            if (next < 0)
                next += count;
        }
        else if (next >= count)
        {
            next = count - 1;
            IsPlaying = false;
        }
        else if (next < 0)
        {
            next = 0;
            IsPlaying = false;
        }

        if (next == CurrentFrame)
            return;

        CurrentFrame = next;
        ApplyFrame();
    }

    public void Play() => IsPlaying = true;

    public void Pause() => IsPlaying = false;

    public void Stop()
    {
        IsPlaying = false;
        CurrentFrame = StartFrame;
        _accum = 0f;
        _appliedFrame = -1;
        ApplyFrame();
    }

    public void SetFrame(int index)
    {
        int count = Frames.Count;
        if (count <= 0)
            return;
        if (index < 0)
            index = 0;
        else if (index >= count)
            index = count - 1;
        CurrentFrame = index;
        _accum = 0f;
        ApplyFrame();
    }

    [HotPath]
    void ApplyFrame()
    {
        if (_appliedFrame == CurrentFrame)
            return;

        int count = Frames.Count;
        if ((uint)CurrentFrame >= (uint)count)
            return;

        SpriteRenderer? renderer = _renderer;
        if (renderer == null)
            return;

        renderer.Sprite = CollectionsMarshal.AsSpan(Frames)[CurrentFrame];
        _appliedFrame = CurrentFrame;
    }
}
