// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

namespace XEngine.Async;

/// <summary>
/// The phase of <c>Game.SimulationStep</c> at which a frame-driven XTask continuation or polling
/// item runs. Values are sequential indices into <see cref="XTaskLoopHelper"/>'s per-phase queues.
/// </summary>
public enum FrameTiming : byte
{
    /// <summary>Inside the fixed-update while-loop, once per accumulated fixed step.</summary>
    FixedUpdate = 0,

    /// <summary>After <c>Scene.Update</c> (Start/Update/LateUpdate) within the simulation step.</summary>
    Update = 1,

    /// <summary>Immediately after the <see cref="Update"/> phase.</summary>
    LateUpdate = 2,

    /// <summary>At the end of the simulation step, before presentation.</summary>
    EndOfFrame = 3,
}
