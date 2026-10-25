Shader "Default/Grid"

Variants
{
}

Properties
{
    _GridColor ("Grid Color", Color) = (0.5, 0.5, 0.5, 0.3)
    _PrimaryGridSize ("Primary Grid Size", Float) = 1.0
    _SecondaryGridSize ("Secondary Grid Size", Float) = 0.25
    _LineWidth ("Line Width", Float) = 0.02
    _Falloff ("Falloff", Float) = 1.5
    _MaxDist ("Max Distance", Float) = 500.0
}

Pass "Grid"
{
    Tags { "RenderOrder" = "Transparent" }
    Blend Alpha
    ZWrite Off
    ZTest Lequal
    Cull None

    GLSLPROGRAM
        Vertex
        {
            #include "XEngineCG"
            #include "VertexAttributes"

            out vec3 worldPos;
            out vec3 viewPos;
            out vec2 uv;
            out vec4 clipPos;

            void main()
            {
                vec4 wp = XENGINE_MATRIX_M * vec4(vertexPosition, 1.0);
                worldPos = wp.xyz;
                viewPos = (XENGINE_MATRIX_V * wp).xyz;
                uv = wp.xz;
                clipPos = XENGINE_MATRIX_MVP * vec4(vertexPosition, 1.0);
                gl_Position = clipPos;
            }
        }

        Fragment
        {
            #include "XEngineCG"

            layout (location = 0) out vec4 FragColor;

            in vec3 worldPos;
            in vec3 viewPos;
            in vec2 uv;
            in vec4 clipPos;

            uniform vec4 _GridColor;
            uniform float _PrimaryGridSize;
            uniform float _SecondaryGridSize;
            uniform float _LineWidth;
            uniform float _Falloff;
            uniform float _MaxDist;

            uniform sampler2D _CameraDepthTexture;

            // Pristine grid anti-aliased grid lines using screen-space derivatives
            // https://bgolus.medium.com/the-best-darn-grid-shader-yet-727f9278b9d8
            float pristineGrid(vec2 uv, vec2 lineWidth)
            {
                lineWidth = clamp(lineWidth, vec2(0.0), vec2(0.5));

                vec4 uvDDXY = vec4(dFdx(uv), dFdy(uv));
                vec2 uvDeriv = vec2(length(uvDDXY.xz), length(uvDDXY.yw));

                bvec2 invertLine = greaterThan(lineWidth, vec2(0.5));

                vec2 targetWidth = vec2(
                    invertLine.x ? 1.0 - lineWidth.x : lineWidth.x,
                    invertLine.y ? 1.0 - lineWidth.y : lineWidth.y
                );

                vec2 drawWidth = clamp(targetWidth, uvDeriv, vec2(0.5));
                vec2 lineAA = max(uvDeriv, vec2(0.000001)) * 1.5;
                vec2 gridUV = abs(fract(uv) * 2.0 - 1.0);

                gridUV.x = invertLine.x ? gridUV.x : 1.0 - gridUV.x;
                gridUV.y = invertLine.y ? gridUV.y : 1.0 - gridUV.y;

                vec2 grid2 = smoothstep(drawWidth + lineAA, drawWidth - lineAA, gridUV);
                grid2 *= clamp(targetWidth / drawWidth, vec2(0.0), vec2(1.0));
                grid2 = mix(grid2, targetWidth, clamp(uvDeriv * 2.0 - 1.0, vec2(0.0), vec2(1.0)));

                grid2.x = invertLine.x ? 1.0 - grid2.x : grid2.x;
                grid2.y = invertLine.y ? 1.0 - grid2.y : grid2.y;

                return mix(grid2.x, 1.0, grid2.y);
            }

            void main()
            {
                // Sample scene depth and compare with grid fragment depth
                vec2 screenUV = gl_FragCoord.xy / _ScreenParams.xy;
                float sceneDepthRaw = texture(_CameraDepthTexture, screenUV).r;
                float sceneDepthLinear = linearizeDepthFromProjection(sceneDepthRaw);
                float gridDepthLinear = linearizeDepthFromProjection(gl_FragCoord.z);

                // Discard if scene geometry is at nearly the same depth (z-fight zone)
                float depthDiff = abs(sceneDepthLinear - gridDepthLinear);
                float threshold = gridDepthLinear * 0.005; // 0.5% of depth scales with distance
                if (depthDiff < threshold)
                    discard;

                // Primary (small) and secondary (large) grids
                float sg = pristineGrid(uv * _PrimaryGridSize, vec2(_LineWidth));
                float bg = pristineGrid(uv * _SecondaryGridSize, vec2(_LineWidth));

                float gridAlpha = max(sg, bg);

                // Axis highlights constant screen-width lines
                vec3 color = _GridColor.rgb;

                float dzPerPx = length(vec2(dFdx(uv.y), dFdy(uv.y)));
                float dxPerPx = length(vec2(dFdx(uv.x), dFdy(uv.x)));

                // X axis (red, runs along X where Z ~= 0)
                float xAxis = 1.0 - smoothstep(0.0, dzPerPx * 1.5, abs(uv.y));
                // Z axis (blue, runs along Z where X ~= 0)
                float zAxis = 1.0 - smoothstep(0.0, dxPerPx * 1.5, abs(uv.x));

                color = mix(color, vec3(0.9, 0.2, 0.2), xAxis);
                gridAlpha = max(gridAlpha, xAxis * 0.9);
                color = mix(color, vec3(0.2, 0.4, 0.9), zAxis);
                gridAlpha = max(gridAlpha, zAxis * 0.9);

                // Distance fade
                float dist = length(viewPos);
                float fade = 1.0 - pow(clamp(dist / _MaxDist, 0.0, 1.0), _Falloff);

                FragColor = vec4(color, gridAlpha * _GridColor.a * fade);
            }
        }
    ENDGLSL

    SLANGPROGRAM
        Vertex
        {
            #include "XEngineCG"

            struct VSInput
            {
                float3 vertexPosition : POSITION;
            };

            struct VSOutput
            {
                float4 position : SV_Position;
                float3 worldPos : TEXCOORD0;
                float3 viewPos : TEXCOORD1;
                float2 uv : TEXCOORD2;
            };

            VSOutput main(VSInput input)
            {
                VSOutput o;
                float4 wp = mul(XENGINE_MATRIX_M, float4(input.vertexPosition, 1.0));
                o.worldPos = wp.xyz;
                o.viewPos = mul(XENGINE_MATRIX_V, wp).xyz;
                o.uv = wp.xz;
                o.position = mul(XENGINE_MATRIX_VP, wp);
                return o;
            }
        }

        Fragment
        {
            #include "XEngineCG"

            cbuffer GridPS : register(b2)
            {
                float4 _GridColor;
                float _PrimaryGridSize;
                float _SecondaryGridSize;
                float _LineWidth;
                float _Falloff;
                float _MaxDist;
            };

            Texture2D _CameraDepthTexture : register(t0);
            SamplerState _CameraDepthSampler : register(s0);

            struct PSInput
            {
                float4 position : SV_Position;
                float3 worldPos : TEXCOORD0;
                float3 viewPos : TEXCOORD1;
                float2 uv : TEXCOORD2;
            };

            float pristineGrid(float2 uv, float2 lineWidth)
            {
                lineWidth = clamp(lineWidth, 0.0.xx, 0.5.xx);
                float4 uvDDXY = float4(ddx(uv), ddy(uv));
                float2 uvDeriv = float2(length(uvDDXY.xz), length(uvDDXY.yw));
                bool2 invertLine = lineWidth > 0.5.xx;
                float2 targetWidth = float2(invertLine.x ? 1.0 - lineWidth.x : lineWidth.x,
                                           invertLine.y ? 1.0 - lineWidth.y : lineWidth.y);
                float2 drawWidth = clamp(targetWidth, uvDeriv, 0.5.xx);
                float2 lineAA = max(uvDeriv, 0.000001.xx) * 1.5;
                float2 gridUV = abs(frac(uv) * 2.0 - 1.0);
                gridUV.x = invertLine.x ? gridUV.x : 1.0 - gridUV.x;
                gridUV.y = invertLine.y ? gridUV.y : 1.0 - gridUV.y;
                float2 grid2 = smoothstep(drawWidth + lineAA, drawWidth - lineAA, gridUV);
                grid2 *= saturate(targetWidth / drawWidth);
                grid2 = lerp(grid2, targetWidth, saturate(uvDeriv * 2.0 - 1.0));
                grid2.x = invertLine.x ? 1.0 - grid2.x : grid2.x;
                grid2.y = invertLine.y ? 1.0 - grid2.y : grid2.y;
                return lerp(grid2.x, 1.0, grid2.y);
            }

            float4 main(PSInput input) : SV_Target
            {
                float2 screenUV = input.position.xy / _ScreenParams.xy;
                float sceneDepthRaw = _CameraDepthTexture.Sample(_CameraDepthSampler, screenUV).r;
                float sceneDepthLinear = linearizeDepthFromProjection(sceneDepthRaw);
                float gridDepthLinear = linearizeDepthFromProjection(input.position.z);
                float depthDiff = abs(sceneDepthLinear - gridDepthLinear);
                float threshold = gridDepthLinear * 0.005;
                if (depthDiff < threshold)
                    discard;

                float sg = pristineGrid(input.uv * _PrimaryGridSize, _LineWidth.xx);
                float bg = pristineGrid(input.uv * _SecondaryGridSize, _LineWidth.xx);
                float gridAlpha = max(sg, bg);
                float3 color = _GridColor.rgb;

                float dzPerPx = length(float2(ddx(input.uv.y), ddy(input.uv.y)));
                float dxPerPx = length(float2(ddx(input.uv.x), ddy(input.uv.x)));
                float xAxis = 1.0 - smoothstep(0.0, dzPerPx * 1.5, abs(input.uv.y));
                float zAxis = 1.0 - smoothstep(0.0, dxPerPx * 1.5, abs(input.uv.x));
                color = lerp(color, float3(0.9, 0.2, 0.2), xAxis);
                gridAlpha = max(gridAlpha, xAxis * 0.9);
                color = lerp(color, float3(0.2, 0.4, 0.9), zAxis);
                gridAlpha = max(gridAlpha, zAxis * 0.9);

                float dist = length(input.viewPos);
                float fade = 1.0 - pow(saturate(dist / _MaxDist), _Falloff);
                return float4(color, gridAlpha * _GridColor.a * fade);
            }
        }
    ENDSLANG
}
