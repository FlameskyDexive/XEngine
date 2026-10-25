// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Globalization;

namespace XEngine.XUI.Core;

/// <summary>
/// A 1-based line/column pointer back into the source document. Every IR node carries one so lint
/// issues, parse errors, and patch results can name an exact spot — the agent reads the file and
/// sees the same coordinates.
/// </summary>
public readonly struct XuiSourceSpan
{
    public static readonly XuiSourceSpan None = new(0, 0);

    public XuiSourceSpan(int line, int column)
    {
        Line = line;
        Column = column;
    }

    public int Line { get; }
    public int Column { get; }

    public bool HasValue => Line > 0;

    public override string ToString()
        => HasValue
            ? string.Create(CultureInfo.InvariantCulture, $"{Line}:{Column}")
            : "?";
}
