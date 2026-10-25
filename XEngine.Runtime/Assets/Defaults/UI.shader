Shader "Paper/UI"

Variants
{
}

Properties
{
}

Pass "UI"
{
    Tags { "RenderOrder" = "Opaque" }

    Blend {
        Src One
        Dst OneMinusSrcAlpha
        Mode Add
    }
    ZTest Off
    ZWrite Off
    Cull Off

    GLSLPROGRAM

    Vertex
    {
        // Attribute locations follow VertexFormat.VertexSemantic values (Position=0,
        // TexCoord0=1, TexCoord1=2, Color=4): the GL/WebGL2 backends bind each attribute
        // at an index equal to its semantic enum value.
        layout (location = 0) in vec2 aPosition;
        layout (location = 1) in vec2 aTexCoord;
        layout (location = 2) in float aBrushIndex;
        layout (location = 4) in vec4 aColor;

        uniform mat4 projection;

        out vec2 fragTexCoord;
        out vec4 fragColor;
        out vec2 fragPos;
        flat out float fragBrushIndex;

        void main()
        {
            fragTexCoord = aTexCoord;
            fragColor = aColor;
            fragPos = aPosition;
            fragBrushIndex = aBrushIndex;
            gl_Position = projection * vec4(aPosition, 0.0, 1.0);
        }
    }

    Fragment
    {
        in vec2 fragTexCoord;
        in vec4 fragColor;
        in vec2 fragPos;
        in float fragBrushIndex;

        out vec4 finalColor;

        uniform sampler2D texture0;
        uniform sampler2D fontTexture;     // dedicated font-atlas sampler, so text batches with shapes
        uniform float dpiScale;
        uniform float texture0SrgbDecode;

        // Per-draw-state records (scissor + brush), addressed by the vertex brushIndex so
        // many states share one draw call. 96 records * 9 float4 each = 864 slots; must
        // match Canvas.BrushRecordsPerPage / UiBrushRecord in Quill. Slot layout per record:
        //   [0] scissor inverse affine (A,B,C,D)   [1] (E,F, extentX, extentY)
        //   [2] brush inverse affine (A,B,C,D)     [3] (E,F, brushType, reserved)
        //   [4] gradient color1 (premul sRGB)      [5] gradient color2 (premul sRGB)
        //   [6] (point1.xy, point2.xy)             [7] (cornerRadii, feather, texE, texF)
        //   [8] texture inverse affine (A,B,C,D)
        layout(std140) uniform UiBrushes
        {
            vec4 uiBrushData[864];
        };

        // Backdrop blur
        uniform sampler2D backdropTexture; // blurred copy of the scene behind the shape
        uniform vec2 viewportSize;         // framebuffer size in pixels
        uniform float backdropBlurAmount;  // > 0 when this fill is frosted glass
        uniform int backdropFlipY;         // 1 to flip the backdrop sample vertically

        // ============== Canvas functions ==============

        // Applies the 2D affine (A,B,C,D in abcd; E,F in ef): p' = (A*x + C*y + E, B*x + D*y + F).
        vec2 applyAffine(vec4 abcd, vec2 ef, vec2 p) {
            return vec2(abcd.x * p.x + abcd.z * p.y + ef.x,
                        abcd.y * p.x + abcd.w * p.y + ef.y);
        }

        float calculateBrushFactor(int rec, int brushType) {
            vec2 logicalPos = fragPos / max(dpiScale, 0.001);
            vec4 m = uiBrushData[rec + 2];
            vec4 t = uiBrushData[rec + 3];
            vec2 transformedPoint = applyAffine(m, t.xy, logicalPos);
            vec4 brushParams = uiBrushData[rec + 6];

            if (brushType == 1) {
                vec2 startPoint = brushParams.xy; vec2 endPoint = brushParams.zw;
                vec2 line = endPoint - startPoint; float lineLength = length(line);
                if (lineLength < 0.001) return 0.0;
                return clamp(dot(transformedPoint - startPoint, line) / (lineLength * lineLength), 0.0, 1.0);
            }
            if (brushType == 2) {
                vec2 center = brushParams.xy;
                return clamp(smoothstep(brushParams.z, brushParams.w, length(transformedPoint - center)), 0.0, 1.0);
            }
            if (brushType == 3) {
                vec2 center = brushParams.xy; vec2 halfSize = brushParams.zw;
                float radius = uiBrushData[rec + 7].x; float feather = uiBrushData[rec + 7].y;
                if (halfSize.x < 0.001 || halfSize.y < 0.001) return 0.0;
                vec2 q = abs(transformedPoint - center) - (halfSize - vec2(radius));
                float dist = min(max(q.x,q.y),0.0) + length(max(q,0.0)) - radius;
                return smoothstep(-feather * 0.5, feather * 0.5, dist);
            }
            return 0.0;
        }

        float scissorMask(int rec, vec2 p) {
            vec4 s0 = uiBrushData[rec];
            vec4 s1 = uiBrushData[rec + 1];
            if(s1.z < 0.0 || s1.w < 0.0) return 1.0;
            float dpi = max(dpiScale, 0.001);
            vec2 logicalP = p / dpi;
            vec2 transformedPoint = applyAffine(s0, s1.xy, logicalP);
            vec2 logicalExt = s1.zw / dpi;
            vec2 distanceFromEdges = abs(transformedPoint) - logicalExt;
            float halfPixelLogical = 0.5 / dpi;
            vec2 smoothEdges = vec2(halfPixelLogical) - distanceFromEdges;
            return clamp(smoothEdges.x, 0.0, 1.0) * clamp(smoothEdges.y, 0.0, 1.0);
        }

        // Single-channel SDF text: width of the distance range in atlas texels (matches Scribe's
        // FontSystem.DistanceRange), and the screen-space span of one unit at this fragment.
        const float sdfPxRange = 4.0;
        float sdfScreenPxRange(vec2 uv) {
            vec2 unitRange = vec2(sdfPxRange) / vec2(textureSize(fontTexture, 0));
            vec2 screenTexSize = vec2(1.0) / fwidth(uv);
            return max(0.5 * dot(unitRange, screenTexSize), 1.0);
        }

        float sampleCoverage(vec2 uv) {
            float d = texture(fontTexture, uv).r;
            float sd = (d - 0.5) * sdfScreenPxRange(uv);
            return clamp(sd + 0.5, 0.0, 1.0);
        }

        // Paper colours are authored as sRGB bytes. The swapchain is sRGB, so shader output is
        // linear. Writing 56/255 (#383838) as if it were linear encodes to ~#828282 and the editor
        // chrome looks milky. Decode, then re-premultiply so One / OneMinusSrcAlpha blending stays
        // in linear.
        vec3 srgbToLinear(vec3 c)
        {
            return c * (c * (c * 0.305306011 + 0.682171111) + 0.012522878);
        }

        vec4 uiColorToLinear(vec4 c)
        {
            float a = max(c.a, 0.0);
            vec3 srgb = (a > 1e-5) ? clamp(c.rgb / a, vec3(0.0), vec3(1.0)) : vec3(0.0);
            return vec4(srgbToLinear(srgb) * a, a);
        }

        // Inverse of srgbToLinear; feeds the text-edge coverage correction below.
        vec3 linearToSrgb(vec3 c)
        {
            c = max(c, vec3(0.0));
            vec3 lo = c * 12.92;
            vec3 hi = 1.055 * pow(c, vec3(1.0 / 2.4)) - 0.055;
            return mix(lo, hi, step(vec3(0.0031308), c));
        }

        // Text coverage gamma correction. Text composites in LINEAR space (see uiColorToLinear),
        // but a linear blend of a light glyph over a dark surface perceptually washes the stroke:
        // half coverage reads far brighter than half ink, so glyphs look thin and fuzzy next to
        // gamma-space-composited native chrome (ClearType). Remap coverage with the exponent that
        // lands the edge where a gamma-space composite would: light text -> exponent > 1 (less
        // ink), dark text -> < 1 (more ink), from the text colour's perceived luminance so both
        // polarities work. 1.5 at the extremes tracks the editor's #191919..#454545 surfaces
        // (#ABABAB on #383838 matches its gamma blend almost exactly); mid-gray stays untouched.
        float textCoverageGamma(float coverage, vec4 premulLinear)
        {
            float a = max(premulLinear.a, 1e-5);
            float l = dot(linearToSrgb(clamp(premulLinear.rgb / a, vec3(0.0), vec3(1.0))), vec3(0.2126, 0.7152, 0.0722));
            float exponent = pow(1.5, l * 2.0 - 1.0); // 1.5 white-on-dark / 1.0 mid / ~0.67 dark-on-light
            return pow(coverage, exponent);
        }

        void main()
        {
            // The three vertices of any triangle share one brush index, so the flat varying is
            // exact; the +0.5 round only guards float precision.
            int rec = int(fragBrushIndex + 0.5) * 9;

            float mask = scissorMask(rec, fragPos);
            vec4 color = fragColor;

            int brushType = int(uiBrushData[rec + 3].z + 0.5);
            if (brushType > 0) {
                float factor = calculateBrushFactor(rec, brushType);
                color = mix(uiBrushData[rec + 4], uiBrushData[rec + 5], factor);
            }

            color = uiColorToLinear(color);

            // SDF text mode: UV.x >= 2.0. The atlas holds a single-channel signed distance field
            // (replicated across RGB); reconstruct sharp coverage from it.
            if (fragTexCoord.x >= 2.0) {
                vec2 uv = fragTexCoord - vec2(2.0);

                // Basic SDF Text
                finalColor = color * textCoverageGamma(sampleCoverage(uv), color) * mask;
                return;

                // Sub-Pixel SDF Text - Needs a Dual Blend or something? It does sorta work but not well enough.
                //vec2 uvPerScreenX = dFdx(uv);
                //vec2 shift = uvPerScreenX / 3.0;
                //
                //vec3 coverage = vec3(sampleCoverage(uv - shift), sampleCoverage(uv), sampleCoverage(uv + shift));

                //float alpha = (coverage.x + coverage.y + coverage.z) / 3.0;
                //finalColor = vec4(color.rgb * coverage, alpha) * mask;
                //return;
            }

            // Edge anti-aliasing: coverage is baked into the geometry (fringe vertices) and carried
            // in fragTexCoord.x (1 = solid core, 0 = outer fringe edge).
            float edgeAlpha = clamp(fragTexCoord.x, 0.0, 1.0);

            // UseTexture ([3].w): untextured records skip the texture0 sample entirely, so they
            // batch with draws that bound any texture (they don't rely on texture0 being white).
            vec4 fill = color;
            if (uiBrushData[rec + 3].w > 0.5) {
                float dpi = max(dpiScale, 0.001);
                vec2 logicalPos = fragPos / dpi;
                vec4 texel = texture(texture0, applyAffine(uiBrushData[rec + 8], uiBrushData[rec + 7].zw, logicalPos));
                if (texture0SrgbDecode > 0.5) texel.rgb = srgbToLinear(texel.rgb);
                // Canvas uses premultiplied-alpha blending. Image files contain straight RGBA, so
                // premultiply their sampled RGB to keep fully transparent pixels from tinting the UI.
                fill = color * vec4(texel.rgb * texel.a, texel.a);
            }

            // Backdrop blur: composite the fill over the blurred scene behind the shape.
            if (backdropBlurAmount > 0.0) {
                vec2 uv = fragPos / viewportSize;
                if (backdropFlipY == 1) uv.y = 1.0 - uv.y;
                vec3 blurred = texture(backdropTexture, uv).rgb;
                vec3 outRgb = blurred * (1.0 - fill.a) + fill.rgb;  // fill is premultiplied
                finalColor = vec4(outRgb, 1.0) * edgeAlpha * mask;
                return;
            }

            finalColor = fill * edgeAlpha * mask;
        }
    }

    ENDGLSL

    SLANGPROGRAM
    Vertex
    {
        cbuffer UIVS : register(b0)
        {
            float4x4 projection;
        };

        struct VSInput
        {
            float2 aPosition : POSITION;
            float2 aTexCoord : TEXCOORD0;
            float aBrushIndex : TEXCOORD1;
            float4 aColor : COLOR;
        };

        struct VSOutput
        {
            float4 position : SV_Position;
            float2 fragTexCoord : TEXCOORD0;
            float4 fragColor : COLOR0;
            float2 fragPos : TEXCOORD1;
            nointerpolation float fragBrushIndex : TEXCOORD2;
        };

        VSOutput main(VSInput input)
        {
            VSOutput o;
            o.fragTexCoord = input.aTexCoord;
            o.fragColor = input.aColor;
            o.fragPos = input.aPosition;
            o.fragBrushIndex = input.aBrushIndex;
            o.position = mul(projection, float4(input.aPosition, 0.0, 1.0));
            return o;
        }
    }

    Fragment
    {
        cbuffer UIPS : register(b1)
        {
            float dpiScale : packoffset(c0.x);
            float2 viewportSize : packoffset(c0.y);
            float backdropBlurAmount : packoffset(c0.w);
            int backdropFlipY : packoffset(c1.x);
            float texture0SrgbDecode : packoffset(c1.y);
        };

        // Per-draw-state records (scissor + brush), addressed by the vertex brushIndex so
        // many states share one draw call. 96 records * 9 float4 each = 864 slots; must
        // match Canvas.BrushRecordsPerPage / UiBrushRecord in Quill. Slot layout per record:
        //   [0] scissor inverse affine (A,B,C,D)   [1] (E,F, extentX, extentY)
        //   [2] brush inverse affine (A,B,C,D)     [3] (E,F, brushType, reserved)
        //   [4] gradient color1 (premul sRGB)      [5] gradient color2 (premul sRGB)
        //   [6] (point1.xy, point2.xy)             [7] (cornerRadii, feather, texE, texF)
        //   [8] texture inverse affine (A,B,C,D)
        cbuffer UiBrushes : register(b2)
        {
            float4 uiBrushData[864];
        };

        Texture2D texture0 : register(t0);
        SamplerState texture0Sampler : register(s0);
        Texture2D fontTexture : register(t1);
        SamplerState fontSampler : register(s1);
        Texture2D backdropTexture : register(t2);
        SamplerState backdropSampler : register(s2);

        struct PSInput
        {
            float4 position : SV_Position;
            float2 fragTexCoord : TEXCOORD0;
            float4 fragColor : COLOR0;
            float2 fragPos : TEXCOORD1;
            nointerpolation float fragBrushIndex : TEXCOORD2;
        };

        // Applies the 2D affine (A,B,C,D in abcd; E,F in ef): p' = (A*x + C*y + E, B*x + D*y + F).
        float2 applyAffine(float4 abcd, float2 ef, float2 p)
        {
            return float2(abcd.x * p.x + abcd.z * p.y + ef.x,
                          abcd.y * p.x + abcd.w * p.y + ef.y);
        }

        float calculateBrushFactor(int rec, int brushType, float2 fragPos)
        {
            float2 logicalPos = fragPos / max(dpiScale, 0.001);
            float4 m = uiBrushData[rec + 2];
            float4 t = uiBrushData[rec + 3];
            float2 transformedPoint = applyAffine(m, t.xy, logicalPos);
            float4 brushParams = uiBrushData[rec + 6];
            if (brushType == 1)
            {
                float2 startPoint = brushParams.xy;
                float2 endPoint = brushParams.zw;
                float2 lineDir = endPoint - startPoint;
                float lineLength = length(lineDir);
                if (lineLength < 0.001) return 0.0;
                return saturate(dot(transformedPoint - startPoint, lineDir) / (lineLength * lineLength));
            }
            if (brushType == 2)
            {
                float2 center = brushParams.xy;
                return saturate(smoothstep(brushParams.z, brushParams.w, length(transformedPoint - center)));
            }
            if (brushType == 3)
            {
                float2 center = brushParams.xy;
                float2 halfSize = brushParams.zw;
                float radius = uiBrushData[rec + 7].x;
                float feather = uiBrushData[rec + 7].y;
                if (halfSize.x < 0.001 || halfSize.y < 0.001) return 0.0;
                float2 q = abs(transformedPoint - center) - (halfSize - radius.xx);
                float dist = min(max(q.x, q.y), 0.0) + length(max(q, 0.0.xx)) - radius;
                return smoothstep(-feather * 0.5, feather * 0.5, dist);
            }
            return 0.0;
        }

        float scissorMask(int rec, float2 p)
        {
            float4 s0 = uiBrushData[rec];
            float4 s1 = uiBrushData[rec + 1];
            if (s1.z < 0.0 || s1.w < 0.0) return 1.0;
            float dpi = max(dpiScale, 0.001);
            float2 logicalP = p / dpi;
            float2 transformedPoint = applyAffine(s0, s1.xy, logicalP);
            float2 logicalExt = s1.zw / dpi;
            float2 distanceFromEdges = abs(transformedPoint) - logicalExt;
            float halfPixelLogical = 0.5 / dpi;
            float2 smoothEdges = halfPixelLogical.xx - distanceFromEdges;
            return saturate(smoothEdges.x) * saturate(smoothEdges.y);
        }

        // Match the GLSL SDF reconstruction: coverage must scale with the atlas texel range and
        // screen-space derivatives, otherwise small Paper text can collapse on Vulkan/D3D.
        static const float sdfPxRange = 4.0;
        float sdfScreenPxRange(float2 uv)
        {
            uint width;
            uint height;
            fontTexture.GetDimensions(width, height);
            float2 unitRange = float2(sdfPxRange, sdfPxRange) / float2(width, height);
            // slang0027s Metal target rejects fwidth (E36107 unavailable feature); ddx+ddy is the
            // exact identity: fwidth(uv) == abs(ddx(uv)) + abs(ddy(uv)).
            float2 du = abs(ddx(uv));
            float2 dv = abs(ddy(uv));
            float2 screenTexSize = float2(1.0, 1.0) / (du + dv);
            return max(0.5 * dot(unitRange, screenTexSize), 1.0);
        }

        float3 srgbToLinear(float3 c)
        {
            return c * (c * (c * 0.305306011 + 0.682171111) + 0.012522878);
        }

        float4 uiColorToLinear(float4 c)
        {
            float a = max(c.a, 0.0);
            float3 srgb = (a > 1e-5) ? saturate(c.rgb / a) : 0.0.xxx;
            return float4(srgbToLinear(srgb) * a, a);
        }

        // Inverse of srgbToLinear; feeds the text-edge coverage correction below.
        float3 linearToSrgb(float3 c)
        {
            c = max(c, 0.0.xxx);
            float3 lo = c * 12.92;
            float3 hi = 1.055 * pow(c, float3(1.0 / 2.4, 1.0 / 2.4, 1.0 / 2.4)) - 0.055;
            return lerp(lo, hi, step(float3(0.0031308, 0.0031308, 0.0031308), c));
        }

        // Text coverage gamma correction (see the GLSL variant for the full rationale):
        // linear-space compositing washes glyph edges; pow(coverage, gamma) with gamma from the
        // text colour's perceived luminance restores gamma-space edge placement for both
        // light-on-dark (gamma > 1) and dark-on-light (gamma < 1) text.
        float textCoverageGamma(float coverage, float4 premulLinear)
        {
            float a = max(premulLinear.a, 1e-5);
            float l = dot(linearToSrgb(saturate(premulLinear.rgb / a)), float3(0.2126, 0.7152, 0.0722));
            float exponent = pow(1.5, l * 2.0 - 1.0);
            return pow(coverage, exponent);
        }

        float4 main(PSInput input) : SV_Target
        {
            // The three vertices of any triangle share one brush index, so the flat varying is
            // exact; the +0.5 round only guards float precision.
            int rec = (int)(input.fragBrushIndex + 0.5) * 9;

            float mask = scissorMask(rec, input.fragPos);
            float4 color = input.fragColor;
            int brushType = (int)(uiBrushData[rec + 3].z + 0.5);
            if (brushType > 0)
            {
                float factor = calculateBrushFactor(rec, brushType, input.fragPos);
                color = lerp(uiBrushData[rec + 4], uiBrushData[rec + 5], factor);
            }

            color = uiColorToLinear(color);

            if (input.fragTexCoord.x >= 2.0)
            {
                float2 uv = input.fragTexCoord - float2(2.0, 2.0);
                float d = fontTexture.Sample(fontSampler, uv).r;
                float coverage = saturate((d - 0.5) * sdfScreenPxRange(uv) + 0.5);
                return color * textCoverageGamma(coverage, color) * mask;
            }

            float edgeAlpha = saturate(input.fragTexCoord.x);

            // UseTexture ([3].w): untextured records skip the texture0 sample entirely, so they
            // batch with draws that bound any texture (they don't rely on texture0 being white).
            float4 fill = color;
            if (uiBrushData[rec + 3].w > 0.5)
            {
                float dpi = max(dpiScale, 0.001);
                float2 logicalPos = input.fragPos / dpi;
                float4 texel = texture0.Sample(texture0Sampler, applyAffine(uiBrushData[rec + 8], uiBrushData[rec + 7].zw, logicalPos));
                if (texture0SrgbDecode > 0.5) texel.rgb = srgbToLinear(texel.rgb);
                // Canvas uses premultiplied-alpha blending. Image files contain straight RGBA, so
                // premultiply their sampled RGB to keep fully transparent pixels from tinting the UI.
                fill = color * float4(texel.rgb * texel.a, texel.a);
            }

            if (backdropBlurAmount > 0.0)
            {
                float2 uv = input.fragPos / viewportSize;
                if (backdropFlipY == 1) uv.y = 1.0 - uv.y;
                float3 blurred = backdropTexture.Sample(backdropSampler, uv).rgb;
                float3 outRgb = blurred * (1.0 - fill.a) + fill.rgb;
                return float4(outRgb, 1.0) * edgeAlpha * mask;
            }

            return fill * edgeAlpha * mask;
        }
    }
    ENDSLANG
}

