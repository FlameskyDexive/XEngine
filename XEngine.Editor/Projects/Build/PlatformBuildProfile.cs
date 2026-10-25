// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using System;
using System.Collections.Generic;

using XEngine.Editor.Projects.Settings;
using XEngine.PaperUI;

namespace XEngine.Editor;

/// <summary>
/// Per-platform scripting define symbols and any future platform-specific
/// build knobs. Serialised as part of <see cref="BuildSettings"/>.
/// </summary>
public class PlatformBuildProfile
{
    /// <summary>
    /// Scripting define symbols passed to <c>dotnet publish</c> when building for this platform.
    /// </summary>
    /// <remarks>
    /// A field rather than an auto-property because Echo only serializes fields: as a property this
    /// never round-tripped, so anything typed here was silently dropped when the editor restarted.
    /// </remarks>
    public List<string> ScriptingDefineSymbols = [];

    public virtual Type GetPipelineType()
    {
        return null;
    }

    public virtual void ToDefault() { }

    public virtual void OnGUI(Paper paper) { }

    public virtual void ModifyDefines(List<string> defines) { }
}
