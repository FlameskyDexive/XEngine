Shader "Default/DefaultUI"

Variants
{
}

Properties
{
    _MainTex ("Texture", Texture2D) = "white"
    _MainColor ("Tint", Color) = (1.0, 1.0, 1.0, 1.0)
    _Tiling ("Tiling", Vector2) = (1.0, 1.0)
    _Offset ("Offset", Vector2) = (0.0, 0.0)
}

Pass "DefaultUI"
{
    Tags { "RenderOrder" = "UI" }

    Blend {
        Src SrcAlpha
        Dst OneMinusSrcAlpha
        Mode Add
    }
    ZTest Off
    ZWrite Off
    Cull Off

    GLSLPROGRAM
		Vertex
		{
            #include "XEngineCG"
            #include "VertexAttributes"

			out vec2 texCoord0;
			out vec3 worldPos;
			out vec4 vColor;

			uniform vec2 _Tiling;
			uniform vec2 _Offset;

			void main()
			{
				gl_Position = TransformClip(vertexPosition);
				texCoord0 = vertexTexCoord0 * _Tiling + _Offset;
				worldPos = TransformPosition(vertexPosition);
				vColor = GetInstanceColor();
			}
		}

		Fragment
		{
            #include "XEngineCG"
            #include "Lighting"

			layout (location = 0) out vec4 fragColor;

			in vec2 texCoord0;
			in vec3 worldPos;
			in vec4 vColor;

			uniform sampler2D _MainTex;
			uniform vec4 _MainColor;

			// Per-item rounded-rect clip (RectMask). _ClipToLocal maps the fragment's world position
			// into the mask's local space (so the clip follows rotation/scale); the fragment is tested
			// against _ClipRect with _ClipRadius rounded corners and a _ClipSoftness edge.
			uniform mat4 _ClipToLocal;
			uniform vec4 _ClipRect;      // minX, minY, maxX, maxY
			uniform float _ClipRadius;
			uniform float _ClipSoftness;
			uniform float _ClipEnable;

			float uiClipCoverage(vec3 worldPosition)
			{
				if (_ClipEnable < 0.5) return 1.0;
				vec2 p = (_ClipToLocal * vec4(worldPosition, 1.0)).xy;
				vec2 c = (_ClipRect.xy + _ClipRect.zw) * 0.5;
				vec2 e = (_ClipRect.zw - _ClipRect.xy) * 0.5 - vec2(_ClipRadius);
				vec2 d = abs(p - c) - e;
				float dist = length(max(d, vec2(0.0))) + min(max(d.x, d.y), 0.0) - _ClipRadius;
				float soft = max(_ClipSoftness, max(fwidth(dist), 1e-4));
				return clamp(0.5 - dist / soft, 0.0, 1.0);
			}

			void main()
			{
				vec4 albedo = texture(_MainTex, texCoord0) * vColor * _MainColor;
				fragColor = albedo * uiClipCoverage(worldPos);
			}
		}
	ENDGLSL

	SLANGPROGRAM
		Vertex
		{
			#include "XEngineCG"

			cbuffer DefaultUIMaterial : register(b2)
			{
				float2 _Tiling;
				float2 _Offset;
				float4 _MainColor;
				float4x4 _ClipToLocal;
				float4 _ClipRect;
				float _ClipRadius;
				float _ClipSoftness;
				float _ClipEnable;
				float _ClipPadding;
			};

			struct VSInput
			{
				float3 vertexPosition : POSITION;
				float2 vertexTexCoord0 : TEXCOORD0;
				float4 vertexColor : COLOR0;
			};

			struct VSOutput
			{
				float4 position : SV_Position;
				float2 texCoord0 : TEXCOORD0;
				float3 worldPos : TEXCOORD1;
				float4 vColor : COLOR0;
			};

			VSOutput main(VSInput input)
			{
				VSOutput o;
				o.position = TransformClip(input.vertexPosition);
				o.texCoord0 = input.vertexTexCoord0 * _Tiling + _Offset;
				o.worldPos = TransformPosition(input.vertexPosition);
				o.vColor = input.vertexColor;
				return o;
			}
		}

		Fragment
		{
			#include "XEngineCG"

			cbuffer DefaultUIMaterial : register(b2)
			{
				float2 _Tiling;
				float2 _Offset;
				float4 _MainColor;
				float4x4 _ClipToLocal;
				float4 _ClipRect;
				float _ClipRadius;
				float _ClipSoftness;
				float _ClipEnable;
				float _ClipPadding;
			};

			Texture2D _MainTex : register(t0);
			SamplerState _MainTexSampler : register(s0);

			struct PSInput
			{
				float4 position : SV_Position;
				float2 texCoord0 : TEXCOORD0;
				float3 worldPos : TEXCOORD1;
				float4 vColor : COLOR0;
			};

			float uiClipCoverage(float3 worldPosition)
			{
				if (_ClipEnable < 0.5) return 1.0;
				float2 p = mul(_ClipToLocal, float4(worldPosition, 1.0)).xy;
				float2 c = (_ClipRect.xy + _ClipRect.zw) * 0.5;
				float2 e = (_ClipRect.zw - _ClipRect.xy) * 0.5 - _ClipRadius.xx;
				float2 d = abs(p - c) - e;
				float dist = length(max(d, 0.0.xx)) + min(max(d.x, d.y), 0.0) - _ClipRadius;
				float distWidth = abs(ddx(dist)) + abs(ddy(dist));
				float soft = max(_ClipSoftness, max(distWidth, 1e-4));
				return saturate(0.5 - dist / soft);
			}

			float4 main(PSInput input) : SV_Target
			{
				float4 albedo = _MainTex.Sample(_MainTexSampler, input.texCoord0) * input.vColor * _MainColor;
				return albedo * uiClipCoverage(input.worldPos);
			}
		}
	ENDSLANG
}
