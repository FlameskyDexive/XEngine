Shader "Default/Shadow2D"

Variants
{
}

Properties
{
    _MainColor ("Light XY Extrude", Color) = (0.0, 0.0, 32.0, 1.0)
    _Tiling ("Edge A", Vector2) = (0.0, 0.0)
    _Offset ("Edge B", Vector2) = (1.0, 0.0)
}

Pass "Shadow2D"
{
    Tags { "RenderOrder" = "Opaque" }
    Blend Off
    ZTest Off
    ZWrite Off
    Cull Off

	GLSLPROGRAM
		Vertex
		{
            #include "XEngineCG"

			layout (location = 0) in vec3 vertexPosition;
			layout (location = 1) in vec2 vertexTexCoord;

			uniform vec2 _Tiling;
			uniform vec2 _Offset;
			uniform vec4 _MainColor;

			void main()
			{
				vec2 a = _Tiling;
				vec2 b = _Offset;
				vec2 light = _MainColor.xy;
				float extrude = max(_MainColor.z, 0.01);
				vec2 da = a - light;
				vec2 db = b - light;
				float la = length(da);
				float lb = length(db);
				da = la > 1e-4 ? da / la : vec2(0.0, 1.0);
				db = lb > 1e-4 ? db / lb : vec2(0.0, 1.0);
				vec2 aFar = a + da * extrude;
				vec2 bFar = b + db * extrude;
				vec2 p = mix(mix(a, b, vertexTexCoord.x), mix(aFar, bFar, vertexTexCoord.x), vertexTexCoord.y);
				gl_Position = XENGINE_MATRIX_VP * vec4(p, 0.0, 1.0);
			}
		}

		Fragment
		{
			layout (location = 0) out vec4 fragColor;

			void main()
			{
				fragColor = vec4(0.0, 0.0, 0.0, 1.0);
			}
		}
	ENDGLSL

	SLANGPROGRAM
		Vertex
		{
			#include "XEngineCG"

			cbuffer UnlitMaterial : register(b2)
			{
				float2 _Tiling;
				float2 _Offset;
				float4 _MainColor;
			};

			struct VSInput
			{
				float3 vertexPosition : POSITION;
				float2 vertexTexCoord : TEXCOORD0;
			};

			struct VSOutput
			{
				float4 position : SV_Position;
			};

			VSOutput main(VSInput input)
			{
				VSOutput o;
				float2 a = _Tiling;
				float2 b = _Offset;
				float2 light = _MainColor.xy;
				float extrude = max(_MainColor.z, 0.01);
				float2 da = a - light;
				float2 db = b - light;
				float la = length(da);
				float lb = length(db);
				da = la > 1e-4 ? da / la : float2(0.0, 1.0);
				db = lb > 1e-4 ? db / lb : float2(0.0, 1.0);
				float2 aFar = a + da * extrude;
				float2 bFar = b + db * extrude;
				float u = input.vertexTexCoord.x;
				float v = input.vertexTexCoord.y;
				float2 p = lerp(lerp(a, b, u), lerp(aFar, bFar, u), v);
				o.position = mul(XENGINE_MATRIX_VP, float4(p, 0.0, 1.0));
				return o;
			}
		}

		Fragment
		{
			float4 main() : SV_Target
			{
				return float4(0.0, 0.0, 0.0, 1.0);
			}
		}
	ENDSLANG
}
