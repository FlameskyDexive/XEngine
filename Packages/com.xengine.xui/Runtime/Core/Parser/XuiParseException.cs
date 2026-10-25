// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Globalization;

namespace XEngine.XUI.Core;

/// <summary>
/// A structural or semantic error that stops a <c>.xui</c> document from producing an IR. Always
/// carries a source span: an agent that gets "line 14, column 7" can go straight to the mistake,
/// which is the difference between a one-shot fix and a guessing loop.
/// </summary>
public sealed class XuiParseException : Exception
{
    public XuiParseException(string message, XuiSourceSpan span, string? sourcePath = null)
        : base(Format(message, span, sourcePath))
    {
        Span = span;
        SourcePath = sourcePath;
        RawMessage = message;
    }

    public XuiSourceSpan Span { get; }
    public string? SourcePath { get; }

    /// <summary>The message without the location prefix, for structured tool output.</summary>
    public string RawMessage { get; }

    private static string Format(string message, XuiSourceSpan span, string? sourcePath)
    {
        string where = sourcePath ?? "<xui>";
        return span.HasValue
            ? string.Create(CultureInfo.InvariantCulture, $"{where}({span.Line},{span.Column}): {message}")
            : where + ": " + message;
    }
}
