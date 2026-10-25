using System;
using System.Collections.Generic;

using XEngine.Editor.Build;
using XEngine.Editor.Build.Web;

namespace XEngine.Editor.Projects.Build;

public enum WebSubTarget
{
    Browser = 0,
    WeChat = 1,
    Douyin = 2,
}

/// <summary>Web / mini-game player settings. One pipeline; sub-targets pick the JS adapter.</summary>
public sealed class WebBuildProfile : PlatformBuildProfile
{
    public WebSubTarget SubTarget = WebSubTarget.Browser;

    /// <summary>
    /// Mono-WASM SIMD. Douyin publish always writes <c>false</c> regardless of this field
    /// (Douyin iOS containers reject SIMD).
    /// </summary>
    public bool WasmEnableSIMD = true;

    public string ApplicationId = "com.xengine.game";

    /// <summary>CSS selector or emscripten canvas target. Mini-game adapters assign Module.canvas.</summary>
    public string CanvasSelector = "#canvas";

    public override Type GetPipelineType() => typeof(WebBuildPipeline);

    public override void ToDefault()
    {
        SubTarget = WebSubTarget.Browser;
        WasmEnableSIMD = true;
        ApplicationId = "com.xengine.game";
        CanvasSelector = "#canvas";
        ScriptingDefineSymbols = [];
    }

    public override void ModifyDefines(List<string> defines)
    {
        defines.Add("XENGINE_WEB");
        defines.Add("XENGINE_MOBILE");
    }
}
