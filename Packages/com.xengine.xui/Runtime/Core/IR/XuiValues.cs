// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Globalization;

namespace XEngine.XUI.Core;

/// <summary>
/// A 2D vector in the IR. Deliberately not <c>XEngine.Vector.Float2</c>: <c>Runtime/Core</c> is pure
/// BCL so the same sources compile inside the offline AI Host, which never loads the engine.
/// </summary>
public readonly struct XuiFloat2
{
    public XuiFloat2(float x, float y)
    {
        X = x;
        Y = y;
    }

    public float X { get; }
    public float Y { get; }

    public override string ToString()
        => string.Create(CultureInfo.InvariantCulture, $"{X},{Y}");
}

/// <summary>
/// A size where either axis may be left to the layout system. <c>size="400x_"</c> pins width and
/// leaves height elastic; <c>_x44</c> does the reverse.
/// </summary>
public readonly struct XuiSize
{
    public XuiSize(float? width, float? height)
    {
        Width = width;
        Height = height;
    }

    public float? Width { get; }
    public float? Height { get; }

    public bool IsEmpty => Width == null && Height == null;

    public override string ToString()
    {
        string w = Width == null ? "_" : Width.Value.ToString(CultureInfo.InvariantCulture);
        string h = Height == null ? "_" : Height.Value.ToString(CultureInfo.InvariantCulture);
        return w + "x" + h;
    }
}

/// <summary>
/// A resolved or deferred color. A <c>color=</c> attribute is either a literal or a theme token; the
/// parser keeps the distinction so the runtime theme store can win over a literal at apply time.
/// </summary>
public readonly struct XuiColor
{
    private XuiColor(float r, float g, float b, float a, string? token)
    {
        R = r;
        G = g;
        B = b;
        A = a;
        Token = token;
    }

    public static XuiColor Literal(float r, float g, float b, float a) => new(r, g, b, a, null);

    /// <summary>A theme token reference. RGBA stays zero until the theme store resolves it.</summary>
    public static XuiColor FromToken(string token) => new(0f, 0f, 0f, 1f, token);

    public float R { get; }
    public float G { get; }
    public float B { get; }
    public float A { get; }

    /// <summary>Non-null when the author wrote a theme token instead of a literal.</summary>
    public string? Token { get; }

    public bool IsToken => Token != null;

    public override string ToString()
    {
        if (Token != null) return Token;
        int r = (int)(R * 255f + 0.5f);
        int g = (int)(G * 255f + 0.5f);
        int b = (int)(B * 255f + 0.5f);
        int a = (int)(A * 255f + 0.5f);
        return a == 255
            ? string.Create(CultureInfo.InvariantCulture, $"#{r:X2}{g:X2}{b:X2}")
            : string.Create(CultureInfo.InvariantCulture, $"#{r:X2}{g:X2}{b:X2}{a:X2}");
    }
}
