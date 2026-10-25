// This file is part of the XEngine Game Engine
// Licensed under the MIT License. See the LICENSE file in the project root for details.

using XEngine.Runtime.Backends.WebGL2;

using Xunit;

namespace XEngine.Runtime.Test.Rhi;

public class WebGL2GlslTests
{
    private const string SampleEs = """
#version 300 es
precision highp float;
layout(set = 0, binding = 1) uniform sampler2D uTex;
layout(binding = 2, std140) uniform Block { vec4 color; };
layout(location = 0) out vec4 fragColor;
void main() { highp vec4 c = texture(uTex, vec2(0.0)); fragColor = c; }
""";

    [Fact]
    public void Prepare_Desktop_RewritesVersionStripsPrecisionAndBindings()
    {
        string prepared = WebGL2Glsl.Prepare(SampleEs, WebGL2GlslStage.Fragment, WebGL2GlslHost.DesktopCore);

        Assert.Contains("#version 330", prepared, StringComparison.Ordinal);
        Assert.DoesNotContain("#version 300 es", prepared, StringComparison.Ordinal);
        Assert.DoesNotContain("precision", prepared, StringComparison.Ordinal);
        Assert.DoesNotContain("highp", prepared, StringComparison.Ordinal);
        Assert.DoesNotContain("binding", prepared, StringComparison.Ordinal);
        Assert.DoesNotContain("layout(set", prepared, StringComparison.Ordinal);
        Assert.Contains("layout(std140)", prepared, StringComparison.Ordinal);
        Assert.Contains("layout(location = 0)", prepared, StringComparison.Ordinal);
        Assert.Contains("uniform sampler2D uTex", prepared, StringComparison.Ordinal);
    }

    [Fact]
    public void Prepare_Browser_KeepsEsVersionAndHighpPrecision()
    {
        const string withoutPrecision = """
#version 450
layout(binding = 0) uniform sampler2D uTex;
layout(location = 0) out vec4 fragColor;
void main() { fragColor = vec4(1.0); }
""";
        string prepared = WebGL2Glsl.Prepare(withoutPrecision, WebGL2GlslStage.Fragment, WebGL2GlslHost.Browser);

        Assert.Contains("#version 300 es", prepared, StringComparison.Ordinal);
        Assert.DoesNotContain("#version 450", prepared, StringComparison.Ordinal);
        Assert.Contains("precision highp float", prepared, StringComparison.Ordinal);
        Assert.DoesNotContain("binding", prepared, StringComparison.Ordinal);
        Assert.Contains("layout(location = 0)", prepared, StringComparison.Ordinal);
    }

    [Fact]
    public void Prepare_BrowserVertex_DoesNotInjectFragmentPrecision()
    {
        const string vertex = """
#version 300 es
layout(location = 0) in vec3 p;
void main() { gl_Position = vec4(p, 1.0); }
""";
        string prepared = WebGL2Glsl.Prepare(vertex, WebGL2GlslStage.Vertex, WebGL2GlslHost.Browser);

        Assert.Contains("#version 300 es", prepared, StringComparison.Ordinal);
        Assert.DoesNotContain("precision highp float", prepared, StringComparison.Ordinal);
    }

    [Fact]
    public void StripBindingLayouts_RemovesEmptyLayoutAfterBindingOnly()
    {
        const string source = "layout(binding = 3) uniform sampler2D uTex;";
        string stripped = WebGL2Glsl.StripBindingLayouts(source);
        Assert.Equal(" uniform sampler2D uTex;", stripped);
    }
}
