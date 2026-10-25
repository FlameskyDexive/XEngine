// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

namespace XEngine.Runtime;

/// <summary>
/// Indicates the order in which the marked component should execute its update methods in the XEngine Game Engine.
/// </summary>
/// <remarks>
/// This attribute can only be applied to classes and cannot be used multiple times on the same class.
/// </remarks>
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public class ExecutionOrderAttribute : Attribute
{
    public int Order { get; }
    public ExecutionOrderAttribute(int order)
    {
        Order = order;
    }
}
