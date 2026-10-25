Shader "Default/Tonemapper"

Variants
{
    Variant("TONEMAP_NONE")
    Variant("TONEMAP_MELON")
    Variant("TONEMAP_ACES")
    Variant("TONEMAP_ACES_SIMPLE")
    Variant("TONEMAP_AGX")
    Variant("TONEMAP_REINHARD_SIMPLE")
    Variant("TONEMAP_REINHARD_LUMA")
    Variant("TONEMAP_REINHARD_WHITE")
    Variant("TONEMAP_ROMBINDAHOUSE")
    Variant("TONEMAP_UNCHARTED2")
}

Properties
{
	_FlipY ("Flip Render Target Y", Float) = 0
}

Pass "Tonemapper"
{
    Tags { "RenderOrder" = "Opaque" }

    // Rasterizer culling mode
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
			TexCoords = vertexTexCoord;
		    gl_Position = vec4(vertexPosition, 1.0);
		}
	}

	Fragment
	{
        #include "XEngineCG"

		uniform float Contrast;
		uniform float Saturation;
		uniform float _FlipY;

		uniform sampler2D _MainTex;

		layout(location = 0) out vec4 OutputColor;

		in vec2 TexCoords;

#ifdef TONEMAP_MELON
		// Shifts red to Yellow, green to Yellow, blue to Cyan,
		vec3 HueShift(vec3 In)
		{
			float A = max(In.x, In.y);
			return vec3(A, max(A, In.z), In.z);
		}

		float Cmax(vec3 In)
		{
			return max(max(In.r, In.g), In.b);
		}

		// Made by TripleMelon
		vec3 MelonTonemap(vec3 color)
		{
			// remaps the colors to [0-1] range
			// tested to be as close ti ACES contrast levels as possible
			color = pow(color, vec3(1.56, 1.56, 1.56));
			color = color/(color + 0.84);

			// governs the transition to white for high color intensities
			float factor = Cmax(color) * 0.15; // multiply by 0.15 to get a similar look to ACES
			factor = factor / (factor + 1); // remaps the factor to [0-1] range
			factor *= factor; // smooths the transition to white

			// shift the hue for high intensities (for a more pleasing look).
			color = mix(color, HueShift(color), factor); // can be removed for more neutral colors
			color = mix(color, vec3(1.0, 1.0, 1.0), factor); // shift to white for high intensities

		    return color;
		}
#endif

#ifdef TONEMAP_ACES
		// from https://github.com/TheRealMJP/BakingLab/blob/master/BakingLab/ACES.hlsl
		const mat3 ACESInputMat = mat3
		(
		    0.59719, 0.35458, 0.04823,
		    0.07600, 0.90834, 0.01566,
		    0.02840, 0.13383, 0.83777
		);

		// ODT_SAT => XYZ => D60_2_D65 => sRGB
		const mat3 ACESOutputMat = mat3
		(
		     1.60475, -0.53108, -0.07367,
		    -0.10208,  1.10813, -0.00605,
		    -0.00327, -0.07276,  1.07602
		);

		vec3 RRTAndODTFit(vec3 v)
		{
		    vec3 a = v * (v + 0.0245786) - 0.000090537;
		    vec3 b = v * (0.983729 * v + 0.4329510) + 0.238081;
		    return a / b;
		}

		vec3 ACESFitted(vec3 color)
		{
		    color = color * ACESInputMat;
		    color = RRTAndODTFit(color);
		    color = color * ACESOutputMat;
		    return color;
		}
#endif

#ifdef TONEMAP_ACES_SIMPLE
		vec3 ACESSimple(vec3 c)
		{
		    float a = 2.51;
		    float b = 0.03;
		    float y = 2.43;
		    float d = 0.59;
		    float e = 0.14;
		    return (c * (a * c + b)) / (c * (y * c + d) + e);
		}
#endif

#ifdef TONEMAP_AGX
		const mat3 agxTransform = mat3(
		    0.842479062253094, 0.0423282422610123, 0.0423756549057051,
		    0.0784335999999992, 0.878468636469772,  0.0784336,
		    0.0792237451477643, 0.0791661274605434, 0.879142973793104);

		const mat3 agxTransformInverse = mat3(
		     1.19687900512017,  -0.0528968517574562, -0.0529716355144438,
		    -0.0980208811401368, 1.15190312990417,   -0.0980434501171241,
		    -0.0990297440797205,-0.0989611768448433,  1.15107367264116);

		vec3 agxDefaultContrastApproximation(vec3 x)
		{
		    vec3 x2 = x * x;
		    vec3 x4 = x2 * x2;
		    return 15.5 * x4 * x2 - 40.14 * x4 * x + 31.96 * x4 - 6.868 * x2 * x + 0.4298 * x2 + 0.1191 * x - 0.00232;
		}

		float agxLuminance(vec3 c)
		{
		    return dot(c, vec3(0.2126729, 0.7151522, 0.0721750));
		}

		vec3 AgX(vec3 color)
		{
		    const float minEv = -12.47393;
		    const float maxEv = 4.026069;

		    color = agxTransform * color;
		    color = clamp(log2(max(color, vec3(1e-10))), minEv, maxEv);
		    color = (color - minEv) / (maxEv - minEv);
		    color = agxDefaultContrastApproximation(color);

		    // Punchy look
		    const vec3 slope = vec3(1.1);
		    const vec3 power = vec3(1.2);
		    const float sat = 1.3;
		    float luma = agxLuminance(color);
		    color = pow(max(color * slope, vec3(0.0)), power);
		    color = max(luma + sat * (color - luma), vec3(0.0));

		    // EOTF back to linear
		    color = agxTransformInverse * color;
		    color = pow(max(vec3(0.0), color), vec3(2.2));
		    return color;
		}
#endif

#ifdef TONEMAP_REINHARD_SIMPLE
		vec3 ReinhardSimple(vec3 color)
		{
			float exposure = 1.5;
			return color * (exposure / (1.0 + color / exposure));
		}
#endif

#ifdef TONEMAP_REINHARD_LUMA
		vec3 ReinhardLuma(vec3 color)
		{
			float luma = dot(color, vec3(0.2126, 0.7152, 0.0722));
			float toneMappedLuma = luma / (1.0 + luma);
			return color * (toneMappedLuma / max(luma, 1e-5));
		}
#endif

#ifdef TONEMAP_REINHARD_WHITE
		vec3 ReinhardWhitePreserving(vec3 color)
		{
			float white = 2.0;
			float luma = dot(color, vec3(0.2126, 0.7152, 0.0722));
			float toneMappedLuma = luma * (1.0 + luma / (white * white)) / (1.0 + luma);
			return color * (toneMappedLuma / max(luma, 1e-5));
		}
#endif

#ifdef TONEMAP_ROMBINDAHOUSE
		vec3 RomBinDaHouse(vec3 color)
		{
			return exp(-1.0 / (2.72 * color + 0.15));
		}
#endif

#ifdef TONEMAP_UNCHARTED2
		vec3 Uncharted2(vec3 color)
		{
			float A = 0.15;
			float B = 0.50;
			float C = 0.10;
			float D = 0.20;
			float E = 0.02;
			float F = 0.30;
			float W = 11.2;
			float exposure = 2.0;
			color *= exposure;
			color = ((color * (A * color + C * B) + D * E) / (color * (A * color + B) + D * F)) - E / F;
			float white = ((W * (A * W + C * B) + D * E) / (W * (A * W + B) + D * F)) - E / F;
			return color / white;
		}
#endif

		vec3 Tonemap(vec3 color)
		{
#ifdef TONEMAP_MELON
			color = MelonTonemap(color);
#endif
#ifdef TONEMAP_ACES
			color = ACESFitted(color);
#endif
#ifdef TONEMAP_ACES_SIMPLE
			color = ACESSimple(color);
#endif
#ifdef TONEMAP_AGX
			color = AgX(color);
#endif
#ifdef TONEMAP_REINHARD_SIMPLE
			color = ReinhardSimple(color);
#endif
#ifdef TONEMAP_REINHARD_LUMA
			color = ReinhardLuma(color);
#endif
#ifdef TONEMAP_REINHARD_WHITE
			color = ReinhardWhitePreserving(color);
#endif
#ifdef TONEMAP_ROMBINDAHOUSE
			color = RomBinDaHouse(color);
#endif
#ifdef TONEMAP_UNCHARTED2
			color = Uncharted2(color);
#endif

		    // Clamp to [0, 1] (also acts as the None/Clamp tonemap when no keyword set)
		  	color = clamp(color, 0.0, 1.0);

			return color;
		}

		mat4 contrastMatrix()
		{
			float t = (1.0 - Contrast) / 2.0;

		    return mat4(Contrast, 0, 0, 0,
		                0, Contrast, 0, 0,
		                0, 0, Contrast, 0,
		                t, t, t, 1);

		}


		mat4 saturationMatrix()
		{
		    vec3 luminance = vec3(0.3086, 0.6094, 0.0820);

		    float oneMinusSat = 1.0 - Saturation;

		    vec3 red = vec3(luminance.x * oneMinusSat);
		    red+= vec3(Saturation, 0, 0);

		    vec3 green = vec3(luminance.y * oneMinusSat);
		    green += vec3(0, Saturation, 0);

		    vec3 blue = vec3(luminance.z * oneMinusSat);
		    blue += vec3(0, 0, Saturation);

		    return mat4(red,     0,
		                green,   0,
		                blue,    0,
		                0, 0, 0, 1);
		}

		void main()
		{
			vec2 pixelUV = gl_FragCoord.xy / vec2(textureSize(_MainTex, 0));
			vec2 sampleCoords = vec2(pixelUV.x, mix(pixelUV.y, 1.0 - pixelUV.y, _FlipY));
			vec4 base = texture(_MainTex, sampleCoords);

			vec3 color = base.rgb;

			color = Tonemap(color);

			// Exact display encoding preserves low-intensity linear particles.
            #if defined(TONEMAP_NONE)
            color = mix(1.055 * pow(color, vec3(1.0 / 2.4)) - vec3(0.055),
                        12.92 * color, lessThanEqual(color, vec3(0.0031308)));
            #else
            color = linearToGammaSpace(color);
            #endif

			OutputColor = contrastMatrix() * saturationMatrix() * vec4(color, base.a);
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
		#include "XEngineCG"

		cbuffer TonemapperPS : register(b2)
		{
			float Contrast;
			float Saturation;
			float _FlipY;
			float Padding;
		};

		Texture2D _MainTex : register(t0);
		SamplerState _MainTexSampler : register(s0);

		struct PSInput
		{
			float4 position : SV_Position;
			float2 TexCoords : TEXCOORD0;
		};

		float3 ACESSimple(float3 c)
		{
			float a = 2.51, b = 0.03, y = 2.43, d = 0.59, e = 0.14;
			return (c * (a * c + b)) / (c * (y * c + d) + e);
		}

		float3 HueShift(float3 color)
		{
			float maximum = max(color.x, color.y);
			return float3(maximum, max(maximum, color.z), color.z);
		}

		float3 MelonTonemap(float3 color)
		{
			color = pow(color, float3(1.56, 1.56, 1.56));
			color = color / (color + 0.84);
			float factor = max(color.x, max(color.y, color.z)) * 0.15;
			factor = factor / (factor + 1.0);
			factor *= factor;
			color = lerp(color, HueShift(color), factor);
			return lerp(color, float3(1.0, 1.0, 1.0), factor);
		}

		static const float3x3 ACESInputMat = float3x3(
			0.59719, 0.07600, 0.02840,
			0.35458, 0.90834, 0.13383,
			0.04823, 0.01566, 0.83777);

		static const float3x3 ACESOutputMat = float3x3(
			1.60475, -0.10208, -0.00327,
			-0.53108, 1.10813, -0.07276,
			-0.07367, -0.00605, 1.07602);

		float3 RRTAndODTFit(float3 value)
		{
			float3 numerator = value * (value + 0.0245786) - 0.000090537;
			float3 denominator = value * (0.983729 * value + 0.4329510) + 0.238081;
			return numerator / denominator;
		}

		float3 ACESFitted(float3 color)
		{
			color = mul(ACESInputMat, color);
			color = RRTAndODTFit(color);
			return mul(ACESOutputMat, color);
		}

		static const float3x3 AgXTransform = float3x3(
			0.842479062253094, 0.0784335999999992, 0.0792237451477643,
			0.0423282422610123, 0.878468636469772, 0.0791661274605434,
			0.0423756549057051, 0.0784336, 0.879142973793104);

		static const float3x3 AgXTransformInverse = float3x3(
			1.19687900512017, -0.0980208811401368, -0.0990297440797205,
			-0.0528968517574562, 1.15190312990417, -0.0989611768448433,
			-0.0529716355144438, -0.0980434501171241, 1.15107367264116);

		float3 AgXContrastApproximation(float3 value)
		{
			float3 valueSquared = value * value;
			float3 valueFourth = valueSquared * valueSquared;
			return 15.5 * valueFourth * valueSquared - 40.14 * valueFourth * value + 31.96 * valueFourth - 6.868 * valueSquared * value + 0.4298 * valueSquared + 0.1191 * value - 0.00232;
		}

		float3 AgX(float3 color)
		{
			const float minEv = -12.47393;
			const float maxEv = 4.026069;
			color = mul(AgXTransform, color);
			color = clamp(log2(max(color, float3(1e-10, 1e-10, 1e-10))), minEv, maxEv);
			color = (color - minEv) / (maxEv - minEv);
			color = AgXContrastApproximation(color);
			float luminance = dot(color, float3(0.2126729, 0.7151522, 0.0721750));
			color = pow(max(color * 1.1, float3(0.0, 0.0, 0.0)), float3(1.2, 1.2, 1.2));
			color = max(luminance + 1.3 * (color - luminance), float3(0.0, 0.0, 0.0));
			color = mul(AgXTransformInverse, color);
			return pow(max(float3(0.0, 0.0, 0.0), color), float3(2.2, 2.2, 2.2));
		}

		float3 ReinhardLuma(float3 color)
		{
			float luminance = dot(color, float3(0.2126, 0.7152, 0.0722));
			float toneMappedLuminance = luminance / (1.0 + luminance);
			return color * (toneMappedLuminance / max(luminance, 1e-5));
		}

		float3 ReinhardWhitePreserving(float3 color)
		{
			const float white = 2.0;
			float luminance = dot(color, float3(0.2126, 0.7152, 0.0722));
			float toneMappedLuminance = luminance * (1.0 + luminance / (white * white)) / (1.0 + luminance);
			return color * (toneMappedLuminance / max(luminance, 1e-5));
		}

		float3 Uncharted2(float3 color)
		{
			const float A = 0.15, B = 0.50, C = 0.10, D = 0.20, E = 0.02, F = 0.30;
			const float W = 11.2, exposure = 2.0;
			color *= exposure;
			color = ((color * (A * color + C * B) + D * E) / (color * (A * color + B) + D * F)) - E / F;
			float white = ((W * (A * W + C * B) + D * E) / (W * (A * W + B) + D * F)) - E / F;
			return color / white;
		}

		float3 Tonemap(float3 color)
		{
#ifdef TONEMAP_MELON
			color = MelonTonemap(color);
#endif
#ifdef TONEMAP_ACES
			color = ACESFitted(color);
#endif
#ifdef TONEMAP_ACES_SIMPLE
			color = ACESSimple(color);
#endif
#ifdef TONEMAP_AGX
			color = AgX(color);
#endif
#ifdef TONEMAP_REINHARD_SIMPLE
			float exposure = 1.5;
			color = color * (exposure / (1.0 + color / exposure));
#endif
#ifdef TONEMAP_REINHARD_LUMA
			color = ReinhardLuma(color);
#endif
#ifdef TONEMAP_REINHARD_WHITE
			color = ReinhardWhitePreserving(color);
#endif
#ifdef TONEMAP_ROMBINDAHOUSE
			color = exp(-1.0 / (2.72 * color + 0.15));
#endif
#ifdef TONEMAP_UNCHARTED2
			color = Uncharted2(color);
#endif
			return saturate(color);
		}

		float4x4 contrastMatrix()
		{
			float t = (1.0 - Contrast) * 0.5;
			return float4x4(
				Contrast, 0, 0, 0,
				0, Contrast, 0, 0,
				0, 0, Contrast, 0,
				t, t, t, 1);
		}

		float4x4 saturationMatrix()
		{
			float3 luminance = float3(0.3086, 0.6094, 0.0820);
			float oneMinusSat = 1.0 - Saturation;
			float3 red = luminance.x * oneMinusSat + float3(Saturation, 0, 0);
			float3 green = luminance.y * oneMinusSat + float3(0, Saturation, 0);
			float3 blue = luminance.z * oneMinusSat + float3(0, 0, Saturation);
			return float4x4(
				red.x, green.x, blue.x, 0,
				red.y, green.y, blue.y, 0,
				red.z, green.z, blue.z, 0,
				0, 0, 0, 1);
		}

		float4 main(PSInput input) : SV_Target
		{
			uint width, height;
			_MainTex.GetDimensions(width, height);
			float2 pixelUV = input.position.xy / float2(width, height);
			float2 sampleCoords = float2(pixelUV.x, lerp(pixelUV.y, 1.0 - pixelUV.y, _FlipY));
			float4 base = _MainTex.Sample(_MainTexSampler, sampleCoords);
			float3 color = Tonemap(base.rgb);
			#if defined(TONEMAP_NONE)
			float3 high = 1.055 * pow(color, float3(1.0 / 2.4, 1.0 / 2.4, 1.0 / 2.4)) - 0.055;
			color = float3(color.r <= 0.0031308 ? 12.92 * color.r : high.r,
			               color.g <= 0.0031308 ? 12.92 * color.g : high.g,
			               color.b <= 0.0031308 ? 12.92 * color.b : high.b);
			#else
			color = linearToGammaSpace(color);
			#endif
			return mul(contrastMatrix(), mul(saturationMatrix(), float4(color, base.a)));
		}
	}
	ENDSLANG
}
