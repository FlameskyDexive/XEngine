// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

namespace XEngine.Runtime;

/// <summary>
/// Marks a type or member as a dynamic runtime root. Android player builds add the declaring type
/// to the generated linker and runtime type registries.
/// </summary>
[AttributeUsage(AttributeTargets.All, AllowMultiple = false, Inherited = false)]
public sealed class PreserveAttribute : Attribute
{
}
