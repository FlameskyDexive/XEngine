// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

namespace XEngine.Async;

// NOTE: The ValueTask bridge (IXTaskSource -> IValueTaskSource mapping + implicit XTask -> ValueTask
// conversion) is intentionally deferred to the Bridge phase (P6) where it is added as a non-breaking
// adapter layer. Keeping IXTaskSource a pure contract here avoids multi-interface dispatch and the
// nullable-signature friction between Action<object> / Action<object?> in the core types.

/// <summary>
/// Status of an <see cref="IXTaskSource"/>. <see cref="Pending"/> is 0 so that <c>default</c> reads
/// as pending.
/// </summary>
public enum XTaskStatus : byte
{
    Pending = 0,
    Succeeded = 1,
    Faulted = 2,
    Canceled = 3,
}

/// <summary>
/// The manual completion-source contract that backs every <see cref="XTask"/>. Mirrors
/// <see cref="System.Threading.Tasks.Sources.IValueTaskSource"/> in shape but uses
/// <see cref="XTaskStatus"/> and a single-continuation slot. Implementors register exactly one
/// continuation via <see cref="OnCompleted"/>; the version token handed to the awaiter is validated
/// on every call so that pooled instances can be safely recycled and re-handed-out.
/// </summary>
public interface IXTaskSource
{
    /// <summary>Polled by the awaiter's IsCompleted; validates the version token.</summary>
    XTaskStatus GetStatus(short token);

    /// <summary>Register exactly one continuation (single-shot contract). The continuation is invoked
    /// with <paramref name="state"/> when the source completes.</summary>
    void OnCompleted(Action<object> continuation, object state, short token);

    /// <summary>Fetch the result; throws on fault/cancel. Recycles the source if pooled.</summary>
    void GetResult(short token);

    /// <summary>Status without token validation; diagnostic use only.</summary>
    XTaskStatus UnsafeGetStatus();
}

/// <summary>Generic variant of <see cref="IXTaskSource"/> that produces a typed result.</summary>
public interface IXTaskSource<out T> : IXTaskSource
{
    /// <summary>Fetch the typed result; throws on fault/cancel.</summary>
    new T GetResult(short token);

    // Map the void-returning IXTaskSource.GetResult onto the typed one, so implementors of
    // IXTaskSource<T> only need to provide `T GetResult(short)`.
    void IXTaskSource.GetResult(short token) => GetResult(token);
}
