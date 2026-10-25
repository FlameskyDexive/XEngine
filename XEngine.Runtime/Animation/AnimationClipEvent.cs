// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using XEngine.Runtime;

namespace XEngine.Animation;

/// <summary>
/// A clip event: either a runtime callback on an <see cref="AnimationClipPlayable"/>
/// (<see cref="NormalizedTime"/> + <see cref="Callback"/>), or an authored disk event
/// (<see cref="Time"/> + <see cref="FunctionName"/> + params) stored on <see cref="AnimationClip.Events"/>.
/// </summary>
public struct AnimationClipEvent
{
    /// <summary>Normalized time in [0,1] at which a playable-registered callback fires.</summary>
    public float NormalizedTime;

    /// <summary>The callback. May be <c>null</c> (the entry is then inert). Not serialized.</summary>
    public Action? Callback;

    /// <summary>★ M40 — Authored event time in seconds (clip local). Ignored for playable-only callbacks.</summary>
    public float Time;

    public string FunctionName;
    public float FloatParam;
    public int IntParam;
    public string StringParam;
    public AssetRef<EngineObject> ObjectParam;
}