Pass "BlurDown"
{
    Tags { "RenderOrder" = "Opaque" }

    Blend Off
    ZTest Off
    ZWrite Off
    Cull Off

    GLSLPROGRAM

    Vertex
    {
        layout (location = 0) in vec3 vertexPosition;
        layout (location = 1) in vec2 vertexTexCoord;

        out vec2 TexCoords;

        void main()
        {
            TexCoords = vertexTexCoord;
            gl_Position = vec4(vertexPosition, 1.0);
        }
    }

    Fragment
    {
        uniform sampler2D _MainTex;
        uniform float _Offset;

        layout(location = 0) out vec4 FragColor;

        in vec2 TexCoords;

        void main()
        {
            vec2 halfpixel = (0.5 / vec2(textureSize(_MainTex, 0))) * _Offset;

            vec4 sum = texture(_MainTex, TexCoords) * 4.0;
            sum += texture(_MainTex, TexCoords - halfpixel);
            sum += texture(_MainTex, TexCoords + halfpixel);
            sum += texture(_MainTex, TexCoords + vec2(halfpixel.x, -halfpixel.y));
            sum += texture(_MainTex, TexCoords - vec2(halfpixel.x, -halfpixel.y));

            FragColor = sum / 8.0;
        }
    }

    ENDGLSL

    SLANGPROGRAM
    Vertex
    {
        struct VSInput
        {
            float3 vertexPosition : POSITION;
            float2 vertexTexCoord : TEXCOORD0;
        };

        struct VSOutput
        {
            float4 position : SV_Position;
            float2 TexCoords : TEXCOORD0;
        };

        VSOutput main(VSInput input)
        {
            VSOutput o;
            o.TexCoords = input.vertexTexCoord;
            o.position = float4(input.vertexPosition, 1.0);
            return o;
        }
    }

    Fragment
    {
        cbuffer BlurDownPS : register(b0)
        {
            float _Offset;
        };

        Texture2D _MainTex : register(t0);
        SamplerState _MainTexSampler : register(s0);

        struct PSInput
        {
            float4 position : SV_Position;
            float2 TexCoords : TEXCOORD0;
        };

        float4 main(PSInput input) : SV_Target
        {
            float2 texSize;
            _MainTex.GetDimensions(texSize.x, texSize.y);
            float2 halfpixel = (0.5 / texSize) * _Offset;
            float4 sum = _MainTex.Sample(_MainTexSampler, input.TexCoords) * 4.0;
            sum += _MainTex.Sample(_MainTexSampler, input.TexCoords - halfpixel);
            sum += _MainTex.Sample(_MainTexSampler, input.TexCoords + halfpixel);
            sum += _MainTex.Sample(_MainTexSampler, input.TexCoords + float2(halfpixel.x, -halfpixel.y));
            sum += _MainTex.Sample(_MainTexSampler, input.TexCoords - float2(halfpixel.x, -halfpixel.y));
            return sum / 8.0;
        }
    }
    ENDSLANG
}

