// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

using XEngine.Echo;

namespace XEngine.Runtime;

/// <summary>
/// Stores a single per-instance property override for a prefab instance.
/// </summary>
[Serializable]
public class PropertyOverride
{
    /// <summary>
    /// Path to the overridden field.
    /// Format: "{componentIdentifier}.{fieldPath}" for component fields,
    /// or "$.{fieldName}" for GameObject-level fields.
    /// </summary>
    [SerializeField]
    public string Path = "";

    /// <summary>
    /// The overridden value, serialized as an EchoObject.
    /// </summary>
    [SerializeField]
    public EchoObject Value = EchoObject.NewCompound();

    /// <summary>
    /// ★ Layer 0 (A1) — Optional human/AI note explaining WHY this property was overridden
    /// (e.g. "raised above ground to avoid clipping"). Provenance: previously a prefab override
    /// stored only the value, never the reason — so an AI (or human) reading the prefab later could
    /// never tell whether an override was an art decision, a gameplay tweak, or a fix. Empty by
    /// default for backward compatibility; round-trips with no format change because Echo has no
    /// schema and absent fields simply deserialize to default.
    /// </summary>
    [SerializeField]
    public string Reason = "";

    /// <summary>UTC ticks recording when this override was set, or 0 if unknown (pre-provenance).</summary>
    [SerializeField]
    public long ModifiedAtUtcTicks = 0L;
}
