// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using XEngine.Vector;

namespace XEngine.PostProcessing;

/// <summary>Untyped override flag + interpolate/copy for volume blending.</summary>
public abstract class ParameterOverride
{
    public bool overrideState;

    internal abstract void Interp(ParameterOverride from, ParameterOverride to, float t);
    internal abstract void SetValue(ParameterOverride parameter);
    public abstract int GetHash();

    public T GetValue<T>() => ((ParameterOverride<T>)this).value;

    public void OverrideFrom(ParameterOverride other, float t)
    {
        if (!other.overrideState)
            return;
        if (overrideState)
            Interp(this, other, t);
        else
            SetValue(other);
        overrideState = true;
    }
}

/// <summary>Typed parameter. Default <see cref="Interp(T,T,float)"/> snaps like Unity (bools / enums).</summary>
public class ParameterOverride<T> : ParameterOverride
{
    public T value = default!;

    public ParameterOverride() : this(default!, false) { }
    public ParameterOverride(T value) : this(value, false) { }

    public ParameterOverride(T value, bool overrideState)
    {
        this.value = value;
        this.overrideState = overrideState;
    }

    internal override void Interp(ParameterOverride from, ParameterOverride to, float t)
        => Interp(from.GetValue<T>(), to.GetValue<T>(), t);

    public virtual void Interp(T from, T to, float t) => value = t > 0f ? to : from;

    internal override void SetValue(ParameterOverride parameter) => value = parameter.GetValue<T>();

    public void Override(T x)
    {
        overrideState = true;
        value = x;
    }

    public override int GetHash()
    {
        unchecked
        {
            int hash = overrideState ? 1 : 0;
            if (value != null)
                hash = hash * 23 + value.GetHashCode();
            return hash;
        }
    }
}

public sealed class FloatParameter : ParameterOverride<float>
{
    public FloatParameter() { }
    public FloatParameter(float value, bool overrideState = false) : base(value, overrideState) { }

    public override void Interp(float from, float to, float t) => value = from + (to - from) * t;
}

public sealed class IntParameter : ParameterOverride<int>
{
    public IntParameter() { }
    public IntParameter(int value, bool overrideState = false) : base(value, overrideState) { }

    public override void Interp(int from, int to, float t) => value = t < 0.5f ? from : to;
}

public sealed class BoolParameter : ParameterOverride<bool>
{
    public BoolParameter() { }
    public BoolParameter(bool value, bool overrideState = false) : base(value, overrideState) { }
}

public sealed class ColorParameter : ParameterOverride<Color>
{
    public ColorParameter() { }
    public ColorParameter(Color value, bool overrideState = false) : base(value, overrideState) { }

    public override void Interp(Color from, Color to, float t) => value = Color.Lerp(from, to, t);
}

public sealed class Vector2Parameter : ParameterOverride<Float2>
{
    public Vector2Parameter() { }
    public Vector2Parameter(Float2 value, bool overrideState = false) : base(value, overrideState) { }

    public override void Interp(Float2 from, Float2 to, float t)
        => value = new Float2(from.X + (to.X - from.X) * t, from.Y + (to.Y - from.Y) * t);
}

public sealed class Vector4Parameter : ParameterOverride<Float4>
{
    public Vector4Parameter() { }
    public Vector4Parameter(Float4 value, bool overrideState = false) : base(value, overrideState) { }

    public override void Interp(Float4 from, Float4 to, float t)
        => value = new Float4(
            from.X + (to.X - from.X) * t,
            from.Y + (to.Y - from.Y) * t,
            from.Z + (to.Z - from.Z) * t,
            from.W + (to.W - from.W) * t);
}
