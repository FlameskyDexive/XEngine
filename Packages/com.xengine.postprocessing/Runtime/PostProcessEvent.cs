// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

namespace XEngine.PostProcessing;

/// <summary>Injection points for custom effects, matching Unity Post-processing Stack v2.</summary>
public enum PostProcessEvent
{
    BeforeTransparent,
    BeforeStack,
    AfterStack,
}
