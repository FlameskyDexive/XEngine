Shader "Default/Refraction"

Variants
{
}

Properties
{
    _RefractionStrength ("Refraction Strength", Float) = 0.1
    _NoiseScale ("Noise Scale", Float) = 1.0
    _Tint ("Tint", Color) = (1.0, 1.0, 1.0, 1.0)
    _BlurRadius ("Blur Radius", Float) = 0.02
    _BlurMIP ("Blur MIP Bias", Float) = 2.0
    _BlurSteps ("Blur Steps", Int) = 4
}

Pass "Refraction"
{
    GrabTexture "_GrabTexture"
    Tags { "RenderOrder" = "Transparent" }
    Blend Alpha
    ZWrite Off
    Cull Back

	GLSLPROGRAM
		Shared
		{
			// Simple 3D noise function
			float hash(vec3 p)
			{
				p = fract(p * 0.3183099 + 0.1);
				p *= 17.0;
				return fract(p.x * p.y * p.z * (p.x + p.y + p.z));
			}

			float noise(vec3 x)
			{
				vec3 i = floor(x);
				vec3 f = fract(x);
				f = f * f * (3.0 - 2.0 * f);

				return mix(mix(mix(hash(i + vec3(0.0, 0.0, 0.0)),
								   hash(i + vec3(1.0, 0.0, 0.0)), f.x),
							   mix(hash(i + vec3(0.0, 1.0, 0.0)),
								   hash(i + vec3(1.0, 1.0, 0.0)), f.x), f.y),
						   mix(mix(hash(i + vec3(0.0, 0.0, 1.0)),
								   hash(i + vec3(1.0, 0.0, 1.0)), f.x),
							   mix(hash(i + vec3(0.0, 1.0, 1.0)),
								   hash(i + vec3(1.0, 1.0, 1.0)), f.x), f.y), f.z);
			}

			// Interleaved Gradient Noise (Jimenez 2014)
			float InterleavedGradientNoise(vec2 pixCoord, int frameCount)
			{
				const vec3 magic = vec3(0.06711056, 0.00583715, 52.9829189);
				vec2 frameMagicScale = vec2(2.083, 4.867);
				pixCoord += float(frameCount) * frameMagicScale;
				return fract(magic.z * fract(dot(pixCoord, magic.xy)));
			}
		}

		Vertex
		{
            #include "XEngineCG"
            #include "VertexAttributes"

			out vec2 texCoord0;
			out vec3 worldPos;
			out vec4 screenPos;
			out vec3 vNormal;

			void main()
			{
				gl_Position = XENGINE_MATRIX_MVP * vec4(vertexPosition, 1.0);
				texCoord0 = vertexTexCoord0;
				worldPos = (XENGINE_MATRIX_M * vec4(vertexPosition, 1.0)).xyz;
				screenPos = gl_Position;
				vNormal = normalize(mat3(XENGINE_MATRIX_M) * vertexNormal);
			}
		}

		Fragment
		{
            #include "XEngineCG"

			layout (location = 0) out vec4 fragColor;

			in vec2 texCoord0;
			in vec3 worldPos;
			in vec4 screenPos;
			in vec3 vNormal;

			uniform sampler2D _GrabTexture;
			uniform float _RefractionStrength;
			uniform float _NoiseScale;
			uniform vec4 _Tint;
			uniform float _BlurRadius;
			uniform float _BlurMIP;
			uniform int _BlurSteps;

			void main()
			{
				// Screen UV from clip space
				vec2 screenUV = (screenPos.xy / screenPos.w) * 0.5 + 0.5;

				// Noise-based refraction offset
				vec3 noiseInput = worldPos * _NoiseScale + vec3(_Time * 0.1);
				vec2 refractionOffset = vec2(
					noise(noiseInput + vec3(0.0, 0.0, 0.0)),
					noise(noiseInput + vec3(5.2, 1.3, 0.0))
				);
				refractionOffset = (refractionOffset * 2.0 - 1.0) * _RefractionStrength;
				vec2 refractedUV = clamp(screenUV + refractionOffset, 0.0, 1.0);

				// One-step mip blur (Mirza Beig technique)
				float ign = InterleavedGradientNoise(gl_FragCoord.xy, int(_Time.w) % 60);
				float angle = ign * 6.28318530718;
				vec2 dir = vec2(cos(angle), sin(angle));
				vec2 aspect = vec2(min(1.0, _ScreenParams.y / _ScreenParams.x),
				                   min(1.0, _ScreenParams.x / _ScreenParams.y));

				int steps = clamp(_BlurSteps, 1, 4);
				vec4 acc = vec4(0.0);
				for (int i = 0; i < steps; i++)
				{
					vec2 offset = dir * _BlurRadius * aspect;
					acc += textureLod(_GrabTexture, refractedUV + offset, _BlurMIP);
					dir = vec2(-dir.y, dir.x); // Rotate 90 degrees
				}
				vec4 refractedColor = acc / float(steps);

				fragColor = vec4(refractedColor.rgb * _Tint.rgb, _Tint.a);
			}
		}
	ENDGLSL

    SLANGPROGRAM

        Vertex
        {
            #include "XEngineCG"

            cbuffer RefractionMaterial : register(b2)
            {
                float _RefractionStrength;
                float _NoiseScale;
                float2 Padding0;
                float4 _Tint;
                float _BlurRadius;
                float _BlurMIP;
                int _BlurSteps;
                float Padding1;
            };

            struct VSInput
            {
                float3 vertexPosition : POSITION;
                float2 vertexTexCoord0 : TEXCOORD0;
                float3 vertexNormal : NORMAL;
            };

            struct VSOutput
            {
                float4 position : SV_Position;
                float2 texCoord0 : TEXCOORD0;
                float3 worldPos : TEXCOORD1;
                float4 screenPos : TEXCOORD2;
                float3 vNormal : TEXCOORD3;
            };

            VSOutput main(VSInput input)
            {
                VSOutput output;
                output.position = TransformClip(input.vertexPosition);
                output.texCoord0 = input.vertexTexCoord0;
                output.worldPos = TransformPosition(input.vertexPosition);
                output.screenPos = output.position;
                output.vNormal = TransformDirection(input.vertexNormal);
                return output;
            }
        }

        Fragment
        {
            #include "XEngineCG"

            cbuffer RefractionMaterial : register(b2)
            {
                float _RefractionStrength;
                float _NoiseScale;
                float2 Padding0;
                float4 _Tint;
                float _BlurRadius;
                float _BlurMIP;
                int _BlurSteps;
                float Padding1;
            };

            Texture2D _GrabTexture : register(t0);
            SamplerState _GrabTextureSampler : register(s0);

            struct PSInput
            {
                float4 position : SV_Position;
                float2 texCoord0 : TEXCOORD0;
                float3 worldPos : TEXCOORD1;
                float4 screenPos : TEXCOORD2;
                float3 vNormal : TEXCOORD3;
            };

            float Hash(float3 p)
            {
                p = frac(p * 0.3183099 + 0.1);
                p *= 17.0;
                return frac(p.x * p.y * p.z * (p.x + p.y + p.z));
            }

            float Noise(float3 x)
            {
                float3 i = floor(x);
                float3 f = frac(x);
                f = f * f * (3.0 - 2.0 * f);

                return lerp(lerp(lerp(Hash(i), Hash(i + float3(1.0, 0.0, 0.0)), f.x),
                                     lerp(Hash(i + float3(0.0, 1.0, 0.0)), Hash(i + float3(1.0, 1.0, 0.0)), f.x), f.y),
                                 lerp(lerp(Hash(i + float3(0.0, 0.0, 1.0)), Hash(i + float3(1.0, 0.0, 1.0)), f.x),
                                     lerp(Hash(i + float3(0.0, 1.0, 1.0)), Hash(i + float3(1.0, 1.0, 1.0)), f.x), f.y), f.z);
            }

            float InterleavedGradientNoise(float2 pixCoord, int frameCount)
            {
                const float3 magic = float3(0.06711056, 0.00583715, 52.9829189);
                pixCoord += float(frameCount) * float2(2.083, 4.867);
                return frac(magic.z * frac(dot(pixCoord, magic.xy)));
            }

            float4 main(PSInput input) : SV_Target
            {
                float2 screenUV = (input.screenPos.xy / input.screenPos.w) * 0.5 + 0.5;
                float3 noiseInput = input.worldPos * _NoiseScale + _Time.xyz * 0.1;
                float2 refractionOffset = float2(
                    Noise(noiseInput),
                    Noise(noiseInput + float3(5.2, 1.3, 0.0)));
                refractionOffset = (refractionOffset * 2.0 - 1.0) * _RefractionStrength;
                float2 refractedUV = clamp(screenUV + refractionOffset, 0.0, 1.0);

                float ign = InterleavedGradientNoise(input.position.xy, (int)_Time.w % 60);
                float angle = ign * 6.28318530718;
                float2 direction = float2(cos(angle), sin(angle));
                float2 aspect = float2(min(1.0, _ScreenParams.y / _ScreenParams.x),
                    min(1.0, _ScreenParams.x / _ScreenParams.y));

                int steps = clamp(_BlurSteps, 1, 4);
                float4 accumulated = 0.0;
                for (int i = 0; i < steps; i++)
                {
                    float2 offset = direction * _BlurRadius * aspect;
                    accumulated += _GrabTexture.SampleLevel(_GrabTextureSampler, refractedUV + offset, _BlurMIP);
                    direction = float2(-direction.y, direction.x);
                }
                float4 refractedColor = accumulated / float(steps);
                return float4(refractedColor.rgb * _Tint.rgb, _Tint.a);
            }
        }
    ENDSLANG
}
