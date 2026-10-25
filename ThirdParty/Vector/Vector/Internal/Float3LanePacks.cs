// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

namespace XEngine.Vector;

// Internal AoSoA storage used by batch kernels. It is deliberately not a game-script vector type.
internal struct Float3Lane4
{
    public Float4 X;
    public Float4 Y;
    public Float4 Z;

    public static Float3Lane4 Load(ReadOnlySpan<Float3> values)
    {
        if (values.Length < 4)
            throw new ArgumentException("Four values are required.", nameof(values));
        return new Float3Lane4
        {
            X = new Float4(values[0].X, values[1].X, values[2].X, values[3].X),
            Y = new Float4(values[0].Y, values[1].Y, values[2].Y, values[3].Y),
            Z = new Float4(values[0].Z, values[1].Z, values[2].Z, values[3].Z),
        };
    }

    public readonly void Store(Span<Float3> values)
    {
        if (values.Length < 4)
            throw new ArgumentException("Four values are required.", nameof(values));
        values[0] = new Float3(X.X, Y.X, Z.X);
        values[1] = new Float3(X.Y, Y.Y, Z.Y);
        values[2] = new Float3(X.Z, Y.Z, Z.Z);
        values[3] = new Float3(X.W, Y.W, Z.W);
    }
}

internal struct Float3Lane8
{
    public Float3Lane4 Low;
    public Float3Lane4 High;

    public static Float3Lane8 Load(ReadOnlySpan<Float3> values)
    {
        if (values.Length < 8)
            throw new ArgumentException("Eight values are required.", nameof(values));
        return new Float3Lane8 { Low = Float3Lane4.Load(values), High = Float3Lane4.Load(values.Slice(4)) };
    }

    public readonly void Store(Span<Float3> values)
    {
        if (values.Length < 8)
            throw new ArgumentException("Eight values are required.", nameof(values));
        Low.Store(values);
        High.Store(values.Slice(4));
    }
}
