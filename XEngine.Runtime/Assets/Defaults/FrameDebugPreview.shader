Shader "Default/FrameDebugPreview"

Variants
{
}

Properties
{
	_FlipY ("Flip Render Target Y", Float) = 0
	_ChannelMode ("Channel Mode (0=All 1=R 2=G 3=B 4=A)", Float) = 0
	_LevelsMin ("Levels Min", Float) = 0
	_LevelsMax ("Levels Max", Float) = 1
}

Pass "FrameDebugPreview"
{
    Tags { "RenderOrder" = "Opaque" }

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
		uniform float _ChannelMode;
		uniform float _LevelsMin;
		uniform float _LevelsMax;

		float Remap(float v)
		{
			float lo = _LevelsMin;
			float hi = max(_LevelsMax, lo + 1e-5);
			return clamp((v - lo) / (hi - lo), 0.0, 1.0);
		}

		void main()
		{
			vec4 c = texture(_MainTex, TexCoords);
			int mode = int(_ChannelMode + 0.5);
			vec3 rgb;
			if (mode == 1)      rgb = vec3(Remap(c.r));
			else if (mode == 2) rgb = vec3(Remap(c.g));
			else if (mode == 3) rgb = vec3(Remap(c.b));
			else if (mode == 4) rgb = vec3(Remap(c.a));
			else                rgb = vec3(Remap(c.r), Remap(c.g), Remap(c.b));
			finalColor = vec4(rgb, 1.0);
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
		cbuffer FrameDebugPreviewPS : register(b2)
		{
			float _ChannelMode;
			float _LevelsMin;
			float _LevelsMax;
			float _FlipY;
		};

		Texture2D _MainTex : register(t0);
		SamplerState _MainTexSampler : register(s0);

		struct PSInput
		{
			float4 position : SV_Position;
			float2 TexCoords : TEXCOORD0;
		};

		float Remap(float v)
		{
			float lo = _LevelsMin;
			float hi = max(_LevelsMax, lo + 1e-5);
			return saturate((v - lo) / (hi - lo));
		}

		float4 main(PSInput input) : SV_Target
		{
			float2 sampleCoords = float2(input.TexCoords.x, lerp(input.TexCoords.y, 1.0 - input.TexCoords.y, _FlipY));
			float4 c = _MainTex.Sample(_MainTexSampler, sampleCoords);
			int mode = (int)(_ChannelMode + 0.5);
			float3 rgb;
			if (mode == 1)      rgb = Remap(c.r).xxx;
			else if (mode == 2) rgb = Remap(c.g).xxx;
			else if (mode == 3) rgb = Remap(c.b).xxx;
			else if (mode == 4) rgb = Remap(c.a).xxx;
			else                rgb = float3(Remap(c.r), Remap(c.g), Remap(c.b));
			return float4(rgb, 1.0);
		}
	}

	ENDSLANG
}
