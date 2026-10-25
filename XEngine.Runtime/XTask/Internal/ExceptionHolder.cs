// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Runtime.ExceptionServices;

namespace XEngine.Async.Internal;

/// <summary>
/// Holds a captured exception with its original stack via <see cref="ExceptionDispatchInfo"/>.
/// Has a finalizer: if the source is recycled or dropped without ever fetching the exception, the
/// finalizer publishes it to <see cref="XTaskScheduler.UnobservedTaskException"/> so it is not
/// silently swallowed.
/// </summary>
internal sealed class ExceptionHolder
{
    private ExceptionDispatchInfo _info;
    private bool _isObserved;

    internal ExceptionHolder(ExceptionDispatchInfo info)
    {
        _info = info;
    }

    /// <summary>The captured source exception (does not mark observed).</summary>
    public Exception SourceException => _info.SourceException;

    /// <summary>Re-throw the captured exception preserving the original stack and mark observed.</summary>
    public void Throw()
    {
        _isObserved = true;
        _info.Throw();
    }

    /// <summary>Return the captured exception and mark observed (no re-throw).</summary>
    public Exception GetException()
    {
        _isObserved = true;
        return _info.SourceException;
    }

    ~ExceptionHolder()
    {
        if (!_isObserved)
        {
            XTaskScheduler.PublishUnobservedTaskException(_info.SourceException);
        }
    }
}
