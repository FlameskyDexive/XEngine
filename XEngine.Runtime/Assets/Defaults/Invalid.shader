Shader "Default/Invalid"

Variants
{
}

Properties
{
}

Pass "Invalid"
{
	Tags { "RenderType" = "Opaque" }
	Cull None

	GLSLPROGRAM
	Vertex
	{
		#include "XEngineCG"
		#include "VertexAttributes"

		void main()
		{
			gl_Position = XENGINE_MATRIX_VP * vec4(vertexPosition, 1.0);
		}
	}

	Fragment
	{
		layout (location = 0) out vec4 fragColor;

		void main()
		{
			fragColor = vec4(1.0, 0.0, 1.0, 1.0);
		}
	}
	ENDGLSL

	SLANGPROGRAM
	Vertex
	{
		// Match every other built-in shader: include XEngineCG (GlobalUniforms cbuffer at
		// register(b0) is what the engine binds each frame — the old hand-rolled
		// "XEnginePerFrame" cbuffer was never bound, so the fallback shader crashed every
		// Vulkan draw that fell back to it).
		#include "XEngineCG"

		struct VSInput
		{
			float3 vertexPosition : POSITION;
		};

		float4 main(VSInput input) : SV_Position
		{
			return TransformClip(input.vertexPosition);
		}
	}

	Fragment
	{
		float4 main() : SV_Target
		{
			return float4(1.0, 0.0, 1.0, 1.0);
		}
	}
	ENDSLANG
}
