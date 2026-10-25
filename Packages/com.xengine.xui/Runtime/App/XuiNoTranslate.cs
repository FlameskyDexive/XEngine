// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Runtime;

namespace XEngine.XUI;

/// <summary>
/// Marks a subtree whose text is not translated: <c>translate="false"</c> in the document.
/// </summary>
/// <remarks>
/// It exists as a component rather than a build-time flag so the decision is inspectable on the live
/// object and survives a hot reload — and so a container can opt out everything inside it without the
/// assembler threading the state through every element it builds.
/// </remarks>
[AddComponentMenu("UI/XUI/No Translate")]
public sealed class XuiNoTranslate : MonoBehaviour
{
}
