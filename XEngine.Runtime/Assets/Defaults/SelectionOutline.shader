Shader "Default/SelectionOutline"

Variants
{
}

Properties
{
	_MainTex ("Selection Mask", Texture2D) = "black"
}

// Composites the selection outline over the scene color. _MainTex is the selection mask (white where
// a selected object drew, black elsewhere): a fragment outside the mask whose neighbourhood touches
// it is an edge and gets the outline colour; everything else stays fully transparent so the scene
// shows through unchanged. Alpha blending puts the ring on top without tinting the object itself.
//
// Colour and thickness are compile-time constants on purpose. Backends bind material constant
// buffers through a hand-written packer keyed on the cbuffer's name (see
// VulkanCommandTranslator.EnsureMaterialUniformsBuffer and its D3D12 counterpart), so a shader that
// declares a new material cbuffer throws "requires uniform buffer" at draw time until a packer is
// added for it. Theming the ring means adding that packer in every backend - not worth it for a
// fixed Unity-orange outline.
// The mask is sampled with V flipped: the mask pass binds its own render target with a plain
// positive-height viewport, which lands upside down relative to the camera's colour target on the
// Vulkan path this was verified on. Flipping here keeps the ring on the object rather than mirrored
// across the viewport. If a backend ever shows the ring mirrored, this is the knob.
Pass "SelectionOutline"
{
	Tags { "RenderOrder" = "Opaque" }

	Blend Alpha
	Cull None
	ZTest Off
	ZWrite Off

	GLSLPROGRAM

	Vertex
	{
		layout (location = 0) in vec3 vertexPosition;
		layout (location = 1) in vec2 vertexTexCoord;

		out vec2 TexCoords;

		void main()
		{
			TexCoords = vec2(vertexTexCoord.x, 1.0 - vertexTexCoord.y);
			gl_Position = vec4(vertexPosition, 1.0);
		}
	}

	Fragment
	{
		layout (location = 0) out vec4 finalColor;

		in vec2 TexCoords;

		uniform sampler2D _MainTex;

		const vec4 OutlineColor = vec4(1.0, 0.45, 0.0, 1.0);
		const float Thickness = 2.0;
		const float MaskEpsilon = 0.02;

		void main()
		{
			float center = texture(_MainTex, TexCoords).r;
			if (center > MaskEpsilon)
			{
				finalColor = vec4(0.0);
				return;
			}

			vec2 offset = Thickness / vec2(textureSize(_MainTex, 0));
			float neighbourhood = 0.0;
			for (int y = -1; y <= 1; ++y)
			{
				for (int x = -1; x <= 1; ++x)
				{
					vec2 uv = TexCoords + vec2(float(x), float(y)) * offset;
					neighbourhood = max(neighbourhood, texture(_MainTex, uv).r);
				}
			}

			finalColor = neighbourhood > MaskEpsilon ? OutlineColor : vec4(0.0);
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
			o.TexCoords = float2(input.vertexTexCoord.x, 1.0 - input.vertexTexCoord.y);
			o.position = float4(input.vertexPosition, 1.0);
			return o;
		}
	}

	Fragment
	{
		Texture2D _MainTex : register(t0);
		SamplerState _MainTexSampler : register(s0);

		static const float4 OutlineColor = float4(1.0, 0.45, 0.0, 1.0);
		static const float Thickness = 2.0;
		static const float MaskEpsilon = 0.02;

		struct PSInput
		{
			float4 position : SV_Position;
			float2 TexCoords : TEXCOORD0;
		};

		float4 main(PSInput input) : SV_Target
		{
			float center = _MainTex.Sample(_MainTexSampler, input.TexCoords).r;
			if (center > MaskEpsilon)
				return float4(0.0, 0.0, 0.0, 0.0);

			uint maskW, maskH;
			_MainTex.GetDimensions(maskW, maskH);
			float2 offset = float2(Thickness / float(maskW), Thickness / float(maskH));
			float neighbourhood = 0.0;
			for (int y = -1; y <= 1; ++y)
			{
				for (int x = -1; x <= 1; ++x)
				{
					float2 uv = input.TexCoords + float2(float(x), float(y)) * offset;
					neighbourhood = max(neighbourhood, _MainTex.Sample(_MainTexSampler, uv).r);
				}
			}

			return neighbourhood > MaskEpsilon ? OutlineColor : float4(0.0, 0.0, 0.0, 0.0);
		}
	}

	ENDSLANG
}
