Shader "Default/Blit"

Variants
{
}

Properties
{
	_FlipY ("Flip Render Target Y", Float) = 0
}

Pass "Blit"
{
    Tags { "RenderOrder" = "Opaque" }

    // Rasterizer culling mode
    // A copy must preserve RGB even when post-processing or VFX leave zero alpha.
    Blend Off
    Cull None
    ZTest Off
    ZWrite Off

	GLSLPROGRAM

	Vertex
	{
		layout (location = 0) in vec3 vertexPosition;
		layout (location = 1) in vec2 vertexTexCoord;
		
		out vec2 TexCoords;
		uniform float _FlipY;
		
		void main()
		{
			TexCoords = vec2(vertexTexCoord.x, mix(vertexTexCoord.y, 1.0 - vertexTexCoord.y, _FlipY));
		    gl_Position = vec4(vertexPosition, 1.0);
		}
	}

	Fragment
	{
		layout (location = 0) out vec4 finalColor;
		
		in vec2 TexCoords;

		uniform sampler2D _MainTex;
		uniform float _FlipY;

		void main()
		{
			finalColor = texture(_MainTex, TexCoords).rgba;
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
		cbuffer BlitMaterial : register(b2)
		{
			float _FlipY;
		};

		struct PSInput
		{
			float4 position : SV_Position;
			float2 TexCoords : TEXCOORD0;
		};

		Texture2D _MainTex : register(t0);
		SamplerState _MainTexSampler : register(s0);

		float4 main(PSInput input) : SV_Target
		{
			float2 uv = float2(input.TexCoords.x, lerp(input.TexCoords.y, 1.0 - input.TexCoords.y, _FlipY));
			return _MainTex.Sample(_MainTexSampler, uv);
		}
	}

	ENDSLANG
}
