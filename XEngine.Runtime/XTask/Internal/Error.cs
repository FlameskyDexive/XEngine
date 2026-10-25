// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

namespace XEngine.Async.Internal;

/// <summary>Centralized exception messages and throw helpers for XTask.</summary>
internal static class Error
{
    public const string NotYetCompleted = "Not yet completed, XTask only allows use via await.";
    public const string AlreadyContinuationRegistered = "Already continuation registered; an XTask source accepts exactly one continuation and cannot be awaited twice.";
    public const string VersionNotMatched = "Token version is not matched; cannot await twice or get Status after await.";
    public const string InvokeSentinel = "The continuation sentinel should never be invoked.";
    public const string Argument_Null = "Value cannot be null.";

    public static ArgumentNullException ArgumentNull(string paramName) => new(paramName);

    public static ArgumentOutOfRangeException ArgumentOutOfRange(string paramName, string message) => new(paramName, message);

    public static ArgumentException Argument(string message) => new(message);

    public static InvalidOperationException InvalidOperation(string message) => new(message);
}
