// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

namespace XEngine.Runtime;

/// <summary>
/// Shows a field or property in the inspector even when it would not normally be serialized.
/// Apply to non-public fields (to surface them in the inspector) or to properties with a getter
/// (to display computed/derived values). This is the inverse of <see cref="HideInInspectorAttribute"/>
/// and the XEngine analog of Odin's <c>[ShowInInspector]</c>.
/// </summary>
/// <remarks>
/// <para>
/// On a <b>field</b>: the field is drawn in the inspector. It is still serialized only if it is
/// public or also marked <c>[SerializeField]</c>; <c>[ShowInInspector]</c> alone makes a private
/// field <i>visible</i> but not <i>persisted</i> (use both attributes to get both).
/// </para>
/// <para>
/// On a <b>property</b>: the property's current value is drawn. Properties are never serialized;
/// this attribute is display-only. Set <see cref="ReadOnly"/> to draw the value as read-only
/// (recommended for computed properties that have no setter or whose setter has side effects).
/// </para>
/// </remarks>
[AttributeUsage(AttributeTargets.Field | AttributeTargets.Property)]
public sealed class ShowInInspectorAttribute : Attribute
{
    /// <summary>
    /// When true, the value is drawn read-only (no editing). Defaults to true for properties
    /// without a public setter, false otherwise.
    /// </summary>
    public bool ReadOnly { get; set; }
}
