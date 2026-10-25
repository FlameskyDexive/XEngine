// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

namespace XEngine;

/// <summary>
/// Marks a class as object-pooled. Exempts the type from PR0007 (hot-path ban on
/// <c>new</c> of reference types). Hot paths should still Rent from a pool rather than rely on <c>new</c>.
/// </summary>
[System.AttributeUsage(System.AttributeTargets.Class, Inherited = false)]
public sealed class PoolAttribute : System.Attribute
{
    public int InitialCapacity { get; set; } = 16;
}
