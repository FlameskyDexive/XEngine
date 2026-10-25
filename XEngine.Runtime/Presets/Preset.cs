// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

namespace XEngine.Runtime;

/// <summary>
/// A preset asset: a snapshot of a component's or asset's serialized field values that can be
/// re-applied to other instances of the same type (the XEngine counterpart of Unity's Preset).
/// Stored as a <c>.preset</c> Echo-text asset. The captured values live in <see cref="Data"/>
/// as serialized Echo text; <see cref="Excluded"/> lists field names that are skipped on apply.
/// </summary>
public class Preset : ScriptableObject
{
    /// <summary>Assembly-qualified name of the component / asset type this preset captures.</summary>
    public string TargetType = "";

    /// <summary>Serialized Echo text of the target's fields at capture time.</summary>
    public string Data = "";

    /// <summary>Field names excluded when applying this preset (right-click → Exclude in the preset inspector).</summary>
    public List<string> Excluded = new();

    /// <summary>Resolve the captured target type, or null when it no longer loads.</summary>
    /// <remarks>
    /// Script component types live in the collectible script AssemblyLoadContext; plain
    /// <see cref="Type.GetType"/> throws NotSupportedException from a non-collectible assembly for
    /// those. <see cref="RuntimeUtils.ResolveType"/> binds against every loaded assembly
    /// (collectible contexts included) and never throws for unloadable names.
    /// </remarks>
    public Type? ResolveTargetType()
        => string.IsNullOrEmpty(TargetType) ? null : RuntimeUtils.ResolveType(TargetType);
}