Pass "BlurUp"
{
    Tags { "RenderOrder" = "Opaque" }

    Blend Off
    ZTest Off
    ZWrite Off
    Cull Off

    GLSLPROGRAM

    Vertex
    {
        layout (location = 0) in vec3 vertexPosition;
        layout (location = 1) in vec2 vertexTexCoord;

        out vec2 TexCoords;

        void main()
        {
            TexCoords = vertexTexCoord;
            gl_Position = vec4(vertexPosition, 1.0);
        }
    }

    Fragment
    {
        uniform sampler2D _MainTex;
        uniform float _Offset;

        layout(location = 0) out vec4 FragColor;

        in vec2 TexCoords;

        void main()
        {
            vec2 halfpixel = (0.5 / vec2(textureSize(_MainTex, 0))) * _Offset;

            vec4 sum = texture(_MainTex, TexCoords + vec2(-halfpixel.x * 2.0, 0.0));
            sum += texture(_MainTex, TexCoords + vec2(-halfpixel.x, halfpixel.y)) * 2.0;
            sum += texture(_MainTex, TexCoords + vec2(0.0, halfpixel.y * 2.0));
            sum += texture(_MainTex, TexCoords + vec2(halfpixel.x, halfpixel.y)) * 2.0;
            sum += texture(_MainTex, TexCoords + vec2(halfpixel.x * 2.0, 0.0));
            sum += texture(_MainTex, TexCoords + vec2(halfpixel.x, -halfpixel.y)) * 2.0;
            sum += texture(_MainTex, TexCoords + vec2(0.0, -halfpixel.y * 2.0));
            sum += texture(_MainTex, TexCoords + vec2(-halfpixel.x, -halfpixel.y)) * 2.0;

            FragColor = sum / 12.0;
        }
    }

    ENDGLSL

    SLANGPROGRAM
    Vertex
    {
        struct VSInput
        {
            float3 vertexPosition : POSITION;
            float2 vertexTexCoord : TEXCOORD0;
        };

        struct VSOutput
        {
            float4 position : SV_Position;
            float2 TexCoords : TEXCOORD0;
        };

        VSOutput main(VSInput input)
        {
            VSOutput o;
            o.TexCoords = input.vertexTexCoord;
            o.position = float4(input.vertexPosition, 1.0);
            return o;
        }
    }

    Fragment
    {
        cbuffer BlurUpPS : register(b0)
        {
            float _Offset;
        };

        Texture2D _MainTex : register(t0);
        SamplerState _MainTexSampler : register(s0);

        struct PSInput
        {
            float4 position : SV_Position;
            float2 TexCoords : TEXCOORD0;
        };

        float4 main(PSInput input) : SV_Target
        {
            float2 texSize;
            _MainTex.GetDimensions(texSize.x, texSize.y);
            float2 halfpixel = (0.5 / texSize) * _Offset;
            float4 sum = _MainTex.Sample(_MainTexSampler, input.TexCoords + float2(-halfpixel.x * 2.0, 0.0));
            sum += _MainTex.Sample(_MainTexSampler, input.TexCoords + float2(-halfpixel.x, halfpixel.y)) * 2.0;
            sum += _MainTex.Sample(_MainTexSampler, input.TexCoords + float2(0.0, halfpixel.y * 2.0));
            sum += _MainTex.Sample(_MainTexSampler, input.TexCoords + float2(halfpixel.x, halfpixel.y)) * 2.0;
            sum += _MainTex.Sample(_MainTexSampler, input.TexCoords + float2(halfpixel.x * 2.0, 0.0));
            sum += _MainTex.Sample(_MainTexSampler, input.TexCoords + float2(halfpixel.x, -halfpixel.y)) * 2.0;
            sum += _MainTex.Sample(_MainTexSampler, input.TexCoords + float2(0.0, -halfpixel.y * 2.0));
            sum += _MainTex.Sample(_MainTexSampler, input.TexCoords + float2(-halfpixel.x, -halfpixel.y)) * 2.0;
            return sum / 12.0;
        }
    }
    ENDSLANG
}
