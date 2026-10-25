// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System.Globalization;

namespace XEngine.XUI.Core;

/// <summary>
/// How much a lint finding matters. <see cref="Error"/> means the screen will not do what the document
/// says; <see cref="Warning"/> means it will, but something is dead, redundant, or about to surprise
/// someone. The offline validator exits non-zero on errors only.
/// </summary>
public enum XuiLintSeverity
{
    Warning,
    Error,
}

/// <summary>
/// One lint finding, addressed to whoever wrote the line.
/// </summary>
/// <remarks>
/// Every issue carries a stable <see cref="Code"/>, a source span, and — where there is one — the
/// <see cref="ElementId"/> it concerns. Codes exist so a fix can be looked up and so a rule can be
/// referenced in a review without quoting its whole message.
/// </remarks>
public sealed class XuiLintIssue
{
    public XuiLintIssue(
        string code,
        XuiLintSeverity severity,
        string message,
        XuiSourceSpan span,
        string? elementId = null)
    {
        Code = code;
        Severity = severity;
        Message = message;
        Span = span;
        ElementId = elementId;
    }

    /// <summary>Stable rule id, e.g. <c>XUI-ID-PREFIX</c>.</summary>
    public string Code { get; }

    public XuiLintSeverity Severity { get; }

    public string Message { get; }

    public XuiSourceSpan Span { get; }

    /// <summary>The element's <c>id</c>, when it has one.</summary>
    public string? ElementId { get; }

    public override string ToString()
        => string.Create(
            CultureInfo.InvariantCulture,
            $"{Span}: {(Severity == XuiLintSeverity.Error ? "error" : "warning")} {Code}: {Message}");
}
