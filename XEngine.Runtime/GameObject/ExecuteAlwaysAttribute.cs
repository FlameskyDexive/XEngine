// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;

namespace XEngine.Runtime;

/// <summary>
/// When applied to a MonoBehaviour, its gameplay lifecycle methods (Start, Update, LateUpdate, FixedUpdate)
/// are called even when the application is not in play mode (e.g., in the editor).
/// Structural lifecycle methods (OnEnable, OnDisable, OnRenderCollect, etc.) always run regardless.
/// </summary>
[AttributeUsage(AttributeTargets.Class, Inherited = true)]
public class ExecuteAlwaysAttribute : Attribute { }
