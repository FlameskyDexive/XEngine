// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Vector;

namespace XEngine.Runtime.Rendering;

/// <summary>
/// One interpolatable settings block inside a <see cref="VolumeProfile"/>.
/// </summary>
public abstract class VolumeComponent
{
    /// <summary>When false, this component is skipped during stack blending.</summary>
    public bool Active = true;

    /// <summary>Lerp <c>this</c> toward <paramref name="state"/> by <paramref name="interpFactor"/> (0–1).</summary>
    public abstract void Interp(VolumeComponent state, float interpFactor);

    /// <summary>Restore identity / default values so the stack can be reused without reallocating.</summary>
    public abstract void ResetToDefault();

    protected static void InterpFloat(ref float dest, ref bool destOverride, float src, bool srcOverride, float t)
    {
        if (!srcOverride)
            return;
        dest = destOverride ? Maths.Lerp(dest, src, t) : src;
        destOverride = true;
    }

    protected static void InterpInt(ref int dest, ref bool destOverride, int src, bool srcOverride, float t)
    {
        if (!srcOverride)
            return;
        dest = destOverride && t < 0.5f ? dest : src;
        destOverride = true;
    }

    protected static void InterpBool(ref bool dest, ref bool destOverride, bool src, bool srcOverride, float t)
    {
        if (!srcOverride)
            return;
        dest = destOverride && t < 0.5f ? dest : src;
        destOverride = true;
    }

    protected static void InterpColor(ref Color dest, ref bool destOverride, Color src, bool srcOverride, float t)
    {
        if (!srcOverride)
            return;
        dest = destOverride ? Color.Lerp(dest, src, t) : src;
        destOverride = true;
    }
}
