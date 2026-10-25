// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Vector;

namespace XEngine.Runtime.UI;

/// <summary>
/// Backend-neutral scale policy supplied by an optional UI package. The Canvas host asks the
/// component for a scale factor and keeps the policy implementation out of the Runtime assembly.
/// </summary>
public interface ICanvasScaler
{
    float ComputeScaleFactor(Float2 screenSize, float fallbackScale);
}
