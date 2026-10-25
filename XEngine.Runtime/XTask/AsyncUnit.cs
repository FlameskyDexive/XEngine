// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Runtime.CompilerServices;

namespace XEngine.Async;

/// <summary>
/// A zero-size unit value used as the result type for void-producing XTasks (the type-position
/// analogue of <c>void</c>). Singleton: always use <see cref="Default"/>.
/// </summary>
public readonly struct AsyncUnit : IEquatable<AsyncUnit>
{
    public static readonly AsyncUnit Default = default;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public bool Equals(AsyncUnit other) => true;

    public override bool Equals(object? obj) => obj is AsyncUnit;

    public override int GetHashCode() => 0;

    public override string ToString() => "()";

    public static bool operator ==(AsyncUnit _, AsyncUnit __) => true;

    public static bool operator !=(AsyncUnit _, AsyncUnit __) => false;
}
