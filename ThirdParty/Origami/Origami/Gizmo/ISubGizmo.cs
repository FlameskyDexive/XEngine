// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Vector;

namespace XEngine.OrigamiUI.Gizmo;

public interface ISubGizmo
{
    bool Pick(Ray ray, Float2 screenPos, out float t);
    GizmoResult? Update(Ray ray, Float2 screenPos);
    void Draw(XEngine.Quill.Canvas canvas);
    void SetFocused(bool focused);
}
