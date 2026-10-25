// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

namespace XEngine.Runtime.Video;

/// <summary>
/// Exception carrying a <see cref="VideoErrorCode"/> so callers can branch on the failure
/// category without parsing message text.
/// </summary>
public sealed class VideoException : Exception
{
    public VideoErrorCode ErrorCode { get; }

    public VideoException(VideoErrorCode errorCode, string message, Exception? innerException = null)
        : base(message, innerException)
    {
        ErrorCode = errorCode;
    }
}
