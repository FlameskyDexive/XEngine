// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

namespace XEngine.Async.Internal;

/// <summary>
/// A polling item registered with <see cref="FrameLoopRunner"/>. <see cref="MoveNext"/> is invoked
/// once per frame at the registered <see cref="FrameTiming"/>; returning false removes the item
/// (e.g. a <c>Delay</c> promise returns false once its timer elapses and it completes).
/// </summary>
internal interface IFrameLoopItem
{
    bool MoveNext();
}
