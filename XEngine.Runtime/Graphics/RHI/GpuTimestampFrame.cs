// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

namespace XEngine.Runtime.RHI;

/// <summary>
/// A completed GPU timestamp interval. Values are published only after the originating GPU
/// submission fence has retired; they never represent CPU command encoding or present waits.
/// </summary>
public readonly record struct GpuTimestampFrame(ulong FrameId, float DurationMs);
